using Jint.Native;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Statements;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// The state of one run of a kernel: the evaluation context, the environment the kernel was entered in, and where each
/// name the kernel reads resolved, found the first time the run reads it. The resolution holds for the whole run: the
/// kernel runs in one environment, which it never leaves (a block that declares something is left to the interpreter,
/// which restores the environment), the slot layout of a declarative environment never changes, and the one thing
/// that adds a binding to an existing environment, a sloppy direct eval or an Annex B function declaration, bumps
/// <see cref="Engine._envBindingInjectionEpoch"/>, which every read of a resolution checks against its value when the
/// name was resolved, resolving the name again when it moved.
/// </summary>
internal sealed class KernelFrame
{
    /// <summary>The slot index of a name not resolved yet in this run (with no environment).</summary>
    internal const int NotResolved = 0;

    /// <summary>The slot index of a name that resolved to the global object.</summary>
    internal const int GlobalBinding = -2;

    /// <summary>The slot index of a name the kernel does not read: anything but a slot or the global object.</summary>
    internal const int Unresolved = -1;

    internal EvaluationContext Context = null!;
    internal Engine Engine = null!;
    internal Environment Start = null!;
    internal GlobalEnvironment? Global;
    internal readonly Environment.BindingName[] Names;
    internal readonly KernelSlot[] Slots;
    internal Completion Abrupt;
    internal bool InUse;

    // How many statements per node of the kernel the tree checks before the kernel runs as compiled code, or -1 to
    // stay on the tree (Engine._loopKernelCodeThreshold).
    internal int CodeThreshold;

    // Whether compiled code of this kernel is running in this frame, the code it completed the outermost loop with
    // when it was entered from a loop inside, and whether an exception is leaving the code through the tree.
    internal bool InCode;
    internal int FinishedCode;
    internal bool Escaping;

    // The statements the tree has checked in this run.
    internal long Work;

    // The slot names of the environment the kernel was entered in the first time, and the slot each name has there
    // (or -1): a run entered in an environment of that layout resolves those names without a walk.
    internal Key[]? Layout;
    internal int[]? LayoutIndices;

    // The next spare frame of the program, while this one is spare.
    internal KernelFrame? NextSpare;

    // For a frame kept with a function environment: the outer environment its names were resolved through.
    internal Environment? Outer;

    // The value a function body's return statement left, a Number in ReturnNumber where it is null.
    internal JsValue? ReturnValue;
    internal double ReturnNumber;

    public KernelFrame(Environment.BindingName[] names)
    {
        Names = names;
        Slots = new KernelSlot[names.Length];
    }

    /// <summary>
    /// Resolves name <paramref name="binding"/> as ResolveBinding would from the environment the kernel was entered in
    /// (https://tc39.es/ecma262/#sec-getidentifierreference): through declarative environments that do not hold it, to
    /// the slot of the first that does, or to the global environment. A name held by name (a dictionary binding), in a
    /// module, a with object or the global lexical declarations stays unresolved; the kernel never reads it.
    /// </summary>
    /// <summary>Whether name <paramref name="binding"/> has not been resolved in this run, or was before a binding was injected.</summary>
    internal bool NeedsResolving(int binding)
    {
        ref var slot = ref Slots[binding];
        return slot.Epoch != Engine._envBindingInjectionEpoch || (slot.Environment is null && slot.Index == NotResolved);
    }

    internal void Resolve(int binding)
    {
        ref var slot = ref Slots[binding];
        slot.Environment = null;
        slot.Index = Unresolved;
        slot.Epoch = Engine._envBindingInjectionEpoch;

        // The environment has the layout the kernel first ran in, and holds no binding by name that could shadow a
        // slot: the name is in its slot or, when the environment's outer one is the global environment, a global.
        if (Start is DeclarativeEnvironment start
            && Layout is not null
            && ReferenceEquals(start._slotNames, Layout)
            && start._dictionary is not { Count: > 0 }
            && start is not ModuleEnvironment
            && ReferenceEquals(start._engine, Engine))
        {
            var index = LayoutIndices![binding];
            if (index >= 0)
            {
                if (start._slots is { } startSlots && index < startSlots.Length)
                {
                    slot.Environment = start;
                    slot.Index = index;
                }

                return;
            }

            if (start._outerEnv is GlobalEnvironment outerGlobal)
            {
                Global = outerGlobal;
                slot.Index = GlobalBinding;
                return;
            }
        }

        var key = Names[binding].Key;
        for (var e = Start; e is not null; e = e._outerEnv)
        {
            if (e is GlobalEnvironment global)
            {
                Global = global;
                slot.Index = GlobalBinding;
                return;
            }

            if (e is not DeclarativeEnvironment declarative || e is ModuleEnvironment || !ReferenceEquals(declarative._engine, Engine))
            {
                return;
            }

            if (SlotLayoutMemo.DictionaryOwns(declarative, key))
            {
                return;
            }

            if (declarative._slotNames is { } slotNames)
            {
                var index = IndexOf(slotNames, key);
                if (index >= 0)
                {
                    if (declarative._slots is { } slots && index < slots.Length)
                    {
                        slot.Environment = declarative;
                        slot.Index = index;
                    }

                    return;
                }
            }
        }
    }

    /// <summary>
    /// What <see cref="Resolve"/> answers for every name, taken for all of them as the run is entered where the
    /// environment has the layout the kernel first ran in: the slots of the names it holds, and the global object for
    /// the others when its outer environment is the global environment. A name this leaves alone is resolved when it
    /// is first read, as before; a binding injected later moves the epoch, which resolves every name again.
    /// </summary>
    internal void ResolveLayout()
    {
        if (Start is not DeclarativeEnvironment start
            || Layout is null
            || !ReferenceEquals(start._slotNames, Layout)
            || start._dictionary is { Count: > 0 }
            || start is ModuleEnvironment
            || !ReferenceEquals(start._engine, Engine))
        {
            return;
        }

        var epoch = Engine._envBindingInjectionEpoch;
        var indices = LayoutIndices!;
        var startSlots = start._slots;
        var global = start._outerEnv as GlobalEnvironment;
        var slots = Slots;
        for (var i = 0; i < slots.Length; i++)
        {
            var index = indices[i];
            ref var slot = ref slots[i];
            if (index >= 0)
            {
                if (startSlots is not null && index < startSlots.Length)
                {
                    slot.Environment = start;
                    slot.Index = index;
                    slot.Epoch = epoch;
                }
            }
            else if (global is not null)
            {
                slot.Index = GlobalBinding;
                slot.Epoch = epoch;
            }
        }

        if (global is not null)
        {
            Global = global;
        }
    }

    /// <summary>Lets go of every name's resolution, for a frame kept with an environment that waits for another function.</summary>
    internal void Forget()
    {
        System.Array.Clear(Slots);
        Global = null;
        Outer = null;
    }

    internal static int IndexOf(Key[] names, Key key)
    {
        for (var i = 0; i < names.Length; i++)
        {
            if (names[i] == key)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Where a name resolved: the environment and slot index of a slot binding, or a marker in the index.</summary>
internal struct KernelSlot
{
    internal DeclarativeEnvironment? Environment;
    internal int Index;

    // The value of Engine._envBindingInjectionEpoch when the name was resolved: a binding injected since may shadow it.
    internal int Epoch;
}

/// <summary>
/// A tree of kernel nodes that runs in place of the interpreter's handlers: names resolved to slots, Numbers kept
/// unboxed, typed array and array elements read and written directly, calls made through the lanes the interpreter
/// makes them through, and no completion records for statements that complete normally. Whatever the kernel does not
/// handle it leaves to the interpreter's handlers, case by case, so every case it handles is computed exactly as the
/// interpreter computes it; see <see cref="KernelExpression"/> for how a pure expression hands a case back.
/// <para>
/// A kernel runs only where nothing can observe how: a frame that cannot suspend, without a debugger, coverage or
/// operator overloading, whose completion values nobody reads. It keeps no environment past the run.
/// </para>
/// </summary>
internal abstract class KernelProgram
{
    private readonly Environment.BindingName[] _names;
    private KernelFrame? _frame;

    // Frames of runs inside a run (recursive calls), kept for the next ones.
    private KernelFrame? _spareFrames;
    private int _spareFrameCount;
    private const int MaxSpareFrames = 32;
    private Key[]? _layout;
    private int[]? _layoutIndices;
    private bool _layoutKnown;

    protected KernelProgram(Environment.BindingName[] names)
    {
        _names = names;
    }

    /// <summary>Whether the context lets a kernel run; see the class remarks.</summary>
    internal static bool CanRun(EvaluationContext context)
        => !context.DebugMode
           && !context.BypassStatementFastPaths
           && !context.OperatorOverloadingAllowed
           && context.Engine.ExecutionContext.Suspendable is null;

    private protected KernelFrame Enter(EvaluationContext context, bool resolveLayout = true)
    {
        var frame = _frame;
        if (frame is null)
        {
            frame = _frame = new KernelFrame(_names);
        }
        else if (frame.InUse)
        {
            // A run inside a run of the same kernel (a recursive call) has a frame of its own.
            frame = _spareFrames;
            if (frame is null)
            {
                frame = new KernelFrame(_names);
            }
            else
            {
                _spareFrames = frame.NextSpare;
                frame.NextSpare = null;
                _spareFrameCount--;
            }
        }

        frame.InUse = true;

        // The engine's own, kept from run to run: written only for a new frame.
        if (!ReferenceEquals(frame.Context, context))
        {
            frame.Context = context;
            frame.Engine = context.Engine;
        }

        var engine = frame.Engine;
        var start = frame.Start = engine.ExecutionContext.LexicalEnvironment;
        if (!_layoutKnown)
        {
            LearnLayout(start);
        }

        // The layout is the program's, which a frame keeps from run to run.
        if (frame.Layout is null && _layout is not null)
        {
            frame.Layout = _layout;
            frame.LayoutIndices = _layoutIndices;
        }

        if (resolveLayout)
        {
            frame.ResolveLayout();
        }

        return frame;
    }

    /// <summary>
    /// Records the slot names of the first environment the kernel runs in, which belong to the function, block or loop
    /// that declares them and hold no engine state, and the slot each of the kernel's names has in it.
    /// </summary>
    private void LearnLayout(Environment start)
    {
        _layoutKnown = true;
        if (start is DeclarativeEnvironment { _slotNames: { } layout })
        {
            var indices = new int[_names.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = KernelFrame.IndexOf(layout, _names[i].Key);
            }

            _layout = layout;
            _layoutIndices = indices;
        }
    }

    /// <summary>
    /// The frame kept with <paramref name="environment"/>, a call's own function environment, entered: names resolved
    /// in an earlier call stay resolved while the environment's outer environment is the same.
    /// </summary>
    private protected KernelFrame EnterKept(EvaluationContext context, FunctionEnvironment environment)
    {
        var frame = environment._kernelFrame;
        if (frame is null || frame.InUse || !ReferenceEquals(frame.Names, _names) || !ReferenceEquals(frame.Context, context))
        {
            return Attach(context, environment);
        }

        frame.InUse = true;
        if (!ReferenceEquals(frame.Outer, environment._outerEnv))
        {
            frame.Forget();
            frame.Outer = environment._outerEnv;
            frame.ResolveLayout();
        }

        return frame;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private KernelFrame Attach(EvaluationContext context, FunctionEnvironment environment)
    {
        var frame = new KernelFrame(_names)
        {
            InUse = true,
            Context = context,
            Engine = context.Engine,
            Start = environment,
            Outer = environment._outerEnv,
        };

        if (!_layoutKnown)
        {
            LearnLayout(environment);
        }

        frame.Layout = _layout;
        frame.LayoutIndices = _layoutIndices;
        frame.ResolveLayout();
        if (environment._kernelFrame is not { InUse: true })
        {
            environment._kernelFrame = frame;
        }

        return frame;
    }

    /// <summary>Ends a run in a frame <see cref="EnterKept"/> gave, which keeps its resolved names.</summary>
    private protected void LeaveKept(KernelFrame frame)
    {
        if (!ReferenceEquals(((FunctionEnvironment) frame.Start)._kernelFrame, frame))
        {
            Leave(frame);
            return;
        }

        if (frame.Abrupt.Value is not null)
        {
            frame.Abrupt = default;
        }

        if (frame.ReturnValue is not null)
        {
            frame.ReturnValue = null;
        }

        frame.InCode = false;
        frame.Escaping = false;
        frame.Work = 0;
        frame.InUse = false;
    }

    private protected void Leave(KernelFrame frame)
    {
        // Nothing of the run stays reachable from the handler tree, and the next run resolves every name again; the
        // layout holds no engine state.
        var slots = frame.Slots;
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = default;
        }

        frame.Global = null;
        frame.Abrupt = default;
        frame.ReturnValue = null;
        frame.Start = null!;
        frame.InCode = false;
        frame.Escaping = false;
        frame.Work = 0;
        frame.InUse = false;
        if (!ReferenceEquals(frame, _frame) && _spareFrameCount < MaxSpareFrames)
        {
            frame.NextSpare = _spareFrames;
            _spareFrames = frame;
            _spareFrameCount++;
        }
    }
}

/// <summary>A <c>for</c> loop run on a kernel.</summary>
internal sealed class LoopKernel : KernelProgram
{
    internal readonly KernelForStatement _loop;

    // The kernel's code (see KernelCode), compiled once the tree has checked CodeThreshold statements per node of the
    // kernel; the statements checked and the iterations begun until then, and whether compiling failed.
    internal KernelCode? _code;
    internal int _iterations;
    private long _work;
    private readonly int _size;
    private bool _noCode;

    // The kernel's loops, by entry.
    internal readonly KernelForStatement[] _loops;

    private LoopKernel(KernelForStatement loop, Environment.BindingName[] names, List<KernelForStatement> loops, int size) : base(names)
    {
        _loop = loop;
        _size = size;
        _loops = loops.ToArray();

        // Entry 0 is the outermost loop, whose code is also entered where it starts; every loop has one at its head.
        for (var i = 0; i < loops.Count; i++)
        {
            loops[i]._kernel = this;
            loops[i]._entry = i;
        }
    }

    internal static LoopKernel? TryCreate(JintForStatement owner, ForStatement statement)
    {
        var compiler = new KernelCompiler();
        var loop = compiler.CompileLoop(statement, owner);
        if (loop is null || !compiler.CompiledAnything)
        {
            return null;
        }

        return new LoopKernel(loop, compiler.Names, compiler.Loops, compiler.Size);
    }

    /// <summary>
    /// Whether the tree has done enough work for the code to pay: compiling costs about as much as running the tree
    /// for CodeThreshold statements per node, and code that runs a statement costs a fraction of the tree.
    /// </summary>
    internal bool WorthCompiling(KernelFrame frame)
    {
        var work = frame.Engine._loopKernelCodeWork;
        return _code is not null || _work + frame.Work >= (work >= 0 ? work : (long) frame.CodeThreshold * _size);
    }

    /// <summary>The kernel's code, compiled now if it has none yet; null where it cannot be compiled.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal KernelCode? Compile()
    {
        if (_code is null && !_noCode)
        {
            _code = KernelCode.TryCompile(_loop);
            _noCode = _code is null;
        }

        return _code;
    }

    /// <summary>Whether the context lets the loop run on its kernel: completion values must be dead too.</summary>
    internal static bool CanRunLoop(EvaluationContext context) => !context.CompletionValuesObservable && CanRun(context);

    internal Completion Run(EvaluationContext context)
    {
        var frame = Enter(context);
        frame.CodeThreshold = frame.Engine._loopKernelCodeThreshold;
        frame.Engine._loopKernelRuns++;
        try
        {
            var code = _loop.Execute(frame);
            _work += frame.Work;
            if (code == KernelStatement.Finished)
            {
                code = frame.FinishedCode;
            }

            return code == KernelStatement.Abrupt
                ? frame.Abrupt
                : new Completion(CompletionType.Normal, JsValue.Undefined, _loop._statement);
        }
        finally
        {
            Leave(frame);
        }
    }
}
