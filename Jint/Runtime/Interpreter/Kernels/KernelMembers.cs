using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Native.TypedArray;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// A pure <c>object[key]</c> with a Number key: an element of a typed array (whose [[Get]] of a Number key never
/// reaches the prototype chain, https://tc39.es/ecma262/#sec-typedarray-get) or a present element of an ordinary
/// array. Anything else, a hole included, answers <see cref="KernelExpression.Bail"/>.
/// </summary>
internal sealed class KernelElement : KernelExpression
{
    private readonly KernelExpression _object;
    private readonly KernelExpression _key;
    private readonly KernelName? _objectName;
    private readonly KernelName? _keyName;

    public KernelElement(Expression expression, KernelExpression obj, KernelExpression key) : base(expression)
    {
        _object = obj;
        _key = key;
        _objectName = obj as KernelName;
        _keyName = key as KernelName;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        var target = _objectName is not null ? _objectName.Evaluate(frame, out _) : _object.Evaluate(frame, out _);
        if (target is null || ReferenceEquals(target, Bail))
        {
            return Bail;
        }

        if (_keyName is null || !_keyName.TryReadNumber(frame, out var key))
        {
            key = 0;
            var keyValue = _key.Evaluate(frame, out key);
            if (!IsNumber(keyValue, ref key))
            {
                return Bail;
            }
        }

        if (target is JsTypedArray typedArray)
        {
            return typedArray.ReadNumberKey(key, out number);
        }

        if (target is JsArray array)
        {
            // -0 is the key "0"; a present dense element is an own data property of the default attributes.
            var index = (uint) key;
            if (index == key && index != uint.MaxValue && array.TryGetValueFast(index, out var element))
            {
                return element;
            }
        }

        return Bail;
    }
}

/// <summary>
/// A pure <c>object.name</c>: an own property of an ordinary object in shape mode, a data property of a built-in
/// object such as <c>Math</c>, the length of an array, or the length of a typed array while its prototype chain
/// answers it with the built-in getter of <c>%TypedArray%.prototype</c>. Anything else answers
/// <see cref="KernelExpression.Bail"/>.
/// </summary>
internal sealed class KernelProperty : KernelExpression
{
    private readonly KernelExpression _object;
    private readonly JsString _name;
    private readonly Key _key;
    private readonly bool _isLength;

    private Shape? _cachedShape;
    private int _cachedSlot;

    // A built-in object's data property: valid while the object's own properties are unchanged; a value write
    // changes the descriptor in place. Built-ins live as long as their realm, so the entry keeps nothing alive.
    private ObjectInstance? _cachedBuiltin;
    private uint _cachedBuiltinVersion;
    private PropertyDescriptor? _cachedBuiltinDescriptor;

    // JintMemberExpression's typed array length cache: the receiver's prototype and the %TypedArray%.prototype above it.
    private ObjectInstance? _typedArrayLengthLink;
    private uint _typedArrayLengthLinkVersion;
    private IntrinsicTypedArrayPrototype? _typedArrayLengthHolder;
    private uint _typedArrayLengthHolderVersion;

    public KernelProperty(Expression expression, KernelExpression obj, string name) : base(expression)
    {
        _object = obj;
        _name = JsString.Create(name);
        _key = name;
        _isLength = string.Equals(name, "length", StringComparison.Ordinal);
    }

    internal KernelExpression Object => _object;

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var target = _object.Evaluate(frame, out _);
        if (target is null || ReferenceEquals(target, Bail))
        {
            number = 0;
            return Bail;
        }

        return Read(target, out number);
    }

    /// <summary>The property of an evaluated base, or <see cref="KernelExpression.Bail"/>.</summary>
    internal JsValue? Read(JsValue target, out double number)
    {
        number = 0;
        if (target is JsObject plain)
        {
            if ((plain._type & (InternalTypes.ShapeMode | InternalTypes.HasLazySlots)) == InternalTypes.ShapeMode)
            {
                var shape = plain.ShapeOf;
                if (ReferenceEquals(shape, _cachedShape))
                {
                    return plain.GetSlot(_cachedSlot);
                }

                if (shape.TryGetSlot(in _key, out var slot))
                {
                    _cachedShape = shape;
                    _cachedSlot = slot;
                    return plain.GetSlot(slot);
                }
            }

            return Bail;
        }

        if (_isLength)
        {
            if (target is JsArray array)
            {
                number = array.GetLength();
                return null;
            }

            if (target is JsTypedArray typedArray)
            {
                return ReadTypedArrayLength(typedArray, out number);
            }
        }

        if (target is ObjectInstance builtin && (builtin._type & InternalTypes.BuiltinShapeMode) != InternalTypes.Empty)
        {
            return ReadBuiltin(builtin);
        }

        return Bail;
    }

    private JsValue ReadBuiltin(ObjectInstance builtin)
    {
        var descriptor = _cachedBuiltinDescriptor;
        if (!ReferenceEquals(builtin, _cachedBuiltin) || builtin._propertiesVersion != _cachedBuiltinVersion)
        {
            _cachedBuiltin = null;
            if (builtin is not BuiltinShapeObject || (builtin._type & InternalTypes.ExoticGet) != InternalTypes.Empty)
            {
                return Bail;
            }

            // Materializing a lazy built-in member runs no script.
            descriptor = builtin.GetOwnProperty(_name);
            if (descriptor == PropertyDescriptor.Undefined
                || !descriptor.IsDataDescriptor()
                || (descriptor._flags & PropertyFlag.CustomJsValue) != PropertyFlag.None)
            {
                return Bail;
            }

            _cachedBuiltin = builtin;
            _cachedBuiltinVersion = builtin._propertiesVersion;
            _cachedBuiltinDescriptor = descriptor;
        }

        return descriptor!._value ?? Bail;
    }

    /// <summary>JintMemberExpression.TryReadTypedArrayLength.</summary>
    private JsValue? ReadTypedArrayLength(JsTypedArray typedArray, out double number)
    {
        number = 0;
        if (typedArray._properties is { } own && own.ContainsKey(_key))
        {
            return Bail;
        }

        var link = typedArray.GetPrototypeOf();
        var holder = _typedArrayLengthHolder;
        if (holder is null
            || !ReferenceEquals(link, _typedArrayLengthLink)
            || link!._propertiesVersion != _typedArrayLengthLinkVersion
            || !ReferenceEquals(link.GetPrototypeOf(), holder)
            || holder._propertiesVersion != _typedArrayLengthHolderVersion)
        {
            _typedArrayLengthLink = null;
            _typedArrayLengthHolder = null;
            if (link is null
                || (link._type & (InternalTypes.ExoticGet | InternalTypes.OwnValueHook)) != InternalTypes.Empty
                || !ObjectInstance.VersionWitnessesOwnProperty(link, _name)
                || !ReferenceEquals(link.GetOwnProperty(_name), PropertyDescriptor.Undefined)
                || link.GetPrototypeOf() is not IntrinsicTypedArrayPrototype intrinsic
                || !ObjectInstance.VersionWitnessesOwnProperty(intrinsic, _name)
                || intrinsic.GetOwnProperty(_name) is not { } descriptor
                || !descriptor.IsAccessorDescriptor()
                || !intrinsic.IsBuiltinLengthGetter(descriptor.Get))
            {
                return Bail;
            }

            _typedArrayLengthLink = link;
            _typedArrayLengthLinkVersion = link._propertiesVersion;
            _typedArrayLengthHolder = intrinsic;
            _typedArrayLengthHolderVersion = intrinsic._propertiesVersion;
        }

        number = typedArray.LengthNumber();
        return null;
    }
}

/// <summary>
/// A call (https://tc39.es/ecma262/#sec-function-calls-runtime-semantics-evaluation) of a callee read purely, a name
/// or a property, with arguments that spread nothing. The callee is evaluated first; one the kernel does not call
/// itself (a bound function, a host or proxy callable, anything not callable) leaves the whole call to the
/// interpreter's handler, which evaluates the callee again, unobservably, and then the arguments. Otherwise the
/// arguments are evaluated once, in order, and the call is made as JintCallExpression makes it: a built-in with a fast
/// call shape through that lane, without an argument list and, when its shape says the call is a leaf for these
/// values, without a call stack frame; an interpreted function with its call stack frame and a pooled argument list.
/// </summary>
internal sealed class KernelCall : KernelExpression
{
    private readonly KernelExpression? _base;
    private readonly KernelProperty? _member;
    private readonly KernelExpression? _callee;
    private readonly KernelExpression[] _arguments;
    private JintExpression? _handler;
    private JintExpression? _calleeHandler;

    private Function? _fastCallee;
    private FastCallShape _fastShape;

    // JintCallExpression's register lane for an interpreted callee of at most four arguments: the definition last
    // probed, and its state when the lane serves it.
    private JintFunctionDefinition? _registerDefinition;
    private JintFunctionDefinition.State? _registerState;

    public KernelCall(CallExpression expression, KernelProperty? member, KernelExpression? callee, KernelExpression[] arguments) : base(expression)
    {
        _member = member;
        _base = member?.Object;
        _callee = callee;
        _arguments = arguments;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        JsValue thisValue;
        JsValue? callee;
        if (_member is not null)
        {
            var target = _base!.Evaluate(frame, out _);
            if (target is null || ReferenceEquals(target, Bail))
            {
                return Interpret(frame);
            }

            thisValue = target;
            callee = _member.Read(target, out _);
        }
        else
        {
            thisValue = JsValue.Undefined;
            callee = _callee!.Evaluate(frame, out _);
        }

        if (callee is not Function function)
        {
            return Interpret(frame);
        }

        if (function is ScriptFunction script)
        {
            return CallScript(frame, script, thisValue);
        }

        if (!ReferenceEquals(function, _fastCallee))
        {
            if (function._functionDefinition is not null || _arguments.Length > 2)
            {
                return Interpret(frame);
            }

            var shape = function.GetFastCallShape(_arguments.Length);
            if (!shape.Supported || shape.Variadic)
            {
                return Interpret(frame);
            }

            _fastCallee = function;
            _fastShape = shape;
        }

        // Before any argument runs: an argument can call back into this kernel and record another callee here.
        var fastShape = _fastShape;
        var arg0 = _arguments.Length >= 1 ? Box(_arguments[0].Evaluate(frame, out var n0), n0) : JsValue.Undefined;
        var arg1 = _arguments.Length >= 2 ? Box(_arguments[1].Evaluate(frame, out var n1), n1) : JsValue.Undefined;

        if (fastShape.IsLeafFor(thisValue, arg0, arg1))
        {
            return function.CallFast(thisValue, arg0, arg1);
        }

        return CallWithFrame(frame, function, thisValue, arg0, arg1);
    }

    private JsValue CallScript(KernelFrame frame, ScriptFunction function, JsValue thisValue)
    {
        var engine = frame.Engine;
        var arguments = _arguments;
        if (arguments.Length <= 4 && !function._isClassConstructor)
        {
            var definition = function._functionDefinition;
            if (!ReferenceEquals(definition, _registerDefinition))
            {
                // What JintCallExpression.ProbeRegisterCallee records.
                _registerDefinition = definition;
                _registerState = function.TryInitializeDefinition() is { SupportsRegisterCall: true } probed ? probed : null;
            }

            // Before any argument runs: an argument can call back into this kernel and probe another callee here.
            if (_registerState is { } state)
            {
                return CallRegisters(frame, function, thisValue, state);
            }
        }


        var values = arguments.Length == 0 ? [] : engine._jsValueArrayPool.RentArray(arguments.Length);
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i] = Box(arguments[i].Evaluate(frame, out var n), n);
        }

        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);

        var callStack = engine.CallStack;
        var recursionDepth = callStack.Push(function, CalleeHandler, engine.ExecutionContext);
        if (recursionDepth > engine._maxRecursionDepth)
        {
            engine.AbandonImplicitMemoryOperation();
            Throw.RecursionDepthOverflowException(callStack);
        }

        JsValue result;
        try
        {
            result = function.CallWithStackFrame(thisValue, values);
        }
        finally
        {
            callStack.TryPopAndDiscard();
        }

        if (values.Length > 0)
        {
            engine._jsValueArrayPool.ReturnArray(values);
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return result;
    }

    /// <summary>JintCallExpression.RegisterLaneCall: the arguments in locals, the callee's own register entry.</summary>
    private JsValue CallRegisters(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state)
    {
        var arguments = _arguments;
        var count = arguments.Length;
        var arg0 = count >= 1 ? Box(arguments[0].Evaluate(frame, out var n0), n0) : JsValue.Undefined;
        var arg1 = count >= 2 ? Box(arguments[1].Evaluate(frame, out var n1), n1) : JsValue.Undefined;
        var arg2 = count >= 3 ? Box(arguments[2].Evaluate(frame, out var n2), n2) : JsValue.Undefined;
        var arg3 = count >= 4 ? Box(arguments[3].Evaluate(frame, out var n3), n3) : JsValue.Undefined;

        var engine = frame.Engine;
        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);

        var callStack = engine.CallStack;
        var recursionDepth = callStack.Push(function, CalleeHandler, engine.ExecutionContext);
        if (recursionDepth > engine._maxRecursionDepth)
        {
            engine.AbandonImplicitMemoryOperation();
            Throw.RecursionDepthOverflowException(callStack);
        }

        JsValue result;
        try
        {
            result = function.CallFromRegisters(thisValue, arg0, arg1, arg2, arg3, count, state, ownsFrame: true);
        }
        finally
        {
            callStack.TryPopAndDiscard();
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue CallWithFrame(KernelFrame frame, Function function, JsValue thisValue, JsValue arg0, JsValue arg1)
    {
        var engine = frame.Engine;
        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);

        var callStack = engine.CallStack;
        var recursionDepth = callStack.Push(function, CalleeHandler, engine.ExecutionContext);
        if (recursionDepth > engine._maxRecursionDepth)
        {
            engine.AbandonImplicitMemoryOperation();
            Throw.RecursionDepthOverflowException(callStack);
        }

        JsValue result;
        try
        {
            result = function.CallFast(thisValue, arg0, arg1);
        }
        finally
        {
            callStack.TryPopAndDiscard();
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return result;
    }

    // The callee's handler, whose node the call stack frame reports the call at.
    private JintExpression CalleeHandler => _calleeHandler ??= JintExpression.Build((Expression) ((CallExpression) _expression).Callee);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue Interpret(KernelFrame frame) => (_handler ??= JintExpression.Build(_expression)).GetValue(frame.Context);
}

/// <summary>
/// A comparison whose operands are evaluated once each, in order, when one of them is not pure (a call above all):
/// two Numbers compare directly, any other pair as the interpreter's comparison handlers compare evaluated operands.
/// </summary>
internal sealed class KernelComparison : KernelExpression
{
    private readonly Operator _operator;
    private readonly KernelExpression _left;
    private readonly KernelExpression _right;

    public KernelComparison(Expression expression, Operator op, KernelExpression left, KernelExpression right) : base(expression)
    {
        _operator = op;
        _left = left;
        _right = right;
    }

    internal static bool IsSupported(Operator op) => op
        is Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual
        or Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality;

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        return Test(frame) == 1 ? JsBoolean.True : JsBoolean.False;
    }

    internal override int Test(KernelFrame frame)
    {
        var left = 0.0;
        var right = 0.0;
        var leftValue = _left.Evaluate(frame, out left);
        var rightValue = _right.Evaluate(frame, out right);
        if (IsNumber(leftValue, ref left) && IsNumber(rightValue, ref right))
        {
            return _operator switch
            {
                Operator.LessThan => left < right,
                Operator.LessThanOrEqual => left <= right,
                Operator.GreaterThan => left > right,
                Operator.GreaterThanOrEqual => left >= right,
                Operator.Equality or Operator.StrictEquality => left == right,
                _ => left != right,
            } ? 1 : 0;
        }

        return CompareValues(frame, Box(leftValue, left), Box(rightValue, right)) ? 1 : 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool CompareValues(KernelFrame frame, JsValue left, JsValue right)
    {
        frame.Context.PrepareFor(_expression);
        switch (_operator)
        {
            case Operator.LessThan:
                return JintExpression.CompareValues(left, right, leftFirst: true) is JsBoolean { _value: true };
            case Operator.GreaterThan:
                return JintExpression.CompareValues(right, left, leftFirst: false) is JsBoolean { _value: true };
            case Operator.LessThanOrEqual:
                return JintExpression.CompareValues(right, left, leftFirst: false) is JsBoolean { _value: false };
            case Operator.GreaterThanOrEqual:
                return JintExpression.CompareValues(left, right, leftFirst: true) is JsBoolean { _value: false };
            case Operator.StrictEquality:
                return left == right;
            case Operator.StrictInequality:
                return left != right;
            case Operator.Equality:
                return left.Type == right.Type ? left.Equals(right) : left.IsLooselyEqual(right);
            default:
                return !(left.Type == right.Type ? left.Equals(right) : left.IsLooselyEqual(right));
        }
    }
}
