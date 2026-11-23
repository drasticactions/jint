using System.Runtime.CompilerServices;
using Jint.Native;
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
        var context = frame.Context;
        context.PrepareFor(_statement);
        context.RunBeforeExecuteStatementChecks(_statement);
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

    public KernelExpressionStatement(Statement statement, KernelExpression expression) : base(statement)
    {
        _expression = expression;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        _expression.EvaluateDiscarded(frame);
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

    public KernelIfStatement(Statement statement, KernelExpression test, KernelStatement consequent, KernelStatement? alternate) : base(statement)
    {
        _test = test;
        _consequent = consequent;
        _alternate = alternate;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        if (_test.Test(frame) == 1)
        {
            return _consequent.Execute(frame);
        }

        return _alternate?.Execute(frame) ?? Normal;
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

    public KernelBlockStatement(Statement statement, KernelStatement[] statements) : base(statement)
    {
        _statements = statements;
        _checked = statements.Length != 1;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        var statements = _statements;
        if (_checked)
        {
            RunChecks(frame);
        }

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
            var statement = statements[i]._statement;
            var context = frame.Context;
            if (ex is JintException)
            {
                frame.Abrupt = JintStatementList.HandleStatementException(context, ex, statement);
                return Abrupt;
            }

            var engine = context.Engine;
            var location = engine.HasLastSyntaxElement ? engine.LastSyntaxLocation : statement.Location;
            ExceptionDataHelper.TryAttachJavaScriptLocation(ex, engine, location);
            throw;
        }

        return Normal;
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
    }

    // The kernel this loop belongs to, and the entry of the kernel's code at this loop's head (see KernelCode).
    internal LoopKernel _kernel = null!;
    internal int _entry;

    internal override int Execute(KernelFrame frame)
    {
        // The outermost loop runs as code once the kernel has code; a loop inside it is entered at its head.
        if (_entry == 0 && _kernel._code is { } compiled && frame.CodeThreshold >= 0 && !frame.InCode)
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
        while (true)
        {
            // An iteration about to begin can as well begin in the kernel's code: everything the tree has done is in
            // the environments.
            _kernel._iterations++;
            if (frame.CodeThreshold >= 0 && _kernel.WorthCompiling(frame) && !frame.InCode && _kernel.Compile() is { } code)
            {
                return EnterCode(frame, code);
            }

            if (test is not null && test.Test(frame) != 1)
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

            update?.EvaluateDiscarded(frame);
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
