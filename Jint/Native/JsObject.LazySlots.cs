using System.Runtime.CompilerServices;
using Jint.Native.Object;
using Jint.Runtime;

namespace Jint.Native;

/// <summary>
/// Lazy layout slots: the storage side of <see cref="JsObjectLayout.Builder.AddLazy"/>.
/// </summary>
public sealed partial class JsObject
{
    /// <summary>
    /// Reads a slot the way every path that observes a slot's <em>value</em> must: materializing it first if
    /// it is still a lazy layout slot whose factory has not run. Callers that only need keys, flags or slot
    /// counts must keep using the raw accessors — an existence question never runs a factory.
    /// <para>
    /// The check is two exact type tests against a value the caller has already loaded, and the branch is
    /// perfectly predicted (a given slot is a sentinel at most once in its life). The hot interpreter lanes
    /// avoid even that by gating on <see cref="InternalTypes.HasLazySlots"/> and routing here only for the
    /// objects that can actually have one.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue GetSlotForRead(int slot)
    {
        // Two exact type tests rather than one test of the abstract base, which would call the cast helper.
        var value = GetSlot(slot);
        if (value is LayoutSlots or DeferredClosures)
        {
            return MaterializeSlot(Unsafe.As<UnmaterializedSlots>(value), slot);
        }

        return value;
    }

    /// <summary>
    /// Produces one lazy slot's value (runs its factory, or creates its closure) and memoizes it into the slot,
    /// so the slot itself is the memo and every read lane sees the value immediately (nothing anywhere caches a
    /// slot's value — the inline caches cache the slot <em>index</em> and re-read the slot on every hit).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue MaterializeSlot(UnmaterializedSlots sentinel, int slot)
    {
        var value = sentinel.Materialize(this, slot);

        // A factory is morally a getter body, so it may have mutated this very object while it ran: written
        // the property being read, deleted an unrelated one (deopting to a dictionary), or frozen the object.
        // Memoize only while the slot is still the one this read started from; otherwise the mutation stands
        // and this read merely returns the factory's value, which is exactly how a JavaScript getter that
        // mutates its own object behaves.
        if ((_type & InternalTypes.ShapeMode) != InternalTypes.Empty
            && slot < _shape!.SlotCount
            && ReferenceEquals(GetSlot(slot), sentinel))
        {
            SetSlot(slot, value);
            ClearLazyFlagIfFullyMaterialized();
        }

        return value;
    }

    /// <summary>
    /// Drops <see cref="InternalTypes.HasLazySlots"/> once no slot holds a sentinel any more, returning the
    /// object to the plain shape read lanes — from then on it is indistinguishable from an object literal of
    /// the same layout. O(slot count), bounded by the maximum a hidden class can describe, and paid only
    /// on the materializations themselves.
    /// </summary>
    /// <seealso cref="Shape.MaxShapeProperties"/>
    private void ClearLazyFlagIfFullyMaterialized()
    {
        var slotCount = _shape!.SlotCount;
        for (var i = 0; i < slotCount; i++)
        {
            if (GetSlot(i) is UnmaterializedSlots)
            {
                return;
            }
        }

        _type &= ~InternalTypes.HasLazySlots;
    }

    /// <summary>
    /// Installs one shared sentinel in every lazy slot of <paramref name="layout"/> and flags the object, so
    /// the read lanes know to check. One allocation per object regardless of how many lazy slots it has.
    /// </summary>
    internal void InstallLazySlots(JsObjectLayout layout, object? lazySlotState)
    {
        var sentinel = new LayoutSlots(layout, lazySlotState);
        var count = layout.Count;
        for (var i = 0; i < count; i++)
        {
            if (layout.GetFactory(i) is not null)
            {
                SetSlot(i, sentinel);
            }
        }

        _type |= InternalTypes.HasLazySlots;
    }

    /// <summary>
    /// The placeholder a lazy slot holds until its value is produced, carrying everything that needs: a layout
    /// slot's factory and state (<see cref="LayoutSlots"/>), or the environment and definitions of the function
    /// expressions assigned to the object (<see cref="DeferredClosures"/>). One instance is shared by the lazy
    /// slots it describes, so an object with four lazy members still costs one extra allocation.
    /// <para>
    /// Never observable: it is <see cref="Types.Empty"/>, like <see cref="JsEmpty"/>, and every path that
    /// observes a slot's value goes through <see cref="GetSlotForRead"/>, the descriptor view
    /// (<c>SlotPropertyDescriptor</c>) or the dictionary deopt, each of which resolves it first.
    /// </para>
    /// </summary>
    internal abstract class UnmaterializedSlots : JsValue
    {
        private protected UnmaterializedSlots() : base(Types.Empty)
        {
        }

        /// <summary>
        /// Produces the value of <paramref name="slot"/> of <paramref name="owner"/>, the slot index the sentinel
        /// was stored at (still the index after a deopt has moved the property into a dictionary).
        /// </summary>
        internal abstract JsValue Materialize(JsObject owner, int slot);

        public override object? ToObject() => null;

        public override string ToString() => "[unmaterialized lazy slot]";
    }

    /// <summary>
    /// The sentinel of a host layout's lazy slots: the layout (which owns the per-slot factories) and the
    /// per-object state.
    /// </summary>
    internal sealed class LayoutSlots : UnmaterializedSlots
    {
        internal readonly JsObjectLayout Layout;
        internal readonly object? State;

        internal LayoutSlots(JsObjectLayout layout, object? state)
        {
            Layout = layout;
            State = state;
        }

        internal override JsValue Materialize(JsObject owner, int slot)
        {
            var factory = Layout.GetFactory(slot);
            System.Diagnostics.Debug.Assert(factory is not null, "a slot holding the sentinel must have a factory");
            return factory!(owner, State) ?? Undefined;
        }
    }
}
