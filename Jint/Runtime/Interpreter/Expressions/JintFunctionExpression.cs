using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Environments;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Expressions;

internal sealed class JintFunctionExpression : JintExpression
{
    private readonly JintFunctionDefinition _function;

    public JintFunctionExpression(FunctionExpression function) : base(function)
    {
        _function = new JintFunctionDefinition(function);
        CanDefer = function is { Id: null, Async: false, Generator: false };
    }

    /// <summary>
    /// Whether the function can be created later than its expression runs, by <see cref="InstantiateDeferred"/>:
    /// an anonymous ordinary function, whose creation needs nothing but the environment and the origin.
    /// </summary>
    internal bool CanDefer { get; }

    internal JintFunctionDefinition Definition => _function;

    /// <summary>
    /// The function an anonymous ordinary function expression would have created when it ran with
    /// <paramref name="env"/> as its lexical environment, no private environment, and <paramref name="origin"/>'s
    /// realm and script or module active. The creation of a deferred closure (see
    /// <see cref="JsObject.TryAddDeferredClosure"/>).
    /// </summary>
    internal static ScriptFunction InstantiateDeferred(Engine engine, JintFunctionDefinition function, Environment env, FunctionOrigin origin)
    {
        var thisMode = function.Strict
            ? FunctionThisMode.Strict
            : FunctionThisMode.Global;

        var closure = new ScriptFunction(engine, function, env, thisMode, origin.Realm.Intrinsics.Function.PrototypeObject);
        closure._functionOrigin = origin;
        closure.SetFunctionName(JsString.Empty);
        closure.MakeConstructor();
        return closure;
    }

    protected override object EvaluateInternal(EvaluationContext context)
    {
        return Build(context.Engine, _function);
    }

    public override JsValue GetValue(EvaluationContext context)
    {
        return Build(context.Engine, _function);
    }

    private static ScriptFunction Build(Engine engine, JintFunctionDefinition function)
    {
        ScriptFunction closure;
        var functionName = function.Name ?? "";
        if (!function.Function.Generator)
        {
            closure = function.Function.Async
                ? InstantiateAsyncFunctionExpression(engine, function, functionName)
                : InstantiateOrdinaryFunctionExpression(engine, function, functionName);
        }
        else
        {
            closure = function.Function.Async
                ? InstantiateAsyncGeneratorFunctionExpression(engine, function, functionName)
                : InstantiateGeneratorFunctionExpression(engine, function, functionName);
        }

        return closure;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-runtime-semantics-instantiateordinaryfunctionexpression
    /// </summary>
    private static ScriptFunction InstantiateOrdinaryFunctionExpression(Engine engine, JintFunctionDefinition function, string? name = "")
    {
        var runningExecutionContext = engine.ExecutionContext;
        var env = runningExecutionContext.LexicalEnvironment;
        var privateEnv = runningExecutionContext.PrivateEnvironment;

        DeclarativeEnvironment? funcEnv = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            funcEnv = JintEnvironment.NewDeclarativeEnvironment(engine, engine.ExecutionContext.LexicalEnvironment);
            funcEnv.CreateImmutableBinding(name!, strict: false);
        }

        var thisMode = function.Strict
            ? FunctionThisMode.Strict
            : FunctionThisMode.Global;

        var intrinsics = engine.Realm.Intrinsics;
        var closure = intrinsics.Function.OrdinaryFunctionCreate(
            intrinsics.Function.PrototypeObject,
            function,
            thisMode,
            funcEnv ?? env,
            privateEnv
        );

        if (name is not null)
        {
            closure.SetFunctionName(name);
        }
        closure.MakeConstructor();

        funcEnv?.InitializeBinding(name!, closure, DisposeHint.Normal);

        return closure;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-runtime-semantics-instantiateasyncfunctionexpression
    /// </summary>
    private static ScriptFunction InstantiateAsyncFunctionExpression(Engine engine, JintFunctionDefinition function, string? name = "")
    {
        var runningExecutionContext = engine.ExecutionContext;
        var env = runningExecutionContext.LexicalEnvironment;
        var privateEnv = runningExecutionContext.PrivateEnvironment;

        DeclarativeEnvironment? funcEnv = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            funcEnv = JintEnvironment.NewDeclarativeEnvironment(engine, engine.ExecutionContext.LexicalEnvironment);
            funcEnv.CreateImmutableBinding(name!, strict: false);
        }

        var thisMode = function.Strict
            ? FunctionThisMode.Strict
            : FunctionThisMode.Global;

        var intrinsics = engine.Realm.Intrinsics;
        var closure = intrinsics.Function.OrdinaryFunctionCreate(
            intrinsics.AsyncFunction.PrototypeObject,
            function,
            thisMode,
            funcEnv ?? env,
            privateEnv
        );

        closure.SetFunctionName(name ?? "");

        funcEnv?.InitializeBinding(name!, closure, DisposeHint.Normal);

        return closure;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-runtime-semantics-instantiategeneratorfunctionexpression
    /// </summary>
    private static ScriptFunction InstantiateGeneratorFunctionExpression(Engine engine, JintFunctionDefinition function, string? name)
    {
        var runningExecutionContext = engine.ExecutionContext;
        var env = runningExecutionContext.LexicalEnvironment;
        var privateEnv = runningExecutionContext.PrivateEnvironment;

        DeclarativeEnvironment? funcEnv = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            funcEnv = JintEnvironment.NewDeclarativeEnvironment(engine, engine.ExecutionContext.LexicalEnvironment);
            funcEnv.CreateImmutableBinding(name!, strict: false);
        }

        var thisMode = function.Strict || engine._isStrict
            ? FunctionThisMode.Strict
            : FunctionThisMode.Global;

        var intrinsics = engine.Realm.Intrinsics;
        var closure = intrinsics.Function.OrdinaryFunctionCreate(
            intrinsics.GeneratorFunction.PrototypeObject,
            function,
            thisMode,
            funcEnv ?? env,
            privateEnv
        );

        if (name is not null)
        {
            closure.SetFunctionName(name);
        }

        var prototype = ObjectInstance.OrdinaryObjectCreate(engine, intrinsics.GeneratorFunction.PrototypeObject.PrototypeObject);
        closure.DefinePropertyOrThrow(CommonProperties.Prototype, new PropertyDescriptor(prototype, PropertyFlag.Writable));

        funcEnv?.InitializeBinding(name!, closure, DisposeHint.Normal);

        return closure;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-runtime-semantics-instantiateasyncgeneratorfunctionexpression
    /// </summary>
    private static ScriptFunction InstantiateAsyncGeneratorFunctionExpression(Engine engine, JintFunctionDefinition function, string? name)
    {
        var runningExecutionContext = engine.ExecutionContext;
        var env = runningExecutionContext.LexicalEnvironment;
        var privateEnv = runningExecutionContext.PrivateEnvironment;

        DeclarativeEnvironment? funcEnv = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            funcEnv = JintEnvironment.NewDeclarativeEnvironment(engine, engine.ExecutionContext.LexicalEnvironment);
            funcEnv.CreateImmutableBinding(name!, strict: false);
        }

        var thisMode = function.Strict || engine._isStrict
            ? FunctionThisMode.Strict
            : FunctionThisMode.Global;

        var intrinsics = engine.Realm.Intrinsics;
        var closure = intrinsics.Function.OrdinaryFunctionCreate(
            intrinsics.AsyncGeneratorFunction.PrototypeObject,
            function,
            thisMode,
            funcEnv ?? env,
            privateEnv
        );

        if (name is not null)
        {
            closure.SetFunctionName(name);
        }

        var prototype = ObjectInstance.OrdinaryObjectCreate(engine, intrinsics.AsyncGeneratorFunction.PrototypeObject.PrototypeObject);
        closure.DefinePropertyOrThrow(CommonProperties.Prototype, new PropertyDescriptor(prototype, PropertyFlag.Writable));

        funcEnv?.InitializeBinding(name!, closure, DisposeHint.Normal);

        return closure;
    }
}
