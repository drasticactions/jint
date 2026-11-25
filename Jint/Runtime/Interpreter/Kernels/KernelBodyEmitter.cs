using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Expressions;
using BindingFlags = System.Reflection.BindingFlags;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// Compiles a function body's kernel tree to one .NET method, node by node: statements, slots and Number operators
/// as code, calls through the lanes the tree calls through, every other node run by calling the node.
/// </summary>
[RequiresDynamicCode("Function bodies are compiled to dynamic methods.")]
internal sealed class KernelBodyEmitter
{
    // The members looked up so far, by type and name, shared by every emitter.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MemberInfo> Members = new();

    private static readonly Type[] Parameters = [typeof(object[]), typeof(KernelFrame), typeof(double).MakeByRefType()];

    private readonly DynamicMethod _method;
    private readonly ILGenerator _il;
    private readonly List<object> _constants = [];
    private readonly Dictionary<object, int> _constantIndices = new(ReferenceEqualityComparer.Instance);
    private readonly List<(KernelBlockStatement Block, int Index, int Parent)> _sites = [];
    private readonly Dictionary<Type, Stack<LocalBuilder>> _free = [];
    private readonly int[] _localSlots;
    private readonly Key[]? _layout;
    private readonly LocalBuilder _engine;
    private readonly LocalBuilder _context;
    private readonly LocalBuilder _exact;
    private readonly LocalBuilder _counter;
    private readonly LocalBuilder _slots;
    private readonly LocalBuilder _site;
    private readonly LocalBuilder _resultValue;
    private readonly LocalBuilder _resultNumber;
    private readonly Label _exit;
    private readonly Label _abrupt;
    private int _currentSite = -1;

    private KernelBodyEmitter(Key[]? layout, int[] localSlots)
    {
        _layout = layout;
        _localSlots = localSlots;
        _method = new DynamicMethod("JintFunctionBody", typeof(JsValue), Parameters, typeof(KernelBodyEmitter).Module, skipVisibility: true);
        _il = _method.GetILGenerator();
        _engine = _il.DeclareLocal(typeof(Engine));
        _context = _il.DeclareLocal(typeof(EvaluationContext));
        _exact = _il.DeclareLocal(typeof(bool));
        _counter = _il.DeclareLocal(typeof(Constraints.MaxStatementsConstraint));
        _slots = _il.DeclareLocal(typeof(Binding[]));
        _site = _il.DeclareLocal(typeof(int));
        _resultValue = _il.DeclareLocal(typeof(JsValue));
        _resultNumber = _il.DeclareLocal(typeof(double));
        _exit = _il.DefineLabel();
        _abrupt = _il.DefineLabel();
    }

    /// <summary>The code of <paramref name="body"/>, run in environments whose slot names are <paramref name="layout"/>.</summary>
    internal static KernelCode Compile(KernelBlockStatement body, Key[]? layout, int[] localSlots)
    {
        var emitter = new KernelBodyEmitter(layout, localSlots);
        emitter.EmitMethod(body);
        var constants = emitter._constants.ToArray();
        var entry = (KernelCode.BodyEntry) emitter._method.CreateDelegate(typeof(KernelCode.BodyEntry), constants);
        return new KernelCode(entry, emitter._il.ILOffset);
    }

    private void EmitMethod(KernelBlockStatement body)
    {
        var il = _il;

        // The handler's sites, filled in once the body is emitted.
        var sitesIndex = _constants.Count;
        _constants.Add(null!);

        // Called without a frame only to be compiled.
        var start = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brtrue, start);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Stind_R8);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(start);
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
        if (_layout is not null)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldfld, Field(typeof(KernelFrame), nameof(KernelFrame.Start)));
            EmitConstant(_layout, typeof(Key[]));
            il.Emit(OpCodes.Ldloc, _engine);
            il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.LocalSlots)));
            il.Emit(OpCodes.Stloc, _slots);

            // An environment of another layout: the tree runs the body.
            var slotted = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, _slots);
            il.Emit(OpCodes.Brtrue, slotted);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_R8, 0.0);
            il.Emit(OpCodes.Stind_R8);
            il.Emit(OpCodes.Ldsfld, Field(typeof(KernelCode), nameof(KernelCode.NotRun)));
            il.Emit(OpCodes.Ret);
            il.MarkLabel(slotted);
        }

        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stloc, _site);
        il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
        il.Emit(OpCodes.Stloc, _resultValue);

        il.BeginExceptionBlock();
        EmitStatement(body);
        il.Emit(OpCodes.Leave, _exit);

        // The handlers of the blocks around the site, as KernelBlockStatement's.
        il.BeginExceptFilterBlock();
        var thrown = il.DeclareLocal(typeof(object));
        il.Emit(OpCodes.Stloc, thrown);
        EmitConstantIndex(sitesIndex, typeof(KernelCodeSites));
        il.Emit(OpCodes.Ldloc, thrown);
        il.Emit(OpCodes.Ldloc, _site);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeSites), nameof(KernelCodeSites.Catches)));
        il.BeginCatchBlock(null!);
        var exception = il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Castclass, typeof(Exception));
        il.Emit(OpCodes.Stloc, exception);
        EmitConstantIndex(sitesIndex, typeof(KernelCodeSites));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, exception);
        il.Emit(OpCodes.Ldloc, _site);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeSites), nameof(KernelCodeSites.Catch)));
        var rethrow = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, rethrow);
        il.Emit(OpCodes.Leave, _abrupt);
        il.MarkLabel(rethrow);
        il.Emit(OpCodes.Rethrow);
        il.EndExceptionBlock();

        il.MarkLabel(_exit);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, _resultNumber);
        il.Emit(OpCodes.Stind_R8);
        il.Emit(OpCodes.Ldloc, _resultValue);
        il.Emit(OpCodes.Ret);

        // An abrupt completion, left in the frame.
        il.MarkLabel(_abrupt);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Stind_R8);
        il.Emit(OpCodes.Ldsfld, BailField);
        il.Emit(OpCodes.Ret);

        _constants[sitesIndex] = new KernelCodeSites(_sites);
    }

    private void EmitStatement(KernelStatement statement)
    {
        var il = _il;
        switch (statement)
        {
            case KernelBlockStatement block:
                {
                    if (block._checked)
                    {
                        EmitChecks(block._statement);
                    }

                    var parent = _currentSite;
                    for (var i = 0; i < block._statements.Length; i++)
                    {
                        _sites.Add((block, i, parent));
                        SetSite(_sites.Count - 1);
                        EmitStatement(block._statements[i]);
                    }

                    SetSite(parent);
                    break;
                }

            case KernelExpressionStatement expressionStatement:
                EmitChecks(statement._statement);
                EmitDiscard(expressionStatement._expression);
                break;

            case KernelVarStatement varStatement:
                EmitChecks(statement._statement);
                foreach (var initializer in varStatement._initializers)
                {
                    EmitDiscard(initializer);
                }

                break;

            case KernelReturnStatement { _direct: true } returnStatement:
                EmitChecks(statement._statement);
                if (returnStatement._argument is null)
                {
                    il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
                    il.Emit(OpCodes.Stloc, _resultValue);
                }
                else
                {
                    EmitValue(returnStatement._argument, _resultValue, _resultNumber);
                }

                il.Emit(OpCodes.Leave, _exit);
                break;

            case KernelLiteralReturnStatement literalReturn:
                // As JintStatementList returns a literal: without the statement's checks.
                EmitConstant(literalReturn._value, typeof(JsValue));
                il.Emit(OpCodes.Stloc, _resultValue);
                il.Emit(OpCodes.Leave, _exit);
                break;

            case KernelEmptyStatement:
                EmitChecks(statement._statement);
                break;

            case KernelIfStatement ifStatement:
                {
                    EmitChecks(statement._statement);
                    var alternate = il.DefineLabel();
                    EmitBranch(ifStatement._test, alternate, when: false);
                    EmitStatement(ifStatement._consequent);
                    if (ifStatement._alternate is { } other)
                    {
                        var end = il.DefineLabel();
                        il.Emit(OpCodes.Br, end);
                        il.MarkLabel(alternate);
                        EmitStatement(other);
                        il.MarkLabel(end);
                    }
                    else
                    {
                        il.MarkLabel(alternate);
                    }

                    break;
                }

            case KernelReturnStatement:
                throw new NotSupportedException("A return that does not complete the body.");

            default:
                {
                    // Run by its node; anything but a normal completion is left in the frame.
                    var next = il.DefineLabel();
                    EmitConstant(statement, typeof(KernelStatement));
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, Method(typeof(KernelStatement), nameof(KernelStatement.Execute)));
                    il.Emit(OpCodes.Brfalse, next);
                    il.Emit(OpCodes.Leave, _abrupt);
                    il.MarkLabel(next);
                    break;
                }
        }
    }

    private void SetSite(int site)
    {
        if (site != _currentSite)
        {
            _currentSite = site;
            _il.Emit(OpCodes.Ldc_I4, site);
            _il.Emit(OpCodes.Stloc, _site);
        }
    }

    /// <summary>
    /// The checks the tree runs before a statement: the location stored; where no constraint would run, one statement
    /// off the exact constraints' allowance and the amortized countdown in place; otherwise the whole check, out of line.
    /// </summary>
    private void EmitChecks(Statement statement)
    {
        var il = _il;
        il.Emit(OpCodes.Ldloc, _engine);
        EmitConstant(statement, typeof(Statement));
        il.Emit(OpCodes.Call, SetLastSyntaxElement);

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
        il.MarkLabel(slow);
        il.Emit(OpCodes.Ldloc, _context);
        il.Emit(OpCodes.Ldloc, _engine);
        EmitConstant(statement, typeof(Statement));
        il.Emit(OpCodes.Ldloc, _exact);
        il.Emit(OpCodes.Ldloc, _counter);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.Check)));
        il.MarkLabel(done);
    }

    /// <summary>The value of an expression in <paramref name="value"/>, or null there and its Number in <paramref name="number"/>.</summary>
    private void EmitValue(KernelExpression expression, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        switch (expression)
        {
            case KernelNumber literal:
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Stloc, value);
                il.Emit(OpCodes.Ldc_R8, literal._value);
                il.Emit(OpCodes.Stloc, number);
                break;

            case KernelConstant constant:
                EmitConstant(constant._value, typeof(JsValue));
                il.Emit(OpCodes.Stloc, value);
                break;

            case KernelRoot root:
                {
                    var bail = il.DefineLabel();
                    var end = il.DefineLabel();
                    EmitPure(root._pure, value, number, bail);
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(bail);
                    EmitNode(root, value, number);
                    il.MarkLabel(end);
                    break;
                }

            case KernelCall call:
                EmitCall(call, value, number);
                break;

            case KernelArithmeticEffect arithmetic:
                EmitArithmeticEffect(arithmetic, value, number);
                break;

            case KernelComparison comparison:
                {
                    var no = il.DefineLabel();
                    var end = il.DefineLabel();
                    EmitComparison(comparison);
                    il.Emit(OpCodes.Brfalse, no);
                    EmitBoolean(true, value);
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(no);
                    EmitBoolean(false, value);
                    il.MarkLabel(end);
                    break;
                }

            case KernelConditionalEffect conditional:
                {
                    var alternate = il.DefineLabel();
                    var end = il.DefineLabel();
                    EmitBranch(conditional._test, alternate, when: false);
                    EmitValue(conditional._consequent, value, number);
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(alternate);
                    EmitValue(conditional._alternate, value, number);
                    il.MarkLabel(end);
                    break;
                }

            case KernelLogicalEffect logical:
                {
                    var end = il.DefineLabel();
                    EmitValue(logical._left, value, number);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, TruthyOfUnion);
                    il.Emit(logical._and ? OpCodes.Brfalse : OpCodes.Brtrue, end);
                    EmitValue(logical._right, value, number);
                    il.MarkLabel(end);
                    break;
                }

            case KernelNotEffect not:
                {
                    var no = il.DefineLabel();
                    var end = il.DefineLabel();
                    EmitBranch(not._argument, no, when: true);
                    EmitBoolean(true, value);
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(no);
                    EmitBoolean(false, value);
                    il.MarkLabel(end);
                    break;
                }

            case KernelAssignSlot assign when LocalSlot(assign._target) >= 0:
                EmitAssign(assign, value, number);
                break;

            default:
                EmitNode(expression, value, number);
                break;
        }
    }

    /// <summary>An expression evaluated for its effects.</summary>
    private void EmitDiscard(KernelExpression expression)
    {
        switch (expression)
        {
            case KernelAssignSlot assign when LocalSlot(assign._target) >= 0:
                EmitAssign(assign, null, null);
                break;

            case KernelCall or KernelArithmeticEffect or KernelConditionalEffect or KernelRoot:
                {
                    var value = Rent(typeof(JsValue));
                    var number = Rent(typeof(double));
                    EmitValue(expression, value, number);
                    Return(value);
                    Return(number);
                    break;
                }

            default:
                EmitConstant(expression, typeof(KernelExpression));
                _il.Emit(OpCodes.Ldarg_1);
                _il.Emit(OpCodes.Callvirt, Method(typeof(KernelExpression), nameof(KernelExpression.EvaluateDiscarded)));
                break;
        }
    }

    /// <summary>The node's own Evaluate.</summary>
    private void EmitNode(KernelExpression expression, LocalBuilder value, LocalBuilder number)
    {
        EmitConstant(expression, typeof(KernelExpression));
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.EvaluateNode)));
        EmitSplit(value, number);
    }

    /// <summary>A <see cref="KernelResult"/> on the stack split into the two locals.</summary>
    private void EmitSplit(LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var result = Rent(typeof(KernelResult));
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldloca, result);
        il.Emit(OpCodes.Ldfld, ResultValue);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloca, result);
        il.Emit(OpCodes.Ldfld, ResultNumber);
        il.Emit(OpCodes.Stloc, number);
        Return(result);
    }

    private void EmitBoolean(bool value, LocalBuilder local)
    {
        _il.Emit(OpCodes.Ldsfld, Field(typeof(JsBoolean), value ? nameof(JsBoolean.True) : nameof(JsBoolean.False)));
        _il.Emit(OpCodes.Stloc, local);
    }

    /// <summary>A pure expression's value, or a jump to <paramref name="bail"/> where it answers Bail.</summary>
    private void EmitPure(KernelExpression expression, LocalBuilder value, LocalBuilder number, Label bail)
    {
        var il = _il;
        switch (expression)
        {
            case KernelNumber or KernelConstant:
                EmitValue(expression, value, number);
                return;

            case KernelName name when LocalSlot(name) >= 0:
                EmitLocalRead(LocalSlot(name), value, number, bail, numberOnly: false);
                return;

            case KernelName name:
                EmitConstant(name, typeof(KernelName));
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.EvaluateName)));
                EmitSplit(value, number);
                break;

            case KernelBinary { _isComparison: true } comparison:
                {
                    var no = il.DefineLabel();
                    var end = il.DefineLabel();
                    EmitCompare(comparison, bail);
                    il.Emit(OpCodes.Brfalse, no);
                    EmitBoolean(true, value);
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(no);
                    EmitBoolean(false, value);
                    il.MarkLabel(end);
                    return;
                }

            case KernelBinary or KernelUnary { _operator: not Operator.LogicalNot }:
                EmitPureNumber(expression, number, bail);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Stloc, value);
                return;

            default:
                EmitConstant(expression, typeof(KernelExpression));
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.EvaluateNode)));
                EmitSplit(value, number);
                break;
        }

        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Ldsfld, BailField);
        il.Emit(OpCodes.Beq, bail);
    }

    /// <summary>The Number of a pure expression in <paramref name="number"/>; anything else jumps to <paramref name="bail"/>.</summary>
    private void EmitPureNumber(KernelExpression expression, LocalBuilder number, Label bail)
    {
        var il = _il;
        switch (expression)
        {
            case KernelNumber literal:
                il.Emit(OpCodes.Ldc_R8, literal._value);
                il.Emit(OpCodes.Stloc, number);
                return;

            case KernelBinary { _isComparison: false } binary:
                {
                    var left = Rent(typeof(double));
                    var right = Rent(typeof(double));
                    EmitPureNumber(binary._left, left, bail);
                    EmitPureNumber(binary._right, right, bail);
                    EmitOperator(binary._operator, left, right);
                    il.Emit(OpCodes.Stloc, number);
                    Return(left);
                    Return(right);
                    return;
                }

            case KernelName name when LocalSlot(name) >= 0:
                {
                    var read = Rent(typeof(JsValue));
                    EmitLocalRead(LocalSlot(name), read, number, bail, numberOnly: true);
                    Return(read);
                    return;
                }

            case KernelUnary { _operator: Operator.UnaryNegation or Operator.UnaryPlus or Operator.BitwiseNot } unary:
                EmitPureNumber(unary._argument, number, bail);
                if (unary._operator == Operator.UnaryNegation)
                {
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Neg);
                    il.Emit(OpCodes.Stloc, number);
                }
                else if (unary._operator == Operator.BitwiseNot)
                {
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, ToInt32);
                    il.Emit(OpCodes.Not);
                    il.Emit(OpCodes.Conv_R8);
                    il.Emit(OpCodes.Stloc, number);
                }

                return;
        }

        // Any other value: a Number, unboxed or boxed, or Bail.
        var value = Rent(typeof(JsValue));
        EmitPure(expression, value, number, bail);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Call, IsNumberValue);
        il.Emit(OpCodes.Brfalse, bail);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Ldloc, number);
        il.Emit(OpCodes.Call, NumberOf);
        il.Emit(OpCodes.Stloc, number);
        Return(value);
    }

    /// <summary>A pure comparison of two Numbers left on the stack; operands that are not Numbers jump to <paramref name="bail"/>.</summary>
    private void EmitCompare(KernelBinary comparison, Label bail)
    {
        var left = Rent(typeof(double));
        var right = Rent(typeof(double));
        EmitPureNumber(comparison._left, left, bail);
        EmitPureNumber(comparison._right, right, bail);
        EmitComparisonOperator(comparison._operator, left, right);
        Return(left);
        Return(right);
    }

    /// <summary>
    /// A slot the body declares read in place: its Number or plain value, or a jump to <paramref name="bail"/> for
    /// anything else (with <paramref name="numberOnly"/>, for any value but a Number).
    /// </summary>
    private void EmitLocalRead(int index, LocalBuilder value, LocalBuilder number, Label bail, bool numberOnly)
    {
        var il = _il;
        var slot = Rent(typeof(Binding).MakeByRefType());
        var reference = il.DefineLabel();
        var done = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, _slots);
        il.Emit(OpCodes.Ldc_I4, index);
        il.Emit(OpCodes.Ldelema, typeof(Binding));
        il.Emit(OpCodes.Stloc, slot);
        il.Emit(OpCodes.Ldloc, slot);
        il.Emit(OpCodes.Ldfld, BindingBits);
        il.Emit(OpCodes.Ldc_I8, 32L);
        il.Emit(OpCodes.Blt_Un, reference);

        // An unboxed Number: the double's bits.
        il.Emit(OpCodes.Ldloc, slot);
        il.Emit(OpCodes.Ldflda, BindingBits);
        il.Emit(OpCodes.Ldind_R8);
        il.Emit(OpCodes.Stloc, number);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(reference);
        if (!numberOnly)
        {
            // Not plain (an owned string), uninitialized, or carrying the read hook: the node's case.
            il.Emit(OpCodes.Ldloc, slot);
            il.Emit(OpCodes.Ldfld, BindingBits);
            il.Emit(OpCodes.Ldc_I8, 16L);
            il.Emit(OpCodes.Bge_Un, bail);
        }

        il.Emit(OpCodes.Ldloc, slot);
        il.Emit(OpCodes.Ldfld, BindingValue);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Brfalse, bail);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Ldfld, Field(typeof(JsValue), nameof(JsValue._type)));
        if (numberOnly)
        {
            il.Emit(OpCodes.Ldc_I4, (int) (InternalTypes.Number | InternalTypes.Integer));
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Brfalse, bail);
            il.Emit(OpCodes.Ldloc, value);
            il.Emit(OpCodes.Ldfld, Field(typeof(JsNumber), nameof(JsNumber._value)));
            il.Emit(OpCodes.Stloc, number);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc, value);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4, (int) InternalTypes.ReadHook);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Brtrue, bail);
        }

        il.MarkLabel(done);
        Return(slot);
    }

    /// <summary>The slot of a name the body declares, or -1.</summary>
    private int LocalSlot(KernelName name) => _layout is not null ? _localSlots[name._binding] : -1;

    /// <summary>Jumps to <paramref name="target"/> when ToBoolean of the expression is <paramref name="when"/>.</summary>
    private void EmitBranch(KernelExpression expression, Label target, bool when)
    {
        var il = _il;
        switch (expression)
        {
            case KernelRoot { _pure: KernelBinary { _isComparison: true } comparison } root:
                {
                    var bail = il.DefineLabel();
                    var end = il.DefineLabel();
                    EmitCompare(comparison, bail);
                    il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(bail);
                    EmitTest(root, target, when);
                    il.MarkLabel(end);
                    return;
                }

            case KernelRoot root:
                {
                    var bail = il.DefineLabel();
                    var end = il.DefineLabel();
                    var value = Rent(typeof(JsValue));
                    var number = Rent(typeof(double));
                    EmitPure(root._pure, value, number, bail);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, TruthyOfUnion);
                    il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                    il.Emit(OpCodes.Br, end);
                    il.MarkLabel(bail);
                    EmitTest(root, target, when);
                    il.MarkLabel(end);
                    Return(value);
                    Return(number);
                    return;
                }

            case KernelComparison comparison:
                EmitComparison(comparison);
                il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                return;

            case KernelNotEffect not:
                EmitBranch(not._argument, target, !when);
                return;

            case KernelLogicalEffect logical:
                {
                    // a && b is false when either is; a || b is true when either is.
                    if (logical._and != when)
                    {
                        EmitBranch(logical._left, target, when);
                        EmitBranch(logical._right, target, when);
                    }
                    else
                    {
                        var end = il.DefineLabel();
                        EmitBranch(logical._left, end, !when);
                        EmitBranch(logical._right, target, when);
                        il.MarkLabel(end);
                    }

                    return;
                }

            case KernelCall or KernelArithmeticEffect or KernelConditionalEffect:
                {
                    var value = Rent(typeof(JsValue));
                    var number = Rent(typeof(double));
                    EmitValue(expression, value, number);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Ldloc, number);
                    il.Emit(OpCodes.Call, TruthyOfUnion);
                    il.Emit(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                    Return(value);
                    Return(number);
                    return;
                }

            default:
                EmitTest(expression, target, when);
                return;
        }
    }

    /// <summary>The node's own Test, 1 for true.</summary>
    private void EmitTest(KernelExpression expression, Label target, bool when)
    {
        EmitConstant(expression, typeof(KernelExpression));
        _il.Emit(OpCodes.Ldarg_1);
        _il.Emit(OpCodes.Callvirt, Method(typeof(KernelExpression), nameof(KernelExpression.Test)));
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(when ? OpCodes.Beq : OpCodes.Bne_Un, target);
    }

    /// <summary><see cref="KernelComparison.Test"/>, the result left on the stack.</summary>
    private void EmitComparison(KernelComparison comparison)
    {
        var il = _il;
        var leftValue = Rent(typeof(JsValue));
        var leftNumber = Rent(typeof(double));
        var rightValue = Rent(typeof(JsValue));
        var rightNumber = Rent(typeof(double));
        var slow = il.DefineLabel();
        var end = il.DefineLabel();
        EmitValue(comparison._left, leftValue, leftNumber);
        EmitValue(comparison._right, rightValue, rightNumber);
        EmitBothNumbers(leftValue, rightValue, slow);
        EmitNumberOf(leftValue, leftNumber);
        EmitNumberOf(rightValue, rightNumber);
        EmitComparisonOperator(comparison._operator, leftNumber, rightNumber);
        il.Emit(OpCodes.Br, end);
        il.MarkLabel(slow);
        EmitConstant(comparison, typeof(KernelComparison));
        il.Emit(OpCodes.Ldarg_1);
        EmitBox(leftValue, leftNumber);
        EmitBox(rightValue, rightNumber);
        il.Emit(OpCodes.Call, Method(typeof(KernelComparison), nameof(KernelComparison.CompareValues)));
        il.MarkLabel(end);
        Return(leftValue);
        Return(leftNumber);
        Return(rightValue);
        Return(rightNumber);
    }

    private void EmitArithmeticEffect(KernelArithmeticEffect arithmetic, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var leftValue = Rent(typeof(JsValue));
        var leftNumber = Rent(typeof(double));
        var rightValue = Rent(typeof(JsValue));
        var rightNumber = Rent(typeof(double));
        var slow = il.DefineLabel();
        var end = il.DefineLabel();
        EmitValue(arithmetic._left, leftValue, leftNumber);
        EmitValue(arithmetic._right, rightValue, rightNumber);
        EmitBothNumbers(leftValue, rightValue, slow);
        EmitNumberOf(leftValue, leftNumber);
        EmitNumberOf(rightValue, rightNumber);
        EmitOperator(arithmetic._operator, leftNumber, rightNumber);
        il.Emit(OpCodes.Stloc, number);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Br, end);
        il.MarkLabel(slow);
        EmitConstant(arithmetic, typeof(KernelArithmeticEffect));
        il.Emit(OpCodes.Ldarg_1);
        EmitBox(leftValue, leftNumber);
        EmitBox(rightValue, rightNumber);
        il.Emit(OpCodes.Call, Method(typeof(KernelArithmeticEffect), nameof(KernelArithmeticEffect.ApplyValues)));
        il.Emit(OpCodes.Stloc, value);
        il.MarkLabel(end);
        Return(leftValue);
        Return(leftNumber);
        Return(rightValue);
        Return(rightNumber);
    }

    /// <summary>The Number of a value <see cref="KernelCodeRuntime.IsNumberValue"/> accepted, stored back to its Number local.</summary>
    private void EmitNumberOf(LocalBuilder value, LocalBuilder number)
    {
        _il.Emit(OpCodes.Ldloc, value);
        _il.Emit(OpCodes.Ldloc, number);
        _il.Emit(OpCodes.Call, NumberOf);
        _il.Emit(OpCodes.Stloc, number);
    }

    /// <summary><see cref="KernelBinary.Apply"/> of two Numbers in locals, left on the stack.</summary>
    private void EmitOperator(Operator op, LocalBuilder left, LocalBuilder right)
    {
        var il = _il;
        var code = op switch
        {
            Operator.Addition => OpCodes.Add,
            Operator.Subtraction => OpCodes.Sub,
            Operator.Multiplication => OpCodes.Mul,
            Operator.Division => OpCodes.Div,
            _ => OpCodes.Nop,
        };

        if (code == OpCodes.Nop)
        {
            il.Emit(OpCodes.Ldc_I4, (int) op);
            il.Emit(OpCodes.Ldloc, left);
            il.Emit(OpCodes.Ldloc, right);
            il.Emit(OpCodes.Call, ApplyOperator);
            return;
        }

        il.Emit(OpCodes.Ldloc, left);
        il.Emit(OpCodes.Ldloc, right);
        il.Emit(code);
    }

    /// <summary>A comparison of two Numbers in locals, left on the stack as 1 or 0.</summary>
    private void EmitComparisonOperator(Operator op, LocalBuilder left, LocalBuilder right)
    {
        var il = _il;
        il.Emit(OpCodes.Ldloc, left);
        il.Emit(OpCodes.Ldloc, right);
        switch (op)
        {
            case Operator.LessThan:
                il.Emit(OpCodes.Clt);
                break;
            case Operator.GreaterThan:
                il.Emit(OpCodes.Cgt);
                break;
            case Operator.LessThanOrEqual:
                // Not greater and not unordered: left <= right.
                il.Emit(OpCodes.Cgt_Un);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case Operator.GreaterThanOrEqual:
                il.Emit(OpCodes.Clt_Un);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case Operator.Equality or Operator.StrictEquality:
                il.Emit(OpCodes.Ceq);
                break;
            default:
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
        }
    }

    private void EmitBothNumbers(LocalBuilder left, LocalBuilder right, Label otherwise)
    {
        _il.Emit(OpCodes.Ldloc, left);
        _il.Emit(OpCodes.Call, IsNumberValue);
        _il.Emit(OpCodes.Brfalse, otherwise);
        _il.Emit(OpCodes.Ldloc, right);
        _il.Emit(OpCodes.Call, IsNumberValue);
        _il.Emit(OpCodes.Brfalse, otherwise);
    }

    private void EmitBox(LocalBuilder value, LocalBuilder number)
    {
        _il.Emit(OpCodes.Ldloc, value);
        _il.Emit(OpCodes.Ldloc, number);
        _il.Emit(OpCodes.Call, BoxMethod);
    }

    /// <summary><see cref="KernelAssignSlot"/> to a slot the body declares; without locals, its value is discarded.</summary>
    private void EmitAssign(KernelAssignSlot assign, LocalBuilder? value, LocalBuilder? number)
    {
        var il = _il;
        var node = il.DefineLabel();
        var end = il.DefineLabel();
        var index = LocalSlot(assign._target);
        var assigned = value ?? Rent(typeof(JsValue));
        var assignedNumber = number ?? Rent(typeof(double));
        il.Emit(OpCodes.Ldloc, _slots);
        il.Emit(OpCodes.Ldc_I4, index);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.IsAssignableLocal)));
        il.Emit(OpCodes.Brfalse, node);
        EmitValue(assign._value, assigned, assignedNumber);
        il.Emit(OpCodes.Ldloc, _slots);
        il.Emit(OpCodes.Ldc_I4, index);
        il.Emit(OpCodes.Ldloc, assigned);
        il.Emit(OpCodes.Ldloc, assignedNumber);
        EmitConstant(assign, typeof(KernelAssignSlot));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.StoreLocal)));
        il.Emit(OpCodes.Br, end);
        il.MarkLabel(node);
        if (value is null)
        {
            EmitConstant(assign, typeof(KernelExpression));
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, Method(typeof(KernelExpression), nameof(KernelExpression.EvaluateDiscarded)));
        }
        else
        {
            EmitNode(assign, value, number!);
        }

        il.MarkLabel(end);
        if (value is null)
        {
            Return(assigned);
            Return(assignedNumber);
        }
    }

    /// <summary>
    /// <see cref="KernelCall.Evaluate"/>: the stack checked, the callee read, then the call made through the lane the
    /// tree makes it through, with the arguments evaluated by this code where the lane takes them evaluated.
    /// </summary>
    private void EmitCall(KernelCall call, LocalBuilder value, LocalBuilder number)
    {
        var il = _il;
        var interpret = il.DefineLabel();
        var end = il.DefineLabel();
        var thisValue = Rent(typeof(JsValue));
        var callee = Rent(typeof(JsValue));
        var scratch = Rent(typeof(double));

        il.Emit(OpCodes.Ldloc, _engine);
        il.Emit(OpCodes.Ldfld, Field(typeof(Engine), nameof(Engine._stackGuard)));
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.CanEnter)));
        il.Emit(OpCodes.Brfalse, interpret);

        if (call._member is not null)
        {
            // A base that is not an object or a primitive value read purely is left to the interpreter.
            EmitPure(call._base!, thisValue, scratch, interpret);
            il.Emit(OpCodes.Ldloc, thisValue);
            il.Emit(OpCodes.Brfalse, interpret);
            EmitConstant(call._member, typeof(KernelProperty));
            il.Emit(OpCodes.Ldloc, thisValue);
            il.Emit(OpCodes.Call, Method(typeof(KernelProperty), nameof(KernelProperty.ReadCallee)));
            il.Emit(OpCodes.Stloc, callee);
        }
        else
        {
            il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
            il.Emit(OpCodes.Stloc, thisValue);
            EmitPure(call._callee!, callee, scratch, interpret);
        }

        var function = Rent(typeof(ScriptFunction));
        var state = Rent(typeof(JintFunctionDefinition.State));
        var shape = Rent(typeof(FastCallShape));
        var builtin = il.DefineLabel();
        var arguments = il.DefineLabel();
        var inline = il.DefineLabel();
        var evaluate = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, callee);
        il.Emit(OpCodes.Call, Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.AsScriptFunction)));
        il.Emit(OpCodes.Stloc, function);
        il.Emit(OpCodes.Ldloc, function);
        il.Emit(OpCodes.Brfalse, builtin);

        // An interpreted function: the register lane, its state taken before any argument runs.
        var count = call._arguments.Length;
        if (count <= 4)
        {
            EmitConstant(call, typeof(KernelCall));
            il.Emit(OpCodes.Ldloc, function);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.RegisterState)));
            il.Emit(OpCodes.Stloc, state);
            il.Emit(OpCodes.Ldloc, state);
            il.Emit(OpCodes.Brfalse, arguments);
            EmitConstant(call, typeof(KernelCall));
            il.Emit(OpCodes.Ldloc, function);
            il.Emit(OpCodes.Ldloc, _engine);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.ComputesInPlace)));
            il.Emit(OpCodes.Brtrue, inline);
            il.Emit(OpCodes.Br, evaluate);
        }
        else
        {
            il.Emit(OpCodes.Br, arguments);
        }

        // A built-in with a fast call shape, which is taken before any argument runs too.
        il.MarkLabel(builtin);
        if (count <= 2)
        {
            EmitConstant(call, typeof(KernelCall));
            il.Emit(OpCodes.Ldloc, callee);
            il.Emit(OpCodes.Ldloca, shape);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.BuiltinShape)));
            il.Emit(OpCodes.Brfalse, interpret);
        }
        else
        {
            il.Emit(OpCodes.Br, interpret);
        }

        // The arguments, once, in order; then the lane's call, the register lane's where there is a function.
        il.MarkLabel(evaluate);
        var values = new LocalBuilder[count <= 4 ? count : 0];
        var numbers = new LocalBuilder[values.Length];
        if (count <= 4)
        {
            for (var i = 0; i < count; i++)
            {
                values[i] = Rent(typeof(JsValue));
                numbers[i] = Rent(typeof(double));
                EmitValue(call._arguments[i], values[i], numbers[i]);
            }

            var callBuiltin = il.DefineLabel();
            var callLane = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, function);
            il.Emit(OpCodes.Brfalse, callBuiltin);
            il.Emit(OpCodes.Ldloc, function);
            il.Emit(OpCodes.Ldloc, state);
            il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.HasCode)));
            il.Emit(OpCodes.Brfalse, callLane);
            EmitRegisterCall(call, function, thisValue, state, values, numbers, nameof(KernelCall.CallFromCode));
            EmitSplit(value, number);
            il.Emit(OpCodes.Br, end);
            il.MarkLabel(callLane);
            EmitRegisterCall(call, function, thisValue, state, values, numbers, nameof(KernelCall.CallRegisterLane));
            EmitSplit(value, number);
            il.Emit(OpCodes.Br, end);

            il.MarkLabel(callBuiltin);
            if (count <= 2)
            {
                EmitConstant(call, typeof(KernelCall));
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc, callee);
                il.Emit(OpCodes.Call, Method(typeof(Unsafe), nameof(Unsafe.As), 1, typeof(Function)));
                il.Emit(OpCodes.Ldloc, thisValue);
                il.Emit(OpCodes.Ldloc, shape);
                for (var i = 0; i < 2; i++)
                {
                    if (i < count)
                    {
                        EmitBox(values[i], numbers[i]);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldsfld, Field(typeof(JsValue), nameof(JsValue.Undefined)));
                    }
                }

                il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallBuiltinFromCode)));
                il.Emit(OpCodes.Stloc, value);
                il.Emit(OpCodes.Br, end);
            }
        }

        // A body the tree computes in place, and a call with an argument list: the tree's lanes, which evaluate the arguments.
        il.MarkLabel(inline);
        EmitConstant(call, typeof(KernelCall));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, function);
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallScriptFromCode)));
        EmitSplit(value, number);
        il.Emit(OpCodes.Br, end);
        il.MarkLabel(arguments);
        EmitConstant(call, typeof(KernelCall));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, function);
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.CallWithArgumentList)));
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Br, end);

        il.MarkLabel(interpret);
        EmitConstant(call, typeof(KernelCall));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, Method(typeof(KernelCall), nameof(KernelCall.Interpret)));
        il.Emit(OpCodes.Stloc, value);
        il.MarkLabel(end);

        for (var i = 0; i < values.Length; i++)
        {
            Return(values[i]);
            Return(numbers[i]);
        }

        Return(shape);
        Return(function);
        Return(state);
        Return(thisValue);
        Return(callee);
        Return(scratch);
    }

    /// <summary>A register lane's call of <paramref name="function"/> with its arguments in locals, through <paramref name="lane"/>.</summary>
    private void EmitRegisterCall(KernelCall call, LocalBuilder function, LocalBuilder thisValue, LocalBuilder state, LocalBuilder[] values, LocalBuilder[] numbers, string lane)
    {
        var il = _il;
        EmitConstant(call, typeof(KernelCall));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, function);
        il.Emit(OpCodes.Ldloc, thisValue);
        il.Emit(OpCodes.Ldloc, state);
        for (var i = 0; i < 4; i++)
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

        il.Emit(OpCodes.Call, Method(typeof(KernelCall), lane));
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

    private void EmitConstant(object value, Type type)
    {
        if (!_constantIndices.TryGetValue(value, out var index))
        {
            index = _constants.Count;
            _constants.Add(value);
            _constantIndices[value] = index;
        }

        EmitConstantIndex(index, type);
    }

    private void EmitConstantIndex(int index, Type type)
    {
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldc_I4, index);
        _il.Emit(OpCodes.Ldelem_Ref);
        _il.Emit(OpCodes.Call, Method(typeof(Unsafe), nameof(Unsafe.As), 1, type));
    }

    private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly FieldInfo BailField = Field(typeof(KernelExpression), nameof(KernelExpression.Bail));
    private static readonly FieldInfo BindingBits = Field(typeof(Binding), "_bits");
    private static readonly FieldInfo BindingValue = Field(typeof(Binding), "_value");
    private static readonly FieldInfo ResultValue = Field(typeof(KernelResult), nameof(KernelResult.Value));
    private static readonly FieldInfo ResultNumber = Field(typeof(KernelResult), nameof(KernelResult.Number));
    private static readonly MethodInfo ToInt32 = typeof(TypeConverter).GetMethod(nameof(TypeConverter.ToInt32), AnyMember, [typeof(double)])!;
    private static readonly MethodInfo TruthyOfUnion = Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.IsTruthy));
    private static readonly MethodInfo BoxMethod = Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.Boxed));
    private static readonly MethodInfo IsNumberValue = Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.IsNumberValue));
    private static readonly MethodInfo NumberOf = Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.NumberOf));
    private static readonly MethodInfo SetLastSyntaxElement = typeof(Engine).GetMethod(nameof(Engine.SetLastSyntaxElement), AnyMember)
        ?? throw new MissingMethodException(nameof(Engine), nameof(Engine.SetLastSyntaxElement));
    private static readonly MethodInfo ApplyOperator = Method(typeof(KernelCodeRuntime), nameof(KernelCodeRuntime.ApplyOperator));

    private const BindingFlags Inherited = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    private static FieldInfo Field([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] Type type, string name)
        => Members.TryGetValue((type, "F" + name), out var member)
            ? (FieldInfo) member
            : (FieldInfo) Members.GetOrAdd((type, "F" + name), type.GetField(name, AnyMember) ?? throw new MissingFieldException(type.Name, name));

    private static MethodInfo Method([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type, string name)
        => Members.TryGetValue((type, "M" + name), out var member)
            ? (MethodInfo) member
            : (MethodInfo) Members.GetOrAdd((type, "M" + name), type.GetMethod(name, Inherited) ?? throw new MissingMethodException(type.Name, name));

    private static MethodInfo Getter([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)] Type type, string name)
        => Members.TryGetValue((type, "G" + name), out var member)
            ? (MethodInfo) member
            : (MethodInfo) Members.GetOrAdd((type, "G" + name), type.GetProperty(name, AnyMember)?.GetMethod ?? throw new MissingMethodException(type.Name, name));

    private static MethodInfo Method([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type, string name, int genericArity, Type argument)
    {
        if (Members.TryGetValue((argument, "A" + name), out var member))
        {
            return (MethodInfo) member;
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name == name && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == genericArity && method.GetParameters() is [{ ParameterType: { IsByRef: false } }])
            {
                return (MethodInfo) Members.GetOrAdd((argument, "A" + name), method.MakeGenericMethod(argument));
            }
        }

        throw new MissingMethodException(type.Name, name);
    }
}
