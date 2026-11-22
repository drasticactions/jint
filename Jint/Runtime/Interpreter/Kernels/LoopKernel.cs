using Jint.Native;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Statements;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// The state of one run of a loop kernel: the evaluation context, and where each name the kernel reads resolved when
/// the kernel was entered. The resolution holds for the whole run: the kernel runs in one environment, which it never
/// leaves (a block that declares something is left to the interpreter, which restores the environment), the slot
/// layout of a declarative environment never changes, and the one thing that adds a binding to an existing
/// environment, a sloppy direct eval or an Annex B function declaration, bumps
/// <see cref="Engine._envBindingInjectionEpoch"/>, which every read of the resolution checks.
/// </summary>
internal sealed class KernelFrame
{
    /// <summary>The slot index of a name that resolved to the global object.</summary>
    internal const int GlobalBinding = -2;

    /// <summary>The slot index of a name the kernel does not read: anything but a slot or the global object.</summary>
    internal const int Unresolved = -1;

    internal EvaluationContext Context = null!;
    internal Engine Engine = null!;
    internal int Epoch;
    internal GlobalEnvironment? Global;
    internal readonly KernelSlot[] Slots;
    internal Completion Abrupt;
    internal bool InUse;

    public KernelFrame(int names)
    {
        Slots = new KernelSlot[names];
    }
}

/// <summary>Where a name resolved: the environment and slot index of a slot binding, or a marker in the index.</summary>
internal struct KernelSlot
{
    internal DeclarativeEnvironment? Environment;
    internal int Index;
}

/// <summary>
/// A <c>for</c> loop run by a tree of kernel nodes instead of the interpreter's handlers: names resolved to slots once
/// on entry, Numbers kept unboxed, typed array and array elements read and written directly, built-in calls made
/// through their fast lane, and no completion records for statements that complete normally. Whatever the kernel does
/// not handle it leaves to the interpreter's handlers, case by case, so every case it handles is computed exactly as
/// the interpreter computes it; see <see cref="KernelExpression"/> for how a pure expression hands a case back.
/// <para>
/// A kernel runs only where nothing can observe how: a frame that cannot suspend, without a debugger, coverage or
/// operator overloading, whose completion values nobody reads. It keeps no environment past the run.
/// </para>
/// </summary>
internal sealed class LoopKernel
{
    private readonly KernelForStatement _loop;
    private readonly Environment.BindingName[] _names;
    private KernelFrame? _frame;

    private LoopKernel(KernelForStatement loop, Environment.BindingName[] names)
    {
        _loop = loop;
        _names = names;
    }

    internal static LoopKernel? TryCreate(JintForStatement owner, ForStatement statement)
    {
        var compiler = new KernelCompiler();
        var loop = compiler.CompileLoop(statement, owner);
        if (loop is null || !compiler.CompiledAnything)
        {
            return null;
        }

        return new LoopKernel(loop, compiler.Names);
    }

    /// <summary>Whether the context lets a kernel run; see the class remarks.</summary>
    internal static bool CanRun(EvaluationContext context)
        => !context.CompletionValuesObservable
           && !context.DebugMode
           && !context.BypassStatementFastPaths
           && !context.OperatorOverloadingAllowed
           && context.Engine.ExecutionContext.Suspendable is null;

    internal Completion Run(EvaluationContext context)
    {
        var frame = _frame;
        if (frame is null || frame.InUse)
        {
            frame = new KernelFrame(_names.Length);
            _frame ??= frame;
        }

        frame.InUse = true;
        frame.Context = context;
        var engine = frame.Engine = context.Engine;
        engine._loopKernelRuns++;
        try
        {
            Bind(frame, engine);
            var code = _loop.Execute(frame);
            return code == KernelStatement.Abrupt
                ? frame.Abrupt
                : new Completion(CompletionType.Normal, JsValue.Undefined, _loop._statement);
        }
        finally
        {
            // Nothing of the run stays reachable from the handler tree.
            System.Array.Clear(frame.Slots, 0, frame.Slots.Length);
            frame.Global = null;
            frame.Abrupt = default;
            frame.Context = null!;
            frame.Engine = null!;
            frame.InUse = false;
        }
    }

    /// <summary>
    /// Resolves each name as ResolveBinding would from the current environment
    /// (https://tc39.es/ecma262/#sec-getidentifierreference): through declarative environments that do not hold it,
    /// to the slot of the first that does, or to the global environment. A name held by name (a dictionary binding),
    /// in a module, a with object or the global lexical declarations stays unresolved; the kernel never reads it.
    /// </summary>
    private void Bind(KernelFrame frame, Engine engine)
    {
        frame.Epoch = engine._envBindingInjectionEpoch;
        var start = engine.ExecutionContext.LexicalEnvironment;
        var names = _names;
        for (var i = 0; i < names.Length; i++)
        {
            ref var slot = ref frame.Slots[i];
            slot.Environment = null;
            slot.Index = KernelFrame.Unresolved;
            var key = names[i].Key;
            for (var e = start; e is not null; e = e._outerEnv)
            {
                if (e is GlobalEnvironment global)
                {
                    frame.Global = global;
                    slot.Index = KernelFrame.GlobalBinding;
                    break;
                }

                if (e is not DeclarativeEnvironment declarative || e is ModuleEnvironment || !ReferenceEquals(declarative._engine, engine))
                {
                    break;
                }

                if (SlotLayoutMemo.DictionaryOwns(declarative, key))
                {
                    break;
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

                        break;
                    }
                }
            }
        }
    }

    private static int IndexOf(Key[] names, Key key)
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
