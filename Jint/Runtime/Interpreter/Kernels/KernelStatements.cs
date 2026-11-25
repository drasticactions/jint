using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.TypedArray;
using Jint.Runtime.Interpreter.Statements;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// A statement of a loop kernel. It runs the per-statement checks exactly where the interpreter's handler runs them,
/// so a statement limit, a time limit or a memory limit stops the script at the same statement, and completes with
/// one of the codes below; an abrupt completion other than an unlabelled break or continue is left in
/// <see cref="KernelFrame.Abrupt"/>.
/// </summary>
internal abstract class KernelStatement
{
    internal const int Normal = 0;
    internal const int Break = 1;
    internal const int Continue = 2;
    internal const int Abrupt = 3;

    /// <summary>
    /// The kernel's outermost loop has completed, run to its end by compiled code entered at the head of a loop inside
    /// it, with the code in <see cref="KernelFrame.FinishedCode"/>: every statement of the tree around passes it on.
    /// </summary>
    internal const int Finished = 4;

    internal readonly Statement _statement;

    // How many times the tree ran the statement: compiled code leaves one the tree hardly ran to the tree (see
    // KernelEmitter).
    internal int _runs;

    protected KernelStatement(Statement statement)
    {
        _statement = statement;
    }

    internal abstract int Execute(KernelFrame frame);

    /// <summary>What JintStatement.Execute does before a statement that is not a block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void RunChecks(KernelFrame frame)
    {
        frame.Work++;
        RunChecks(frame, _statement);
    }

    /// <summary>
    /// <see cref="EvaluationContext.PrepareFor"/> and <see cref="EvaluationContext.RunBeforeExecuteStatementChecks"/>
    /// of <paramref name="statement"/>, with a statement the exact constraints let pass taken in place: a kernel runs
    /// without coverage or a debugger (<see cref="KernelProgram.CanRun"/>), which are all the per-statement checks do
    /// besides the constraints.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RunChecks(KernelFrame frame, Statement statement)
    {
        var context = frame.Context;
        var engine = frame.Engine;
        engine.SetLastSyntaxElement(statement);
        if (context.ShouldRunPerStatementChecks)
        {
            if (engine._statementsBeforeExactChecks > 0)
            {
                engine._statementsBeforeExactChecks--;
            }
            else
            {
                engine.RunPerStatementChecks(statement);
            }
        }
        else
        {
            context.ChargeStatement();
        }

        if (context.RunsAmortizedChecks && --engine._amortizedConstraintCountdown <= 0)
        {
            KernelCodeRuntime.CheckAmortized(engine);
        }
    }
}

/// <summary>A statement the kernel leaves to the interpreter's handler.</summary>
internal sealed class KernelInterpretedStatement : KernelStatement
{
    internal readonly JintStatement _handler;

    public KernelInterpretedStatement(Statement statement, JintStatement handler) : base(statement)
    {
        _handler = handler;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        var completion = _handler.Execute(frame.Context);
        if (completion.Type == CompletionType.Normal)
        {
            return Normal;
        }

        if (completion.Type is CompletionType.Break or CompletionType.Continue && completion.Target is null)
        {
            return completion.Type == CompletionType.Break ? Break : Continue;
        }

        frame.Abrupt = completion;
        return Abrupt;
    }
}

/// <summary>An expression statement, whose value nobody reads.</summary>
internal sealed class KernelExpressionStatement : KernelStatement
{
    internal readonly KernelExpression _expression;

    // The effects statements are most, called directly.
    private readonly int _kind;
    private const int Other = 0;
    private const int Assign = 1;
    private const int Update = 2;
    private const int Store = 3;
    private const int Compound = 4;

    public KernelExpressionStatement(Statement statement, KernelExpression expression) : base(statement)
    {
        _expression = expression;
        _kind = expression switch
        {
            KernelAssignSlot => Assign,
            KernelUpdateSlot => Update,
            KernelStoreElement => Store,
            KernelCompoundSlot => Compound,
            _ => Other,
        };
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        var expression = _expression;
        switch (_kind)
        {
            case Assign:
                Unsafe.As<KernelAssignSlot>(expression).EvaluateDiscarded(frame);
                break;
            case Update:
                Unsafe.As<KernelUpdateSlot>(expression).EvaluateDiscarded(frame);
                break;
            case Store:
                Unsafe.As<KernelStoreElement>(expression).EvaluateDiscarded(frame);
                break;
            case Compound:
                Unsafe.As<KernelCompoundSlot>(expression).EvaluateDiscarded(frame);
                break;
            default:
                expression.EvaluateDiscarded(frame);
                break;
        }

        return Normal;
    }
}

/// <summary><c>var a = x, b;</c>: each initializer stored as an assignment stores it; a declarator without one does nothing.</summary>
internal sealed class KernelVarStatement : KernelStatement
{
    internal readonly KernelExpression[] _initializers;

    public KernelVarStatement(Statement statement, KernelExpression[] initializers) : base(statement)
    {
        _initializers = initializers;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        foreach (var initializer in _initializers)
        {
            initializer.EvaluateDiscarded(frame);
        }

        return Normal;
    }
}

/// <summary><c>return value;</c>, whose value is not a call the interpreter would make as a tail call.</summary>
internal sealed class KernelReturnStatement : KernelStatement
{
    internal readonly KernelExpression? _argument;

    public KernelReturnStatement(Statement statement, KernelExpression? argument) : base(statement)
    {
        _argument = argument;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        var value = _argument is null ? JsValue.Undefined : Box(_argument.Evaluate(frame, out var number), number);
        frame.Abrupt = new Completion(CompletionType.Return, value, _statement);
        return Abrupt;
    }

    private static JsValue Box(JsValue? value, double number) => KernelExpression.Box(value, number);
}

/// <summary>
/// <c>return literal;</c> directly in a statement list, which JintStatementList completes with the literal's value
/// without running the statement or its checks.
/// </summary>
internal sealed class KernelLiteralReturnStatement : KernelStatement
{
    internal readonly JsValue _value;

    public KernelLiteralReturnStatement(Statement statement, JsValue value) : base(statement)
    {
        _value = value;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        frame.Abrupt = new Completion(CompletionType.Return, _value, _statement);
        return Abrupt;
    }
}

internal sealed class KernelEmptyStatement : KernelStatement
{
    public KernelEmptyStatement(Statement statement) : base(statement)
    {
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        return Normal;
    }
}

/// <summary>An unlabelled <c>break</c> or <c>continue</c>.</summary>
internal sealed class KernelJumpStatement : KernelStatement
{
    internal readonly int _code;

    public KernelJumpStatement(Statement statement, bool isBreak) : base(statement)
    {
        _code = isBreak ? Break : Continue;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        return _code;
    }
}

internal sealed class KernelIfStatement : KernelStatement
{
    internal readonly KernelExpression _test;
    internal readonly KernelStatement _consequent;
    internal readonly KernelStatement? _alternate;

    // A test comparing two operands KernelOperand reads, taken without its nodes while its root takes it purely.
    private readonly KernelRoot? _compareRoot;
    private readonly KernelBinary? _compare;

    public KernelIfStatement(Statement statement, KernelExpression test, KernelStatement consequent, KernelStatement? alternate) : base(statement)
    {
        _test = test;
        _consequent = consequent;
        _alternate = alternate;
        if (test is KernelRoot { _pure: KernelBinary { _isComparison: true } compare } root
            && (compare._leftKind != KernelOperand.Other || compare._rightKind != KernelOperand.Other))
        {
            _compareRoot = root;
            _compare = compare;
        }
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        if (TestCondition(frame) == 1)
        {
            return _consequent.Execute(frame);
        }

        return _alternate?.Execute(frame) ?? Normal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TestCondition(KernelFrame frame)
    {
        var compare = _compare;
        if (compare is not null
            && !_compareRoot!._interpretOnly
            && KernelOperand.TryNumber(compare._left, compare._leftKind, frame, out var left)
            && KernelOperand.TryNumber(compare._right, compare._rightKind, frame, out var right))
        {
            _compareRoot.Completed();
            return KernelBinary.Compare(compare._operator, left, right) ? 1 : 0;
        }

        // Two operands that are not Numbers: the root tests again, which repeats nothing observable.
        return _test.Test(frame);
    }
}

/// <summary>
/// A block that declares nothing, as JintBlockStatement runs it: one statement runs alone; any other number runs as
/// a statement list, which is checked as a statement itself. Either way a throw out of a statement becomes the
/// block's throw completion, with the location the statement list gives it.
/// </summary>
internal sealed class KernelBlockStatement : KernelStatement
{
    internal readonly KernelStatement[] _statements;
    internal readonly bool _checked;

    public KernelBlockStatement(Statement statement, KernelStatement[] statements) : this(statement, statements, statements.Length != 1)
    {
    }

    /// <summary>A block, or with <paramref name="isChecked"/> a function body, which is checked whatever its length.</summary>
    public KernelBlockStatement(Statement statement, KernelStatement[] statements, bool isChecked) : base(statement)
    {
        _statements = statements;
        _checked = isChecked;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        if (!_checked)
        {
            return ExecuteSingle(frame);
        }

        var statements = _statements;
        RunChecks(frame);

        var i = 0;
        try
        {
            for (; i < statements.Length; i++)
            {
                var code = statements[i].Execute(frame);
                if (code != Normal)
                {
                    return code;
                }
            }
        }
        catch (Exception ex) when (!frame.Escaping && JintStatementList.ShouldCatch(ex))
        {
            if (Handle(frame, ex, statements[i]._statement))
            {
                return Abrupt;
            }

            throw;
        }

        return Normal;
    }

    /// <summary>A block of one statement, which runs alone and unchecked; its handler is the same.</summary>
    private int ExecuteSingle(KernelFrame frame)
    {
        var statement = _statements[0];
        try
        {
            return statement.Execute(frame);
        }
        catch (Exception ex) when (!frame.Escaping && JintStatementList.ShouldCatch(ex))
        {
            if (Handle(frame, ex, statement._statement))
            {
                return Abrupt;
            }

            throw;
        }
    }

    /// <summary>
    /// An exception out of <paramref name="statement"/>: true once it is the block's throw completion; false, with the
    /// location attached, for the handler to rethrow it.
    /// </summary>
    private static bool Handle(KernelFrame frame, Exception ex, Statement statement)
    {
        var context = frame.Context;
        if (ex is JintException)
        {
            frame.Abrupt = JintStatementList.HandleStatementException(context, ex, statement);
            return true;
        }

        var engine = context.Engine;
        var location = engine.HasLastSyntaxElement ? engine.LastSyntaxLocation : statement.Location;
        ExceptionDataHelper.TryAttachJavaScriptLocation(ex, engine, location);
        return false;
    }
}

/// <summary>
/// A <c>for</c> statement whose header declares nothing lexically
/// (https://tc39.es/ecma262/#sec-forbodyevaluation): the checks of the statement and of a <c>var</c> header, the test,
/// the body and the update, with the body's break and continue handled as JintForStatement handles them.
/// </summary>
internal sealed class KernelForStatement : KernelStatement
{
    internal readonly KernelStatement? _initStatement;
    internal readonly KernelExpression? _initExpression;
    internal readonly KernelExpression? _test;
    internal readonly KernelExpression? _update;
    internal readonly KernelStatement _body;
    internal readonly JintForStatement? _owner;

    public KernelForStatement(
        Statement statement,
        KernelStatement? initStatement,
        KernelExpression? initExpression,
        KernelExpression? test,
        KernelExpression? update,
        KernelStatement body,
        JintForStatement? owner) : base(statement)
    {
        _initStatement = initStatement;
        _initExpression = initExpression;
        _test = test;
        _update = update;
        _body = body;
        _owner = owner;
        _header = KernelLoopHeader.TryCreate(test, update);
    }

    // The test and update taken in place when they count a name up or down to a limit (see KernelLoopHeader).
    internal readonly KernelLoopHeader? _header;

    // The loop kernel this loop belongs to (none in a function body), and the entry of its code at this loop's head.
    internal LoopKernel? _kernel;
    internal int _entry;

    internal override int Execute(KernelFrame frame)
    {
        // The outermost loop runs as code once the kernel has code; a loop inside it is entered at its head.
        if (_entry == 0 && _kernel?._code is { } compiled && frame.CodeThreshold >= 0 && !frame.InCode)
        {
            return compiled.Run(frame, KernelCode.StartEntry);
        }

        // The loop the kernel stands in for has been checked by its own Execute already.
        if (_owner is null)
        {
            RunChecks(frame);
        }

        _initStatement?.Execute(frame);
        _initExpression?.EvaluateDiscarded(frame);

        var test = _test;
        var update = _update;
        var body = _body;
        var header = _header;

        // The counter's Number as the update stored it, which the test that follows reads (nothing runs between).
        var counted = false;
        var counter = 0.0;
        while (true)
        {
            // An iteration about to begin can as well begin in the kernel's code: everything the tree has done is in
            // the environments.
            var kernel = _kernel;
            if (kernel is not null)
            {
                kernel._iterations++;
            }

            if (kernel is not null && frame.CodeThreshold >= 0 && kernel.WorthCompiling(frame) && !frame.InCode && kernel.Compile() is { } code)
            {
                return EnterCode(frame, code);
            }

            if (header is not null)
            {
                var result = header.Test(frame, counted, counter);
                if (result < 0)
                {
                    result = test!.Test(frame);
                }

                if (result != 1)
                {
                    return Normal;
                }
            }
            else if (test is not null && test.Test(frame) != 1)
            {
                return Normal;
            }

            var bodyCode = body.Execute(frame);
            if (bodyCode != Normal)
            {
                if (bodyCode == Finished)
                {
                    return Finished;
                }

                if (bodyCode == Break)
                {
                    return Normal;
                }

                if (bodyCode == Abrupt && !Continues(frame))
                {
                    var completion = frame.Abrupt;
                    if (completion.Type == CompletionType.Break && string.Equals(completion.Target, _statement.LabelSet?.Name, StringComparison.Ordinal))
                    {
                        return Normal;
                    }

                    return Abrupt;
                }
            }

            if (header is not null)
            {
                counted = header.Update(frame, out counter);
                if (!counted)
                {
                    update!.EvaluateDiscarded(frame);
                }
            }
            else
            {
                update?.EvaluateDiscarded(frame);
            }
        }
    }

    /// <summary>
    /// The rest of the kernel's outermost loop run as code from this loop's head. A loop inside it then finishes the
    /// outermost loop for the tree around, which only passes <see cref="KernelStatement.Finished"/> on; an exception out of the code is
    /// marked as escaping, so that no block of the tree around takes it for one of its own statements'.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int EnterCode(KernelFrame frame, KernelCode code)
    {
        if (_entry == 0)
        {
            return code.Run(frame, 0);
        }

        try
        {
            frame.FinishedCode = code.Run(frame, _entry);
            return Finished;
        }
        catch (Exception) when (Escape(frame))
        {
            throw;
        }
    }

    private static bool Escape(KernelFrame frame)
    {
        frame.Escaping = true;
        return false;
    }

    /// <summary>
    /// What the loop does with the abrupt completion of its body in <see cref="KernelFrame.Abrupt"/>: 0 to go on with
    /// the update (a labelled continue that names this loop), 1 to complete normally (a labelled break that names it),
    /// 2 to complete with it.
    /// </summary>
    internal int AbruptOutcome(KernelFrame frame)
    {
        if (Continues(frame))
        {
            return 0;
        }

        var completion = frame.Abrupt;
        return completion.Type == CompletionType.Break && string.Equals(completion.Target, _statement.LabelSet?.Name, StringComparison.Ordinal) ? 1 : 2;
    }

    /// <summary>A labelled continue that names this loop.</summary>
    private bool Continues(KernelFrame frame)
    {
        var completion = frame.Abrupt;
        return completion.Type == CompletionType.Continue && _owner is not null && _owner.ContinuesWith(completion.Target!);
    }
}

/// <summary>
/// The test and the update of a loop that counts a name: a test <c>counter &lt;op&gt; limit</c> comparing it with a
/// Number literal, a name or the <c>length</c> of a name, and an update <c>++counter</c>, <c>counter++</c>,
/// <c>--counter</c>, <c>counter--</c> or <c>counter += literal</c> (<c>-=</c>). Each is taken in place, without a
/// node per operand, in the cases where its nodes complete with a Number: the counter's slot holds a Number (and, for
/// the update, is a mutable binding), the limit is a Number, an array's length, or a typed array's length its node has
/// found the built-in getter answers. Every other case is left to the nodes, which repeat nothing observable: the
/// test is pure and a case the update does not take has done nothing yet.
/// </summary>
internal sealed class KernelLoopHeader
{
    private const int LimitNumber = 0;
    private const int LimitName = 1;
    private const int LimitLength = 2;

    private readonly KernelRoot _root;
    private readonly Acornima.Operator _operator;
    private readonly KernelName _counter;
    private readonly int _limitKind;
    private readonly double _limitNumber;
    private readonly KernelName? _limitName;
    private readonly KernelProperty? _limitProperty;
    private readonly int _counterBinding;
    private readonly double _step;

    private KernelLoopHeader(KernelRoot root, KernelBinary binary, KernelName counter, int limitKind, double limitNumber, KernelName? limitName, KernelProperty? limitProperty, double step)
    {
        _root = root;
        _operator = binary._operator;
        _counter = counter;
        _counterBinding = counter._binding;
        _limitKind = limitKind;
        _limitNumber = limitNumber;
        _limitName = limitName;
        _limitProperty = limitProperty;
        _step = step;
    }

    internal static KernelLoopHeader? TryCreate(KernelExpression? test, KernelExpression? update)
    {
        if (test is not KernelRoot { _pure: KernelBinary { _isComparison: true, _leftName: { } counter } binary } root)
        {
            return null;
        }

        double step;
        switch (update)
        {
            case KernelUpdateSlot slot when slot._target._binding == counter._binding:
                step = slot._change;
                break;
            case KernelCompoundSlot { _operation: { _operator: Acornima.Operator.Addition or Acornima.Operator.Subtraction, _rightNumber: { } amount } operation } compound
                when compound._target._binding == counter._binding:
                step = operation._operator == Acornima.Operator.Addition ? amount._value : -amount._value;
                break;
            default:
                return null;
        }

        switch (binary._right)
        {
            case KernelNumber number:
                return new KernelLoopHeader(root, binary, counter, LimitNumber, number._value, null, null, step);
            case KernelName name:
                return new KernelLoopHeader(root, binary, counter, LimitName, 0, name, null, step);
            case KernelProperty { _isLength: true, Object: KernelName owner } property:
                return new KernelLoopHeader(root, binary, counter, LimitLength, 0, owner, property, step);
            default:
                return null;
        }
    }

    /// <summary>
    /// The test: 1 or 0, or -1 for a case left to its node. <paramref name="counted"/> says that the update just stored
    /// <paramref name="counter"/>, which the counter then reads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Test(KernelFrame frame, bool counted, double counter)
    {
        if (_root._interpretOnly || (!counted && !_counter.TryReadNumber(frame, out counter)))
        {
            return -1;
        }

        double limit;
        switch (_limitKind)
        {
            case LimitNumber:
                limit = _limitNumber;
                break;
            case LimitName:
                if (!_limitName!.TryReadNumber(frame, out limit))
                {
                    return -1;
                }

                break;
            default:
                if (!TryReadLength(frame, out limit))
                {
                    return -1;
                }

                break;
        }

        _root.Completed();
        return _operator switch
        {
            Acornima.Operator.LessThan => counter < limit,
            Acornima.Operator.LessThanOrEqual => counter <= limit,
            Acornima.Operator.GreaterThan => counter > limit,
            Acornima.Operator.GreaterThanOrEqual => counter >= limit,
            Acornima.Operator.Equality or Acornima.Operator.StrictEquality => counter == limit,
            _ => counter != limit,
        } ? 1 : 0;
    }

    /// <summary>The length its node reads, where it is an array's or a fixed-length typed array's the node has cached the getter of.</summary>
    private bool TryReadLength(KernelFrame frame, out double length)
    {
        var target = _limitName!.Evaluate(frame, out _);
        if (target is JsTypedArray typedArray)
        {
            if (_limitProperty!.CachedTypedArrayLengthHolds(typedArray)
                && typedArray._viewedArrayBuffer is { _arrayBufferData: not null, _arrayBufferMaxByteLength: null }
                && typedArray._arrayLength != JsTypedArray.LengthAuto)
            {
                length = typedArray._arrayLength;
                return true;
            }
        }
        else if (target is JsArray array)
        {
            length = array.GetLength();
            return true;
        }

        length = 0;
        return false;
    }

    /// <summary>The update of a counter in a mutable binding holding a Number: true with the Number stored; false, having done nothing, otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Update(KernelFrame frame, out double counter)
    {
        ref var binding = ref KernelSlots.Find(frame, _counterBinding, out _);
        if (!Unsafe.IsNullRef(ref binding) && binding.TryGetMutableNumber(out var value))
        {
            counter = value + _step;
            Environments.Binding.StoreMutableNumber(ref binding, counter);
            return true;
        }

        counter = 0;
        return false;
    }
}
