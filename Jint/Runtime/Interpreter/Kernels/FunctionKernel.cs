using Jint.Native;
using Jint.Runtime.Environments;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// A function body run on kernel nodes in place of its statement list: as a tree, then as .NET code once the tree
/// has run it often enough, where the engine compiles bodies.
/// </summary>
internal sealed class FunctionKernel : KernelProgram
{
    /// <summary>How many calls of a function run its body on the interpreter before it runs on a kernel.</summary>
    internal const int DefaultThreshold = 16;

    /// <summary>How many function bodies one engine runs on kernels at most.</summary>
    internal const int MaxCompiledFunctions = 4096;

    /// <summary>How many calls of a body run on its tree before the body is compiled to .NET code.</summary>
    internal const int DefaultCodeThreshold = 1000;

    /// <summary>How many function bodies one engine compiles to .NET code at most.</summary>
    internal const int MaxCompiledBodies = 1024;

    /// <summary>How many kernel nodes a body run on a kernel has at most.</summary>
    private const int MaxSize = 2000;

    /// <summary>How many kernel nodes a body compiled to .NET code has at most: the JIT's time grows with the code.</summary>
    private const int MaxCodeSize = 500;

    private readonly KernelBlockStatement _body;
    private readonly int _loops;
    private readonly Key[]? _layout;
    private readonly int[] _localSlots;
    private readonly int _size;
    private readonly bool _createsClosures;

    // The frames of calls made straight into the body, by call depth, and the depth.
    private KernelFrame?[] _callFrames = new KernelFrame?[4];
    private int _callDepth;
    private const int MaxCallFrames = 32;

    // The body's code, the calls and statements the tree ran before it, and whether compiling failed.
    internal KernelCode? _code;
    private int _calls;
    private long _work;
    private bool _noCode;

    private FunctionKernel(KernelBlockStatement body, int loops, Key[]? layout, int[] localSlots, Environments.Environment.BindingName[] names, int size, bool createsClosures) : base(names)
    {
        _createsClosures = createsClosures;
        _body = body;
        _loops = loops;
        _layout = layout;
        _localSlots = localSlots;
        _size = size;
    }

    /// <summary>
    /// The kernel of a body that runs on one, or null where the body stays on the interpreter, as a single <c>return</c>
    /// without a call or property read does: the interpreter runs one about as fast as a kernel is entered.
    /// </summary>
    internal static FunctionKernel? TryCreate(Engine engine, JintFunctionDefinition definition, JintFunctionDefinition.State state)
    {
        if (definition.Function.Body is not FunctionBody body
            || definition.IsAsync
            || definition.IsGenerator
            || body.Body is [ReturnStatement { Argument: var argument }] && (argument is null || !HasCallOrMember(argument))
            || !CanCompile(body, out var createsClosures))
        {
            return null;
        }

        var compiler = new KernelCompiler();
        var root = compiler.CompileFunctionBody(body, definition.Strict || engine._isStrict);
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

        return new FunctionKernel(root, loops.Count, layout, localSlots, names, compiler.Size, createsClosures);
    }

    /// <summary>The body's code, compiled now once the tree has run the body often enough for it to pay; null otherwise.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private KernelCode? CodeWhenWorthIt(Engine engine)
    {
        var threshold = engine._functionKernelCodeThreshold;
        if (threshold < 0 || _noCode)
        {
            return null;
        }

        var work = engine._loopKernelCodeWork;
        if (work >= 0 ? _work < work : _calls < threshold)
        {
            return null;
        }

        if (engine._functionKernelCodeBudget <= 0 || _size > MaxCodeSize)
        {
            _noCode = true;
            return null;
        }

        _noCode = true;
        var code = KernelCode.TryCompileBody(_body, _layout, _localSlots);
        if (code is null)
        {
            return null;
        }

        engine._functionKernelCodeBudget--;
        if (!engine._functionKernelCodeInBackground)
        {
            _code = code;
            return code;
        }

        // The JIT compiles the code on a pool thread, called there without a frame; the tree runs until it is done.
        System.Threading.ThreadPool.UnsafeQueueUserWorkItem(static state => state.Kernel.Publish(state.Code), (Kernel: this, Code: code), preferLocal: false);
        return null;
    }

    private void Publish(KernelCode code)
    {
        try
        {
            code.Body!(null!, out _);
        }
        catch (Exception)
        {
            // Code the JIT refuses stays unpublished: the tree goes on running the body.
            return;
        }

        System.Threading.Volatile.Write(ref _code, code);
    }

    /// <summary>
    /// Whether a kernel runs the body: no function declaration, class, loop (it has a loop kernel), <c>try</c>, <c>with</c>,
    /// <c>using</c>, <c>yield</c> or <c>await</c>, and no direct <c>eval</c>, <c>arguments</c> or <c>super</c> in it or its arrows.
    /// </summary>
    private static bool CanCompile(FunctionBody body, out bool createsClosures)
    {
        createsClosures = false;
        var pending = new Stack<(Node Node, bool InArrow)>();
        pending.Push((body, false));
        while (pending.Count > 0)
        {
            var (parent, inArrow) = pending.Pop();
            foreach (var node in parent.ChildNodes)
            {
                switch (node)
                {
                    case FunctionExpression:
                        // Its body is its own; the kernel only makes the closure.
                        createsClosures = true;
                        continue;
                    case ArrowFunctionExpression:
                        // Its body reads the arguments, super and new.target of this one.
                        createsClosures = true;
                        pending.Push((node, true));
                        continue;
                    case Super or MetaProperty:
                    case Identifier { Name: "arguments" }:
                    case CallExpression { Callee: Identifier { Name: "eval" } }:
                        return false;
                }

                if (!inArrow)
                {
                    switch (node)
                    {
                        case IFunction:
                        case ClassDeclaration or ClassExpression:
                        case TryStatement or WithStatement:
                        case ForStatement or WhileStatement or DoWhileStatement or ForInStatement or ForOfStatement:
                        case YieldExpression or AwaitExpression:
                        case VariableDeclaration { Kind: VariableDeclarationKind.Using or VariableDeclarationKind.AwaitUsing }:
                            return false;
                    }
                }

                pending.Push((node, inArrow));
            }
        }

        return true;
    }

    /// <summary>Whether calls run in the frames the kernel keeps: not where the body makes a closure.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool RunsInFrames(JintFunctionDefinition.State state) => !state.EnvironmentMayEscape && !_createsClosures;

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

    private static bool HasCallOrMember(Node node)
    {
        if (node is CallExpression or MemberExpression or ThisExpression)
        {
            return true;
        }

        foreach (var child in node.ChildNodes)
        {
            if (HasCallOrMember(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a statement of the body other than a <c>throw</c> or a function stored to a property is run by the
    /// interpreter as a whole, which code would only enter and leave again: such a body stays on the interpreter.
    /// </summary>
    private static bool LeavesAStatementToTheInterpreter(KernelStatement statement) => statement switch
    {
        KernelInterpretedStatement { _statement: not ThrowStatement } => true,
        KernelExpressionStatement { _expression: KernelInterpreted { _expression: not AssignmentExpression { Left: MemberExpression, Right: FunctionExpression or ArrowFunctionExpression } } } => true,
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

        try
        {
            var code = _code ?? CodeWhenWorthIt(engine);
            if (code is not null)
            {
                engine._functionKernelCodeRuns++;
                return RunCode(code, frame, out number);
            }

            return RunTree(frame, out number);
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
    }

    /// <summary>
    /// The frame of a call of <paramref name="function"/> made straight into the body, at the kernel's call depth: its
    /// environment, which the frame owns, bound to the function, slots not filled yet; <see cref="BeginCall"/> enters it.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal KernelFrame EnterCallFrame(EvaluationContext context, Native.Function.ScriptFunction function, JintFunctionDefinition.State state)
    {
        if (function.TakeKernelEnvironment() is { } kept && EnterKeptFrame(context, function, state, kept) is { } own)
        {
            return own;
        }

        var depth = _callDepth;
        var frames = _callFrames;
        var frame = (uint) depth < (uint) frames.Length ? frames[depth] : null;
        if (frame is null)
        {
            frame = NewCallFrame(context, function, state, depth);
        }

        var environment = frame.CallEnvironment!;
        if (!ReferenceEquals(environment._functionObject, function))
        {
            environment._functionObject = function;
        }

        if (!ReferenceEquals(frame.Outer, function._environment))
        {
            Rebind(frame, function._environment);
        }

        frame.Engine._functionKernelRuns++;
        return frame;
    }

    /// <summary>The frame a closure instance kept from its last call, or null where it cannot serve.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private KernelFrame? EnterKeptFrame(EvaluationContext context, Native.Function.ScriptFunction function, JintFunctionDefinition.State state, FunctionEnvironment environment)
    {
        var frame = environment.KernelFrame!;
        if (frame.InUse
            || !ReferenceEquals(frame.Names, Names)
            || !ReferenceEquals(frame.Context, context)
            || !ReferenceEquals(frame.CallEnvironment, environment)
            || (state.UseFixedSlots && environment._slots is null))
        {
            return null;
        }

        frame.InUse = true;
        if (!ReferenceEquals(environment._functionObject, function))
        {
            environment._functionObject = function;
        }

        // A call the interpreter made in the environment left its this bound.
        environment.ReleaseThis();

        if (!ReferenceEquals(frame.Outer, function._environment))
        {
            Rebind(frame, function._environment);
        }

        frame.Engine._functionKernelRuns++;
        return frame;
    }

    /// <summary>Enters the frame <see cref="EnterCallFrame"/> gave, once its slots are filled.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void BeginCall(KernelFrame frame)
    {
        if (!frame.OwnedByInstance)
        {
            _callDepth++;
        }
    }

    /// <summary>Ends a call <see cref="BeginCall"/> entered: slots cleared, a closure's frame kept or let go of.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void LeaveCallFrame(KernelFrame frame, JintFunctionDefinition.State state, Native.Function.ScriptFunction function)
    {
        if (!frame.OwnedByInstance)
        {
            _callDepth--;
        }

        var environment = frame.CallEnvironment!;
        if (environment._slots is { } slots)
        {
            if (slots.Length <= 4)
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    slots[i] = default;
                }
            }
            else
            {
                System.Array.Clear(slots);
            }
        }

        if (!state.CanSkipThisBinding)
        {
            environment.ReleaseThis();
        }

        if (frame.Abrupt.Value is not null)
        {
            frame.Abrupt = default;
        }

        if (frame.ReturnValue is not null)
        {
            frame.ReturnValue = null;
        }

        frame.Work = 0;
        if (frame.OwnedByInstance)
        {
            frame.InUse = false;
            function.KeepKernelEnvironment(environment);
        }
        else if (frame.Outer is not GlobalEnvironment)
        {
            KeepOrRelease(frame, function);
        }
    }

    /// <summary>
    /// A closure's frame handed to the instance for its next calls, from its second call on; otherwise its function and
    /// outer environment let go of, so that the kernel keeps neither alive.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void KeepOrRelease(KernelFrame frame, Native.Function.ScriptFunction function)
    {
        var environment = frame.CallEnvironment!;
        var depth = _callDepth;
        if ((uint) depth < (uint) _callFrames.Length && ReferenceEquals(_callFrames[depth], frame) && function.KeepKernelEnvironment(environment))
        {
            _callFrames[depth] = null;
            frame.OwnedByInstance = true;
            frame.InUse = false;
            environment.KernelFrame = frame;
            return;
        }

        Release(frame);
    }

    /// <summary>A call of <paramref name="function"/> straight into the body's code, its arguments stored to the slots of a frame of its own.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal KernelResult CallCode(Engine engine, Native.Function.ScriptFunction function, Expressions.JintExpression site, JsValue thisValue, JintFunctionDefinition.State state, KernelCode code, JsValue? v0, double n0, JsValue? v1, double n1, JsValue? v2, double n2, JsValue? v3, double n3, int count)
    {
        var frame = EnterCallFrame(engine._evaluationContext, function, state);
        var environment = frame.CallEnvironment!;
        if (!state.CanSkipThisBinding)
        {
            function.OrdinaryCallBindThis(environment, thisValue);
        }

        var slots = environment._slots;
        var parameters = state.ParameterNames.Length;
        if (slots is not null && state.RestParameterSlot < 0 && parameters <= 4)
        {
            if (parameters > 0)
            {
                StoreParameter(ref slots[0], count > 0, v0, n0);
                if (parameters > 1)
                {
                    StoreParameter(ref slots[1], count > 1, v1, n1);
                    if (parameters > 2)
                    {
                        StoreParameter(ref slots[2], count > 2, v2, n2);
                        if (parameters > 3)
                        {
                            StoreParameter(ref slots[3], count > 3, v3, n3);
                        }
                    }
                }
            }

            var template = state.NonParameterSlotTemplate!;
            var first = state.ParameterSlotCount;
            for (var i = 0; i < template.Length; i++)
            {
                slots[first + i] = template[i];
            }
        }
        else
        {
            Instantiate(environment, state, v0, n0, v1, n1, v2, n2, v3, n3, count);
        }

        var positions = engine.MarkLastSyntaxPositions();
        var source = engine._lastSyntaxLocation.SourceFile;
        BeginCall(frame);
        engine.PushCallFrame(environment, site);
        engine._functionKernelCodeRuns++;
        JsValue? value;
        double number;
        try
        {
            value = RunCode(code, frame, out number);
        }
        finally
        {
            engine.PopCallFrame();
            LeaveCallFrame(frame, state, function);
        }

        engine.RestoreLastSyntaxElement(positions, source);
        return new KernelResult(value, number);
    }

    /// <summary>A parameter's binding, as <see cref="NumberArguments"/> stores it: the value, the Number unboxed, or undefined.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void StoreParameter(ref Binding slot, bool present, JsValue? value, double number)
    {
        if (!present)
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Instantiate(FunctionEnvironment environment, JintFunctionDefinition.State state, JsValue? v0, double n0, JsValue? v1, double n1, JsValue? v2, double n2, JsValue? v3, double n3, int count)
        => Engine.FunctionDeclarationInstantiationInto(environment, state, new NumberArguments(v0, n0, v1, n1, v2, n2, v3, n3, count));

    /// <summary>The value of a body whose code left its completion in the frame.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static JsValue? CompleteFromFrame(KernelFrame frame, out double number)
    {
        if (frame.Abrupt.Type != CompletionType.Normal)
        {
            return CompleteAbruptly(frame, out number);
        }

        number = frame.ReturnNumber;
        return frame.ReturnValue;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private KernelFrame NewCallFrame(EvaluationContext context, Native.Function.ScriptFunction function, JintFunctionDefinition.State state, int depth)
    {
        var environment = new FunctionEnvironment(context.Engine, function, JsValue.Undefined);
        environment._outerEnv = function._environment;
        if (state.UseFixedSlots)
        {
            environment._slotNames = state.SlotNames;
            environment._slots = new Binding[state.SlotNames!.Length];
        }

        var frame = NewFrame(context, environment);
        frame.CallEnvironment = environment;
        frame.CodeThreshold = -1;
        if (depth < MaxCallFrames)
        {
            if (depth >= _callFrames.Length)
            {
                System.Array.Resize(ref _callFrames, System.Math.Min(MaxCallFrames, _callFrames.Length * 2));
            }

            _callFrames[depth] = frame;
        }

        return frame;
    }

    /// <summary>The frame bound to another outer environment: its names resolved again.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Rebind(KernelFrame frame, Environments.Environment? outer)
    {
        frame.CallEnvironment!._outerEnv = outer;
        frame.Forget();
        frame.Outer = outer;
        frame.ResolveLayout();
    }

    /// <summary>A closure's function and outer environment let go of, so that the kernel keeps neither alive.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Release(KernelFrame frame)
    {
        var environment = frame.CallEnvironment!;
        environment._functionObject = null!;
        environment._outerEnv = null;
        frame.Forget();
    }

    /// <summary>The body run in <paramref name="frame"/>, where completion values are not observable: <see cref="RunUnboxed"/> without entering.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal JsValue? RunBody(KernelFrame frame, out double number)
    {
        var code = _code ?? CodeWhenWorthIt(frame.Engine);
        if (code is not null)
        {
            frame.Engine._functionKernelCodeRuns++;
            return RunCode(code, frame, out number);
        }

        return RunTree(frame, out number);
    }

    /// <summary>The body run by its code, or by the tree where the code does not run it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private JsValue? RunCode(KernelCode code, KernelFrame frame, out double number)
    {
        var value = code.Body!(frame, out number);
        if (ReferenceEquals(value, KernelExpression.Bail))
        {
            return CompleteFromFrame(frame, out number);
        }

        return ReferenceEquals(value, KernelCode.NotRun) ? RunTree(frame, out number) : value;
    }

    /// <summary>The body run by the tree.</summary>
    private JsValue? RunTree(KernelFrame frame, out double number)
    {
        _calls++;
        var result = _body.Execute(frame);
        _work += frame.Work;
        if (result == KernelStatement.Normal)
        {
            number = 0;
            return JsValue.Undefined;
        }

        // A return statement of the body left its value in the frame.
        return CompleteFromFrame(frame, out number);
    }

    /// <summary>The value of a body that completed with a <c>return</c> it did not leave in the frame, or its throw.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static JsValue CompleteAbruptly(KernelFrame frame, out double number)
    {
        number = 0;
        var completion = frame.Abrupt;
        if (completion.Type == CompletionType.Throw)
        {
            Throw.JavaScriptException(frame.Engine, completion.Value, in completion);
        }

        return completion.Type == CompletionType.Return ? completion.Value : JsValue.Undefined;
    }
}
