using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// A pure binary operator on two Numbers: the arithmetic, bitwise and shift operators and the comparisons
/// (https://tc39.es/ecma262/#sec-applystringornumericbinaryoperator, https://tc39.es/ecma262/#sec-relational-operators,
/// https://tc39.es/ecma262/#sec-equality-operators). Any operand that is not a Number answers
/// <see cref="KernelExpression.Bail"/>: a string, an object or a BigInt takes the interpreter's path, which may convert
/// it (and run its valueOf) or concatenate.
/// </summary>
internal sealed class KernelBinary : KernelExpression
{
    internal readonly Operator _operator;
    internal readonly KernelExpression _left;
    internal readonly KernelExpression _right;
    internal readonly bool _isComparison;

    // The operands a loop reads most, a slot and a literal, read without a call.
    internal readonly KernelName? _leftName;
    internal readonly KernelName? _rightName;
    internal readonly KernelNumber? _leftNumber;
    internal readonly KernelNumber? _rightNumber;

    public KernelBinary(Expression expression, Operator op, KernelExpression left, KernelExpression right) : base(expression)
    {
        _operator = op;
        _left = left;
        _right = right;
        _isComparison = IsComparison(op);
        _leftName = left as KernelName;
        _rightName = right as KernelName;
        _leftNumber = left as KernelNumber;
        _rightNumber = right as KernelNumber;
        _leftKind = KernelOperand.KindOf(left);
        _rightKind = KernelOperand.KindOf(right);
    }

    // How KernelOperand reads the operands.
    internal readonly byte _leftKind;
    internal readonly byte _rightKind;

    internal static bool IsSupported(Operator op) => op switch
    {
        Operator.Addition or Operator.Subtraction or Operator.Multiplication or Operator.Division or Operator.Remainder
            or Operator.BitwiseAnd or Operator.BitwiseOr or Operator.BitwiseXor
            or Operator.LeftShift or Operator.RightShift or Operator.UnsignedRightShift => true,
        _ => IsComparison(op),
    };

    internal static bool IsComparison(Operator op) => op
        is Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual
        or Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality;

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        if (!TryOperands(frame, out var left, out var right))
        {
            number = 0;
            return Bail;
        }

        if (_isComparison)
        {
            number = 0;
            return Compare(left, right) ? JsBoolean.True : JsBoolean.False;
        }

        number = Apply(_operator, left, right);
        return null;
    }

    internal override int Test(KernelFrame frame)
    {
        if (!TryOperands(frame, out var left, out var right))
        {
            return -1;
        }

        if (_isComparison)
        {
            return Compare(left, right) ? 1 : 0;
        }

        var number = Apply(_operator, left, right);
        return number != 0 && !double.IsNaN(number) ? 1 : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryOperands(KernelFrame frame, out double left, out double right)
    {
        // An operand that is not a Number makes the whole operation Bail, so a pure operand read without its nodes
        // answers as its nodes would.
        if (!KernelOperand.TryNumber(_left, _leftKind, frame, out left))
        {
            right = 0;
            return false;
        }

        return KernelOperand.TryNumber(_right, _rightKind, frame, out right);
    }

    private bool Compare(double left, double right) => Compare(_operator, left, right);

    /// <summary>A comparison operator of two Numbers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Compare(Operator op, double left, double right) => op switch
    {
        Operator.LessThan => left < right,
        Operator.LessThanOrEqual => left <= right,
        Operator.GreaterThan => left > right,
        Operator.GreaterThanOrEqual => left >= right,
        Operator.Equality or Operator.StrictEquality => left == right,
        _ => left != right,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Apply(Operator op, double left, double right)
    {
        switch (op)
        {
            case Operator.Addition:
                return left + right;
            case Operator.Subtraction:
                return left - right;
            case Operator.Multiplication:
                return left * right;
            case Operator.Division:
                return left / right;
            case Operator.Remainder:
                return JintExpression.RemainderUnboxed(left, right);
            case Operator.BitwiseAnd:
                return TypeConverter.ToInt32(left) & TypeConverter.ToInt32(right);
            case Operator.BitwiseOr:
                return TypeConverter.ToInt32(left) | TypeConverter.ToInt32(right);
            case Operator.BitwiseXor:
                return TypeConverter.ToInt32(left) ^ TypeConverter.ToInt32(right);
            case Operator.LeftShift:
                return TypeConverter.ToInt32(left) << (int) (TypeConverter.ToUint32(right) & 0x1F);
            case Operator.RightShift:
                return TypeConverter.ToInt32(left) >> (int) (TypeConverter.ToUint32(right) & 0x1F);
            default:
                return TypeConverter.ToUint32(left) >> (int) (TypeConverter.ToUint32(right) & 0x1F);
        }
    }
}

/// <summary>
/// <c>&amp;&amp;</c> and <c>||</c> over pure operands (https://tc39.es/ecma262/#sec-binary-logical-operators): ToBoolean
/// runs no script.
/// </summary>
internal sealed class KernelLogical : KernelExpression
{
    internal readonly bool _and;
    internal readonly KernelExpression _left;
    internal readonly KernelExpression _right;

    public KernelLogical(Expression expression, bool and, KernelExpression left, KernelExpression right) : base(expression)
    {
        _and = and;
        _left = left;
        _right = right;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var left = _left.Evaluate(frame, out number);
        if (ReferenceEquals(left, Bail))
        {
            return Bail;
        }

        var truthy = left is null ? number != 0 && !double.IsNaN(number) : TypeConverter.ToBoolean(left);
        if (truthy != _and)
        {
            return left;
        }

        return _right.Evaluate(frame, out number);
    }

    internal override int Test(KernelFrame frame)
    {
        var left = _left.Test(frame);
        if (left < 0)
        {
            return -1;
        }

        if ((left == 1) != _and)
        {
            return left;
        }

        return _right.Test(frame);
    }
}

/// <summary>Unary <c>!</c>, <c>-</c>, <c>+</c> and <c>~</c> over a pure operand; the last three on a Number only.</summary>
internal sealed class KernelUnary : KernelExpression
{
    internal readonly Operator _operator;
    internal readonly KernelExpression _argument;

    public KernelUnary(Expression expression, Operator op, KernelExpression argument) : base(expression)
    {
        _operator = op;
        _argument = argument;
    }

    internal static bool IsSupported(Operator op)
        => op is Operator.LogicalNot or Operator.UnaryNegation or Operator.UnaryPlus or Operator.BitwiseNot;

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        if (_operator == Operator.LogicalNot)
        {
            number = 0;
            var test = _argument.Test(frame);
            return test < 0 ? Bail : test == 0 ? JsBoolean.True : JsBoolean.False;
        }

        var value = _argument.Evaluate(frame, out number);
        if (!IsNumber(value, ref number))
        {
            return Bail;
        }

        number = _operator switch
        {
            Operator.UnaryNegation => -number,
            Operator.UnaryPlus => number,
            _ => ~TypeConverter.ToInt32(number),
        };
        return null;
    }

    internal override int Test(KernelFrame frame)
    {
        if (_operator == Operator.LogicalNot)
        {
            var test = _argument.Test(frame);
            return test < 0 ? -1 : 1 - test;
        }

        return base.Test(frame);
    }
}

/// <summary>
/// <c>&amp;&amp;</c> and <c>||</c> with an operand that is not pure, a call above all: each operand is evaluated at
/// most once, in order, and never answers Bail.
/// </summary>
internal sealed class KernelLogicalEffect : KernelExpression
{
    internal readonly bool _and;
    internal readonly KernelExpression _left;
    internal readonly KernelExpression _right;

    public KernelLogicalEffect(Expression expression, bool and, KernelExpression left, KernelExpression right) : base(expression)
    {
        _and = and;
        _left = left;
        _right = right;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var left = _left.Evaluate(frame, out number);
        var truthy = left is null ? number != 0 && !double.IsNaN(number) : TypeConverter.ToBoolean(left);
        return truthy != _and ? left : _right.Evaluate(frame, out number);
    }

    internal override int Test(KernelFrame frame)
    {
        var left = _left.Test(frame);
        return (left == 1) != _and ? left : _right.Test(frame);
    }
}

/// <summary><c>!argument</c> for an argument with effects, which evaluates it once and never answers Bail.</summary>
internal sealed class KernelNotEffect : KernelExpression
{
    internal readonly KernelExpression _argument;

    public KernelNotEffect(Expression expression, KernelExpression argument) : base(expression)
    {
        _argument = argument;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        return _argument.Test(frame) == 0 ? JsBoolean.True : JsBoolean.False;
    }

    internal override int Test(KernelFrame frame) => 1 - _argument.Test(frame);
}

/// <summary><c>test ? consequent : alternate</c> over pure operands.</summary>
internal sealed class KernelConditional : KernelExpression
{
    internal readonly KernelExpression _test;
    internal readonly KernelExpression _consequent;
    internal readonly KernelExpression _alternate;

    public KernelConditional(Expression expression, KernelExpression test, KernelExpression consequent, KernelExpression alternate) : base(expression)
    {
        _test = test;
        _consequent = consequent;
        _alternate = alternate;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var test = _test.Test(frame);
        if (test < 0)
        {
            number = 0;
            return Bail;
        }

        return test == 1 ? _consequent.Evaluate(frame, out number) : _alternate.Evaluate(frame, out number);
    }
}
