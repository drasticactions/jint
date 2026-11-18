using System.Runtime.CompilerServices;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter;
using Jint.Runtime.Interpreter.Expressions;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Native;

/// <summary>
/// Deferred closures: a statement <c>o.name = function () { ... };</c> that adds a property to an object under
/// construction stores a lazy slot instead of a new function, and the function is created on the first read of
/// the property. A constructor that installs its methods this way (<c>this.start = function () { ... }</c>, a
/// dozen times) then allocates only the closures its callers read.
/// </summary>
public sealed partial class JsObject
{
    // How far back from the newest slot a store looks for the sentinel of an earlier store from the same
    // environment. Methods are usually installed one after another, so the sentinel is in the previous slot.
    private const int DeferredClosureSearchDepth = 8;

    /// <summary>
    /// Adds <paramref name="property"/> to this object as a lazy slot that will hold a closure of
    /// <paramref name="definition"/> over <paramref name="environment"/>, when the store <c>[[Set]]</c> would make
    /// is the creation of a plain data property on a constructor's <c>this</c>: this object is extensible,
    /// in shape mode and still being built, has no such own property, and no link of its prototype chain owns
    /// the name (so no setter runs and no read-only property refuses the store). Returns false, having changed
    /// nothing, otherwise; the caller then creates the function and stores it the ordinary way. A caller that has
    /// proved the prototype chain part itself (<paramref name="prototypeChainChecked"/>) skips that walk.
    /// <para>
    /// Creating a function expression has no side effects, and the closure captures the environment itself rather
    /// than its contents, so creating it at the first read gives the same function the statement would have. The
    /// realm and the script or module the function belongs to are the ones active at the store, kept with the
    /// environment.
    /// </para>
    /// </summary>
    internal bool TryAddDeferredClosure(
        JsString property,
        JintFunctionDefinition definition,
        Environment environment,
        ref DeferredClosureTransition? transition,
        bool prototypeChainChecked = false)
    {
        const InternalTypes Mask = InternalTypes.PlainObject | InternalTypes.BuiltinShapeMode | InternalTypes.ShapeMode | InternalTypes.ShapeBuilding;
        if ((_type & Mask) != (InternalTypes.PlainObject | InternalTypes.ShapeMode | InternalTypes.ShapeBuilding)
            || !_initialized
            || !Extensible)
        {
            return false;
        }

        Key key = property.ToString();
        var shape = _shape!;
        if (shape.TryGetSlot(in key, out _) || Shape.IsIntegerIndexLikeKey(key.Name))
        {
            return false;
        }

        // The walk [[Set]] makes before it creates the property (ObjectInstance.SetOnPrototypeChain), declining
        // every link whose [[GetOwnProperty]] or [[Set]] is not the ordinary one.
        for (var link = prototypeChainChecked ? null : GetPrototypeOf(); link is not null; link = link.GetPrototypeOf())
        {
            if ((link._type & InternalTypes.PlainObject) == InternalTypes.Empty
                && !Jint.Native.Function.Function.IsConstructorPrototype(link))
            {
                return false;
            }

            if (!ReferenceEquals(link.GetOwnProperty(property), PropertyDescriptor.Undefined))
            {
                return false;
            }
        }

        var engine = _engine;
        var origin = FunctionOrigin.Of(engine.Realm, engine.GetActiveScriptOrModule());
        var sentinel = FindDeferredClosures(environment, origin) ?? new DeferredClosures(environment, origin);

        var slot = shape.SlotCount;
        var previous = sentinel.Definitions;
        var next = DeferredClosureTransition.Next(ref transition, previous, slot, definition);
        if (!TryShapeAdd(in key, sentinel))
        {
            return false;
        }

        sentinel.Definitions = next;
        _type |= InternalTypes.HasLazySlots;
        return true;
    }

    /// <summary>
    /// Reads a slot for the callee of a call <c>o.name(...)</c>: <see cref="GetSlotForRead"/>, except that the
    /// first call of a deferred closure whose body cannot reach its own function object
    /// (<see cref="JintFunctionDefinition.FunctionObjectUse.NotReachable"/>) gets a function made for that call
    /// alone, and the slot keeps deferring. A constructor's methods are mostly called once per object, if at
    /// all, so an object that lives long then holds no closure for them; a second call creates the slot's
    /// function as a read does.
    /// <para>
    /// The function the call runs is not observable: the body cannot name it, the callee value goes no further
    /// than the call, and what the engine keeps of a frame (the recursion count, a stack trace's frame name and
    /// location, the profilers' entries) is keyed by the definition, which it shares with the function a read
    /// creates. So the call behaves as if it had run the slot's function, which a later read creates and
    /// memoizes once as before. A debugger, which can show a frame's function, always gets the slot's.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue GetSlotForCall(int slot)
    {
        var value = GetSlot(slot);
        if (value is DeferredClosures closures)
        {
            return closures.CalleeForCall(this, slot);
        }

        if (value is LayoutSlots)
        {
            return MaterializeSlot(Unsafe.As<UnmaterializedSlots>(value), slot);
        }

        return value;
    }

    private DeferredClosures? FindDeferredClosures(Environment environment, FunctionOrigin origin)
    {
        if ((_type & InternalTypes.HasLazySlots) == InternalTypes.Empty)
        {
            return null;
        }

        var last = _shape!.SlotCount - 1;
        var stop = System.Math.Max(0, last - DeferredClosureSearchDepth + 1);
        for (var i = last; i >= stop; i--)
        {
            if (GetSlot(i) is DeferredClosures closures
                && ReferenceEquals(closures.Environment, environment)
                && ReferenceEquals(closures.Origin, origin))
            {
                return closures;
            }
        }

        return null;
    }

    /// <summary>
    /// The sentinel of an object's deferred closures from one environment: the environment, the function origin,
    /// and the definition of each slot's function by slot index. The definitions array is shared by every object
    /// built by the same sequence of stores (see <see cref="DeferredClosureTransition"/>) and never changes once
    /// built; a further store replaces it.
    /// </summary>
    internal sealed class DeferredClosures : UnmaterializedSlots
    {
        internal readonly Environment Environment;
        internal readonly FunctionOrigin Origin;
        internal JintFunctionDefinition?[]? Definitions;

        // The slots below 32 whose function has run once for a call without being created (see
        // GetSlotForCall). It fills the padding after JsValue's type field, so the sentinel is no larger.
        private uint _calledSlots;

        internal DeferredClosures(Environment environment, FunctionOrigin origin)
        {
            Environment = environment;
            Origin = origin;
        }

        /// <summary>
        /// The callee of a call of <paramref name="slot"/>, which holds this sentinel: a function for this call
        /// alone on the slot's first call when its body cannot reach it, the slot's materialized function
        /// otherwise. See <see cref="GetSlotForCall"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal JsValue CalleeForCall(JsObject owner, int slot)
        {
            var definition = Definitions![slot]!;
            var engine = owner._engine;
            var bit = slot < 32 ? 1u << slot : 0u;
            if ((_calledSlots & bit) == 0
                && bit != 0
                && !engine._isDebugMode
                && definition.GetFunctionObjectUse() == JintFunctionDefinition.FunctionObjectUse.NotReachable)
            {
                _calledSlots |= bit;
                return JintFunctionExpression.InstantiateDeferred(engine, definition, Environment, Origin);
            }

            return owner.MaterializeSlot(this, slot);
        }

        internal override JsValue Materialize(JsObject owner, int slot)
        {
            var definition = Definitions![slot];
            System.Diagnostics.Debug.Assert(definition is not null, "a slot holding the sentinel must have a definition");
            return JintFunctionExpression.InstantiateDeferred(owner._engine, definition!, Environment, Origin);
        }
    }

    /// <summary>
    /// One store site's memo of the definitions array it last produced: from <see cref="Previous"/>, the array
    /// the object had, a store at <see cref="Slot"/> gives <see cref="Result"/>. The site's definition is fixed,
    /// so the result depends on nothing else, and objects built the same way share every array. Immutable, so
    /// sites shared by engines on several threads may replace it freely.
    /// </summary>
    internal sealed class DeferredClosureTransition
    {
        // A site whose stores keep missing the memo (objects built in several ways) stops deferring after this
        // many misses, since each miss allocates an array.
        private const int MaxMisses = 16;

        private readonly JintFunctionDefinition?[]? Previous;
        private readonly int Slot;
        private readonly JintFunctionDefinition?[] Result;
        private readonly int Misses;

        private DeferredClosureTransition(JintFunctionDefinition?[]? previous, int slot, JintFunctionDefinition?[] result, int misses)
        {
            Previous = previous;
            Slot = slot;
            Result = result;
            Misses = misses;
        }

        /// <summary>Whether the site should still try to defer its closures.</summary>
        internal static bool Enabled(DeferredClosureTransition? transition) => transition is null || transition.Misses < MaxMisses;

        internal static JintFunctionDefinition?[] Next(
            ref DeferredClosureTransition? transition,
            JintFunctionDefinition?[]? previous,
            int slot,
            JintFunctionDefinition definition)
        {
            var memo = transition;
            if (memo is not null && ReferenceEquals(memo.Previous, previous) && memo.Slot == slot)
            {
                return memo.Result;
            }

            var result = new JintFunctionDefinition?[System.Math.Max(slot + 1, previous?.Length ?? 0)];
            previous?.CopyTo(result, 0);
            result[slot] = definition;
            transition = new DeferredClosureTransition(previous, slot, result, memo is null ? 0 : memo.Misses + 1);
            return result;
        }
    }
}
