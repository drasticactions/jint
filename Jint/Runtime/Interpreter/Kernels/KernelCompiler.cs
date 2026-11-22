using Jint.Native;
using Jint.Runtime.Interpreter.Expressions;
using Jint.Runtime.Interpreter.Statements;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// Builds the node tree of a <see cref="LoopKernel"/> from a <c>for</c> statement. A construct it has no node for
/// becomes a node that runs the interpreter's handler for it, so the tree covers every loop; the compiler only decides
/// how much of it runs on kernel nodes.
/// </summary>
internal sealed class KernelCompiler
{
    private readonly List<Environment.BindingName> _names = [];
    private readonly Dictionary<Key, int> _nameIndices = [];
    private int _compiled;

    internal Environment.BindingName[] Names => _names.ToArray();

    /// <summary>Whether any part of the loop runs on kernel nodes rather than on the interpreter's handlers.</summary>
    internal bool CompiledAnything => _compiled > 0;

    /// <summary>The loop, or null when its header is not one a kernel runs: a lexical declaration or a label.</summary>
    internal KernelForStatement? CompileLoop(ForStatement statement, JintForStatement? owner)
    {
        KernelStatement? initStatement = null;
        KernelExpression? initExpression = null;
        switch (statement.Init)
        {
            case null:
                break;
            case VariableDeclaration { Kind: VariableDeclarationKind.Var } declaration:
                initStatement = CompileStatement(declaration, blockLevel: false);
                break;
            case VariableDeclaration:
                return null;
            case Expression expression:
                initExpression = CompileEffect(expression);
                break;
            default:
                return null;
        }

        if (owner is null && statement.LabelSet is not null)
        {
            return null;
        }

        var test = statement.Test is null ? null : CompileTest(statement.Test);
        var update = statement.Update is null ? null : CompileEffect(statement.Update);
        var body = CompileStatement(statement.Body, blockLevel: false);
        return new KernelForStatement(statement, initStatement, initExpression, test, update, body, owner);
    }

    /// <summary>A statement of a statement list, where a <c>return</c> of a literal is resolved without running it.</summary>
    private KernelStatement CompileListStatement(Statement statement, bool blockLevel)
    {
        if (JintStatement.FastResolve(statement) is { } value)
        {
            _compiled++;
            return new KernelLiteralReturnStatement(statement, value);
        }

        return CompileStatement(statement, blockLevel);
    }

    private KernelStatement CompileStatement(Statement statement, bool blockLevel)
    {
        var compiled = TryCompileStatement(statement);
        if (compiled is not null)
        {
            _compiled++;
            return compiled;
        }

        var handler = blockLevel ? JintStatement.BuildBlockLevel(statement) : JintStatement.Build(statement);
        return new KernelInterpretedStatement(statement, handler);
    }

    private KernelStatement? TryCompileStatement(Statement statement)
    {
        switch (statement)
        {
            case ExpressionStatement expressionStatement:
                return new KernelExpressionStatement(statement, CompileEffect(expressionStatement.Expression));

            case VariableDeclaration { Kind: VariableDeclarationKind.Var } declaration:
                {
                    var initializers = new List<KernelExpression>();
                    foreach (var declarator in declaration.Declarations)
                    {
                        if (declarator.Id is not Identifier id)
                        {
                            return null;
                        }

                        if (declarator.Init is null)
                        {
                            continue;
                        }

                        var initializer = CompileSlotAssignment(declarator.Init, id, declarator.Init, isDeclarator: true);
                        if (initializer is null)
                        {
                            return null;
                        }

                        initializers.Add(initializer);
                    }

                    return new KernelVarStatement(statement, initializers.ToArray());
                }

            case IfStatement ifStatement:
                if (ifStatement.Consequent is FunctionDeclaration || ifStatement.Alternate is FunctionDeclaration)
                {
                    return null;
                }

                return new KernelIfStatement(
                    statement,
                    CompileTest(ifStatement.Test),
                    CompileStatement(ifStatement.Consequent, blockLevel: false),
                    ifStatement.Alternate is null ? null : CompileStatement(ifStatement.Alternate, blockLevel: false));

            case NestedBlockStatement block:
                {
                    foreach (var nested in block.Body)
                    {
                        if (nested is ClassDeclaration or FunctionDeclaration
                            || nested is VariableDeclaration { Kind: not VariableDeclarationKind.Var })
                        {
                            return null;
                        }
                    }

                    var statements = new KernelStatement[block.Body.Count];
                    for (var i = 0; i < statements.Length; i++)
                    {
                        // One statement runs alone, the statements of any other block as a statement list.
                        statements[i] = statements.Length == 1
                            ? CompileStatement(block.Body[i], blockLevel: true)
                            : CompileListStatement(block.Body[i], blockLevel: true);
                    }

                    return new KernelBlockStatement(statement, statements);
                }

            case EmptyStatement:
                return new KernelEmptyStatement(statement);

            case BreakStatement { Label: null }:
                return new KernelJumpStatement(statement, isBreak: true);

            case ContinueStatement { Label: null }:
                return new KernelJumpStatement(statement, isBreak: false);

            case ForStatement forStatement:
                return CompileLoop(forStatement, owner: null);

            case ReturnStatement returnStatement:
                // An async generator's own return awaits; a call in tail position is made as a tail call.
                if (JintAsyncGeneratorReturnStatement.IsMarked(returnStatement)
                    || returnStatement.Argument is { } argument && HasTailCall(argument))
                {
                    return null;
                }

                return new KernelReturnStatement(statement, returnStatement.Argument is null ? null : CompileValue(returnStatement.Argument));

            default:
                return null;
        }
    }

    /// <summary>An expression in statement position, or a loop's initializer or update.</summary>
    private KernelExpression CompileEffect(Expression expression) => CompileValue(expression);

    /// <summary>An expression whose value is used once, by something that is not a pure expression.</summary>
    private KernelExpression CompileValue(Expression expression)
    {
        var effect = TryCompileEffect(expression);
        if (effect is not null)
        {
            _compiled++;
            return effect;
        }

        var pure = TryCompilePure(expression);
        if (pure is not null)
        {
            _compiled++;
            return new KernelRoot(pure);
        }

        return new KernelInterpreted(expression);
    }

    private KernelExpression CompileTest(Expression expression) => CompileValue(expression);

    private KernelExpression? TryCompileEffect(Expression expression)
    {
        switch (expression)
        {
            case AssignmentExpression { Operator: Operator.Assignment } assignment:
                if (assignment.Left is Identifier target)
                {
                    return CompileSlotAssignment(assignment, target, assignment.Right, isDeclarator: false);
                }

                if (assignment.Left is MemberExpression { Computed: true, Optional: false, Object: not Super } member)
                {
                    var obj = TryCompilePure(member.Object);
                    var key = obj is null ? null : TryCompilePure(member.Property);
                    var value = key is null ? null : TryCompilePure(assignment.Right);
                    if (value is not null)
                    {
                        return new KernelStoreElement(assignment, obj!, key!, value);
                    }
                }

                return null;

            case AssignmentExpression compound:
                {
                    if (compound.Left is not Identifier compoundTarget
                        || IsEvalOrArguments(compoundTarget)
                        || KernelCompoundSlot.BinaryOperator(compound.Operator) is not { } op)
                    {
                        return null;
                    }

                    var right = TryCompilePure(compound.Right);
                    if (right is null)
                    {
                        return null;
                    }

                    var name = Name(compoundTarget);
                    return new KernelCompoundSlot(compound, name, new KernelBinary(compound, op, name, right));
                }

            case UpdateExpression { Argument: Identifier updateTarget } update when !IsEvalOrArguments(updateTarget):
                return new KernelUpdateSlot(update, Name(updateTarget));

            case NonLogicalBinaryExpression binary when KernelComparison.IsSupported(binary.Operator) && TryCompilePure(binary) is null:
                {
                    // A comparison with an operand that is not pure, a call above all: each operand once, in order.
                    var left = TryCompileEffect(binary.Left);
                    var right = TryCompileEffect(binary.Right);
                    if (left is null && right is null)
                    {
                        return null;
                    }

                    return new KernelComparison(binary, binary.Operator, left ?? CompileValue(binary.Left), right ?? CompileValue(binary.Right));
                }

            case LogicalExpression { Operator: Operator.LogicalAnd or Operator.LogicalOr } logical when TryCompilePure(logical) is null:
                {
                    // An operand that is not pure, a call above all: each operand at most once, in order.
                    var left = TryCompileEffect(logical.Left);
                    var right = TryCompileEffect(logical.Right);
                    if (left is null && right is null)
                    {
                        return null;
                    }

                    return new KernelLogicalEffect(logical, logical.Operator == Operator.LogicalAnd, left ?? CompileValue(logical.Left), right ?? CompileValue(logical.Right));
                }

            case NonUpdateUnaryExpression { Operator: Operator.LogicalNot } not when TryCompilePure(not) is null:
                {
                    var argument = TryCompileEffect(not.Argument);
                    return argument is null ? null : new KernelNotEffect(not, argument);
                }

            case CallExpression call:
                return TryCompileCall(call);

            default:
                return null;
        }
    }

    /// <summary>An assignment to a name, or (<paramref name="isDeclarator"/>) a var initializer, whose node is the initializer.</summary>
    private KernelExpression? CompileSlotAssignment(Expression node, Identifier target, Expression value, bool isDeclarator)
    {
        if (IsEvalOrArguments(target) || value.IsFunctionDefinition())
        {
            return null;
        }

        return new KernelAssignSlot(node, Name(target), CompileValue(value), isDeclarator);
    }

    /// <summary>Whether the static analysis marked a call in the expression as one in tail position.</summary>
    private static bool HasTailCall(Expression expression) => expression switch
    {
        CallExpression or TaggedTemplateExpression => true,
        ConditionalExpression conditional => HasTailCall(conditional.Consequent) || HasTailCall(conditional.Alternate),
        LogicalExpression logical => HasTailCall(logical.Right),
        SequenceExpression sequence => HasTailCall(sequence.Expressions[sequence.Expressions.Count - 1]),
        _ => false,
    };

    private KernelExpression? TryCompileCall(CallExpression call)
    {
        if (call.Optional || ReferenceEquals(call.UserData, TailCallMarker.Instance))
        {
            return null;
        }

        foreach (var argument in call.Arguments)
        {
            if (argument is SpreadElement)
            {
                return null;
            }
        }

        KernelProperty? member = null;
        KernelExpression? callee = null;
        switch (call.Callee)
        {
            case MemberExpression { Computed: false, Optional: false, Object: not Super, Property: Identifier property } memberCallee:
                {
                    var obj = TryCompilePure(memberCallee.Object);
                    if (obj is null)
                    {
                        return null;
                    }

                    member = new KernelProperty(memberCallee, obj, property.Name);
                    break;
                }

            case Identifier identifier when !string.Equals(identifier.Name, "eval", StringComparison.Ordinal):
                callee = TryCompilePure(identifier);
                if (callee is null)
                {
                    return null;
                }

                break;

            default:
                return null;
        }

        var arguments = new KernelExpression[call.Arguments.Count];
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = CompileValue(call.Arguments[i]);
        }

        return new KernelCall(call, member, callee, arguments);
    }

    /// <summary>A pure expression (see <see cref="KernelExpression"/>), or null.</summary>
    private KernelExpression? TryCompilePure(Expression expression)
    {
        switch (expression)
        {
            case Identifier identifier:
                {
                    var name = identifier.UserData as Environment.BindingName ?? new Environment.BindingName(identifier.Name);
                    if (name.CalculatedValue is { } calculated)
                    {
                        return new KernelConstant(identifier, calculated);
                    }

                    return IsEvalOrArguments(identifier) ? null : Name(identifier);
                }

            case Literal literal:
                {
                    var value = JintLiteralExpression.ConvertToJsValue(literal);
                    if (value is null || value is JsBigInt)
                    {
                        return null;
                    }

                    return value is JsNumber number ? new KernelNumber(literal, number._value) : new KernelConstant(literal, value);
                }

            case NonLogicalBinaryExpression binary when KernelBinary.IsSupported(binary.Operator):
                {
                    var left = TryCompilePure(binary.Left);
                    var right = left is null ? null : TryCompilePure(binary.Right);
                    return right is null ? null : new KernelBinary(binary, binary.Operator, left!, right);
                }

            case LogicalExpression { Operator: Operator.LogicalAnd or Operator.LogicalOr } logical:
                {
                    var left = TryCompilePure(logical.Left);
                    var right = left is null ? null : TryCompilePure(logical.Right);
                    return right is null ? null : new KernelLogical(logical, logical.Operator == Operator.LogicalAnd, left!, right);
                }

            case NonUpdateUnaryExpression unary when KernelUnary.IsSupported(unary.Operator):
                {
                    var argument = TryCompilePure(unary.Argument);
                    return argument is null ? null : new KernelUnary(unary, unary.Operator, argument);
                }

            case ConditionalExpression conditional:
                {
                    var test = TryCompilePure(conditional.Test);
                    var consequent = test is null ? null : TryCompilePure(conditional.Consequent);
                    var alternate = consequent is null ? null : TryCompilePure(conditional.Alternate);
                    return alternate is null ? null : new KernelConditional(conditional, test!, consequent!, alternate);
                }

            case MemberExpression { Optional: false, Object: not Super } member:
                {
                    var obj = TryCompilePure(member.Object);
                    if (obj is null)
                    {
                        return null;
                    }

                    if (member.Computed)
                    {
                        var key = TryCompilePure(member.Property);
                        return key is null ? null : new KernelElement(member, obj, key);
                    }

                    return member.Property is Identifier property ? new KernelProperty(member, obj, property.Name) : null;
                }

            default:
                return null;
        }
    }

    private KernelName Name(Identifier identifier)
    {
        var name = identifier.UserData as Environment.BindingName ?? new Environment.BindingName(identifier.Name);
        if (!_nameIndices.TryGetValue(name.Key, out var index))
        {
            index = _names.Count;
            _names.Add(name);
            _nameIndices[name.Key] = index;
        }

        return new KernelName(identifier, index, name);
    }

    private static bool IsEvalOrArguments(Identifier identifier)
        => identifier.Name is "eval" or "arguments";
}
