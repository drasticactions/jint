using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.TypedArray;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// The Number of a pure operand read in the node that uses it, without a call per operand, for the shapes loops use
/// most: a literal, a name, arithmetic of two of those, an element <c>name[key]</c> of a typed array or an array with
/// such a key, and a <see cref="KernelRoot"/> over one of them. Each answers the Number its nodes answer, and false
/// wherever they would answer anything else (a value that is not a Number, or Bail); a caller then evaluates the
/// operand with its nodes, which repeat nothing observable, as the operand is pure.
/// </summary>
internal static class KernelOperand
{
    internal const byte Other = 0;
    internal const byte Number = 1;
    internal const byte Name = 2;
    internal const byte Arithmetic = 3;
    internal const byte Element = 4;
    internal const byte Root = 5;

    /// <summary>How <see cref="TryNumber"/> reads <paramref name="expression"/>.</summary>
    internal static byte KindOf(KernelExpression expression) => expression switch
    {
        KernelNumber => Number,
        KernelName => Name,
        KernelBinary { _isComparison: false } binary when IsLeaf(binary._left) && IsLeaf(binary._right) => Arithmetic,
        KernelElement { _objectName: not null } element when KeyKindOf(element._key) != Other => Element,
        KernelRoot { _pure: var pure } when KindOf(pure) is not Other and not Root => Root,
        _ => Other,
    };

    private static bool IsLeaf(KernelExpression expression) => expression is KernelNumber or KernelName;

    /// <summary>How <see cref="TryKey"/> reads the key of an element: a literal, a name, or arithmetic of those.</summary>
    internal static byte KeyKindOf(KernelExpression key) => key switch
    {
        KernelNumber => Number,
        KernelName => Name,
        KernelBinary { _isComparison: false } binary when IsLeaf(binary._left) && IsLeaf(binary._right) => Arithmetic,
        _ => Other,
    };

    /// <summary>The Number of an operand of kind <paramref name="kind"/>; false where its nodes answer anything else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryNumber(KernelExpression expression, byte kind, KernelFrame frame, out double value)
    {
        switch (kind)
        {
            case Number:
                value = Unsafe.As<KernelNumber>(expression)._value;
                return true;
            case Name:
                return TryName(Unsafe.As<KernelName>(expression), frame, out value);
            case Arithmetic:
                return TryArithmetic(Unsafe.As<KernelBinary>(expression), frame, out value);
            case Element:
                return TryElement(Unsafe.As<KernelElement>(expression), frame, out value);
            case Root:
                return TryRoot(Unsafe.As<KernelRoot>(expression), frame, out value);
            default:
                {
                    var result = expression.Evaluate(frame, out value);
                    return KernelExpression.IsNumber(result, ref value);
                }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryName(KernelName name, KernelFrame frame, out double value)
    {
        if (name.TryReadNumber(frame, out value))
        {
            return true;
        }

        return TryNameSlow(name, frame, out value);
    }

    /// <summary>A name not holding a Number in a slot it resolved to: a global, a name to resolve, or no Number.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryNameSlow(KernelName name, KernelFrame frame, out double value)
    {
        var result = name.Evaluate(frame, out value);
        return KernelExpression.IsNumber(result, ref value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryLeaf(KernelExpression leaf, KernelFrame frame, out double value)
    {
        if (leaf is KernelNumber number)
        {
            value = number._value;
            return true;
        }

        return TryName(Unsafe.As<KernelName>(leaf), frame, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryArithmetic(KernelBinary binary, KernelFrame frame, out double value)
    {
        if (TryLeaf(binary._left, frame, out var left) && TryLeaf(binary._right, frame, out var right))
        {
            value = KernelBinary.Apply(binary._operator, left, right);
            return true;
        }

        value = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryKey(KernelExpression key, byte kind, KernelFrame frame, out double value)
    {
        switch (kind)
        {
            case Number:
                value = Unsafe.As<KernelNumber>(key)._value;
                return true;
            case Name:
                return TryName(Unsafe.As<KernelName>(key), frame, out value);
            default:
                return TryArithmetic(Unsafe.As<KernelBinary>(key), frame, out value);
        }
    }

    /// <summary><see cref="KernelElement.Evaluate"/> answering a Number: an element of a typed array of Numbers or a present Number element of an array.</summary>
    internal static bool TryElement(KernelElement element, KernelFrame frame, out double value)
    {
        var target = element._objectName!.Evaluate(frame, out _);
        if (TryKey(element._key, element._keyKind, frame, out var key))
        {
            if (target is JsTypedArray typedArray)
            {
                var buffer = typedArray._viewedArrayBuffer;
                if (typedArray._arrayElementType == TypedArrayElementType.Uint8
                    && buffer._arrayBufferData is { } data
                    && typedArray._contentType == TypedArrayContentType.Number
                    && typedArray._arrayLength != JsTypedArray.LengthAuto
                    && buffer._arrayBufferMaxByteLength is null
                    && key >= 0
                    && key < typedArray._arrayLength)
                {
                    // KernelCodeRuntime.ReadTypedElement of a Uint8 element; -0 is the key "0".
                    var index = (int) key;
                    if (index == key)
                    {
                        value = data[typedArray._byteOffset + index];
                        return true;
                    }
                }

                var read = KernelCodeRuntime.ReadTypedElement(typedArray, key);
                value = read.Number;
                return KernelExpression.IsNumber(read.Value, ref value);
            }

            if (target is JsArray array)
            {
                // -0 is the key "0"; a present dense element is an own data property of the default attributes.
                var index = (uint) key;
                value = 0;
                return index == key && index != uint.MaxValue && array.TryGetValueFast(index, out var item) && KernelExpression.IsNumber(item, ref value);
            }
        }

        value = 0;
        return false;
    }

    /// <summary>A root over a pure operand read without its nodes, counted as an evaluation that completed.</summary>
    private static bool TryRoot(KernelRoot root, KernelFrame frame, out double value)
    {
        if (!root._interpretOnly && TryNumber(root._pure, root._pureKind, frame, out value))
        {
            root.Completed();
            return true;
        }

        value = 0;
        return false;
    }
}
