using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// An optional chain (https://tc39.es/ecma262/#sec-optional-chaining-evaluation), which a <c>?.</c> link of a nullish base
/// ends with undefined; pure when its links are.
/// </summary>
internal sealed class KernelChain : KernelExpression
{
    /// <summary>What a link answers when the chain ends early.</summary>
    internal static readonly JsValue ShortCircuit = new ShortCircuitValue();

    internal readonly KernelExpression _inner;

    public KernelChain(ChainExpression expression, KernelExpression inner) : base(expression)
    {
        _inner = inner;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var value = _inner.Evaluate(frame, out number);
        return ReferenceEquals(value, ShortCircuit) ? JsValue.Undefined : value;
    }

    private sealed class ShortCircuitValue : JsValue
    {
        public ShortCircuitValue() : base(Types.Empty)
        {
        }

        public override object? ToObject() => null;
    }
}

/// <summary>A member link of an optional chain over a pure object, read purely; anything else answers Bail.</summary>
internal sealed class KernelChainMember : KernelExpression
{
    internal readonly KernelExpression _object;
    private readonly KernelProperty? _property;
    private readonly KernelExpression? _key;
    private readonly bool _optional;

    public KernelChainMember(MemberExpression expression, KernelExpression obj, KernelProperty? property, KernelExpression? key) : base(expression)
    {
        _object = obj;
        _property = property;
        _key = key;
        _optional = expression.Optional;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        var target = _object.Evaluate(frame, out var targetNumber);
        if (ReferenceEquals(target, Bail) || ReferenceEquals(target, KernelChain.ShortCircuit))
        {
            return target;
        }

        target ??= JsNumber.Create(targetNumber);
        if (_optional && target.IsNullOrUndefined())
        {
            return KernelChain.ShortCircuit;
        }

        if (_property is not null)
        {
            return _property.Read(target, out number, frame.Engine);
        }

        var key = _key!.Evaluate(frame, out var keyNumber);
        return ReferenceEquals(key, Bail) ? Bail : KernelElement.Read(target, key, keyNumber, out number);
    }
}

/// <summary>The base of a call an optional chain makes, set by the chain just before the call reads it.</summary>
internal sealed class KernelHeld : KernelExpression
{
    internal JsValue? _value;

    public KernelHeld(Expression expression) : base(expression)
    {
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        return _value;
    }
}

/// <summary>
/// The call that ends an optional chain, of a callee read purely: a nullish base of a <c>?.</c> member, or a nullish
/// callee of a <c>?.()</c>, ends the chain before an argument runs; the call is made as <see cref="KernelCall"/> makes it.
/// </summary>
internal sealed class KernelChainCall : KernelExpression
{
    private readonly KernelExpression? _object;
    private readonly bool _memberOptional;
    private readonly bool _callOptional;
    private readonly KernelProperty? _property;
    private readonly KernelExpression? _key;
    private readonly KernelExpression? _callee;
    private readonly KernelHeld? _held;
    internal readonly KernelCall _call;
    private readonly Expression _chain;
    private JintExpression? _handler;

    // A chain whose reads keep handing it back (a host object's, say) stays on the interpreter.
    private int _handBacks;
    private const int MaxHandBacks = 64;

    public KernelChainCall(ChainExpression chain, CallExpression call, KernelExpression? obj, KernelProperty? property, KernelExpression? key, KernelExpression? callee, KernelHeld? held, KernelCall inner) : base(call)
    {
        _chain = chain;
        _object = obj;
        _memberOptional = call.Callee is MemberExpression { Optional: true };
        _callOptional = call.Optional;
        _property = property;
        _key = key;
        _callee = callee;
        _held = held;
        _call = inner;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        if (_handBacks >= MaxHandBacks)
        {
            return Interpret(frame);
        }

        if (_object is null)
        {
            if (_callOptional)
            {
                var callee = _callee!.Evaluate(frame, out _);
                if (ReferenceEquals(callee, Bail))
                {
                    return HandBack(frame);
                }

                if (callee is not null && callee.IsNullOrUndefined())
                {
                    return KernelChain.ShortCircuit;
                }
            }

            return _call.Evaluate(frame, out number);
        }

        var target = _object.Evaluate(frame, out var targetNumber);
        if (ReferenceEquals(target, Bail))
        {
            return HandBack(frame);
        }

        if (ReferenceEquals(target, KernelChain.ShortCircuit))
        {
            return target;
        }

        target ??= JsNumber.Create(targetNumber);
        if (_memberOptional && target.IsNullOrUndefined())
        {
            return KernelChain.ShortCircuit;
        }

        if (_callOptional)
        {
            JsValue? callee;
            if (_property is not null)
            {
                callee = _property.Read(target, out var calleeNumber, frame.Engine);
                callee ??= JsNumber.Create(calleeNumber);
            }
            else
            {
                var key = _key!.Evaluate(frame, out var keyNumber);
                callee = ReferenceEquals(key, Bail) ? Bail : KernelElement.Read(target, key, keyNumber, out var calleeNumber) ?? JsNumber.Create(calleeNumber);
            }

            if (ReferenceEquals(callee, Bail))
            {
                return HandBack(frame);
            }

            if (callee.IsNullOrUndefined())
            {
                return KernelChain.ShortCircuit;
            }
        }

        var held = _held!;
        held._value = target;
        var result = _call.Evaluate(frame, out number);
        held._value = null;
        return result;
    }

    private JsValue HandBack(KernelFrame frame)
    {
        _handBacks++;
        return Interpret(frame);
    }

    /// <summary>The whole chain on the interpreter, before anything observable ran.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue Interpret(KernelFrame frame) => (_handler ??= JintExpression.Build(_chain)).GetValue(frame.Context);
}
