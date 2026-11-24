using System.Runtime.CompilerServices;
using Jint.Native;

namespace Jint.Runtime.Interpreter.Expressions;

/// <summary>
/// Probes the native stack, then evaluates the node it stands in for. Built only into a chain deeper than
/// <see cref="SyntaxNesting.EvaluationProbeInterval"/>, one per that many levels: an operator chain, a chain of
/// member accesses, calls, tagged templates and <c>new</c> expressions, and a chain of conditional operators down
/// their alternates. An expression of ordinary depth never contains one and evaluates exactly as it did.
/// </summary>
/// <remarks>
/// <para>
/// Evaluating <c>a || b || …</c> recurses once per operand, and the hoisting walk does not count those levels
/// against its fixed limit (see <see cref="SyntaxNesting"/>), so the chain has to bound itself. A node is built
/// where it first runs, which need not be where it runs later: a function built at the top of the stack can run
/// again from the bottom of a deep recursion, with less stack left than a long chain needs.
/// </para>
/// <para>
/// It probes the way a call expression does. With <c>MaxExecutionStackCount</c> set, a stack that runs low moves
/// the evaluation to a fresh one; otherwise the <c>StackOverflowGuard</c> backstop throws the catchable
/// <c>RangeError</c> the rest of the interpreter throws.
/// </para>
/// <para>
/// It forwards every evaluation entry, under the node it wraps. A parent can tell it apart only by type, and gets
/// one only for an operand that itself has an operator operand, so the lanes that type-test an operand for an
/// identifier, a constant, a member read or <c>x % c</c> (<c>ModuloEqualityLane</c>, <c>BoxedNumericOperandLane</c>,
/// the comparison lanes) still match. <c>SumOfProductsLane</c> follows a sum's left operands and declines a sum
/// with a probe in it, which takes a sum nested exactly at a multiple of the interval, and costs only that lane.
/// </para>
/// <para>
/// In a chain of member accesses and calls it stands between a link and its base, and hands the base's result on
/// as it is: a <c>Reference</c>, which a call takes its <c>this</c> value from, and an optional chain's short
/// circuit, which <see cref="EnableShortCircuitPropagation"/> asks the base to produce. It is never put between a
/// call and the member access it calls, whose node the call reads the receiver through (see
/// <see cref="SyntaxNesting.SubscriptProbeInterval"/>).
/// </para>
/// <para>
/// A <c>+</c> chain that <c>JintBinaryExpression</c> flattens into one node evaluates its operands in a loop, not
/// one level each, but the walk still charged them a level each; so a flattened chain whose nested form would have
/// carried a probe is wrapped whole (<see cref="Around"/>), and probes once before its loop.
/// </para>
/// </remarks>
internal sealed class JintStackProbeExpression : JintExpression
{
    private readonly JintExpression _operator;

    private JintStackProbeExpression(JintExpression wrapped) : base(wrapped._expression)
    {
        _operator = wrapped;
    }

    /// <summary>
    /// <paramref name="wrapped"/>, behind a probe.
    /// </summary>
    internal static JintExpression Around(JintExpression wrapped) => new JintStackProbeExpression(wrapped);

    /// <summary>
    /// Builds <paramref name="operand"/> of an operator, behind a probe where <see cref="AtProbeDepth"/> puts one.
    /// </summary>
    /// <remarks>
    /// An operand that is itself an operator is built here rather than through <c>JintExpression.Build</c>, whose
    /// frame is sized for every kind of node: building a chain recurses once per level, and those frames are what a
    /// deep chain runs out of stack on while it is built. The result is the node <c>Build</c> would have made.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static JintExpression BuildOperand(Expression operand)
    {
        if (operand is not BinaryExpression binary || operand.UserData is JintExpression)
        {
            return AtProbeDepth(operand, Build(operand));
        }

        JintExpression built;
        using (SyntaxNesting.Enter())
        {
            built = binary is NonLogicalBinaryExpression nonLogical
                ? JintBinaryExpression.Build(nonLogical)
                : BuildLogical((LogicalExpression) binary);
        }

        return AtProbeDepth(operand, built);
    }

    private static JintExpression BuildLogical(LogicalExpression expression) => expression.Operator switch
    {
        Operator.LogicalAnd => new JintLogicalAndExpression(expression),
        Operator.LogicalOr => new JintLogicalOrExpression(expression),
        Operator.NullishCoalescing => new NullishCoalescingExpression(expression),
        _ => Build(expression),
    };

    /// <summary>
    /// Builds the object of a member access, or the callee or tag of a call, tagged template or <c>new</c>
    /// expression, behind a probe where the chain it continues gets one: below a level at a depth the probe
    /// interval divides, or below a member access one level under that, which takes the probe of a call or tagged
    /// template that cannot put one around the member access it calls. Only a base the chain continues below gets
    /// one: a member access, call, tagged template or <c>new</c> expression whose own base is one too.
    /// </summary>
    /// <param name="chainBase">The base to build.</param>
    /// <param name="ofMember">Whether the node being built is a member access, which takes the shifted probe.</param>
    /// <param name="takesReceiver">Whether the node being built is a call or tagged template, which reads its
    /// <c>this</c> value through a member access it calls and so cannot have a probe in front of one.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static JintExpression BuildSubscriptBase(Expression chainBase, bool ofMember, bool takesReceiver)
    {
        var built = Build(chainBase);

        var probes = SyntaxNesting.IsEvaluationProbeDepth
            ? !takesReceiver || chainBase is not (MemberExpression or ChainExpression { Expression: MemberExpression })
            : ofMember && SyntaxNesting.IsShiftedSubscriptProbeDepth;

        if (probes
            && SyntaxNesting.ContinuesSubscriptChain(chainBase)
            && built is JintMemberExpression or JintCallExpression or JintTaggedTemplateExpression or JintNewExpression or JintSuspendableNewExpression)
        {
            return new JintStackProbeExpression(built);
        }

        return built;
    }

    /// <summary>
    /// Builds the alternate of a conditional operator, behind a probe when the conditional sits at a depth the probe
    /// interval divides and the alternate continues a chain of conditionals below it (<c>a ? b : c ? d : e ? …</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static JintExpression BuildAlternate(Expression alternate)
    {
        var built = Build(alternate);
        if (SyntaxNesting.IsEvaluationProbeDepth
            && alternate is ConditionalExpression { Alternate: ConditionalExpression }
            && built is JintConditionalExpression)
        {
            return new JintStackProbeExpression(built);
        }

        return built;
    }

    /// <summary>
    /// Puts <paramref name="built"/>, the operand just built for the operator being built, behind a probe when the
    /// operator sits at a depth the probe interval divides and the operand continues the chain below it: it is an
    /// operator with an operator operand of its own.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static JintExpression AtProbeDepth(Expression operand, JintExpression built)
    {
        if (SyntaxNesting.IsEvaluationProbeDepth
            && operand is BinaryExpression { Left: BinaryExpression } or BinaryExpression { Right: BinaryExpression }
            && built is JintBinaryExpression or JintLogicalOrExpression or JintLogicalAndExpression or NullishCoalescingExpression)
        {
            return new JintStackProbeExpression(built);
        }

        return built;
    }

    public override JsValue GetValue(EvaluationContext context)
    {
        if (MustMoveToFreshStack(context))
        {
            return context.Engine._stackGuard.RunOnEmptyStack(_operator.GetValue, context);
        }

        return _operator.GetValue(context);
    }

    protected override object EvaluateInternal(EvaluationContext context)
    {
        if (MustMoveToFreshStack(context))
        {
            return context.Engine._stackGuard.RunOnEmptyStack(_operator.EvaluateWithoutNodeTracking, context);
        }

        return _operator.EvaluateWithoutNodeTracking(context);
    }

    public override bool GetBooleanValue(EvaluationContext context)
    {
        if (MustMoveToFreshStack(context))
        {
            return context.Engine._stackGuard.RunOnEmptyStack(_operator.GetBooleanValue, context);
        }

        return _operator.GetBooleanValue(context);
    }

    internal override bool HasDiscardFastPath => _operator.HasDiscardFastPath;

    internal override void EvaluateAndDiscard(EvaluationContext context)
    {
        if (MustMoveToFreshStack(context))
        {
            // The value is discarded either way; GetValue is EvaluateAndDiscard's own default.
            context.Engine._stackGuard.RunOnEmptyStack(_operator.GetValue, context);
            return;
        }

        _operator.EvaluateAndDiscard(context);
    }

    internal override void EnableShortCircuitPropagation() => _operator.EnableShortCircuitPropagation();

    /// <summary>
    /// <see cref="JintCallExpression"/>'s probe: whether the stack-hopping lane wants a fresh stack, or, with the lane
    /// off, the backstop, which throws if the stack is too low.
    /// </summary>
    /// <remarks>
    /// With the lane on, its answer is the runtime's for this very stack, so the backstop is not asked as well: it
    /// probes from a frame further down, and on a stack whose reserve ends between the two it threw where the lane
    /// had just decided to stay — which of them a recursion meets first moved with the JIT's frame sizes.
    /// </remarks>
    internal static bool MustMoveToFreshStack(EvaluationContext context)
    {
        var stackGuard = context.Engine._stackGuard;
        if (stackGuard.HopsToFreshStack)
        {
            return !stackGuard.TryEnterOnCurrentStack();
        }

        stackGuard.EnsureNativeStackHeadroom();
        return false;
    }
}
