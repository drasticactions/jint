using Jint.Native;
using Jint.Runtime.Interpreter.Expressions;
using Jint.Runtime.Interpreter.Statements;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// Builds the node tree of a <see cref="LoopKernel"/> or a <see cref="FunctionKernel"/>, where a construct without a
/// node of its own becomes a node that runs the interpreter's handler for it.
/// </summary>
internal sealed class KernelCompiler
{
    private readonly List<Environment.BindingName> _names = [];
    private readonly Dictionary<Key, int> _nameIndices = [];
    private int _compiled;

    // The scopes around the statement being compiled, innermost last, and for each binding whether a scope declares it.
    private readonly List<Dictionary<Key, int>> _scopes = [];
    private readonly List<bool> _scopedNames = [];
    private bool _anyScoped;

    // How many statements loop code runs as a whole are around the statement being compiled.
    private int _opaque;

    // The outermost loop with a code entry around the statement being compiled, by its index in Loops, or -1.
    private int _rootLoop = -1;

    /// <summary>For each binding whether a scope of the kernel declares it, or null where none does.</summary>
    internal bool[]? Scoped => _anyScoped ? _scopedNames.ToArray() : null;

    /// <summary>For each loop in <see cref="Loops"/>, the index of the outermost loop it is in (its own for an outermost one).</summary>
    internal List<int> LoopRoots { get; } = [];

    /// <summary>How many nodes each outermost loop has, by its index in <see cref="Loops"/>.</summary>
    internal Dictionary<int, int> RootSizes { get; } = [];

    /// <summary>
    /// How deep the kernel tree may nest, statements and expressions together. Compiling recurses once per level,
    /// and so does running the tree, with no stack probe on the way; so a construct below that depth runs on the
    /// interpreter's node for it, which carries the probes a long chain needs (see <see cref="SyntaxNesting"/>).
    /// A generated loop may hold an operator or call chain thousands of levels deep.
    /// </summary>
    private const int MaxDepth = SyntaxNesting.EvaluationProbeInterval;

    private int _depth;

    internal Environment.BindingName[] Names => _names.ToArray();

    /// <summary>Whether any part of the loop runs on kernel nodes rather than on the interpreter's handlers.</summary>
    internal bool CompiledAnything => _compiled > 0;

    /// <summary>How many nodes the kernel has, what compiling it costs in proportion to.</summary>
    internal int Size => _compiled;

    /// <summary>The loops compiled, the outermost first.</summary>
    internal List<KernelForStatement> Loops { get; } = [];

    /// <summary>
    /// The loop, or null when its header is not one a kernel runs: a lexical declaration of a loop the interpreter
    /// owns, one the kernel cannot scope, or a label the loop is not compiled with.
    /// </summary>
    internal KernelForStatement? CompileLoop(ForStatement statement, JintForStatement? owner, string[]? labels = null)
    {
        if (owner is null && statement.LabelSet is not null && labels is null)
        {
            return null;
        }

        KernelScope? scope = null;
        if (statement.Init is VariableDeclaration { Kind: not VariableDeclarationKind.Var } lexical)
        {
            if (owner is not null || lexical.Kind is not (VariableDeclarationKind.Let or VariableDeclarationKind.Const))
            {
                return null;
            }

            var keys = new List<Key>();
            lexical.GetBoundNames(keys);
            var captured = JintForStatement.ForLoopMayCapture(statement);
            scope = new KernelScope(BeginScope(keys), keys.ToArray(), lexical.Kind == VariableDeclarationKind.Const, slots: !captured && keys.Count <= 16, catchScope: false);
            scope._perIteration = captured && lexical.Kind == VariableDeclarationKind.Let;
        }

        // A lexical header keeps a loop on the tree; a loop in a statement loop code runs whole is an outermost loop.
        var hasEntry = scope is null;
        var outerRoot = _rootLoop;
        var outerOpaque = _opaque;
        if (hasEntry && _opaque > 0)
        {
            _rootLoop = -1;
            _opaque = 0;
        }

        var root = _rootLoop;
        var index = -1;
        var size = _compiled;
        if (hasEntry)
        {
            index = Loops.Count;
            Loops.Add(null!);
            LoopRoots.Add(root >= 0 ? root : index);
            if (root < 0)
            {
                _rootLoop = index;
            }
        }
        else
        {
            _opaque++;
        }

        try
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
                case VariableDeclaration declaration:
                    initStatement = TryCompileLexicalDeclaration(declaration);
                    if (initStatement is null)
                    {
                        return null;
                    }

                    _compiled++;
                    break;
                case Expression expression:
                    initExpression = CompileEffect(expression);
                    break;
                default:
                    return null;
            }

            var test = statement.Test is null ? null : CompileTest(statement.Test);
            var update = statement.Update is null ? null : CompileEffect(statement.Update);
            var body = CompileStatement(statement.Body, blockLevel: false);
            var loop = new KernelForStatement(statement, initStatement, initExpression, test, update, body, owner)
            {
                _labels = labels,
                _scope = scope,
                _hasEntry = hasEntry,
            };

            if (hasEntry)
            {
                Loops[index] = loop;
                if (root < 0)
                {
                    RootSizes[index] = _compiled - size;
                }
            }

            return loop;
        }
        finally
        {
            if (scope is not null)
            {
                EndScope();
            }

            _rootLoop = outerRoot;
            _opaque = outerOpaque;
        }
    }

    // Whether empty object literals left to the interpreter start hot, as in a body the interpreter has run often.
    private bool _sitesStartHot;

    // Whether a call in tail position may be made as a tail call: not in a sloppy function, which makes none.
    private bool _tailCalls = true;

    /// <summary>A function body, checked as a statement and handling the exceptions of its statements as a block does.</summary>
    internal KernelBlockStatement CompileFunctionBody(FunctionBody body, bool strict)
    {
        _sitesStartHot = true;
        _tailCalls = strict;
        var statements = new KernelStatement[body.Body.Count];
        for (var i = 0; i < statements.Length; i++)
        {
            statements[i] = CompileListStatement(body.Body[i], blockLevel: false);
        }

        return new KernelBlockStatement(body, statements, isChecked: true);
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
        KernelStatement? compiled = null;
        if (_depth < MaxDepth)
        {
            _depth++;
            try
            {
                compiled = TryCompileStatement(statement);
            }
            finally
            {
                _depth--;
            }
        }

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

                        var initializer = CompileSlotAssignment(declarator.Init, id, declarator.Init, isDeclarator: true, name: declarator.Init.IsFunctionDefinition() ? id.Name : null);
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
                return TryCompileBlock(block);

            case VariableDeclaration { Kind: VariableDeclarationKind.Let or VariableDeclarationKind.Const } lexical:
                return TryCompileLexicalDeclaration(lexical);

            case EmptyStatement:
                return new KernelEmptyStatement(statement);

            case BreakStatement { Label: null }:
                return new KernelJumpStatement(statement, isBreak: true);

            case ContinueStatement { Label: null }:
                return new KernelJumpStatement(statement, isBreak: false);

            case BreakStatement or ContinueStatement:
                return new KernelLabeledJumpStatement(statement, isBreak: statement is BreakStatement);

            case ForStatement forStatement:
                return CompileLoop(forStatement, owner: null);

            case WhileStatement or DoWhileStatement:
                return Opaque(() => TryCompileWhile(statement, labels: null));

            case ForInStatement or ForOfStatement:
                return Opaque(() => TryCompileForInOf(statement, labels: null));

            case SwitchStatement switchStatement:
                return Opaque(() => TryCompileSwitch(switchStatement));

            case TryStatement tryStatement:
                return Opaque(() => TryCompileTry(tryStatement));

            case LabeledStatement labeled:
                return TryCompileLabeled(labeled);

            case ReturnStatement returnStatement:
                // An async generator's own return awaits; a call in tail position is made as a tail call.
                if (JintAsyncGeneratorReturnStatement.IsMarked(returnStatement)
                    || returnStatement.Argument is { } argument && _tailCalls && HasTailCall(argument))
                {
                    return null;
                }

                return new KernelReturnStatement(statement, returnStatement.Argument is null ? null : CompileValue(returnStatement.Argument));

            default:
                return null;
        }
    }

    /// <summary>A statement compiled where loop code runs it as a whole, so that loops in it are outermost loops.</summary>
    private KernelStatement? Opaque(Func<KernelStatement?> compile)
    {
        _opaque++;
        try
        {
            return compile();
        }
        finally
        {
            _opaque--;
        }
    }

    /// <summary>A block; one that declares let or const names runs in a scope of its own.</summary>
    private KernelStatement? TryCompileBlock(NestedBlockStatement block)
    {
        foreach (var nested in block.Body)
        {
            if (nested is ClassDeclaration or FunctionDeclaration or VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing })
            {
                return null;
            }
        }

        var state = (JintBlockStatement.BlockState) (block.UserData ??= JintBlockStatement.BuildState(block));
        KernelScope? scope = null;
        if (state.Declarations.Count > 0)
        {
            var keys = new List<Key>();
            foreach (var declaration in state.Declarations)
            {
                if (declaration.Declaration is not VariableDeclaration { Kind: VariableDeclarationKind.Let or VariableDeclarationKind.Const })
                {
                    return null;
                }

                keys.AddRange(declaration.BoundNames);
            }

            scope = new KernelScope(BeginScope(keys), keys.ToArray(), state);
            _opaque++;
        }

        try
        {
            var statements = new KernelStatement[block.Body.Count];
            for (var i = 0; i < statements.Length; i++)
            {
                // One statement runs alone, the statements of any other block as a statement list.
                statements[i] = statements.Length == 1
                    ? CompileStatement(block.Body[i], blockLevel: true)
                    : CompileListStatement(block.Body[i], blockLevel: true);
            }

            var compiled = new KernelBlockStatement(block, statements);
            return scope is null ? compiled : new KernelScopeStatement(block, scope, compiled);
        }
        finally
        {
            if (scope is not null)
            {
                EndScope();
                _opaque--;
            }
        }
    }

    /// <summary><c>let</c> or <c>const</c>: each declarator initializing its binding in the scope it is in.</summary>
    private KernelLexicalDeclaration? TryCompileLexicalDeclaration(VariableDeclaration declaration)
    {
        var count = declaration.Declarations.Count;
        var targets = new KernelName?[count];
        var patterns = new DestructuringPattern?[count];
        var initializers = new KernelExpression?[count];
        for (var i = 0; i < count; i++)
        {
            var declarator = declaration.Declarations[i];
            var init = declarator.Init;
            if (init is ClassExpression)
            {
                return null;
            }

            if (declarator.Id is Identifier id)
            {
                if (IsEvalOrArguments(id))
                {
                    return null;
                }

                targets[i] = Name(id);
                initializers[i] = init is null ? null : init.IsFunctionDefinition() ? CompileFunction(init, id.Name) : CompileValue(init);
            }
            else if (declarator.Id is DestructuringPattern pattern && init is not null)
            {
                patterns[i] = pattern;
                initializers[i] = CompileValue(init);
            }
            else
            {
                return null;
            }
        }

        return new KernelLexicalDeclaration(declaration, targets, patterns, initializers);
    }

    /// <summary>A while or do-while loop, with the labels it is labelled with.</summary>
    private KernelWhileStatement TryCompileWhile(Statement statement, string[]? labels)
    {
        if (statement is WhileStatement whileStatement)
        {
            var test = CompileTest(whileStatement.Test);
            return new KernelWhileStatement(statement, test, CompileStatement(whileStatement.Body, blockLevel: false), labels, doWhile: false);
        }

        var doWhile = (DoWhileStatement) statement;
        var body = CompileStatement(doWhile.Body, blockLevel: false);
        return new KernelWhileStatement(statement, CompileTest(doWhile.Test), body, labels, doWhile: true);
    }

    /// <summary>
    /// A for-in or for-of loop (not <c>for await</c>) whose head is a name, a member, a pattern or a declaration of one
    /// binding or pattern; a lexical head whose right-hand side could see its names stays on the interpreter.
    /// </summary>
    private KernelForInOfStatement? TryCompileForInOf(Statement statement, string[]? labels)
    {
        Node left;
        Expression right;
        Statement bodyStatement;
        bool enumerate;
        if (statement is ForOfStatement forOf)
        {
            if (forOf.Await)
            {
                return null;
            }

            (left, right, bodyStatement, enumerate) = (forOf.Left, forOf.Right, forOf.Body, false);
        }
        else
        {
            var forIn = (ForInStatement) statement;
            (left, right, bodyStatement, enumerate) = (forIn.Left, forIn.Right, forIn.Body, true);
        }

        KernelScope? scope = null;
        KernelName? target = null;
        DestructuringPattern? pattern = null;
        Expression? member = null;
        var lexical = false;
        var var = false;
        switch (left)
        {
            case VariableDeclaration { Kind: VariableDeclarationKind.Var } declaration:
                {
                    var declarator = declaration.Declarations[0];
                    if (declarator.Init is not null)
                    {
                        return null;
                    }

                    var = true;
                    if (declarator.Id is Identifier id)
                    {
                        if (IsEvalOrArguments(id))
                        {
                            return null;
                        }

                        target = Name(id);
                    }
                    else
                    {
                        pattern = (DestructuringPattern) declarator.Id;
                    }

                    break;
                }

            case VariableDeclaration { Kind: VariableDeclarationKind.Let or VariableDeclarationKind.Const }:
                lexical = true;
                break;

            case VariableDeclaration:
                return null;

            case Identifier id:
                if (IsEvalOrArguments(id))
                {
                    return null;
                }

                target = Name(id);
                break;

            case MemberExpression memberExpression:
                member = memberExpression;
                break;

            case DestructuringPattern destructuring:
                pattern = destructuring;
                break;

            default:
                return null;
        }

        var rightValue = CompileValue(right);
        if (lexical)
        {
            var declaration = (VariableDeclaration) left;
            var keys = new List<Key>();
            declaration.GetBoundNames(keys);
            if (MayRead(right, keys))
            {
                return null;
            }

            var declarator = declaration.Declarations[0];
            var destructuring = declarator.Id is DestructuringPattern;
            var reuse = keys.Count is > 0 and <= 16
                        && !JintFunctionDefinition.EnvironmentEscapeAstVisitor.IsCapturing(bodyStatement)
                        && !JintFunctionDefinition.EnvironmentEscapeAstVisitor.MayEscape(bodyStatement)
                        && (!destructuring
                            || (!JintFunctionDefinition.EnvironmentEscapeAstVisitor.IsCapturing(declarator.Id)
                                && !JintFunctionDefinition.EnvironmentEscapeAstVisitor.MayEscape(declarator.Id)));
            scope = new KernelScope(BeginScope(keys), keys.ToArray(), declaration.Kind == VariableDeclarationKind.Const, slots: reuse, catchScope: false);
            if (destructuring)
            {
                pattern = (DestructuringPattern) declarator.Id;
            }
            else
            {
                target = Name((Identifier) declarator.Id);
            }
        }

        try
        {
            var body = CompileStatement(bodyStatement, blockLevel: false);
            return new KernelForInOfStatement(statement, enumerate, rightValue, body, labels, scope, target, pattern, member, var);
        }
        finally
        {
            if (scope is not null)
            {
                EndScope();
            }
        }
    }

    /// <summary>Whether <paramref name="node"/> names any of <paramref name="keys"/>, or eval, anywhere in it.</summary>
    private static bool MayRead(Node node, List<Key> keys)
    {
        var pending = new Stack<Node>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is Identifier identifier)
            {
                if (string.Equals(identifier.Name, "eval", StringComparison.Ordinal))
                {
                    return true;
                }

                foreach (var key in keys)
                {
                    if (string.Equals(key.Name, identifier.Name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            foreach (var child in current.ChildNodes)
            {
                pending.Push(child);
            }
        }

        return false;
    }

    /// <summary>A switch whose cases declare nothing lexically.</summary>
    private KernelSwitchStatement? TryCompileSwitch(SwitchStatement statement)
    {
        foreach (var switchCase in statement.Cases)
        {
            foreach (var nested in switchCase.Consequent)
            {
                if (nested is ClassDeclaration or FunctionDeclaration or VariableDeclaration { Kind: not VariableDeclarationKind.Var })
                {
                    return null;
                }
            }
        }

        var discriminant = CompileValue(statement.Discriminant);
        var tests = new KernelExpression?[statement.Cases.Count];
        var consequents = new KernelBlockStatement[tests.Length];
        for (var i = 0; i < tests.Length; i++)
        {
            var switchCase = statement.Cases[i];
            tests[i] = switchCase.Test is null ? null : CompileValue(switchCase.Test);
            var statements = new KernelStatement[switchCase.Consequent.Count];
            for (var j = 0; j < statements.Length; j++)
            {
                statements[j] = CompileListStatement(switchCase.Consequent[j], blockLevel: true);
            }

            // A case's statements run as a statement list without a statement of its own: unchecked.
            consequents[i] = new KernelBlockStatement(statement, statements, isChecked: false);
        }

        return new KernelSwitchStatement(statement, discriminant, tests, consequents);
    }

    /// <summary>A try statement; a catch parameter is bound in a scope of its own.</summary>
    private KernelTryStatement TryCompileTry(TryStatement statement)
    {
        var block = CompileStatement(statement.Block, blockLevel: false);
        KernelScope? scope = null;
        KernelStatement? handler = null;
        if (statement.Handler is { } clause)
        {
            switch (clause.Param)
            {
                case Identifier id:
                    {
                        Key[] keys = [id.Name];
                        var pooled = !JintFunctionDefinition.EnvironmentEscapeAstVisitor.MayEscape(clause.Body);
                        scope = new KernelScope(BeginScope(keys), keys, constant: false, slots: pooled, catchScope: true);
                        break;
                    }

                case DestructuringPattern pattern:
                    {
                        var keys = new List<Key>();
                        pattern.GetBoundNames(keys);
                        scope = new KernelScope(BeginScope(keys), keys.ToArray(), constant: false, slots: false, catchScope: true);
                        break;
                    }
            }

            try
            {
                handler = CompileStatement(clause.Body, blockLevel: false);
            }
            finally
            {
                if (scope is not null)
                {
                    EndScope();
                }
            }
        }

        var finalizer = statement.Finalizer is null ? null : CompileStatement(statement.Finalizer, blockLevel: false);
        var jumpsOut = JumpsOut(statement.Block) || (statement.Handler is { } catchClause && JumpsOut(catchClause.Body));
        return new KernelTryStatement(statement, block, scope, handler, finalizer, jumpsOut);
    }

    /// <summary>Whether a break or continue in <paramref name="root"/> names a statement around it.</summary>
    private static bool JumpsOut(Node root)
    {
        var pending = new Stack<(Node Node, int Loops, int Breakables, List<string>? Labels)>();
        pending.Push((root, 0, 0, null));
        while (pending.Count > 0)
        {
            var (node, loops, breakables, labels) = pending.Pop();
            switch (node)
            {
                case IFunction or ClassDeclaration or ClassExpression:
                    continue;
                case BreakStatement jump when jump.Label is null ? breakables == 0 : !Declares(labels, jump.Label.Name):
                case ContinueStatement next when next.Label is null ? loops == 0 : !Declares(labels, next.Label.Name):
                    return true;
            }

            var loop = node is ForStatement or WhileStatement or DoWhileStatement or ForInStatement or ForOfStatement ? 1 : 0;
            var breakable = loop == 1 || node is SwitchStatement ? 1 : 0;
            if (node is LabeledStatement labeled)
            {
                labels = labels is null ? [] : [.. labels];
                labels.Add(labeled.Label.Name);
            }

            foreach (var child in node.ChildNodes)
            {
                pending.Push((child, loops + loop, breakables + breakable, labels));
            }
        }

        return false;

        static bool Declares(List<string>? labels, string label) => labels is not null && labels.Exists(name => string.Equals(name, label, StringComparison.Ordinal));
    }

    /// <summary>A labelled statement, its label added to the label set of the loop it labels.</summary>
    private KernelLabeledStatement? TryCompileLabeled(LabeledStatement statement, List<string>? outer = null)
    {
        var labels = outer ?? [];
        labels.Add(statement.Label.Name);
        KernelStatement? body;
        switch (statement.Body)
        {
            case LabeledStatement nested:
                body = Opaque(() => TryCompileLabeled(nested, labels));
                break;
            case ForStatement loop when labels.Count == 1:
                body = CompileLoop(loop, owner: null, labels.ToArray());
                break;
            case ForStatement loop:
                body = Opaque(() => CompileLoop(loop, owner: null, labels.ToArray()));
                break;
            case WhileStatement or DoWhileStatement:
                body = Opaque(() => TryCompileWhile(statement.Body, labels.ToArray()));
                break;
            case ForInStatement or ForOfStatement:
                body = Opaque(() => TryCompileForInOf(statement.Body, labels.ToArray()));
                break;
            case FunctionDeclaration or ClassDeclaration:
                return null;
            default:
                _opaque++;
                try
                {
                    body = CompileStatement(statement.Body, blockLevel: false);
                }
                finally
                {
                    _opaque--;
                }

                break;
        }

        if (body is null)
        {
            return null;
        }

        _compiled++;
        return new KernelLabeledStatement(statement, body);
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

        var interpreted = new KernelInterpreted(expression);
        if (_sitesStartHot && interpreted._handler is JintObjectExpression.JintEmptyObjectExpression emptyObject)
        {
            emptyObject.StartHot();
        }

        return interpreted;
    }

    private KernelExpression CompileTest(Expression expression) => CompileValue(expression);

    private KernelExpression? TryCompileEffect(Expression expression)
    {
        if (_depth >= MaxDepth)
        {
            return null;
        }

        _depth++;
        try
        {
            return TryCompileEffectCore(expression);
        }
        finally
        {
            _depth--;
        }
    }

    private KernelExpression? TryCompileEffectCore(Expression expression)
    {
        switch (expression)
        {
            case AssignmentExpression { Operator: Operator.Assignment } assignment:
                if (assignment.Left is Identifier target)
                {
                    // An anonymous function assigned to a name that is not parenthesized takes the name.
                    var takesName = assignment.Right.IsAnonymousFunctionDefinition() && target.Range.Start == assignment.Range.Start;
                    return CompileSlotAssignment(assignment, target, assignment.Right, isDeclarator: false, name: takesName ? target.Name : null);
                }

                if (assignment.Left is MemberExpression { Computed: false, Optional: false, Object: not Super, Property: Identifier } named
                    && !assignment.Right.IsFunctionDefinition())
                {
                    var owner = TryCompilePure(named.Object);
                    return owner is null ? null : new KernelStoreProperty(assignment, named, owner, CompileValue(assignment.Right));
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
                        // A right-hand side with effects, a call above all, evaluated once after the binding is read.
                        var effect = compound.Right.IsFunctionDefinition() ? null : TryCompileEffect(compound.Right);
                        return effect is null ? null : new KernelCompoundEffect(compound, Name(compoundTarget), op, effect);
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

            case NonLogicalBinaryExpression binary when KernelArithmeticEffect.IsSupported(binary.Operator) && TryCompilePure(binary) is null:
                {
                    // An arithmetic operand that is not pure, a call above all: each operand once, in order.
                    var left = TryCompileEffect(binary.Left);
                    var right = TryCompileEffect(binary.Right);
                    if (left is null && right is null)
                    {
                        return null;
                    }

                    return new KernelArithmeticEffect(binary, left ?? CompileValue(binary.Left), right ?? CompileValue(binary.Right));
                }

            case NonLogicalBinaryExpression { Operator: Operator.InstanceOf } instanceOf:
                return new KernelInstanceOf(instanceOf, CompileValue(instanceOf.Left), CompileValue(instanceOf.Right));

            case ConditionalExpression conditional when TryCompilePure(conditional) is null:
                {
                    var test = TryCompileEffect(conditional.Test);
                    var consequent = TryCompileEffect(conditional.Consequent);
                    var alternate = TryCompileEffect(conditional.Alternate);
                    if (test is null && consequent is null && alternate is null)
                    {
                        return null;
                    }

                    return new KernelConditionalEffect(
                        conditional,
                        test ?? CompileValue(conditional.Test),
                        consequent ?? CompileValue(conditional.Consequent),
                        alternate ?? CompileValue(conditional.Alternate));
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

            case FunctionExpression or ArrowFunctionExpression:
                return new KernelFunction(expression, name: null);

            case NewExpression newExpression:
                return TryCompileNew(newExpression);

            case SequenceExpression sequence:
                {
                    var expressions = new KernelExpression[sequence.Expressions.Count];
                    for (var i = 0; i < expressions.Length; i++)
                    {
                        expressions[i] = CompileValue(sequence.Expressions[i]);
                    }

                    return new KernelSequence(expression, expressions);
                }

            default:
                return null;
        }
    }

    /// <summary>An assignment to a name, or (<paramref name="isDeclarator"/>) a var initializer, whose node is the initializer.</summary>
    private KernelExpression? CompileSlotAssignment(Expression node, Identifier target, Expression value, bool isDeclarator, string? name)
    {
        if (IsEvalOrArguments(target) || value is ClassExpression)
        {
            return null;
        }

        var compiled = value.IsFunctionDefinition() ? CompileFunction(value, name) : CompileValue(value);
        return new KernelAssignSlot(node, Name(target), compiled, isDeclarator);
    }

    private KernelFunction CompileFunction(Expression function, string? name)
    {
        _compiled++;
        return new KernelFunction(function, name);
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

        var count = call.Arguments.Count;
        for (var i = 0; i < count; i++)
        {
            if (call.Arguments[i] is SpreadElement)
            {
                return i == count - 1 && i <= KernelSpreadCall.MaxLeading ? TryCompileSpreadCall(call) : null;
            }
        }

        KernelProperty? member = null;
        KernelExpression? callee = null;
        KernelExpression? computedObject = null;
        KernelExpression? computedKey = null;
        switch (call.Callee)
        {
            case MemberExpression { Computed: true, Optional: false, Object: not Super } computed:
                {
                    computedObject = TryCompilePure(computed.Object);
                    computedKey = computedObject is null ? null : TryCompilePure(computed.Property);
                    if (computedKey is null)
                    {
                        return null;
                    }

                    break;
                }

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

        return new KernelCall(call, member, callee, arguments, computedObject, computedKey);
    }

    /// <summary>A call of a name or a named member whose last argument is a spread.</summary>
    private KernelSpreadCall? TryCompileSpreadCall(CallExpression call)
    {
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
            var argument = call.Arguments[i];
            arguments[i] = CompileValue(argument is SpreadElement spread ? spread.Argument : argument);
        }

        return new KernelSpreadCall(call, member, callee, arguments);
    }

    private KernelNew? TryCompileNew(NewExpression expression)
    {
        foreach (var argument in expression.Arguments)
        {
            if (argument is SpreadElement)
            {
                return null;
            }
        }

        var callee = TryCompilePure(expression.Callee);
        if (callee is null)
        {
            return null;
        }

        var arguments = new KernelExpression[expression.Arguments.Count];
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = CompileValue(expression.Arguments[i]);
        }

        return new KernelNew(expression, callee, arguments);
    }

    /// <summary>A pure expression (see <see cref="KernelExpression"/>), or null.</summary>
    private KernelExpression? TryCompilePure(Expression expression)
    {
        if (_depth >= MaxDepth)
        {
            return null;
        }

        _depth++;
        try
        {
            return TryCompilePureCore(expression);
        }
        finally
        {
            _depth--;
        }
    }

    private KernelExpression? TryCompilePureCore(Expression expression)
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

                    if (IsArguments(identifier))
                    {
                        return new KernelArgumentsValue(identifier, Name(identifier));
                    }

                    return IsEvalOrArguments(identifier) ? null : Name(identifier);
                }

            case ThisExpression:
                return new KernelThis(expression);

            case Literal literal:
                {
                    var value = JintLiteralExpression.ConvertToJsValue(literal);
                    if (value is null || value is JsBigInt)
                    {
                        return null;
                    }

                    // The constant the literal's handler would have published, engine-neutral.
                    literal.UserData ??= value;

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

            case NonUpdateUnaryExpression { Operator: Operator.TypeOf } typeOf:
                {
                    var argument = TryCompilePure(typeOf.Argument);
                    return argument is null ? null : new KernelTypeOf(typeOf, argument);
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

            case MemberExpression { Optional: false, Object: Identifier argumentsObject } member when IsArguments(argumentsObject):
                {
                    if (!member.Computed)
                    {
                        return member.Property is Identifier { Name: "length" } ? new KernelArgumentsRead(member, Name(argumentsObject), null) : null;
                    }

                    var index = TryCompilePure(member.Property);
                    return index is null ? null : new KernelArgumentsRead(member, Name(argumentsObject), index);
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
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name.Key, out var scoped))
            {
                return new KernelName(identifier, scoped, _names[scoped]);
            }
        }

        if (!_nameIndices.TryGetValue(name.Key, out var index))
        {
            index = _names.Count;
            _names.Add(name);
            _scopedNames.Add(false);
            _nameIndices[name.Key] = index;
        }

        return new KernelName(identifier, index, name);
    }

    /// <summary>Opens a scope declaring <paramref name="keys"/>, each a binding of its own; the bindings, in order.</summary>
    private int[] BeginScope(IReadOnlyList<Key> keys)
    {
        var scope = new Dictionary<Key, int>();
        var bindings = new int[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            var index = _names.Count;
            _names.Add(new Environment.BindingName(keys[i].Name));
            _scopedNames.Add(true);
            scope[keys[i]] = index;
            bindings[i] = index;
        }

        _anyScoped = true;
        _scopes.Add(scope);
        return bindings;
    }

    private void EndScope() => _scopes.RemoveAt(_scopes.Count - 1);

    private static bool IsEvalOrArguments(Identifier identifier)
        => identifier.Name is "eval" or "arguments";

    private static bool IsArguments(Identifier identifier) => string.Equals(identifier.Name, "arguments", StringComparison.Ordinal);
}
