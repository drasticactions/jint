using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jint.Native;

namespace Jint.Runtime.Environments;

[Flags]
internal enum BindingFlags : byte
{
    None = 0,
    CanBeDeleted = 1,
    Mutable = 2,
    Strict = 4,

    /// <summary>
    /// The binding holds a raw number instead of a materialized <see cref="JsValue"/>. Only ever set for
    /// slot-stored bindings by the numeric read-modify-write fast paths; a materializing read converts back (with
    /// write-back caching). Never stored: <see cref="Binding"/> tells an unboxed number by its layout.
    /// </summary>
    UnboxedNumber = 8,

    /// <summary>
    /// The binding holds a <see cref="JsString.ConcatenatedString"/> that a <c>+=</c> on it produced and nothing
    /// else holds, so the next <c>+=</c> may extend it in place rather than allocate a new value. Set only by
    /// <see cref="Binding.ChangeValueOwned"/>, cleared by every other store. A read that hands the value anywhere marks
    /// the string shared first (<see cref="Binding.Value"/>), which ends the ownership: see
    /// <see cref="JsString.ConcatenatedString"/> for why that keeps every string anyone can hold immutable. Nothing
    /// casts the value on the strength of the flag alone.
    /// </summary>
    OwnsString = 16,
}

[DebuggerDisplay("Mutable: {Mutable}, Strict: {Strict}, CanBeDeleted: {CanBeDeleted}, Value: {_value}, Unboxed: {IsUnboxedNumber}")]
internal readonly struct Binding
{
    // Two words. A binding holding a reference value (or none yet) keeps the value and, in _bits, its flags, all
    // below ReferenceBitsLimit. An unboxed number keeps the double's bits in _bits; a double whose bits fall below
    // the limit (+0 and a few denormals) is stored boxed instead, so the two cases never meet. Whether the value is
    // plain or unboxed is one comparison of _bits, as it was one test of the flags byte when the binding was three
    // words.
    //
    // An unboxed number's _value says what the binding was declared as. It is null for a binding that is nothing but
    // mutable (a var, a let or a parameter), so that the numeric lanes find a writable number with two comparisons
    // and store one without reading the binding they replace and without a write barrier; an immutable binding
    // (a const) holds the marker of its flags there. A mutable binding that can be deleted (an eval var) stores its
    // numbers boxed.
    private readonly JsValue? _value;
    private readonly long _bits;

    private const ulong ReferenceBitsLimit = 32;
    private const ulong PlainBitsLimit = (ulong) BindingFlags.OwnsString;
    private const BindingFlags DeclarationFlags = BindingFlags.CanBeDeleted | BindingFlags.Mutable | BindingFlags.Strict;

    public Binding(
        JsValue value,
        bool canBeDeleted,
        bool mutable,
        bool strict)
    {
        _value = value;
        _bits = (long) ((canBeDeleted ? BindingFlags.CanBeDeleted : BindingFlags.None)
                        | (mutable ? BindingFlags.Mutable : BindingFlags.None)
                        | (strict ? BindingFlags.Strict : BindingFlags.None));
    }

    private Binding(JsValue? value, long bits)
    {
        _value = value;
        _bits = bits;
    }

    private bool IsReference
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ulong) _bits < ReferenceBitsLimit;
    }

    private BindingFlags Flags => IsReference
        ? (BindingFlags) _bits
        : UnboxedFlags | BindingFlags.UnboxedNumber;

    private BindingFlags UnboxedFlags => _value is null ? BindingFlags.Mutable : Unsafe.As<UnboxedMarker>(_value).Flags;

    /// <summary>
    /// The materialized value; readers must check <see cref="HasReferenceValue"/> (or use the
    /// environment's materializing accessors) so unboxed bindings are not observed as their marker.
    /// </summary>
    /// <remarks>
    /// This is how a value leaves a binding, so it is where an owned string stops being extensible: whatever
    /// the caller does with the value, the binding's next <c>+=</c> allocates instead of writing into it. The
    /// struct is a copy here, so the flag itself stays set until the next store; the string carries the answer.
    /// </remarks>
    public JsValue Value
    {
        get
        {
            Debug.Assert(IsReference, "unboxed binding read through Value without materialization");
            var value = _value!;
            if (OwnsString)
            {
                MarkShared(value);
            }

            return value;
        }
    }

    /// <summary>
    /// The value without ending an owned string's ownership, for a binding that holds a reference value: an unboxed
    /// number's is null or its marker, so a reader tests <see cref="IsPlainValue"/>, <see cref="IsUnboxedNumber"/> or
    /// <see cref="HoldsReferenceOrNothing"/> first, or compares the result by type or reference only. Only for a
    /// reader that cannot let a string escape, or that ends the ownership itself: the <c>+=</c> lanes reading their
    /// own operand, the number-only slot readers, the arithmetic-lane readers that use only a length or an array, the
    /// slot readers that test <see cref="IsPlainValue"/> first and leave every other case to
    /// <c>DeclarativeEnvironment.ReadSlotSlow</c>, and the identifier read, whose <c>HandOn</c> marks an owned string
    /// through its <c>ReadHook</c> bit. Every other reader uses <see cref="Value"/>.
    /// </summary>
    public JsValue? PeekValue => _value;

    /// <summary>
    /// Whether <see cref="PeekValue"/> is the value as any reader may see it: neither an unboxed number to
    /// materialize nor an owned string to mark shared. One comparison, which the hot slot readers make in place of
    /// the null test they made before strings could be owned.
    /// </summary>
    public bool IsPlainValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ulong) _bits < PlainBitsLimit;
    }

    /// <summary>
    /// Whether the binding holds a reference value or none yet (an uninitialized binding), so that
    /// <see cref="PeekValue"/> is the value or <see langword="null"/>; otherwise it holds an unboxed number. The
    /// identifier reads take this test in place of <see cref="HasReferenceValue"/>, the null test following anyway.
    /// </summary>
    public bool HoldsReferenceOrNothing
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IsReference;
    }

    public bool CanBeDeleted => (Flags & BindingFlags.CanBeDeleted) != BindingFlags.None;

    /// <summary>
    /// Whether the binding may be assigned, with no branch on the layout: a reference binding's flag, or an unboxed
    /// number's null marker.
    /// </summary>
    public bool Mutable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((ulong) _bits < ReferenceBitsLimit & (_bits & (long) BindingFlags.Mutable) != 0)
               | ((ulong) _bits >= ReferenceBitsLimit & _value is null);
    }

    /// <summary>
    /// <see cref="Mutable"/> and <see cref="IsInitialized"/> together, the test of every slot store: a mutable
    /// reference binding holding a value, or an unboxed number with no marker.
    /// </summary>
    public bool IsAssignable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ulong) _bits < ReferenceBitsLimit
            ? (_bits & (long) BindingFlags.Mutable) != 0 && _value is not null
            : _value is null;
    }

    public bool Strict => (Flags & BindingFlags.Strict) != BindingFlags.None;

    public bool IsUnboxedNumber
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsReference;
    }

    // The flags of a reference binding that owns its string lie in [OwnsString, ReferenceBitsLimit).
    public bool OwnsString => (ulong) _bits - PlainBitsLimit < ReferenceBitsLimit - PlainBitsLimit;

    public bool HasReferenceValue => IsReference && _value is not null;

    public double UnboxedNumber
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert(IsUnboxedNumber);
            return BitConverter.Int64BitsToDouble(_bits);
        }
    }

    /// <summary>
    /// The number of a binding that is nothing but mutable (a var, a let or a parameter) and holds a number, unboxed
    /// or as a <see cref="JsNumber"/>: the read half of the numeric read-modify-write lanes, whose write half is
    /// <see cref="StoreMutableNumber"/>. Two comparisons on the unboxed path, a type test and a comparison on the boxed
    /// one. A number is never owned, and nothing but its double leaves this method.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetMutableNumber(out double value)
    {
        var current = _value;
        if (current is null && (ulong) _bits >= ReferenceBitsLimit)
        {
            value = BitConverter.Int64BitsToDouble(_bits);
            return true;
        }

        // A JsNumber is a reference value, so _bits holds the flags, and a number never owns: exactly Mutable.
        if (current is JsNumber number && _bits == (long) BindingFlags.Mutable)
        {
            value = number._value;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Stores <paramref name="value"/> unboxed into a binding that is nothing but mutable: the store of the numeric
    /// lanes into a binding <see cref="TryGetMutableNumber"/> accepted, which needs nothing of the binding it replaces.
    /// The unboxed store writes a null reference, so it needs no write barrier; the boxed one, for the doubles whose
    /// bits would read as flags, is out of line so that the two stores are not merged into one with a barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreMutableNumber(ref Binding binding, double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        if ((ulong) bits >= ReferenceBitsLimit)
        {
            binding = new Binding(null, bits);
        }
        else
        {
            StoreBoxedMutableNumber(ref binding, value);
        }
    }

    // +0 (the shared JsNumber) or a tiny denormal, whose bits would read as a reference binding's flags.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreBoxedMutableNumber(ref Binding binding, double value)
    {
        binding = new Binding(JsNumber.Create(value), (long) BindingFlags.Mutable);
    }

    /// <summary>
    /// Stores <paramref name="value"/> unboxed into an initialized binding, whatever its declaration flags (a const
    /// keeps its marker's immutability), with the common case, a binding that is nothing but mutable, inline. A
    /// binding that is mutable and deletable, and a number whose bits would read as flags, store the number boxed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreUnboxedNumber(ref Binding binding, double value)
    {
        var bits = binding._bits;
        if ((ulong) bits < ReferenceBitsLimit
                ? (bits & (long) DeclarationFlags) == (long) BindingFlags.Mutable
                : binding._value is null)
        {
            StoreMutableNumber(ref binding, value);
            return;
        }

        // a const being initialized, the other case a declaration stores
        var numberBits = BitConverter.DoubleToInt64Bits(value);
        if (bits == (long) BindingFlags.Strict && (ulong) numberBits >= ReferenceBitsLimit)
        {
            binding = new Binding(UnboxedMarker.Const, numberBits);
            return;
        }

        StoreUnboxedNumberSlow(ref binding, value);
    }

    // A const, an immutable binding of another kind, or a deletable one: unboxed with the marker of its flags, or
    // boxed when it has none or the bits would read as flags.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreUnboxedNumberSlow(ref Binding binding, double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        if ((ulong) bits >= ReferenceBitsLimit)
        {
            var marker = binding.IsReference ? UnboxedMarker.For((BindingFlags) binding._bits) : binding._value;
            if (marker is not null)
            {
                binding = new Binding(marker, bits);
                return;
            }
        }

        binding = binding.ChangeValue(JsNumber.Create(value));
    }

    private long DeclarationBits()
    {
        return IsReference
            ? _bits & (long) DeclarationFlags
            : (long) UnboxedFlags;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Binding ChangeValue(JsValue argument)
    {
        return new Binding(argument, DeclarationBits());
    }

    /// <summary>
    /// <see cref="ChangeValue"/> for a binding known to be mutable, whose declaration flags are Mutable and perhaps
    /// CanBeDeleted: a conditional move in place of the branch on the layout.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Binding ChangeMutableValue(JsValue argument)
    {
        Debug.Assert(Mutable);
        var declared = (ulong) _bits < ReferenceBitsLimit
            ? _bits & (long) (BindingFlags.Mutable | BindingFlags.CanBeDeleted)
            : (long) BindingFlags.Mutable;
        return new Binding(argument, declared);
    }

    /// <summary>
    /// Stores a string a <c>+=</c> has just created, which nothing else holds yet, and takes ownership of it so the
    /// next <c>+=</c> can extend it in place. The caller must have created <paramref name="value"/> itself; a value
    /// that has ever been handed on refuses the claim and is stored like any other.
    /// </summary>
    public Binding ChangeValueOwned(JsString.ConcatenatedString value)
    {
        return value.TryClaimForBinding()
            ? new Binding(value, DeclarationBits() | (long) BindingFlags.OwnsString)
            : ChangeValue(value);
    }

    /// <summary>
    /// This binding as a copy for another storage location: an owned string is marked shared, and the copy does
    /// not own it. For code that moves <see cref="Binding"/> structs wholesale rather than reading
    /// <see cref="Value"/>.
    /// </summary>
    public Binding Detach()
    {
        if (!OwnsString)
        {
            return this;
        }

        MarkShared(_value);
        return new Binding(_value, _bits & ~(long) BindingFlags.OwnsString);
    }

    // An uninitialized binding holds no reference value and no unboxed number.
    public bool IsInitialized() => _value is not null || (ulong) _bits >= ReferenceBitsLimit;

    /// <summary>
    /// A type test rather than a cast on the strength of <see cref="BindingFlags.OwnsString"/>. Out of line, so the
    /// test costs the readers nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MarkShared(JsValue? value)
    {
        if (value is JsString.ConcatenatedString owned)
        {
            owned.MarkShared();
        }
    }

    /// <summary>
    /// What an immutable binding holding an unboxed number holds in place of a value: one shared instance per
    /// combination of the declaration flags. Never observable: it is <see cref="Types.Empty"/>, and every reader of a
    /// binding asks whether it holds an unboxed number before it looks at the value.
    /// </summary>
    private sealed class UnboxedMarker : JsValue
    {
        /// <summary>The marker of a const.</summary>
        internal static readonly UnboxedMarker Const = new(BindingFlags.Strict);

        private static readonly UnboxedMarker?[] _markers = Create();

        internal readonly BindingFlags Flags;

        private UnboxedMarker(BindingFlags flags) : base(Types.Empty)
        {
            Flags = flags;
        }

        /// <summary>The marker for an immutable binding's flags, or null for a mutable one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static UnboxedMarker? For(BindingFlags flags)
        {
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_markers), (int) (flags & DeclarationFlags));
        }

        private static UnboxedMarker?[] Create()
        {
            var markers = new UnboxedMarker?[(int) DeclarationFlags + 1];
            for (var i = 0; i < markers.Length; i++)
            {
                var flags = (BindingFlags) i;
                if (flags == BindingFlags.Strict)
                {
                    markers[i] = Const;
                }
                else if ((flags & BindingFlags.Mutable) == BindingFlags.None)
                {
                    markers[i] = new UnboxedMarker(flags);
                }
            }

            return markers;
        }

        public override object? ToObject() => null;

        public override string ToString() => "[unboxed number binding]";
    }
}
