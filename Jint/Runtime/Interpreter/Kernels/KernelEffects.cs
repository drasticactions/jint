using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// The slot a kernel name resolved to, for the stores: the binding is reached only while the resolution holds (the
/// environment is there and nothing has injected a binding since the kernel was entered).
/// </summary>
internal static class KernelSlots
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref Binding Find(KernelFrame frame, int binding, out DeclarativeEnvironment? environment)
    {
        ref var slot = ref frame.Slots[binding];
        environment = slot.Environment;
        if (environment is not null && slot.Epoch == frame.Engine._envBindingInjectionEpoch)
        {
            var slots = environment._slots;
            if (slots is not null && (uint) slot.Index < (uint) slots.Length)
            {
                return ref slots[slot.Index];
            }
        }

        return ref FindResolving(frame, binding, out environment);
    }

    /// <summary>A name not resolved in this run yet, or resolved before a binding was injected somewhere: resolved now.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ref Binding FindResolving(KernelFrame frame, int binding, out DeclarativeEnvironment? environment)
    {
        if (frame.NeedsResolving(binding))
        {
            frame.Resolve(binding);
            ref var slot = ref frame.Slots[binding];
            environment = slot.Environment;
            if (environment?._slots is { } slots && (uint) slot.Index < (uint) slots.Length)
            {
                return ref slots[slot.Index];
            }
        }

        environment = null;
        return ref Unsafe.NullRef<Binding>();
    }
}

/// <summary>
/// A kernel expression with effects, which leaves a case it does not handle to the interpreter's handler of the whole
/// expression before it has evaluated anything; in statement position that handler runs as the interpreter runs an
/// expression statement, through its discarding lane when it has one (an append to a string a binding owns extends it).
/// </summary>
internal abstract class KernelEffect : KernelExpression
{
    private JintExpression? _handler;

    protected KernelEffect(Expression expression) : base(expression)
    {
    }

    private protected JintExpression Handler => _handler ??= JintExpression.Build(_expression);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue Interpret(KernelFrame frame) => Handler.GetValue(frame.Context);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void InterpretDiscarded(KernelFrame frame)
    {
        var handler = Handler;
        if (handler.HasDiscardFastPath)
        {
            handler.EvaluateAndDiscard(frame.Context);
        }
        else
        {
            handler.GetValue(frame.Context);
        }
    }
}

/// <summary>
/// <c>name = value</c>, or the initializer of <c>var name = value</c>, into a slot
/// (https://tc39.es/ecma262/#sec-assignment-operators-runtime-semantics-evaluation): the binding is resolved before the
/// right-hand side runs and re-validated after it, as SimpleAssignmentExpression.TryAssignSlot does. A binding the
/// kernel cannot reach, a const and an uninitialized binding leave the whole assignment to the interpreter before
/// anything is evaluated. The right-hand side is never an anonymous function or class, so nothing is named.
/// </summary>
internal sealed class KernelAssignSlot : KernelEffect
{
    internal readonly KernelName _target;
    internal readonly KernelExpression _value;
    internal readonly Expression _valueExpression;
    internal readonly bool _isDeclarator;
    private JintExpression? _initializerHandler;
    private JintIdentifierExpression? _targetHandler;

    public KernelAssignSlot(Expression expression, KernelName target, KernelExpression value, bool isDeclarator) : base(expression)
    {
        _target = target;
        _value = value;
        _valueExpression = value._expression;
        _isDeclarator = isDeclarator;
        _valueKind = value is KernelRoot root && root._pureKind is KernelOperand.Element ? KernelOperand.Root : KernelOperand.Other;
    }

    // How KernelOperand reads a value that is an element read purely, until it has read too many that are not Numbers.
    private byte _valueKind;
    private int _valueMisses;

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        ref var binding = ref KernelSlots.Find(frame, _target._binding, out var environment);
        if (Unsafe.IsNullRef(ref binding) || !binding.IsAssignable)
        {
            number = 0;
            return _isDeclarator ? InterpretDeclarator(frame) : Interpret(frame);
        }

        return Store(frame, ref binding, environment!, out number);
    }

    internal override void EvaluateDiscarded(KernelFrame frame)
    {
        ref var binding = ref KernelSlots.Find(frame, _target._binding, out var environment);
        if (Unsafe.IsNullRef(ref binding) || !binding.IsAssignable)
        {
            if (_isDeclarator)
            {
                InterpretDeclarator(frame);
            }
            else
            {
                InterpretDiscarded(frame);
            }

            return;
        }

        Store(frame, ref binding, environment!, out _);
    }

    private JsValue? Store(KernelFrame frame, ref Binding binding, DeclarativeEnvironment environment, out double number)
    {
        JsValue? value = null;
        if (_valueKind == KernelOperand.Other || !TryNumberValue(frame, out number))
        {
            value = _value.Evaluate(frame, out number);
        }

        if (binding.IsAssignable)
        {
            if (value is null)
            {
                Binding.StoreUnboxedNumber(ref binding, number);
            }
            else
            {
                binding = binding.ChangeMutableValue(value);
            }
        }
        else
        {
            // degenerate: the right-hand side changed the binding's state; the full store produces the exact semantics
            environment.SetMutableBinding(_target._name, Box(value, number), frame.Engine.ExecutionContext.Strict);
        }

        return value;
    }

    /// <summary>A value read without its nodes; one that is not a Number is read again by them, which repeats nothing observable.</summary>
    private bool TryNumberValue(KernelFrame frame, out double number)
    {
        if (KernelOperand.TryNumber(_value, _valueKind, frame, out number))
        {
            _valueMisses = 0;
            return true;
        }

        if (++_valueMisses >= 16)
        {
            _valueKind = KernelOperand.Other;
        }

        return false;
    }

    /// <summary>What JintVariableDeclaration does for a declarator whose initializer needs no name.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue InterpretDeclarator(KernelFrame frame)
    {
        var context = frame.Context;
        var target = _targetHandler ??= new JintIdentifierExpression((Identifier) _target._expression);
        var initializer = _initializerHandler ??= JintExpression.Build(_valueExpression);
        if (JintAssignmentExpression.SimpleAssignmentExpression.AssignToIdentifier(context, target, initializer, hasEvalOrArguments: false) is JsValue assigned)
        {
            return assigned;
        }

        var engine = context.Engine;
        var reference = (Reference) target.Evaluate(context);
        reference.AssertValid(engine.Realm);
        var value = initializer.GetValue(context);
        engine.PutValue(reference, value);
        engine._referencePool.Return(reference);
        return value;
    }
}

/// <summary>
/// <c>name++</c>, <c>++name</c>, <c>name--</c> and <c>--name</c> on a slot holding a Number; anything else (a value
/// to convert, a const, an uninitialized binding) is left to the interpreter, which reads the binding again.
/// </summary>
internal sealed class KernelUpdateSlot : KernelEffect
{
    internal readonly KernelName _target;
    internal readonly double _change;
    internal readonly bool _prefix;

    public KernelUpdateSlot(UpdateExpression expression, KernelName target) : base(expression)
    {
        _target = target;
        _change = expression.Operator == Operator.Increment ? 1 : -1;
        _prefix = expression.Prefix;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number) => TryRun(frame, out number) ? null : Interpret(frame);

    internal override void EvaluateDiscarded(KernelFrame frame)
    {
        if (!TryRun(frame, out _))
        {
            InterpretDiscarded(frame);
        }
    }

    private bool TryRun(KernelFrame frame, out double number)
    {
        ref var binding = ref KernelSlots.Find(frame, _target._binding, out _);
        if (!Unsafe.IsNullRef(ref binding) && binding.TryGetMutableNumber(out var value))
        {
            var updated = value + _change;
            Binding.StoreMutableNumber(ref binding, updated);
            number = _prefix ? updated : value;
            return true;
        }

        number = 0;
        return false;
    }
}

/// <summary>
/// <c>name op= value</c> for an arithmetic or bitwise operator, with a pure right-hand side, on a slot holding a
/// Number: two Numbers are combined and stored; anything else is left to the interpreter, which reads the binding and
/// evaluates the right-hand side again, unobservably.
/// </summary>
internal sealed class KernelCompoundSlot : KernelEffect
{
    internal readonly KernelName _target;
    internal readonly KernelBinary _operation;

    public KernelCompoundSlot(Expression expression, KernelName target, KernelBinary operation) : base(expression)
    {
        _target = target;
        _operation = operation;
    }

    internal static Operator? BinaryOperator(Operator op) => op switch
    {
        Operator.AdditionAssignment => Operator.Addition,
        Operator.SubtractionAssignment => Operator.Subtraction,
        Operator.MultiplicationAssignment => Operator.Multiplication,
        Operator.DivisionAssignment => Operator.Division,
        Operator.RemainderAssignment => Operator.Remainder,
        Operator.BitwiseAndAssignment => Operator.BitwiseAnd,
        Operator.BitwiseOrAssignment => Operator.BitwiseOr,
        Operator.BitwiseXorAssignment => Operator.BitwiseXor,
        Operator.LeftShiftAssignment => Operator.LeftShift,
        Operator.RightShiftAssignment => Operator.RightShift,
        Operator.UnsignedRightShiftAssignment => Operator.UnsignedRightShift,
        _ => null,
    };

    internal override JsValue? Evaluate(KernelFrame frame, out double number) => TryRun(frame, out number) ? null : Interpret(frame);

    internal override void EvaluateDiscarded(KernelFrame frame)
    {
        if (!TryRun(frame, out _))
        {
            InterpretDiscarded(frame);
        }
    }

    private bool TryRun(KernelFrame frame, out double number)
    {
        ref var binding = ref KernelSlots.Find(frame, _target._binding, out _);
        if (!Unsafe.IsNullRef(ref binding) && binding.TryGetMutableNumber(out _))
        {
            // The operation reads the binding through its own left operand, the same kernel name.
            var value = _operation.Evaluate(frame, out number);
            if (value is null)
            {
                Binding.StoreMutableNumber(ref binding, number);
                return true;
            }
        }

        number = 0;
        return false;
    }
}

/// <summary>
/// <c>object[key] = value</c> with pure operands: a Number stored into a typed array of Numbers with a Number key
/// (https://tc39.es/ecma262/#sec-typedarraysetelement, which converts the value and then stores it if the index is
/// valid). Anything else is left to the interpreter, which evaluates the operands again, unobservably.
/// </summary>
internal sealed class KernelStoreElement : KernelEffect
{
    internal readonly KernelExpression _object;
    internal readonly KernelExpression _key;
    internal readonly KernelExpression _value;

    // The object a loop stores into most, a name, read without a call.
    internal readonly KernelName? _objectName;

    public KernelStoreElement(Expression expression, KernelExpression obj, KernelExpression key, KernelExpression value) : base(expression)
    {
        _object = obj;
        _key = key;
        _value = value;
        _objectName = obj as KernelName;
        _keyKind = KernelOperand.KindOf(key);
        _valueKind = KernelOperand.KindOf(value);
    }

    // How KernelOperand reads the key and the value.
    internal readonly byte _keyKind;
    internal readonly byte _valueKind;

    internal override JsValue? Evaluate(KernelFrame frame, out double number) => TryRun(frame, out number) ? null : Interpret(frame);

    internal override void EvaluateDiscarded(KernelFrame frame)
    {
        if (!TryRun(frame, out _))
        {
            InterpretDiscarded(frame);
        }
    }

    private bool TryRun(KernelFrame frame, out double number)
    {
        var target = _objectName is not null ? _objectName.Evaluate(frame, out _) : _object.Evaluate(frame, out _);
        if (target is JsTypedArray typedArray)
        {
            // Operands that are not Numbers leave the store to the interpreter.
            if (KernelOperand.TryNumber(_key, _keyKind, frame, out var key)
                && KernelOperand.TryNumber(_value, _valueKind, frame, out number)
                && KernelCodeRuntime.StoreTypedElement(typedArray, key, number))
            {
                return true;
            }
        }

        number = 0;
        return false;
    }
}
