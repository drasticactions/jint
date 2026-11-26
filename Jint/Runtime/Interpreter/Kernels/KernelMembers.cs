using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Native.Symbol;
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

        return ReadIndex(target, key, out number);
    }

    /// <summary>The element of an evaluated base at a key read purely, or <see cref="KernelExpression.Bail"/>.</summary>
    internal static JsValue? Read(JsValue target, JsValue? key, double keyNumber, out double number)
    {
        if (!IsNumber(key, ref keyNumber))
        {
            number = 0;
            return Bail;
        }

        return ReadIndex(target, keyNumber, out number);
    }

    private static JsValue? ReadIndex(JsValue target, double key, out double number)
    {
        number = 0;
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
/// A pure <c>arguments.length</c> or <c>arguments[index]</c> of an arguments object none of whose properties was
/// materialized, read as its [[Get]] reads it; anything else answers <see cref="KernelExpression.Bail"/>.
/// </summary>
internal sealed class KernelArgumentsRead : KernelExpression
{
    internal readonly KernelName _arguments;
    internal readonly KernelExpression? _index;

    public KernelArgumentsRead(Expression expression, KernelName arguments, KernelExpression? index) : base(expression)
    {
        _arguments = arguments;
        _index = index;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        if (frame.ArgumentsPending)
        {
            return ReadPending(frame, out number);
        }

        if (_arguments.PeekSlot(frame) is not JsArguments arguments)
        {
            return Bail;
        }

        if (_index is null)
        {
            var length = arguments.VirtualLength;
            if (length < 0)
            {
                return Bail;
            }

            number = length;
            return null;
        }

        var key = _index.Evaluate(frame, out var keyNumber);
        if (!IsNumber(key, ref keyNumber))
        {
            return Bail;
        }

        var index = (uint) keyNumber;
        if (index != keyNumber || index == uint.MaxValue)
        {
            return Bail;
        }

        return arguments.VirtualElement(index) ?? Bail;
    }

    /// <summary>The read of an arguments object the call put off: from the call's arguments or, where mapped, its parameters; anything else makes the object and answers Bail.</summary>
    private JsValue? ReadPending(KernelFrame frame, out double number)
    {
        number = 0;
        var count = frame.PendingArguments.Count;
        if (_index is null)
        {
            number = count;
            return null;
        }

        var key = _index.Evaluate(frame, out var keyNumber);
        if (!IsNumber(key, ref keyNumber) || keyNumber < 0 || keyNumber >= count || (int) keyNumber != keyNumber)
        {
            frame.MaterializeArguments();
            return Bail;
        }

        var index = (int) keyNumber;
        if (frame.ArgumentsMapped && index < frame.ArgumentsState!.ParameterNames.Length)
        {
            ref var binding = ref frame.CallEnvironment!._slots![index];
            if (binding.IsUnboxedNumber)
            {
                number = binding.UnboxedNumber;
                return null;
            }

            if (binding.IsPlainValue && binding.PeekValue is { } value)
            {
                return value;
            }

            frame.MaterializeArguments();
            return Bail;
        }

        return frame.PendingArguments.ValueAt(index, out number);
    }
}

/// <summary>A pure read of an arguments object, which is materialized first as the interpreter's read of it does.</summary>
internal sealed class KernelArgumentsValue : KernelExpression
{
    internal readonly KernelName _arguments;

    public KernelArgumentsValue(Expression expression, KernelName arguments) : base(expression)
    {
        _arguments = arguments;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        if (frame.ArgumentsPending)
        {
            frame.MaterializeArguments();
        }

        if (_arguments.PeekSlot(frame) is not JsArguments arguments)
        {
            return _arguments.Evaluate(frame, out number);
        }

        arguments.Materialize();
        return arguments;
    }
}

/// <summary>
/// A pure <c>object.name</c>: a data property of an object, own or up its prototype chain, a method of a string or
/// Number, or a length of an array, string or typed array; anything else answers <see cref="KernelExpression.Bail"/>.
/// </summary>
internal sealed class KernelProperty : KernelExpression
{
    internal readonly KernelExpression _object;
    internal readonly JsString _name;
    internal readonly Key _key;
    internal readonly bool _isLength;

    internal Shape? _cachedShape;
    internal int _cachedSlot;

    // A shape of receivers without an own property of the name.
    private Shape? _missShape;

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

        return Read(target, out number, frame.Engine);
    }

    /// <summary>The property of an evaluated base, or <see cref="KernelExpression.Bail"/>; a primitive's needs <paramref name="engine"/>.</summary>
    internal JsValue? Read(JsValue target, out double number, Engine? engine = null)
    {
        number = 0;
        if (target is JsObject plain)
        {
            var type = plain._type;
            if ((type & InternalTypes.ShapeMode) != InternalTypes.Empty)
            {
                var shape = plain.ShapeOf;
                int slot;
                if (ReferenceEquals(shape, _cachedShape))
                {
                    slot = _cachedSlot;
                }
                else if (ReferenceEquals(shape, _missShape))
                {
                    return ReadInherited(plain);
                }
                else if (shape.TryGetSlot(in _key, out slot))
                {
                    _cachedShape = shape;
                    _cachedSlot = slot;
                }
                else
                {
                    _missShape = shape;
                    return ReadInherited(plain);
                }

                var value = plain.GetSlot(slot);
                return (type & InternalTypes.HasLazySlots) == InternalTypes.Empty ? value : ReadLazy(plain, slot, value);
            }

            return ReadDictionary(plain);
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

        if (target is ObjectInstance other)
        {
            return (other._type & InternalTypes.BuiltinShapeMode) != InternalTypes.Empty ? ReadBuiltin(other) : ReadOther(other);
        }

        return engine is null ? Bail : ReadPrimitive(target, engine, out number);
    }

    /// <summary><see cref="Read"/> for the callee of a call.</summary>
    internal JsValue? ReadCallee(JsValue target, Engine engine) => Read(target, out _, engine);

    /// <summary>An own data property of an array or a function, or one up its prototype chain.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ReadOther(ObjectInstance receiver)
    {
        var type = receiver._type;
        if ((type & (InternalTypes.Array | InternalTypes.Function)) == InternalTypes.Empty
            || (type & (InternalTypes.ExoticGet | InternalTypes.OwnValueHook | InternalTypes.OrdinaryGet)) != InternalTypes.Empty)
        {
            return Bail;
        }

        // The engine's own types only: a host's may answer own properties with code of its own.
        var runtimeType = receiver.GetType();
        if (!ReferenceEquals(runtimeType, _otherType))
        {
            if (runtimeType.Assembly != typeof(KernelProperty).Assembly)
            {
                return Bail;
            }

            _otherType = runtimeType;
        }

        if (receiver is not JsArray { _properties: null })
        {
            var descriptor = receiver.GetOwnProperty(_name);
            if (!ReferenceEquals(descriptor, PropertyDescriptor.Undefined))
            {
                return IsPlainData(descriptor) ? descriptor._value ?? Bail : Bail;
            }
        }

        return ReadFrom(receiver.GetPrototypeOf());
    }

    /// <summary>A string's length, or a method of a string or a Number from the running realm's prototype.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue? ReadPrimitive(JsValue target, Engine engine, out double number)
    {
        number = 0;
        if (target is JsString text)
        {
            if (_isLength)
            {
                number = text.Length;
                return null;
            }

            return ReadFrom(engine.RunningRealm.Intrinsics.String.PrototypeObject);
        }

        return target is JsNumber ? ReadFrom(engine.RunningRealm.Intrinsics.Number.PrototypeObject) : Bail;
    }

    /// <summary>A lazy slot's value: a closure not made yet is made now; a host's slot answers Bail.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsValue ReadLazy(JsObject owner, int slot, JsValue value)
    {
        if (value is JsObject.LayoutSlots)
        {
            return Bail;
        }

        return value is JsObject.UnmaterializedSlots ? owner.GetSlotForRead(slot) : value;
    }

    /// <summary>An own data property of an object kept by name, or the prototype's where it has none.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ReadDictionary(JsObject plain)
    {
        if ((plain._type & (InternalTypes.PlainObject | InternalTypes.ExoticGet | InternalTypes.OwnValueHook)) != InternalTypes.PlainObject)
        {
            return Bail;
        }

        if (plain._properties is { } properties && properties.TryGetValue(_key, out var descriptor))
        {
            return IsPlainData(descriptor) ? descriptor._value ?? Bail : Bail;
        }

        return ReadInherited(plain);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPlainData(PropertyDescriptor descriptor)
        => (descriptor._flags & PropertyFlag.CustomJsValue) == PropertyFlag.None && descriptor.IsDataDescriptor();

    // The prototype last read from, its version, and its slot or (kept by name) descriptor of the member.
    private ObjectInstance? _protoHolder;
    private uint _protoHolderVersion;
    private Shape? _protoHolderShape;
    private int _protoHolderSlot;
    private PropertyDescriptor? _protoDescriptor;

    // The links of the chain before the holder, with their versions; null where the holder is the first.
    private ObjectInstance[]? _protoLinks;
    private uint[]? _protoLinkVersions;
    private const int MaxProtoLinks = 8;

    // The last engine type ReadOther read own properties of.
    private Type? _otherType;

    /// <summary>The member up the prototype chain of an object without an own property of the name, or Bail.</summary>
    private JsValue ReadInherited(JsObject receiver) => ReadFrom(receiver._prototype);

    /// <summary>The member of the first object of the chain that starts at <paramref name="start"/> to have it, or Bail.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ReadFrom(ObjectInstance? start)
    {
        if (start is null)
        {
            return Bail;
        }

        if (_absentLinks is { } absent && AbsentHolds(start, absent, _absentVersions!))
        {
            return JsValue.Undefined;
        }

        var holder = _protoLinks is { } links ? PastLinks(start, links, _protoLinkVersions!) : start;
        if (holder is null || !ReferenceEquals(holder, _protoHolder) || holder._propertiesVersion != _protoHolderVersion)
        {
            var cached = CacheInherited(start);
            if (cached <= 0)
            {
                return cached == 0 ? JsValue.Undefined : Bail;
            }

            holder = _protoHolder!;
        }

        if (_protoDescriptor is { } descriptor)
        {
            return IsPlainData(descriptor) ? descriptor._value ?? Bail : Bail;
        }

        var shaped = Unsafe.As<JsObject>(holder);
        if ((shaped._type & InternalTypes.ShapeMode) == InternalTypes.Empty || !ReferenceEquals(shaped.ShapeOf, _protoHolderShape))
        {
            return Bail;
        }

        var value = shaped.GetSlot(_protoHolderSlot);
        return (shaped._type & InternalTypes.HasLazySlots) == InternalTypes.Empty ? value : ReadLazy(shaped, _protoHolderSlot, value);
    }

    /// <summary>The object past <paramref name="links"/> from <paramref name="start"/>, or null where the chain or a version moved.</summary>
    private static ObjectInstance? PastLinks(ObjectInstance start, ObjectInstance[] links, uint[] versions)
    {
        ObjectInstance? reached = start;
        for (var i = 0; i < links.Length; i++)
        {
            var link = links[i];
            if (!ReferenceEquals(reached, link) || link._propertiesVersion != versions[i])
            {
                return null;
            }

            reached = link.GetPrototypeOf();
        }

        return reached;
    }

    /// <summary>
    /// Records the first object of the chain from <paramref name="start"/> that has the member, which must be an own
    /// data property, and the links before it, each of which must witness the member's absence by its version; or false.
    /// </summary>
    private int CacheInherited(ObjectInstance start)
    {
        _protoHolder = null;
        _protoHolderShape = null;
        _protoDescriptor = null;
        _protoLinks = null;
        _protoLinkVersions = null;
        var holder = start;
        var passed = 0;
        while (true)
        {
            if ((holder._type & (InternalTypes.ExoticGet | InternalTypes.OwnValueHook)) != InternalTypes.Empty
                || !ObjectInstance.VersionWitnessesOwnProperty(holder, _name))
            {
                return -1;
            }

            var found = CacheOwn(holder);
            if (found > 0)
            {
                break;
            }

            if (found < 0 || ++passed > MaxProtoLinks)
            {
                return -1;
            }

            if (holder.GetPrototypeOf() is not { } next)
            {
                return CacheAbsent(start, passed) ? 0 : -1;
            }

            holder = next;
        }

        if (passed > 0)
        {
            var links = new ObjectInstance[passed];
            var versions = new uint[passed];
            var link = start;
            for (var i = 0; i < passed; i++)
            {
                links[i] = link;
                versions[i] = link._propertiesVersion;
                link = link.GetPrototypeOf()!;
            }

            _protoLinks = links;
            _protoLinkVersions = versions;
        }

        _protoHolder = holder;
        _protoHolderVersion = holder._propertiesVersion;
        return 1;
    }

    // A chain none of whose links has the member, with their versions, as CacheInherited last walked it.
    private ObjectInstance[]? _absentLinks;
    private uint[]? _absentVersions;

    /// <summary>Records the <paramref name="count"/> links from <paramref name="start"/> as a chain without the member; false where a resolver may answer the read.</summary>
    private bool CacheAbsent(ObjectInstance start, int count)
    {
        if (start.Engine._resolverWatchesValueBase)
        {
            return false;
        }

        var links = new ObjectInstance[count];
        var versions = new uint[count];
        var link = start;
        for (var i = 0; i < count; i++)
        {
            links[i] = link;
            versions[i] = link._propertiesVersion;
            link = link.GetPrototypeOf()!;
        }

        _absentLinks = links;
        _absentVersions = versions;
        return true;
    }

    /// <summary>Whether the chain from <paramref name="start"/> is the recorded one without the member, its versions unchanged.</summary>
    private static bool AbsentHolds(ObjectInstance start, ObjectInstance[] links, uint[] versions)
    {
        ObjectInstance? reached = start;
        for (var i = 0; i < links.Length; i++)
        {
            var link = links[i];
            if (!ReferenceEquals(reached, link) || link._propertiesVersion != versions[i])
            {
                return false;
            }

            reached = link.GetPrototypeOf();
        }

        return reached is null;
    }

    /// <summary>1 with where <paramref name="holder"/> keeps the member recorded, 0 where it has none, -1 where it cannot be recorded.</summary>
    private int CacheOwn(ObjectInstance holder)
    {
        if (holder is JsObject plain)
        {
            if ((plain._type & InternalTypes.ShapeMode) != InternalTypes.Empty)
            {
                if (!plain.ShapeOf.TryGetSlot(in _key, out var slot))
                {
                    return 0;
                }

                _protoHolderShape = plain.ShapeOf;
                _protoHolderSlot = slot;
                return 1;
            }

            if ((plain._type & InternalTypes.PlainObject) == InternalTypes.Empty)
            {
                return -1;
            }

            if (plain._properties is not { } named || !named.TryGetValue(_key, out var own))
            {
                return 0;
            }

            if (!IsPlainData(own))
            {
                return -1;
            }

            _protoDescriptor = own;
            return 1;
        }

        // Materializing a lazy built-in member runs no script; a value write changes a descriptor kept by name.
        var descriptor = holder.GetOwnProperty(_name);
        if (descriptor == PropertyDescriptor.Undefined)
        {
            return 0;
        }

        if (!IsPlainData(descriptor)
            || (holder is not BuiltinShapeObject
                && ((holder._type & InternalTypes.BuiltinShapeMode) == InternalTypes.Empty || !ReferenceEquals(holder.GetOwnProperty(_name), descriptor))
                && (holder._properties is not { } properties || !properties.TryGetValue(_key, out var kept) || !ReferenceEquals(kept, descriptor))))
        {
            return -1;
        }

        _protoDescriptor = descriptor;
        return 1;
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
/// A call (https://tc39.es/ecma262/#sec-function-calls-runtime-semantics-evaluation) of a callee read purely, made as
/// JintCallExpression makes it; a callee that is not a function leaves the whole call to the interpreter.
/// </summary>
internal sealed class KernelCall : KernelExpression
{
    internal readonly KernelExpression? _base;
    internal readonly KernelProperty? _member;
    internal readonly KernelExpression? _callee;
    internal readonly KernelExpression? _key;
    internal readonly KernelExpression[] _arguments;
    private JintExpression? _handler;

    // The built-in last called and how: its fast call shape, or a span or an argument list where it has none.
    internal Function? _fastCallee;
    internal FastCallShape _fastShape;
    internal byte _builtinLane;
    internal const byte FastLane = 0;
    internal const byte VariadicLane = 1;
    internal const byte ListLane = 2;

    // JintCallExpression's register lane for an interpreted callee of at most four arguments: the definition last
    // probed, and its state when the lane serves it.
    internal JintFunctionDefinition? _registerDefinition;
    internal JintFunctionDefinition.State? _registerState;

    // The state of the definition last probed where its kernel's calls put off the arguments object.
    private JintFunctionDefinition.State? _deferredArgumentsState;

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

    public KernelCall(CallExpression expression, KernelProperty? member, KernelExpression? callee, KernelExpression[] arguments, KernelExpression? obj = null, KernelExpression? key = null) : base(expression)
    {
        _member = member;
        _base = member?.Object ?? obj;
        _callee = callee;
        _key = key;
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
            var target = _base!.Evaluate(frame, out var targetNumber);
            if (target is null)
            {
                target = JsNumber.Create(targetNumber);
            }
            else if (ReferenceEquals(target, Bail))
            {
                return Interpret(frame);
            }

            thisValue = target;
            callee = _member.ReadCallee(target, frame.Engine);
        }
        else if (_key is not null)
        {
            var target = _base!.Evaluate(frame, out _);
            if (target is null || ReferenceEquals(target, Bail))
            {
                return Interpret(frame);
            }

            var key = _key.Evaluate(frame, out var keyNumber);
            thisValue = target;
            callee = ReadElement(target, key, keyNumber);
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
            if (function._functionDefinition is not null)
            {
                return Interpret(frame);
            }

            ProbeBuiltin(function);
        }

        // Before any argument runs: an argument can call back into this kernel and record another callee here.
        var fastShape = _fastShape;
        var lane = _builtinLane;
        if (lane == ListLane)
        {
            return CallWithList(frame, function, thisValue);
        }

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
            return lane == VariadicLane ? CallVariadic(function, thisValue, arg0, arg1) : function.CallFast(thisValue, arg0, arg1);
        }

        return CallWithFrame(frame, function, thisValue, arg0, arg1);
    }

    /// <summary>Records how a built-in is called at this site: through its fast call shape, with a span or with a list.</summary>
    private void ProbeBuiltin(Function function)
    {
        var count = _arguments.Length;
        var shape = count <= 2 ? function.GetFastCallShape(count) : default;
        _fastCallee = function;
        _fastShape = shape;
        _builtinLane = !shape.Supported ? ListLane : shape.Variadic ? VariadicLane : FastLane;
        _intrinsic = count == 1 && _builtinLane == FastLane ? KernelIntrinsics.Identify(function) : KernelIntrinsics.None;
    }

    /// <summary>The fast call shape compiled code may call the site's built-in through directly: none where it takes a span or a list.</summary>
    private FastCallShape CodeShape => _builtinLane == FastLane ? _fastShape : default;

    /// <summary>
    /// The callee of <c>object[key](...)</c> read purely: a present element of an array, or an own data property of an
    /// ordinary object named by a string; anything else is Bail.
    /// </summary>
    private static JsValue? ReadElement(JsValue target, JsValue? key, double keyNumber)
    {
        if (KernelExpression.IsNumber(key, ref keyNumber))
        {
            if (target is JsArray array)
            {
                var index = (uint) keyNumber;
                if (index == keyNumber && index != uint.MaxValue && array.TryGetValueFast(index, out var element))
                {
                    return element;
                }
            }

            return Bail;
        }

        if (key is not JsString name || target is not JsObject plain || ReferenceEquals(key, Bail))
        {
            return Bail;
        }

        Key property = name.ToString();
        var type = plain._type;
        if ((type & InternalTypes.ShapeMode) != InternalTypes.Empty)
        {
            if (!plain.ShapeOf.TryGetSlot(in property, out var slot))
            {
                return Bail;
            }

            var value = plain.GetSlot(slot);
            return (type & InternalTypes.HasLazySlots) == InternalTypes.Empty ? value : ReadLazySlot(plain, slot, value);
        }

        if ((type & (InternalTypes.PlainObject | InternalTypes.ExoticGet | InternalTypes.OwnValueHook)) == InternalTypes.PlainObject
            && plain._properties is { } properties
            && properties.TryGetValue(property, out var descriptor)
            && (descriptor._flags & PropertyFlag.CustomJsValue) == PropertyFlag.None
            && descriptor.IsDataDescriptor())
        {
            return descriptor._value ?? Bail;
        }

        return Bail;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsValue ReadLazySlot(JsObject owner, int slot, JsValue value)
    {
        if (value is JsObject.LayoutSlots)
        {
            return Bail;
        }

        return value is JsObject.UnmaterializedSlots ? owner.GetSlotForRead(slot) : value;
    }

    /// <summary>A built-in of a variadic fast call shape called with its arguments as a span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private JsValue CallVariadic(Function function, JsValue thisValue, JsValue arg0, JsValue arg1)
    {
        var buffer = new TwoArguments();
        buffer[0] = arg0;
        buffer[1] = arg1;
        return function.CallFastVariadic(thisValue, ((ReadOnlySpan<JsValue>) buffer).Slice(0, _arguments.Length));
    }

    /// <summary>Two arguments in a row, for a span.</summary>
    [InlineArray(2)]
    private struct TwoArguments
    {
        private JsValue _element0;
    }

    /// <summary>A built-in, host or bound function called with its arguments in a pooled list, as JintCallExpression calls one without a fast call shape.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue CallWithList(KernelFrame frame, Function function, JsValue thisValue)
    {
        var engine = frame.Engine;
        var arguments = _arguments;
        var values = arguments.Length == 0 ? [] : engine._jsValueArrayPool.RentArray(arguments.Length);
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i] = Box(arguments[i].Evaluate(frame, out var n), n);
        }

        return CallListed(frame, function, thisValue, values);
    }

    /// <summary>The call of <see cref="CallWithList"/>, the arguments evaluated into <paramref name="values"/>, which it returns to the pool.</summary>
    private JsValue CallListed(KernelFrame frame, Function function, JsValue thisValue, JsValue[] values)
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
            result = function.Call(thisValue, values);
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
    /// For compiled code, the lane <see cref="Evaluate"/> takes, chosen before any argument runs: 0 the interpreter, 1 a
    /// built-in's fast lane, 2 the register lane, 3 an interpreted function's argument list, 4 a built-in's.
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
                    var probedState = script.TryInitializeDefinition();
                    _registerState = probedState is { SupportsRegisterCall: true } ? probedState : null;
                    _deferredArgumentsState = probedState is { ArgumentsInFrame: true } ? probedState : null;
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
            if (function._functionDefinition is not null)
            {
                return 0;
            }

            ProbeBuiltin(function);
        }

        shape = CodeShape;
        return _arguments.Length > 2 ? 4 : 1;
    }

    /// <summary>For compiled code: the register lane's state for <paramref name="function"/>, probed as <see cref="CallScript"/> probes it, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JintFunctionDefinition.State? RegisterState(ScriptFunction function)
        => ReferenceEquals(function._functionDefinition, _registerDefinition) && !function._isClassConstructor && _arguments.Length <= 4
            ? _registerState
            : ProbeRegisterState(function);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JintFunctionDefinition.State? ProbeRegisterState(ScriptFunction function)
    {
        if (_arguments.Length > 4 || function._isClassConstructor)
        {
            return null;
        }

        _registerDefinition = function._functionDefinition;
        var probedState = function.TryInitializeDefinition();
        _registerState = probedState is { SupportsRegisterCall: true } ? probedState : null;
        _deferredArgumentsState = probedState is { ArgumentsInFrame: true } ? probedState : null;
        ProbeInlineBody();
        return _registerState;
    }

    /// <summary>Whether the tree computes this call of <paramref name="function"/> in place (see <see cref="KernelInlineBody"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ComputesInPlace(ScriptFunction function, Engine engine) => _inlineBody is not null && InlineSiteAccepts(function, engine);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool InlineSiteAccepts(ScriptFunction function, Engine engine) => _inlineSite!.Accepts(function, engine);

    /// <summary>The tree's call of an interpreted function, for compiled code.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal KernelResult CallScriptFromCode(KernelFrame frame, ScriptFunction function, JsValue thisValue)
    {
        var value = CallScript(frame, function, thisValue, out var number);
        return new KernelResult(value, number);
    }

    /// <summary>
    /// For compiled code: the fast call shape of a built-in <paramref name="callee"/>, probed as <see cref="Evaluate"/>
    /// probes it; false to leave the call to the interpreter.
    /// </summary>
    internal bool BuiltinShape(JsValue? callee, out FastCallShape shape)
    {
        shape = default;
        if (callee is not Function function)
        {
            return false;
        }

        if (_arguments.Length > 2)
        {
            return false;
        }

        if (!ReferenceEquals(function, _fastCallee))
        {
            if (function._functionDefinition is not null)
            {
                return false;
            }

            ProbeBuiltin(function);
        }

        shape = CodeShape;
        return true;
    }

    /// <summary>For compiled code: whether <paramref name="callee"/> is a function other than a script function, recorded as the site's built-in.</summary>
    internal bool ListBuiltin(JsValue? callee)
    {
        if (callee is not Function function || function._functionDefinition is not null)
        {
            return false;
        }

        if (!ReferenceEquals(function, _fastCallee))
        {
            ProbeBuiltin(function);
        }

        return true;
    }

    /// <summary><see cref="CallWithList"/> for compiled code.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue CallWithListFromCode(KernelFrame frame, Function function, JsValue thisValue) => CallWithList(frame, function, thisValue);

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

    /// <summary><see cref="CallBuiltin"/> out of line, for compiled code.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue CallBuiltinFromCode(KernelFrame frame, Function function, JsValue thisValue, FastCallShape shape, JsValue arg0, JsValue arg1)
        => CallBuiltin(frame, function, thisValue, shape, arg0, arg1);

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

    /// <summary>The call of an interpreted function through the register lane, the arguments evaluated, Numbers unboxed.</summary>
    internal KernelResult CallRegisterLane(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, JsValue? v0, double n0, JsValue? v1, double n1, JsValue? v2, double n2, JsValue? v3, double n3)
    {
        if (_inlineBody is { } body && TryComputeInPlace(frame, function, body, v0, n0, v1, n1, v2, n2, v3, n3, out var computed))
        {
            return computed;
        }

        var count = _arguments.Length;
        if (function._functionDefinition!._kernel is { } kernel)
        {
            if (kernel._code is { } code)
            {
                var engine = frame.Engine;
                if (engine.CanPushCallFrame && kernel.RunsInFrames(state) && !frame.Context.CompletionValuesObservable && engine._stackGuard.HasVerifiedHeadroom())
                {
                    return kernel.CallCode(engine, function, CallHandler, thisValue, state, code, v0, n0, v1, n1, v2, n2, v3, n3, count);
                }
            }

            var value = CallKernel(frame, function, thisValue, state, kernel, new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count), out var number);
            return new KernelResult(value, number);
        }

        return new KernelResult(CallFromNumbers(frame, function, thisValue, state, new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count)), 0);
    }

    /// <summary>Whether <paramref name="function"/>'s body has code <see cref="CallFromCode"/> calls straight into.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasCode(ScriptFunction function, JintFunctionDefinition.State state)
        => function._functionDefinition!._kernel is { _code: not null };

    /// <summary><see cref="CallRegisterLane"/> for compiled code: straight into the callee's code where it has some.</summary>
    internal KernelResult CallFromCode(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, JsValue? v0, double n0, JsValue? v1, double n1, JsValue? v2, double n2, JsValue? v3, double n3)
    {
        if (function._functionDefinition!._kernel is { _code: { } code } kernel)
        {
            var engine = frame.Engine;
            if (engine.CanPushCallFrame && kernel.RunsInFrames(state) && !frame.Context.CompletionValuesObservable && engine._stackGuard.HasVerifiedHeadroom())
            {
                return kernel.CallCode(engine, function, CallHandler, thisValue, state, code, v0, n0, v1, n1, v2, n2, v3, n3, _arguments.Length);
            }
        }

        return CallRegisterLaneOutOfLine(frame, function, thisValue, state, v0, n0, v1, n1, v2, n2, v3, n3);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private KernelResult CallRegisterLaneOutOfLine(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, JsValue? v0, double n0, JsValue? v1, double n1, JsValue? v2, double n2, JsValue? v3, double n3)
        => CallRegisterLane(frame, function, thisValue, state, v0, n0, v1, n1, v2, n2, v3, n3);

    /// <summary>The argument list of an interpreted function's call, rented for compiled code to fill.</summary>
    internal JsValue[] RentArguments(KernelFrame frame) => _arguments.Length == 0 ? [] : frame.Engine._jsValueArrayPool.RentArray(_arguments.Length);

    /// <summary>The call of an interpreted function with an argument list filled from <see cref="RentArguments"/>.</summary>
    internal JsValue CallWithArguments(KernelFrame frame, ScriptFunction function, JsValue thisValue, JsValue[] values)
    {
        var engine = frame.Engine;
        if (DeferringKernel(frame, function) is { } kernel)
        {
            var count = values.Length;
            var arguments = new NumberArguments(
                count > 0 ? values[0] : null, 0,
                count > 1 ? values[1] : null, 0,
                count > 2 ? values[2] : null, 0,
                count > 3 ? values[3] : null, 0,
                count);
            var value = CallDeferringArguments(frame, function, thisValue, _deferredArgumentsState!, kernel, in arguments, out var number);
            if (count > 0)
            {
                engine._jsValueArrayPool.ReturnArray(values);
            }

            return Box(value, number);
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
                var probedState = function.TryInitializeDefinition();
                _registerState = probedState is { SupportsRegisterCall: true } ? probedState : null;
                _deferredArgumentsState = probedState is { ArgumentsInFrame: true } ? probedState : null;
                ProbeInlineBody();
            }

            // Before any argument runs: an argument can call back into this kernel and probe another callee here.
            if (_registerState is { } state)
            {
                if (_inlineBody is { } body && _inlineSite!.Accepts(function, engine))
                {
                    return CallInline(frame, function, thisValue, state, body, out number);
                }

                if (definition!._kernel is { } kernel)
                {
                    return CallKernel(frame, function, thisValue, state, kernel, out number);
                }

                return CallRegisters(frame, function, thisValue, state);
            }
        }

        return CallWithArgumentList(frame, function, thisValue);
    }

    /// <summary>The kernel of <paramref name="function"/> when this site calls it putting off its arguments object, in a frame of the kernel; or null.</summary>
    private FunctionKernel? DeferringKernel(KernelFrame frame, ScriptFunction function)
    {
        var engine = frame.Engine;
        return _deferredArgumentsState is { } state
               && ReferenceEquals(function._functionDefinition, _registerDefinition)
               && function._functionDefinition!._kernel is { } kernel
               && engine.CanPushCallFrame
               && kernel.RunsInFrames(state)
               && !frame.Context.CompletionValuesObservable
               && engine._stackGuard.HasVerifiedHeadroom()
            ? kernel
            : null;
    }

    /// <summary>The call of a function whose kernel puts off its arguments object, its arguments evaluated in order.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue? CallDeferringArguments(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, FunctionKernel kernel, out double number)
    {
        var arguments = _arguments;
        var count = arguments.Length;
        double n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        var v0 = count >= 1 ? arguments[0].Evaluate(frame, out n0) : null;
        var v1 = count >= 2 ? arguments[1].Evaluate(frame, out n1) : null;
        var v2 = count >= 3 ? arguments[2].Evaluate(frame, out n2) : null;
        var v3 = count >= 4 ? arguments[3].Evaluate(frame, out n3) : null;
        return CallDeferringArguments(frame, function, thisValue, state, kernel, new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count), out number);
    }

    /// <summary>The call of a function whose kernel puts off its arguments object, in a frame of the kernel.</summary>
    private JsValue? CallDeferringArguments(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, FunctionKernel kernel, in NumberArguments arguments, out double number)
    {
        var engine = frame.Engine;
        var oldPositions = engine.MarkLastSyntaxPositions();
        var oldSource = engine._lastSyntaxLocation.SourceFile;
        var result = function.CallKernelInFrameDeferringArguments(CallHandler, thisValue, in arguments, state, kernel, out number);
        engine.RestoreLastSyntaxElement(oldPositions, oldSource);
        return result;
    }

    /// <summary>The call of an interpreted function with its arguments in a pooled list.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue CallWithArgumentList(KernelFrame frame, ScriptFunction function, JsValue thisValue)
    {
        var engine = frame.Engine;
        if (DeferringKernel(frame, function) is { } kernel)
        {
            return Box(CallDeferringArguments(frame, function, thisValue, _deferredArgumentsState!, kernel, out var number), number);
        }

        var arguments = _arguments;
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
    /// The body of an accepted callee computed in place for arguments evaluated by loop code, as <see cref="CallInline"/>
    /// computes it; false, having run nothing, where it does not apply.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryComputeInPlace(KernelFrame frame, ScriptFunction function, KernelInlineBody body, JsValue? v0, double n0, JsValue? v1, double n1, JsValue? v2, double n2, JsValue? v3, double n3, out KernelResult computed)
    {
        computed = default;
        if (!_inlineSite!.Accepts(function, frame.Engine)
            || !IsNumber(v0, ref n0) || !IsNumber(v1, ref n1) || !IsNumber(v2, ref n2) || !IsNumber(v3, ref n3)
            || !TryTakeInlineChecks(frame))
        {
            return false;
        }

        var result = body.Compute(n0, n1, n2, n3);
        computed = body.IsBoolean ? new KernelResult(result != 0 ? JsBoolean.True : JsBoolean.False, 0) : new KernelResult(null, result);
        return true;
    }

    /// <summary>
    /// Whether the tree computes the body of the definition just probed in place: a call through the register lane
    /// of a body <see cref="KernelInlineBody"/> accepts, which never reads <c>this</c>, as compiled code computes it.
    /// </summary>
    private void ProbeInlineBody()
    {
        _inlineBody = null;
        _inlineSite = null;
        if (_registerState is not null && _registerDefinition is { } definition)
        {
            var count = _arguments.Length;
            var probe = definition._inlineProbe;
            if (probe is null || probe.ArgumentCount != count)
            {
                var body = KernelInlineBody.TryCreate(definition, count);
                probe = definition._inlineProbe = new KernelInlineProbe(count, body, body is null ? null : new KernelInlineSite(definition, body.MathKinds.ToArray()));
            }

            _inlineBody = probe.Body;
            _inlineSite = probe.Site;
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
        return CallScriptWithNumbers(frame, function, thisValue, state, new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count), out number);
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
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue CallRegisters(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state)
    {
        var arguments = _arguments;
        var count = arguments.Length;
        double n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        var v0 = count >= 1 ? arguments[0].Evaluate(frame, out n0) : null;
        var v1 = count >= 2 ? arguments[1].Evaluate(frame, out n1) : null;
        var v2 = count >= 3 ? arguments[2].Evaluate(frame, out n2) : null;
        var v3 = count >= 4 ? arguments[3].Evaluate(frame, out n3) : null;
        return CallFromNumbers(frame, function, thisValue, state, new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count));
    }

    /// <summary>The call of a function whose body runs on a kernel, its arguments evaluated in order; a Number result stays unboxed.</summary>
    private JsValue? CallKernel(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, FunctionKernel kernel, out double number)
    {
        var arguments = _arguments;
        var count = arguments.Length;
        double n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        var v0 = count >= 1 ? arguments[0].Evaluate(frame, out n0) : null;
        var v1 = count >= 2 ? arguments[1].Evaluate(frame, out n1) : null;
        var v2 = count >= 3 ? arguments[2].Evaluate(frame, out n2) : null;
        var v3 = count >= 4 ? arguments[3].Evaluate(frame, out n3) : null;
        return CallKernel(frame, function, thisValue, state, kernel, new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count), out number);
    }

    private JsValue? CallKernel(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, FunctionKernel kernel, in NumberArguments arguments, out double number)
    {
        var engine = frame.Engine;
        if (engine.CanPushCallFrame && kernel.RunsInFrames(state) && !frame.Context.CompletionValuesObservable && engine._stackGuard.HasVerifiedHeadroom())
        {
            var oldPositions = engine.MarkLastSyntaxPositions();
            var oldSource = engine._lastSyntaxLocation.SourceFile;
            var result = function.CallKernelInFrame(CallHandler, thisValue, in arguments, state, kernel, out number);
            engine.RestoreLastSyntaxElement(oldPositions, oldSource);
            return result;
        }

        return CallKernelWithStackFrame(frame, function, thisValue, state, kernel, in arguments, out number);
    }

    /// <summary>The call of a function whose body runs on a kernel, with its call stack frame and execution context pushed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue? CallKernelWithStackFrame(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, FunctionKernel kernel, in NumberArguments arguments, out double number)
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

        JsValue? result;
        try
        {
            result = function.CallKernel(thisValue, in arguments, state, kernel, out number);
        }
        finally
        {
            callStack.TryPopAndDiscard();
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return result;
    }

    /// <summary>The call of an interpreted function through the register lane with arguments a kernel evaluated.</summary>
    private JsValue? CallScriptWithNumbers(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, in NumberArguments arguments, out double number)
    {
        if (function._functionDefinition!._kernel is { } kernel)
        {
            return CallKernel(frame, function, thisValue, state, kernel, in arguments, out number);
        }

        number = 0;
        return CallFromNumbers(frame, function, thisValue, state, in arguments);
    }

    /// <summary>The call of an interpreted function through the register lane with its Numbers unboxed.</summary>
    private JsValue CallFromNumbers(KernelFrame frame, ScriptFunction function, JsValue thisValue, JintFunctionDefinition.State state, in NumberArguments arguments)
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
            result = function.CallFromNumbers(thisValue, in arguments, state);
        }
        finally
        {
            callStack.TryPopAndDiscard();
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return result;
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
        // The lane of this built-in, which an argument may have recorded another callee over.
        var lane = ReferenceEquals(function, _fastCallee) ? _builtinLane : LaneOf(function);
        if (lane == ListLane)
        {
            var count = _arguments.Length;
            var values = count == 0 ? [] : frame.Engine._jsValueArrayPool.RentArray(count);
            if (count > 0)
            {
                values[0] = arg0;
                if (count > 1)
                {
                    values[1] = arg1;
                }
            }

            return CallListed(frame, function, thisValue, values);
        }

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
            result = lane == VariadicLane ? CallVariadic(function, thisValue, arg0, arg1) : function.CallFast(thisValue, arg0, arg1);
        }
        finally
        {
            callStack.TryPopAndDiscard();
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return result;
    }

    /// <summary>How a built-in is called at a site of this many arguments; see <see cref="ProbeBuiltin"/>.</summary>
    private byte LaneOf(Function function)
    {
        var count = _arguments.Length;
        var shape = count <= 2 ? function.GetFastCallShape(count) : default;
        return !shape.Supported ? ListLane : shape.Variadic ? VariadicLane : FastLane;
    }

    // The call's handler, whose node the call stack frame reports the call at.
    internal JintExpression CallHandler => _handler ??= JintExpression.Build(_expression);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue Interpret(KernelFrame frame) => (_handler ??= JintExpression.Build(_expression)).GetValue(frame.Context);
}

/// <summary>
/// A call whose last argument is a spread, of a callee read purely: the list made as the interpreter makes it, an array
/// whose iteration is the built-in one read by index; a callee that is not a function leaves the call to the interpreter.
/// </summary>
internal sealed class KernelSpreadCall : KernelExpression
{
    internal const int MaxLeading = 3;

    internal readonly KernelExpression? _base;
    internal readonly KernelProperty? _member;
    internal readonly KernelExpression? _callee;
    internal readonly KernelExpression[] _arguments;
    private readonly SpreadElement _spread;
    private JintSpreadExpression? _spreadHandler;
    private JintExpression? _handler;

    public KernelSpreadCall(CallExpression expression, KernelProperty? member, KernelExpression? callee, KernelExpression[] arguments) : base(expression)
    {
        _member = member;
        _base = member?.Object;
        _callee = callee;
        _arguments = arguments;
        _spread = (SpreadElement) expression.Arguments[expression.Arguments.Count - 1];
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        if (!frame.Engine._stackGuard.TryEnterOnCurrentStack())
        {
            return Interpret(frame);
        }

        JsValue thisValue;
        JsValue? callee;
        if (_member is not null)
        {
            var target = _base!.Evaluate(frame, out var targetNumber);
            if (target is null)
            {
                target = JsNumber.Create(targetNumber);
            }
            else if (ReferenceEquals(target, Bail))
            {
                return Interpret(frame);
            }

            thisValue = target;
            callee = _member.ReadCallee(target, frame.Engine);
        }
        else
        {
            thisValue = JsValue.Undefined;
            callee = _callee!.Evaluate(frame, out _);
        }

        if (callee is not Function function || (function is not ScriptFunction && function._functionDefinition is not null))
        {
            return Interpret(frame);
        }

        frame.Work += function is ScriptFunction ? KernelCall.ScriptCallWork : KernelCall.BuiltinCallWork;
        var values = Arguments(frame, out var rented);
        if (function is ScriptFunction script && values.Length <= 4 && InFrameState(frame, script, out var kernel) is { } state)
        {
            return CallInFrame(frame, script, thisValue, values, rented, state, kernel!, out number);
        }

        return Call(frame, function, thisValue, values, rented);
    }

    /// <summary>The argument list: the leading arguments, then the spread's values.</summary>
    private JsValue[] Arguments(KernelFrame frame, out bool rented)
    {
        var engine = frame.Engine;
        var arguments = _arguments;
        var leading = arguments.Length - 1;
        var a0 = leading > 0 ? Box(arguments[0].Evaluate(frame, out var n0), n0) : null;
        var a1 = leading > 1 ? Box(arguments[1].Evaluate(frame, out var n1), n1) : null;
        var a2 = leading > 2 ? Box(arguments[2].Evaluate(frame, out var n2), n2) : null;
        if (frame.RestPending && arguments[leading] is KernelRestSpread && KernelRestSpread.SpreadsAsItsArguments(frame))
        {
            // A rest array not made yet: the call's arguments past the rest parameter, as reading the array by index gives them.
            var restSlot = frame.ArgumentsState!.RestParameterSlot;
            var count = Math.Max(frame.PendingArguments.Count - restSlot, 0);
            if (engine._hasMaxArraySize)
            {
                ArrayInstance.ValidateListLength(engine, (ulong) leading + (ulong) count);
            }

            var spreadValues = engine._jsValueArrayPool.RentArray(leading + count);
            Lead(spreadValues, leading, a0, a1, a2);
            for (var j = 0; j < count; j++)
            {
                spreadValues[leading + j] = frame.PendingArguments.At(restSlot + j);
            }

            rented = true;
            return spreadValues;
        }

        var argument = Box(arguments[leading].Evaluate(frame, out var n), n);

        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);
        JsValue? iteratorMethod = null;
        if (argument is JsArray array && array.HasOriginalIterator(out iteratorMethod))
        {
            var length = array.GetLength();
            if (engine._hasMaxArraySize)
            {
                ArrayInstance.ValidateListLength(engine, (ulong) leading + length);
            }

            if (length <= int.MaxValue - MaxLeading)
            {
                var values = engine._jsValueArrayPool.RentArray(leading + (int) length);
                Lead(values, leading, a0, a1, a2);
                for (var j = 0; j < (int) length; j++)
                {
                    values[leading + j] = array.GetElement((uint) j);
                }

                engine.RestoreLastSyntaxElement(in oldSyntaxElement);
                rented = true;
                return values;
            }
        }

        var target = new List<JsValue>(leading + 4);
        if (leading > 0)
        {
            target.Add(a0!);
            if (leading > 1)
            {
                target.Add(a1!);
                if (leading > 2)
                {
                    target.Add(a2!);
                }
            }
        }

        var iterator = (_spreadHandler ??= new JintSpreadExpression(_spread)).GetIterator(engine.Realm, argument, iteratorMethod);
        ExpressionCache.SpreadInto(engine, target, iterator);
        if (engine._hasMaxArraySize)
        {
            ArrayInstance.ValidateListLength(engine, (uint) target.Count);
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        rented = false;
        return target.ToArray();
    }

    private static void Lead(JsValue[] values, int leading, JsValue? a0, JsValue? a1, JsValue? a2)
    {
        if (leading > 0)
        {
            values[0] = a0!;
            if (leading > 1)
            {
                values[1] = a1!;
                if (leading > 2)
                {
                    values[2] = a2!;
                }
            }
        }
    }

    /// <summary>The register state of a function whose body runs on a kernel in its frames, or null.</summary>
    private static JintFunctionDefinition.State? InFrameState(KernelFrame frame, ScriptFunction function, out FunctionKernel? kernel)
    {
        var engine = frame.Engine;
        kernel = function._functionDefinition!._kernel;
        return kernel is not null
               && !function._isClassConstructor
               && function._functionDefinition.BuiltState is { SupportsRegisterCall: true } state
               && engine.CanPushCallFrame
               && kernel.RunsInFrames(state)
               && !frame.Context.CompletionValuesObservable
               && engine._stackGuard.HasVerifiedHeadroom()
            ? state
            : null;
    }

    /// <summary>The call of a function whose body runs on a kernel, in a frame of the kernel, as a call without spread makes it.</summary>
    private JsValue? CallInFrame(KernelFrame frame, ScriptFunction function, JsValue thisValue, JsValue[] values, bool rented, JintFunctionDefinition.State state, FunctionKernel kernel, out double number)
    {
        var engine = frame.Engine;
        var count = values.Length;
        var arguments = new NumberArguments(
            count > 0 ? values[0] : null, 0,
            count > 1 ? values[1] : null, 0,
            count > 2 ? values[2] : null, 0,
            count > 3 ? values[3] : null, 0,
            count);
        var oldPositions = engine.MarkLastSyntaxPositions();
        var oldSource = engine._lastSyntaxLocation.SourceFile;
        var result = function.CallKernelInFrame(CallHandler, thisValue, in arguments, state, kernel, out number);
        engine.RestoreLastSyntaxElement(oldPositions, oldSource);
        if (rented && count > 0)
        {
            engine._jsValueArrayPool.ReturnArray(values);
        }

        return result;
    }

    /// <summary>The call with its list, through a call stack frame as JintCallExpression makes it.</summary>
    private JsValue Call(KernelFrame frame, Function function, JsValue thisValue, JsValue[] values, bool rented)
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
            result = function is ScriptFunction script ? script.CallWithStackFrame(thisValue, values) : function.Call(thisValue, values);
        }
        finally
        {
            callStack.TryPopAndDiscard();
        }

        if (rented && values.Length > 0)
        {
            engine._jsValueArrayPool.ReturnArray(values);
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return result;
    }

    private JintExpression CallHandler => _handler ??= JintExpression.Build(_expression);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue Interpret(KernelFrame frame) => CallHandler.GetValue(frame.Context);
}

/// <summary>
/// <c>new C(arguments)</c> (https://tc39.es/ecma262/#sec-evaluatenew) of a callee read purely, without spread; a callee
/// that is not a constructor leaves the whole expression to the interpreter before an argument runs.
/// </summary>
internal sealed class KernelNew : KernelExpression
{
    internal readonly KernelExpression _callee;
    internal readonly KernelExpression[] _arguments;
    private JintExpression? _handler;

    // The argument list where it has a spread.
    private readonly KernelSpreadList? _spread;

    public KernelNew(NewExpression expression, KernelExpression callee, KernelExpression[] arguments) : base(expression)
    {
        _callee = callee;
        _arguments = arguments;
        if (KernelSpreadList.SpreadsOf(expression.Arguments) is { } spreads)
        {
            _spread = new KernelSpreadList(KernelLocation.After(expression.Callee) ?? expression, expression.Arguments, spreads, arguments);
        }
    }

    private JintExpression Handler => _handler ??= JintExpression.Build(_expression);

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        var callee = _callee.Evaluate(frame, out _);
        if (callee is not Function function || !function.IsConstructor)
        {
            return Interpret(frame);
        }

        var engine = frame.Engine;
        var arguments = _arguments;
        if (_spread is not null)
        {
            return ConstructWithSpread(frame, function);
        }

        if (function is ScriptFunction script && FramedState(frame, script, arguments.Length, out var kernel) is { } state)
        {
            return ConstructInFrame(frame, script, state, kernel!);
        }

        var values = arguments.Length == 0 ? [] : engine._jsValueArrayPool.RentArray(arguments.Length);
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i] = Box(arguments[i].Evaluate(frame, out var n), n);
        }

        var oldSyntaxElement = engine.MarkLastSyntaxElement();
        frame.Context.PrepareFor(_expression);
        var instance = engine.Construct(function, values, function, Handler);
        if (values.Length > 0)
        {
            engine._jsValueArrayPool.ReturnArray(values);
        }

        engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        return instance;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue Interpret(KernelFrame frame) => Handler.GetValue(frame.Context);

    /// <summary>The <c>new</c> with an argument list that has a spread, made as the interpreter makes it.</summary>
    private JsValue ConstructWithSpread(KernelFrame frame, Function function)
    {
        var engine = frame.Engine;
        var values = _spread!.BuildArray(frame, rent: true, out var rented);
        var count = values.Length;
        ObjectInstance? instance = null;
        if (count <= 4 && function is ScriptFunction script && FramedState(frame, script, count, out var kernel) is { } state)
        {
            var arguments = new NumberArguments(
                count > 0 ? values[0] : null, 0,
                count > 1 ? values[1] : null, 0,
                count > 2 ? values[2] : null, 0,
                count > 3 ? values[3] : null, 0,
                count);
            var oldPositions = engine.MarkLastSyntaxPositions();
            var oldSource = engine._lastSyntaxLocation.SourceFile;
            instance = script.ConstructKernelInFrame(Handler, in arguments, state, kernel!);
            engine.RestoreLastSyntaxElement(oldPositions, oldSource);
        }

        if (instance is null)
        {
            var oldSyntaxElement = engine.MarkLastSyntaxElement();
            frame.Context.PrepareFor(_expression);
            instance = engine.Construct(function, values, function, Handler);
            engine.RestoreLastSyntaxElement(in oldSyntaxElement);
        }

        if (rented)
        {
            engine._jsValueArrayPool.ReturnArray(values);
        }

        return instance;
    }

    /// <summary>The state of an ordinary function whose body runs on a kernel in its frames, which a <c>new</c> then calls straight into; or null.</summary>
    private static JintFunctionDefinition.State? FramedState(KernelFrame frame, ScriptFunction function, int count, out FunctionKernel? kernel)
    {
        var engine = frame.Engine;
        kernel = function._functionDefinition?._kernel;
        return count <= 4
               && function._constructorKind == ConstructorKind.Base
               && !function._isClassConstructor
               && kernel is not null
               && function._functionDefinition!.BuiltState is { SupportsRegisterCall: true } state
               && kernel.RunsInFrames(state)
               && engine.CanPushCallFrame
               && !frame.Context.CompletionValuesObservable
               && engine._stackGuard.HasVerifiedHeadroom()
            ? state
            : null;
    }

    /// <summary>The <c>new</c> of <paramref name="function"/> in a frame of its kernel, its arguments evaluated in order.</summary>
    private JsValue ConstructInFrame(KernelFrame frame, ScriptFunction function, JintFunctionDefinition.State state, FunctionKernel kernel)
    {
        var arguments = _arguments;
        var count = arguments.Length;
        double n0 = 0, n1 = 0, n2 = 0, n3 = 0;
        var v0 = count >= 1 ? arguments[0].Evaluate(frame, out n0) : null;
        var v1 = count >= 2 ? arguments[1].Evaluate(frame, out n1) : null;
        var v2 = count >= 3 ? arguments[2].Evaluate(frame, out n2) : null;
        var v3 = count >= 4 ? arguments[3].Evaluate(frame, out n3) : null;
        var values = new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count);

        var engine = frame.Engine;
        var oldPositions = engine.MarkLastSyntaxPositions();
        var oldSource = engine._lastSyntaxLocation.SourceFile;
        var instance = function.ConstructKernelInFrame(Handler, in values, state, kernel);
        if (instance is null)
        {
            // A prototype that is not a data property: [[Construct]] reads it, with the arguments already evaluated.
            var list = count == 0 ? [] : engine._jsValueArrayPool.RentArray(count);
            if (count > 0)
            {
                list[0] = Box(v0, n0);
            }

            if (count > 1)
            {
                list[1] = Box(v1, n1);
            }

            if (count > 2)
            {
                list[2] = Box(v2, n2);
            }

            if (count > 3)
            {
                list[3] = Box(v3, n3);
            }

            frame.Context.PrepareFor(_expression);
            instance = engine.Construct(function, list, function, Handler);
            if (count > 0)
            {
                engine._jsValueArrayPool.ReturnArray(list);
            }
        }

        engine.RestoreLastSyntaxElement(oldPositions, oldSource);
        return instance;
    }
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

/// <summary>The spread argument naming a rest parameter whose array a call may have put off: made first where it was.</summary>
internal sealed class KernelRestSpread : KernelExpression
{
    private readonly KernelExpression _read;

    public KernelRestSpread(Expression expression, KernelExpression read) : base(expression)
    {
        _read = read;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        if (frame.RestPending)
        {
            frame.MaterializeRest();
        }

        return _read.Evaluate(frame, out number);
    }

    /// <summary>Whether spreading a rest array made now would read it by index: its realm's Array.prototype iterates as built.</summary>
    internal static bool SpreadsAsItsArguments(KernelFrame frame)
    {
        var engine = frame.Engine;
        var prototype = engine.RunningRealm.Intrinsics.Array.PrototypeObject;
        var descriptor = prototype.GetOwnProperty(GlobalSymbolRegistry.Iterator);
        return (descriptor._flags & PropertyFlag.CustomJsValue) == PropertyFlag.None
               && descriptor.IsDataDescriptor()
               && ReferenceEquals(descriptor._value, prototype._originalIteratorFunction)
               && (!engine._arrayIteratorNextMayBeReplaced || prototype.ArrayIteratorsStepNatively());
    }
}
