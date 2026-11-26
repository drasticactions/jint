using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Expressions;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>
/// An expression of a loop kernel (see <see cref="LoopKernel"/>). A <em>pure</em> expression reads bindings,
/// elements and data properties and computes on primitives; it never runs script or host code and never throws, and
/// whenever it meets a case it does not handle it answers <see cref="Bail"/> before it has done anything observable,
/// so that the expression it belongs to can be evaluated again by the interpreter's own handler with exactly the
/// steps the specification gives it. Every other kernel expression evaluates its own operands once and completes
/// like the interpreter's handler; it never answers <see cref="Bail"/>.
/// </summary>
internal abstract class KernelExpression
{
    /// <summary>What a pure expression answers for a case it leaves to the interpreter.</summary>
    internal static readonly JsValue Bail = new BailValue();

    internal readonly Expression _expression;

    protected KernelExpression(Expression expression)
    {
        _expression = expression;
    }

    /// <summary>
    /// The value: null with <paramref name="number"/> set for a Number, or the value, which may also be a
    /// <see cref="JsNumber"/>, or <see cref="Bail"/> from a pure expression.
    /// </summary>
    internal abstract JsValue? Evaluate(KernelFrame frame, out double number);

    /// <summary>ToBoolean of the value: 1 or 0, or -1 for <see cref="Bail"/>.</summary>
    internal virtual int Test(KernelFrame frame)
    {
        var value = Evaluate(frame, out var number);
        if (value is null)
        {
            return number != 0 && !double.IsNaN(number) ? 1 : 0;
        }

        if (ReferenceEquals(value, Bail))
        {
            return -1;
        }

        return TypeConverter.ToBoolean(value) ? 1 : 0;
    }

    /// <summary>Evaluates the expression for its effects, its value unused.</summary>
    internal virtual void EvaluateDiscarded(KernelFrame frame) => Evaluate(frame, out _);

    /// <summary>The Number of a value an operand answered, or false when it is not a Number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsNumber(JsValue? value, ref double number)
    {
        if (value is null)
        {
            return true;
        }

        if ((value._type & (InternalTypes.Number | InternalTypes.Integer)) != InternalTypes.Empty)
        {
            number = Unsafe.As<JsNumber>(value)._value;
            return true;
        }

        return false;
    }

    /// <summary>A value as the interpreter hands it on: a raw Number boxed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue Box(JsValue? value, double number) => value ?? JsNumber.Create(number);

    private sealed class BailValue : JsValue
    {
        public BailValue() : base(Types.Empty)
        {
        }

        public override object? ToObject() => null;
    }
}

/// <summary>A Number literal.</summary>
internal sealed class KernelNumber : KernelExpression
{
    internal readonly double _value;

    public KernelNumber(Expression expression, double value) : base(expression)
    {
        _value = value;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = _value;
        return null;
    }
}

/// <summary>A literal other than a Number: a string, a boolean, null, or the undefined the name always reads.</summary>
internal sealed class KernelConstant : KernelExpression
{
    internal readonly JsValue _value;

    public KernelConstant(Expression expression, JsValue value) : base(expression)
    {
        _value = value;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        return _value;
    }
}

/// <summary>
/// A pure read of a name: the slot of a declarative environment the kernel resolved the name to when it was entered,
/// or a writable data property of the global object. Anything else (an uninitialized binding, a binding an
/// environment keeps by name, a with object, an accessor, a name a sloppy eval or an Annex B function declaration
/// may since have shadowed) answers <see cref="KernelExpression.Bail"/>.
/// </summary>
internal sealed class KernelName : KernelExpression
{
    internal readonly int _binding;
    internal readonly Environment.BindingName _name;

    // The global binding last read: valid while neither the global object's own properties nor the global lexical
    // declarations changed, as JintIdentifierExpression's global cache. Value writes change the descriptor in place.
    internal GlobalEnvironment? _cachedGlobalEnv;
    internal Descriptors.PropertyDescriptor? _cachedGlobalDescriptor;
    internal uint _cachedGlobalShapeVersion;
    internal int _cachedGlobalLexicalVersion;

    public KernelName(Identifier expression, int binding, Environment.BindingName name) : base(expression)
    {
        _binding = binding;
        _name = name;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        ref var slot = ref frame.Slots[_binding];
        var environment = slot.Environment;
        if (environment is not null && slot.Epoch == frame.Engine._envBindingInjectionEpoch)
        {
            var slots = environment._slots;
            if (slots is not null && (uint) slot.Index < (uint) slots.Length)
            {
                ref var binding = ref slots[slot.Index];
                if (binding.IsUnboxedNumber)
                {
                    number = binding.UnboxedNumber;
                    return null;
                }

                // An owned string is not plain, an arguments object carries the read hook: the interpreter's read marks both.
                number = 0;
                var value = binding.IsPlainValue ? binding.PeekValue : null;
                return value is not null && (value._type & InternalTypes.ReadHook) == InternalTypes.Empty ? value : Bail;
            }
        }

        if (slot.Index == KernelFrame.GlobalBinding && slot.Epoch == frame.Engine._envBindingInjectionEpoch)
        {
            number = 0;
            return ReadGlobal(frame);
        }

        return ReadUnresolved(frame, out number);
    }

    /// <summary>
    /// A name not read through a slot: resolved first when this run has not resolved it, or did before a binding was
    /// injected somewhere; then a slot, a global or Bail.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue? ReadUnresolved(KernelFrame frame, out double number)
    {
        number = 0;
        if (frame.NeedsResolving(_binding))
        {
            frame.Resolve(_binding);
            if (frame.Slots[_binding].Environment is not null)
            {
                return Evaluate(frame, out number);
            }
        }

        return frame.Slots[_binding].Index == KernelFrame.GlobalBinding ? ReadGlobal(frame) : Bail;
    }

    /// <summary>The plain value in the name's slot, one with a read hook included; null where no slot holds one.</summary>
    internal JsValue? PeekSlot(KernelFrame frame)
    {
        ref var slot = ref frame.Slots[_binding];
        if (slot.Environment is null || slot.Epoch != frame.Engine._envBindingInjectionEpoch)
        {
            if (!frame.NeedsResolving(_binding))
            {
                return null;
            }

            frame.Resolve(_binding);
            if (slot.Environment is null)
            {
                return null;
            }
        }

        var slots = slot.Environment._slots;
        if (slots is null || (uint) slot.Index >= (uint) slots.Length)
        {
            return null;
        }

        ref var binding = ref slots[slot.Index];
        return binding.IsPlainValue ? binding.PeekValue : null;
    }

    /// <summary>The Number a slot holds, unboxed or boxed; false for anything else, which <see cref="Evaluate"/> answers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadNumber(KernelFrame frame, out double number)
    {
        ref var slot = ref frame.Slots[_binding];
        var environment = slot.Environment;
        if (environment is not null && slot.Epoch == frame.Engine._envBindingInjectionEpoch)
        {
            var slots = environment._slots;
            if (slots is not null && (uint) slot.Index < (uint) slots.Length)
            {
                ref var binding = ref slots[slot.Index];
                if (binding.IsUnboxedNumber)
                {
                    number = binding.UnboxedNumber;
                    return true;
                }

                if (binding.PeekValue is { } value && (value._type & (InternalTypes.Number | InternalTypes.Integer)) != InternalTypes.Empty)
                {
                    number = Unsafe.As<JsNumber>(value)._value;
                    return true;
                }
            }
        }

        number = 0;
        return false;
    }

    // Where IsUnresolvable last found the name absent: the global environment and the versions it checked.
    private GlobalEnvironment? _absentGlobal;
    private int _absentLexicalVersion;
    private ObjectInstance[]? _absentChain;
    private uint[]? _absentVersions;

    /// <summary>
    /// Whether the name resolved to the global environment and neither its declarations nor any link of the global
    /// object's prototype chain has it: a reference to it is unresolvable. False where finding out could run anything.
    /// </summary>
    internal bool IsUnresolvable(KernelFrame frame)
    {
        ref var slot = ref frame.Slots[_binding];
        if (slot.Index != KernelFrame.GlobalBinding
            || slot.Epoch != frame.Engine._envBindingInjectionEpoch
            || frame.Global is not { _globalObject: { } globalObject } globalEnv)
        {
            return false;
        }

        if (ReferenceEquals(globalEnv, _absentGlobal) && globalEnv._lexicalMutations == _absentLexicalVersion && ChainHolds(globalObject))
        {
            return true;
        }

        _absentGlobal = null;
        if (globalEnv._declarativeRecord.HasBinding(_name.Key))
        {
            return false;
        }

        var links = new List<ObjectInstance>();
        for (ObjectInstance? link = globalObject; link is not null; link = link.GetPrototypeOf())
        {
            if ((link._type & (InternalTypes.ExoticGet | InternalTypes.OwnValueHook)) != InternalTypes.Empty
                || link.GetType().Assembly != typeof(KernelName).Assembly
                || !ReferenceEquals(link.GetOwnProperty(_name.Value), Descriptors.PropertyDescriptor.Undefined))
            {
                return false;
            }

            links.Add(link);
        }

        if (links.TrueForAll(link => ObjectInstance.VersionWitnessesOwnProperty(link, _name.Value)))
        {
            _absentChain = links.ToArray();
            _absentVersions = Array.ConvertAll(_absentChain, link => link._propertiesVersion);
            _absentLexicalVersion = globalEnv._lexicalMutations;
            _absentGlobal = globalEnv;
        }

        return true;
    }

    /// <summary>Whether the chain IsUnresolvable recorded starts at <paramref name="globalObject"/> and is unchanged.</summary>
    private bool ChainHolds(ObjectInstance globalObject)
    {
        var chain = _absentChain!;
        var versions = _absentVersions!;
        ObjectInstance? link = globalObject;
        for (var i = 0; i < chain.Length; i++)
        {
            if (!ReferenceEquals(link, chain[i]) || link._propertiesVersion != versions[i])
            {
                return false;
            }

            link = link.GetPrototypeOf();
        }

        return link is null;
    }

    private JsValue ReadGlobal(KernelFrame frame)
    {
        // The name resolved to the global environment at the current epoch (see ReadUnresolved).
        var globalEnv = frame.Global!;

        var descriptor = _cachedGlobalDescriptor;
        if (!ReferenceEquals(globalEnv, _cachedGlobalEnv)
            || globalEnv._global._propertiesVersion != _cachedGlobalShapeVersion
            || globalEnv._lexicalMutations != _cachedGlobalLexicalVersion)
        {
            descriptor = RememberGlobal(globalEnv);
            if (descriptor is null)
            {
                return Bail;
            }
        }

        var value = descriptor!.Value;
        return value is not null && (value._type & InternalTypes.ReadHook) == InternalTypes.Empty ? value : Bail;
    }

    /// <summary>JintIdentifierExpression.TryRememberGlobalBinding: a writable data property of the global object itself.</summary>
    private Descriptors.PropertyDescriptor? RememberGlobal(GlobalEnvironment globalEnv)
    {
        _cachedGlobalEnv = null;
        _cachedGlobalDescriptor = null;
        if (globalEnv._globalObject is not { } globalObject || globalEnv._declarativeRecord.HasBinding(_name.Key))
        {
            return null;
        }

        Descriptors.PropertyDescriptor? descriptor = null;
        globalObject._properties?.TryGetValue(_name.Key, out descriptor);
        if (descriptor is null && (globalObject._type & InternalTypes.BuiltinShapeMode) != InternalTypes.Empty)
        {
            var shaped = globalObject.GetOwnProperty(_name.Key);
            if (shaped != Descriptors.PropertyDescriptor.Undefined)
            {
                descriptor = shaped;
            }
        }

        if (descriptor is null
            || !descriptor.IsDataDescriptor()
            || !descriptor.Writable
            || (descriptor._flags & Descriptors.PropertyFlag.CustomJsValue) != Descriptors.PropertyFlag.None)
        {
            return null;
        }

        _cachedGlobalEnv = globalEnv;
        _cachedGlobalDescriptor = descriptor;
        _cachedGlobalShapeVersion = globalObject._propertiesVersion;
        _cachedGlobalLexicalVersion = globalEnv._lexicalMutations;
        return descriptor;
    }
}

/// <summary>
/// A pure <c>this</c> (https://tc39.es/ecma262/#sec-resolvethisbinding): the bound this value of the function
/// environment the run is in, past the environments of blocks and arrows; anything else answers Bail.
/// </summary>
internal sealed class KernelThis : KernelExpression
{
    public KernelThis(Expression expression) : base(expression)
    {
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        return Read(frame.Start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue Read(Environment? environment)
    {
        if (environment is FunctionEnvironment function && function.BoundThis is { } bound)
        {
            return bound;
        }

        return ReadOuter(environment);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsValue ReadOuter(Environment? environment)
    {
        for (var e = environment; e is DeclarativeEnvironment and not ModuleEnvironment; e = e._outerEnv)
        {
            if (e is FunctionEnvironment function && !function.HasLexicalThis)
            {
                return function.BoundThis ?? Bail;
            }
        }

        return Bail;
    }
}

/// <summary>
/// A pure expression in a place that needs its value: a <see cref="KernelExpression.Bail"/> is answered by evaluating
/// the expression with the interpreter's handler, which repeats nothing observable, as the pure evaluation did nothing
/// observable.
/// </summary>
internal sealed class KernelRoot : KernelExpression
{
    internal readonly KernelExpression _pure;
    private JintExpression? _handler;

    // An expression that hands most of its evaluations back (a read of a host object's property, say) stops trying:
    // each hand-back costs the evaluation the interpreter repeats. A hand-back lowers the score by more than an
    // evaluation that completes raises it.
    private int _score;
    internal bool _interpretOnly;
    private const int ScoreCeiling = 1024;
    private const int BailCost = 16;
    private const int ScoreFloor = -64;

    // How KernelOperand reads the pure expression where a Number is wanted.
    internal readonly byte _pureKind;

    public KernelRoot(KernelExpression pure) : base(pure._expression)
    {
        _pure = pure;
        _pureKind = KernelOperand.KindOf(pure);
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        if (!_interpretOnly)
        {
            // Arithmetic of a name and a literal or two names: anything but two Numbers makes it Bail, which its nodes
            // then answer again.
            if (_pureKind == KernelOperand.Arithmetic && KernelOperand.TryNumber(_pure, KernelOperand.Arithmetic, frame, out number))
            {
                Completed();
                return null;
            }

            var value = _pure.Evaluate(frame, out number);
            if (!ReferenceEquals(value, Bail))
            {
                Completed();
                return value;
            }

            HandedBack();
        }

        number = 0;
        return Interpret(frame);
    }

    internal override int Test(KernelFrame frame)
    {
        if (!_interpretOnly)
        {
            var result = _pure.Test(frame);
            if (result >= 0)
            {
                Completed();
                return result;
            }

            HandedBack();
        }

        return InterpretTest(frame) ? 1 : 0;
    }

    /// <summary>ToBoolean of the value, as the interpreter's handler evaluates it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal bool InterpretTest(KernelFrame frame) => (_handler ??= JintExpression.Build(_expression)).GetBooleanValue(frame.Context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Completed()
    {
        if (_score < ScoreCeiling)
        {
            _score++;
        }
    }

    private void HandedBack()
    {
        _score -= BailCost;
        if (_score < ScoreFloor)
        {
            _interpretOnly = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue Interpret(KernelFrame frame) => (_handler ??= JintExpression.Build(_expression)).GetValue(frame.Context);
}

/// <summary>A function or arrow expression: its handler's closure, named as a declaration or assignment names it.</summary>
internal sealed class KernelFunction : KernelExpression
{
    private readonly JintExpression _handler;
    private readonly JsString? _name;

    public KernelFunction(Expression expression, string? name) : base(expression)
    {
        _handler = JintExpression.Build(expression);
        _name = name is null ? null : new JsString(name);
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        var value = _handler.GetValue(frame.Context);
        if (_name is not null)
        {
            ((Native.Function.Function) value).SetFunctionName(_name);
        }

        return value;
    }
}

/// <summary>An expression the kernel leaves to the interpreter's handler.</summary>
internal sealed class KernelInterpreted : KernelExpression
{
    internal readonly JintExpression _handler;
    internal readonly bool _canDiscard;

    public KernelInterpreted(Expression expression) : base(expression)
    {
        _handler = JintExpression.Build(expression);
        _canDiscard = _handler.HasDiscardFastPath;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        return _handler.GetValue(frame.Context);
    }

    internal override int Test(KernelFrame frame) => _handler.GetBooleanValue(frame.Context) ? 1 : 0;

    internal override void EvaluateDiscarded(KernelFrame frame)
    {
        // As JintExpressionStatement evaluates an expression whose completion value nobody reads.
        if (_canDiscard)
        {
            _handler.EvaluateAndDiscard(frame.Context);
        }
        else
        {
            _handler.GetValue(frame.Context);
        }
    }
}

/// <summary>A comma expression: each operand evaluated in order, the last one's value its value.</summary>
internal sealed class KernelSequence : KernelExpression
{
    internal readonly KernelExpression[] _expressions;

    public KernelSequence(Expression expression, KernelExpression[] expressions) : base(expression)
    {
        _expressions = expressions;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        var expressions = _expressions;
        var last = expressions.Length - 1;
        for (var i = 0; i < last; i++)
        {
            expressions[i].EvaluateDiscarded(frame);
        }

        return expressions[last].Evaluate(frame, out number);
    }

    internal override void EvaluateDiscarded(KernelFrame frame)
    {
        foreach (var expression in _expressions)
        {
            expression.EvaluateDiscarded(frame);
        }
    }
}

/// <summary>The last syntax element the interpreter's reads of operands leave, for errors a kernel node raises after them.</summary>
internal static class KernelLocation
{
    /// <summary>The node the interpreter's read of <paramref name="operand"/> leaves as the last syntax element, or null where it restores the one before.</summary>
    internal static Node? After(Expression? operand) => operand switch
    {
        Identifier or ThisExpression or MemberExpression => operand,
        Literal literal => JintLiteralExpression.ConvertToJsValue(literal) is null ? operand : null,
        NonUpdateUnaryExpression { Operator: Operator.UnaryNegation, Argument: Literal } => null,
        NonUpdateUnaryExpression or LogicalExpression { Operator: Operator.NullishCoalescing } => operand,
        ChainExpression { Expression: MemberExpression member } => member,
        _ => null,
    };

    /// <summary>The last syntax element after reading <paramref name="operands"/> in order, starting from <paramref name="start"/>.</summary>
    internal static Node Fold(Node start, IEnumerable<Expression?> operands)
    {
        var location = start;
        foreach (var operand in operands)
        {
            location = After(operand) ?? location;
        }

        return location;
    }
}
