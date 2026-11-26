using System.Runtime.CompilerServices;
using System.Threading;
using Jint.Native.Object;
using Jint.Native.Promise;
using Jint.Pooling;
using Jint.Runtime;
using Jint.Runtime.CallStack;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter;
using Jint.Runtime.Interpreter.Expressions;
using Jint.Runtime.Interpreter.Kernels;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Native.Function;

public sealed class ScriptFunction : Function, IConstructor
{
    // Function._scriptState: four flags, then two bits each for the states of the own "arguments" and "caller".
    private const int FlagClassConstructor = 1;
    private const int FlagStrict = 2;
    private const int FlagReturnedEnvironment = 4;
    private const int FlagCtorShaped = 8;
    private const int ArgumentsStateShift = 4;
    private const int CallerStateShift = 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasFlag(int flag) => (_scriptState & flag) != 0;

    private void SetFlag(int flag, bool value)
    {
        _scriptState = (byte) (value ? _scriptState | flag : _scriptState & ~flag);
    }

    private byte _argumentsState
    {
        get => (byte) ((_scriptState >> ArgumentsStateShift) & 3);
        set => _scriptState = (byte) ((_scriptState & ~(3 << ArgumentsStateShift)) | (value << ArgumentsStateShift));
    }

    private byte _callerState
    {
        get => (byte) ((_scriptState >> CallerStateShift) & 3);
        set => _scriptState = (byte) ((_scriptState & ~(3 << CallerStateShift)) | (value << CallerStateShift));
    }

    internal bool _isClassConstructor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => HasFlag(FlagClassConstructor);
        set => SetFlag(FlagClassConstructor, value);
    }

    internal JsValue? _classFieldInitializerName
    {
        get => _rare?.ClassFieldInitializerName;
        set
        {
            if (_rare is not null || value is not null)
            {
                Rare.ClassFieldInitializerName = value;
            }
        }
    }

    // Whether this function's code is strict, resolved once at creation. Normally that is a static
    // property of the AST: the parser marks the FunctionBody, propagating an enclosing "use strict"
    // into every nested function. An arrow function with a *concise* (expression) body has no
    // FunctionBody node to carry the mark, yet is still strict when it appears in strict code
    // (https://tc39.es/ecma262/#sec-strict-mode-code), so JintArrowFunctionExpression ORs in the
    // strictness of the running context it was created from.
    internal bool _strict
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => HasFlag(FlagStrict);
        set => SetFlag(FlagStrict, value);
    }

    // Own restricted "arguments"/"caller" properties of non-strict, non-arrow, non-generator,
    // non-async functions. Dedicated fields (like Function's name/length/prototype) instead of
    // dictionary entries, so a plain sloppy function's instantiation allocates neither the
    // property dictionary nor the two descriptors: the fields start at the pending sentinel and
    // materialize on first read. null means absent (strict functions, methods after MakeMethod,
    // or deleted); a deleted-then-redefined property goes to the dictionary, which preserves the
    // previous key order after resurrection. Enumeration order (length, name, prototype,
    // arguments, caller) matches the old ctor-time dictionary inserts.
    //
    // Each is a state of two bits in Function._scriptState: absent, pending (the sentinel) or materialized, the
    // descriptor then being held in the rare data, so that a sloppy closure does not carry two descriptor
    // references nothing reads.

    private const byte RestrictedAbsent = 0;
    private const byte RestrictedPending = 1;
    private const byte RestrictedMaterialized = 2;

    internal PropertyDescriptor? _argumentsDescriptor
    {
        get => _argumentsState switch
        {
            RestrictedAbsent => null,
            RestrictedPending => _pendingDescriptor,
            _ => _rare!.ArgumentsDescriptor,
        };
        set
        {
            if (value is null || ReferenceEquals(value, _pendingDescriptor))
            {
                if (_rare is not null)
                {
                    _rare.ArgumentsDescriptor = null;
                }

                _argumentsState = value is null ? RestrictedAbsent : RestrictedPending;
            }
            else
            {
                Rare.ArgumentsDescriptor = value;
                _argumentsState = RestrictedMaterialized;
            }
        }
    }

    internal PropertyDescriptor? _callerDescriptor
    {
        get => _callerState switch
        {
            RestrictedAbsent => null,
            RestrictedPending => _pendingDescriptor,
            _ => _rare!.CallerDescriptor,
        };
        set
        {
            if (value is null || ReferenceEquals(value, _pendingDescriptor))
            {
                if (_rare is not null)
                {
                    _rare.CallerDescriptor = null;
                }

                _callerState = value is null ? RestrictedAbsent : RestrictedPending;
            }
            else
            {
                Rare.CallerDescriptor = value;
                _callerState = RestrictedMaterialized;
            }
        }
    }

    // Reuse cache for this function's call environments, populated on a pool-eligible call's return.
    // Interpreted via State.IsDirectRecursive: a FunctionEnvironment (the next call reuses it directly)
    // for ordinary functions, or a RecursiveEnvPool for direct-recursive ones. Held per function instance
    // (thus per engine) rather than on the shared JintFunctionDefinition.State so a prepared script reused
    // across engines never pins an engine via a cached environment (issue #2560). Cleared slot arrays hold
    // no engine references and stay shared on State._cachedSlots. Kept in the field of the function's own
    // descriptors while it has none (see Function.HeldEnvironment), so a closure carries no field for it.
    internal object? _envReuse
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => HeldEnvironment;
        set => HeldEnvironment = value;
    }

    /// <summary>Takes the call environment a kernel call of this instance kept with its frame, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FunctionEnvironment? TakeKernelEnvironment()
    {
        if (_ownDescriptors is FunctionEnvironment { KernelFrame.OwnedByInstance: true } environment)
        {
            _ownDescriptors = null;
            return environment;
        }

        return _ownDescriptors is FunctionRareData { EnvReuse: FunctionEnvironment { KernelFrame.OwnedByInstance: true } } ? TakeRareKernelEnvironment() : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private FunctionEnvironment? TakeRareKernelEnvironment()
    {
        var environment = (FunctionEnvironment?) _envReuse;
        _envReuse = null;
        return environment;
    }

    /// <summary>Keeps a kernel call's environment for this instance's next call; the first call's is not kept.</summary>
    internal bool KeepKernelEnvironment(FunctionEnvironment environment)
    {
        if (!_returnedEnvironment)
        {
            _returnedEnvironment = true;
            return false;
        }

        // An environment a call on the interpreter kept gives way; a pool of a recursive function does not.
        if (_envReuse is { } held && held is not FunctionEnvironment)
        {
            return false;
        }

        _envReuse = environment;
        return true;
    }

    /// <summary>Takes the environment a previous call of this function left for the next one, if any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FunctionEnvironment? TakeReusableEnvironment()
    {
        var held = _ownDescriptors;
        if (held is FunctionEnvironment environment)
        {
            _ownDescriptors = null;
            return environment;
        }

        if (held is FunctionRareData { EnvReuse: FunctionEnvironment rareEnvironment } rare)
        {
            rare.EnvReuse = null;
            return rareEnvironment;
        }

        return TakePooledEnvironment(held);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FunctionEnvironment? TakePooledEnvironment(object? held)
    {
        var pool = held as RecursiveEnvPool ?? (held as FunctionRareData)?.EnvReuse as RecursiveEnvPool;
        return pool?.TryRent();
    }

    // Whether a call of this instance has returned its environment. The first one is not kept: a closure made for
    // every object (a method a constructor defines) is mostly called once, and its kept environment would live
    // as long as the closure for nothing. The second and later calls keep theirs for the next.
    private bool _returnedEnvironment
    {
        get => HasFlag(FlagReturnedEnvironment);
        set => SetFlag(FlagReturnedEnvironment, value);
    }

    // [[PrivateMethods]] and [[Fields]] of a class constructor, both lists even when empty; an ordinary function has
    // neither. A class whose decorators added instance initializers keeps its fields in its private-methods list, a
    // DecoratedInstanceElements, and has no fields list: ObjectInstance.InitializeInstanceElements relies on all three.
    internal List<PrivateElement>? _privateMethods
    {
        get => _rare?.PrivateMethods;
        set
        {
            if (_rare is not null || value is not null)
            {
                Rare.PrivateMethods = value;
            }
        }
    }

    internal List<ClassFieldDefinition>? _fields
    {
        get => _rare?.Fields;
        set
        {
            if (_rare is not null || value is not null)
            {
                Rare.Fields = value;
            }
        }
    }

    // Allocation-site feedback for shaping `new T()` instances. A constructor's first
    // CtorShapePromoteThreshold instances build dictionaries (so a constructor called once or twice — the
    // overwhelming norm, e.g. across the Test262 suite — never grows the shared per-prototype transition
    // tree); once it proves "hot" it is promoted to shape mode so repeated `new T()` with a stable layout
    // reuse one interned hidden class. _ctorEmptyShape caches the prototype's empty root to avoid a
    // per-construct lookup (revalidated when .prototype is reassigned).
    private const int CtorShapePromoteThreshold = 16;
    private bool _ctorShaped
    {
        get => HasFlag(FlagCtorShaped);
        set => SetFlag(FlagCtorShaped, value);
    }

    // Static eligibility verdict for this function instance: 0 = not yet analyzed, 1 = eligible
    // (body statically clean AND every class field name shape-compatible), 2 = ineligible. Provably
    // clean constructors skip the sampling window and shape from instance #3 — a short-lived engine
    // constructing 3-15 instances of each type otherwise never promotes — while instances #1 and #2
    // stay on the dictionary path: the shared per-prototype transition tree only pays off from about
    // three instances of a layout, so constructors of unrepeated layouts intern no shape state at
    // all. Combines the AST-pure State.CtorBodyShapeEligibility with the per-function class-fields
    // check; the combined verdict cannot live on the shared State because the shared
    // empty-constructor AST serves classes with different fields.
    // These four live in the rare data: only a function used as a constructor has them.
    private byte _ctorStaticEligibility
    {
        get => _rare?.CtorStaticEligibility ?? 0;
        set => Rare.CtorStaticEligibility = value;
    }

    private int _ctorSampleCount
    {
        get => _rare?.CtorSampleCount ?? 0;
        set => Rare.CtorSampleCount = value;
    }

    private Shape? _ctorEmptyShape
    {
        get => _rare?.CtorEmptyShape;
        set => Rare.CtorEmptyShape = value;
    }

    private ObjectInstance? _ctorEmptyShapeProto
    {
        get => _rare?.CtorEmptyShapeProto;
        set => Rare.CtorEmptyShapeProto = value;
    }

    /// <summary>
    /// http://www.ecma-international.org/ecma-262/5.1/#sec-13.2
    /// </summary>
    public ScriptFunction(
        Engine engine,
        IFunction functionDeclaration,
        bool strict,
        ObjectInstance? proto = null)
        : this(
            engine,
            new JintFunctionDefinition(functionDeclaration),
            JintEnvironment.NewDeclarativeEnvironment(engine, engine.ExecutionContext.LexicalEnvironment),
            strict ? FunctionThisMode.Strict : FunctionThisMode.Global,
            proto)
    {
    }

    internal ScriptFunction(
        Engine engine,
        JintFunctionDefinition function,
        Environment env,
        FunctionThisMode thisMode,
        ObjectInstance? proto = null)
        : base(engine, engine.Realm, function, env, thisMode)
    {
        engine._environmentCaptures++;
        _prototype = proto ?? _engine.Realm.Intrinsics.Function.PrototypeObject;
        // The own "length" property exists from birth; its descriptor is materialized lazily
        // from the definition on first read (see Function._pendingDescriptor).
        _length = _pendingDescriptor;
        _strict = function.Strict || thisMode == FunctionThisMode.Strict;

        if (!function.Strict
            && function.Function is not ArrowFunctionExpression
            && !function.Function.Generator
            && !function.Function.Async)
        {
            _argumentsDescriptor = _pendingDescriptor;
            _callerDescriptor = _pendingDescriptor;
        }
    }

    // A plain data property holding null, which is what a browser answers for a function that is not
    // currently executing. %ThrowTypeError% belongs on Function.prototype — where AddRestrictedFunctionProperties
    // puts it, and where a *strict* function's read still lands; a thrower on the own property is reachable by
    // a read no browser makes throw. Real code walks it:
    // Object.getOwnPropertyNames(fn).reduce((acc, k) => (acc[k] = fn[k], acc), {}) is how a Flux-era
    // library copies a store's statics, and its skip-list is built from a *strict* function, which has no
    // own "arguments" to list — so the copy reads it off every sloppy one it is given.
    internal PropertyDescriptor MaterializeArgumentsDescriptor()
    {
        return _argumentsDescriptor = new PropertyDescriptor(Null, PropertyFlag.Configurable);
    }

    // Deliberately undefined rather than the null a browser answers: undefined is test262's sentinel for
    // "this implementation does not offer the caller extension" (language/arguments-object/10.6-13-a-2 and
    // -3), and the alternative is a value the suite then requires to be callable.
    internal PropertyDescriptor MaterializeCallerDescriptor()
    {
        return _callerDescriptor = new PropertyDescriptor(Undefined, PropertyFlag.Configurable);
    }

    /// <summary>
    /// Stores a replacement descriptor for a currently field-backed restricted property. Returns
    /// false when the property is not field-backed (never was, or was deleted), in which case the
    /// caller stores it in the property dictionary — putting a resurrected property at the end of
    /// the key order exactly like the previous dictionary-backed representation did.
    /// </summary>
    internal bool TrySetRestrictedOwnProperty(JsValue property, PropertyDescriptor desc)
    {
        if (_argumentsDescriptor is not null && CommonProperties.Arguments.Equals(property))
        {
            _argumentsDescriptor = desc;
            return true;
        }

        if (_callerDescriptor is not null && CommonProperties.Caller.Equals(property))
        {
            _callerDescriptor = desc;
            return true;
        }

        return false;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-ecmascript-function-objects-call-thisargument-argumentslist
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal override JsValue Call(JsValue thisObject, JsCallArguments arguments)
    {
        // Deliberately the first statement, and deliberately here rather than at the call expression.
        // A call expression is only one of the routes into a function body — `new`, an accessor, a
        // valueOf/toString coercion, a Proxy trap and a host Invoke all reach one without evaluating a
        // CallExpression, and every one of those recursions ends as a native stack overflow when the
        // only probe in the engine is the call expression's. Being first also keeps it outside the
        // try/finally in CallOnce: a throw from inside it must not reach a finally that pops an
        // execution context this frame never pushed. On unless the host explicitly disables
        // Options.Constraints.StackOverflowGuard or selects MaxExecutionStackCount; what is left here
        // is one branch on a cached bool — see StackGuard.EnsureStackHeadroom.
        //
        // The probe belongs on the entry points that add a native frame, which is why it is here and in
        // CallWithStackFrame rather than inside CallOnce: ContinueTailCalls re-enters CallOnce and
        // CallCore from a loop, and a trampolined tail call consumes no stack to probe for.
        _engine._stackGuard.EnsureStackHeadroom();

        var result = CallOnce(thisObject, arguments);
        return result is TailCallRequest ? ContinueTailCalls(result, ownsFrame: false) : result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue CallWithStackFrame(JsValue thisObject, JsCallArguments arguments)
    {
        _engine._stackGuard.EnsureStackHeadroom();

        var result = CallOnce(thisObject, arguments);
        return result is TailCallRequest ? ContinueTailCalls(result, ownsFrame: true) : result;
    }

    /// <summary>
    /// Builds the function's State on its first call. Analysing the body is part of the call, so a body too deep for
    /// the stack fails as a <c>RangeError</c> the caller can catch, and an async function rejects its promise with it,
    /// as it does with any throw from its body. Returns the State, or that rejected promise: one object rather than an
    /// out parameter, so that <see cref="CallOnce"/>'s frame gains no address-taken local to clear on every call.
    /// See <see cref="SyntaxNesting"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private object InitializeDefinitionForCall()
    {
        var definition = _functionDefinition!;
        if (definition.Function is not { Async: true, Generator: false })
        {
            return definition.InitializeOrThrowRangeError(_realm);
        }

        try
        {
            return definition.Initialize();
        }
        catch (ScriptPreparationException exception) when (SyntaxNesting.IsTooDeep(exception))
        {
            // Nothing runs in here: a catch block runs on top of the frames of the walk that threw, and only
            // leaving it gives their stack back.
        }

        var promiseCapability = PromiseConstructor.NewPromiseCapability(_engine, _realm.Intrinsics.Promise);
        promiseCapability.Reject(SyntaxNesting.CreateRangeError(_realm));
        return promiseCapability.PromiseInstance;
    }

    /// <summary>
    /// The State, built if need be, for a call site that only wants it to choose a faster lane; <see langword="null"/>
    /// when the body is too deep for the stack to analyse, which the call itself then reports.
    /// </summary>
    internal JintFunctionDefinition.State? TryInitializeDefinition()
    {
        var state = _functionDefinition!.BuiltState;
        if (state is not null)
        {
            return state;
        }

        try
        {
            return _functionDefinition.Initialize();
        }
        catch (ScriptPreparationException exception) when (SyntaxNesting.IsTooDeep(exception))
        {
            return null;
        }
    }

    private JsValue CallOnce(JsValue thisObject, JsCallArguments arguments)
    {
        var state = _functionDefinition!.BuiltState;
        if (state is null)
        {
            var initialized = InitializeDefinitionForCall();
            if (initialized is not JintFunctionDefinition.State built)
            {
                return (JsValue) initialized;
            }

            state = built;
        }

        var strict = _strict;
        _functionDefinition.EnsureTailCallMarkers(state, strict);

        // Env-less leaf call: no bindings to create, no this/arguments/new.target route, no
        // closures — the callee FunctionEnvironment would exist only as a chain pointer, so the
        // frame runs against the captured environment directly. `arguments` are intentionally
        // ignored (0 params, no arguments object). Strictness rides on the pushed execution
        // context (set in CallLeaf), so no separate scope is needed here.
        if (state.SupportsLeafCall && !_engine._isDebugMode && !_isClassConstructor)
        {
            return CallLeaf(strict);
        }

        // Fixed-slot or empty-instantiation synchronous call: shares its whole body with the
        // register-argument entry point (CallFromRegisters) through CallCore, which the ArrayArguments
        // source specializes back to reading this very array. A leaf function is register-eligible as
        // well, but has already left through the arm above. Anything else falls through to the general
        // arm below unchanged.
        if (state.SupportsRegisterCall && !_engine._isDebugMode && !_isClassConstructor)
        {
            return CallCore(thisObject, new ArrayArguments(arguments), state);
        }

        FunctionEnvironment? funcEnv = null;

        try
        {
            ref readonly var calleeContext = ref PrepareForOrdinaryCall(Undefined, state, strict);

            if (_isClassConstructor)
            {
                Throw.TypeError(calleeContext.Realm, ClassConstructorWithoutNewMessage());
            }

            // Capture funcEnv for end-of-call pool return when bindings can't escape. Direct-recursive
            // functions return their env to a small bounded pool (so each live frame reuses a distinct
            // env); other non-escaping functions use the single-slot pool. Escaping envs are not pooled.
            if (!state.EnvironmentMayEscape)
            {
                funcEnv = (FunctionEnvironment) calleeContext.LexicalEnvironment;
            }

            // Bodies that provably never resolve this/super/new.target leave the this-binding
            // Uninitialized (any missed route throws via GetThisBinding rather than silently
            // observing a wrong value). The debugger reads the binding through CallFrame.This,
            // so debug mode always binds.
            if (!state.CanSkipThisBinding || _engine._isDebugMode)
            {
                OrdinaryCallBindThis(in calleeContext, thisObject);
            }

            // actual call
            var context = _engine._evaluationContext;

            var result = _functionDefinition.EvaluateBody(context, this, arguments, state);

            // For async functions/generators, DisposeResources is deferred to when
            // the body truly completes (AsyncBlockStart/AsyncFunctionResume).
            // Calling it here would dispose too early (before awaits complete).
            if (!_functionDefinition.IsAsync)
            {
                result = calleeContext.LexicalEnvironment.DisposeResources(result);
            }

            if (result.Type == CompletionType.Throw)
            {
                Throw.JavaScriptException(_engine, result.Value, in result);
            }

            // The DebugHandler needs the current execution context before the return for stepping through the return point
            if (context.DebugMode)
            {
                // We don't have a statement, but we still need a Location for debuggers. DebugHandler will infer one from
                // the function body:
                _engine.Debugger.OnReturnPoint(
                    _functionDefinition.Function.Body,
                    result.Type == CompletionType.Normal ? Undefined : result.Value
                );
            }

            if (result.Type == CompletionType.Return)
            {
                return result.Value;
            }
        }
        finally
        {
            if (funcEnv is not null)
            {
                ReturnEnvironment(funcEnv, state);
            }
            _engine.LeaveExecutionContext();
        }

        return Undefined;
    }

    /// <summary>
    /// The message for calling a class constructor as a function. The class's name lives on the constructor's
    /// own <c>name</c> property and nowhere else: <c>_functionDefinition</c> here is the constructor
    /// <em>method</em>'s, whose name is empty for every class, named or not. Reading the property instead
    /// covers all three ways a class acquires a name — a declaration's binding, a class expression's own
    /// identifier, and the target NamedEvaluation gives an anonymous one — and a class that acquired none
    /// gets the nameless phrasing rather than a message with a hole in it.
    /// </summary>
    private string ClassConstructorWithoutNewMessage()
    {
        var name = GetOwnFunctionName();
        return string.IsNullOrEmpty(name)
            ? "Class constructors cannot be invoked without 'new'"
            : $"Class constructor {name} cannot be invoked without 'new'";
    }

    /// <summary>
    /// The end-of-call environment-reuse step, shared by <see cref="Call"/>'s general arm and by
    /// <see cref="CallCore{TArgs}"/>: hands a non-escaping call environment (and its fixed-slot
    /// array) to whichever cache can serve the next call.
    /// </summary>
    private void ReturnEnvironment(FunctionEnvironment funcEnv, JintFunctionDefinition.State state)
    {
        // Cache on this function instance (per-engine by construction, so a prepared script's
        // shared State can't pin engines — see _envReuse). Single-threaded like the engine, so
        // no Interlocked is needed on the instance side.
        if (state.IsDirectRecursive)
        {
            // Return the env (with its fixed-slot array still attached) to the bounded
            // recursive pool so another simultaneously live frame can reuse env + slots.
            if (funcEnv._slots is { } recursiveSlots)
            {
                ClearSlots(recursiveSlots);
            }
            var pool = _envReuse as RecursiveEnvPool;
            if (pool is null)
            {
                _envReuse = pool = new RecursiveEnvPool();
            }
            pool.Return(funcEnv);
        }
        else
        {
            if (funcEnv._slots is { } slots)
            {
                ClearSlots(slots);

                // An environment this instance keeps for its own next call keeps its cleared slot array too, so
                // that call takes neither from the State. Any other environment hands the array to the shared
                // State: cleared, it holds no engine references, so any instance sharing this State (also in
                // another engine) can reuse it.
                if (_functionDefinition!.IsDynamic || !_returnedEnvironment)
                {
                    Interlocked.Exchange(ref state._cachedSlots, slots);
                    funcEnv._slots = null;
                }
            }

            if (_functionDefinition!.IsDynamic)
            {
                // Function-constructor instances are one-shot (a fresh ScriptFunction per
                // `new Function(...)`), so an instance-level cache never warms. Park the env
                // on the per-realm definition instead — env identity then stays stable across
                // instances, keeping the shared statement tree's per-node slot caches valid.
                //
                // The outer link stays attached, unlike the block/catch/loop parks. Those pool only
                // environments no closure can reach at all; EnvironmentMayEscape is weaker — it lets a
                // closure that reads none of the slots escape this one — and such a closure still
                // resolves its free identifiers *through* this record. Detaching left such a closure
                // starting its lookup at a record with no outer link, which identifier resolution reads
                // as "this is the global environment" — so a plain global read threw InvalidCastException.
                // Keeping the link roots nothing: a dynamic function closes over the realm's global
                // environment (CreateDynamicFunction), which the realm holds for its own lifetime.
                Interlocked.Exchange(ref state._dynamicCachedEnv, funcEnv);
            }
            else if (_returnedEnvironment)
            {
                // Keep the env for the next call; one already held means a nested call returned, so recursion gets a pool.
                var held = _envReuse;
                if (held is RecursiveEnvPool pool)
                {
                    pool.Return(funcEnv);
                }
                else if (held is FunctionEnvironment nested && !ReferenceEquals(nested, funcEnv))
                {
                    pool = new RecursiveEnvPool();
                    pool.Return(nested);
                    pool.Return(funcEnv);
                    _envReuse = pool;
                }
                else
                {
                    _envReuse = funcEnv;
                }
            }
            else
            {
                _returnedEnvironment = true;
                if (state.ReusesEnvironmentAcrossInstances)
                {
                    // the first call of another instance can have it
                    _engine.ParkEnvironment(state, funcEnv);
                }
            }
        }
    }

    /// <summary>
    /// Clears a returned environment's slots. A call's few slots are cleared in place: a cleared binding holds only
    /// null references, which need no write barrier, and the general clear costs more than that for so few.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearSlots(Binding[] slots)
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
            System.Array.Clear(slots, 0, slots.Length);
        }
    }

    /// <summary>
    /// The shared body behind this function's two fast synchronous [[Call]] entry points — the
    /// array-backed <see cref="Call"/> arm and the register-backed <see cref="CallFromRegisters"/> —
    /// generic over where the argument values live, so the JIT specializes it per source and the
    /// register form never materializes an argument array.
    /// </summary>
    /// <remarks>
    /// Covers only what both callers gate on: <see cref="JintFunctionDefinition.State.SupportsRegisterCall"/>
    /// (fixed-slot FDI, neither generator nor async), not in debug mode, not a class constructor.
    /// Those preconditions are exactly what removes the three things <see cref="Call"/>'s general arm
    /// still carries — the async DisposeResources deferral, the JsArguments materialization (fixed
    /// slots require !ArgumentsObjectNeeded) and the debugger's OnReturnPoint hook. Everything else
    /// is step for step the general arm.
    /// </remarks>
    private JsValue CallCore<TArgs>(JsValue thisObject, in TArgs args, JintFunctionDefinition.State state)
        where TArgs : struct, IArgumentSource
    {
        FunctionEnvironment? funcEnv = null;

        try
        {
            ref readonly var calleeContext = ref PrepareForOrdinaryCall(Undefined, state, _strict);

            // Capture funcEnv for end-of-call pool return when bindings can't escape.
            if (!state.EnvironmentMayEscape)
            {
                funcEnv = (FunctionEnvironment) calleeContext.LexicalEnvironment;
            }

            // Bodies that provably never resolve this/super/new.target leave the this-binding
            // Uninitialized. Debug mode, which always binds, is excluded by the gate.
            if (!state.CanSkipThisBinding)
            {
                OrdinaryCallBindThis(in calleeContext, thisObject);
            }

            // actual call
            var context = _engine._evaluationContext;
            var definition = _functionDefinition!;

            // EvaluateBodyFast, with the instantiation taken out so that this method inlines one copy of it.
            _engine.FunctionDeclarationInstantiationFast(state, in args);

            if (definition.Kernel(_engine, state) is { } kernel && KernelProgram.CanRun(context))
            {
                return kernel.Run(context, funcEnv);
            }

            if (definition.SingleReturnBody is { } singleReturnBody)
            {
                // The statement-body arm, with the completion unwrapped by the list.
                return singleReturnBody.ExecuteSingleReturnBody(context);
            }

            if (definition.ValueBody is { } valueBody)
            {
                // The same for a longer body whose environment has nothing to dispose.
                return valueBody.ExecuteValueBody(context);
            }

            return EvaluateInstantiatedBody(context, definition, in calleeContext);
        }
        finally
        {
            if (funcEnv is not null)
            {
                ReturnEnvironment(funcEnv, state);
            }
            _engine.LeaveExecutionContext();
        }
    }

    /// <summary>
    /// The rest of <see cref="CallCore{TArgs}"/> for a body other than a single <c>return</c>: evaluates it, disposes
    /// its resources and turns the completion into the call's value. Out of line because its completion records are
    /// stack slots the runtime zeroes on entry, which every call through the register lane paid for when they were
    /// part of its frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue EvaluateInstantiatedBody(EvaluationContext context, JintFunctionDefinition definition, in Runtime.Environments.ExecutionContext calleeContext)
    {
        var result = definition.EvaluateInstantiatedBodyFast(context);

        // Not async by the gate, so disposal is never deferred to AsyncBlockStart.
        result = calleeContext.LexicalEnvironment.DisposeResources(result);

        if (result.Type == CompletionType.Throw)
        {
            Throw.JavaScriptException(_engine, result.Value, in result);
        }

        return result.Type == CompletionType.Return ? result.Value : Undefined;
    }

    /// <summary>
    /// The register-argument [[Call]] entry point: same observable behaviour as <see cref="Call"/>,
    /// but the arguments arrive in locals instead of through a rented <see cref="JsCallArguments"/>.
    /// Arguments the site did not supply arrive as <see cref="JsValue.Undefined"/>, matching
    /// <c>Arguments.At</c>; <paramref name="argCount"/> is the site's real arity, so registers beyond
    /// it are never read.
    /// </summary>
    /// <remarks>
    /// Only valid when <paramref name="state"/> is this function's own state, its
    /// <see cref="JintFunctionDefinition.State.SupportsRegisterCall"/> holds, and the caller has
    /// established !Engine._isDebugMode and !_isClassConstructor — the same gate <see cref="Call"/>
    /// applies before taking the array-backed arm. Deliberately not named CallFast: that name belongs
    /// to <see cref="Function.CallFast"/>, the arity-specialized built-in lane, which this is not.
    /// </remarks>
    internal JsValue CallFromRegisters(
        JsValue thisObject,
        JsValue arg0,
        JsValue arg1,
        JsValue arg2,
        JsValue arg3,
        int argCount,
        JintFunctionDefinition.State state,
        bool ownsFrame)
    {
        // The register lane is dispatched straight from JintCallExpression, so it never passes through
        // Call above and needs a probe of its own. As there, the probe sits on the entry that adds the
        // native frame, not on the CallCore the trampoline re-enters.
        _engine._stackGuard.EnsureStackHeadroom();

        _functionDefinition!.EnsureTailCallMarkers(state, _strict);

        // A leaf function has no parameters to fill, so the registers (already evaluated by the caller)
        // are not needed, and it runs without a callee environment as its [[Call]] would run it.
        var result = state.SupportsLeafCall
            ? CallLeaf(_strict)
            : CallCore(thisObject, new RegisterArguments(arg0, arg1, arg2, arg3, argCount), state);
        return result is TailCallRequest ? ContinueTailCalls(result, ownsFrame) : result;
    }

    /// <summary><see cref="CallFromRegisters"/> with Numbers unboxed, which reach the parameters unboxed.</summary>
    internal JsValue CallFromNumbers(JsValue thisObject, in NumberArguments arguments, JintFunctionDefinition.State state)
    {
        _engine._stackGuard.EnsureStackHeadroom();
        _functionDefinition!.EnsureTailCallMarkers(state, _strict);
        var result = state.SupportsLeafCall
            ? CallLeaf(_strict)
            : CallCore(thisObject, in arguments, state);
        return result is TailCallRequest ? ContinueTailCalls(result, ownsFrame: true) : result;
    }

    /// <summary>
    /// A kernel's call of this function, whose body runs on <paramref name="kernel"/>: <see cref="CallCore{TArgs}"/>
    /// with the body's Number result left unboxed in <paramref name="number"/>.
    /// </summary>
    internal JsValue? CallKernel(JsValue thisObject, in NumberArguments arguments, JintFunctionDefinition.State state, FunctionKernel kernel, out double number)
    {
        var engine = _engine;
        engine._stackGuard.EnsureStackHeadroom();
        FunctionEnvironment? funcEnv = null;
        try
        {
            ref readonly var calleeContext = ref PrepareForOrdinaryCall(Undefined, state, _strict);
            if (!state.EnvironmentMayEscape)
            {
                funcEnv = (FunctionEnvironment) calleeContext.LexicalEnvironment;
            }

            if (!state.CanSkipThisBinding)
            {
                OrdinaryCallBindThis(in calleeContext, thisObject);
            }

            engine.FunctionDeclarationInstantiationFast(state, in arguments);
            return kernel.RunUnboxed(engine._evaluationContext, funcEnv, out number);
        }
        finally
        {
            if (funcEnv is not null)
            {
                ReturnEnvironment(funcEnv, state);
            }

            engine.LeaveExecutionContext();
        }
    }

    /// <summary>
    /// <see cref="CallKernel"/> made at <paramref name="site"/> with one frame for the call, from which the call stack
    /// frame and execution context are pushed only when something reads either stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue? CallKernelInFrame(JintExpression site, JsValue thisObject, in NumberArguments arguments, JintFunctionDefinition.State state, FunctionKernel kernel, out double number)
    {
        var engine = _engine;
        var frame = kernel.EnterCallFrame(engine._evaluationContext, this, state);
        var funcEnv = frame.CallEnvironment!;
        if (!state.CanSkipThisBinding)
        {
            OrdinaryCallBindThis(funcEnv, thisObject);
        }

        Engine.FunctionDeclarationInstantiationInto(funcEnv, state, in arguments);
        kernel.BeginCall(frame);
        engine.PushCallFrame(funcEnv, site);
        JsValue? result;
        try
        {
            result = kernel.RunBody(frame, out number);
        }
        finally
        {
            engine.PopCallFrame();
            kernel.LeaveCallFrame(frame, state, this);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CanPrepareTailCall(EvaluationContext context, bool isTailPosition)
    {
        var engine = context.Engine;
        return isTailPosition
               && engine.ExecutionContext.Strict
               && engine.ExecutionContext.Suspendable is null
               && !engine._isDebugMode;
    }

    /// <summary>
    /// Runs interpreted proper tail calls as a trampoline, after the caller's execution context and
    /// environment have unwound. Replacing the visible frame keeps error stacks and recursion tracking
    /// aligned with the execution-context replacement required by PrepareForTailCall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recursion depth is read straight off the call stack — <see cref="JintCallStack.ReplaceTop"/>
    /// returns it exactly as <c>JintCallStack.Push</c> does — because a replaced frame keeps
    /// counting until this trampoline returns (see that method's remarks). The counters used to be
    /// private locals here instead, which held only while <em>one</em> activation of this loop was on
    /// the stack: a tail-call target that re-entered the engine through a non-tail route, and so landed
    /// in a nested trampoline, restarted them from zero, and <c>Options.LimitRecursion</c> then never
    /// fired however deep the native stack grew.
    /// </para>
    /// <para>
    /// What is left here is the ledger of retentions to hand back on the way out: one per
    /// <see cref="JintCallStack.ReplaceTop"/>, tallied against the function that call <em>displaced</em>
    /// — the replacement is accounted by the frame it now names, so it is the displaced one whose
    /// occurrence outlives its element. Two fields carry the one- and two-function cases (direct and
    /// mutual tail recursion) without allocating; anything wider spills into the dictionary. Tallied by
    /// <see cref="Function"/> identity rather than by definition — two instances sharing a definition
    /// are one key to the statistic, so their separate tallies release against the same entry and still
    /// sum to what was retained.
    /// </para>
    /// </remarks>
    private JsValue ContinueTailCalls(JsValue result, bool ownsFrame)
    {
        var engine = _engine;
        var callStack = engine.CallStack;
        var pushedFrame = false;
        Function? currentFrame = ownsFrame ? this : null;
        var tracksDepth = engine._maxRecursionDepth >= 0;

        Function? retained0 = null;
        var retainCount0 = 0;
        Function? retained1 = null;
        var retainCount1 = 0;
        Dictionary<Function, int>? retainedRest = null;

        try
        {
            while (result is TailCallRequest request)
            {
                var target = request.Target;
                var thisObject = request.ThisObject;
                var arguments = request.Arguments;
                var argumentsRented = request.ArgumentsRented;
                var expression = request.Expression;
                var registerState = request.RegisterState;
                var arg0 = request.Arg0;
                var arg1 = request.Arg1;
                var arg2 = request.Arg2;
                var arg3 = request.Arg3;
                var argCount = request.ArgCount;
                engine.ReturnTailCallRequest(request);

                try
                {
                    int recursionDepth;
                    if (currentFrame is not null && callStack.TopIs(currentFrame))
                    {
                        recursionDepth = callStack.ReplaceTop(target, expression);

                        if (tracksDepth)
                        {
                            var displaced = currentFrame;
                            if (retained0 is null || ReferenceEquals(retained0, displaced))
                            {
                                retained0 = displaced;
                                retainCount0++;
                            }
                            else if (retained1 is null || ReferenceEquals(retained1, displaced))
                            {
                                retained1 = displaced;
                                retainCount1++;
                            }
                            else
                            {
                                retainedRest ??= new Dictionary<Function, int>();
                                retainedRest.TryGetValue(displaced, out var retained);
                                retainedRest[displaced] = retained + 1;
                            }
                        }
                    }
                    else
                    {
                        recursionDepth = callStack.Push(target, expression, engine.ExecutionContext);
                        pushedFrame = true;
                    }

                    currentFrame = target;

                    if (recursionDepth > engine._maxRecursionDepth)
                    {
                        engine.AbandonImplicitMemoryOperation();
                        Throw.RecursionDepthOverflowException(callStack, preserveTop: true);
                    }

                    result = registerState is null
                        ? target.CallOnce(thisObject, arguments)
                        : registerState.SupportsLeafCall
                            ? target.CallLeaf(target._strict)
                            : target.CallCore(
                                thisObject,
                                new RegisterArguments(arg0, arg1, arg2, arg3, argCount),
                                registerState);
                }
                finally
                {
                    if (argumentsRented)
                    {
                        engine._jsValueArrayPool.ReturnArray(arguments);
                    }
                }
            }

            return result;
        }
        finally
        {
            if (pushedFrame)
            {
                callStack.TryPopAndDiscard();
            }

            if (retained0 is not null)
            {
                callStack.ReleaseTailRetention(retained0, retainCount0);
            }

            if (retained1 is not null)
            {
                callStack.ReleaseTailRetention(retained1, retainCount1);
            }

            if (retainedRest is not null)
            {
                foreach (var entry in retainedRest)
                {
                    callStack.ReleaseTailRetention(entry.Key, entry.Value);
                }
            }
        }
    }

    /// <summary>
    /// The env-less [[Call]] arm for <see cref="JintFunctionDefinition.State.SupportsLeafCall"/>
    /// functions: pushes an execution context whose environments are the captured environment
    /// itself, runs the body statement list, and maps the completion exactly like the ordinary
    /// arm (Return → value, fall-through → undefined, Throw → JavaScriptException).
    /// Function-level DisposeResources is skipped deliberately: a leaf body cannot register
    /// function-level dispose resources (no lexical declarations), and running dispose against
    /// the CAPTURED environment would drain the enclosing function's pending `using` resources
    /// mid-lifetime. Nested blocks own their disposal end-to-end.
    /// </summary>
    private JsValue CallLeaf(bool strict)
    {
        var engine = _engine;
        var origin = _functionOrigin;
        engine.EnterLeafCallExecutionContext(origin.ScriptOrModule, _environment!, _privateEnvironment, origin.Realm, this, strict);
        try
        {
            var context = engine._evaluationContext;
            var definition = _functionDefinition!;
            if (definition.Kernel(engine, definition.BuiltState!) is { } kernel && KernelProgram.CanRun(context))
            {
                return kernel.Run(context, environment: null);
            }

            if (definition.SingleReturnBody is { } singleReturnBody)
            {
                return singleReturnBody.ExecuteSingleReturnBody(context);
            }

            if (definition.ValueBody is { } valueBody)
            {
                return valueBody.ExecuteValueBody(context);
            }

            var result = definition.EvaluateLeafBody(context);

            if (result.Type == CompletionType.Throw)
            {
                Throw.JavaScriptException(engine, result.Value, in result);
            }

            return result.Type == CompletionType.Return ? result.Value : Undefined;
        }
        finally
        {
            engine.LeaveExecutionContext();
        }
    }

    internal override bool IsConstructor
    {
        get
        {
            if (!_homeObject.IsUndefined() && !_isClassConstructor)
            {
                return false;
            }

            var function = _functionDefinition?.Function;
            return function is not null
                   && function is not ArrowFunctionExpression
                   && !function.Generator
                   && !function.Async;
        }
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-ecmascript-function-objects-construct-argumentslist-newtarget
    /// </summary>
    ObjectInstance IConstructor.Construct(JsCallArguments arguments, JsValue newTarget)
        => Construct(arguments, newTarget, ownsFrame: false);

    internal ObjectInstance ConstructWithStackFrame(JsCallArguments arguments, JsValue newTarget)
        => Construct(arguments, newTarget, ownsFrame: true);

    private ObjectInstance Construct(JsCallArguments arguments, JsValue newTarget, bool ownsFrame)
    {
        // `function F() { new F(); } new F();` reaches a function body without ever evaluating a
        // CallExpression, so [[Construct]] carries the probe as [[Call]] does.
        _engine._stackGuard.EnsureStackHeadroom();

        var state = _functionDefinition!.BuiltState ?? _functionDefinition.InitializeOrThrowRangeError(_realm);
        _functionDefinition.EnsureTailCallMarkers(state, _strict);
        var callerContext = _engine.ExecutionContext;
        var kind = _constructorKind;

        var thisArgument = Undefined;

        if (kind == ConstructorKind.Base)
        {
            var currentPrototypeDescriptor = _prototypeDescriptor;
            if (ReferenceEquals(newTarget, this) && ReferenceEquals(currentPrototypeDescriptor, _pendingDescriptor))
            {
                currentPrototypeDescriptor = MaterializePrototypeDescriptor();
            }

            if (ReferenceEquals(newTarget, this)
                && currentPrototypeDescriptor is { } prototypeDescriptor
                && !prototypeDescriptor.IsAccessorDescriptor())
            {
                thisArgument = NewThis(prototypeDescriptor);
            }
            else
            {
                thisArgument = OrdinaryCreateFromConstructor(
                    newTarget,
                    static intrinsics => intrinsics.Object.PrototypeObject,
                    static (Engine engine, Realm _, object? _) => new JsObject(engine));
            }

            StartShape(thisArgument, state);
        }

        var strict = _thisMode == FunctionThisMode.Strict;
        ref readonly var calleeContext = ref PrepareForOrdinaryCall(newTarget, state, strict);
        var constructorEnv = (FunctionEnvironment) calleeContext.LexicalEnvironment;
        TailCallRequest? tailCallRequest = null;

        try
        {
            if (kind == ConstructorKind.Base)
            {
                OrdinaryCallBindThis(in calleeContext, thisArgument);
                ((ObjectInstance) thisArgument).InitializeInstanceElements(this);
            }

            var context = _engine._evaluationContext;

            Completion result;
            if (kind == ConstructorKind.Base && state.SupportsRegisterCall && !_engine._isDebugMode && !_isClassConstructor)
            {
                _engine.FunctionDeclarationInstantiationFast(state, new ArrayArguments(arguments));
                if (_functionDefinition.Kernel(_engine, state) is { } kernel && KernelProgram.CanRun(context))
                {
                    // A body on a kernel holds no resources to dispose and makes no tail call.
                    var returned = kernel.Run(context, environment: null);
                    if (constructorEnv._slots is { } slots && kernel.KeepsEnvironmentPrivate(state))
                    {
                        // Nothing reaches the environment past the call: its cleared slots serve the next one.
                        ClearSlots(slots);
                        Interlocked.Exchange(ref state._cachedSlots, slots);
                        constructorEnv._slots = null;
                    }

                    return returned as ObjectInstance ?? (ObjectInstance) thisArgument;
                }

                result = _functionDefinition.EvaluateInstantiatedBodyFast(context);
            }
            else
            {
                result = _functionDefinition.EvaluateBody(context, this, arguments, state);
            }

            result = constructorEnv.DisposeResources(result);

            // The DebugHandler needs the current execution context before the return for stepping through the return point
            // We exclude the constructors Jint synthesizes for classes without an explicit one: their body
            // belongs to no program the host was handed, so its end is not a position anything can name.
            bool isStep = context.DebugMode &&
                          result.Type != CompletionType.Throw &&
                          !ClassDefinition.IsSynthesizedConstructor(_functionDefinition.Function);
            if (isStep)
            {
                // We don't have a statement, but we still need a Location for debuggers. DebugHandler will infer one from
                // the function body:
                _engine.Debugger.OnReturnPoint(
                    _functionDefinition.Function.Body,
                    result.Type == CompletionType.Normal ? thisArgument : result.Value
                );
            }

            if (result is { Type: CompletionType.Return, Value: TailCallRequest request })
            {
                tailCallRequest = request;
            }
            else if (result.Type == CompletionType.Return)
            {
                if (ResolveConstructorReturn(result.Value, kind, thisArgument, callerContext.Realm) is { } returnObject)
                {
                    return returnObject;
                }
            }
            else if (result.Type == CompletionType.Throw)
            {
                Throw.JavaScriptException(_engine, result.Value, in result);
            }
        }
        finally
        {
            _engine.LeaveExecutionContext();
        }

        if (tailCallRequest is not null)
        {
            var value = ContinueTailCalls(tailCallRequest, ownsFrame);
            if (ResolveConstructorReturn(value, kind, thisArgument, callerContext.Realm) is { } returnObject)
            {
                return returnObject;
            }
        }

        return (ObjectInstance) constructorEnv.GetThisBinding();
    }

    /// <summary>The object a <c>new</c> of this function makes, its prototype read from <paramref name="prototypeDescriptor"/>, a data property.</summary>
    private JsObject NewThis(PropertyDescriptor prototypeDescriptor)
        => new(_engine) { _prototype = prototypeDescriptor.Value as ObjectInstance ?? _realm.Intrinsics.Object.PrototypeObject };

    /// <summary>
    /// Once the constructor is hot, a fresh <c>this</c> starts in shape-building mode, so that its stores transition a
    /// shared hidden class instead of building a dictionary; a body proved clean shapes from the third instance.
    /// </summary>
    private void StartShape(JsValue thisArgument, JintFunctionDefinition.State state)
    {
        if (thisArgument is JsObject thisObject && thisObject.Prototype is { } proto)
        {
            if (_ctorShaped || CheckCtorShapeEligibility(state))
            {
                if (!ReferenceEquals(_ctorEmptyShapeProto, proto))
                {
                    _ctorEmptyShape = _engine.GetEmptyShape(proto);
                    _ctorEmptyShapeProto = proto;
                }

                thisObject.StartShapeBuilding(_ctorEmptyShape!);
            }
        }
    }

    /// <summary>
    /// [[Construct]] (https://tc39.es/ecma262/#sec-ecmascript-function-objects-construct-argumentslist-newtarget) of this
    /// function in a frame of <paramref name="kernel"/>; null, having run nothing, where its prototype is not data.
    /// </summary>
    internal ObjectInstance? ConstructKernelInFrame(JintExpression site, in NumberArguments arguments, JintFunctionDefinition.State state, FunctionKernel kernel)
    {
        var prototypeDescriptor = _prototypeDescriptor;
        if (ReferenceEquals(prototypeDescriptor, _pendingDescriptor))
        {
            prototypeDescriptor = MaterializePrototypeDescriptor();
        }

        if (prototypeDescriptor is null || prototypeDescriptor.IsAccessorDescriptor())
        {
            return null;
        }

        var engine = _engine;
        _functionDefinition!.EnsureTailCallMarkers(state, _strict);
        var thisArgument = NewThis(prototypeDescriptor);
        StartShape(thisArgument, state);

        var frame = kernel.EnterCallFrame(engine._evaluationContext, this, state);
        var environment = frame.CallEnvironment!;
        if (!state.CanSkipThisBinding)
        {
            OrdinaryCallBindThis(environment, thisArgument);
        }

        thisArgument.InitializeInstanceElements(this);
        Engine.FunctionDeclarationInstantiationInto(environment, state, in arguments);
        kernel.BeginCall(frame);
        engine.PushCallFrame(environment, site);
        JsValue? result;
        try
        {
            result = kernel.RunBody(frame, out _);
        }
        finally
        {
            engine.PopCallFrame();
            kernel.LeaveCallFrame(frame, state, this);
        }

        return result as ObjectInstance ?? thisArgument;
    }

    private static ObjectInstance? ResolveConstructorReturn(
        JsValue value,
        ConstructorKind kind,
        JsValue thisArgument,
        Realm callerRealm)
    {
        if (value is ObjectInstance objectValue)
        {
            return objectValue;
        }

        if (kind == ConstructorKind.Base)
        {
            return (ObjectInstance) thisArgument;
        }

        if (!value.IsUndefined())
        {
            Throw.TypeError(callerRealm);
        }

        return null;
    }

    /// <summary>
    /// Cold-path shaping decision for a not-yet-promoted constructor: statically clean bodies (see
    /// <see cref="JintFunctionDefinition.ComputeCtorBodyShapeEligibility"/>) promote after their SECOND
    /// construction and shape from instance #3 — instances #1 and #2 stay on the dictionary path, so
    /// constructors of layouts that never recur (the overwhelming norm) intern no transition tree,
    /// empty-shape root or prototype CWT entry. Measured: shaping at instance #1 regressed a
    /// 200-distinct-one-shot-ctor guard ~20% time/alloc; shaping at instance #2 still lost ~3% time /
    /// ~440 B per eval on re-evaluated class declarations constructing exactly two instances — the
    /// interned tree pays off at ≥3 instances of a layout. (Documented alternatives: shape from #1 =
    /// `return true` from the eligible branch; from #2 = set _ctorShaped unconditionally there.)
    /// Ineligible bodies keep the sampling threshold with its pre-existing pacing (the
    /// threshold-tripping instance itself stays on the dictionary path; the next construct starts
    /// shaped). Returns whether the CURRENT instance should start in shape-building mode.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool CheckCtorShapeEligibility(JintFunctionDefinition.State state)
    {
        var eligibility = _ctorStaticEligibility;
        if (eligibility == 0)
        {
            _ctorStaticEligibility = eligibility = ComputeCtorStaticEligibility(state);
        }

        if (eligibility == 1)
        {
            // dictionaries for the first two instances; shape from the THIRD construction on
            if (++_ctorSampleCount >= 2)
            {
                _ctorShaped = true;
            }
            return false;
        }

        if (++_ctorSampleCount >= CtorShapePromoteThreshold)
        {
            _ctorShaped = true;
        }

        return false;
    }

    /// <summary>
    /// Combines the shared AST-level body verdict (cached on the cross-engine State) with this function's
    /// class-field names: every field must be a plain string key that cannot be an array index — a
    /// digit-leading key would force the ordered-enumeration deopt on first keys read. Private names are
    /// fine (PrivateFieldAdd bypasses property storage), while symbol names reject, and so does a
    /// class whose decorators added instance initializers (its fields are in a
    /// <see cref="DecoratedInstanceElements"/>; they invoke arbitrary callables against `this`). Field
    /// initializer bodies are deliberately not scanned: post-threshold hot constructors already run
    /// them under shape building today, and TryShapeAdd's MaxFanout bounds any dynamism.
    /// </summary>
    private byte ComputeCtorStaticEligibility(JintFunctionDefinition.State state)
    {
        var bodyEligibility = state.CtorBodyShapeEligibility;
        if (bodyEligibility == 0)
        {
            bodyEligibility = JintFunctionDefinition.ComputeCtorBodyShapeEligibility(_functionDefinition!.Function) ? (byte) 1 : (byte) 2;
            state.CtorBodyShapeEligibility = bodyEligibility;
        }

        if (bodyEligibility != 1)
        {
            return 2;
        }

        if (_privateMethods is DecoratedInstanceElements)
        {
            return 2;
        }

        var fields = _fields;
        if (fields is not null)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                var name = fields[i].Name;
                if (name is PrivateName)
                {
                    continue;
                }

                if (name is not JsString jsString)
                {
                    return 2;
                }

                var stringName = jsString.ToString();
                if (stringName.Length > 0 && char.IsDigit(stringName[0]))
                {
                    return 2;
                }
            }
        }

        return 1;
    }

    internal void MakeClassConstructor()
    {
        _isClassConstructor = true;
    }
}

/// <summary>
/// Internal completion payload used to transfer an interpreted tail call to the trampoline.
/// It is represented as a non-empty JsValue so ordinary Completion propagation carries it unchanged.
/// </summary>
internal sealed class TailCallRequest : JsValue
{
    internal TailCallRequest()
        : base(InternalTypes.TailCall)
    {
    }

    internal TailCallRequest Reassign(
        ScriptFunction target,
        JsValue thisObject,
        JsValue[] arguments,
        bool argumentsRented,
        JintExpression expression)
    {
        Target = target;
        ThisObject = thisObject;
        Arguments = arguments;
        ArgumentsRented = argumentsRented;
        RegisterState = null;
        Expression = expression;
        return this;
    }

    internal TailCallRequest Reassign(
        ScriptFunction target,
        JsValue thisObject,
        JsValue arg0,
        JsValue arg1,
        JsValue arg2,
        JsValue arg3,
        int argCount,
        JintFunctionDefinition.State state,
        JintExpression expression)
    {
        Target = target;
        ThisObject = thisObject;
        Arguments = null!;
        ArgumentsRented = false;
        Arg0 = arg0;
        Arg1 = arg1;
        Arg2 = arg2;
        Arg3 = arg3;
        ArgCount = argCount;
        RegisterState = state;
        Expression = expression;
        return this;
    }

    internal ScriptFunction Target = null!;
    internal JsValue ThisObject = null!;
    internal JsValue[] Arguments = null!;
    internal bool ArgumentsRented;
    internal JsValue Arg0 = null!;
    internal JsValue Arg1 = null!;
    internal JsValue Arg2 = null!;
    internal JsValue Arg3 = null!;
    internal int ArgCount;
    internal JintFunctionDefinition.State? RegisterState;
    internal JintExpression Expression = null!;

    public override object? ToObject() => throw new InvalidOperationException("A tail-call request escaped the interpreter trampoline.");
}

internal static class TailCallRequestReuse
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TailCallRequest RentTailCallRequest(
        this Engine engine,
        ScriptFunction target,
        JsValue thisObject,
        JsValue[] arguments,
        bool argumentsRented,
        JintExpression expression)
    {
        var request = engine._tailCallRequest;
        engine._tailCallRequest = null;
        return (request ?? new TailCallRequest()).Reassign(target, thisObject, arguments, argumentsRented, expression);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TailCallRequest RentTailCallRequest(
        this Engine engine,
        ScriptFunction target,
        JsValue thisObject,
        JsValue arg0,
        JsValue arg1,
        JsValue arg2,
        JsValue arg3,
        int argCount,
        JintFunctionDefinition.State state,
        JintExpression expression)
    {
        var request = engine._tailCallRequest;
        engine._tailCallRequest = null;
        return (request ?? new TailCallRequest()).Reassign(
            target,
            thisObject,
            arg0,
            arg1,
            arg2,
            arg3,
            argCount,
            state,
            expression);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ReturnTailCallRequest(this Engine engine, TailCallRequest request)
    {
        request.Target = null!;
        request.ThisObject = null!;
        request.Arguments = null!;
        request.ArgumentsRented = false;
        request.Arg0 = null!;
        request.Arg1 = null!;
        request.Arg2 = null!;
        request.Arg3 = null!;
        request.ArgCount = 0;
        request.RegisterState = null;
        request.Expression = null!;
        engine._tailCallRequest = request;
    }
}
