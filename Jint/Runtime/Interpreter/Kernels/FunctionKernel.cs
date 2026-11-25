using Jint.Native;
using Jint.Runtime.Environments;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// A function body run on kernel nodes in place of its statement list, in the call's environment: as a tree, then as
/// .NET code once the tree has done enough work, where the engine compiles bodies.
/// </summary>
internal sealed class FunctionKernel : KernelProgram
{
    /// <summary>How many calls of a function run its body on the interpreter before it runs on a kernel.</summary>
    internal const int DefaultThreshold = 16;

    /// <summary>How many function bodies one engine runs on kernels at most.</summary>
    internal const int MaxCompiledFunctions = 4096;

    /// <summary>How many kernel nodes a body run on a kernel has at most.</summary>
    private const int MaxSize = 2000;

    private readonly KernelBlockStatement _body;
    private readonly int _loops;
    private readonly Key[]? _layout;
    private readonly int[] _localSlots;
    private readonly int _size;

    // The body's code, the calls and statements the tree ran before it, and whether compiling failed.
    internal KernelCode? _code;
    private int _calls;
    private long _work;
    private bool _noCode;

    private FunctionKernel(KernelBlockStatement body, int loops, Key[]? layout, int[] localSlots, Environments.Environment.BindingName[] names, int size) : base(names)
    {
        _body = body;
        _loops = loops;
        _layout = layout;
        _localSlots = localSlots;
        _size = size;
    }

    /// <summary>
    /// The kernel of a body that runs on one, or null where the body stays on the interpreter, as a single
    /// <c>return</c> without a call does: the interpreter runs one about as fast as a kernel is entered.
    /// </summary>
    internal static FunctionKernel? TryCreate(JintFunctionDefinition definition, JintFunctionDefinition.State state)
    {
        if (definition.Function.Body is not FunctionBody body
            || definition.IsAsync
            || definition.IsGenerator
            || body.Body is [ReturnStatement { Argument: var argument }] && (argument is null || !HasCall(argument))
            || !CanCompile(body))
        {
            return null;
        }

        var compiler = new KernelCompiler();
        var root = compiler.CompileFunctionBody(body);
        if (!compiler.CompiledAnything || compiler.Size > MaxSize || LeavesAStatementToTheInterpreter(root))
        {
            return null;
        }

        var loops = compiler.Loops;
        for (var i = 0; i < loops.Count; i++)
        {
            loops[i]._entry = i;
        }

        MarkDirectReturns(root);
        var names = compiler.Names;
        var layout = state.UseFixedSlots ? state.SlotNames : null;
        var localSlots = new int[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            localSlots[i] = layout is null ? -1 : KernelFrame.IndexOf(layout, names[i].Key);
        }

        return new FunctionKernel(root, loops.Count, layout, localSlots, names, compiler.Size);
    }

    /// <summary>The body's code, compiled now if the tree has done enough work for it to pay; null otherwise.</summary>
    private KernelCode? CodeWhenWorthIt(Engine engine)
    {
        var threshold = engine._functionKernelCodeThreshold;
        if (threshold < 0 || _noCode || engine._loopKernelCodeThreshold < 0)
        {
            return null;
        }

        var work = engine._loopKernelCodeWork;
        if (_work < (work >= 0 ? work : (long) threshold * _size))
        {
            return null;
        }

        _code = KernelCode.TryCompile(_body, _loops, _calls, _layout, _localSlots);
        _noCode = _code is null;
        return _code;
    }

    /// <summary>
    /// Whether a kernel runs the body: no closure, loop (it has a loop kernel), <c>try</c>, <c>with</c>, <c>using</c>,
    /// direct <c>eval</c>, <c>arguments</c>, <c>super</c>, <c>yield</c> or <c>await</c>.
    /// </summary>
    private static bool CanCompile(FunctionBody body)
    {
        var pending = new Stack<Node>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            foreach (var node in pending.Pop().ChildNodes)
            {
                switch (node)
                {
                    case IFunction:
                    case ClassDeclaration or ClassExpression:
                    case TryStatement or WithStatement:
                    case ForStatement or WhileStatement or DoWhileStatement or ForInStatement or ForOfStatement:
                    case YieldExpression or AwaitExpression or Super:
                    case VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing }:
                    case Identifier { Name: "arguments" }:
                    case CallExpression { Callee: Identifier { Name: "eval" } }:
                        return false;
                }

                pending.Push(node);
            }
        }

        return true;
    }

    /// <summary>Marks the return statements that complete the body, outside any loop, to leave their value in the frame.</summary>
    private static void MarkDirectReturns(KernelStatement statement)
    {
        switch (statement)
        {
            case KernelReturnStatement returnStatement:
                returnStatement._direct = true;
                break;
            case KernelBlockStatement block:
                foreach (var nested in block._statements)
                {
                    MarkDirectReturns(nested);
                }

                break;
            case KernelIfStatement ifStatement:
                MarkDirectReturns(ifStatement._consequent);
                if (ifStatement._alternate is { } alternate)
                {
                    MarkDirectReturns(alternate);
                }

                break;
        }
    }

    private static bool HasCall(Node node)
    {
        if (node is CallExpression)
        {
            return true;
        }

        foreach (var child in node.ChildNodes)
        {
            if (HasCall(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a statement of the body other than a <c>throw</c> is run by the interpreter as a whole, which code would
    /// only enter and leave again: such a body stays on the interpreter.
    /// </summary>
    private static bool LeavesAStatementToTheInterpreter(KernelStatement statement) => statement switch
    {
        KernelInterpretedStatement { _statement: not ThrowStatement } => true,
        KernelExpressionStatement { _expression: KernelInterpreted } => true,
        KernelReturnStatement { _argument: KernelInterpreted } => true,
        KernelBlockStatement block => Array.Exists(block._statements, LeavesAStatementToTheInterpreter),
        KernelIfStatement ifStatement => LeavesAStatementToTheInterpreter(ifStatement._consequent)
                                         || (ifStatement._alternate is { } alternate && LeavesAStatementToTheInterpreter(alternate)),
        KernelForStatement loop => LeavesAStatementToTheInterpreter(loop._body)
                                   || (loop._initStatement is { } init && LeavesAStatementToTheInterpreter(init)),
        _ => false,
    };

    /// <summary>
    /// The body run in the current execution context: the value it returns, or undefined; <paramref name="environment"/>
    /// is the call's own environment, whose frame keeps its names, or null.
    /// </summary>
    internal JsValue Run(EvaluationContext context, FunctionEnvironment? environment)
        => KernelExpression.Box(RunUnboxed(context, environment, out var number), number);

    /// <summary><see cref="Run"/> with a Number the body returns left unboxed: null, and the Number in <paramref name="number"/>.</summary>
    internal JsValue? RunUnboxed(EvaluationContext context, FunctionEnvironment? environment, out double number)
    {
        var frame = environment is not null ? EnterKept(context, environment) : Enter(context);
        var engine = frame.Engine;
        frame.CodeThreshold = -1;
        engine._functionKernelRuns++;

        var observable = context.CompletionValuesObservable;
        if (observable)
        {
            context.CompletionValuesObservable = false;
        }

        Completion completion;
        try
        {
            var code = _code ?? CodeWhenWorthIt(engine);
            if (code is not null)
            {
                engine._functionKernelCodeRuns++;
                if (code.RunFunction(frame) is { } value)
                {
                    number = 0;
                    return value;
                }
            }
            else
            {
                _calls++;
                var result = _body.Execute(frame);
                _work += frame.Work;
                if (result == KernelStatement.Normal)
                {
                    number = 0;
                    return JsValue.Undefined;
                }
            }

            completion = frame.Abrupt;
            if (completion.Type == CompletionType.Normal)
            {
                // A return statement of the body left its value in the frame.
                number = frame.ReturnNumber;
                return frame.ReturnValue;
            }
        }
        finally
        {
            if (observable)
            {
                context.CompletionValuesObservable = true;
            }

            if (environment is not null)
            {
                LeaveKept(frame);
            }
            else
            {
                Leave(frame);
            }
        }

        number = 0;
        if (completion.Type == CompletionType.Throw)
        {
            Throw.JavaScriptException(engine, completion.Value, in completion);
        }

        return completion.Type == CompletionType.Return ? completion.Value : JsValue.Undefined;
    }
}
