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
        _isEquality = op is Operator.StrictEquality or Operator.StrictInequality or Operator.Equality or Operator.Inequality;
        _neverNumbers = _isEquality && (NeverNumber(left) || NeverNumber(right));
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

    // Whether the operator is an equality, and whether an operand is never a Number.
    internal readonly bool _isEquality;
    internal readonly bool _neverNumbers;

    private static bool NeverNumber(KernelExpression operand) => operand is KernelTypeOf or KernelConstant { _value: not JsNumber };

    /// <summary>
    /// An equality of operands that are not both Numbers: 1 or 0 for values it compares without running script (any
    /// two for strict equality, two of one type or one null or undefined for loose equality), -1 for Bail.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int EqualValues(KernelFrame frame)
    {
        var left = _left.Evaluate(frame, out var leftNumber);
        if (ReferenceEquals(left, Bail))
        {
            return -1;
        }

        var right = _right.Evaluate(frame, out var rightNumber);
        return ReferenceEquals(right, Bail) ? -1 : Equal(_operator, left, leftNumber, right, rightNumber);
    }

    /// <summary><see cref="EqualValues"/> of two evaluated operands, a Number in the number where the value is null.</summary>
    internal static int Equal(Operator op, JsValue? left, double leftNumber, JsValue? right, double rightNumber)
    {
        var leftValue = Box(left, leftNumber);
        var rightValue = Box(right, rightNumber);
        bool equal;
        if (op is Operator.StrictEquality or Operator.StrictInequality)
        {
            equal = leftValue == rightValue;
        }
        else if (leftValue.Type == rightValue.Type)
        {
            equal = leftValue.Equals(rightValue);
        }
        else if (leftValue.IsNullOrUndefined() || rightValue.IsNullOrUndefined())
        {
            // https://tc39.es/ecma262/#sec-islooselyequal converts nothing when an operand is null or undefined.
            var other = leftValue.IsNullOrUndefined() ? rightValue : leftValue;
            equal = other.IsNullOrUndefined() || (other._type & InternalTypes.IsHTMLDDA) != InternalTypes.Empty;
        }
        else
        {
            return -1;
        }

        return equal == (op is Operator.StrictEquality or Operator.Equality) ? 1 : 0;
    }

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
        if (_neverNumbers || !TryOperands(frame, out var left, out var right))
        {
            number = 0;
            if (_isEquality)
            {
                var equal = EqualValues(frame);
                return equal < 0 ? Bail : equal == 1 ? JsBoolean.True : JsBoolean.False;
            }

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
        if (_neverNumbers || !TryOperands(frame, out var left, out var right))
        {
            return _isEquality ? EqualValues(frame) : -1;
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
/// A pure <c>typeof</c> (https://tc39.es/ecma262/#sec-typeof-operator): the type of a pure operand's value, and
/// "undefined" for a name no environment, global declaration or link of the global object's prototype chain has.
/// </summary>
internal sealed class KernelTypeOf : KernelExpression
{
    internal readonly KernelExpression _argument;

    public KernelTypeOf(Expression expression, KernelExpression argument) : base(expression)
    {
        _argument = argument;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var value = _argument.Evaluate(frame, out number);
        number = 0;
        if (value is null)
        {
            return JsString.NumberString;
        }

        if (ReferenceEquals(value, Bail))
        {
            return _argument is KernelName name && name.IsUnresolvable(frame) ? JsString.UndefinedString : Bail;
        }

        return JintUnaryExpression.TypeOfString(value);
    }

    /// <summary>The type of a value an operand answered, a Number where it is null.</summary>
    internal static JsString Of(JsValue? value) => value is null ? JsString.NumberString : JintUnaryExpression.TypeOfString(value);
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

/// <summary>
/// An arithmetic or bitwise operator with an operand that is not pure, a call above all: each operand once, in order;
/// two Numbers are combined directly, any other pair as the interpreter combines evaluated operands.
/// </summary>
internal sealed class KernelArithmeticEffect : KernelExpression
{
    internal readonly Operator _operator;
    internal readonly KernelExpression _left;
    internal readonly KernelExpression _right;
    private readonly Node _location;

    public KernelArithmeticEffect(NonLogicalBinaryExpression expression, KernelExpression left, KernelExpression right) : base(expression)
    {
        _operator = expression.Operator;
        _left = left;
        _right = right;
        _location = expression.Left;
    }

    internal static bool IsSupported(Operator op) => KernelBinary.IsSupported(op) && !KernelBinary.IsComparison(op);

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var left = _left.Evaluate(frame, out var leftNumber);
        var right = _right.Evaluate(frame, out var rightNumber);
        if (IsNumber(left, ref leftNumber) && IsNumber(right, ref rightNumber))
        {
            number = KernelBinary.Apply(_operator, leftNumber, rightNumber);
            return null;
        }

        number = 0;
        return ApplyValues(frame, Box(left, leftNumber), Box(right, rightNumber));
    }

    /// <summary>The operator applied to evaluated operands that are not both Numbers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue ApplyValues(KernelFrame frame, JsValue left, JsValue right)
    {
        frame.Context.PrepareFor(_expression);
        return JintAssignmentExpression.ComputeCompound(frame.Context, KernelCompoundSlot.CompoundOperator(_operator), left, right, _location);
    }
}

/// <summary><c>left instanceof right</c> (https://tc39.es/ecma262/#sec-instanceofoperator): each operand once, in order.</summary>
internal sealed class KernelInstanceOf : KernelExpression
{
    internal readonly KernelExpression _left;
    internal readonly KernelExpression _right;

    public KernelInstanceOf(NonLogicalBinaryExpression expression, KernelExpression left, KernelExpression right) : base(expression)
    {
        _left = left;
        _right = right;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        return Test(frame) == 1 ? JsBoolean.True : JsBoolean.False;
    }

    internal override int Test(KernelFrame frame)
    {
        var left = Box(_left.Evaluate(frame, out var leftNumber), leftNumber);
        var right = Box(_right.Evaluate(frame, out var rightNumber), rightNumber);
        frame.Context.PrepareFor(_expression);
        return left.InstanceofOperator(right) ? 1 : 0;
    }
}

/// <summary><c>test ? consequent : alternate</c> with an operand that is not pure: the test, then one branch, once each.</summary>
internal sealed class KernelConditionalEffect : KernelExpression
{
    internal readonly KernelExpression _test;
    internal readonly KernelExpression _consequent;
    internal readonly KernelExpression _alternate;

    public KernelConditionalEffect(Expression expression, KernelExpression test, KernelExpression consequent, KernelExpression alternate) : base(expression)
    {
        _test = test;
        _consequent = consequent;
        _alternate = alternate;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
        => _test.Test(frame) == 1 ? _consequent.Evaluate(frame, out number) : _alternate.Evaluate(frame, out number);

    internal override int Test(KernelFrame frame) => _test.Test(frame) == 1 ? _consequent.Test(frame) : _alternate.Test(frame);
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
