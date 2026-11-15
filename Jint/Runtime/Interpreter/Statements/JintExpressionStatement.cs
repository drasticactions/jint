using Jint.Native;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Statements;

internal sealed class JintExpressionStatement : JintStatement<ExpressionStatement>
{
    private readonly JintExpression _expression;

    // identifiers are queried the most
    private readonly JintIdentifierExpression? _identifierExpression;

    // only node types with a non-materializing discard path divert; gating here keeps
    // every other statement on the exact same call sequence as before
    private readonly bool _expressionCanDiscard;

    public JintExpressionStatement(ExpressionStatement statement) : base(statement)
    {
        _expression = JintExpression.Build(statement.Expression);
        _identifierExpression = _expression as JintIdentifierExpression;
        _expressionCanDiscard = _expression.HasDiscardFastPath;
    }

    /// <summary>
    /// Called by the statement list or block that runs this statement directly: a <c>yield</c> making up the whole
    /// statement may then hand a return() resumption back as a value, since that runner tests the request next.
    /// </summary>
    internal void MarkStatementPosition(YieldStatementPosition position)
    {
        if (_expression is JintYieldExpression yieldExpression)
        {
            yieldExpression.MarkStatementPosition(position);
        }
    }

    protected override Completion ExecuteInternal(EvaluationContext context)
    {
        // generators/async functions surface suspended values through completions,
        // so elision only applies in plain synchronous frames
        if (!_expressionCanDiscard
            || context.CompletionValuesObservable
            || context.Engine.ExecutionContext.Suspendable is not null)
        {
            var value = _identifierExpression is not null
                ? _identifierExpression.GetValue(context)
                : _expression.GetValue(context);

            return new Completion(CompletionType.Normal, value, _statement);
        }

        _expression.EvaluateAndDiscard(context);
        return new Completion(CompletionType.Normal, JsValue.Undefined, _statement);
    }

    /// <summary>
    /// <see cref="JintStatement.Execute"/> for a statement list running in a frame that cannot suspend (no generator or
    /// async function in its execution context): the same checks and evaluation, with the value of the Normal
    /// completion the statement always ends with returned on its own.
    /// </summary>
    internal JsValue ExecuteWithoutSuspendable(EvaluationContext context)
    {
        context.PrepareFor(_statement);
        context.RunBeforeExecuteStatementChecks(_statement);

        if (_expressionCanDiscard && !context.CompletionValuesObservable)
        {
            _expression.EvaluateAndDiscard(context);
            return JsValue.Undefined;
        }

        return _identifierExpression is not null
            ? _identifierExpression.GetValue(context)
            : _expression.GetValue(context);
    }

    /// <summary>
    /// Tight-loop entry: evaluates the expression for side effects only, skipping the
    /// per-statement ceremony (PrepareFor, constraint checks, Completion construction).
    /// Callers guarantee a non-suspendable frame, no constraint/debug checks and dead
    /// completion values; expression evaluation still tracks the current node for errors.
    /// </summary>
    internal override void ExecuteDiscarded(EvaluationContext context)
    {
        context.ChargeStatement();

        if (_expressionCanDiscard)
        {
            _expression.EvaluateAndDiscard(context);
        }
        else
        {
            _expression.GetValue(context);
        }
    }
}
