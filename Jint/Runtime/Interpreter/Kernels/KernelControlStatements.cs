using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Iterator;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Expressions;
using Jint.Runtime.Interpreter.Statements;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// The names a block, a loop head or a catch clause of a kernel declares, in an environment of their own made as the
/// interpreter makes it; the frame's slots of those names point into it while it is the lexical environment.
/// </summary>
internal sealed class KernelScope
{
    internal readonly int[] _bindings;
    internal readonly Key[] _keys;
    private readonly Key[]? _slotNames;
    private readonly Binding[]? _slotTemplates;
    private readonly bool _constant;
    private readonly DeclarationCache? _declarations;
    private readonly bool _catch;

    // Whether a let loop head copies its bindings to a new environment for each iteration.
    internal bool _perIteration;

    // An environment with slots, kept for the next entry while nothing holds it.
    private DeclarativeEnvironment? _cached;
    private List<Key>? _keyList;

    internal List<Key> KeyList => _keyList ??= [.. _keys];

    /// <summary>The scope of a block that declares let and const names.</summary>
    internal KernelScope(int[] bindings, Key[] keys, JintBlockStatement.BlockState state)
    {
        _bindings = bindings;
        _keys = keys;
        _slotNames = state.SlotNames;
        _slotTemplates = state.SlotTemplates;
        _declarations = state.DeclarationCache;
    }

    /// <summary>The scope of a loop head or a catch parameter, in slots where nothing can capture it.</summary>
    internal KernelScope(int[] bindings, Key[] keys, bool constant, bool slots, bool catchScope)
    {
        _bindings = bindings;
        _keys = keys;
        _constant = constant;
        _catch = catchScope;
        if (slots && keys.Length > 0)
        {
            _slotNames = keys;
            _slotTemplates = new Binding[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                _slotTemplates[i] = catchScope
                    ? default
                    : constant
                        ? new Binding(null!, canBeDeleted: false, mutable: false, strict: true)
                        : new Binding(null!, canBeDeleted: false, mutable: true, strict: false);
            }
        }
    }

    internal bool HasSlots => _slotNames is not null;

    /// <summary>A new environment of the scope in <paramref name="outer"/>, its bindings uninitialized.</summary>
    internal DeclarativeEnvironment Create(Engine engine, Environment outer)
    {
        if (_slotNames is not null)
        {
            var cached = _cached;
            if (cached is not null && ReferenceEquals(cached._engine, engine))
            {
                _cached = null;
                cached._outerEnv = outer;
                return cached;
            }

            var environment = JintEnvironment.NewDeclarativeEnvironment(engine, outer, _catch);
            environment._slotNames = _slotNames;
            environment._slots = (Binding[]) _slotTemplates!.Clone();
            return environment;
        }

        var created = JintEnvironment.NewDeclarativeEnvironment(engine, outer, _catch);
        if (_declarations is { } declarations)
        {
            JintStatementList.BlockDeclarationInstantiation(created, declarations);
        }
        else
        {
            foreach (var key in _keys)
            {
                if (_constant)
                {
                    created.CreateImmutableBinding(key, strict: true);
                }
                else
                {
                    created.CreateMutableBinding(key, canBeDeleted: false);
                }
            }
        }

        return created;
    }

    /// <summary>Points the frame's slots of the scope's names into <paramref name="environment"/>.</summary>
    internal void Bind(KernelFrame frame, DeclarativeEnvironment environment)
    {
        var epoch = frame.Engine._envBindingInjectionEpoch;
        var slotted = environment._slotNames is not null && ReferenceEquals(environment._slotNames, _slotNames);
        var bindings = _bindings;
        for (var i = 0; i < bindings.Length; i++)
        {
            ref var slot = ref frame.Slots[bindings[i]];
            slot.Environment = slotted ? environment : null;
            slot.Index = slotted ? i : KernelFrame.Unresolved;
            slot.Epoch = epoch;
        }
    }

    /// <summary>Lets go of the frame's slots of the scope's names.</summary>
    internal void Unbind(KernelFrame frame)
    {
        var bindings = _bindings;
        for (var i = 0; i < bindings.Length; i++)
        {
            frame.Slots[bindings[i]] = default;
        }
    }

    /// <summary>The bindings of an environment with slots uninitialized again, for the next iteration.</summary>
    internal void Reset(DeclarativeEnvironment environment)
    {
        var slots = environment._slots!;
        var templates = _slotTemplates!;
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = templates[i];
        }
    }

    /// <summary>An environment with slots kept for the next entry, holding nothing of this one.</summary>
    internal void Park(DeclarativeEnvironment environment)
    {
        if (_slotNames is null)
        {
            return;
        }

        environment._outerEnv = null;
        Reset(environment);
        _cached = environment;
    }

    /// <summary>The environment entered: made, the lexical environment, and the frame's slots pointing into it.</summary>
    internal DeclarativeEnvironment Enter(KernelFrame frame, Environment outer)
    {
        var environment = Create(frame.Engine, outer);
        frame.Engine.UpdateLexicalEnvironment(environment);
        Bind(frame, environment);
        return environment;
    }

    /// <summary>The environment left: <paramref name="outer"/> the lexical environment again.</summary>
    internal void Leave(KernelFrame frame, DeclarativeEnvironment environment, Environment outer)
    {
        frame.Engine.UpdateLexicalEnvironment(outer);
        Unbind(frame);
        Park(environment);
    }
}

/// <summary>A block that declares let and const names, run in an environment of its own as JintBlockStatement runs it.</summary>
internal sealed class KernelScopeStatement : KernelStatement
{
    internal readonly KernelScope _scope;
    internal readonly KernelBlockStatement _block;

    public KernelScopeStatement(Statement statement, KernelScope scope, KernelBlockStatement block) : base(statement)
    {
        _scope = scope;
        _block = block;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        var outer = frame.Engine.ExecutionContext.LexicalEnvironment;
        var environment = _scope.Enter(frame, outer);
        try
        {
            return _block.Execute(frame);
        }
        finally
        {
            _scope.Leave(frame, environment, outer);
        }
    }
}

/// <summary><c>let</c> or <c>const</c>: each initializer's value, or undefined, initializes its binding in the current scope.</summary>
internal sealed class KernelLexicalDeclaration : KernelStatement
{
    internal readonly KernelName?[] _targets;
    private readonly DestructuringPattern?[] _patterns;
    internal readonly KernelExpression?[] _initializers;

    public KernelLexicalDeclaration(Statement statement, KernelName?[] targets, DestructuringPattern?[] patterns, KernelExpression?[] initializers) : base(statement)
    {
        _targets = targets;
        _patterns = patterns;
        _initializers = initializers;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        for (var i = 0; i < _initializers.Length; i++)
        {
            var initializer = _initializers[i];
            JsValue? value;
            double number = 0;
            if (initializer is null)
            {
                value = JsValue.Undefined;
            }
            else
            {
                value = initializer.Evaluate(frame, out number);
            }

            if (_targets[i] is { } target)
            {
                Initialize(frame, target, value, number);
            }
            else
            {
                var context = frame.Context;
                DestructuringPatternAssignmentExpression.ProcessPatterns(context, _patterns[i]!, KernelExpression.Box(value, number), frame.Engine.ExecutionContext.LexicalEnvironment, checkPatternPropertyReference: true);
            }
        }

        return Normal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Initialize(KernelFrame frame, KernelName target, JsValue? value, double number)
    {
        ref var binding = ref KernelSlots.Find(frame, target._binding, out _);
        if (!Unsafe.IsNullRef(ref binding))
        {
            if (value is null)
            {
                Binding.StoreUnboxedNumber(ref binding, number);
            }
            else
            {
                binding = binding.ChangeValue(value);
            }

            return;
        }

        InitializeByName(frame, target, KernelExpression.Box(value, number));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeByName(KernelFrame frame, KernelName target, JsValue value)
        => frame.Engine.ExecutionContext.LexicalEnvironment.InitializeBinding(target._name.Key, value, DisposeHint.Normal);
}

/// <summary>A <c>while</c> or <c>do</c>-<c>while</c> loop, completing as JintWhileStatement and JintDoWhileStatement complete.</summary>
internal sealed class KernelWhileStatement : KernelStatement
{
    internal readonly KernelExpression _test;
    internal readonly KernelStatement _body;
    internal readonly string[]? _labels;
    internal readonly bool _doWhile;

    public KernelWhileStatement(Statement statement, KernelExpression test, KernelStatement body, string[]? labels, bool doWhile) : base(statement)
    {
        _test = test;
        _body = body;
        _labels = labels;
        _doWhile = doWhile;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        var test = _test;
        var body = _body;
        if (_doWhile)
        {
            while (true)
            {
                var outcome = KernelLoops.Outcome(this, frame, body.Execute(frame), _labels);
                if (outcome != KernelLoops.Next)
                {
                    return outcome == KernelLoops.Done ? Normal : Abrupt;
                }

                if (test.Test(frame) != 1)
                {
                    return Normal;
                }
            }
        }

        while (test.Test(frame) == 1)
        {
            var outcome = KernelLoops.Outcome(this, frame, body.Execute(frame), _labels);
            if (outcome != KernelLoops.Next)
            {
                return outcome == KernelLoops.Done ? Normal : Abrupt;
            }
        }

        return Normal;
    }
}

/// <summary>What an iteration statement does with the completion of its body.</summary>
internal static class KernelLoops
{
    internal const int Next = 0;
    internal const int Done = 1;
    internal const int Leave = 2;

    /// <summary>
    /// Next for a normal completion or a continue of this loop, Done for a break of it, each cleared from the frame, and
    /// Leave for any other abrupt completion, which is left there (https://tc39.es/ecma262/#sec-loopcontinues).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Outcome(KernelStatement loop, KernelFrame frame, int code, string[]? labels)
    {
        if (code is KernelStatement.Normal or KernelStatement.Continue)
        {
            return Next;
        }

        return code == KernelStatement.Break ? Done : AbruptOutcome(loop._statement, frame, labels);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AbruptOutcome(Statement loop, KernelFrame frame, string[]? labels)
    {
        var completion = frame.Abrupt;
        if (completion.Type == CompletionType.Continue && labels is not null && Array.IndexOf(labels, completion.Target) >= 0)
        {
            frame.Abrupt = default;
            return Next;
        }

        if (completion.Type == CompletionType.Break && string.Equals(completion.Target, loop.LabelSet?.Name, StringComparison.Ordinal))
        {
            frame.Abrupt = default;
            return Done;
        }

        return Leave;
    }
}

/// <summary>What compiled code does with an abrupt completion out of a loop or switch's statement.</summary>
internal static class KernelTargets
{
    /// <summary>0 to continue <paramref name="target"/>, 1 to complete it normally, 2 to leave it with the completion.</summary>
    internal static int Outcome(KernelStatement target, KernelFrame frame)
    {
        if (target is KernelWhileStatement loop)
        {
            var outcome = KernelLoops.Outcome(loop, frame, KernelStatement.Abrupt, loop._labels);
            return outcome == KernelLoops.Next ? 0 : outcome == KernelLoops.Done ? 1 : 2;
        }

        var completion = frame.Abrupt;
        if (completion.Type == CompletionType.Break && string.Equals(completion.Target, target._statement.LabelSet?.Name, StringComparison.Ordinal))
        {
            frame.Abrupt = default;
            return 1;
        }

        return 2;
    }

    /// <summary>Whether a case's selector matches the discriminant, as JintSwitchBlock compares them.</summary>
    internal static bool Matches(JsValue selector, JsValue input) => selector == input;

    /// <summary>A let or const binding in a slot of the body's environment initialized.</summary>
    internal static void InitializeLocal(Binding[] slots, int index, JsValue? value, double number)
    {
        ref var binding = ref slots[index];
        if (value is null)
        {
            Binding.StoreUnboxedNumber(ref binding, number);
        }
        else
        {
            binding = binding.ChangeValue(value);
        }
    }
}

/// <summary>A labelled statement: a break naming its label completes it normally.</summary>
internal sealed class KernelLabeledStatement : KernelStatement
{
    internal readonly KernelStatement _body;
    private readonly string _label;

    public KernelLabeledStatement(LabeledStatement statement, KernelStatement body) : base(statement)
    {
        _body = body;
        _label = statement.Label.Name;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        var code = _body.Execute(frame);
        if (code == Abrupt && frame.Abrupt.Type == CompletionType.Break && string.Equals(frame.Abrupt.Target, _label, StringComparison.Ordinal))
        {
            frame.Abrupt = default;
            return Normal;
        }

        return code;
    }
}

/// <summary>A <c>break</c> or <c>continue</c> naming a label: its completion left in the frame.</summary>
internal sealed class KernelLabeledJumpStatement : KernelStatement
{
    private readonly CompletionType _type;

    public KernelLabeledJumpStatement(Statement statement, bool isBreak) : base(statement)
    {
        _type = isBreak ? CompletionType.Break : CompletionType.Continue;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        frame.Abrupt = new Completion(_type, JsEmpty.Instance, _statement);
        return Abrupt;
    }
}

/// <summary>A switch whose cases declare nothing lexically, run as JintSwitchBlock runs one.</summary>
internal sealed class KernelSwitchStatement : KernelStatement
{
    internal readonly KernelExpression _discriminant;
    internal readonly KernelExpression?[] _tests;
    internal readonly KernelBlockStatement[] _consequents;

    public KernelSwitchStatement(Statement statement, KernelExpression discriminant, KernelExpression?[] tests, KernelBlockStatement[] consequents) : base(statement)
    {
        _discriminant = discriminant;
        _tests = tests;
        _consequents = consequents;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        var input = KernelExpression.Box(_discriminant.Evaluate(frame, out var number), number);
        var tests = _tests;
        var hit = false;
        var defaultIndex = -1;
        var i = 0;
        while (true)
        {
            for (; i < tests.Length; i++)
            {
                if (!hit)
                {
                    var test = tests[i];
                    if (test is null)
                    {
                        defaultIndex = i;
                        continue;
                    }

                    var selector = KernelExpression.Box(test.Evaluate(frame, out var selectorNumber), selectorNumber);
                    if (selector != input)
                    {
                        continue;
                    }

                    hit = true;
                }

                var code = _consequents[i].Execute(frame);
                if (code != Normal)
                {
                    return Complete(frame, code);
                }
            }

            if (hit || defaultIndex < 0)
            {
                return Normal;
            }

            hit = true;
            i = defaultIndex;
        }
    }

    private int Complete(KernelFrame frame, int code)
    {
        if (code == Break)
        {
            return Normal;
        }

        if (code == Abrupt && frame.Abrupt.Type == CompletionType.Break && string.Equals(frame.Abrupt.Target, _statement.LabelSet?.Name, StringComparison.Ordinal))
        {
            frame.Abrupt = default;
            return Normal;
        }

        return code;
    }
}

/// <summary>A try statement, run as JintTryStatement runs one in a frame that cannot suspend.</summary>
internal sealed class KernelTryStatement : KernelStatement
{
    internal readonly KernelStatement _block;
    private readonly KernelScope? _scope;
    private readonly Node? _parameter;
    internal readonly KernelStatement? _handler;
    internal readonly KernelStatement? _finalizer;

    // Whether a break or continue in the block or the catch clause leaves the statement: body code then runs it as a node.
    internal readonly bool _jumpsOut;

    public KernelTryStatement(TryStatement statement, KernelStatement block, KernelScope? scope, KernelStatement? handler, KernelStatement? finalizer, bool jumpsOut) : base(statement)
    {
        _block = block;
        _scope = scope;
        _parameter = statement.Handler?.Param;
        _handler = handler;
        _finalizer = finalizer;
        _jumpsOut = jumpsOut;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        var code = _block.Execute(frame);
        if (code == Abrupt && _handler is not null)
        {
            switch (EnterCatch(frame, out var outer, out var environment))
            {
                case Normal:
                    try
                    {
                        code = _handler.Execute(frame);
                    }
                    finally
                    {
                        LeaveCatch(frame, outer, environment);
                    }

                    break;
                case Abrupt:
                    code = Abrupt;
                    break;
            }
        }

        if (_finalizer is null)
        {
            return code;
        }

        // A normal completion of the finally block keeps the block's completion and the value it returns.
        var completion = frame.Abrupt;
        var returnValue = frame.ReturnValue;
        var returnNumber = frame.ReturnNumber;
        frame.Abrupt = default;
        var finalized = _finalizer.Execute(frame);
        if (finalized != Normal)
        {
            // The finally block's completion replaces the block's.
            return finalized;
        }

        frame.Abrupt = completion;
        frame.ReturnValue = returnValue;
        frame.ReturnNumber = returnNumber;
        return code;
    }

    /// <summary>
    /// The catch clause entered for a throw completion of the block (https://tc39.es/ecma262/#sec-runtime-semantics-catchclauseevaluation):
    /// Normal to run it, Abrupt when binding the parameter threw, Break when the completion is not a throw.
    /// </summary>
    internal int EnterCatch(KernelFrame frame, out Environment? outer, out DeclarativeEnvironment? environment)
    {
        outer = null;
        environment = null;
        if (frame.Abrupt.Type != CompletionType.Throw)
        {
            return Break;
        }

        var thrown = frame.Abrupt.Value;
        frame.Abrupt = default;
        var scope = _scope;
        if (scope is null)
        {
            return Normal;
        }

        var engine = frame.Engine;
        outer = engine.ExecutionContext.LexicalEnvironment;
        environment = scope.Create(engine, outer);
        engine.UpdateLexicalEnvironment(environment);
        if (scope.HasSlots)
        {
            environment._slots![0] = new Binding(thrown, canBeDeleted: false, mutable: true, strict: false);
        }
        else
        {
            try
            {
                _parameter.BindingInitialization(frame.Context, thrown, environment);
            }
            catch (Exception ex) when (ex is JintException && JintStatementList.ShouldCatch(ex))
            {
                engine.UpdateLexicalEnvironment(outer);
                frame.Abrupt = JintStatementList.HandleStatementException(frame.Context, ex, _statement);
                return Abrupt;
            }
        }

        scope.Bind(frame, environment);
        return Normal;
    }

    /// <summary>The catch clause's environment left, where it has one.</summary>
    internal void LeaveCatch(KernelFrame frame, Environment? outer, DeclarativeEnvironment? environment)
    {
        if (environment is not null)
        {
            _scope!.Leave(frame, environment, outer!);
        }
    }
}

/// <summary>
/// A for-in or for-of loop, run as JintForInForOfStatement runs one in a frame that cannot suspend: an abrupt
/// completion of the body or the binding closes the iterator.
/// </summary>
internal sealed class KernelForInOfStatement : KernelStatement
{
    private readonly bool _enumerate;
    private readonly KernelExpression _right;
    internal readonly KernelStatement _body;
    private readonly string[]? _labels;
    private readonly KernelScope? _scope;
    private readonly KernelName? _target;
    private readonly DestructuringPattern? _pattern;
    private readonly bool _var;
    private readonly Expression? _member;
    private JintExpression? _targetHandler;

    public KernelForInOfStatement(
        Statement statement,
        bool enumerate,
        KernelExpression right,
        KernelStatement body,
        string[]? labels,
        KernelScope? scope,
        KernelName? target,
        DestructuringPattern? pattern,
        Expression? member,
        bool var) : base(statement)
    {
        _enumerate = enumerate;
        _right = right;
        _body = body;
        _labels = labels;
        _scope = scope;
        _target = target;
        _pattern = pattern;
        _member = member;
        _var = var;
    }

    internal override int Execute(KernelFrame frame)
    {
        _runs++;
        RunChecks(frame);
        var engine = frame.Engine;
        var value = KernelExpression.Box(_right.Evaluate(frame, out var number), number);
        IteratorInstance iterator;
        if (_enumerate)
        {
            if (value.IsNullOrUndefined())
            {
                return Normal;
            }

            iterator = new IteratorInstance.ForInIterator(engine, TypeConverter.ToObject(engine.Realm, value));
        }
        else
        {
            // An array whose iterator is the original one is stepped by index, as that iterator steps it.
            JsValue? iteratorMethod = null;
            if (value is JsArray array && array.HasOriginalIterator(out iteratorMethod))
            {
                return Iterate(frame, null, array);
            }

            if (!value.TryGetIteratorFromRead(engine.Realm, iteratorMethod, out var found))
            {
                Throw.TypeError(engine.Realm, "The value is not iterable");
            }

            iterator = found!;
        }

        return Iterate(frame, iterator, null);
    }

    /// <summary>
    /// The loop over <paramref name="iterator"/>, or over <paramref name="array"/> stepped as its array iterator steps
    /// (https://tc39.es/ecma262/#sec-createarrayiterator), closed through an iterator made where it stopped.
    /// </summary>
    private int Iterate(KernelFrame frame, IteratorInstance? iterator, JsArray? array)
    {
        var engine = frame.Engine;
        var scope = _scope;
        var outer = scope is null ? null : engine.ExecutionContext.LexicalEnvironment;
        DeclarativeEnvironment? environment = null;
        var completionType = CompletionType.Normal;
        var close = false;
        uint position = 0;
        try
        {
            while (true)
            {
                close = false;
                JsValue? next;
                if (array is not null)
                {
                    if (position >= array.GetLength())
                    {
                        return Normal;
                    }

                    next = array.GetElement(position);
                    position++;
                }
                else if (!iterator!.TryStepValue(out next))
                {
                    return Normal;
                }

                close = true;
                if (scope is not null)
                {
                    // A fresh binding for each iteration; with slots, nothing can tell one reset environment from a new one.
                    if (environment is null || !scope.HasSlots)
                    {
                        environment = scope.Create(engine, outer!);
                    }
                    else
                    {
                        scope.Reset(environment);
                    }

                    engine.UpdateLexicalEnvironment(environment);
                    scope.Bind(frame, environment);
                    if (_pattern is null)
                    {
                        if (scope.HasSlots)
                        {
                            environment.InitializeSlotBinding(0, next);
                        }
                        else
                        {
                            environment.InitializeBinding(_target!._name.Key, next, DisposeHint.Normal);
                        }
                    }
                    else
                    {
                        DestructuringPatternAssignmentExpression.ProcessPatterns(frame.Context, _pattern, next, environment, checkPatternPropertyReference: true);
                    }
                }
                else if (_target is not null)
                {
                    Store(frame, _target, next);
                }
                else if (_pattern is not null)
                {
                    DestructuringPatternAssignmentExpression.ProcessPatterns(frame.Context, _pattern, next, null, checkPatternPropertyReference: !_var);
                }
                else
                {
                    StoreMember(frame, next);
                }

                var code = _body.Execute(frame);
                if (scope is not null)
                {
                    engine.UpdateLexicalEnvironment(outer!);
                }

                var outcome = KernelLoops.Outcome(this, frame, code, _labels);
                if (outcome == KernelLoops.Next)
                {
                    continue;
                }

                if (outcome == KernelLoops.Done)
                {
                    return Normal;
                }

                completionType = code == Abrupt ? frame.Abrupt.Type : CompletionType.Normal;
                return code;
            }
        }
        catch (Exception exception) when (Leaving(exception, out completionType))
        {
            throw;
        }
        finally
        {
            if (scope is not null)
            {
                engine.UpdateLexicalEnvironment(outer!);
                scope.Unbind(frame);
            }

            if (close)
            {
                if (array is null)
                {
                    iterator!.Close(completionType);
                }
                else if (engine._builtinIteratorChainMayHaveReturn && !array.OriginalIteratorInheritsNoReturnMethod)
                {
                    array.OriginalIteratorPrototype.ConstructValuesAt(array, position).Close(completionType);
                }
            }

            if (environment is not null && scope!.HasSlots)
            {
                scope.Park(environment);
            }
        }
    }

    /// <summary>The filter that records the completion an exception leaves the loop with; it never catches.</summary>
    private static bool Leaving(Exception exception, out CompletionType completionType)
    {
        completionType = exception is GeneratorReturnException ? CompletionType.Return : CompletionType.Throw;
        return false;
    }

    /// <summary>The step's value put to a name, as PutValue puts it.</summary>
    private void Store(KernelFrame frame, KernelName target, JsValue value)
    {
        ref var binding = ref KernelSlots.Find(frame, target._binding, out _);
        if (!Unsafe.IsNullRef(ref binding) && binding.IsAssignable)
        {
            binding = binding.ChangeMutableValue(value);
            return;
        }

        PutThroughReference(frame, (_targetHandler ??= new JintIdentifierExpression((Identifier) target._expression)), value);
    }

    private void StoreMember(KernelFrame frame, JsValue value)
        => PutThroughReference(frame, (_targetHandler ??= new JintMemberExpression((MemberExpression) _member!)), value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PutThroughReference(KernelFrame frame, JintExpression handler, JsValue value)
    {
        var engine = frame.Engine;
        if (handler.Evaluate(frame.Context) is not Reference reference)
        {
            Throw.ReferenceError(engine.Realm, "Invalid left-hand side in assignment");
            return;
        }

        engine.PutValue(reference, value);
        engine._referencePool.Return(reference);
    }
}
