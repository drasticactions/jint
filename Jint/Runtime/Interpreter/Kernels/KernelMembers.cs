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
    internal readonly KernelExpression _object;
    internal readonly KernelExpression _key;
    internal readonly KernelName? _objectName;
    internal readonly KernelName? _keyName;

    public KernelElement(Expression expression, KernelExpression obj, KernelExpression key) : base(expression)
    {
        _object = obj;
        _key = key;
        _objectName = obj as KernelName;
        _keyName = key as KernelName;
        _keyKind = KernelOperand.KeyKindOf(key);
    }

    // How KernelOperand reads the key.
    internal readonly byte _keyKind;

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        var target = _objectName is not null ? _objectName.Evaluate(frame, out _) : _object.Evaluate(frame, out _);
        if (target is null || ReferenceEquals(target, Bail))
        {
            return Bail;
        }

        double key;
        if (_keyKind != KernelOperand.Other)
        {
            // A key that is not a Number makes the element Bail, as its nodes would.
            if (!KernelOperand.TryKey(_key, _keyKind, frame, out key))
            {
                return Bail;
            }
        }
        else
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
            var element = KernelCodeRuntime.ReadTypedElement(typedArray, key);
            number = element.Number;
            return element.Value;
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
    internal readonly KernelExpression _object;
    internal readonly JsString _name;
    internal readonly Key _key;
    internal readonly bool _isLength;

    internal Shape? _cachedShape;
    internal int _cachedSlot;

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
                // What ReadTypedArrayLength answers while what it checked last holds, for a fixed-length view.
                if (CachedTypedArrayLengthHolds(typedArray)
                    && typedArray._viewedArrayBuffer is { _arrayBufferData: not null, _arrayBufferMaxByteLength: null }
                    && typedArray._arrayLength != JsTypedArray.LengthAuto)
                {
                    number = typedArray._arrayLength;
                    return null;
                }

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

    /// <summary>
    /// Whether what <see cref="ReadTypedArrayLength"/> last checked holds for <paramref name="typedArray"/>: no own
    /// properties, the same prototype and <c>%TypedArray%.prototype</c> above it, neither changed since.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CachedTypedArrayLengthHolds(JsTypedArray typedArray)
    {
        var link = _typedArrayLengthLink;
        var holder = _typedArrayLengthHolder;
        return holder is not null
               && typedArray._properties is null
               && ReferenceEquals(typedArray._prototype, link)
               && link!._propertiesVersion == _typedArrayLengthLinkVersion
               && ReferenceEquals(link._prototype, holder)
               && holder._propertiesVersion == _typedArrayLengthHolderVersion;
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
    internal readonly KernelExpression? _base;
    internal readonly KernelProperty? _member;
    internal readonly KernelExpression? _callee;
    internal readonly KernelExpression[] _arguments;
    private JintExpression? _handler;

    internal Function? _fastCallee;
    internal FastCallShape _fastShape;

    // JintCallExpression's register lane for an interpreted callee of at most four arguments: the definition last
    // probed, and its state when the lane serves it.
    internal JintFunctionDefinition? _registerDefinition;
    internal JintFunctionDefinition.State? _registerState;

    // The body of the definition last probed, when the tree computes it in place (see KernelInlineBody), with the
    // site that accepts its callees; and which Math function the fast callee is, when it is one computed in place.
    private KernelInlineBody? _inlineBody;
    private KernelInlineSite? _inlineSite;
    private int _intrinsic;

    // What a call made on the tree counts for in statements towards compiling the kernel (LoopKernel.WorthCompiling):
    // measured on the WebGL conformance loops, a call of a small script function takes the tree about as long as 20
    // to 30 statements of its own, a built-in's about 3.
    internal const int ScriptCallWork = 16;
    internal const int BuiltinCallWork = 2;

    public KernelCall(CallExpression expression, KernelProperty? member, KernelExpression? callee, KernelExpression[] arguments) : base(expression)
    {
        _member = member;
        _base = member?.Object;
        _callee = callee;
        _arguments = arguments;
        _argumentKinds = new byte[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            _argumentKinds[i] = arguments[i] is KernelRoot { _pureKind: KernelOperand.Element or KernelOperand.Arithmetic } ? KernelOperand.Root : KernelOperand.Other;
        }
    }

    // How KernelOperand reads arguments that are pure, for a body computed in place.
    private readonly byte[] _argumentKinds;

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;

        // A stack too low for the call: the interpreter makes it, on a fresh stack.
        if (!frame.Engine._stackGuard.TryEnterOnCurrentStack())
        {
            return Interpret(frame);
        }

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
            frame.Work += ScriptCallWork;
            return CallScript(frame, script, thisValue, out number);
        }

        frame.Work += BuiltinCallWork;
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
            _intrinsic = _arguments.Length == 1 ? KernelIntrinsics.Identify(function) : KernelIntrinsics.None;
        }

        // Before any argument runs: an argument can call back into this kernel and record another callee here.
        var fastShape = _fastShape;
        var intrinsic = _intrinsic;
        if (intrinsic != KernelIntrinsics.None)
        {
            // A Math function of a Number, computed as the built-in computes it: it runs no script and cannot throw.
            var argument = _arguments[0].Evaluate(frame, out number);
            if (argument is null)
            {
                number = KernelIntrinsics.Compute(intrinsic, number);
                return null;
            }

            if ((argument._type & (InternalTypes.Number | InternalTypes.Integer)) != InternalTypes.Empty)
            {
                number = KernelIntrinsics.Compute(intrinsic, Unsafe.As<JsNumber>(argument)._value);
                return null;
            }

            number = 0;
            return fastShape.IsLeafFor(thisValue, argument, JsValue.Undefined)
                ? function.CallFast(thisValue, argument, JsValue.Undefined)
                : CallWithFrame(frame, function, thisValue, argument, JsValue.Undefined);
        }

        var arg0 = _arguments.Length >= 1 ? Box(_arguments[0].Evaluate(frame, out var n0), n0) : JsValue.Undefined;
        var arg1 = _arguments.Length >= 2 ? Box(_arguments[1].Evaluate(frame, out var n1), n1) : JsValue.Undefined;

        if (fastShape.IsLeafFor(thisValue, arg0, arg1))
        {
            return function.CallFast(thisValue, arg0, arg1);
        }

        return CallWithFrame(frame, function, thisValue, arg0, arg1);
    }

    /// <summary>
    /// For compiled code, the steps of <see cref="Evaluate"/> between the callee and the arguments: 0 to leave the
    /// whole call to the interpreter, 1 for a built-in through its fast lane (<paramref name="shape"/>), 2 for an
    /// interpreted function through the register lane (<paramref name="state"/>), 3 for one with an argument list. The
    /// lane's state is taken before any argument runs, as <see cref="Evaluate"/> takes it.
    /// </summary>
    internal int Prepare(JsValue? callee, out FastCallShape shape, out JintFunctionDefinition.State? state)
    {
        shape = default;
        state = null;
        if (callee is not Function function)
        {
            return 0;
        }

        if (function is ScriptFunction script)
        {
            if (_arguments.Length <= 4 && !script._isClassConstructor)
            {
                var definition = script._functionDefinition;
                if (!ReferenceEquals(definition, _registerDefinition))
                {
                    _registerDefinition = definition;
                    _registerState = script.TryInitializeDefinition() is { SupportsRegisterCall: true } probed ? probed : null;
                    ProbeInlineBody();
                }

                if (_registerState is { } registerState)
                {
                    state = registerState;
                    return 2;
                }
            }

            return 3;
        }

        if (!ReferenceEquals(function, _fastCallee))
        {
            if (function._functionDefinition is not null || _arguments.Length > 2)
            {
                return 0;
            }

            var probedShape = function.GetFastCallShape(_arguments.Length);
            if (!probedShape.Supported || probedShape.Variadic)
            {
                return 0;
            }

            _fastCallee = function;
            _fastShape = probedShape;
            _intrinsic = _arguments.Length == 1 ? KernelIntrinsics.Identify(function) : KernelIntrinsics.None;
        }

        shape = _fastShape;
        return 1;
    }

    /// <summary>Whether a built-in's fast lane needs no call stack frame for these values: it then runs no script.</summary>
    internal static bool IsLeaf(FastCallShape shape, JsValue thisValue, JsValue arg0, JsValue arg1) => shape.IsLeafFor(thisValue, arg0, arg1);

    /// <summary>The call of a built-in through its fast lane, the arguments evaluated (undefined past the last).</summary>
    internal JsValue CallBuiltin(KernelFrame frame, Function function, JsValue thisValue, FastCallShape shape, JsValue arg0, JsValue arg1)
    {
        if (shape.IsLeafFor(thisValue, arg0, arg1))
        {
            return function.CallFast(thisValue, arg0, arg1);
        }

        return CallWithFrame(frame, function, thisValue, arg0, arg1);
    }

    /// <summary>The call of a built-in of one argument through its fast lane, the argument evaluated.</summary>
    internal JsValue CallIntrinsic(KernelFrame frame, Function function, JsValue thisValue, JsValue arg0)
        => CallBuiltin(frame, function, thisValue, function.GetFastCallShape(_arguments.Length), arg0, JsValue.Undefined);

    /// <summary>
    /// The call of an interpreted function whose body compiled code would have computed in place, the arguments
    /// evaluated: through the register lane, which the function's definition supports.
    /// </summary>
    internal JsValue CallInlined(KernelFrame frame, ScriptFunction function, JsValue arg0, JsValue arg1, JsValue arg2, JsValue arg3)
    {
        if (function.TryInitializeDefinition() is { SupportsRegisterCall: true } state)
        {
            return CallFromRegisters(frame, function, JsValue.Undefined, state, arg0, arg1, arg2, arg3, _arguments.Length);
        }

        var values = RentArguments(frame);
        JsValue[] all = [arg0, arg1, arg2, arg3];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = all[i];
        }

        return CallWithArguments(frame, function, JsValue.Undefined, values);
    }

    /// <summary>The call of an interpreted function through the register lane, the arguments evaluated.</summary>
    internal JsValue CallRegisterLane(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, JsValue arg0, JsValue arg1, JsValue arg2, JsValue arg3)
        => CallFromRegisters(frame, function, thisValue, state, arg0, arg1, arg2, arg3, _arguments.Length);

    /// <summary>The argument list of an interpreted function's call, rented for compiled code to fill.</summary>
    internal JsValue[] RentArguments(KernelFrame frame) => _arguments.Length == 0 ? [] : frame.Engine._jsValueArrayPool.RentArray(_arguments.Length);

    /// <summary>The call of an interpreted function with an argument list filled from <see cref="RentArguments"/>.</summary>
    internal JsValue CallWithArguments(KernelFrame frame, ScriptFunction function, JsValue thisValue, JsValue[] values)
    {
        var engine = frame.Engine;
        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);

        var callStack = engine.CallStack;
        var recursionDepth = callStack.Push(function, CallHandler, engine.ExecutionContext);
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

    private JsValue? CallScript(KernelFrame frame, ScriptFunction function, JsValue thisValue, out double number)
    {
        number = 0;
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
                ProbeInlineBody();
            }

            // Before any argument runs: an argument can call back into this kernel and probe another callee here.
            if (_registerState is { } state)
            {
                if (_inlineBody is { } body && _inlineSite!.Accepts(function, engine))
                {
                    return CallInline(frame, function, thisValue, state, body, out number);
                }

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
        var recursionDepth = callStack.Push(function, CallHandler, engine.ExecutionContext);
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

    /// <summary>
    /// Whether the tree computes the body of the definition just probed in place: a call of a name (whose
    /// <c>this</c> the body cannot read anyway) through the register lane, of a body <see cref="KernelInlineBody"/>
    /// accepts, as compiled code computes it.
    /// </summary>
    private void ProbeInlineBody()
    {
        _inlineBody = null;
        _inlineSite = null;
        if (_member is null && _registerState is not null && _registerDefinition is { } definition
            && KernelInlineBody.TryCreate(definition, _arguments.Length) is { } body)
        {
            _inlineBody = body;
            _inlineSite = new KernelInlineSite(definition, body.MathKinds.ToArray());
        }
    }

    /// <summary>
    /// The two statement checks a call of an inlined body runs (the body's and its <c>return</c>'s), taken when neither
    /// would run a constraint; false, having taken neither, otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryTakeInlineChecks(KernelFrame frame)
    {
        var context = frame.Context;
        var engine = frame.Engine;
        var exact = context.ShouldRunPerStatementChecks;
        if (exact)
        {
            if (engine._statementsBeforeExactChecks < 2)
            {
                return false;
            }
        }
        else if (context.StatementCounter is not null)
        {
            return false;
        }

        if (context.RunsAmortizedChecks)
        {
            if (engine._amortizedConstraintCountdown < 3)
            {
                return false;
            }

            engine._amortizedConstraintCountdown -= 2;
        }

        if (exact)
        {
            engine._statementsBeforeExactChecks -= 2;
        }

        return true;
    }

    /// <summary>
    /// A call of a function whose body the tree computes in place, the callee accepted by the site: the arguments
    /// evaluated in order, then the body computed when every one is a Number and its statement checks would pass
    /// without running anything; otherwise the call made through the register lane with the arguments evaluated.
    /// </summary>
    private JsValue? CallInline(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, KernelInlineBody body, out double number)
    {
        var arguments = _arguments;
        var count = arguments.Length;
        double n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        var v0 = count >= 1 ? Argument(frame, 0, out n0) : null;
        var v1 = count >= 2 ? Argument(frame, 1, out n1) : null;
        var v2 = count >= 3 ? Argument(frame, 2, out n2) : null;
        var v3 = count >= 4 ? Argument(frame, 3, out n3) : null;
        if (IsNumber(v0, ref n0) && IsNumber(v1, ref n1) && IsNumber(v2, ref n2) && IsNumber(v3, ref n3) && TryTakeInlineChecks(frame))
        {
            var result = body.Compute(n0, n1, n2, n3);
            if (body.IsBoolean)
            {
                number = 0;
                return result != 0 ? JsBoolean.True : JsBoolean.False;
            }

            number = result;
            return null;
        }

        number = 0;
        return CallFromRegisters(
            frame,
            function,
            thisValue,
            state,
            count >= 1 ? Box(v0, n0) : JsValue.Undefined,
            count >= 2 ? Box(v1, n1) : JsValue.Undefined,
            count >= 3 ? Box(v2, n2) : JsValue.Undefined,
            count >= 4 ? Box(v3, n3) : JsValue.Undefined,
            count);
    }

    /// <summary>An argument's value; a pure one that is a Number read without its nodes, any other evaluated by them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private JsValue? Argument(KernelFrame frame, int index, out double number)
    {
        var argument = _arguments[index];
        if (_argumentKinds[index] != KernelOperand.Other && KernelOperand.TryNumber(argument, KernelOperand.Root, frame, out number))
        {
            return null;
        }

        return argument.Evaluate(frame, out number);
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
        return CallFromRegisters(frame, function, thisValue, state, arg0, arg1, arg2, arg3, count);
    }

    private JsValue CallFromRegisters(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, JsValue arg0, JsValue arg1, JsValue arg2, JsValue arg3, int count)
    {
        var engine = frame.Engine;
        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);

        var callStack = engine.CallStack;
        var recursionDepth = callStack.Push(function, CallHandler, engine.ExecutionContext);
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
    internal JsValue CallWithFrame(KernelFrame frame, Function function, JsValue thisValue, JsValue arg0, JsValue arg1)
    {
        var engine = frame.Engine;
        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);

        var callStack = engine.CallStack;
        var recursionDepth = callStack.Push(function, CallHandler, engine.ExecutionContext);
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

    // The call's handler, whose node the call stack frame reports the call at.
    private JintExpression CallHandler => _handler ??= JintExpression.Build(_expression);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue Interpret(KernelFrame frame) => (_handler ??= JintExpression.Build(_expression)).GetValue(frame.Context);
}

/// <summary>
/// A comparison whose operands are evaluated once each, in order, when one of them is not pure (a call above all):
/// two Numbers compare directly, any other pair as the interpreter's comparison handlers compare evaluated operands.
/// </summary>
internal sealed class KernelComparison : KernelExpression
{
    internal readonly Operator _operator;
    internal readonly KernelExpression _left;
    internal readonly KernelExpression _right;

    public KernelComparison(Expression expression, Operator op, KernelExpression left, KernelExpression right) : base(expression)
    {
        _operator = op;
        _left = left;
        _right = right;
        _rightKind = right is KernelRoot ? KernelOperand.KindOf(right) : KernelOperand.Other;
    }

    // How KernelOperand reads a right operand that is pure.
    private readonly byte _rightKind;

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
        JsValue? rightValue = null;
        if (_rightKind == KernelOperand.Other || !KernelOperand.TryNumber(_right, _rightKind, frame, out right))
        {
            rightValue = _right.Evaluate(frame, out right);
        }

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
    internal bool CompareValues(KernelFrame frame, JsValue left, JsValue right)
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
