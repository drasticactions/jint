using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Jint.Constraints;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.TypedArray;
using Jint.Runtime.Environments;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// The .NET code a <see cref="KernelForStatement"/> was compiled to (see <c>KernelEmitter</c>): the same steps as the
/// kernel's tree, with Numbers in locals, names read through the slots they resolved to on entry, and no call per node.
/// Every case the tree hands back to the interpreter is handed back at the same point, every case the code does not
/// compile runs the tree's own node, and the statement checks run at the same statements, so a run of the code and a
/// run of the tree do the same observable things. It can be entered where the tree loop starts or at the head of an
/// iteration the tree loop is about to begin, with the state the tree has left in the environments.
/// </summary>
internal sealed class KernelCode
{
    /// <summary>
    /// The code from <paramref name="entry"/> (<see cref="StartEntry"/>, or a loop's <see cref="KernelForStatement._entry"/>
    /// for that loop's head): 0 for a normal completion of the outermost loop, 3 for an abrupt one, left in
    /// <see cref="KernelFrame.Abrupt"/>.
    /// </summary>
    internal delegate int Entry(object[] constants, KernelFrame frame, int entry);

    /// <summary>
    /// The code of a function body over its constants, entered at <see cref="StartEntry"/>: the value it returns,
    /// undefined when it completes normally, or null for an abrupt completion left in <see cref="KernelFrame.Abrupt"/>.
    /// </summary>
    internal delegate JsValue? FunctionEntry(KernelFrame frame, int entry);

    /// <summary>The entry where the outermost loop starts.</summary>
    internal const int StartEntry = -1;

    /// <summary>
    /// How many statements the tree of a kernel checks per node of the kernel before the kernel is compiled: the JIT
    /// takes about as long to compile a node as the tree takes to run this many statements (a few hundred
    /// microseconds), and the code runs them in a fraction of that.
    /// </summary>
    internal const int DefaultThreshold = 16384;

    private readonly Entry _entry = null!;
    private readonly FunctionEntry _functionEntry = null!;
    private readonly object[] _constants;

    internal KernelCode(Entry entry, object[] constants)
    {
        _entry = entry;
        _constants = constants;
    }

    internal KernelCode(FunctionEntry entry, object[] constants)
    {
        _functionEntry = entry;
        _constants = constants;
    }

    /// <summary>Whether this runtime compiles code at run time to machine code; where it does not, kernels stay trees.</summary>
    internal static bool Supported
    {
        get
        {
            return RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled;
        }
    }

    internal int Run(KernelFrame frame, int entry)
    {
        frame.Engine._loopKernelCodeRuns++;
        frame.InCode = true;
        try
        {
            return _entry(_constants, frame, entry);
        }
        finally
        {
            frame.InCode = false;
        }
    }

    /// <summary>A function body's code from its start (see <see cref="FunctionEntry"/>).</summary>
    internal JsValue? RunFunction(KernelFrame frame)
    {
        // The program's Leave clears the flag.
        frame.InCode = true;
        return _functionEntry(frame, StartEntry);
    }

    /// <summary>The loop's code, or null where code cannot be compiled.</summary>
    internal static KernelCode? TryCompile(KernelForStatement loop) => TryCompile(loop, null, 0);

    /// <summary>A function body's code, or null where code cannot be compiled.</summary>
    internal static KernelCode? TryCompile(KernelBlockStatement body, int loops, int calls, Key[]? layout, int[] localSlots) => TryCompile(null, body, loops, calls, layout, localSlots);

    private static KernelCode? TryCompile(KernelForStatement? loop, KernelBlockStatement? body, int loops, int calls = 0, Key[]? layout = null, int[]? localSlots = null)
    {
        // The guard stated here, beside the call, so that the trimming and AOT analyses fold it.
        if (!RuntimeFeature.IsDynamicCodeSupported || !RuntimeFeature.IsDynamicCodeCompiled)
        {
            return null;
        }

        try
        {
            return loop is not null ? KernelEmitter.Compile(loop) : KernelEmitter.Compile(body!, loops, calls, layout, localSlots!);
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NotSupportedException or MissingMemberException or TypeInitializationException)
        {
            // No dynamic code after all, or a member compiled code calls was trimmed away: the kernel stays a tree.
            return null;
        }
    }
}

/// <summary>
/// The Math functions compiled code computes itself for a Number argument, each exactly as its built-in does: no
/// script runs, nothing throws, and the result is the same double.
/// </summary>
internal static class KernelIntrinsics
{
    internal const int None = 0;
    internal const int Abs = 1;
    internal const int Floor = 2;
    internal const int Ceil = 3;
    internal const int Sqrt = 4;
    internal const int Count = 5;

    private static readonly string[] Names = ["", "abs", "floor", "ceil", "sqrt"];

    internal static string Name(int kind) => Names[kind];

    /// <summary>The function a Math property of this name is, or <see cref="None"/>.</summary>
    internal static int Kind(string name)
    {
        var kind = System.Array.IndexOf(Names, name);
        return kind > 0 ? kind : None;
    }

    /// <summary>Function <paramref name="kind"/> of a Number, as its built-in computes it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Compute(int kind, double value) => kind switch
    {
        Abs => System.Math.Abs(value),
        Floor => System.Math.Floor(value),
        Ceil => System.Math.Ceiling(value),
        _ => System.Math.Sqrt(value),
    };

    /// <summary>Which of the functions <paramref name="function"/> is in its realm, or <see cref="None"/>.</summary>
    internal static int Identify(Native.Function.Function function)
    {
        // The Math object made its functions itself, so this one is among them if it is one of them.
        var functions = function._realm.Intrinsics.Math._kernelFunctions;
        if (functions is not null)
        {
            for (var kind = Abs; kind < functions.Length; kind++)
            {
                if (ReferenceEquals(functions[kind], function))
                {
                    return kind;
                }
            }
        }

        return None;
    }

    /// <summary>The .NET method that computes function <paramref name="kind"/> of a double.</summary>
    internal static System.Reflection.MethodInfo Method(int kind) => typeof(System.Math).GetMethod(kind switch
    {
        Abs => nameof(System.Math.Abs),
        Floor => nameof(System.Math.Floor),
        Ceil => nameof(System.Math.Ceiling),
        _ => nameof(System.Math.Sqrt),
    }, [typeof(double)])!;
}

/// <summary>
/// A call site where compiled code computes the body of the function it calls itself: a function whose body is
/// <c>return</c> of Number arithmetic over its parameters, literals and the Math functions of
/// <see cref="KernelIntrinsics"/> (see <c>KernelEmitter</c>). The code does so for a callee of that definition only
/// while nothing a call does can be seen: no recursion limit or profiler counts the call, the statement checks of the
/// body would pass without running anything, every argument is a Number, and a name <c>Math</c> in the body is the
/// realm's Math object with its functions in place.
/// </summary>
internal sealed class KernelInlineSite
{
    internal readonly Interpreter.JintFunctionDefinition Definition;
    private readonly int[] _mathKinds;

    // What the last accepted callee's Math resolved to: the global environment, the global object's descriptor of
    // Math and the Math object's descriptors of the functions, valid while neither object's own properties nor the
    // global lexical declarations changed and the values are the same.
    private GlobalEnvironment? _global;
    private uint _globalVersion;
    private int _lexicalVersion;
    private Descriptors.PropertyDescriptor? _mathDescriptor;
    private ObjectInstance? _math;
    private uint _mathVersion;
    private Descriptors.PropertyDescriptor?[]? _functionDescriptors;
    private Native.Function.Function?[]? _functions;

    internal KernelInlineSite(Interpreter.JintFunctionDefinition definition, int[] mathKinds)
    {
        Definition = definition;
        _mathKinds = mathKinds;
    }

    /// <summary>Whether a call of <paramref name="callee"/> may be computed in place, when its arguments are Numbers.</summary>
    internal bool Accepts(JsValue callee, Engine engine)
    {
        if (callee is not Native.Function.ScriptFunction function
            || !ReferenceEquals(function._functionDefinition, Definition)
            || function._isClassConstructor
            || !engine._callStack.CountsNoCalls)
        {
            return false;
        }

        return _mathKinds.Length == 0 || MathHolds(function);
    }

    private bool MathHolds(Native.Function.ScriptFunction function)
    {
        if (function._environment is not GlobalEnvironment global)
        {
            return false;
        }

        var math = function._realm.Intrinsics.Math;
        if (ReferenceEquals(global, _global)
            && global._globalObject is { } globalObject
            && globalObject._propertiesVersion == _globalVersion
            && global._lexicalMutations == _lexicalVersion
            && ReferenceEquals(_mathDescriptor!._value, math)
            && ReferenceEquals(math, _math)
            && math._propertiesVersion == _mathVersion
            && FunctionsHold())
        {
            return true;
        }

        _global = null;
        if (global._globalObject is not { } owner || global._declarativeRecord.HasBinding("Math"))
        {
            return false;
        }

        var descriptor = owner.GetOwnProperty("Math");
        if (!descriptor.IsDataDescriptor() || (descriptor._flags & Descriptors.PropertyFlag.CustomJsValue) != Descriptors.PropertyFlag.None || !ReferenceEquals(descriptor._value, math))
        {
            return false;
        }

        var descriptors = new Descriptors.PropertyDescriptor?[_mathKinds.Length];
        var functions = new Native.Function.Function?[_mathKinds.Length];
        for (var i = 0; i < _mathKinds.Length; i++)
        {
            var own = math.GetOwnProperty(KernelIntrinsics.Name(_mathKinds[i]));
            if (!own.IsDataDescriptor()
                || (own._flags & Descriptors.PropertyFlag.CustomJsValue) != Descriptors.PropertyFlag.None
                || own._value is not Native.Function.Function intrinsic
                || KernelIntrinsics.Identify(intrinsic) != _mathKinds[i])
            {
                return false;
            }

            descriptors[i] = own;
            functions[i] = intrinsic;
        }

        _global = global;
        _globalVersion = owner._propertiesVersion;
        _lexicalVersion = global._lexicalMutations;
        _mathDescriptor = descriptor;
        _math = math;
        _mathVersion = math._propertiesVersion;
        _functionDescriptors = descriptors;
        _functions = functions;
        return true;
    }

    private bool FunctionsHold()
    {
        for (var i = 0; i < _functions!.Length; i++)
        {
            if (!ReferenceEquals(_functionDescriptors![i]!._value, _functions[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// What a kernel expression answers, as compiled code holds it: a null <see cref="Value"/> with the
/// <see cref="Number"/>, or the value, or Bail. Returned in two registers, and kept in two once inlined, where an out
/// parameter would keep the Number in memory.
/// </summary>
internal readonly struct KernelResult
{
    internal readonly JsValue? Value;
    internal readonly double Number;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal KernelResult(JsValue? value, double number)
    {
        Value = value;
        Number = number;
    }

    internal static KernelResult Bail
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(KernelExpression.Bail, 0);
    }
}

/// <summary>
/// A read of a name compiled code does not hold in a local: the value as <see cref="KernelResult"/> holds it, and
/// whether the code may hold it until anything else runs (<see cref="MutableNumber"/> for a Number in a slot the code
/// may also store it to, <see cref="Value"/> for any other value it read).
/// </summary>
internal readonly struct KernelNameRead
{
    internal const int Uncached = 0;
    internal const int MutableNumber = 1;
    internal const int Value = 2;

    internal readonly JsValue? Result;
    internal readonly double Number;
    internal readonly int Kind;

    internal KernelNameRead(JsValue? result, double number, int kind)
    {
        Result = result;
        Number = number;
        Kind = kind;
    }
}

/// <summary>
/// What compiled code may assume of a Uint8Array a name holds until anything else runs: its elements are
/// <c>Data[Offset .. Offset + Length)</c>, read and written in place. Nothing but script can detach, resize or replace
/// the buffer, and compiled code drops the facts with the value before anything else runs.
/// </summary>
internal readonly struct KernelTypedFacts
{
    internal readonly byte[]? Data;
    internal readonly int Offset;
    internal readonly int Length;

    internal KernelTypedFacts(byte[] data, int offset, int length)
    {
        Data = data;
        Offset = offset;
        Length = length;
    }
}

/// <summary>
/// The steps of the kernel nodes that compiled code calls, each the same code as the node's own, small enough to be
/// inlined into it.
/// </summary>
internal static class KernelCodeRuntime
{
    /// <summary>
    /// <paramref name="bits"/> for a name in the call environment of a function no closure, eval or arguments object
    /// reaches, whose bindings only the function's own code reads and writes; 0 for any other.
    /// </summary>
    internal static int KeptAcrossCalls(DeclarativeEnvironment? environment, int bits)
        => environment is FunctionEnvironment { _functionObject: Native.Function.ScriptFunction { _functionDefinition.BuiltState: { UseFixedSlots: true, EnvironmentMayEscape: false } } }
            ? bits
            : 0;

    /// <summary>
    /// The slot name <paramref name="binding"/> resolves to in this run, resolved now if it was not; a null reference
    /// for a name that is not in a slot, which the code reads through its node.
    /// </summary>
    internal static ref Binding Bind(KernelFrame frame, int binding, out DeclarativeEnvironment? environment)
    {
        if (frame.NeedsResolving(binding))
        {
            frame.Resolve(binding);
        }

        ref var slot = ref frame.Slots[binding];
        environment = slot.Environment;
        if (environment?._slots is { } slots && (uint) slot.Index < (uint) slots.Length)
        {
            return ref slots[slot.Index];
        }

        environment = null;
        return ref Unsafe.NullRef<Binding>();
    }

    /// <summary>
    /// <see cref="Bind"/> for a name a function body declares, at <paramref name="index"/> of <paramref name="slots"/>,
    /// the slots of the environment the run was entered in when it has the body's layout.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref Binding BindLocal(KernelFrame frame, int binding, int index, Binding[]? slots, out DeclarativeEnvironment? environment)
    {
        if (slots is not null && (uint) index < (uint) slots.Length)
        {
            environment = Unsafe.As<DeclarativeEnvironment>(frame.Start);
            return ref slots[index];
        }

        return ref Bind(frame, binding, out environment);
    }

    /// <summary>The slots of <paramref name="start"/> when it is an environment of the layout a function body was compiled for.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Binding[]? LocalSlots(Environment start, Key[] layout, Engine engine)
        => start is DeclarativeEnvironment declarative
           && ReferenceEquals(declarative._slotNames, layout)
           && declarative._dictionary is not { Count: > 0 }
           && ReferenceEquals(declarative._engine, engine)
            ? declarative._slots
            : null;

    /// <summary><see cref="KernelName.Evaluate"/> of a name whose slot holds: the Number, or the value, or Bail.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static KernelResult ReadSlot(ref Binding binding)
    {
        if (binding.IsUnboxedNumber)
        {
            return new KernelResult(null, binding.UnboxedNumber);
        }

        var value = binding.IsPlainValue ? binding.PeekValue : null;
        return value is not null && (value._type & InternalTypes.ReadHook) == InternalTypes.Empty ? new KernelResult(value, 0) : KernelResult.Bail;
    }

    /// <summary>
    /// <see cref="KernelName.Evaluate"/> for compiled code: through the slot the name resolved to on entry while it
    /// holds (no binding injected since <paramref name="epoch"/>), through the node otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelNameRead ReadNameCold(ref Binding slot, KernelName name, KernelFrame frame, int epoch)
    {
        KernelResult result;
        if (!Unsafe.IsNullRef(ref slot) && frame.Engine._envBindingInjectionEpoch == epoch)
        {
            if (slot.TryGetMutableNumber(out var number))
            {
                return new KernelNameRead(null, number, KernelNameRead.MutableNumber);
            }

            result = ReadSlot(ref slot);
        }
        else
        {
            result = ReadName(name, frame);
        }

        var value = result.Value;
        var kind = value is not null && !ReferenceEquals(value, KernelExpression.Bail) ? KernelNameRead.Value : KernelNameRead.Uncached;
        return new KernelNameRead(value, result.Number, kind);
    }

    /// <summary><see cref="ReadNameCold"/> with the read of a slot that holds inline, for code that reads it first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static KernelNameRead ReadNameFirst(ref Binding slot, KernelName name, KernelFrame frame, int epoch)
    {
        if (!Unsafe.IsNullRef(ref slot) && frame.Engine._envBindingInjectionEpoch == epoch)
        {
            if (slot.TryGetMutableNumber(out var number))
            {
                return new KernelNameRead(null, number, KernelNameRead.MutableNumber);
            }

            if (slot.IsPlainValue && slot.PeekValue is { } value && (value._type & InternalTypes.ReadHook) == InternalTypes.Empty)
            {
                return new KernelNameRead(value, 0, KernelNameRead.Value);
            }
        }

        return ReadNameCold(ref slot, name, frame, epoch);
    }

    /// <summary><see cref="KernelName.TryReadNumber"/> of a name whose slot holds: the Number, or Bail.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static KernelResult ReadSlotNumber(ref Binding binding)
    {
        if (binding.IsUnboxedNumber)
        {
            return new KernelResult(null, binding.UnboxedNumber);
        }

        if (binding.PeekValue is { } value && (value._type & (InternalTypes.Number | InternalTypes.Integer)) != InternalTypes.Empty)
        {
            return new KernelResult(null, Unsafe.As<JsNumber>(value)._value);
        }

        return KernelResult.Bail;
    }

    /// <summary>A node's <see cref="KernelExpression.Evaluate"/>.</summary>
    internal static KernelResult Evaluate(KernelExpression expression, KernelFrame frame)
    {
        var value = expression.Evaluate(frame, out var number);
        return new KernelResult(value, number);
    }

    /// <summary><see cref="KernelName.Evaluate"/>, for a name read through its node.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelResult EvaluateName(KernelName name, KernelFrame frame)
    {
        var value = name.Evaluate(frame, out var number);
        return new KernelResult(value, number);
    }

    /// <summary>
    /// <see cref="KernelName.Evaluate"/> of a name not read through a slot: the global binding its node last read,
    /// while it holds, as <c>ReadGlobal</c> reads it; anything else through the node.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelResult ReadName(KernelName name, KernelFrame frame)
    {
        ref var slot = ref frame.Slots[name._binding];
        if (slot.Index == KernelFrame.GlobalBinding
            && slot.Epoch == frame.Engine._envBindingInjectionEpoch
            && frame.Global is { } globalEnv
            && ReferenceEquals(globalEnv, name._cachedGlobalEnv)
            && globalEnv._global._propertiesVersion == name._cachedGlobalShapeVersion
            && globalEnv._lexicalMutations == name._cachedGlobalLexicalVersion)
        {
            var value = name._cachedGlobalDescriptor!.Value;
            if (value is not null && (value._type & InternalTypes.ReadHook) == InternalTypes.Empty)
            {
                return new KernelResult(value, 0);
            }
        }

        return EvaluateName(name, frame);
    }

    /// <summary><see cref="KernelExpression.IsNumber"/>: the Number of a value, or Bail.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static KernelResult ToNumber(JsValue? value, double number)
    {
        if (value is null)
        {
            return new KernelResult(null, number);
        }

        if ((value._type & (InternalTypes.Number | InternalTypes.Integer)) != InternalTypes.Empty)
        {
            return new KernelResult(null, Unsafe.As<JsNumber>(value)._value);
        }

        return KernelResult.Bail;
    }

    /// <summary>ToBoolean of a value a kernel expression answered (not Bail).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Truthy(JsValue? value, double number)
        => value is null ? number != 0 && !double.IsNaN(number) : TypeConverter.ToBoolean(value);

    /// <summary>ToBoolean of a Number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Truthy(double number) => number != 0 && !double.IsNaN(number);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue Box(JsValue? value, double number) => value ?? JsNumber.Create(number);

    /// <summary><see cref="KernelElement.Evaluate"/> once its base and key are evaluated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelResult ReadElement(JsValue target, double key)
    {
        if (target is JsTypedArray typedArray)
        {
            return ReadTypedElement(typedArray, key);
        }

        if (target is JsArray array)
        {
            var index = (uint) key;
            if (index == key && index != uint.MaxValue && array.TryGetValueFast(index, out var element))
            {
                return new KernelResult(element, 0);
            }
        }

        return KernelResult.Bail;
    }

    /// <summary><see cref="KernelProperty.Read"/>, with the shape its node last read and a typed array's length inline.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelResult ReadProperty(KernelProperty property, JsValue target)
    {
        if (target is JsObject plain
            && (plain._type & (InternalTypes.ShapeMode | InternalTypes.HasLazySlots)) == InternalTypes.ShapeMode
            && ReferenceEquals(plain.ShapeOf, property._cachedShape))
        {
            return new KernelResult(plain.GetSlot(property._cachedSlot), 0);
        }

        if (property._isLength && target is JsArray array)
        {
            return new KernelResult(null, array.GetLength());
        }

        if (property._isLength && target is JsTypedArray typedArray && property.CachedTypedArrayLengthHolds(typedArray))
        {
            var buffer = typedArray._viewedArrayBuffer;
            if (buffer._arrayBufferData is not null && typedArray._arrayLength != JsTypedArray.LengthAuto && buffer._arrayBufferMaxByteLength is null)
            {
                return new KernelResult(null, typedArray._arrayLength);
            }
        }

        return ReadPropertySlow(property, target);
    }

    /// <summary>The callee of a method call, read as <see cref="KernelProperty.ReadCallee"/> reads it.</summary>
    internal static JsValue? ReadCallee(KernelProperty property, JsValue target)
    {
        if (target is JsObject plain
            && (plain._type & (InternalTypes.ShapeMode | InternalTypes.HasLazySlots)) == InternalTypes.ShapeMode
            && ReferenceEquals(plain.ShapeOf, property._cachedShape))
        {
            return plain.GetSlot(property._cachedSlot);
        }

        return property.ReadCallee(target);
    }

    /// <summary>Whether a value an expression answered is a Number, unboxed into <paramref name="number"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsNumber(JsValue? value, ref double number) => KernelExpression.IsNumber(value, ref number);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static KernelResult ReadPropertySlow(KernelProperty property, JsValue target)
    {
        var value = property.Read(target, out var number);
        return new KernelResult(value, number);
    }

    /// <summary>
    /// <see cref="JsTypedArray.ReadNumberKey"/>, with the elements of a fixed-length view of a fixed-length buffer read
    /// in place as <see cref="JsArrayBuffer.RawBytesToNumeric"/> reads them on a little-endian host.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelResult ReadTypedElement(JsTypedArray typedArray, double key)
    {
        var buffer = typedArray._viewedArrayBuffer;
        if (buffer._arrayBufferData is { } data
            && typedArray._contentType == TypedArrayContentType.Number
            && typedArray._arrayLength != JsTypedArray.LengthAuto
            && buffer._arrayBufferMaxByteLength is null
            && BitConverter.IsLittleEndian)
        {
            // -0 is the key "0"
            if (key >= 0 && key < typedArray._arrayLength)
            {
                var index = (int) key;
                if (index == key)
                {
                    var offset = typedArray._byteOffset;
                    switch (typedArray._arrayElementType)
                    {
                        case TypedArrayElementType.Uint8:
                        case TypedArrayElementType.Uint8C:
                            return new KernelResult(null, data[offset + index]);
                        case TypedArrayElementType.Int8:
                            return new KernelResult(null, (sbyte) data[offset + index]);
                        case TypedArrayElementType.Int16:
                            return new KernelResult(null, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + index * 2)));
                        case TypedArrayElementType.Uint16:
                            return new KernelResult(null, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + index * 2)));
                        case TypedArrayElementType.Int32:
                            return new KernelResult(null, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + index * 4)));
                        case TypedArrayElementType.Uint32:
                            return new KernelResult(null, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + index * 4)));
                        case TypedArrayElementType.Float32:
                            {
                                // A NaN read canonicalizes to the NaN Number value.
                                var single = BitConverter.ToSingle(data, offset + index * 4);
                                return new KernelResult(null, float.IsNaN(single) ? double.NaN : single);
                            }

                        case TypedArrayElementType.Float64:
                            return new KernelResult(null, BitConverter.ToDouble(data, offset + index * 8));
                    }
                }
            }
        }

        return ReadTypedElementSlow(typedArray, key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static KernelResult ReadTypedElementSlow(JsTypedArray typedArray, double key)
    {
        var value = typedArray.ReadNumberKey(key, out var number);
        return new KernelResult(value, number);
    }

    /// <summary>
    /// The facts of a Uint8Array whose elements <see cref="ReadTypedElement"/> and <see cref="StoreTypedElement"/>
    /// read and write in place (a fixed-length view of a fixed-length, mutable buffer on a little-endian host); none
    /// (a null <see cref="KernelTypedFacts.Data"/>) for any other value.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelTypedFacts TypedFacts(JsValue target)
    {
        if (target is JsTypedArray typedArray
            && typedArray._arrayElementType == TypedArrayElementType.Uint8
            && typedArray._contentType == TypedArrayContentType.Number
            && typedArray._arrayLength != JsTypedArray.LengthAuto
            && typedArray._viewedArrayBuffer is { _arrayBufferData: { } data, _arrayBufferMaxByteLength: null, _isImmutable: false }
            && BitConverter.IsLittleEndian)
        {
            return new KernelTypedFacts(data, typedArray._byteOffset, (int) typedArray._arrayLength);
        }

        return default;
    }

    /// <summary>
    /// The length <see cref="ReadProperty"/> answers for an array or a typed array, which compiled code may hold with
    /// the value until anything else runs; Bail for anything else.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static KernelResult LengthFact(KernelProperty property, JsValue target)
    {
        if (property._isLength)
        {
            if (target is JsArray array)
            {
                return new KernelResult(null, array.GetLength());
            }

            if (target is JsTypedArray typedArray && property.CachedTypedArrayLengthHolds(typedArray))
            {
                var buffer = typedArray._viewedArrayBuffer;
                if (buffer._arrayBufferData is not null && typedArray._arrayLength != JsTypedArray.LengthAuto && buffer._arrayBufferMaxByteLength is null)
                {
                    return new KernelResult(null, typedArray._arrayLength);
                }
            }
        }

        return KernelResult.Bail;
    }

    /// <summary>A Number as a Uint8 element stores it: <see cref="ToInteger"/>, modulo 2^8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ToUint8(double value) => (byte) ToInteger(value);

    /// <summary><see cref="KernelStoreElement"/>'s store once its operands are evaluated.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool StoreElement(JsValue target, double key, double value)
        => target is JsTypedArray typedArray && StoreTypedElement(typedArray, key, value);

    /// <summary>
    /// <see cref="JsTypedArray.TryWriteNumberKey"/>, with the elements of a fixed-length view of a fixed-length buffer
    /// written in place as <see cref="JsArrayBuffer.SetValueInBuffer"/> writes them on a little-endian host.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool StoreTypedElement(JsTypedArray typedArray, double key, double value)
    {
        var buffer = typedArray._viewedArrayBuffer;
        if (buffer._arrayBufferData is { } data
            && typedArray._contentType == TypedArrayContentType.Number
            && !buffer._isImmutable
            && typedArray._arrayLength != JsTypedArray.LengthAuto
            && buffer._arrayBufferMaxByteLength is null
            && BitConverter.IsLittleEndian)
        {
            // TypedArraySetElement: the value is a Number already; the index decides whether it is stored. -0 is "0".
            if (key >= 0 && key < typedArray._arrayLength)
            {
                var index = (int) key;
                if (index == key)
                {
                    var offset = typedArray._byteOffset;
                    switch (typedArray._arrayElementType)
                    {
                        case TypedArrayElementType.Uint8:
                            data[offset + index] = (byte) ToInteger(value);
                            return true;
                        case TypedArrayElementType.Int8:
                            data[offset + index] = (byte) (sbyte) ToInteger(value);
                            return true;
                        case TypedArrayElementType.Int16:
                        case TypedArrayElementType.Uint16:
                            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + index * 2), (short) ToInteger(value));
                            return true;
                        case TypedArrayElementType.Int32:
                        case TypedArrayElementType.Uint32:
                            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + index * 4), (int) ToInteger(value));
                            return true;
                        case TypedArrayElementType.Float32:
                            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + index * 4), BitConverter.SingleToInt32Bits((float) value));
                            return true;

                        case TypedArrayElementType.Float64:
                            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + index * 8), BitConverter.DoubleToInt64Bits(value));
                            return true;
                    }
                }
            }
        }

        return StoreTypedElementSlow(typedArray, key, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool StoreTypedElementSlow(JsTypedArray typedArray, double key, double value) => typedArray.TryWriteNumberKey(key, value);

    /// <summary><see cref="JsArrayBuffer.DoubleToInt64"/>: truncated toward zero, NaN, ±0 and ±∞ to 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ToInteger(double value)
        => double.IsNaN(value) || value == 0 || double.IsInfinity(value) ? 0 : (long) value;

    /// <summary>The store of <see cref="KernelAssignSlot"/> once the right-hand side is evaluated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void StoreAssigned(ref Binding binding, DeclarativeEnvironment? environment, KernelAssignSlot node, KernelFrame frame, JsValue? value, double number)
    {
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
            StoreDegenerate(environment!, node, frame, value, number);
        }
    }

    // The right-hand side changed the binding's state; the full store produces the exact semantics.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreDegenerate(DeclarativeEnvironment environment, KernelAssignSlot node, KernelFrame frame, JsValue? value, double number)
        => environment.SetMutableBinding(node._target._name, KernelExpression.Box(value, number), frame.Engine.ExecutionContext.Strict);

    /// <summary><see cref="KernelUpdateSlot"/> on a binding holding a mutable Number: its value; Bail, having done nothing, otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static KernelResult TryUpdate(ref Binding binding, double change, bool prefix)
    {
        if (binding.TryGetMutableNumber(out var value))
        {
            var updated = value + change;
            Binding.StoreMutableNumber(ref binding, updated);
            return new KernelResult(null, prefix ? updated : value);
        }

        return KernelResult.Bail;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsMutableNumber(ref Binding binding) => binding.TryGetMutableNumber(out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryMutableNumber(ref Binding binding, out double value) => binding.TryGetMutableNumber(out value);

    /// <summary>The store of a compound assignment's result to the binding it read.</summary>
    internal static void StoreCompound(ref Binding binding, DeclarativeEnvironment? environment, KernelCompoundEffect node, KernelFrame frame, JsValue? value, double number)
    {
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
            environment!.SetMutableBinding(node._target._name, KernelExpression.Box(value, number), frame.Engine.ExecutionContext.Strict);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void StoreMutableNumber(ref Binding binding, double value) => Binding.StoreMutableNumber(ref binding, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAssignable(ref Binding binding) => binding.IsAssignable;

    /// <summary>What <see cref="KernelStatement"/>'s checks do before a statement.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Check(EvaluationContext context, Engine engine, Statement statement, bool exact, MaxStatementsConstraint? counter)
    {
        engine.SetLastSyntaxElement(statement);
        if (exact)
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
        else if (counter is not null)
        {
            counter.Check();
        }

        if (context.RunsAmortizedChecks && --engine._amortizedConstraintCountdown <= 0)
        {
            CheckAmortized(engine);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void CheckAmortized(Engine engine)
    {
        engine._amortizedConstraintCountdown = EvaluationContext.AmortizedConstraintCheckInterval;
        engine.CheckAmortizedConstraints();
    }

    /// <summary>The completion of a <c>return</c>.</summary>
    internal static void SetReturn(KernelFrame frame, JsValue value, Statement statement)
        => frame.Abrupt = new Completion(CompletionType.Return, value, statement);
}

/// <summary>
/// The statements of the blocks compiled code runs, each a site: a block, the index of the statement in it, and the
/// site of the statement around the block. The code's one handler handles an exception at a site as the handlers of
/// the blocks around it would, innermost first (see <see cref="KernelBlockStatement"/>).
/// </summary>
internal sealed class KernelCodeSites
{
    private readonly KernelBlockStatement[] _blocks;
    private readonly int[] _indices;
    private readonly int[] _parents;

    internal KernelCodeSites(List<(KernelBlockStatement Block, int Index, int Parent)> sites)
    {
        _blocks = new KernelBlockStatement[sites.Count];
        _indices = new int[sites.Count];
        _parents = new int[sites.Count];
        for (var i = 0; i < sites.Count; i++)
        {
            (_blocks[i], _indices[i], _parents[i]) = sites[i];
        }
    }

    /// <summary>The filter: whether the handler of a block around <paramref name="site"/> catches the exception.</summary>
    internal bool Catches(object exception, int site)
        => site >= 0 && exception is Exception e && Interpreter.JintStatementList.ShouldCatch(e);

    /// <summary>
    /// The handlers of the blocks around <paramref name="site"/>, innermost first, each as
    /// <see cref="KernelBlockStatement"/>'s: true once one has made the exception the block's throw completion (in
    /// <see cref="KernelFrame.Abrupt"/>), or false, with the location attached, to rethrow it past them all.
    /// </summary>
    internal bool Catch(KernelFrame frame, Exception exception, int site)
    {
        var context = frame.Context;
        var engine = context.Engine;
        for (; site >= 0; site = _parents[site])
        {
            // A block whose handler rethrew is followed by the filter of the block around it.
            if (!Interpreter.JintStatementList.ShouldCatch(exception))
            {
                return false;
            }

            var statement = _blocks[site]._statements[_indices[site]]._statement;
            if (exception is JintException)
            {
                frame.Abrupt = Interpreter.JintStatementList.HandleStatementException(context, exception, statement);
                return true;
            }

            var location = engine.HasLastSyntaxElement ? engine.LastSyntaxLocation : statement.Location;
            ExceptionDataHelper.TryAttachJavaScriptLocation(exception, engine, location);
        }

        return false;
    }
}
