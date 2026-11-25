using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Jint.Constraints;
using Jint.Native;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// Compiles a <see cref="KernelForStatement"/>, or a function body (a <see cref="KernelBlockStatement"/>), and everything
/// in it to one dynamic method (see <see cref="KernelCode"/>).
/// <para>
/// Each node becomes the IL of its own steps. A pure expression that meets a case it does not handle jumps to the
/// hand-back of the node that would have handed it back in the tree (the <see cref="KernelRoot"/> above it, or the
/// effect whose operands it is), which runs the interpreter's handler as the tree does. A Number is a double in a
/// local, a value a <see cref="JsValue"/> in another, as the tree's <c>(JsValue?, double)</c> pairs. A name is read
/// through a reference to the slot it resolved to on entry, while no binding has been injected since (otherwise, and
/// for a name not in a slot, through its node). Every node the emitter has no IL for is called as the tree calls it.
/// </para>
/// <para>
/// A name is held in locals between the steps that run anything else ("foreign" steps: a call, a hand-back, a node of
/// the tree): a Number of a slot of a mutable binding, which the code reads and stores in the local, or any other value
/// it read, which it only reads. Before a foreign step the Numbers the code stored are written back to their slots
/// (and the statement's location stored, see below); after it every local is dropped, and the next read reads the
/// binding again. Nothing else can read or change a binding while the code holds it, so no one sees a difference. Only
/// foreign steps and checks that run a constraint throw, so an exception finds the bindings written back, and the
/// handlers need no locals, which keeps every other local of the method in registers.
/// </para>
/// <para>
/// A statement's checks run where the tree runs them. Its location, which only a foreign step, a check that runs a
/// constraint, an exception or the end of the run can see, is stored before the first of those, as the location the
/// interpreter would have stored when it began the statement.
/// </para>
/// <para>
/// The whole method is one try block. Its filter and handler stand in for the handlers of the blocks the code runs
/// (<see cref="KernelBlockStatement"/>): a local holds the statement of the innermost block being run (a
/// <see cref="KernelCodeSites"/> site), set as each statement of a block begins and put back where the code returns to
/// the statement around it, and the handler does what the handlers of that block and of the blocks around it would.
/// Nothing inside the method catches, so that the JIT keeps the method's locals in registers.
/// </para>
/// <para>
/// IL here keeps one rule: a sub-expression is emitted with an empty evaluation stack and leaves its result in a local
/// (or as the one value on the stack, where noted), so that every jump to a hand-back is made with an empty stack.
/// </para>
/// </summary>
[RequiresDynamicCode("Compiles a kernel to a dynamic method; KernelCode.TryCompile calls it only where dynamic code is supported.")]
internal sealed class KernelEmitter
{
    private static readonly Type[] Parameters = [typeof(object[]), typeof(KernelFrame), typeof(int)];

    private readonly DynamicMethod _method;
    private readonly ILGenerator _il;
    private readonly KernelStatement _root;
    private readonly List<object> _constants = [];
    private readonly Dictionary<object, int> _constantIndices = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, LocalBuilder> _hot = new(ReferenceEqualityComparer.Instance);
    private readonly SortedDictionary<int, NameLocals> _names = new();
    private readonly Dictionary<Type, Stack<LocalBuilder>> _free = [];
    private readonly List<Loop> _loops = [];
    private readonly List<(KernelBlockStatement Block, int Index, int Parent)> _sites = [];

    // The bases of the callee reads held at call sites, dropped with the names' locals.
    private readonly List<LocalBuilder> _siteCaches = [];

    // Statements the tree ran fewer times than this stay on the tree (a node of the tree in the code): code for a case
    // that hardly ever runs costs the JIT as much as code for one that runs on every iteration. No limit until the
    // kernel has run enough iterations to tell.
    private readonly int _coldLimit;
    private const int ColdShare = 64;
    private const int ColdEvidence = 256;

    // Each loop's head, by entry, and the site of the code there.
    private readonly Label[] _heads;
    private readonly int[] _headSites;

    private readonly LocalBuilder _engine;
    private readonly LocalBuilder _context;
    private readonly LocalBuilder _exact;
    private readonly LocalBuilder _counter;
    private readonly LocalBuilder _epoch;
    private readonly LocalBuilder _siteLocal;

    // The statement whose location the interpreter would have stored last, while the code has not stored it.
    private readonly LocalBuilder _pending;

    // The shared blocks before and after a foreign step, and where each returns to.
    private readonly Label _syncLabel;
    private readonly Label _dropLabel;
    private readonly List<Label> _syncReturns = [];
    private readonly List<Label> _dropReturns = [];
    private readonly LocalBuilder _syncReturn;
    private readonly LocalBuilder _dropReturn;

    // The one local whose address is taken, for an out parameter of a call off the hot paths: a local whose address
    // is taken lives in memory.
    private readonly LocalBuilder _outEnvironment;
    private int _depth;

    // The site of the code being emitted: the statement of the innermost block it is in, or NoSite.
    private int _site = NoSite;
    private const int NoSite = -1;

    // The site of a target outside the try block, which no handler is asked about.
    private const int AnySite = int.MinValue;
    private Target _abruptExit;

    // For a function body: the environment's slot names, each name's slot (or -1), their local, and a return's exit.
    private readonly Key[]? _layout;
    private readonly int[]? _localSlots;
    private readonly LocalBuilder? _slotsLocal;
    private readonly LocalBuilder? _returnValue;
    private readonly Label _returnExit;

    private KernelEmitter(KernelStatement root, int iterations, int loops, Key[]? layout = null, int[]? localSlots = null)
    {
        _root = root;
        _layout = layout;
        _localSlots = localSlots;
        var isFunction = root is not KernelForStatement;
        _method = isFunction
            ? new DynamicMethod("JintFunctionKernel", typeof(JsValue), Parameters, typeof(KernelEmitter).Module, skipVisibility: true)
            : new DynamicMethod("JintLoopKernel", typeof(int), Parameters, typeof(KernelEmitter).Module, skipVisibility: true);
        _il = _method.GetILGenerator();
        _engine = _il.DeclareLocal(typeof(Engine));
        _context = _il.DeclareLocal(typeof(EvaluationContext));
        _exact = _il.DeclareLocal(typeof(bool));
        _counter = _il.DeclareLocal(typeof(MaxStatementsConstraint));
        _epoch = _il.DeclareLocal(typeof(int));
        _siteLocal = _il.DeclareLocal(typeof(int));
        _outEnvironment = _il.DeclareLocal(typeof(DeclarativeEnvironment));
        _pending = _il.DeclareLocal(typeof(Statement));
        _syncReturn = _il.DeclareLocal(typeof(int));
        _dropReturn = _il.DeclareLocal(typeof(int));
        _syncLabel = _il.DefineLabel();
        _dropLabel = _il.DefineLabel();
        if (isFunction)
        {
            _slotsLocal = _il.DeclareLocal(typeof(Binding[]));
            _returnValue = _il.DeclareLocal(typeof(JsValue));
            _returnExit = _il.DefineLabel();
        }
        _coldLimit = iterations >= ColdEvidence ? iterations / ColdShare : 0;
        _heads = new Label[loops];
        _headSites = new int[loops];
        for (var i = 0; i < loops; i++)
        {
            _heads[i] = _il.DefineLabel();
        }
    }

    internal static KernelCode Compile(KernelForStatement loop)
        => Compile(new KernelEmitter(loop, loop._kernel!._iterations, loop._kernel._loops.Length));

    /// <summary>
    /// The code of a function body with <paramref name="loops"/> loops whose tree ran <paramref name="calls"/> calls,
    /// the slot names of the call's environment, and the slot of each name in it (or -1).
    /// </summary>
    internal static KernelCode Compile(KernelBlockStatement body, int loops, int calls, Key[]? layout, int[] localSlots)
    {
        var emitter = new KernelEmitter(body, calls, loops, layout, localSlots);
        emitter.EmitMethod();
        var constants = emitter._constants.ToArray();
        var entry = (KernelCode.FunctionEntry) emitter._method.CreateDelegate(typeof(KernelCode.FunctionEntry), constants);
        return new KernelCode(entry, constants);
    }

    private static KernelCode Compile(KernelEmitter emitter)
    {
        emitter.EmitMethod();
        var entry = (KernelCode.Entry) emitter._method.CreateDelegate(typeof(KernelCode.Entry));
        return new KernelCode(entry, emitter._constants.ToArray());
    }

    private readonly struct Target
    {
        internal readonly Label Label;
        internal readonly int Depth;
        internal readonly int Site;

        internal Target(Label label, int depth, int site)
        {
            Label = label;
            Depth = depth;
            Site = site;
        }
    }

    private sealed class Loop
    {
        internal KernelForStatement Node = null!;
        internal Target Update;
        internal Target End;
        internal Target Abrupt;
        internal bool AbruptUsed;
    }

    private sealed class NameLocals
    {
        internal int Binding;
        internal LocalBuilder Ref = null!;
        internal LocalBuilder Environment = null!;

        // What the code holds of the name, in State: the Number of a slot the code may store to (NumberHeld), or any
        // other value it read (ValueHeld). With a value, whether it is a Uint8Array whose facts are taken (TypedHeld,
        // or NotTyped), and whether its length as an array or a typed array is held (LengthHeld, or NoLength). One
        // store of 0 drops it all.
        internal const int NumberHeld = 1;
        internal const int ValueHeld = 2;
        internal const int TypedHeld = 4;
        internal const int NotTyped = 8;
        internal const int LengthHeld = 16;
        internal const int NoLength = 32;

        internal LocalBuilder State = null!;
        internal LocalBuilder Number = null!;
        internal LocalBuilder Value = null!;
        internal LocalBuilder TypedData = null!;
        internal LocalBuilder TypedOffset = null!;
        internal LocalBuilder TypedLength = null!;
        internal LocalBuilder Length = null!;

        // Whether the code stores a Number to the local, which then has to be written back.
        internal bool Stored;
    }

    private void EmitMethod()
    {
        var il = _il;
        var prologue = il.DefineLabel();
        var body = il.DefineLabel();
        var normalExit = new Target(il.DefineLabel(), 0, AnySite);
        _abruptExit = new Target(il.DefineLabel(), 0, AnySite);
        il.Emit(OpCodes.Br, prologue);

        il.BeginExceptionBlock();
        _depth = 1;
        il.MarkLabel(body);

        // An entry at a loop's head: to a stub that sets the site there.
        var start = il.DefineLabel();
        var stubs = new Label[_heads.Length];
        for (var i = 0; i < stubs.Length; i++)
        {
            stubs[i] = il.DefineLabel();
        }

        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, start);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Switch, stubs);
        il.MarkLabel(start);
        if (_root is KernelForStatement loop)
        {
            EmitLoop(loop, isRoot: true);
        }
        else
        {
            EmitBlock((KernelBlockStatement) _root);
        }

        EmitExit(normalExit);
        for (var i = 0; i < stubs.Length; i++)
        {
            il.MarkLabel(stubs[i]);
            il.Emit(OpCodes.Ldc_I4, _headSites[i]);
            il.Emit(OpCodes.Stloc, _siteLocal);
            il.Emit(OpCodes.Br, _heads[i]);
        }

        EmitSharedBlocks();

        // The handlers of the blocks around the site: an exception they turn into a throw completion ends every loop.
        var sites = new KernelCodeSites(_sites);
        il.BeginExceptFilterBlock();
        var thrown = il.DeclareLocal(typeof(object));
        il.Emit(OpCodes.Stloc, thrown);
        EmitConstant(sites);
        il.Emit(OpCodes.Ldloc, thrown);
        il.Emit(OpCodes.Ldloc, _siteLocal);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeSites), nameof(KernelCodeSites.Catches)));
        il.BeginCatchBlock(null!);
        var exception = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Castclass, typeof(Exception));
        il.Emit(OpCodes.Stloc, exception);
        EmitConstant(sites);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, exception);
        il.Emit(OpCodes.Ldloc, _siteLocal);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeSites), nameof(KernelCodeSites.Catch)));
        var rethrow = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, rethrow);
        il.Emit(OpCodes.Leave, _abruptExit.Label);
        il.MarkLabel(rethrow);
        il.Emit(OpCodes.Rethrow);
        il.EndExceptionBlock();
        _depth = 0;

        if (_returnValue is not null)
        {
            // A function body: undefined for a normal completion, null for an abrupt one, or the value returned.
            il.MarkLabel(normalExit.Label);
            il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
            il.Emit(OpCodes.Ret);
            il.MarkLabel(_abruptExit.Label);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(_returnExit);
            il.Emit(OpCodes.Ldloc, _returnValue);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            il.MarkLabel(normalExit.Label);
            il.Emit(OpCodes.Ldc_I4, KernelStatement.Normal);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(_abruptExit.Label);
            il.Emit(OpCodes.Ldc_I4, KernelStatement.Abrupt);
            il.Emit(OpCodes.Ret);
        }

        // The prologue, emitted last because it loads what the body turned out to need.
        il.MarkLabel(prologue);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelFrame), nameof(KernelFrame.Engine)));
        il.Emit(OpCodes.Stloc, _engine);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelFrame), nameof(KernelFrame.Context)));
        il.Emit(OpCodes.Stloc, _context);
        il.Emit(OpCodes.Ldloc, _context);
        il.Emit(OpCodes.Call, Getter(typeof(EvaluationContext), nameof(EvaluationContext.ShouldRunPerStatementChecks)));
        il.Emit(OpCodes.Stloc, _exact);
        il.Emit(OpCodes.Ldloc, _context);
        il.Emit(OpCodes.Call, Getter(typeof(EvaluationContext), nameof(EvaluationContext.StatementCounter)));
        il.Emit(OpCodes.Stloc, _counter);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, Field(typeof(Engine), nameof(Engine._envBindingInjectionEpoch)));
        il.Emit(OpCodes.Stloc, _epoch);
        il.Emit(OpCodes.Ldc_I4, NoSite);
        il.Emit(OpCodes.Stloc, _siteLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, _pending);
        if (_slotsLocal is not null && _layout is not null)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldfld, Field(typeof(KernelFrame), nameof(KernelFrame.Start)));
            EmitConstant(_layout, typeof(Key[]));
            il.Emit(OpCodes.Ldloc, _engine);
            il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.LocalSlots)));
            il.Emit(OpCodes.Stloc, _slotsLocal);
        }

        foreach (var name in _names)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, name.Key);
            if (_slotsLocal is not null && _localSlots![name.Key] >= 0)
            {
                il.Emit(OpCodes.Ldc_I4, _localSlots[name.Key]);
                il.Emit(OpCodes.Ldloc, _slotsLocal);
                il.Emit(OpCodes.Ldloca, _outEnvironment);
                il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.BindLocal)));
                il.Emit(OpCodes.Stloc, name.Value.Ref);
                il.Emit(OpCodes.Ldloc, _outEnvironment);
                il.Emit(OpCodes.Stloc, name.Value.Environment);
                continue;
            }

            il.Emit(OpCodes.Ldloca, _outEnvironment);
            il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.Bind)));
            il.Emit(OpCodes.Stloc, name.Value.Ref);
            il.Emit(OpCodes.Ldloc, _outEnvironment);
            il.Emit(OpCodes.Stloc, name.Value.Environment);
        }

        foreach (var hot in _hot)
        {
            EmitConstantLoad(hot.Key, hot.Value.LocalType);
            il.Emit(OpCodes.Stloc, hot.Value);
        }

        il.Emit(OpCodes.Br, body);
    }

    // ----- loops and statements -----

    private void EmitLoop(KernelForStatement node, bool isRoot)
    {
        var il = _il;
        var head = _heads[node._entry];
        _headSites[node._entry] = _site;
        var loop = new Loop
        {
            Node = node,
            Update = NewTarget(),
            End = NewTarget(),
            Abrupt = NewTarget(),
        };

        // The loop the kernel stands in for has been checked by its own Execute already.
        if (node._owner is null)
        {
            EmitCheck(node._statement);
        }

        if (node._initStatement is not null)
        {
            EmitStatement(node._initStatement, ignoreCode: true);
        }

        if (node._initExpression is not null)
        {
            EmitDiscard(node._initExpression);
        }

        il.MarkLabel(head);
        _loops.Add(loop);
        if (node._test is not null)
        {
            EmitBranch(node._test, null, loop.End.Label, when: false);
        }

        EmitStatement(node._body);
        il.MarkLabel(loop.Update.Label);
        if (node._update is not null)
        {
            EmitDiscard(node._update);
        }

        il.Emit(OpCodes.Br, head);
        _loops.RemoveAt(_loops.Count - 1);

        if (loop.AbruptUsed)
        {
            il.MarkLabel(loop.Abrupt.Label);
            EmitConstant(node);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, Method(typeof(KernelForStatement), nameof(KernelForStatement.AbruptOutcome)));
            il.Emit(OpCodes.Switch, new[] { loop.Update.Label, loop.End.Label });
            EmitAbrupt();
        }

        il.MarkLabel(loop.End.Label);
    }

    /// <summary>Where an abrupt completion of a statement goes: to the innermost loop, which may continue or end with it.</summary>
    private Target AbruptTarget()
    {
        if (_loops.Count == 0)
        {
            return _abruptExit;
        }

        var loop = _loops[_loops.Count - 1];
        loop.AbruptUsed = true;
        return loop.Abrupt;
    }

    private Loop InnermostLoop => _loops[_loops.Count - 1];

    private void EmitStatement(KernelStatement statement, bool ignoreCode = false)
    {
        var il = _il;
        if (statement._runs < _coldLimit && !ContainsLoop(statement))
        {
            EmitTreeStatement(statement, ignoreCode);
            return;
        }

        switch (statement)
        {
            case KernelExpressionStatement expressionStatement:
                EmitCheck(statement._statement);
                EmitDiscard(expressionStatement._expression);
                return;

            case KernelVarStatement varStatement:
                EmitCheck(statement._statement);
                foreach (var initializer in varStatement._initializers)
                {
                    EmitDiscard(initializer);
                }

                return;

            case KernelReturnStatement returnStatement:
                {
                    EmitCheck(statement._statement);
                    var value = Rent(typeof(JsValue));
                    if (returnStatement._argument is null)
                    {
                        il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
                    }
                    else
                    {
                        EmitBoxed(returnStatement._argument);
                    }

                    il.Emit(OpCodes.Stloc, value);
                    if (_returnValue is not null)
                    {
                        il.Emit(OpCodes.Ldloc, value);
                        il.Emit(OpCodes.Stloc, _returnValue);
                        Return(value);
                        EmitExit(new Target(_returnExit, 0, AnySite));
                        return;
                    }

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloc, value);
                    EmitConstant(statement._statement, typeof(Statement));
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.SetReturn)));
                    Return(value);
                    EmitExit(_abruptExit);
                    return;
                }

            case KernelLiteralReturnStatement literalReturn:
                if (_returnValue is not null)
                {
                    EmitConstant(literalReturn._value, typeof(JsValue));
                    il.Emit(OpCodes.Stloc, _returnValue);
                    EmitExit(new Target(_returnExit, 0, AnySite));
                    return;
                }

                il.Emit(OpCodes.Ldarg_1);
                EmitConstant(literalReturn._value, typeof(JsValue));
                EmitConstant(statement._statement, typeof(Statement));
                il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.SetReturn)));
                EmitExit(_abruptExit);
                return;

            case KernelEmptyStatement:
                EmitCheck(statement._statement);
                return;

            case KernelJumpStatement jump:
                EmitCheck(statement._statement);
                EmitJump(jump._code == KernelStatement.Break ? InnermostLoop.End : InnermostLoop.Update);
                return;

            case KernelIfStatement ifStatement:
                {
                    EmitCheck(statement._statement);
                    var otherwise = il.DefineLabel();
                    EmitBranch(ifStatement._test, null, otherwise, when: false);
                    EmitStatement(ifStatement._consequent);
                    if (ifStatement._alternate is null)
                    {
                        il.MarkLabel(otherwise);
                        return;
                    }

                    var end = il.DefineLabel();
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(otherwise);
                    EmitStatement(ifStatement._alternate);
                    il.MarkLabel(end);
                    return;
                }

            case KernelBlockStatement block:
                EmitBlock(block);
                return;

            case KernelForStatement loop:
                EmitLoop(loop, isRoot: false);
                return;

            default:
                EmitTreeStatement(statement, ignoreCode);
                return;
        }
    }

    private static bool ContainsLoop(KernelStatement statement) => statement switch
    {
        KernelForStatement => true,
        KernelBlockStatement block => Array.Exists(block._statements, ContainsLoop),
        KernelIfStatement ifStatement => ContainsLoop(ifStatement._consequent) || (ifStatement._alternate is { } alternate && ContainsLoop(alternate)),
        _ => false,
    };

    /// <summary>A statement run by its node of the tree, as the tree runs it (a KernelInterpretedStatement, or any).</summary>
    private void EmitTreeStatement(KernelStatement statement, bool ignoreCode)
    {
        var il = _il;
        switch (statement)
        {
            default:
                {
                    EmitBeforeForeign();
                    EmitConstant(statement);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, Method(typeof(KernelStatement), nameof(KernelStatement.Execute)));
                    var code = Rent(typeof(int));
                    il.Emit(OpCodes.Stloc, code);
                    EmitAfterForeign();
                    if (ignoreCode)
                    {
                        Return(code);
                        return;
                    }

                    var next = il.DefineLabel();
                    var notBreak = il.DefineLabel();
                    var notContinue = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, code);
                    il.Emit(OpCodes.Brfalse, next);
                    if (_loops.Count == 0)
                    {
                        // Outside a loop an unlabelled break or continue cannot complete a statement.
                        EmitAbrupt();
                        il.MarkLabel(next);
                        Return(code);
                        return;
                    }

                    il.Emit(OpCodes.Ldloc, code);
                    il.Emit(OpCodes.Ldc_I4, KernelStatement.Break);
                    il.Emit(OpCodes.Bne_Un, notBreak);
                    EmitJump(InnermostLoop.End);
                    il.MarkLabel(notBreak);
                    il.Emit(OpCodes.Ldloc, code);
                    il.Emit(OpCodes.Ldc_I4, KernelStatement.Continue);
                    il.Emit(OpCodes.Bne_Un, notContinue);
                    EmitJump(InnermostLoop.Update);
                    il.MarkLabel(notContinue);
                    EmitAbrupt();
                    il.MarkLabel(next);
                    Return(code);
                    return;
                }
        }
    }

    /// <summary>
    /// A block as <see cref="KernelBlockStatement"/> runs it, its statements each at a site of its own, so that the
    /// method's handler handles an exception out of statement <c>i</c> as the block's handler would.
    /// </summary>
    private void EmitBlock(KernelBlockStatement block)
    {
        var il = _il;
        if (block._checked)
        {
            EmitCheck(block._statement);
        }

        var statements = block._statements;
        if (statements.Length == 0)
        {
            return;
        }

        var parent = _site;
        for (var i = 0; i < statements.Length; i++)
        {
            _sites.Add((block, i, parent));
            SetSite(_sites.Count - 1);
            EmitStatement(statements[i]);
        }

        SetSite(parent);
    }

    private void SetSite(int site)
    {
        _site = site;
        _il.Emit(OpCodes.Ldc_I4, site);
        _il.Emit(OpCodes.Stloc, _siteLocal);
    }

    /// <summary>
    /// <see cref="KernelCodeRuntime.Check"/>: the statement becomes the pending location; when no check would run a
    /// constraint, one statement off the exact constraints' allowance and the amortized countdown in place; otherwise
    /// the whole check, out of line, which stores the location first.
    /// </summary>
    private void EmitCheck(Statement statement)
    {
        var il = _il;
        EmitHot(statement, typeof(Statement));
        il.Emit(OpCodes.Stloc, _pending);

        var slow = il.DefineLabel();
        var notExact = il.DefineLabel();
        var amortized = il.DefineLabel();
        var commit = il.DefineLabel();
        var done = il.DefineLabel();
        var allowance = Field(typeof(Engine), nameof(Engine._statementsBeforeExactChecks));
        var countdown = Field(typeof(Engine), nameof(Engine._amortizedConstraintCountdown));
        il.Emit(OpCodes.Ldloc, _exact);
        il.Emit(OpCodes.Brfalse, notExact);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, allowance);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, slow);
        il.Emit(OpCodes.Br, amortized);
        il.MarkLabel(notExact);
        il.Emit(OpCodes.Ldloc, _counter);
        il.Emit(OpCodes.Brtrue, slow);
        il.MarkLabel(amortized);
        il.Emit(OpCodes.Ldloc, _context);
        il.Emit(OpCodes.Call, Getter(typeof(EvaluationContext), nameof(EvaluationContext.RunsAmortizedChecks)));
        il.Emit(OpCodes.Brfalse, commit);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, countdown);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ble, slow);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, countdown);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stfld, countdown);
        il.MarkLabel(commit);
        il.Emit(OpCodes.Ldloc, _exact);
        il.Emit(OpCodes.Brfalse, done);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, allowance);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stfld, allowance);
        il.Emit(OpCodes.Br, done);

        // A constraint may run, which sees the location and may throw: what a foreign step would see first.
        il.MarkLabel(slow);
        EmitBeforeForeign();
        il.Emit(OpCodes.Ldloc, _context);
        il.Emit(OpCodes.Ldloc, _engine);
        EmitHot(statement, typeof(Statement));
        il.Emit(OpCodes.Ldloc, _exact);
        il.Emit(OpCodes.Ldloc, _counter);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.Check)));
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, _pending);
        il.MarkLabel(done);
    }

    /// <summary>Before a foreign step: the pending location stored, the Numbers the code stored written back.</summary>
    private void EmitBeforeForeign()
    {
        var il = _il;
        var back = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4, _syncReturns.Count);
        il.Emit(OpCodes.Stloc, _syncReturn);
        il.Emit(OpCodes.Br, _syncLabel);
        il.MarkLabel(back);
        _syncReturns.Add(back);
    }

    /// <summary>After a foreign step: every local a name is held in dropped.</summary>
    private void EmitAfterForeign()
    {
        var il = _il;
        var back = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4, _dropReturns.Count);
        il.Emit(OpCodes.Stloc, _dropReturn);
        il.Emit(OpCodes.Br, _dropLabel);
        il.MarkLabel(back);
        _dropReturns.Add(back);
    }

    /// <summary>The end of the run: everything a foreign step would see, then out of the try blocks.</summary>
    private void EmitExit(Target exit)
    {
        EmitBeforeForeign();
        _il.Emit(OpCodes.Leave, exit.Label);
    }

    /// <summary>An abrupt completion of a statement: to the innermost loop, or out of the run.</summary>
    private void EmitAbrupt()
    {
        var target = AbruptTarget();
        if (target.Label == _abruptExit.Label)
        {
            EmitExit(target);
        }
        else
        {
            EmitJump(target);
        }
    }

    /// <summary>The blocks <see cref="EmitBeforeForeign"/> and <see cref="EmitAfterForeign"/> jump to, emitted once.</summary>
    private void EmitSharedBlocks()
    {
        var il = _il;
        il.MarkLabel(_syncLabel);
        var located = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, _pending);
        il.Emit(OpCodes.Brfalse, located);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldloc, _pending);
        il.Emit(OpCodes.Call, SetLastSyntaxElement);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, _pending);
        il.MarkLabel(located);
        EmitWriteBack();
        il.Emit(OpCodes.Ldloc, _syncReturn);
        il.Emit(OpCodes.Switch, _syncReturns.ToArray());
        il.Emit(OpCodes.Br, _syncReturns[0]);

        il.MarkLabel(_dropLabel);
        foreach (var name in _names.Values)
        {
            EmitDrop(name);
        }

        foreach (var site in _siteCaches)
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc, site);
        }

        if (_dropReturns.Count == 0)
        {
            il.Emit(OpCodes.Br, _syncLabel);
            return;
        }

        il.Emit(OpCodes.Ldloc, _dropReturn);
        il.Emit(OpCodes.Switch, _dropReturns.ToArray());
        il.Emit(OpCodes.Br, _dropReturns[0]);
    }

    /// <summary>Every Number the code stored to a local written back to its slot.</summary>
    private void EmitWriteBack()
    {
        var il = _il;
        foreach (var name in _names.Values)
        {
            if (!name.Stored)
            {
                continue;
            }

            var skip = il.DefineLabel();
            EmitHeld(name, NameLocals.NumberHeld);
            il.Emit(OpCodes.Brfalse, skip);
            il.Emit(OpCodes.Ldloc, name.Ref);
            il.Emit(OpCodes.Ldloc, name.Number);
            il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.StoreMutableNumber)));
            il.MarkLabel(skip);
        }
    }

    /// <summary>
    /// After a store to a name's binding that did not go through its locals: the binding read again, so that the
    /// statements after find it held.
    /// </summary>
    private void EmitReload(KernelName name, NameLocals locals)
    {
        EmitDrop(locals);
        var value = Rent(typeof(JsValue));
        var number = Rent(typeof(double));
        EmitNameCold(name, locals, value, number);
        Return(number);
        Return(value);
    }

    /// <summary>The bits <paramref name="bits"/> of the name's state, left on the stack.</summary>
    private void EmitHeld(NameLocals name, int bits)
    {
        _il.Emit(OpCodes.Ldloc, name.State);
        _il.Emit(OpCodes.Ldc_I4, bits);
        _il.Emit(OpCodes.And);
    }

    private void EmitAddState(NameLocals name, int bits)
    {
        _il.Emit(OpCodes.Ldloc, name.State);
        _il.Emit(OpCodes.Ldc_I4, bits);
        _il.Emit(OpCodes.Or);
        _il.Emit(OpCodes.Stloc, name.State);
    }

    /// <summary>The locals of a name dropped, after a store to its binding that did not go through them.</summary>
    private void EmitDrop(NameLocals name)
    {
        var il = _il;
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, name.State);
    }

    // ----- expressions -----

    private static bool IsPure(KernelExpression expression) => expression
        is KernelNumber or KernelConstant or KernelName or KernelBinary or KernelLogical or KernelUnary or KernelConditional
        or KernelElement or KernelProperty;

    /// <summary>
    /// The value of <paramref name="expression"/> into <paramref name="value"/> and <paramref name="number"/>, as the
    /// tree's <c>Evaluate</c> answers it (a null value for a Number); a pure expression jumps to <paramref name="bail"/>
    /// where the tree answers Bail.
    /// </summary>
    private void EmitUnion(KernelExpression expression, Label? bail, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        Debug.Assert(bail is not null || !IsPure(expression));
        switch (expression)
        {
            case KernelNumber or KernelBinary { _isComparison: false } or KernelUnary { _operator: not Operator.LogicalNot }:
                EmitNumber(expression, bail!.Value);
                il.Emit(OpCodes.Stloc, number);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Stloc, value);
                return;

            case KernelConstant constant:
                EmitConstant(constant._value, typeof(JsValue));
                il.Emit(OpCodes.Stloc, value);
                return;

            case KernelBinary or KernelUnary:
                EmitBooleanValue(expression, bail, value);
                return;

            case KernelName name:
                EmitNameUnion(name, bail!.Value, value, number);
                return;

            case KernelLogical logical:
                {
                    var done = il.DefineLabel();
                    EmitUnion(logical._left, bail, value, number);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, TruthyOfUnion);
                    il.Emit(logical._and ? OpCodes.Brfalse : OpCodes.Brtrue, done);
                    EmitUnion(logical._right, bail, value, number);
                    il.MarkLabel(done);
                    return;
                }

            case KernelConditional conditional:
                {
                    var otherwise = il.DefineLabel();
                    var done = il.DefineLabel();
                    EmitBranch(conditional._test, bail, otherwise, when: false);
                    EmitUnion(conditional._consequent, bail, value, number);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(otherwise);
                    EmitUnion(conditional._alternate, bail, value, number);
                    il.MarkLabel(done);
                    return;
                }

            case KernelElement element:
                {
                    var target = Rent(typeof(JsValue));
                    var targetNumber = Rent(typeof(double));
                    EmitUnion(element._object, bail, target, targetNumber);
                    il.Emit(OpCodes.Ldloc, target);
                    il.Emit(OpCodes.Brfalse, bail!.Value);
                    EmitNumber(element._key, bail.Value);
                    var key = Rent(typeof(double));
                    il.Emit(OpCodes.Stloc, key);
                    var generic = il.DefineLabel();
                    var read = il.DefineLabel();
                    if (element._object is KernelName held)
                    {
                        // The elements of a Uint8Array the name holds, read in place.
                        var index = Rent(typeof(int));
                        EmitTypedIndex(Name(held), target, key, index, generic);
                        var locals = Name(held);
                        il.Emit(OpCodes.Ldloc, locals.TypedData);
                        il.Emit(OpCodes.Ldloc, locals.TypedOffset);
                        il.Emit(OpCodes.Ldloc, index);
                        il.Emit(OpCodes.Add);
                        il.Emit(OpCodes.Ldelem_U1);
                        il.Emit(OpCodes.Conv_R8);
                        il.Emit(OpCodes.Stloc, number);
                        il.Emit(OpCodes.Ldnull);
                        il.Emit(OpCodes.Stloc, value);
                        il.Emit(OpCodes.Br, read);
                        Return(index);
                    }

                    il.MarkLabel(generic);
                    il.Emit(OpCodes.Ldloc, target);
                    il.Emit(OpCodes.Ldloc, key);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ReadElement)));
                    EmitResult(value, number, bail);
                    il.MarkLabel(read);
                    Return(key);
                    Return(targetNumber);
                    Return(target);
                    return;
                }

            case KernelProperty property:
                {
                    var target = Rent(typeof(JsValue));
                    var targetNumber = Rent(typeof(double));
                    EmitUnion(property._object, bail, target, targetNumber);
                    il.Emit(OpCodes.Ldloc, target);
                    il.Emit(OpCodes.Brfalse, bail!.Value);
                    var generic = il.DefineLabel();
                    var read = il.DefineLabel();
                    if (property._isLength && property._object is KernelName held)
                    {
                        // The length of an array or a typed array the name holds, held with it.
                        var locals = Name(held);
                        var known = il.DefineLabel();
                        EmitHeld(locals, NameLocals.LengthHeld);
                        il.Emit(OpCodes.Brtrue, known);
                        EmitHeld(locals, NameLocals.ValueHeld | NameLocals.NoLength);
                        il.Emit(OpCodes.Ldc_I4, NameLocals.ValueHeld);
                        il.Emit(OpCodes.Bne_Un, generic);
                        EmitHot(property, typeof(KernelProperty));
                        il.Emit(OpCodes.Ldloc, target);
                        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.LengthFact)));
                        var fact = Rent(typeof(KernelResult));
                        il.Emit(OpCodes.Stloc, fact);
                        il.Emit(OpCodes.Ldloc, fact);
                        il.Emit(OpCodes.Ldfld, ResultNumber);
                        il.Emit(OpCodes.Stloc, locals.Length);
                        il.Emit(OpCodes.Ldloc, fact);
                        il.Emit(OpCodes.Ldfld, ResultValue);
                        Return(fact);
                        var hasLength = il.DefineLabel();
                        il.Emit(OpCodes.Brfalse, hasLength);
                        EmitAddState(locals, NameLocals.NoLength);
                        il.Emit(OpCodes.Br, generic);
                        il.MarkLabel(hasLength);
                        EmitAddState(locals, NameLocals.LengthHeld);
                        il.MarkLabel(known);
                        il.Emit(OpCodes.Ldloc, locals.Length);
                        il.Emit(OpCodes.Stloc, number);
                        il.Emit(OpCodes.Ldnull);
                        il.Emit(OpCodes.Stloc, value);
                        il.Emit(OpCodes.Br, read);
                    }

                    il.MarkLabel(generic);
                    EmitHot(property, typeof(KernelProperty));
                    il.Emit(OpCodes.Ldloc, target);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ReadProperty)));
                    EmitResult(value, number, bail);
                    il.MarkLabel(read);
                    Return(targetNumber);
                    Return(target);
                    return;
                }

            case KernelRoot root:
                {
                    var handBack = il.DefineLabel();
                    var done = il.DefineLabel();
                    EmitUnion(root._pure, handBack, value, number);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(handBack);
                    EmitBeforeForeign();
                    EmitConstant(root);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, Method(typeof(KernelRoot), nameof(KernelRoot.Interpret)));
                    il.Emit(OpCodes.Stloc, value);
                    EmitAfterForeign();
                    il.MarkLabel(done);
                    return;
                }

            case KernelAssignSlot or KernelUpdateSlot or KernelCompoundSlot or KernelCompoundEffect or KernelStoreElement or KernelStoreProperty:
                EmitEffect((KernelEffect) expression, value, number);
                return;

            case KernelLogicalEffect logical:
                {
                    var done = il.DefineLabel();
                    EmitUnion(logical._left, null, value, number);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, TruthyOfUnion);
                    il.Emit(logical._and ? OpCodes.Brfalse : OpCodes.Brtrue, done);
                    EmitUnion(logical._right, null, value, number);
                    il.MarkLabel(done);
                    return;
                }

            case KernelNotEffect or KernelComparison:
                EmitBooleanValue(expression, bail, value);
                return;

            case KernelCall call:
                EmitCall(call, value, number);
                return;

            default:
                // KernelInterpreted, KernelCall, or any expression without IL: evaluated as the tree evaluates it.
                EmitBeforeForeign();
                EmitConstant(expression, typeof(KernelExpression));
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.Evaluate)));
                EmitResult(value, number, null);
                EmitAfterForeign();
                if (bail is not null)
                {
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldsfld, BailField);
                    il.Emit(OpCodes.Beq, bail.Value);
                }

                return;
        }
    }

    /// <summary>
    /// Stores the <see cref="KernelResult"/> on the stack into <paramref name="value"/> and <paramref name="number"/>,
    /// then jumps to <paramref name="bail"/>, if there is one, when it is Bail.
    /// </summary>
    private void EmitResult(LocalBuilder value, LocalBuilder number, Label? bail)
    {
        var il = _il;
        var result = Rent(typeof(KernelResult));
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldfld, ResultValue);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldfld, ResultNumber);
        il.Emit(OpCodes.Stloc, number);
        Return(result);
        if (bail is not null)
        {
            il.Emit(OpCodes.Ldloc, value);
            il.Emit(OpCodes.Ldsfld, BailField);
            il.Emit(OpCodes.Beq, bail.Value);
        }
    }

    /// <summary>
    /// The Number of the <see cref="KernelResult"/> on the stack, left on the stack; a result that is not a Number
    /// jumps to <paramref name="bail"/>.
    /// </summary>
    private void EmitNumberResult(Label bail)
    {
        var il = _il;
        var result = Rent(typeof(KernelResult));
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldfld, ResultValue);
        il.Emit(OpCodes.Brtrue, bail);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldfld, ResultNumber);
        Return(result);
    }

    /// <summary>
    /// <see cref="KernelCall.Evaluate"/>: the callee read purely (a hand-back leaves the whole call to the interpreter),
    /// the lane chosen as the node chooses it, then the arguments evaluated in order and the call made through the lane.
    /// </summary>
    private void EmitCall(KernelCall call, LocalBuilder value, LocalBuilder number)
    {
        // A stack too low for the call: the interpreter makes it, on a fresh stack.
        var il = _il;
        var onStack = il.DefineLabel();
        var done = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, Field(typeof(Engine), nameof(Engine._stackGuard)));
        il.Emit(OpCodes.Call, Method(typeof(Runtime.CallStack.StackGuard), nameof(Runtime.CallStack.StackGuard.TryEnterOnCurrentStack)));
        il.Emit(OpCodes.Brtrue, onStack);
        EmitBeforeForeign();
        EmitConstant(call);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.Interpret)));
        il.Emit(OpCodes.Stloc, value);
        EmitAfterForeign();
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(onStack);
        EmitCallOnStack(call, value, number);
        il.MarkLabel(done);
    }

    private void EmitCallOnStack(KernelCall call, LocalBuilder value, LocalBuilder number)
    {
        if (call._member is null
            && call._callee is not null
            && call._registerState is not null
            && call._registerDefinition is { } definition
            && KernelInlineBody.TryCreate(definition, call._arguments.Length) is { } body)
        {
            EmitInlineCall(call, body, value, number);
            return;
        }

        if (call._member is not null
            && call._base is KernelName
            && call._arguments.Length == 1
            && call._fastCallee is { } seen
            && KernelIntrinsics.Identify(seen) is var kind and not KernelIntrinsics.None)
        {
            EmitIntrinsicCall(call, seen, kind, value, number);
            return;
        }

        EmitCallThroughLanes(call, value, number);
    }

    /// <summary>
    /// A call of a Math function the code computes itself (see <see cref="KernelIntrinsics"/>): while the callee read
    /// is that function, which runs no script and cannot throw for a Number, a Number argument gives the result
    /// in place; another argument calls the function through its lane. A callee read that is anything else makes the
    /// call as <see cref="EmitCallThroughLanes"/> makes it, reading the callee again (a read that runs nothing).
    /// The callee read is held with the base it was read from until anything else runs.
    /// </summary>
    private void EmitIntrinsicCall(KernelCall call, Native.Function.Function intrinsic, int kind, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var generic = il.DefineLabel();
        var cached = il.DefineLabel();
        var done = il.DefineLabel();
        var thisValue = Rent(typeof(JsValue));
        var thisNumber = Rent(typeof(double));
        var callee = Rent(typeof(JsValue));
        var siteBase = il.DeclareLocal(typeof(JsValue));
        var siteCallee = il.DeclareLocal(typeof(JsValue));
        _siteCaches.Add(siteBase);

        EmitUnion(call._base!, generic, thisValue, thisNumber);
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Brfalse, generic);
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Ldloc, siteBase);
        il.Emit(OpCodes.Beq, cached);
        EmitHot(call._member!, typeof(KernelProperty));
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ReadProperty)));
        EmitResult(callee, thisNumber, generic);
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Stloc, siteCallee);
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Stloc, siteBase);
        il.MarkLabel(cached);
        il.Emit(OpCodes.Ldloc, siteCallee);
        EmitHot(intrinsic, typeof(JsValue));
        il.Emit(OpCodes.Bne_Un, generic);

        var argument = Rent(typeof(JsValue));
        var argumentNumber = Rent(typeof(double));
        var boxed = il.DefineLabel();
        EmitUnion(call._arguments[0], null, argument, argumentNumber);
        il.Emit(OpCodes.Ldloc, argument);
        il.Emit(OpCodes.Ldloc, argumentNumber);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ToNumber)));
        EmitNumberResult(boxed);
        il.Emit(OpCodes.Call, KernelIntrinsics.Method(kind));
        il.Emit(OpCodes.Stloc, number);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(boxed);
        EmitBeforeForeign();
        EmitConstant(call);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, siteCallee);
        il.Emit(OpCodes.Castclass, typeof(Native.Function.Function));
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Ldloc, argument);
        il.Emit(OpCodes.Ldloc, argumentNumber);
        il.Emit(OpCodes.Call, BoxMethod);
        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallIntrinsic)));
        il.Emit(OpCodes.Stloc, value);
        EmitAfterForeign();
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(generic);
        EmitCallThroughLanes(call, value, number);
        il.MarkLabel(done);
        Return(argumentNumber);
        Return(argument);
        Return(callee);
        Return(thisNumber);
        Return(thisValue);
    }

    private static bool IsComparison(Operator op) => op
        is Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual
        or Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality;

    /// <summary>
    /// A call of a function whose body the code computes in place (<see cref="KernelInlineBody"/>): the callee read, which
    /// must be one <see cref="KernelInlineSite.Accepts"/> (held until anything else runs), the arguments evaluated in
    /// order, then the body computed when every argument is a Number and the body's two statement checks (the body
    /// and its <c>return</c>) would pass without running anything; otherwise the call made through the register lane
    /// with the arguments evaluated. A callee read that is anything else makes the call as
    /// <see cref="EmitCallThroughLanes"/> makes it, reading the callee again (a read that runs nothing).
    /// </summary>
    private void EmitInlineCall(KernelCall call, KernelInlineBody body, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var site = new KernelInlineSite(call._registerDefinition!, body.MathKinds.ToArray());
        var generic = il.DefineLabel();
        var accepted = il.DefineLabel();
        var interpret = il.DefineLabel();
        var done = il.DefineLabel();
        var callee = Rent(typeof(JsValue));
        var calleeNumber = Rent(typeof(double));
        var siteCallee = il.DeclareLocal(typeof(JsValue));
        _siteCaches.Add(siteCallee);

        EmitUnion(call._callee!, generic, callee, calleeNumber);
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Brfalse, generic);
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Ldloc, siteCallee);
        il.Emit(OpCodes.Beq, accepted);
        EmitConstant(site);
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Call, Method(typeof(KernelInlineSite), nameof(KernelInlineSite.Accepts)));
        il.Emit(OpCodes.Brfalse, generic);
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Stloc, siteCallee);
        il.MarkLabel(accepted);

        var arguments = call._arguments;
        var values = new LocalBuilder[arguments.Length];
        var numbers = new LocalBuilder[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i] = Rent(typeof(JsValue));
            numbers[i] = Rent(typeof(double));
            EmitUnion(arguments[i], null, values[i], numbers[i]);
        }

        var parameters = new LocalBuilder[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            parameters[i] = Rent(typeof(double));
            il.Emit(OpCodes.Ldloc, values[i]);
            il.Emit(OpCodes.Ldloc, numbers[i]);
            il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ToNumber)));
            EmitNumberResult(interpret);
            il.Emit(OpCodes.Stloc, parameters[i]);
        }

        EmitInlineChecks(interpret);
        if (body.IsBoolean)
        {
            var isTrue = il.DefineLabel();
            EmitInlineBranch(body, (NonLogicalBinaryExpression) body.Result, parameters, isTrue);
            il.Emit(OpCodes.Ldsfld, Field(typeof(JsBoolean), nameof(JsBoolean.False)));
            il.Emit(OpCodes.Stloc, value);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(isTrue);
            il.Emit(OpCodes.Ldsfld, Field(typeof(JsBoolean), nameof(JsBoolean.True)));
            il.Emit(OpCodes.Stloc, value);
        }
        else
        {
            EmitInlineNumber(body, body.Result, parameters);
            il.Emit(OpCodes.Stloc, number);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc, value);
        }

        il.Emit(OpCodes.Br, done);

        // The call itself, with the arguments evaluated.
        il.MarkLabel(interpret);
        EmitBeforeForeign();
        EmitConstant(call);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Castclass, typeof(Native.Function.ScriptFunction));
        for (var i = 0; i < 4; i++)
        {
            if (i < arguments.Length)
            {
                il.Emit(OpCodes.Ldloc, values[i]);
                il.Emit(OpCodes.Ldloc, numbers[i]);
                il.Emit(OpCodes.Call, BoxMethod);
            }
            else
            {
                il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
            }
        }

        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallInlined)));
        il.Emit(OpCodes.Stloc, value);
        EmitAfterForeign();
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(generic);
        EmitCallThroughLanes(call, value, number);
        il.MarkLabel(done);
        for (var i = arguments.Length - 1; i >= 0; i--)
        {
            Return(parameters[i]);
            Return(numbers[i]);
            Return(values[i]);
        }

        Return(calleeNumber);
        Return(callee);
    }

    /// <summary>
    /// The two statement checks a call of an inlined body runs (the body's and its <c>return</c>'s), taken as the
    /// interpreter would when neither runs a constraint; anything else jumps to <paramref name="call"/>, which makes
    /// the call.
    /// </summary>
    private void EmitInlineChecks(Label call)
    {
        var il = _il;
        var notExact = il.DefineLabel();
        var amortized = il.DefineLabel();
        var commit = il.DefineLabel();
        var done = il.DefineLabel();
        var allowance = Field(typeof(Engine), nameof(Engine._statementsBeforeExactChecks));
        var countdown = Field(typeof(Engine), nameof(Engine._amortizedConstraintCountdown));
        il.Emit(OpCodes.Ldloc, _exact);
        il.Emit(OpCodes.Brfalse, notExact);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, allowance);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, call);
        il.Emit(OpCodes.Br, amortized);
        il.MarkLabel(notExact);
        il.Emit(OpCodes.Ldloc, _counter);
        il.Emit(OpCodes.Brtrue, call);
        il.MarkLabel(amortized);
        il.Emit(OpCodes.Ldloc, _context);
        il.Emit(OpCodes.Call, Getter(typeof(EvaluationContext), nameof(EvaluationContext.RunsAmortizedChecks)));
        il.Emit(OpCodes.Brfalse, commit);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, countdown);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Blt, call);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, countdown);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stfld, countdown);
        il.MarkLabel(commit);
        il.Emit(OpCodes.Ldloc, _exact);
        il.Emit(OpCodes.Brfalse, done);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, allowance);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stfld, allowance);
        il.MarkLabel(done);
    }

    /// <summary>A Number expression of an inlined body, its parameters' Numbers in locals, left on the stack.</summary>
    private void EmitInlineNumber(KernelInlineBody body, Expression expression, LocalBuilder[] parameters)
    {
        var il = _il;
        switch (expression)
        {
            case Literal literal:
                il.Emit(OpCodes.Ldc_R8, ((JsNumber) JintLiteralExpression.ConvertToJsValue(literal)!)._value);
                return;

            case Identifier identifier:
                il.Emit(OpCodes.Ldloc, parameters[Array.IndexOf(body.Parameters, identifier.Name)]);
                return;

            case NonUpdateUnaryExpression unary:
                EmitInlineNumber(body, unary.Argument, parameters);
                if (unary.Operator == Operator.UnaryNegation)
                {
                    il.Emit(OpCodes.Neg);
                }

                return;

            case NonLogicalBinaryExpression binary:
                {
                    var left = Rent(typeof(double));
                    var right = Rent(typeof(double));
                    EmitInlineNumber(body, binary.Left, parameters);
                    il.Emit(OpCodes.Stloc, left);
                    EmitInlineNumber(body, binary.Right, parameters);
                    il.Emit(OpCodes.Stloc, right);
                    EmitArithmetic(binary.Operator, left, right);
                    Return(right);
                    Return(left);
                    return;
                }

            case CallExpression call:
                {
                    var name = ((Identifier) ((MemberExpression) call.Callee).Property).Name;
                    EmitInlineNumber(body, (Expression) call.Arguments[0], parameters);
                    il.Emit(OpCodes.Call, KernelIntrinsics.Method(KernelIntrinsics.Kind(name)));
                    return;
                }

            default:
                throw new NotSupportedException(expression.Type.ToString());
        }
    }

    /// <summary>Jumps to <paramref name="target"/> when the comparison an inlined body returns is true.</summary>
    private void EmitInlineBranch(KernelInlineBody body, NonLogicalBinaryExpression comparison, LocalBuilder[] parameters, Label target)
    {
        var il = _il;
        var left = Rent(typeof(double));
        var right = Rent(typeof(double));
        EmitInlineNumber(body, comparison.Left, parameters);
        il.Emit(OpCodes.Stloc, left);
        EmitInlineNumber(body, comparison.Right, parameters);
        il.Emit(OpCodes.Stloc, right);
        il.Emit(OpCodes.Ldloc, left);
        il.Emit(OpCodes.Ldloc, right);
        il.Emit(CompareBranch(comparison.Operator, when: true), target);
        Return(right);
        Return(left);
    }

    private void EmitCallThroughLanes(KernelCall call, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var interpret = il.DefineLabel();
        var done = il.DefineLabel();
        var thisValue = Rent(typeof(JsValue));
        var callee = Rent(typeof(JsValue));
        var calleeNumber = Rent(typeof(double));
        if (call._member is not null)
        {
            EmitUnion(call._base!, interpret, thisValue, calleeNumber);
            il.Emit(OpCodes.Ldloc, thisValue);
            il.Emit(OpCodes.Brfalse, interpret);
            EmitHot(call._member, typeof(KernelProperty));
            il.Emit(OpCodes.Ldloc, thisValue);
            il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ReadCallee)));
            il.Emit(OpCodes.Stloc, callee);
        }
        else
        {
            il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
            il.Emit(OpCodes.Stloc, thisValue);
            EmitUnion(call._callee!, interpret, callee, calleeNumber);
        }

        var shape = il.DeclareLocal(typeof(Native.Function.FastCallShape));
        var state = il.DeclareLocal(typeof(JintFunctionDefinition.State));
        var kind = Rent(typeof(int));
        EmitConstant(call);
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Ldloca, shape);
        il.Emit(OpCodes.Ldloca, state);
        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.Prepare)));
        il.Emit(OpCodes.Stloc, kind);
        il.Emit(OpCodes.Ldloc, kind);
        il.Emit(OpCodes.Brfalse, interpret);

        // The lanes the tree has called through; a call Prepare sends through any other is left to the interpreter,
        // as it is decided before any argument runs.
        var arguments = call._arguments;
        var builtinSeen = call._fastCallee is not null && arguments.Length <= 2;
        var registersSeen = call._registerState is not null && arguments.Length <= 4;
        var listSeen = call._registerDefinition is not null && (call._registerState is null || arguments.Length > 4);
        for (var k = 1; k <= 3; k++)
        {
            if (!(k == 1 ? builtinSeen : k == 2 ? registersSeen : listSeen))
            {
                il.Emit(OpCodes.Ldloc, kind);
                il.Emit(OpCodes.Ldc_I4, k);
                il.Emit(OpCodes.Beq, interpret);
            }
        }

        // Each argument a value or, where that is null, a Number: the register lane takes Numbers unboxed.
        var values = new LocalBuilder[arguments.Length];
        var numbers = new LocalBuilder[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i] = Rent(typeof(JsValue));
            numbers[i] = Rent(typeof(double));
            EmitUnion(arguments[i], null, values[i], numbers[i]);
        }

        void LoadArgument(int i)
        {
            if (i < values.Length)
            {
                il.Emit(OpCodes.Ldloc, values[i]);
                il.Emit(OpCodes.Ldloc, numbers[i]);
                il.Emit(OpCodes.Call, BoxMethod);
            }
            else
            {
                il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
            }
        }

        void LoadRegister(int i)
        {
            if (i < values.Length)
            {
                il.Emit(OpCodes.Ldloc, values[i]);
                il.Emit(OpCodes.Ldloc, numbers[i]);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldc_R8, 0.0);
            }
        }

        var afterCall = il.DefineLabel();
        if (builtinSeen)
        {
            var framed = il.DefineLabel();
            var otherKind = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, kind);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Bne_Un, otherKind);

            // The arguments boxed once for this lane.
            for (var i = 0; i < values.Length; i++)
            {
                LoadArgument(i);
                il.Emit(OpCodes.Stloc, values[i]);
                il.Emit(OpCodes.Ldc_R8, 0.0);
                il.Emit(OpCodes.Stloc, numbers[i]);
            }

            // A leaf runs no script: the locals stay. They are written back all the same, since it may throw.
            il.Emit(OpCodes.Ldloc, shape);
            il.Emit(OpCodes.Ldloc, thisValue);
            LoadArgument(0);
            LoadArgument(1);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.IsLeaf)));
            il.Emit(OpCodes.Brfalse, framed);
            EmitBeforeForeign();
            il.Emit(OpCodes.Ldloc, callee);
            il.Emit(OpCodes.Castclass, typeof(Native.Function.Function));
            il.Emit(OpCodes.Ldloc, thisValue);
            LoadArgument(0);
            LoadArgument(1);
            il.Emit(OpCodes.Callvirt, Method(typeof(Native.Function.Function), "CallFast"));
            il.Emit(OpCodes.Stloc, value);
            il.Emit(OpCodes.Br, afterCall);
            il.MarkLabel(framed);
            EmitBeforeForeign();
            EmitConstant(call);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, callee);
            il.Emit(OpCodes.Castclass, typeof(Native.Function.Function));
            il.Emit(OpCodes.Ldloc, thisValue);
            LoadArgument(0);
            LoadArgument(1);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallWithFrame)));
            il.Emit(OpCodes.Stloc, value);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(otherKind);
        }

        if (registersSeen)
        {
            var otherKind = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, kind);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Bne_Un, otherKind);
            EmitBeforeForeign();
            EmitConstant(call);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, callee);
            il.Emit(OpCodes.Castclass, typeof(Native.Function.ScriptFunction));
            il.Emit(OpCodes.Ldloc, thisValue);
            il.Emit(OpCodes.Ldloc, state);
            LoadRegister(0);
            LoadRegister(1);
            LoadRegister(2);
            LoadRegister(3);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallRegisterLane)));
            EmitResult(value, number, null);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(otherKind);
        }

        if (listSeen)
        {
            EmitBeforeForeign();
            var list = il.DeclareLocal(typeof(JsValue[]));
            EmitConstant(call);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.RentArguments)));
            il.Emit(OpCodes.Stloc, list);
            for (var i = 0; i < values.Length; i++)
            {
                il.Emit(OpCodes.Ldloc, list);
                il.Emit(OpCodes.Ldc_I4, i);
                LoadArgument(i);
                il.Emit(OpCodes.Stelem_Ref);
            }

            EmitConstant(call);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, callee);
            il.Emit(OpCodes.Castclass, typeof(Native.Function.ScriptFunction));
            il.Emit(OpCodes.Ldloc, thisValue);
            il.Emit(OpCodes.Ldloc, list);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallWithArguments)));
            il.Emit(OpCodes.Stloc, value);
            il.Emit(OpCodes.Br, done);
        }
        else
        {
            // Unreachable: every kind not compiled went to the interpreter.
            il.Emit(OpCodes.Br, interpret);
        }

        il.MarkLabel(interpret);
        EmitBeforeForeign();
        EmitConstant(call);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.Interpret)));
        il.Emit(OpCodes.Stloc, value);
        il.MarkLabel(done);
        EmitAfterForeign();
        il.MarkLabel(afterCall);

        for (var i = values.Length - 1; i >= 0; i--)
        {
            Return(numbers[i]);
            Return(values[i]);
        }

        Return(kind);
        Return(calleeNumber);
        Return(callee);
        Return(thisValue);
    }

    /// <summary>Stores the value on the stack, then jumps to <paramref name="bail"/> when it is Bail.</summary>
    private void EmitStoreCheckingBail(LocalBuilder value, Label bail)
    {
        var il = _il;
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Ldsfld, BailField);
        il.Emit(OpCodes.Beq, bail);
    }

    /// <summary>A boolean-valued expression as the JsBoolean its Evaluate answers.</summary>
    private void EmitBooleanValue(KernelExpression expression, Label? bail, LocalBuilder value)
    {
        var il = _il;
        var isTrue = il.DefineLabel();
        var done = il.DefineLabel();
        EmitBranch(expression, bail, isTrue, when: true);
        il.Emit(OpCodes.Ldsfld, Field(typeof(JsBoolean), nameof(JsBoolean.False)));
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(isTrue);
        il.Emit(OpCodes.Ldsfld, Field(typeof(JsBoolean), nameof(JsBoolean.True)));
        il.Emit(OpCodes.Stloc, value);
        il.MarkLabel(done);
    }

    /// <summary>A name's value: from its locals while they hold it, otherwise read and, where it may be, held.</summary>
    private void EmitNameUnion(KernelName name, Label bail, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var locals = Name(name);
        var notNumber = il.DefineLabel();
        var cold = il.DefineLabel();
        var done = il.DefineLabel();
        EmitHeld(locals, NameLocals.NumberHeld);
        il.Emit(OpCodes.Brfalse, notNumber);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloc, locals.Number);
        il.Emit(OpCodes.Stloc, number);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(notNumber);
        EmitHeld(locals, NameLocals.ValueHeld);
        il.Emit(OpCodes.Brfalse, cold);
        il.Emit(OpCodes.Ldloc, locals.Value);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(cold);
        EmitNameCold(name, locals, value, number);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Ldsfld, BailField);
        il.Emit(OpCodes.Beq, bail);
        il.MarkLabel(done);
    }

    /// <summary>
    /// <see cref="KernelCodeRuntime.ReadNameCold"/> into <paramref name="value"/> and <paramref name="number"/>, held
    /// in the name's locals where the read says it may be.
    /// </summary>
    private void EmitNameCold(KernelName name, NameLocals locals, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var read = Rent(typeof(KernelNameRead));
        var held = il.DefineLabel();
        var heldValue = il.DefineLabel();
        var done = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, locals.Ref);
        EmitHot(name, typeof(KernelName));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, _epoch);

        // A function body reads most names once per call, a loop once per run.
        var reader = _localSlots is not null ? nameof(KernelCodeRuntime.ReadNameFirst) : nameof(KernelCodeRuntime.ReadNameCold);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), reader));
        il.Emit(OpCodes.Stloc, read);
        il.Emit(OpCodes.Ldloc, read);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelNameRead), nameof(KernelNameRead.Result)));
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloc, read);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelNameRead), nameof(KernelNameRead.Number)));
        il.Emit(OpCodes.Stloc, number);
        il.Emit(OpCodes.Ldloc, read);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelNameRead), nameof(KernelNameRead.Kind)));
        il.Emit(OpCodes.Switch, new[] { done, held, heldValue });
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(held);
        il.Emit(OpCodes.Ldloc, number);
        il.Emit(OpCodes.Stloc, locals.Number);
        il.Emit(OpCodes.Ldc_I4, NameLocals.NumberHeld);
        il.Emit(OpCodes.Stloc, locals.State);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(heldValue);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Stloc, locals.Value);
        il.Emit(OpCodes.Ldc_I4, NameLocals.ValueHeld);
        il.Emit(OpCodes.Stloc, locals.State);
        il.MarkLabel(done);
        Return(read);
    }

    /// <summary>Jumps to <paramref name="slow"/> unless the name's slot reference holds: a slot, and no binding injected since entry.</summary>
    private void EmitSlotGuard(NameLocals locals, Label slow)
    {
        var il = _il;
        il.Emit(OpCodes.Ldloc, locals.Ref);
        il.Emit(OpCodes.Call, IsNullRef);
        il.Emit(OpCodes.Brtrue, slow);
        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, Field(typeof(Engine), nameof(Engine._envBindingInjectionEpoch)));
        il.Emit(OpCodes.Ldloc, _epoch);
        il.Emit(OpCodes.Bne_Un, slow);
    }

    /// <summary>The Number of a pure expression, left on the stack; anything else jumps to <paramref name="bail"/>.</summary>
    private void EmitNumber(KernelExpression expression, Label bail)
    {
        var il = _il;
        switch (expression)
        {
            case KernelNumber literal:
                il.Emit(OpCodes.Ldc_R8, literal._value);
                return;

            case KernelConstant { _value: JsNumber constantNumber }:
                il.Emit(OpCodes.Ldc_R8, constantNumber._value);
                return;

            case KernelConstant:
            case KernelBinary { _isComparison: true }:
            case KernelUnary { _operator: Operator.LogicalNot }:
                // Never a Number: the tree's operand test answers Bail.
                il.Emit(OpCodes.Br, bail);
                il.Emit(OpCodes.Ldc_R8, 0.0);
                return;

            case KernelName name:
                {
                    var locals = Name(name);
                    var notNumber = il.DefineLabel();
                    var cold = il.DefineLabel();
                    var convert = il.DefineLabel();
                    var done = il.DefineLabel();
                    var result = Rent(typeof(double));
                    var value = Rent(typeof(JsValue));
                    var number = Rent(typeof(double));
                    EmitHeld(locals, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brfalse, notNumber);
                    il.Emit(OpCodes.Ldloc, locals.Number);
                    il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(notNumber);
                    EmitHeld(locals, NameLocals.ValueHeld);
                    il.Emit(OpCodes.Brfalse, cold);
                    il.Emit(OpCodes.Ldloc, locals.Value);
                    il.Emit(OpCodes.Stloc, value);
                    il.Emit(OpCodes.Br, convert);
                    il.MarkLabel(cold);
                    EmitNameCold(name, locals, value, number);
                    il.MarkLabel(convert);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ToNumber)));
                    EmitNumberResult(bail);
                    il.Emit(OpCodes.Stloc, result);
                    il.MarkLabel(done);
                    il.Emit(OpCodes.Ldloc, result);
                    Return(number);
                    Return(value);
                    Return(result);
                    return;
                }

            case KernelBinary binary:
                {
                    var left = Rent(typeof(double));
                    var right = Rent(typeof(double));
                    EmitNumber(binary._left, bail);
                    il.Emit(OpCodes.Stloc, left);
                    EmitNumber(binary._right, bail);
                    il.Emit(OpCodes.Stloc, right);
                    EmitArithmetic(binary._operator, left, right);
                    Return(right);
                    Return(left);
                    return;
                }

            case KernelUnary unary:
                EmitNumber(unary._argument, bail);
                switch (unary._operator)
                {
                    case Operator.UnaryNegation:
                        il.Emit(OpCodes.Neg);
                        break;
                    case Operator.UnaryPlus:
                        break;
                    default:
                        il.Emit(OpCodes.Call, ToInt32);
                        il.Emit(OpCodes.Not);
                        il.Emit(OpCodes.Conv_R8);
                        break;
                }

                return;

            case KernelConditional conditional:
                {
                    var otherwise = il.DefineLabel();
                    var done = il.DefineLabel();
                    var result = Rent(typeof(double));
                    EmitBranch(conditional._test, bail, otherwise, when: false);
                    EmitNumber(conditional._consequent, bail);
                    il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(otherwise);
                    EmitNumber(conditional._alternate, bail);
                    il.Emit(OpCodes.Stloc, result);
                    il.MarkLabel(done);
                    il.Emit(OpCodes.Ldloc, result);
                    Return(result);
                    return;
                }

            default:
                {
                    // An element, a property, a logical operator: the value, which must be a Number.
                    var value = Rent(typeof(JsValue));
                    var result = Rent(typeof(double));
                    EmitUnion(expression, bail, value, result);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, result);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ToNumber)));
                    EmitNumberResult(bail);
                    Return(result);
                    Return(value);
                    return;
                }
        }
    }

    /// <summary><see cref="KernelBinary"/>'s Apply of two Numbers in locals, left on the stack.</summary>
    private void EmitArithmetic(Operator op, LocalBuilder left, LocalBuilder right)
    {
        var il = _il;
        switch (op)
        {
            case Operator.Addition:
            case Operator.Subtraction:
            case Operator.Multiplication:
            case Operator.Division:
                il.Emit(OpCodes.Ldloc, left);
                il.Emit(OpCodes.Ldloc, right);
                il.Emit(op switch
                {
                    Operator.Addition => OpCodes.Add,
                    Operator.Subtraction => OpCodes.Sub,
                    Operator.Multiplication => OpCodes.Mul,
                    _ => OpCodes.Div,
                });
                return;

            case Operator.Remainder:
                il.Emit(OpCodes.Ldloc, left);
                il.Emit(OpCodes.Ldloc, right);
                il.Emit(OpCodes.Call, Method(typeof(JintExpression), nameof(JintExpression.RemainderUnboxed)));
                return;

            case Operator.BitwiseAnd:
            case Operator.BitwiseOr:
            case Operator.BitwiseXor:
                il.Emit(OpCodes.Ldloc, left);
                il.Emit(OpCodes.Call, ToInt32);
                il.Emit(OpCodes.Ldloc, right);
                il.Emit(OpCodes.Call, ToInt32);
                il.Emit(op switch
                {
                    Operator.BitwiseAnd => OpCodes.And,
                    Operator.BitwiseOr => OpCodes.Or,
                    _ => OpCodes.Xor,
                });
                il.Emit(OpCodes.Conv_R8);
                return;

            case Operator.LeftShift:
            case Operator.RightShift:
                il.Emit(OpCodes.Ldloc, left);
                il.Emit(OpCodes.Call, ToInt32);
                il.Emit(OpCodes.Ldloc, right);
                il.Emit(OpCodes.Call, ToUint32);
                il.Emit(OpCodes.Ldc_I4, 0x1F);
                il.Emit(OpCodes.And);
                il.Emit(op == Operator.LeftShift ? OpCodes.Shl : OpCodes.Shr);
                il.Emit(OpCodes.Conv_R8);
                return;

            case Operator.UnsignedRightShift:
                il.Emit(OpCodes.Ldloc, left);
                il.Emit(OpCodes.Call, ToUint32);
                il.Emit(OpCodes.Ldloc, right);
                il.Emit(OpCodes.Call, ToUint32);
                il.Emit(OpCodes.Ldc_I4, 0x1F);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Shr_Un);
                il.Emit(OpCodes.Conv_R_Un);
                il.Emit(OpCodes.Conv_R8);
                return;

            default:
                throw new NotSupportedException(op.ToString());
        }
    }

    /// <summary>
    /// Jumps to <paramref name="target"/> when ToBoolean of <paramref name="expression"/> is <paramref name="when"/>,
    /// and falls through otherwise; a pure expression jumps to <paramref name="bail"/> where the tree's Test answers -1.
    /// </summary>
    private void EmitBranch(KernelExpression expression, Label? bail, Label target, bool when)
    {
        var il = _il;
        Debug.Assert(bail is not null || !IsPure(expression));
        switch (expression)
        {
            case KernelBinary { _isComparison: true } comparison:
                {
                    var left = Rent(typeof(double));
                    var right = Rent(typeof(double));
                    EmitNumber(comparison._left, bail!.Value);
                    il.Emit(OpCodes.Stloc, left);
                    EmitNumber(comparison._right, bail.Value);
                    il.Emit(OpCodes.Stloc, right);
                    il.Emit(OpCodes.Ldloc, left);
                    il.Emit(OpCodes.Ldloc, right);
                    il.Emit(CompareBranch(comparison._operator, when), target);
                    Return(right);
                    Return(left);
                    return;
                }

            case KernelNumber or KernelBinary or KernelUnary { _operator: not Operator.LogicalNot }:
                {
                    var number = Rent(typeof(double));
                    EmitNumber(expression, bail!.Value);
                    il.Emit(OpCodes.Stloc, number);
                    EmitNumberBranch(number, target, when);
                    Return(number);
                    return;
                }

            case KernelUnary not:
                EmitBranch(not._argument, bail, target, !when);
                return;

            case KernelLogical logical:
                EmitLogicalBranch(logical._and, logical._left, logical._right, bail, target, when);
                return;

            case KernelLogicalEffect logical:
                EmitLogicalBranch(logical._and, logical._left, logical._right, null, target, when);
                return;

            case KernelNotEffect not:
                EmitBranch(not._argument, null, target, !when);
                return;

            case KernelConditional conditional:
                {
                    var otherwise = il.DefineLabel();
                    var done = il.DefineLabel();
                    EmitBranch(conditional._test, bail, otherwise, when: false);
                    EmitBranch(conditional._consequent, bail, target, when);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(otherwise);
                    EmitBranch(conditional._alternate, bail, target, when);
                    il.MarkLabel(done);
                    return;
                }

            case KernelRoot root:
                {
                    var handBack = il.DefineLabel();
                    var done = il.DefineLabel();
                    EmitBranch(root._pure, handBack, target, when);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(handBack);
                    EmitBeforeForeign();
                    EmitConstant(root);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, Method(typeof(KernelRoot), nameof(KernelRoot.InterpretTest)));
                    var test = Rent(typeof(bool));
                    il.Emit(OpCodes.Stloc, test);
                    EmitAfterForeign();
                    il.Emit(OpCodes.Ldloc, test);
                    Return(test);
                    il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                    il.MarkLabel(done);
                    return;
                }

            case KernelComparison comparison:
                EmitComparisonBranch(comparison, target, when);
                return;

            case KernelInterpreted:
                {
                    EmitBeforeForeign();
                    EmitConstant(expression);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, Method(typeof(KernelExpression), nameof(KernelExpression.Test)));
                    var test = Rent(typeof(int));
                    il.Emit(OpCodes.Stloc, test);
                    EmitAfterForeign();
                    il.Emit(OpCodes.Ldloc, test);
                    Return(test);
                    il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                    return;
                }

            default:
                {
                    // A name, an element, a property, a constant, a call, an effect: ToBoolean of the value.
                    var value = Rent(typeof(JsValue));
                    var number = Rent(typeof(double));
                    EmitUnion(expression, bail, value, number);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, TruthyOfUnion);
                    il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                    Return(number);
                    Return(value);
                    return;
                }
        }
    }

    private void EmitLogicalBranch(bool and, KernelExpression left, KernelExpression right, Label? bail, Label target, bool when)
    {
        var il = _il;
        if (and != when)
        {
            // a && b is false when either is; a || b is true when either is.
            EmitBranch(left, bail, target, when);
            EmitBranch(right, bail, target, when);
            return;
        }

        // a && b is true when both are; a || b is false when both are.
        var skip = il.DefineLabel();
        EmitBranch(left, bail, skip, !when);
        EmitBranch(right, bail, target, when);
        il.MarkLabel(skip);
    }

    /// <summary>ToBoolean of a Number in a local: true unless it is +0, -0 or NaN.</summary>
    private void EmitNumberBranch(LocalBuilder number, Label target, bool when)
    {
        var il = _il;
        il.Emit(OpCodes.Ldloc, number);
        il.Emit(OpCodes.Call, TruthyOfNumber);
        il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
    }

    private static OpCode CompareBranch(Operator op, bool when)
    {
        // A comparison with NaN is false: the branches taken when it is false are the unordered ones.
        if (when)
        {
            return op switch
            {
                Operator.LessThan => OpCodes.Blt,
                Operator.LessThanOrEqual => OpCodes.Ble,
                Operator.GreaterThan => OpCodes.Bgt,
                Operator.GreaterThanOrEqual => OpCodes.Bge,
                Operator.Equality or Operator.StrictEquality => OpCodes.Beq,
                _ => OpCodes.Bne_Un,
            };
        }

        return op switch
        {
            Operator.LessThan => OpCodes.Bge_Un,
            Operator.LessThanOrEqual => OpCodes.Bgt_Un,
            Operator.GreaterThan => OpCodes.Ble_Un,
            Operator.GreaterThanOrEqual => OpCodes.Blt_Un,
            Operator.Equality or Operator.StrictEquality => OpCodes.Bne_Un,
            _ => OpCodes.Beq,
        };
    }

    /// <summary><see cref="KernelComparison.Test"/>: both operands once, in order; two Numbers compared directly.</summary>
    private void EmitComparisonBranch(KernelComparison comparison, Label target, bool when)
    {
        var il = _il;
        var leftValue = Rent(typeof(JsValue));
        var left = Rent(typeof(double));
        var rightValue = Rent(typeof(JsValue));
        var right = Rent(typeof(double));
        EmitUnion(comparison._left, null, leftValue, left);
        EmitUnion(comparison._right, null, rightValue, right);
        var slow = il.DefineLabel();
        var done = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, leftValue);
        il.Emit(OpCodes.Ldloc, left);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ToNumber)));
        EmitNumberResult(slow);
        il.Emit(OpCodes.Stloc, left);
        il.Emit(OpCodes.Ldloc, rightValue);
        il.Emit(OpCodes.Ldloc, right);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ToNumber)));
        EmitNumberResult(slow);
        il.Emit(OpCodes.Stloc, right);
        il.Emit(OpCodes.Ldloc, left);
        il.Emit(OpCodes.Ldloc, right);
        il.Emit(CompareBranch(comparison._operator, when), target);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(slow);
        EmitBeforeForeign();
        EmitConstant(comparison);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, leftValue);
        il.Emit(OpCodes.Ldloc, left);
        il.Emit(OpCodes.Call, BoxMethod);
        il.Emit(OpCodes.Ldloc, rightValue);
        il.Emit(OpCodes.Ldloc, right);
        il.Emit(OpCodes.Call, BoxMethod);
        il.Emit(OpCodes.Call, Method(typeof(KernelComparison), nameof(KernelComparison.CompareValues)));
        var compared = Rent(typeof(bool));
        il.Emit(OpCodes.Stloc, compared);
        EmitAfterForeign();
        il.Emit(OpCodes.Ldloc, compared);
        Return(compared);
        il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
        il.MarkLabel(done);
        Return(right);
        Return(rightValue);
        Return(left);
        Return(leftValue);
    }

    /// <summary>The value of a non-pure expression, boxed, left on the stack.</summary>
    private void EmitBoxed(KernelExpression expression)
    {
        var il = _il;
        var value = Rent(typeof(JsValue));
        var number = Rent(typeof(double));
        EmitUnion(expression, null, value, number);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Ldloc, number);
        il.Emit(OpCodes.Call, BoxMethod);
        Return(number);
        Return(value);
    }

    /// <summary>An expression evaluated for its effects, as the tree's <c>EvaluateDiscarded</c>.</summary>
    private void EmitDiscard(KernelExpression expression)
    {
        var il = _il;
        switch (expression)
        {
            case KernelAssignSlot or KernelUpdateSlot or KernelCompoundSlot or KernelCompoundEffect or KernelStoreElement or KernelStoreProperty:
                EmitEffect((KernelEffect) expression, null, null);
                return;

            case KernelInterpreted:
                EmitBeforeForeign();
                EmitConstant(expression);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, Method(typeof(KernelExpression), nameof(KernelExpression.EvaluateDiscarded)));
                EmitAfterForeign();
                return;

            default:
                {
                    var value = Rent(typeof(JsValue));
                    var number = Rent(typeof(double));
                    EmitUnion(expression, null, value, number);
                    Return(number);
                    Return(value);
                    return;
                }
        }
    }

    /// <summary>
    /// An assignment, update or element store; its value into <paramref name="value"/> and <paramref name="number"/>,
    /// or discarded when they are null. A case the tree's node leaves to the interpreter's handler is left to it here,
    /// at the same point.
    /// </summary>
    private void EmitEffect(KernelEffect effect, LocalBuilder? value, LocalBuilder? number)
    {
        var il = _il;
        var interpret = il.DefineLabel();
        var done = il.DefineLabel();
        var resultValue = value ?? Rent(typeof(JsValue));
        var resultNumber = number ?? Rent(typeof(double));

        switch (effect)
        {
            case KernelAssignSlot assign:
                {
                    // The binding, found before the right-hand side runs: the slot of a name held as a Number (a
                    // mutable binding), or the one the tree's lookup finds, which must be assignable. A Number is then
                    // stored to the local while the name is held, anything else to the binding found.
                    var target = Name(assign._target);
                    var binding = il.DeclareLocal(typeof(Binding).MakeByRefType());
                    var environment = Rent(typeof(DeclarativeEnvironment));
                    var unheld = il.DefineLabel();
                    var found = il.DefineLabel();
                    var throughSlot = il.DefineLabel();
                    EmitHeld(target, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brfalse, unheld);
                    il.Emit(OpCodes.Ldloc, target.Ref);
                    il.Emit(OpCodes.Stloc, binding);
                    il.Emit(OpCodes.Ldloc, target.Environment);
                    il.Emit(OpCodes.Stloc, environment);
                    il.Emit(OpCodes.Br, found);
                    il.MarkLabel(unheld);
                    EmitFind(assign._target, binding, environment);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Call, IsNullRef);
                    il.Emit(OpCodes.Brtrue, interpret);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.IsAssignable)));
                    il.Emit(OpCodes.Brfalse, interpret);
                    il.MarkLabel(found);
                    EmitUnion(assign._value, null, resultValue, resultNumber);

                    // Held after the right-hand side only if nothing else ran, or it read the name back through the
                    // same slot.
                    EmitHeld(target, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brfalse, throughSlot);
                    il.Emit(OpCodes.Ldloc, resultValue);
                    il.Emit(OpCodes.Brtrue, throughSlot);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Stloc, target.Number);
                    target.Stored = true;
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(throughSlot);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Ldloc, environment);
                    EmitConstant(assign);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloc, resultValue);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.StoreAssigned)));
                    EmitReload(assign._target, target);
                    il.Emit(OpCodes.Br, done);

                    il.MarkLabel(interpret);
                    EmitBeforeForeign();
                    EmitConstant(assign);
                    il.Emit(OpCodes.Ldarg_1);
                    if (assign._isDeclarator)
                    {
                        il.Emit(OpCodes.Call, Method(typeof(KernelAssignSlot), nameof(KernelAssignSlot.InterpretDeclarator)));
                        il.Emit(OpCodes.Stloc, resultValue);
                    }
                    else if (value is null)
                    {
                        il.Emit(OpCodes.Call, Method(typeof(KernelEffect), nameof(KernelEffect.InterpretDiscarded)));
                    }
                    else
                    {
                        il.Emit(OpCodes.Call, Method(typeof(KernelEffect), nameof(KernelEffect.Interpret)));
                        il.Emit(OpCodes.Stloc, resultValue);
                    }

                    EmitAfterForeign();
                    Return(environment);
                    break;
                }

            case KernelUpdateSlot update:
                {
                    var target = Name(update._target);
                    var unheld = il.DefineLabel();
                    var old = Rent(typeof(double));
                    EmitHeld(target, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brfalse, unheld);
                    il.Emit(OpCodes.Ldloc, target.Number);
                    il.Emit(OpCodes.Stloc, old);
                    il.Emit(OpCodes.Ldloc, old);
                    il.Emit(OpCodes.Ldc_R8, update._change);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stloc, target.Number);
                    target.Stored = true;
                    il.Emit(OpCodes.Ldloc, update._prefix ? target.Number : old);
                    il.Emit(OpCodes.Stloc, resultNumber);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Stloc, resultValue);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(unheld);
                    Return(old);

                    var binding = il.DeclareLocal(typeof(Binding).MakeByRefType());
                    var environment = Rent(typeof(DeclarativeEnvironment));
                    EmitFind(update._target, binding, environment);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Call, IsNullRef);
                    il.Emit(OpCodes.Brtrue, interpret);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Ldc_R8, update._change);
                    il.Emit(update._prefix ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.TryUpdate)));
                    EmitNumberResult(interpret);
                    il.Emit(OpCodes.Stloc, resultNumber);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Stloc, resultValue);
                    EmitReload(update._target, target);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(interpret);
                    EmitInterpretEffect(effect, value is not null, resultValue);
                    Return(environment);
                    break;
                }

            case KernelCompoundSlot compound:
                {
                    // A name held as a Number, or a slot holding a mutable Number; the operation reads it through its
                    // own left operand, and hands back to the interpreter for anything but two Numbers.
                    var target = Name(compound._target);
                    var binding = il.DeclareLocal(typeof(Binding).MakeByRefType());
                    var environment = Rent(typeof(DeclarativeEnvironment));
                    var ready = il.DefineLabel();
                    var toSlot = il.DefineLabel();
                    EmitHeld(target, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brtrue, ready);
                    EmitFind(compound._target, binding, environment);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Call, IsNullRef);
                    il.Emit(OpCodes.Brtrue, interpret);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.IsMutableNumber)));
                    il.Emit(OpCodes.Brfalse, interpret);
                    il.MarkLabel(ready);
                    EmitNumber(compound._operation, interpret);
                    il.Emit(OpCodes.Stloc, resultNumber);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Stloc, resultValue);

                    // A pure operation leaves a held name held, and holds one it read through the same slot.
                    EmitHeld(target, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brfalse, toSlot);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Stloc, target.Number);
                    target.Stored = true;
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(toSlot);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.StoreMutableNumber)));
                    EmitReload(compound._target, target);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(interpret);
                    EmitInterpretEffect(effect, value is not null, resultValue);
                    Return(environment);
                    break;
                }

            case KernelCompoundEffect compound:
                {
                    // The binding's Number is read before the right-hand side runs; the result is stored as an assignment stores it.
                    var target = Name(compound._target);
                    var binding = il.DeclareLocal(typeof(Binding).MakeByRefType());
                    var environment = Rent(typeof(DeclarativeEnvironment));
                    var left = Rent(typeof(double));
                    var unheld = il.DefineLabel();
                    var found = il.DefineLabel();
                    var generic = il.DefineLabel();
                    var combined = il.DefineLabel();
                    var throughSlot = il.DefineLabel();
                    EmitHeld(target, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brfalse, unheld);
                    il.Emit(OpCodes.Ldloc, target.Number);
                    il.Emit(OpCodes.Stloc, left);
                    il.Emit(OpCodes.Ldloc, target.Ref);
                    il.Emit(OpCodes.Stloc, binding);
                    il.Emit(OpCodes.Ldloc, target.Environment);
                    il.Emit(OpCodes.Stloc, environment);
                    il.Emit(OpCodes.Br, found);
                    il.MarkLabel(unheld);
                    EmitFind(compound._target, binding, environment);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Call, IsNullRef);
                    il.Emit(OpCodes.Brtrue, interpret);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Ldloca, left);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.TryMutableNumber)));
                    il.Emit(OpCodes.Brfalse, interpret);
                    il.MarkLabel(found);
                    EmitUnion(compound._value, null, resultValue, resultNumber);
                    il.Emit(OpCodes.Ldloc, resultValue);
                    il.Emit(OpCodes.Ldloca, resultNumber);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.IsNumber)));
                    il.Emit(OpCodes.Brfalse, generic);
                    il.Emit(OpCodes.Ldc_I4, (int) compound._operator);
                    il.Emit(OpCodes.Ldloc, left);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Call, Method(typeof(KernelBinary), nameof(KernelBinary.Apply)));
                    il.Emit(OpCodes.Stloc, resultNumber);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Stloc, resultValue);
                    il.Emit(OpCodes.Br, combined);
                    il.MarkLabel(generic);
                    EmitBeforeForeign();
                    EmitConstant(compound);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloc, left);
                    il.Emit(OpCodes.Ldloc, resultValue);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCompoundEffect), nameof(KernelCompoundEffect.ApplyValues)));
                    il.Emit(OpCodes.Stloc, resultValue);
                    EmitAfterForeign();
                    il.MarkLabel(combined);

                    // Held after the right-hand side only if nothing else ran.
                    EmitHeld(target, NameLocals.NumberHeld);
                    il.Emit(OpCodes.Brfalse, throughSlot);
                    il.Emit(OpCodes.Ldloc, resultValue);
                    il.Emit(OpCodes.Brtrue, throughSlot);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Stloc, target.Number);
                    target.Stored = true;
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(throughSlot);
                    il.Emit(OpCodes.Ldloc, binding);
                    il.Emit(OpCodes.Ldloc, environment);
                    EmitConstant(compound);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloc, resultValue);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.StoreCompound)));
                    EmitReload(compound._target, target);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(interpret);
                    EmitInterpretEffect(effect, value is not null, resultValue);
                    Return(left);
                    Return(environment);
                    break;
                }

            case KernelStoreElement store:
                {
                    var target = Rent(typeof(JsValue));
                    var targetNumber = Rent(typeof(double));
                    var key = Rent(typeof(double));
                    EmitUnion(store._object, interpret, target, targetNumber);
                    il.Emit(OpCodes.Ldloc, target);
                    il.Emit(OpCodes.Isinst, typeof(JsTypedArray));
                    il.Emit(OpCodes.Brfalse, interpret);
                    EmitNumber(store._key, interpret);
                    il.Emit(OpCodes.Stloc, key);
                    EmitNumber(store._value, interpret);
                    il.Emit(OpCodes.Stloc, resultNumber);
                    var generic = il.DefineLabel();
                    if (store._object is KernelName held)
                    {
                        // The elements of a Uint8Array the name holds, written in place.
                        var index = Rent(typeof(int));
                        EmitTypedIndex(Name(held), target, key, index, generic);
                        var locals = Name(held);
                        il.Emit(OpCodes.Ldloc, locals.TypedData);
                        il.Emit(OpCodes.Ldloc, locals.TypedOffset);
                        il.Emit(OpCodes.Ldloc, index);
                        il.Emit(OpCodes.Add);
                        il.Emit(OpCodes.Ldloc, resultNumber);
                        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ToUint8)));
                        il.Emit(OpCodes.Stelem_I1);
                        il.Emit(OpCodes.Ldnull);
                        il.Emit(OpCodes.Stloc, resultValue);
                        il.Emit(OpCodes.Br, done);
                        Return(index);
                    }

                    il.MarkLabel(generic);
                    il.Emit(OpCodes.Ldloc, target);
                    il.Emit(OpCodes.Ldloc, key);
                    il.Emit(OpCodes.Ldloc, resultNumber);
                    il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.StoreElement)));
                    il.Emit(OpCodes.Brfalse, interpret);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Stloc, resultValue);
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(interpret);
                    EmitInterpretEffect(effect, value is not null, resultValue);
                    Return(key);
                    Return(targetNumber);
                    Return(target);
                    break;
                }

            case KernelStoreProperty store:
                {
                    var target = Rent(typeof(JsValue));
                    var targetNumber = Rent(typeof(double));
                    EmitUnion(store._object, interpret, target, targetNumber);
                    EmitBoxed(store._value);
                    il.Emit(OpCodes.Stloc, resultValue);
                    EmitBeforeForeign();
                    EmitConstant(store);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloc, target);
                    il.Emit(OpCodes.Ldloc, targetNumber);
                    il.Emit(OpCodes.Call, BoxMethod);
                    il.Emit(OpCodes.Ldloc, resultValue);
                    il.Emit(OpCodes.Call, Method(typeof(KernelStoreProperty), nameof(KernelStoreProperty.Store)));
                    EmitAfterForeign();
                    il.Emit(OpCodes.Br, done);
                    il.MarkLabel(interpret);
                    EmitInterpretEffect(effect, value is not null, resultValue);
                    Return(targetNumber);
                    Return(target);
                    break;
                }

            default:
                throw new NotSupportedException(effect.GetType().Name);
        }

        il.MarkLabel(done);
        if (value is null)
        {
            Return(resultValue);
        }

        if (number is null)
        {
            Return(resultNumber);
        }
    }

    /// <summary>
    /// The index of an element of the Uint8Array a name holds into <paramref name="index"/>, the array's facts taken
    /// the first time after the name was read; anything else (not held, another kind of value, a key that is not an
    /// index of an element) jumps to <paramref name="generic"/>, the step <see cref="KernelCodeRuntime.ReadTypedElement"/>
    /// or <see cref="KernelCodeRuntime.StoreTypedElement"/> takes.
    /// </summary>
    private void EmitTypedIndex(NameLocals locals, LocalBuilder target, LocalBuilder key, LocalBuilder index, Label generic)
    {
        var il = _il;
        var known = il.DefineLabel();
        EmitHeld(locals, NameLocals.TypedHeld);
        il.Emit(OpCodes.Brtrue, known);
        EmitHeld(locals, NameLocals.ValueHeld | NameLocals.NotTyped);
        il.Emit(OpCodes.Ldc_I4, NameLocals.ValueHeld);
        il.Emit(OpCodes.Bne_Un, generic);
        il.Emit(OpCodes.Ldloc, target);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.TypedFacts)));
        var facts = Rent(typeof(KernelTypedFacts));
        il.Emit(OpCodes.Stloc, facts);
        il.Emit(OpCodes.Ldloc, facts);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelTypedFacts), nameof(KernelTypedFacts.Data)));
        il.Emit(OpCodes.Stloc, locals.TypedData);
        il.Emit(OpCodes.Ldloc, facts);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelTypedFacts), nameof(KernelTypedFacts.Offset)));
        il.Emit(OpCodes.Stloc, locals.TypedOffset);
        il.Emit(OpCodes.Ldloc, facts);
        il.Emit(OpCodes.Ldfld, Field(typeof(KernelTypedFacts), nameof(KernelTypedFacts.Length)));
        il.Emit(OpCodes.Stloc, locals.TypedLength);
        Return(facts);

        var typed = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, locals.TypedData);
        il.Emit(OpCodes.Brtrue, typed);
        EmitAddState(locals, NameLocals.NotTyped);
        il.Emit(OpCodes.Br, generic);
        il.MarkLabel(typed);
        EmitAddState(locals, NameLocals.TypedHeld);

        // An index of an element: an integer the key converts to and back exactly (-0 is "0"; NaN and keys out of
        // range do not), below the length.
        il.MarkLabel(known);
        il.Emit(OpCodes.Ldloc, key);
        EmitTruncate();
        il.Emit(OpCodes.Stloc, index);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ldloc, key);
        il.Emit(OpCodes.Bne_Un, generic);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Ldloc, locals.TypedLength);
        il.Emit(OpCodes.Bge_Un, generic);
    }

    /// <summary>
    /// The double on the stack truncated to an int, any int for NaN or a value out of range: one instruction where
    /// the processor has one, rather than the saturating conversion of <c>conv.i4</c>.
    /// </summary>
    private void EmitTruncate()
    {
        if (TruncateScalar is not null)
        {
            _il.Emit(OpCodes.Call, CreateScalar!);
            _il.Emit(OpCodes.Call, TruncateScalar);
        }
        else
        {
            _il.Emit(OpCodes.Conv_I4);
        }
    }

    private static readonly MethodInfo? CreateScalar = typeof(System.Runtime.Intrinsics.Vector128).GetMethod(
        nameof(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe), [typeof(double)]);

    private static readonly MethodInfo? TruncateScalar = System.Runtime.Intrinsics.X86.Sse2.IsSupported && CreateScalar is not null
        ? typeof(System.Runtime.Intrinsics.X86.Sse2).GetMethod(
            nameof(System.Runtime.Intrinsics.X86.Sse2.ConvertToInt32WithTruncation), [typeof(System.Runtime.Intrinsics.Vector128<double>)])
        : null;

    private void EmitInterpretEffect(KernelEffect effect, bool valueUsed, LocalBuilder value)
    {
        var il = _il;
        EmitBeforeForeign();
        EmitConstant(effect);
        il.Emit(OpCodes.Ldarg_1);
        if (valueUsed)
        {
            il.Emit(OpCodes.Call, Method(typeof(KernelEffect), nameof(KernelEffect.Interpret)));
            il.Emit(OpCodes.Stloc, value);
        }
        else
        {
            il.Emit(OpCodes.Call, Method(typeof(KernelEffect), nameof(KernelEffect.InterpretDiscarded)));
        }

        EmitAfterForeign();
    }

    /// <summary><see cref="KernelSlots.Find"/>: the slot reference of the run while it holds, else the tree's lookup.</summary>
    private void EmitFind(KernelName name, LocalBuilder binding, LocalBuilder environment)
    {
        var il = _il;
        var locals = Name(name);
        var slow = il.DefineLabel();
        var done = il.DefineLabel();
        EmitSlotGuard(locals, slow);
        il.Emit(OpCodes.Ldloc, locals.Ref);
        il.Emit(OpCodes.Stloc, binding);
        il.Emit(OpCodes.Ldloc, locals.Environment);
        il.Emit(OpCodes.Stloc, environment);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(slow);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, name._binding);
        il.Emit(OpCodes.Ldloca, _outEnvironment);
        il.Emit(OpCodes.Call, Method(typeof(KernelSlots), nameof(KernelSlots.Find)));
        il.Emit(OpCodes.Stloc, binding);
        il.Emit(OpCodes.Ldloc, _outEnvironment);
        il.Emit(OpCodes.Stloc, environment);
        il.MarkLabel(done);
    }

    // ----- locals, constants, members -----

    private NameLocals Name(KernelName name)
    {
        if (!_names.TryGetValue(name._binding, out var locals))
        {
            locals = new NameLocals
            {
                Binding = name._binding,
                Ref = _il.DeclareLocal(typeof(Binding).MakeByRefType()),
                Environment = _il.DeclareLocal(typeof(DeclarativeEnvironment)),
                Number = _il.DeclareLocal(typeof(double)),
                Value = _il.DeclareLocal(typeof(JsValue)),
                State = _il.DeclareLocal(typeof(int)),
                TypedData = _il.DeclareLocal(typeof(byte[])),
                TypedOffset = _il.DeclareLocal(typeof(int)),
                TypedLength = _il.DeclareLocal(typeof(int)),
                Length = _il.DeclareLocal(typeof(double)),
            };
            _names[name._binding] = locals;
        }

        return locals;
    }

    private Target NewTarget() => new(_il.DefineLabel(), _depth, _site);

    /// <summary>A jump to a target, with the site put back to the target's when it is in another.</summary>
    private void EmitJump(Target target)
    {
        if (target.Site != AnySite && target.Site != _site)
        {
            _il.Emit(OpCodes.Ldc_I4, target.Site);
            _il.Emit(OpCodes.Stloc, _siteLocal);
        }

        _il.Emit(target.Depth < _depth ? OpCodes.Leave : OpCodes.Br, target.Label);
    }

    private LocalBuilder Rent(Type type)
    {
        if (_free.TryGetValue(type, out var free) && free.Count > 0)
        {
            return free.Pop();
        }

        return _il.DeclareLocal(type);
    }

    private void Return(LocalBuilder local)
    {
        if (!_free.TryGetValue(local.LocalType, out var free))
        {
            _free[local.LocalType] = free = new Stack<LocalBuilder>();
        }

        free.Push(local);
    }

    private int ConstantIndex(object value)
    {
        if (!_constantIndices.TryGetValue(value, out var index))
        {
            index = _constants.Count;
            _constants.Add(value);
            _constantIndices[value] = index;
        }

        return index;
    }

    /// <summary>A constant, loaded from the constants where it is used: for the paths that hand back or call the tree.</summary>
    private void EmitConstant(object value, Type? type = null) => EmitConstantLoad(value, type ?? value.GetType());

    private void EmitConstantLoad(object value, Type type)
    {
        var il = _il;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, ConstantIndex(value));
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Castclass, type);
    }

    /// <summary>A constant the code uses on every iteration, loaded once on entry into a local.</summary>
    private void EmitHot(object value, Type type)
    {
        if (!_hot.TryGetValue(value, out var local))
        {
            local = _il.DeclareLocal(type);
            _hot[value] = local;
        }

        _il.Emit(OpCodes.Ldloc, local);
    }

    private const System.Reflection.BindingFlags AnyMember = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;

    private static readonly FieldInfo BailField = Field(typeof(KernelExpression), nameof(KernelExpression.Bail));
    private static readonly FieldInfo ResultValue = Field(typeof(KernelResult), nameof(KernelResult.Value));
    private static readonly FieldInfo ResultNumber = Field(typeof(KernelResult), nameof(KernelResult.Number));
    private static readonly MethodInfo IsNullRef = typeof(Unsafe).GetMethod(nameof(Unsafe.IsNullRef))!.MakeGenericMethod(typeof(Binding));
    private static readonly MethodInfo ToInt32 = typeof(TypeConverter).GetMethod(nameof(TypeConverter.ToInt32), AnyMember, [typeof(double)])!;
    private static readonly MethodInfo ToUint32 = typeof(TypeConverter).GetMethod(nameof(TypeConverter.ToUint32), AnyMember, [typeof(double)])!;
    private static readonly MethodInfo TruthyOfUnion = typeof(KernelCodeRuntime).GetMethod(nameof(KernelCodeRuntime.Truthy), AnyMember, [typeof(JsValue), typeof(double)])!;
    private static readonly MethodInfo TruthyOfNumber = typeof(KernelCodeRuntime).GetMethod(nameof(KernelCodeRuntime.Truthy), AnyMember, [typeof(double)])!;
    private static readonly MethodInfo BoxMethod = Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.Box));

    // Looked up by its own name, not through Method, which would keep every method of the engine for reflection.
    private static readonly MethodInfo SetLastSyntaxElement = typeof(Engine).GetMethod(nameof(Engine.SetLastSyntaxElement), AnyMember)
        ?? throw new MissingMethodException(nameof(Engine), nameof(Engine.SetLastSyntaxElement));

    // The members compiled code calls, of Jint's own types, each named with typeof and nameof. A member a trimmer
    // removed nonetheless makes the lookup throw MissingMemberException, which KernelCode.TryCompile answers by
    // leaving the kernel a tree.
    private const System.Reflection.BindingFlags Inherited = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy;

    private static FieldInfo Field([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] Type type, string name)
        => type.GetField(name, AnyMember) ?? throw new MissingFieldException(type.Name, name);

    private static MethodInfo Method([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type, string name)
        => type.GetMethod(name, Inherited) ?? throw new MissingMethodException(type.Name, name);

    private static MethodInfo Getter([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)] Type type, string name)
        => type.GetProperty(name, AnyMember)?.GetMethod ?? throw new MissingMethodException(type.Name, name);
}
