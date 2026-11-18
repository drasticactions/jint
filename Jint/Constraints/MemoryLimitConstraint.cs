using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jint.Runtime;

namespace Jint.Constraints;

/// <summary>
/// Describes how accurately <see cref="MemoryLimitConstraint"/> can account managed allocations on the
/// current runtime.
/// </summary>
public enum MemoryLimitAccuracy
{
    /// <summary>
    /// The runtime does not expose a per-thread managed-allocation counter. Execution fails explicitly
    /// instead of silently running without the configured limit.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Managed allocations are measured on every thread while that thread is actively executing work for
    /// the engine. Idle time between asynchronous continuations is excluded.
    /// </summary>
    ExecutionThread
}

/// <summary>
/// Bounds managed allocations performed while an engine executes one operation.
/// </summary>
/// <remarks>
/// <para>
/// The runtime only exposes allocation counters per managed thread. Jint therefore divides an operation
/// into synchronous execution segments, snapshots the current thread at each segment boundary, and carries
/// the accumulated total with promise reactions and module-load continuations. A continuation may resume on
/// any thread without losing the bytes allocated before the hop, and allocations performed by unrelated
/// work while the operation is suspended are not charged.
/// </para>
/// <para>
/// This is an allocation budget, not a retained-memory or process-memory limit. It includes managed
/// allocations made by synchronous host callbacks invoked by script, because those callbacks are part of
/// the engine operation. It cannot include unmanaged memory, allocations made by worker threads a host
/// callback starts, or work an asynchronous producer performs before handing its result back to Jint.
/// </para>
/// <para>
/// By default the budget is reset for every top-level engine entry. Use <see cref="Begin"/> and
/// <see cref="End"/> to cover a host operation made from several entries with one shared budget.
/// </para>
/// <para>
/// A long string concatenation that defers its copy is charged when it is built, for the characters it
/// appends, and one whose result alone exceeds the limit fails at once. A host's <c>ToString()</c> or
/// <c>ToObject()</c> on a result is not charged and copies every string in full, shared characters included;
/// read an untrusted result through <see cref="Engine.ConvertResult"/> under <see cref="ResultLimits"/>, which
/// also checks each such copy against this limit before making it.
/// </para>
/// </remarks>
public sealed class MemoryLimitConstraint : Constraint
{
    private readonly long _memoryLimit;
    private OperationState? _reusableState;
    private OperationState? _lastState;
    private OperationState? _explicitOperation;
    private OperationState? _activeState;
    private long _segmentStart;
    private int _segmentThreadId;
    private int _segmentDepth;
    private Engine? _engine;
    private bool _aborting;

    /// <summary>
    /// The value of the thread's allocation counter up to which <see cref="CheckOnEngineThread"/> cannot fail, so
    /// that it passes on one read of the counter: <see cref="long.MaxValue"/> while no operation is running, and
    /// <see cref="long.MinValue"/> once the running one cannot pass at all. Kept by <see cref="RefreshPassLimit"/>
    /// whenever the running operation, its segment or its charge changes.
    /// </summary>
    private long _passLimit = long.MaxValue;

    /// <summary>
    /// How many more <see cref="CheckBeforeStatement"/> calls pass without reading the allocation counter. Set only
    /// after a read that left at least <see cref="UncheckedHeadroom"/> bytes below <see cref="_passLimit"/>, and
    /// cleared whenever the pass limit changes.
    /// </summary>
    private int _uncheckedStatements;

    /// <summary>
    /// The most statements in a row that <see cref="CheckBeforeStatement"/> lets pass on one read of the counter.
    /// Reading the counter is most of what the check of a statement costs.
    /// </summary>
    internal const int MaxUncheckedStatements = 15;

    /// <summary>
    /// The room below the pass limit that a read must find before the next statements skip theirs. Closer to the
    /// limit, every statement reads the counter, so a loop that allocates its way to the limit stops at the same
    /// statement as when every statement read it, unless fewer than <see cref="MaxUncheckedStatements"/> statements
    /// allocate this much.
    /// </summary>
    internal const long UncheckedHeadroom = 256 * 1024;

    public MemoryLimitConstraint(long memoryLimit)
    {
        if (memoryLimit <= 0 || memoryLimit == long.MaxValue)
        {
            Throw.ArgumentOutOfRangeException(nameof(memoryLimit), "Memory limit must be between 1 and Int64.MaxValue - 1.");
        }

        _memoryLimit = memoryLimit;
    }

    /// <summary>The managed-allocation budget in bytes.</summary>
    public long MemoryLimit => _memoryLimit;

    /// <summary>
    /// Managed bytes attributed to the current operation, or to the most recently entered operation when
    /// the engine is idle.
    /// </summary>
    /// <remarks>
    /// Includes characters a deferred string concatenation has been charged for that nothing has allocated yet.
    /// </remarks>
    public long AllocatedBytes
    {
        get
        {
            using var ownership = EnterHostCall();
            return GetUsage(_activeState ?? _lastState);
        }
    }

    /// <summary>The accounting accuracy available on the current runtime.</summary>
    public static MemoryLimitAccuracy Accuracy => GCPolyfills.AllocatedBytesForCurrentThreadIsSupported
        ? MemoryLimitAccuracy.ExecutionThread
        : MemoryLimitAccuracy.Unavailable;

    /// <summary>Whether a host-defined multi-entry operation is currently armed.</summary>
    public bool IsOperationActive
    {
        get
        {
            using var ownership = EnterHostCall();
            return _explicitOperation is not null;
        }
    }

    /// <summary>
    /// Starts a host-defined operation. Every engine entry and asynchronous continuation until
    /// <see cref="End"/> shares one allocation budget.
    /// </summary>
    /// <remarks>
    /// Calling <see cref="Begin"/> again re-arms the constraint with a fresh budget. Bytes allocated by host
    /// code between engine entries are not charged; only synchronous segments in which the engine is active
    /// are measurable without also charging unrelated process activity.
    /// </remarks>
    public void Begin()
    {
        using var ownership = EnterHostCall();
        if (_segmentDepth != 0)
        {
            Throw.InvalidOperationException("A memory-limit operation cannot be started while the engine is executing.");
        }

        EnsureSupported();
        if (_explicitOperation is { } previous)
        {
            previous.Enabled = false;
        }
        _explicitOperation = RentState();
        _lastState = _explicitOperation;
    }

    /// <summary>
    /// Ends the host-defined operation. A continuation that runs afterwards no longer shares this operation's
    /// accumulated total; it is treated as a fresh ordinary operation and receives a fresh budget.
    /// </summary>
    /// <remarks>Safe to call when no operation is active and safe to call more than once.</remarks>
    public void End()
    {
        using var ownership = EnterHostCall();
        if (_explicitOperation is { } state)
        {
            state.Enabled = false;
            _explicitOperation = null;
        }
    }

    /// <summary>
    /// Takes <paramref name="bytes"/> off what the running operation has been charged, for a host that has measured that
    /// much of what the operation allocated to be garbage already. A host that releases what the collector reclaimed turns
    /// the allocation budget into a bound on what the operation keeps: a long loop that allocates and drops short-lived
    /// values then runs to its end, while one that keeps what it allocates still fails at the limit.
    /// </summary>
    /// <remarks>
    /// Does nothing when no operation is running or the running one has already exceeded the limit, and never takes the
    /// operation below zero. The engine does not measure what is garbage itself: what the host releases is trusted.
    /// </remarks>
    public void Release(long bytes)
    {
        using var ownership = EnterHostCall();
        var state = _activeState;
        if (bytes <= 0 || state is null || !state.Enabled || state.Exceeded)
        {
            return;
        }

        state.AllocatedBytes -= Math.Min(bytes, GetUsage(state));
        RefreshPassLimit();
    }

    /// <summary>
    /// Never amortizable: allocation between two checks is irreversible and unbounded per statement.
    /// </summary>
    public override bool IsAmortizable => false;

    public override void Check()
    {
        using var ownership = EnterHostCall();
        var state = _activeState;
        if (state is null || !state.Enabled)
        {
            return;
        }

        EnsureSupported();

        var usage = GetUsage(state);
        if (state.Exceeded || usage > _memoryLimit)
        {
            var message = $"Script has allocated {usage} but is limited to {_memoryLimit}";
            if (state.Exceeded)
            {
                // A later check on an operation already over budget: the cycle was torn down the first
                // time and there is nothing left to abort.
                Throw.MemoryLimitExceededException(message);
            }

            state.Exceeded = true;
            RefreshPassLimit();
            Throw.MemoryLimitExceededException(message, Abort());
        }
    }

    /// <summary>
    /// <see cref="Check"/> for the engine's own check points — before a statement, after a host call — which run on
    /// the thread that owns the engine, so the ownership <see cref="Check"/> takes for a host is already held. The
    /// common case is one read of the allocation counter against <see cref="_passLimit"/>.
    /// </summary>
    /// <remarks>
    /// The counter is per thread, and this does not ask which thread it runs on. It does not need to: the pass
    /// limit is only finite while the running operation has been charged no more than the limit, so on a thread
    /// other than the running segment's, where <see cref="Check"/> counts only what was charged, a check this
    /// passes is one <see cref="Check"/> passes too.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckOnEngineThread()
    {
        if (!GCPolyfills.AllocatedBytesForCurrentThreadIsSupported || GC.GetAllocatedBytesForCurrentThread() > _passLimit)
        {
            Check();
        }
    }

    /// <summary>
    /// <see cref="CheckOnEngineThread"/> before a statement: a read of the counter that finds the operation at least
    /// <see cref="UncheckedHeadroom"/> bytes below the pass limit lets the next <see cref="MaxUncheckedStatements"/>
    /// statements pass without one. The limit then still stops a runaway allocation, at most that many statements
    /// later; a change of the pass limit (a charge, a segment starting or ending) makes the next statement read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckBeforeStatement()
    {
        if (_uncheckedStatements > 0)
        {
            _uncheckedStatements--;
            return;
        }

        if (!GCPolyfills.AllocatedBytesForCurrentThreadIsSupported)
        {
            Check();
            return;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        if (allocated > _passLimit)
        {
            Check();
            return;
        }

        if (_passLimit - allocated >= UncheckedHeadroom)
        {
            _uncheckedStatements = MaxUncheckedStatements;
        }
    }

    /// <summary>
    /// Recomputes <see cref="_passLimit"/> from the running operation: what it has been charged and, while its
    /// segment runs, the counter value the segment started at. It mirrors <see cref="GetUsage"/> and the test in
    /// <see cref="Check"/>, which fails when the usage exceeds the limit or the operation already has.
    /// </summary>
    private void RefreshPassLimit()
    {
        _uncheckedStatements = 0;
        var state = _activeState;
        if (state is null)
        {
            _passLimit = long.MaxValue;
            return;
        }

        var charged = state.AllocatedBytes;
        if (state.Exceeded || charged > _memoryLimit)
        {
            _passLimit = long.MinValue;
            return;
        }

        if (_segmentDepth <= 0)
        {
            // Nothing running adds to the charge.
            _passLimit = long.MaxValue;
            return;
        }

        // Usage is the charge plus the counter's growth since the segment started; it passes while it is at most
        // the limit. A charge below zero (a release ahead of the segment's own allocations) raises the limit.
        var room = _memoryLimit - charged;
        _passLimit = _segmentStart > long.MaxValue - room ? long.MaxValue : _segmentStart + room;
    }

    /// <summary>
    /// Tears down the evaluation cycle's transient asynchronous work, once. The teardown runs host code —
    /// a worker host is told its connection ended, a host stream is disposed — and host code may re-enter
    /// the engine, whose next statement check would find a fresh operation over the same budget and start a
    /// second teardown from inside the first. One is what this path promises; the flag is what makes that
    /// true whatever the host does.
    /// </summary>
    private Exception? Abort()
    {
        if (_aborting || _engine is not { } engine)
        {
            return null;
        }

        _aborting = true;
        try
        {
            return engine.AbortMemoryLimitedOperation();
        }
        finally
        {
            _aborting = false;
        }
    }

    /// <summary>
    /// Clears the ordinary operation's accounting state. An explicitly armed
    /// <see cref="Begin"/>/<see cref="End"/> window remains host-owned and is not reset.
    /// The engine starts each ordinary entry through its internal segment lifecycle rather than calling this
    /// method on the way out, so the completed operation remains available through <see cref="AllocatedBytes"/>.
    /// </summary>
    public override void Reset()
    {
        using var ownership = EnterHostCall();
        if (_explicitOperation is null)
        {
            _lastState = null;
        }
    }

    internal OperationState BeginEntry()
    {
        EnsureSupported();
        var state = _explicitOperation ?? RentState();
        _lastState = state;
        return state;
    }

    internal OperationState ContinueOrBeginEntry(OperationState? capturedState)
        => capturedState is { Enabled: true } ? capturedState : BeginEntry();

    internal OperationState? CaptureOperationState()
    {
        var state = _activeState;
        if (state is null || !state.Enabled)
        {
            return null;
        }

        state.Captured = true;
        return state;
    }

    internal OperationState? CurrentOperationState => _activeState;

    /// <summary>
    /// Charges the active operation for a concatenation that defers its copy instead of making it
    /// (<see cref="Native.JsString.Concat"/>), as if the characters had been allocated where the copy would
    /// have been (sebastienros/jint#4162).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The charge is what the concatenation <em>appends</em>, its shorter operand: the same thing the result
    /// costs when built with <c>+=</c>, and what keeps an accumulator linear, because charging every node its
    /// whole length charges <c>s = s + x</c> for a copy of <c>s</c> on every iteration — the quadratic cost
    /// the deferral exists to remove. It is also enough to bound every read of the value: following the
    /// longer operand down from a node reaches one real string, and each node on that path added no more
    /// than its shorter operand, so a node never stands for more than that string plus what its path was
    /// charged, whoever flattens it and whenever.
    /// </para>
    /// <para>
    /// That argument cannot bound a path charged across several operations, each within its own budget, or
    /// a real string the script never allocated — a host string with a character appended. So a result
    /// whose flat form alone exceeds the whole budget is charged in full, exactly as the copy it replaced
    /// would have been, and refused before the node exists: no operation leaves behind a value larger than
    /// its budget.
    /// </para>
    /// </remarks>
    internal void ChargeDeferredConcatenation(int leftLength, int rightLength)
    {
        var state = _activeState;
        if (state is null)
        {
            // No operation is armed, so there is nothing to charge: the host called in from outside a run.
            return;
        }

        var length = (long) leftLength + rightLength;
        if (length * sizeof(char) <= _memoryLimit)
        {
            // Seen by the next exact check — the statement's own, or the one closing the entry.
            state.AllocatedBytes = SaturatingAdd(state.AllocatedBytes, Math.Min(leftLength, rightLength) * (long) sizeof(char));
            RefreshPassLimit();
            return;
        }

        state.AllocatedBytes = SaturatingAdd(state.AllocatedBytes, length * sizeof(char));
        RefreshPassLimit();
        Check();
    }

    /// <summary>
    /// Refuses a copy of <paramref name="length"/> characters that the active operation is about to make,
    /// before it is made, when the copy would take the operation past the limit (sebastienros/jint#4175).
    /// </summary>
    /// <remarks>
    /// For a caller copying out text that many values share — a slice view, a deferred concatenation — one at a
    /// time, under a constraint cadence that counts values rather than characters, so that the cadence's next
    /// check could come thousands of copies later. A copy that fits is not charged here: the allocation counter
    /// sees it once it is made. One that does not is charged in full and refused, as an over-long deferred
    /// concatenation is.
    /// </remarks>
    internal void CheckBeforeCopying(int length)
    {
        var state = _activeState;
        if (state is null || !state.Enabled)
        {
            return;
        }

        var bytes = length * (long) sizeof(char);
        if (SaturatingAdd(GetUsage(state), bytes) <= _memoryLimit)
        {
            return;
        }

        state.AllocatedBytes = SaturatingAdd(state.AllocatedBytes, bytes);
        RefreshPassLimit();
        Check();
    }

    internal bool TrySuspendActiveSegment(
        out OperationState? operationState,
        out SegmentToken segment)
    {
        operationState = _activeState;
        if (operationState is null)
        {
            segment = default;
            return false;
        }

        segment = BeginSegment(state: null);
        return true;
    }

    internal void Attach(Engine engine)
    {
        if (_engine is not null && !ReferenceEquals(_engine, engine))
        {
            Throw.InvalidOperationException(
                "A MemoryLimitConstraint instance can only be registered with one Engine. Register a constraint factory when Options is shared.");
        }

        _engine = engine;
    }

    private Engine.HostCallScope? EnterHostCall() => _engine?.EnterHostCallIfNeeded();

    internal SegmentToken BeginSegment(OperationState? state)
    {
        if (ReferenceEquals(state, _activeState))
        {
            if (state is not null)
            {
                _segmentDepth++;
                RefreshPassLimit();
            }

            return new SegmentToken(Switched: false, null, 0);
        }

        var previousState = _activeState;
        var previousDepth = _segmentDepth;
        AccumulateActiveSegment();

        _activeState = state is { Enabled: true } ? state : null;
        _segmentDepth = _activeState is null ? 0 : 1;
        if (_activeState is not null)
        {
            StartSegment();
        }

        RefreshPassLimit();
        return new SegmentToken(Switched: true, previousState, previousDepth);
    }

    internal void EndSegment(in SegmentToken token)
    {
        if (!token.Switched)
        {
            if (_activeState is not null)
            {
                _segmentDepth--;
                RefreshPassLimit();
            }
            return;
        }

        AccumulateActiveSegment();
        _activeState = token.PreviousState;
        _segmentDepth = token.PreviousDepth;
        if (_activeState is not null && _segmentDepth > 0)
        {
            StartSegment();
        }

        RefreshPassLimit();
    }

    private OperationState RentState()
    {
        var state = _reusableState;
        if (state is null || state.Captured || ReferenceEquals(state, _activeState))
        {
            state = new OperationState();
            _reusableState = state;
        }
        else
        {
            state.AllocatedBytes = 0;
            state.Exceeded = false;
            state.Enabled = true;
        }

        return state;
    }

    private long GetUsage(OperationState? state)
    {
        if (state is null)
        {
            return 0;
        }

        var usage = state.AllocatedBytes;
        if (ReferenceEquals(state, _activeState)
            && _segmentDepth > 0
            && Environment.CurrentManagedThreadId == _segmentThreadId)
        {
            var current = GC.GetAllocatedBytesForCurrentThread();
            usage = SaturatingAdd(usage, current - _segmentStart);
        }

        // A release can take the total below zero until the running segment's own allocations are added in.
        return Math.Max(usage, 0);
    }

    private void StartSegment()
    {
        EnsureSupported();
        _segmentThreadId = Environment.CurrentManagedThreadId;
        _segmentStart = GC.GetAllocatedBytesForCurrentThread();
    }

    private void AccumulateActiveSegment()
    {
        if (_activeState is null || _segmentDepth == 0)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId != _segmentThreadId)
        {
            Throw.InvalidOperationException("A synchronous engine execution segment changed managed threads.");
        }

        var current = GC.GetAllocatedBytesForCurrentThread();
        _activeState.AllocatedBytes = SaturatingAdd(_activeState.AllocatedBytes, current - _segmentStart);
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
        {
            return left;
        }

        return left >= long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static void EnsureSupported()
    {
        if (!GCPolyfills.AllocatedBytesForCurrentThreadIsSupported)
        {
            Throw.PlatformNotSupportedException(
                "The current runtime does not expose GC.GetAllocatedBytesForCurrentThread, so Jint cannot enforce a memory allocation limit without charging unrelated process allocations.");
        }
    }

    internal sealed class OperationState
    {
        internal long AllocatedBytes;
        internal bool Captured;
        internal volatile bool Enabled = true;
        internal bool Exceeded;
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct SegmentToken(bool Switched, OperationState? PreviousState, int PreviousDepth);
}
