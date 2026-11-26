using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jint.Native;
using Jint.Runtime.Environments;

namespace Jint.Runtime.Interpreter;

/// <summary>
/// A call's argument list, abstracted over where the values physically live. Two implementations
/// exist: <see cref="ArrayArguments"/> reads the <see cref="JsCallArguments"/> array the public
/// [[Call]] boundary hands over, and <see cref="RegisterArguments"/> reads locals a call site
/// evaluated straight into registers, never renting an array at all.
/// </summary>
/// <remarks>
/// Every consumer is generic and constrained <c>where TArgs : struct, IArgumentSource</c> so the JIT
/// produces one specialization per implementation and <see cref="At"/> inlines to an array index or a
/// register read. Consuming through the interface itself would box the struct and dispatch virtually,
/// which is the entire cost this abstraction exists to avoid — so never widen a parameter to
/// <c>IArgumentSource</c>.
/// </remarks>
internal interface IArgumentSource
{
    int Count { get; }

    /// <summary>
    /// The argument at <paramref name="index"/>, or <see cref="JsValue.Undefined"/> when the call
    /// supplied fewer arguments — the same answer <see cref="Arguments.At(JsValue[], int)"/> gives.
    /// </summary>
    JsValue At(int index);

    /// <summary>Binds the first <paramref name="count"/> slots to the arguments, as mutable parameter bindings.</summary>
    void StoreParameters(Binding[] slots, int count);
}

/// <summary>
/// <see cref="IArgumentSource"/> over the argument array of an ordinary [[Call]].
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct ArrayArguments(JsCallArguments arguments) : IArgumentSource
{
    private readonly JsCallArguments _arguments = arguments;

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _arguments.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JsValue At(int index) => (uint) index < (uint) _arguments.Length ? _arguments[index] : JsValue.Undefined;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreParameters(Binding[] slots, int count)
    {
        for (var i = 0; i < count; i++)
        {
            slots[i] = new Binding(At(i), canBeDeleted: false, mutable: true, strict: false);
        }
    }
}

/// <summary>
/// <see cref="IArgumentSource"/> over up to four values held in locals, for call sites whose arity is
/// a build-time constant and which therefore never need an argument array. Registers beyond
/// <see cref="Count"/> are not read, so a caller may leave them at any value.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct RegisterArguments(JsValue a0, JsValue a1, JsValue a2, JsValue a3, int count) : IArgumentSource
{
    private readonly JsValue _a0 = a0;
    private readonly JsValue _a1 = a1;
    private readonly JsValue _a2 = a2;
    private readonly JsValue _a3 = a3;
    private readonly int _count = count;

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JsValue At(int index)
    {
        if ((uint) index >= (uint) _count)
        {
            return JsValue.Undefined;
        }

        return index switch
        {
            0 => _a0,
            1 => _a1,
            2 => _a2,
            _ => _a3,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreParameters(Binding[] slots, int count)
    {
        if (count == 0)
        {
            return;
        }

        slots[0] = Parameter(_count > 0 ? _a0 : JsValue.Undefined);
        if (count > 1)
        {
            slots[1] = Parameter(_count > 1 ? _a1 : JsValue.Undefined);
            if (count > 2)
            {
                slots[2] = Parameter(_count > 2 ? _a2 : JsValue.Undefined);
                if (count > 3)
                {
                    slots[3] = Parameter(_count > 3 ? _a3 : JsValue.Undefined);
                    for (var i = 4; i < count; i++)
                    {
                        slots[i] = Parameter(JsValue.Undefined);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Binding Parameter(JsValue value) => new(value, canBeDeleted: false, mutable: true, strict: false);
}

/// <summary>
/// <see cref="IArgumentSource"/> over up to four values, each a value or, where that is null, a Number that a parameter
/// binding takes unboxed.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct NumberArguments(JsValue? v0, double n0, JsValue? v1, double n1, JsValue? v2, double n2, JsValue? v3, double n3, int count) : IArgumentSource
{
    private readonly JsValue? _v0 = v0;
    private readonly JsValue? _v1 = v1;
    private readonly JsValue? _v2 = v2;
    private readonly JsValue? _v3 = v3;
    private readonly double _n0 = n0;
    private readonly double _n1 = n1;
    private readonly double _n2 = n2;
    private readonly double _n3 = n3;
    private readonly int _count = count;

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>The argument at <paramref name="index"/> below the count: null with its Number in <paramref name="number"/>, or the value.</summary>
    internal JsValue? ValueAt(int index, out double number)
    {
        switch (index)
        {
            case 0:
                number = _n0;
                return _v0;
            case 1:
                number = _n1;
                return _v1;
            case 2:
                number = _n2;
                return _v2;
            default:
                number = _n3;
                return _v3;
        }
    }

    public JsValue At(int index)
    {
        if ((uint) index >= (uint) _count)
        {
            return JsValue.Undefined;
        }

        return index switch
        {
            0 => _v0 ?? JsNumber.Create(_n0),
            1 => _v1 ?? JsNumber.Create(_n1),
            2 => _v2 ?? JsNumber.Create(_n2),
            _ => _v3 ?? JsNumber.Create(_n3),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreParameters(Binding[] slots, int count)
    {
        if (count == 0)
        {
            return;
        }

        Store(ref slots[0], 0, _v0, _n0);
        if (count > 1)
        {
            Store(ref slots[1], 1, _v1, _n1);
            if (count > 2)
            {
                Store(ref slots[2], 2, _v2, _n2);
                if (count > 3)
                {
                    Store(ref slots[3], 3, _v3, _n3);
                    for (var i = 4; i < count; i++)
                    {
                        slots[i] = RegisterArguments.Parameter(JsValue.Undefined);
                    }
                }
            }
        }
    }

    /// <summary>The arguments with their Numbers boxed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisterArguments Boxed() => new(
        _count > 0 ? _v0 ?? JsNumber.Create(_n0) : JsValue.Undefined,
        _count > 1 ? _v1 ?? JsNumber.Create(_n1) : JsValue.Undefined,
        _count > 2 ? _v2 ?? JsNumber.Create(_n2) : JsValue.Undefined,
        _count > 3 ? _v3 ?? JsNumber.Create(_n3) : JsValue.Undefined,
        _count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store(ref Binding slot, int index, JsValue? value, double number)
    {
        if (index >= _count)
        {
            slot = RegisterArguments.Parameter(JsValue.Undefined);
        }
        else if (value is null)
        {
            Binding.StoreMutableNumber(ref slot, number);
        }
        else
        {
            slot = RegisterArguments.Parameter(value);
        }
    }
}
