using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// The body of a function a call site computes in place (see <see cref="KernelInlineSite"/>): <c>return</c> of
/// Number arithmetic and comparisons over its simple parameters, Number literals and the Math functions of
/// <see cref="KernelIntrinsics"/>, so that for Number arguments it runs no script, throws nothing and allocates
/// nothing. Compiled code computes it in its own instructions; the tree computes it with <see cref="Compute"/>.
/// </summary>
internal sealed class KernelInlineBody
{
    internal readonly Expression Result;
    internal readonly string[] Parameters;
    internal readonly List<int> MathKinds = [];
    internal bool IsBoolean;

    // The body as the tree computes it: a postfix program over a stack of Numbers (see Compute).
    private int[] _program = [];
    private double[] _constants = [];
    private int _depth;

    private const int PushParameter = 0;
    private const int PushConstant = 1;
    private const int Negate = 2;
    private const int Binary = 3;
    private const int CallMath = 4;

    private KernelInlineBody(Expression result, string[] parameters)
    {
        Result = result;
        Parameters = parameters;
    }

    internal static KernelInlineBody? TryCreate(JintFunctionDefinition definition, int argumentCount)
    {
        var function = definition.Function;
        if (function.Generator || function.Async || function.Params.Count != argumentCount || argumentCount > 4)
        {
            return null;
        }

        var parameters = new string[argumentCount];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (function.Params[i] is not Identifier { Name: var name }
                || name is "arguments" or "eval" or "Math"
                || System.Array.IndexOf(parameters, name, 0, i) >= 0)
            {
                return null;
            }

            parameters[i] = name;
        }

        // A return of a literal runs without its statement check on the interpreter, which the call then makes.
        if (function.Body is not FunctionBody { Body: { Count: 1 } statements }
            || statements[0] is not ReturnStatement { Argument: { } result }
            || result is Literal)
        {
            return null;
        }

        var body = new KernelInlineBody(result, parameters);
        var kind = body.Classify(result, 0);
        if (kind == Invalid)
        {
            return null;
        }

        body.IsBoolean = kind == Boolean;
        var program = new List<int>();
        var constants = new List<double>();
        var depth = 0;
        body.CompileProgram(result, program, constants, 0, ref depth);
        body._program = program.ToArray();
        body._constants = constants.ToArray();
        body._depth = depth;
        return body;
    }

    internal const int Invalid = 0;
    internal const int Number = 1;
    internal const int Boolean = 2;

    /// <summary>
    /// How deep a body computed in place may nest. Classifying and compiling it recurse once per level, with no
    /// stack probe on the way, and a generated body may be an operator chain thousands of levels deep.
    /// </summary>
    private const int MaxDepth = SyntaxNesting.EvaluationProbeInterval;

    /// <summary>What <paramref name="expression"/> answers for Number parameters: a Number, a boolean, or something not computed in place.</summary>
    private int Classify(Expression expression, int depth)
    {
        if (depth >= MaxDepth)
        {
            return Invalid;
        }

        depth++;
        switch (expression)
        {
            case Literal literal:
                return JintLiteralExpression.ConvertToJsValue(literal) is JsNumber ? Number : Invalid;

            case Identifier identifier:
                return System.Array.IndexOf(Parameters, identifier.Name) >= 0 ? Number : Invalid;

            case NonUpdateUnaryExpression { Operator: Operator.UnaryNegation or Operator.UnaryPlus } unary:
                return Classify(unary.Argument, depth) == Number ? Number : Invalid;

            case NonLogicalBinaryExpression binary when KernelBinary.IsSupported(binary.Operator):
                if (Classify(binary.Left, depth) != Number || Classify(binary.Right, depth) != Number)
                {
                    return Invalid;
                }

                return KernelBinary.IsComparison(binary.Operator) ? Boolean : Number;

            case CallExpression
            {
                Optional: false,
                Callee: MemberExpression { Computed: false, Optional: false, Object: Identifier { Name: "Math" }, Property: Identifier { Name: var name } },
                Arguments: { Count: 1 } arguments,
            } when arguments[0] is Expression argument && KernelIntrinsics.Kind(name) is var kind and not KernelIntrinsics.None:
                if (Classify(argument, depth) != Number)
                {
                    return Invalid;
                }

                if (!MathKinds.Contains(kind))
                {
                    MathKinds.Add(kind);
                }

                return Number;

            default:
                return Invalid;
        }
    }

    /// <summary>The postfix program of an expression <see cref="Classify"/> accepted, and the stack depth it needs.</summary>
    private void CompileProgram(Expression expression, List<int> program, List<double> constants, int height, ref int depth)
    {
        switch (expression)
        {
            case Literal literal:
                program.Add(PushConstant);
                program.Add(constants.Count);
                constants.Add(((JsNumber) JintLiteralExpression.ConvertToJsValue(literal)!)._value);
                depth = System.Math.Max(depth, height + 1);
                return;

            case Identifier identifier:
                program.Add(PushParameter);
                program.Add(System.Array.IndexOf(Parameters, identifier.Name));
                depth = System.Math.Max(depth, height + 1);
                return;

            case NonUpdateUnaryExpression unary:
                CompileProgram(unary.Argument, program, constants, height, ref depth);
                if (unary.Operator == Operator.UnaryNegation)
                {
                    program.Add(Negate);
                    program.Add(0);
                }

                return;

            case NonLogicalBinaryExpression binary:
                CompileProgram(binary.Left, program, constants, height, ref depth);
                CompileProgram(binary.Right, program, constants, height + 1, ref depth);
                program.Add(Binary);
                program.Add((int) binary.Operator);
                return;

            default:
                {
                    var call = (CallExpression) expression;
                    CompileProgram((Expression) call.Arguments[0], program, constants, height, ref depth);
                    program.Add(CallMath);
                    program.Add(KernelIntrinsics.Kind(((Identifier) ((MemberExpression) call.Callee).Property).Name));
                    return;
                }
        }
    }

    /// <summary>
    /// The body's value for Number arguments: the Number, or for a comparison 1 when it is true and 0 when it is false.
    /// </summary>
    internal double Compute(double arg0, double arg1, double arg2, double arg3)
    {
        Span<double> parameters = stackalloc double[4];
        parameters[0] = arg0;
        parameters[1] = arg1;
        parameters[2] = arg2;
        parameters[3] = arg3;
        Span<double> stack = stackalloc double[_depth];
        var top = -1;
        var program = _program;
        for (var pc = 0; pc < program.Length; pc += 2)
        {
            var operand = program[pc + 1];
            switch (program[pc])
            {
                case PushParameter:
                    stack[++top] = parameters[operand];
                    break;
                case PushConstant:
                    stack[++top] = _constants[operand];
                    break;
                case Negate:
                    stack[top] = -stack[top];
                    break;
                case CallMath:
                    stack[top] = KernelIntrinsics.Compute(operand, stack[top]);
                    break;
                default:
                    {
                        var op = (Operator) operand;
                        var right = stack[top--];
                        var left = stack[top];
                        stack[top] = KernelBinary.IsComparison(op)
                            ? (KernelBinary.Compare(op, left, right) ? 1 : 0)
                            : KernelBinary.Apply(op, left, right);
                        break;
                    }
            }
        }

        return stack[0];
    }
}
