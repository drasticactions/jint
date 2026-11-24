using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Native.TypedArray;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Environments;
using Jint.Runtime.Interop;

namespace Jint.Runtime.Interpreter.Expressions;

/// <summary>
/// http://www.ecma-international.org/ecma-262/5.1/#sec-11.2.1
/// </summary>
internal sealed class JintMemberExpression : JintExpression
{
    /// <summary>
    /// How many links may sit between a cached receiver and the holder of the member it reads. It bounds two
    /// things and nothing else: the array one warmed site allocates, and the reference-plus-integer comparisons
    /// a warm hit runs before it may trust the entry — both of which stay far below the <c>GetOwnProperty</c>
    /// per level they replace, which is why the bound is generous rather than tight. Eight covers what real
    /// hierarchies reach: a DOM member declared on <c>EventTarget.prototype</c> and read off an
    /// <c>HTMLParagraphElement</c> sits four links up, and a <c>class</c> hierarchy rarely reaches three. A
    /// chain longer than this still resolves — <see cref="ReadAfterOwnMissUncached"/> hands the rest of the walk
    /// to the deepest link it reached — it simply resolves uncached, every time.
    /// </summary>
    private const int MaxCachedPrototypeChainLinks = 8;

    private readonly MemberExpression _memberExpression;
    private readonly JintExpression _objectExpression;
    private readonly JintExpression? _propertyExpression;
    private readonly JsValue? _determinedProperty;
    private readonly bool _objectExpressionCanShortCircuit;
    private readonly bool _computedReadEligible;
    // Not readonly: a call or a tagged template whose callee this is clears it at build time (see
    // HandsOnBaseAsReceiver).
    private bool _objectReadKeepsRawEnvironmentWalk;
    private ObjectInstance? _cachedReadObject;
    private PropertyDescriptor? _cachedReadDescriptor;
    private uint _cachedReadVersion;

    // Shape-keyed inline cache for shape-mode receivers (read / member-call / write share these because
    // a member node serves a single role and reads the same property name). A Shape is immutable, so a
    // matching shape reference proves the slot index is still valid — no version check, and the cache
    // hits across *all* objects of the same layout, not just the previously-seen instance.
    private Shape? _cachedShape;
    private int _cachedShapeSlot;

    // The last shape found to have no own slot of this name, so that a member read off the prototype (a method
    // call on an instance, above all) does not search the receiver's shape again on every evaluation. A Shape is
    // immutable, so the miss holds for every object of that shape.
    private Shape? _cachedOwnMissShape;

    // The computed-read lane's key for _cachedShape/_cachedShapeSlot: a computed node has no determined name, so
    // the entry also names the key string it was resolved for (ReadComputedStringKey). The static lanes never run
    // on a computed node, so the two never share an entry.
    private JsString? _cachedComputedKey;

    // Prototype-member inline cache: resolves `obj.member` where `member` lives anywhere on obj's prototype
    // chain (arr.push, date.getTime, and — since the chain form below — a getter or method a
    // `class C extends B extends A` declares on A.prototype, or a DOM member declared on Node.prototype and
    // read off an element three interfaces below it). The own-property caches above only handle own
    // properties, so without this every such read/call re-walks the chain and probes each level's dictionary.
    //
    // Validity: the receiver has no own property of this name, proved afresh on every read (the lanes above
    // establish it on the way here, and ReadAfterOwnMiss probes when they have not), the chain from the
    // receiver's prototype to the holder still linked exactly as recorded (nothing re-pointed by
    // [[SetPrototypeOf]], nothing inserted or removed), no intermediate link having gained an own property of
    // this name, and the holder's own-property shape unchanged (member not redefined or removed). The entry
    // names no receiver, so it serves every receiver whose chain it is — `p.acc.add(v)` over a thousand
    // particles, `results.push(x)` on a new array each time — and keeps none of them alive. Exotic receivers
    // and links (Proxy/TypedArray/IteratorResult) are excluded via InternalTypes.ExoticGet, and every object
    // whose _propertiesVersion cannot witness this property name is excluded by CanCacheAgainstReceiverVersion
    // and ObjectInstance.VersionWitnessesOwnProperty.
    private ObjectInstance? _cachedProtoHolder;
    private uint _cachedProtoHolderVersion;
    private PropertyDescriptor? _cachedProtoDescriptor;

    // The links STRICTLY BETWEEN the receiver and the holder, nearest-first, each paired with the
    // _propertiesVersion it carried when the entry was created. Null when the holder is the receiver's direct
    // prototype — the shape every entry had before deep chains were cacheable, and the one the validity check
    // still serves without touching this field beyond a single null test.
    private PrototypeChainLink[]? _cachedProtoChain;

    // The prototype chain that a deferred function store (TryAssignDeferredFunction) last found free of this node's
    // name, from the receiver's prototype to the end, each link with the _propertiesVersion it had. While every link
    // is still in place with its version unmoved, the next receiver with the same prototype needs no walk.
    private PrototypeChainLink[]? _deferredStoreChain;

    // String-receiver method cache for member calls (str.slice(...)): a primitive string receiver has no
    // own properties beyond `length` and index-coercible names, and those are excluded at build time
    // (_stringReceiverCallEligible), so resolution is prototype-only — the method descriptor is read
    // straight off the realm's %String.prototype% and cached under the same holder-identity +
    // _propertiesVersion guard as the prototype-method cache above. The receiver itself is never boxed
    // or materialized (no ToString/Length touch on lazy CustomString implementations).
    private ObjectInstance? _cachedStringProtoHolder;
    private uint _cachedStringProtoHolderVersion;
    private PropertyDescriptor? _cachedStringProtoDescriptor;
    private readonly bool _stringReceiverCallEligible;

    // Typed array `length` cache: `ta.length` is the built-in get %TypedArray%.prototype.length as long as the
    // receiver has no own `length`, its prototype (%Uint8Array.prototype% and the like) has none either and still
    // links to the holder, and the holder still has the built-in getter. The entry records the prototype and the
    // holder with the _propertiesVersion each had; any own-property change of either moves its version (a
    // redefined accessor is a new descriptor, a removed one a removal), and a re-pointed chain fails the identity
    // tests. The receiver's own miss is proved on every read. Like the prototype-member cache it names no receiver,
    // so one entry serves every array of a kind that passes through the site.
    private ObjectInstance? _typedArrayLengthLink;
    private uint _typedArrayLengthLinkVersion;
    private IntrinsicTypedArrayPrototype? _typedArrayLengthHolder;
    private uint _typedArrayLengthHolderVersion;

    // Whether this node reads the literal name `length`, decided once at build time so that the host
    // array-like lane below costs every other member node a single field test rather than a type check.
    private readonly bool _readsLengthName;

    // Member-call eligibility is a pure function of this node's shape, so it is decided once in the
    // constructor instead of re-deriving four type tests on every call this node is the callee of —
    // JintCallExpression probes it before every member call it dispatches.
    private readonly bool _fastCallEligible;

    // The same member-call shape on any other base (`p.acc.add(v)`, `v.copy(w).sub(u)`): the call evaluates
    // the base once itself and resolves the callee on the value through the caches above, so the base is
    // never evaluated a second time. See ResolveCalleeOnBase.
    private readonly bool _baseCallEligible;

    // ObjectWrapper member cache: interop receivers carry InternalTypes.ExoticGet (members resolve against
    // the wrapped CLR object), so none of the caches above apply and every host.value read/write walks
    // ObjectWrapper.Get/Set → GetOwnProperty → dictionary probe → reflection accessor. But once
    // ObjectWrapper.GetOwnProperty has resolved a member it stores the descriptor in the wrapper's own
    // _properties and every subsequent Get/Set consults that stored instance first — so receiver identity +
    // _propertiesVersion prove the stored descriptor is still exactly what the wrapper would hand back, and
    // reads/writes can go straight through it. The cached descriptor stays live: a CLR property's
    // ReflectionDescriptor re-invokes the CLR getter/setter on every use, so host-side value changes remain
    // visible and JS-side writes reach the CLR setter. Any define/redefine/delete on the wrapper bumps the
    // version and re-resolves. Population is gated by ObjectWrapper.TryGetInlineCacheableDescriptor (exact
    // ObjectWrapper type only, no dictionary targets, no ICollection `length`, no custom member accessor);
    // _cachedWrapperDescriptor is non-null whenever _cachedWrapper is.
    private ObjectWrapper? _cachedWrapper;
    private uint _cachedWrapperVersion;
    private PropertyDescriptor? _cachedWrapperDescriptor;

    private static readonly JsValue _nullMarker = new JsString("NULL MARKER");

    // Set by the next link of the same optional chain (see JintExpression.EnableShortCircuitPropagation):
    // true means this node's short circuit is consumed by its parent link, false means this node is the
    // outermost link and its short circuit is the chain's value, i.e. a real undefined.
    private bool _propagatesShortCircuit;

    public JintMemberExpression(MemberExpression expression) : base(expression)
    {
        _memberExpression = (MemberExpression) _expression;
        _objectExpression = JintStackProbeExpression.BuildSubscriptBase(_memberExpression.Object, ofMember: true, takesReceiver: false);
        _objectExpressionCanShortCircuit = CanShortCircuit(_memberExpression.Object);
        _objectReadKeepsRawEnvironmentWalk = _objectExpression is JintIdentifierExpression { HasEvalOrArguments: true };

        // A ChainExpression object is a chain of its own whose value has already been taken (`(a?.b).c`),
        // so its short circuit is a real undefined and this member access must run against it — that is the
        // whole difference the parentheses make. Anything else is the same chain continuing.
        if (_objectExpressionCanShortCircuit && _memberExpression.Object.Type != NodeType.ChainExpression)
        {
            _objectExpression.EnableShortCircuitPropagation();
        }

        // Computed reads like a[i] / a[i][j] / a[0] (but not super[i] or optional a?.[i]) can take a
        // dense-array fast path in GetValue that resolves base+index without a Reference rent.
        _computedReadEligible = _memberExpression.Computed
            && !_memberExpression.Optional
            && !_objectExpressionCanShortCircuit
            && _objectExpression is not JintSuperExpression;

        var determined = _expression.UserData as JsValue ?? InitializeDeterminedProperty(_memberExpression, cache: false);

        if (ReferenceEquals(determined, _nullMarker))
        {
            _propertyExpression = Build(_memberExpression.Property);
            _determinedProperty = null;
        }
        else
        {
            _determinedProperty = determined;
        }

        var memberCallShape = _propertyExpression is null
                              && _determinedProperty is JsString
                              && !_memberExpression.Optional
                              && !_objectExpressionCanShortCircuit
                              && _objectExpression is not JintSuperExpression;

        _fastCallEligible = memberCallShape && _objectExpression is JintIdentifierExpression or JintThisExpression;
        _baseCallEligible = memberCallShape && !_fastCallEligible;

        _stringReceiverCallEligible = memberCallShape && !CanBeOwnStringInstanceProperty((JsString) _determinedProperty!);

        _readsLengthName = _propertyExpression is null
                           && _determinedProperty is JsString determinedName
                           && CommonProperties.Length.Equals(determinedName);
    }

    /// <summary>
    /// Whether a literal property name can resolve to an OWN property of a boxed string: <c>length</c>,
    /// or any name whose <c>ToNumber</c> coercion is a non-negative int32 — <see cref="Native.String.StringInstance"/>.GetOwnProperty
    /// coerces the name, so "0", "01", "0x1", "1e1", " 1", "-0" and even "" can all address a character.
    /// Such names shadow the prototype on string receivers and must never engage the prototype-only
    /// string-method call cache.
    /// </summary>
    private static bool CanBeOwnStringInstanceProperty(JsString name)
    {
        if (CommonProperties.Length.Equals(name))
        {
            return true;
        }

        // Mirrors StringInstance.IsInt32 + the index >= 0 probe; the < length half is receiver-specific,
        // so any non-negative int32 coercion is (conservatively) treated as a possible own index.
        // NaN/Infinity/fractional/negative coercions can never address a character; -0 compares >= 0
        // and is correctly denied.
        var number = TypeConverter.ToNumber(name);
        return number >= 0 && number <= int.MaxValue && (int) number == number;
    }

    /// <summary>
    /// Build-time probe for the comparison lane's member-bound form: a non-computed, non-optional
    /// `.length` read off a plain identifier (`i &lt; arr.length` / `i &lt; s.length`).
    /// </summary>
    internal bool TryGetIdentifierLengthShape([NotNullWhen(true)] out JintIdentifierExpression? baseIdentifier)
    {
        baseIdentifier = null;
        if (_memberExpression.Computed
            || _memberExpression.Optional
            || _objectExpressionCanShortCircuit
            || _objectExpression is not JintIdentifierExpression identifierBase
            || _determinedProperty is not JsString name
            || !string.Equals(name.ToString(), "length", StringComparison.Ordinal))
        {
            return false;
        }

        baseIdentifier = identifierBase;
        return true;
    }

    /// <summary>
    /// Build-time probe for arithmetic-lane leaves: a computed read whose index is an identifier
    /// or a numeric constant. The returned object expression lets the lane compose chains
    /// (`m[i][j]` probes the outer member, then its inner member, down to an identifier base) —
    /// shapes it can later read purely via slot-resolved dense access.
    /// </summary>
    internal bool TryGetComputedIndexShape(
        [NotNullWhen(true)] out JintExpression? objectExpression,
        out JintIdentifierExpression? indexIdentifier,
        out uint constantIndex)
    {
        objectExpression = null;
        indexIdentifier = null;
        constantIndex = 0;

        if (!_computedReadEligible)
        {
            return false;
        }

        if (_determinedProperty is JsNumber determinedNumber
            && ArrayInstance.IsArrayIndex(determinedNumber, out constantIndex))
        {
            objectExpression = _objectExpression;
            return true;
        }

        if (_propertyExpression is JintIdentifierExpression identifierIndex)
        {
            objectExpression = _objectExpression;
            indexIdentifier = identifierIndex;
            return true;
        }

        return false;
    }

    internal static JsValue InitializeDeterminedProperty(MemberExpression expression, bool cache)
    {
        JsValue? property = null;
        if (!expression.Computed)
        {
            if (expression.Property is Identifier identifier)
            {
                property = cache ? JsString.CachedCreate(identifier.Name) : JsString.Create(identifier.Name);
            }
        }
        else if (expression.Property.Type == NodeType.Literal)
        {
            property = JintLiteralExpression.ConvertToJsValue((Literal) expression.Property);
        }

        return property ?? _nullMarker;
    }

    internal override void EnableShortCircuitPropagation() => _propagatesShortCircuit = true;

    /// <summary>
    /// Called at build time by a call or a tagged template whose callee this is. The base becomes the callee's
    /// <c>this</c> (https://tc39.es/ecma262/#sec-evaluatecall), so an <c>arguments</c> base is handed on rather
    /// than only read from, and has to be resolved through the identifier read that marks the object as having
    /// escaped the frame instead of the raw walk that keeps it poolable.
    /// </summary>
    internal void HandsOnBaseAsReceiver() => _objectReadKeepsRawEnvironmentWalk = false;

    /// <summary>
    /// What this link returns when the chain short-circuits here or below: the signal when the next link
    /// is waiting for it, and the chain's actual value — <c>undefined</c> — when this is the outermost
    /// link. Only ever reached from a link that can short-circuit, which is exactly the shape whose fast
    /// paths <see cref="_objectExpressionCanShortCircuit"/> and <c>_memberExpression.Optional</c> disarm.
    /// </summary>
    private JsValue ShortCircuit() => _propagatesShortCircuit ? ShortCircuited : JsValue.Undefined;

    protected override object EvaluateInternal(EvaluationContext context)
    {
        JsValue? actualThis = null;
        object? baseReferenceName = null;
        JsValue? baseValue = null;

        // Non-null only for `super[expr]`, whose super base must not be resolved until after `expr`
        // has been evaluated - see the comment on the super branch below.
        FunctionEnvironment? deferredSuperEnvironment = null;

        var engine = context.Engine;
        ref readonly var executionContext = ref engine.ExecutionContext;
        var strict = executionContext.Strict;
        var suspendable = executionContext.Suspendable;

        // A super base is derived from the running execution context, not from evaluating the object
        // side, so a resume re-derives it rather than reading it back - which is also what keeps the
        // deferral below intact across `super[await x]`.
        if (suspendable is { IsResuming: true }
            && _objectExpression is not JintSuperExpression
            && suspendable.Data.TryGet(this, out MemberExpressionSuspendData? suspendData))
        {
            // Resume: reuse the previously-resolved object state so a side-effectful
            // object expression (e.g. getObj()[await x]) doesn't run twice.
            baseValue = suspendData!.BaseValue;
            baseReferenceName = suspendData.BaseReferenceName;
            actualThis = suspendData.ActualThis;
        }
        else
        {
            if (_objectExpression is JintIdentifierExpression identifierExpression)
            {
                baseReferenceName = identifierExpression.Identifier.Key.Name;
                if (_objectReadKeepsRawEnvironmentWalk)
                {
                    // `arguments[i]` / `eval.x`: GetValue's HandOn would materialize the arguments object and
                    // permanently opt the frame's JsArguments out of pooling; the raw walk
                    // returns the live object and mapped-index reads stay on the parameter map. Sound only
                    // because the object goes no further than this access: a callee is told otherwise
                    // (HandsOnBaseAsReceiver), and a closure's late read is answered by a snapshot taken at
                    // instantiation (State.ArgumentsMayOutliveCall).
                    var env = engine.ExecutionContext.LexicalEnvironment;
                    JintEnvironment.TryGetIdentifierEnvironmentWithBindingValue(
                        env,
                        identifierExpression.Identifier,
                        strict,
                        out _,
                        out baseValue);
                }
                else
                {
                    // Route through the identifier node's slot/global caches instead of walking
                    // the environment chain on every evaluation; unresolvable and TDZ bases
                    // throw the same ReferenceError the generic fallback would produce.
                    baseValue = identifierExpression.GetValue(context);
                }
            }
            else if (_objectExpression is JintThisExpression thisExpression)
            {
                baseValue = (JsValue?) thisExpression.GetValue(context);
            }
            else if (_objectExpression is JintSuperExpression)
            {
                // https://tc39.es/ecma262/#sec-super-keyword-runtime-semantics-evaluation
                // `super [ Expression ]` evaluates Expression (steps 3-4) and only then calls
                // MakeSuperPropertyReference, whose GetSuperBase reads [[HomeObject]].[[Prototype]].
                // So a side effect in Expression that re-points the home object's prototype - the
                // whole point of staging/sm/class/superPropOrdering.js - is observed by the lookup.
                // `super . IdentifierName` has no such expression and resolves its base immediately.
                var env = (FunctionEnvironment) engine.ExecutionContext.GetThisEnvironment();
                actualThis = env.GetThisBinding();
                if (_propertyExpression is null)
                {
                    baseValue = env.GetSuperBase();
                }
                else
                {
                    deferredSuperEnvironment = env;
                }
            }

            if (baseValue is null && deferredSuperEnvironment is null)
            {
                // fast checks failed
                var baseReference = _objectExpression.Evaluate(context);
                if (context.IsSuspended())
                {
                    // The object-side expression itself suspended (e.g. it's a call
                    // expression with an awaiting argument). Do NOT save suspend data:
                    // on resume we re-evaluate _objectExpression so it produces the
                    // real result via its own resume mechanism.
                    //
                    // What comes back is a sentinel Reference(undefined, undefined), and it is only ever a
                    // placeholder for the shape of the answer: every caller owes an IsSuspended() check
                    // before it reads one. That used to be asserted here as though it held; it did not, and
                    // completing the read through Reference(undefined, undefined) raised a TypeError inside
                    // a frame that was already suspended, which then wiped the statement-list resume
                    // position on its way out and replayed the whole body (sebastienros/jint#4086). The
                    // checks are in JintMemberExpression.GetValue (both lanes) and GetCalleeForCall,
                    // JintCallExpression (callee and argument list), JintUnaryExpression (typeof, delete),
                    // JintUpdateExpression, JintAssignmentExpression (both forms),
                    // DestructuringPatternAssignmentExpression and JintTaggedTemplateExpression - a new
                    // consumer of an evaluated member reference owes one too.
                    return context.Engine._referencePool.Rent(JsValue.Undefined, JsValue.Undefined, strict, thisValue: null);
                }
                if (ReferenceEquals(ShortCircuited, baseReference))
                {
                    // A link below short-circuited. Stop here without evaluating the property expression
                    // — `undefined?.x[y + 1]` must not evaluate `y + 1` — and hand the signal on.
                    return ShortCircuit();
                }
                if (baseReference is Reference reference)
                {
                    baseReferenceName = reference.ReferencedName;
                    baseValue = engine.GetValue(reference, returnReferenceToPool: true);
                }
                else
                {
                    baseValue = engine.GetValue(baseReference, returnReferenceToPool: false);
                }
            }

            // Only this link's own `?.` short-circuits. An object expression that merely *contains* one
            // (`({})?.a` in `({})?.a['b']`) has already decided not to short-circuit and produced a
            // genuine undefined, so this access must run against it and throw. A deferred super base
            // leaves baseValue null here but cannot reach this test: bare `super` is not a
            // MemberExpression, so `super?.x` is a SyntaxError and Optional is always false for one.
            if (_memberExpression.Optional && baseValue!.IsNullOrUndefined())
            {
                return ShortCircuit();
            }
        }

        var property = _determinedProperty ?? _propertyExpression!.GetValue(context);

        // Unconditional, suspension included: GetSuperBase reads [[HomeObject]].[[Prototype]], and a
        // [[HomeObject]] is always an ordinary object, so this is a field read with nothing observable
        // about when it happens - only about what it happens *after*. A suspended pass still needs a
        // base for the Reference it hands back, and the resumed pass re-derives it after re-evaluating
        // the property expression, which is where the ordering guarantee actually has to hold.
        if (deferredSuperEnvironment is not null)
        {
            baseValue = deferredSuperEnvironment.GetSuperBase();
        }

        if (context.IsSuspended())
        {
            // Property-side suspended. Save the resolved object state so resume
            // doesn't re-evaluate the (potentially side-effectful) object side.
            if (suspendable is not null && _objectExpression is not JintSuperExpression)
            {
                var data = suspendable.Data.GetOrCreate<MemberExpressionSuspendData>(this);
                data.BaseValue = baseValue!;
                data.BaseReferenceName = baseReferenceName;
                data.ActualThis = actualThis;
            }
        }
        else
        {
            suspendable?.Data.Clear(this);
        }

        if (property.IsPrivateName())
        {
            return MakePrivateReference(engine, baseValue!, property);
        }

        return context.Engine._referencePool.Rent(baseValue!, property, strict, thisValue: actualThis);
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-makeprivatereference
    /// </summary>
    private static Reference MakePrivateReference(Engine engine, JsValue baseValue, JsValue privateIdentifier)
    {
        var privEnv = engine.ExecutionContext.PrivateEnvironment;
        var privateName = privEnv!.ResolvePrivateIdentifier(privateIdentifier.ToString());
        return engine._referencePool.Rent(baseValue, privateName!, strict: true, thisValue: null);
    }

    /// <summary>
    /// Override GetValue to provide proper error location when base is null/undefined.
    /// For read operations, the error should be thrown with the property node's location.
    /// </summary>
    public override JsValue GetValue(EvaluationContext context)
    {
        // Fast path for common property reads (e.g. obj.prop) where we can avoid creating and resolving a Reference.
        var engine = context.Engine;
        if (_propertyExpression is null
            && _determinedProperty is JsString determinedProperty
            && !_memberExpression.Optional
            && !_objectExpressionCanShortCircuit
            && !engine._resolverWatchesValueBase
            && _objectExpression is not JintSuperExpression)
        {
            var baseValue = _objectExpression.GetValue(context);
            if (context.IsSuspended())
            {
                return JsValue.Undefined;
            }

            if (baseValue.IsNullOrUndefined())
            {
                if (engine._nullishPropagatesInline)
                {
                    // The recognized NullPropagatingReferenceResolver yields the base itself for a nullish
                    // base. Serving it here is what the whole recognition is for: no Reference rent, no
                    // interface call, and no error message built only to be discarded. Returning a value is
                    // mandatory rather than merely faster — merely suppressing the throw would fall through to
                    // GetV, whose ToObject(null) raises a different and worse error.
                    return baseValue;
                }

                if (engine._resolverWatchesNullishBase)
                {
                    // A resolver subscribed to null/undefined bases can substitute a value for this read
                    // (the null-propagation use case), and only Engine.GetValue's TryPropertyReference lane
                    // offers it that chance. Complete through a Reference rented from the already-resolved
                    // base — the base is not re-evaluated, so nothing observable happens twice.
                    var nullishBaseReference = engine._referencePool.Rent(baseValue, determinedProperty, engine.ExecutionContext.Strict, thisValue: null);
                    return CompleteReadFromReference(context, engine, nullishBaseReference);
                }

                TypeConverter.CheckObjectCoercible(engine, baseValue, _memberExpression.Property, Throw.SafeToDisplayString(determinedProperty));
            }

            context.PrepareFor(_expression);

            if (baseValue.IsObject())
            {
                var baseObject = baseValue.AsObjectNoTypeCheck();
                // One AND against the pair, then two compares against it. Testing the pair rather than
                // ShapeMode alone is what keeps the plain arm below identical to what it was before lazy
                // layout slots existed: an object with no lazy slots takes the same single AND+CMP it always
                // did, and only an object that can actually hold an unmaterialized slot pays the checked read.
                var shapeFlags = baseObject._type & (InternalTypes.ShapeMode | InternalTypes.HasLazySlots);
                if (shapeFlags == InternalTypes.ShapeMode)
                {
                    // Shape-keyed read: a matching shape proves the slot index; read it straight out of
                    // the slot array (no descriptor, no dictionary). Misses re-resolve the slot for the
                    // new shape; an own-property miss (inherited / absent) falls to the full Get.
                    var shapeObj = Unsafe.As<JsObject>(baseObject);
                    var shape = shapeObj.ShapeOf;
                    if (ReferenceEquals(shape, _cachedShape))
                    {
                        return shapeObj.GetSlot(_cachedShapeSlot);
                    }

                    if (!ReferenceEquals(shape, _cachedOwnMissShape))
                    {
                        if (shape.TryGetSlot(determinedProperty.ToString(), out var slot))
                        {
                            DropReceiverEntries();
                            _cachedShape = shape;
                            _cachedShapeSlot = slot;
                            return shapeObj.GetSlot(slot);
                        }

                        _cachedOwnMissShape = shape;
                    }

                    // Slot miss ⇒ no own string property (any non-shapeable property would have deopted).
                    return ReadAfterOwnMiss(baseObject, determinedProperty, ownMissConfirmed: true);
                }

                if (shapeFlags == (InternalTypes.ShapeMode | InternalTypes.HasLazySlots))
                {
                    // Same cache, same slot indices, one checked read: a slot still holding an
                    // unmaterialized lazy layout slot runs its factory here and memoizes into the slot.
                    // _cachedShape/_cachedShapeSlot are deliberately SHARED with the plain arm: a shape maps
                    // names to slots identically whatever the object's slots currently hold — a lazy layout's
                    // shape is the very shape an object literal of the same keys interns — and an object
                    // carrying the flag can never enter the plain arm, so a hit resolved by either arm is
                    // valid for the other. Once every lazy slot is materialized the flag clears and the
                    // object is served by the plain arm, indistinguishable from a literal.
                    var lazyObj = Unsafe.As<JsObject>(baseObject);
                    var lazyShape = lazyObj.ShapeOf;
                    if (ReferenceEquals(lazyShape, _cachedShape))
                    {
                        return lazyObj.GetSlotForRead(_cachedShapeSlot);
                    }

                    if (lazyShape.TryGetSlot(determinedProperty.ToString(), out var lazySlot))
                    {
                        DropReceiverEntries();
                        _cachedShape = lazyShape;
                        _cachedShapeSlot = lazySlot;
                        return lazyObj.GetSlotForRead(lazySlot);
                    }

                    return ReadAfterOwnMiss(baseObject, determinedProperty, ownMissConfirmed: true);
                }

                if ((baseObject._type & (InternalTypes.PlainObject | InternalTypes.BuiltinShapeMode)) != InternalTypes.Empty)
                {
                    // Version-based inline cache: as long as the same object is read and its own-property
                    // shape (descriptor add/replace/remove) hasn't changed since we cached, the previously
                    // resolved descriptor reference is still valid — even for configurable properties.
                    if (ReferenceEquals(baseObject, _cachedReadObject)
                        && baseObject._propertiesVersion == _cachedReadVersion
                        && _cachedReadDescriptor is not null)
                    {
                        return ObjectInstance.UnwrapJsValue(_cachedReadDescriptor, baseObject);
                    }

                    var ownDescriptor = baseObject.GetOwnProperty(determinedProperty);
                    if (!ReferenceEquals(ownDescriptor, PropertyDescriptor.Undefined))
                    {
                        DropReceiverEntries();
                        _cachedReadObject = baseObject;
                        _cachedReadVersion = baseObject._propertiesVersion;
                        _cachedReadDescriptor = ownDescriptor;

                        return ObjectInstance.UnwrapJsValue(ownDescriptor, baseObject);
                    }

                    // GetOwnProperty already proved the own-property miss for this receiver.
                    _cachedReadObject = null;
                    _cachedReadDescriptor = null;
                    return ReadAfterOwnMiss(baseObject, determinedProperty, ownMissConfirmed: true);
                }

                _cachedReadObject = null;
                _cachedReadDescriptor = null;
                return ReadFromNonPlainReceiver(baseObject, determinedProperty);
            }

            // JsString primitive: skip ToObject's StringInstance allocation for the hot `s.length`
            // case. `for (var i = 0; i < data.length; i++)` re-reads .length every iteration —
            // observed in dromaeo-string-base64 / sunspider string-base64.
            //
            // Other paths fall through to GetV (which allocates a wrapper but is correct) — the
            // wrapper is needed for spec-compliant numeric-index lookup like t['0'] returning
            // the indexed char via StringInstance.GetOwnProperty's ToNumber coercion. v4.8.0
            // allocated for these too via Engine.GetValue's ToObject path.
            // The type test after the flag one: a host may derive a value of type String that is not a JsString.
            if (baseValue.IsString() && baseValue is JsString lengthReceiver && CommonProperties.Length.Equals(determinedProperty))
            {
                return JsNumber.Create((uint) lengthReceiver.Length);
            }

            return baseValue.GetV(engine.Realm, determinedProperty);
        }

        // Fast path for computed dense-array element reads (a[i], a[i][j], a[0]) in non-suspendable
        // contexts: resolve the base (recursively via GetValue, so chained reads stay rent-free) and
        // the index once, then read the dense slot directly. A miss rents a Reference from the
        // already-resolved operands — no re-evaluation — and completes through the normal path. The
        // Suspendable==null gate means neither operand can suspend, so no suspend bookkeeping is needed.
        // The dense-array hit below only fires for an object (JsArray) base; every other outcome —
        // including a null/undefined base — completes through the rented Reference, which still reaches
        // CheckCoercible and TryPropertyReference. So only a resolver watching non-nullish bases has to
        // disarm this lane.
        if (_computedReadEligible
            && !engine._resolverWatchesValueBase
            && engine.ExecutionContext.Suspendable is null)
        {
            var baseValue = _objectExpression.GetValue(context);
            var property = _determinedProperty ?? _propertyExpression!.GetValue(context);
            context.PrepareFor(_expression);

            if (baseValue is JsArray fastArray
                && fastArray.CanUseFastAccess
                && property is JsNumber fastIndexNumber
                && ArrayInstance.IsArrayIndex(fastIndexNumber, out var fastIndex)
                && fastArray.TryGetValueFast(fastIndex, out var fastValue))
            {
                return fastValue;
            }

            // A typed array with a Number key: its [[Get]] reads the element (undefined out of range) and never the
            // prototype chain, so no Reference and no string form of the key.
            if (baseValue is JsTypedArray fastTypedArray && property is JsNumber typedArrayKey)
            {
                return fastTypedArray.GetElementOfNumberKey(typedArrayKey._value);
            }

            // Same lane for a host array-like: one virtual TryGetIndex, no Reference and no key object. Sits
            // after the JsArray hit so array traffic is untouched. A false is an authoritative own miss and
            // falls through to the Reference path below, which keeps prototype-resolved indices and
            // out-of-range reads on the full pipeline.
            if (baseValue is ArrayLikeObject fastArrayLike
                && property is JsNumber arrayLikeIndexNumber
                && ArrayInstance.IsArrayIndex(arrayLikeIndexNumber, out var arrayLikeIndex)
                && fastArrayLike.ReadIndex(arrayLikeIndex, out var arrayLikeValue))
            {
                return arrayLikeValue;
            }

            // A string key on an object: what Engine.GetValue does with the Reference below, [[Get]] with the base as
            // the receiver (a string is already a property key), without the Reference. A host may derive a key of
            // type String that is not a JsString; the Reference path converts it.
            if (property.IsString() && baseValue.IsObject() && property is JsString stringKey)
            {
                return ReadComputedStringKey(baseValue.AsObjectNoTypeCheck(), stringKey);
            }

            var rentedReference = engine._referencePool.Rent(baseValue, property, engine.ExecutionContext.Strict, thisValue: null);
            return CompleteReadFromReference(context, engine, rentedReference);
        }

        var result = Evaluate(context);

        // Before the `is not Reference` test rather than after it, because a suspended pass can hand back
        // either kind and neither may be used. A suspension on the object side produces the sentinel
        // Reference(undefined, undefined), and completing that read is what raised a TypeError inside an
        // already-suspended frame (sebastienros/jint#4086); one on the property side produces
        // Reference(base, undefined), whose completion is an observable `base[undefined]` probe that a
        // Proxy or a getter sees. Testing first also keeps a non-Reference signal from escaping as a value.
        // Returns Undefined exactly as the guarded fast lane above does; the caller discards it after its
        // own IsSuspended() check.
        if (context.IsSuspended())
        {
            // Resume re-evaluates this node and rents its own reference, so this one is done with.
            engine._referencePool.Return(result as Reference);
            return JsValue.Undefined;
        }

        if (result is not Reference reference)
        {
            // see JintExpression.GetValue: not a Reference means the protocol guarantees a JsValue
            return Unsafe.As<JsValue>(result);
        }

        return CompleteReadFromReference(context, engine, reference);
    }

    /// <summary>
    /// <c>o[key]</c> for a string key and an object <c>o</c>. A shape-mode receiver is read through the shape cache,
    /// whose entry holds for the key it was resolved for: the same string object, or, after one compare, a string
    /// of the same text (which then becomes the entry's key). A shape holds only plain data properties, so its slot
    /// is what [[Get]] returns; any other receiver, and a key the shape has no slot for, take [[Get]].
    /// </summary>
    private JsValue ReadComputedStringKey(ObjectInstance baseObject, JsString key)
    {
        if ((baseObject._type & (InternalTypes.ShapeMode | InternalTypes.HasLazySlots)) == InternalTypes.ShapeMode)
        {
            var shapeObj = Unsafe.As<JsObject>(baseObject);
            var shape = shapeObj.ShapeOf;
            if (ReferenceEquals(shape, _cachedShape) && _cachedComputedKey is { } cachedKey)
            {
                if (ReferenceEquals(key, cachedKey))
                {
                    return shapeObj.GetSlot(_cachedShapeSlot);
                }

                if (string.Equals(key.ToString(), cachedKey.ToString(), StringComparison.Ordinal))
                {
                    _cachedComputedKey = key;
                    return shapeObj.GetSlot(_cachedShapeSlot);
                }
            }

            if (shape.TryGetSlot(key.ToString(), out var slot))
            {
                _cachedShape = shape;
                _cachedShapeSlot = slot;
                _cachedComputedKey = key;
                return shapeObj.GetSlot(slot);
            }
        }

        return baseObject.Get(key, baseObject);
    }

    /// <summary>
    /// Completes a read from an already-resolved <see cref="Reference"/>: string-character and
    /// dense-array element fast paths, then the null-base coercibility check, then the full
    /// <see cref="Engine.GetValue(Reference, bool)"/> pipeline. The reference is always returned to
    /// the pool.
    /// </summary>
    private JsValue CompleteReadFromReference(EvaluationContext context, Engine engine, Reference reference)
    {
        // Fast path for string character access: str[intIndex]
        if (_memberExpression.Computed
            && reference.Base is JsString str
            && reference.ReferencedName is JsNumber num
            && num.IsInteger())
        {
            engine._referencePool.Return(reference);
            var index = num.AsInteger();
            if ((uint) index < (uint) str.Length)
            {
                return JsString.Create(str[index]);
            }

            return JsValue.Undefined;
        }

        // Fast path for dense array element access: arr[intIndex] with a clean prototype chain.
        // Skips Engine.GetValue's property pipeline; holes / out-of-range / sparse arrays fall
        // through (TryGetValueFast returns false) so prototype-chain and length semantics are kept.
        if (_memberExpression.Computed
            && reference.Base is JsArray array
            && array.CanUseFastAccess
            && reference.ReferencedName is JsNumber arrayIndexNumber
            && ArrayInstance.IsArrayIndex(arrayIndexNumber, out var arrayIndex)
            && array.TryGetValueFast(arrayIndex, out var arrayValue))
        {
            engine._referencePool.Return(reference);
            return arrayValue;
        }

        // Host array-like element access, reached when the read arrived here with an already-rented Reference
        // (the non-computed-eligible shapes: optional chaining, super bases, short-circuitable object
        // expressions). Same authoritative-miss rule as the branch in GetValue — a false keeps the read on the
        // full pipeline below.
        if (_memberExpression.Computed
            && reference.Base is ArrayLikeObject arrayLike
            && reference.ReferencedName is JsNumber arrayLikeIndexNumber
            && ArrayInstance.IsArrayIndex(arrayLikeIndexNumber, out var arrayLikeIndex)
            && arrayLike.ReadIndex(arrayLikeIndex, out var arrayLikeValue))
        {
            engine._referencePool.Return(reference);
            return arrayLikeValue;
        }

        // Check if base is null/undefined before calling Engine.GetValue
        // This ensures the error has the correct location (the property access)
        // Per ECMAScript spec, ToObject(base) must happen before ToPropertyKey(property),
        // so we must NOT try to convert property to string for the error message if it's an object.
        // The recognized NullPropagatingReferenceResolver is excluded: the read is completed by
        // Engine.GetValue's matching branch, which returns the nullish base, so nothing may throw on the way
        // there — and the error message this block would build is never used. (Were it reached anyway, the
        // resolver's own CheckCoercible accepts a nullish value, so it still would not throw.)
        if (reference.Base.IsNullOrUndefined() && !engine._nullishPropagatesInline)
        {
            var property = reference.ReferencedName;
            // Only use property for error message if it's already a primitive (won't trigger ToPropertyKey).
            // The rendering goes through the safe helper because a Symbol *is* primitive and
            // TypeConverter.ToString throws for one, which replaced this TypeError with
            // "Cannot convert a Symbol value to a string" for every `nullishBase[someSymbol]` read.
            var referenceName = property.IsPrimitive()
                ? Throw.SafeToDisplayString(property)
                : null;

            TypeConverter.CheckObjectCoercible(engine, reference.Base, _memberExpression.Property, referenceName);
        }

        return engine.GetValue(reference, returnReferenceToPool: true);
    }

    /// <summary>
    /// Whether this member expression can serve as a call's callee via <see cref="GetCalleeForCall"/>
    /// without renting a <see cref="Reference"/>: a non-computed, non-optional literal-name property
    /// access on a side-effect-free, never-suspending base (a plain identifier or <c>this</c>). The
    /// identifier/<c>this</c> restriction guarantees the base evaluates once with no observable side
    /// effect, so the call's slow-path fallback (taken when the resolved value is not callable) never
    /// double-evaluates anything observable.
    /// </summary>
    internal bool IsFastCallEligible => _fastCallEligible;

    /// <summary>
    /// member call when the receiver is an object, reusing the same version-gated own-property inline
    /// cache as <see cref="GetValue"/> and avoiding a <see cref="Reference"/> rent. <paramref name="thisObject"/>
    /// is the receiver value, matching the property-reference this-binding the slow path produces
    /// (<see cref="Reference.ThisValue"/> is the base). A primitive string receiver resolves the method
    /// prototype-only via the string-method cache when the name was proven at build time to never be an
    /// own property of a boxed string; other primitive receivers (and denied/missed string lookups)
    /// return <see cref="JsValue.Undefined"/> so the caller falls through to the Reference path (which
    /// never forces lazy-string materialization).
    /// </summary>
    internal JsValue GetCalleeForCall(EvaluationContext context, out JsValue thisObject)
    {
        var baseValue = _objectExpression.GetValue(context);
        if (context.IsSuspended())
        {
            thisObject = JsValue.Undefined;
            return JsValue.Undefined;
        }

        ResolveCalleeOnBase(context, baseValue, out var callee, out thisObject);
        return callee;
    }

    /// <summary>
    /// Whether this member expression is a literal-name, non-optional member call on a base other than a plain
    /// identifier or <c>this</c> (the <see cref="IsFastCallEligible"/> shape), which a call evaluates through
    /// <see cref="EvaluateBaseForCall"/> and <see cref="ResolveCalleeOnBase"/> instead of renting a
    /// <see cref="Reference"/> for the callee.
    /// </summary>
    internal bool IsBaseCallEligible => _baseCallEligible;

    /// <summary>
    /// Evaluates the base of a <see cref="IsBaseCallEligible"/> member call: the value
    /// <see cref="EvaluateInternal"/> would have put in the callee's <see cref="Reference"/>. Only called by a
    /// frame that cannot suspend.
    /// </summary>
    internal JsValue EvaluateBaseForCall(EvaluationContext context) => _objectExpression.GetValue(context);

    /// <summary>
    /// The callee's <see cref="Reference"/> for a base <see cref="EvaluateBaseForCall"/> produced, which is the one
    /// <see cref="EvaluateInternal"/> rents for a literal-name property: what a call needs when the base is not
    /// an object or the value is not callable.
    /// </summary>
    internal Reference RentReferenceForBase(Engine engine, JsValue baseValue)
        => engine._referencePool.Rent(baseValue, _determinedProperty!, engine.ExecutionContext.Strict, thisValue: null);

    /// <summary>
    /// Resolves the callee of a member call on an evaluated base through this node's inline caches. Returns
    /// false, with <paramref name="callee"/> and <paramref name="thisObject"/> undefined, for a base the caches
    /// do not serve (a primitive other than a string, null, undefined, a string method the string cache
    /// declines); the caller then resolves it through a <see cref="Reference"/>. When it returns true,
    /// <paramref name="callee"/> is the property's value, callable or not.
    /// </summary>
    internal bool ResolveCalleeOnBase(EvaluationContext context, JsValue baseValue, out JsValue callee, out JsValue thisObject)
    {
        var determinedProperty = (JsString) _determinedProperty!;

        context.PrepareFor(_expression);

        // Object receivers take the own-property/prototype cache path below; primitive string receivers
        // take the prototype-only string-method cache further down. Other primitive receivers
        // (number/boolean/...) are declined so the caller falls through to the Reference path.
        if (baseValue.IsObject())
        {
            var baseObject = baseValue.AsObjectNoTypeCheck();
            thisObject = baseObject;

            // See the read lane: testing the pair keeps the plain arm byte-equivalent, and the sibling arm
            // below serves the objects that can hold an unmaterialized lazy layout slot.
            var shapeFlags = baseObject._type & (InternalTypes.ShapeMode | InternalTypes.HasLazySlots);
            if (shapeFlags == InternalTypes.ShapeMode)
            {
                var shapeObj = Unsafe.As<JsObject>(baseObject);
                var shape = shapeObj.ShapeOf;
                if (ReferenceEquals(shape, _cachedShape))
                {
                    callee = shapeObj.GetSlot(_cachedShapeSlot);
                    return true;
                }

                if (!ReferenceEquals(shape, _cachedOwnMissShape))
                {
                    if (shape.TryGetSlot(determinedProperty.ToString(), out var slot))
                    {
                        DropReceiverEntries();
                        _cachedShape = shape;
                        _cachedShapeSlot = slot;
                        callee = shapeObj.GetSlot(slot);
                        return true;
                    }

                    _cachedOwnMissShape = shape;
                }

                // Slot miss ⇒ no own string property (any non-shapeable property would have deopted).
                callee = ReadAfterOwnMiss(baseObject, determinedProperty, ownMissConfirmed: true);
                return true;
            }

            if (shapeFlags == (InternalTypes.ShapeMode | InternalTypes.HasLazySlots))
            {
                // A member call whose callee is a lazy member (`e.body.toString()` resolves the base through
                // the read lane; `e.decode()` resolves the callee here) materializes it like any other value
                // observation, except a deferred closure's first call (JsObject.GetSlotForCall). Cache fields
                // shared with the plain arm — see the read lane's note.
                var lazyObj = Unsafe.As<JsObject>(baseObject);
                var lazyShape = lazyObj.ShapeOf;
                if (ReferenceEquals(lazyShape, _cachedShape))
                {
                    callee = lazyObj.GetSlotForCall(_cachedShapeSlot);
                    return true;
                }

                if (lazyShape.TryGetSlot(determinedProperty.ToString(), out var lazySlot))
                {
                    DropReceiverEntries();
                    _cachedShape = lazyShape;
                    _cachedShapeSlot = lazySlot;
                    callee = lazyObj.GetSlotForCall(lazySlot);
                    return true;
                }

                callee = ReadAfterOwnMiss(baseObject, determinedProperty, ownMissConfirmed: true);
                return true;
            }

            if ((baseObject._type & (InternalTypes.PlainObject | InternalTypes.BuiltinShapeMode)) != InternalTypes.Empty)
            {
                if (ReferenceEquals(baseObject, _cachedReadObject)
                    && baseObject._propertiesVersion == _cachedReadVersion
                    && _cachedReadDescriptor is not null)
                {
                    callee = ObjectInstance.UnwrapJsValue(_cachedReadDescriptor, baseObject);
                    return true;
                }

                var ownDescriptor = baseObject.GetOwnProperty(determinedProperty);
                if (!ReferenceEquals(ownDescriptor, PropertyDescriptor.Undefined))
                {
                    DropReceiverEntries();
                    _cachedReadObject = baseObject;
                    _cachedReadVersion = baseObject._propertiesVersion;
                    _cachedReadDescriptor = ownDescriptor;
                    callee = ObjectInstance.UnwrapJsValue(ownDescriptor, baseObject);
                    return true;
                }

                // GetOwnProperty already proved the own-property miss for this receiver.
                _cachedReadObject = null;
                _cachedReadDescriptor = null;
                callee = ReadAfterOwnMiss(baseObject, determinedProperty, ownMissConfirmed: true);
                return true;
            }

            callee = ReadFromNonPlainReceiver(baseObject, determinedProperty);
            return true;
        }

        // A host may derive a value of type String that is not a JsString; it takes the Reference path.
        if (_stringReceiverCallEligible && baseValue.IsString() && baseValue is JsString jsString)
        {

            // Primitive-string member call (str.slice(...)): the name can never be an own property of a
            // boxed string (build-time proof), so resolve straight off the realm's %String.prototype% —
            // the same object and receiver-binding Engine.GetValue's string lane uses — guarded by
            // holder identity + _propertiesVersion, exactly like the prototype-method cache above.
            // The receiver is passed through untouched (no boxing, no materialization); `this` is the
            // primitive itself, matching Reference.ThisValue on the slow path. In-place method
            // replacement (String.prototype.slice = fn) mutates the cached descriptor's value and is
            // picked up by UnwrapJsValue; define/delete bump the version and re-resolve. A miss on the
            // direct prototype (absent, or found deeper like Object.prototype.hasOwnProperty) and
            // accessor-backed slots fall back to the Reference path so getter side effects run exactly
            // once even when the result is non-callable.
            thisObject = jsString;

            var stringPrototype = context.Engine.Realm.Intrinsics.String.PrototypeObject;
            if (ReferenceEquals(stringPrototype, _cachedStringProtoHolder)
                && stringPrototype._propertiesVersion == _cachedStringProtoHolderVersion)
            {
                callee = ObjectInstance.UnwrapJsValue(_cachedStringProtoDescriptor!, jsString);
                return true;
            }

            var descriptor = stringPrototype.GetOwnProperty(determinedProperty);
            if (!ReferenceEquals(descriptor, PropertyDescriptor.Undefined)
                && (descriptor._flags & PropertyFlag.NonData) == PropertyFlag.None)
            {
                _cachedStringProtoHolder = stringPrototype;
                _cachedStringProtoHolderVersion = stringPrototype._propertiesVersion;
                _cachedStringProtoDescriptor = descriptor;
                callee = ObjectInstance.UnwrapJsValue(descriptor, jsString);
                return true;
            }
        }

        callee = JsValue.Undefined;
        thisObject = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// Read completion for receivers outside the shape / plain-object lanes. A host object resolved to
    /// <see cref="PropertyAccessSemantics.Ordinary"/> resolves from a single own-property probe, which also
    /// re-establishes the own miss the prototype-method cache needs — or, when the host overrides
    /// <see cref="ObjectInstance.TryGetOwnPropertyValue"/>, from that hook instead, which answers the same
    /// question and produces the same value with no descriptor. Otherwise an
    /// <see cref="ObjectWrapper"/> receiver consults the wrapper member cache: on a hit (same wrapper instance,
    /// unchanged <c>_propertiesVersion</c>) the stored descriptor is still exactly what <c>ObjectWrapper.Get</c>'s
    /// own-property probe would return, so it unwraps directly — for a CLR property that re-invokes the CLR
    /// getter through the live <c>ReflectionDescriptor</c>, identical to the full path. Everything else —
    /// and any wrapper bail — funnels into <see cref="ReadAfterOwnMiss"/>, whose full <c>Get</c> resolves
    /// and stores the wrapper member so the next populate attempt succeeds.
    /// </summary>
    private JsValue ReadFromNonPlainReceiver(ObjectInstance baseObject, JsString property)
    {
        // An array's `length` (`for (i = 0, n = a.length; …)`): its [[Get]] is sealed and answers the name from the
        // inline length value before anything else, so that value is the read.
        if (_readsLengthName
            && baseObject is JsArray array
            && array.TryGetLengthValue(out var arrayLength))
        {
            return arrayLength;
        }

        // A typed array's `length`, while the read is the one the built-in getter answers (see the cache's fields).
        if (_readsLengthName
            && baseObject is JsTypedArray typedArray
            && TryReadTypedArrayLength(typedArray, property, out var typedArrayLength))
        {
            return typedArrayLength;
        }

        // A host collection's `length`, answered from its own O(1) count with no descriptor and no accessor
        // invocation - but only while ArrayLikeObject's guard proves that read is the one [[Get]] would make.
        // The build-time name test is what keeps this off every other member node.
        if (_readsLengthName
            && baseObject is ArrayLikeObject arrayLikeReceiver
            && arrayLikeReceiver.TryReadLength(out var arrayLikeLength))
        {
            return JsNumber.Create(arrayLikeLength);
        }

        if ((baseObject._type & InternalTypes.OrdinaryGet) != InternalTypes.Empty)
        {
            // A host-defined object with ordinary [[Get]]. Its own properties live in the host, not in the
            // engine's property bag, so nothing moves _propertiesVersion when that set changes and the version
            // cannot stand in for "this receiver still has no own property of this name" — a projected member
            // that appears after a prototype read was cached must shadow it from the very next read.
            //
            // So the own-property question is asked again on every read, and the prototype-method cache is
            // consulted only once it has been answered "no". That order is also the whole reason such a
            // receiver may be cached at all: this is the only lane that reaches the cache with one, and it
            // re-establishes the own miss before every consult (see CanCacheAgainstReceiverVersion).
            //
            // A host that overrides TryGetOwnPropertyValue answers it without a descriptor — one that projects
            // from native storage would otherwise allocate one per read purely for UnwrapJsValue to discard.
            // The hook answers the *same* question, so the ordering above is untouched: a false is an
            // authoritative own miss, exactly what the discarded descriptor would have proved, re-established
            // on this read like every other.
            if ((baseObject._type & InternalTypes.OwnValueHook) != InternalTypes.Empty)
            {
                if (baseObject.TryGetOwnPropertyValue(property, baseObject, out var projectedValue))
                {
                    DropReceiverEntries();
                    if (HostContractVerification.Enabled)
                    {
                        ObjectInstance.AssertOwnValueAgreesWithDescriptor(baseObject, property, baseObject, answered: true, projectedValue);
                        AssertOrdinaryGetAgrees(baseObject, property, projectedValue);
                    }

                    return projectedValue;
                }

                if (HostContractVerification.Enabled)
                {
                    ObjectInstance.AssertOwnValueAgreesWithDescriptor(baseObject, property, baseObject, answered: false, JsValue.Undefined);
                }

                var projectedMiss = ReadAfterOwnMiss(baseObject, property, ownMissConfirmed: true);
                if (HostContractVerification.Enabled)
                {
                    AssertOrdinaryGetAgrees(baseObject, property, projectedMiss);
                }

                return projectedMiss;
            }

            var ownDescriptor = baseObject.GetOwnProperty(property);
            if (!ReferenceEquals(ownDescriptor, PropertyDescriptor.Undefined))
            {
                DropReceiverEntries();

                // The probe that proves the own property exists *is* the read: the descriptor no longer has to
                // be materialized a second time inside Get (ObjectInstance.Get's fast path needs PlainObject,
                // which such an object cannot claim because it stores nothing in _properties).
                var ownValue = ObjectInstance.UnwrapJsValue(ownDescriptor, baseObject);
                if (HostContractVerification.Enabled)
                {
                    AssertOrdinaryGetAgrees(baseObject, property, ownValue);
                }

                return ownValue;
            }

            var inheritedValue = ReadAfterOwnMiss(baseObject, property, ownMissConfirmed: true);
            if (HostContractVerification.Enabled)
            {
                AssertOrdinaryGetAgrees(baseObject, property, inheritedValue);
            }

            return inheritedValue;
        }

        if (ReferenceEquals(baseObject, _cachedWrapper)
            && baseObject._propertiesVersion == _cachedWrapperVersion)
        {
            return ObjectInstance.UnwrapJsValue(_cachedWrapperDescriptor!, baseObject);
        }

        if (baseObject is ObjectWrapper wrapper)
        {
            var descriptor = wrapper.TryGetInlineCacheableDescriptor(property);
            if (descriptor is not null)
            {
                DropReceiverEntries();
                _cachedWrapper = wrapper;
                _cachedWrapperVersion = wrapper._propertiesVersion;
                _cachedWrapperDescriptor = descriptor;
                return ObjectInstance.UnwrapJsValue(descriptor, wrapper);
            }
        }

        return ReadAfterOwnMiss(baseObject, property, ownMissConfirmed: false);
    }

    /// <summary>
    /// Verifier for the <see cref="PropertyAccessSemantics.Ordinary"/> contract a host object declares but Jint
    /// cannot check statically: the value the descriptor-driven lane produced must equal what the object's own
    /// <c>Get</c> returns. Gated on <see cref="HostContractVerification.Enabled"/>, so an integration suite run
    /// against a Debug Jint — or against the shipped Release package with the
    /// <c>Jint.EnableHostContractVerification</c> switch set — becomes the checker, and every other process
    /// pays nothing. The recomputation is skipped whenever it could be observable — an accessor, a
    /// custom-valued descriptor, or an exotic holder anywhere on the chain — so enabling it never changes what
    /// a script sees.
    /// </summary>
    private static void AssertOrdinaryGetAgrees(ObjectInstance baseObject, JsString property, JsValue value)
    {
        for (var o = (ObjectInstance?) baseObject; o is not null; o = o.GetPrototypeOf())
        {
            if ((o._type & InternalTypes.ExoticGet) != InternalTypes.Empty)
            {
                return;
            }

            var descriptor = o.GetOwnProperty(property);
            if (ReferenceEquals(descriptor, PropertyDescriptor.Undefined))
            {
                continue;
            }

            if ((descriptor._flags & (PropertyFlag.NonData | PropertyFlag.CustomJsValue)) != PropertyFlag.None)
            {
                return;
            }

            break;
        }

        if (!JsValue.SameValue(baseObject.Get(property, baseObject), value))
        {
            HostContractVerification.Fail($"{baseObject.GetType()} declared PropertyAccessSemantics.Ordinary but its Get('{property}') disagrees with UnwrapJsValue(GetOwnProperty('{property}')). Declare PropertyAccessSemantics.Exotic instead, or make Get ordinary.");
        }
    }

    /// <summary>
    /// Reads <c>length</c> of a typed array through the typed array length cache, recording an entry when the
    /// receiver's chain is the built-in one. False hands the read to the full [[Get]]: the receiver or a link
    /// owns a <c>length</c>, the chain is not the two ordinary links that end at a <c>%TypedArray%.prototype</c>
    /// (a Proxy, an exotic or host link, a prototype set to something else), or the getter is not the built-in.
    /// </summary>
    private bool TryReadTypedArrayLength(JsTypedArray typedArray, JsString property, out JsValue length)
    {
        // A typed array is never in a shape mode: its string-keyed own properties are its dictionary, and a symbol
        // or an integer index is no `length`.
        if (typedArray._properties is { } own && own.ContainsKey(property.ToString()))
        {
            length = JsValue.Undefined;
            return false;
        }

        var link = typedArray.GetPrototypeOf();
        var holder = _typedArrayLengthHolder;
        if (holder is not null
            && ReferenceEquals(link, _typedArrayLengthLink)
            && link!._propertiesVersion == _typedArrayLengthLinkVersion
            && ReferenceEquals(link.GetPrototypeOf(), holder)
            && holder._propertiesVersion == _typedArrayLengthHolderVersion)
        {
            length = IntrinsicTypedArrayPrototype.LengthOf(typedArray);
            return true;
        }

        _typedArrayLengthLink = null;
        _typedArrayLengthHolder = null;
        if (link is null
            || (link._type & (InternalTypes.ExoticGet | InternalTypes.OwnValueHook)) != InternalTypes.Empty
            || !ObjectInstance.VersionWitnessesOwnProperty(link, property)
            || !ReferenceEquals(link.GetOwnProperty(property), PropertyDescriptor.Undefined)
            || link.GetPrototypeOf() is not IntrinsicTypedArrayPrototype intrinsic
            || !ObjectInstance.VersionWitnessesOwnProperty(intrinsic, property)
            || intrinsic.GetOwnProperty(property) is not { } descriptor
            || !descriptor.IsAccessorDescriptor()
            || !intrinsic.IsBuiltinLengthGetter(descriptor.Get))
        {
            length = JsValue.Undefined;
            return false;
        }

        _typedArrayLengthLink = link;
        _typedArrayLengthLinkVersion = link._propertiesVersion;
        _typedArrayLengthHolder = intrinsic;
        _typedArrayLengthHolderVersion = intrinsic._propertiesVersion;
        length = IntrinsicTypedArrayPrototype.LengthOf(typedArray);
        return true;
    }

    /// <summary>
    /// Resolves a member read from <paramref name="baseObject"/> after the own-property fast paths have
    /// missed: tries the prototype-member inline cache, then walks the prototype chain
    /// (<see cref="ReadAfterOwnMissUncached"/>), which serves the read and records an entry for it when it can.
    /// <paramref name="ownMissConfirmed"/> is <c>true</c> when the caller already proved the receiver has no
    /// own property of this name (a shape slot miss or a <c>GetOwnProperty</c> that returned undefined), so
    /// the walk can skip re-probing it.
    /// </summary>
    private JsValue ReadAfterOwnMiss(ObjectInstance baseObject, JsString property, bool ownMissConfirmed)
    {
        var holder = _cachedProtoHolder;
        if (holder is not null && (baseObject._type & InternalTypes.ExoticGet) == InternalTypes.Empty)
        {
            // The entry proves nothing about the receiver, so the own miss is established here when the caller
            // has not: the probe the uncached walk would make, made before the cache rather than after it.
            if (ownMissConfirmed || baseObject.ProbeOwnPropertyChecked(property) == OwnPropertyProbe.Missing)
            {
                if (TryReadFromPrototypeCache(baseObject, holder, out var value))
                {
                    return value;
                }

                ownMissConfirmed = true;
            }
        }

        return ReadAfterOwnMissUncached(baseObject, property, ownMissConfirmed);
    }

    /// <summary>
    /// The prototype-member inline cache's validity check for a receiver that is not exotic and has been proved
    /// to have no own property of the name on this read. <paramref name="holder"/> is the already-loaded
    /// <c>_cachedProtoHolder</c>, non-null. Every version comparison is only meaningful because
    /// <see cref="CanCacheAgainstReceiverVersion"/> and <see cref="ObjectInstance.VersionWitnessesOwnProperty"/>
    /// between them refused to create an entry any of whose versions cannot witness the name.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadFromPrototypeCache(ObjectInstance baseObject, ObjectInstance holder, out JsValue value)
    {
        // GetPrototypeOf(), not the _prototype field: a subclass may shadow the field and override
        // [[GetPrototypeOf]] (e.g. interop instances), and base Get walks via the same accessor. It is a pure
        // field read for ordinary objects and never the proxy trap (proxies carry ExoticGet and never reach here).
        var reached = baseObject.GetPrototypeOf();

        // Null for a direct-prototype entry, so the common shape costs one predictable field test and the call
        // below is never made.
        var chain = _cachedProtoChain;
        if (chain is not null)
        {
            reached = RevalidateChain(reached, chain);
        }

        if (ReferenceEquals(reached, holder)
            && holder._propertiesVersion == _cachedProtoHolderVersion)
        {
            value = ObjectInstance.UnwrapJsValue(_cachedProtoDescriptor!, baseObject);
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// Walks the recorded intermediate links and returns what the chain reaches past the last of them, or
    /// <see langword="null"/> if it no longer runs as recorded. Two facts are re-proved per link, and they are
    /// the two ways a chain stops meaning what it meant: the link is still the object that occupied this
    /// position (so nothing was re-pointed by <c>[[SetPrototypeOf]]</c>, inserted or removed), and its
    /// <c>_propertiesVersion</c> is unmoved (so it has not gained an own property of this name, which would
    /// shadow the holder's from here on). A <see langword="null"/> return can never be mistaken for success:
    /// the caller compares it against a non-null holder.
    /// </summary>
    private static ObjectInstance? RevalidateChain(ObjectInstance? reached, PrototypeChainLink[] chain)
    {
        foreach (var recorded in chain)
        {
            var link = recorded.Link;
            if (!ReferenceEquals(reached, link) || link._propertiesVersion != recorded.Version)
            {
                return null;
            }

            reached = link.GetPrototypeOf();
        }

        return reached;
    }

    /// <summary>
    /// The uncached completion of a member read whose receiver has no own property of that name: walk the
    /// prototype chain the way <c>[[Get]]</c> would, serve the read, and record an entry for it when every
    /// link on the way can still be proved on a later read.
    /// <para>
    /// The walk is this lane's own rather than a call back into <see cref="ObjectInstance.Get(JsValue, JsValue)"/>
    /// because the caller has already established the receiver's own miss: re-entering <c>Get</c> would re-probe
    /// the receiver and then walk the very links this method has to walk anyway to find the holder. It hands
    /// the rest of the walk to a link the instant it reaches one it may not walk itself — an exotic
    /// <c>[[Get]]</c>, a host's own-value hook, or the depth past which an entry would no longer be recorded —
    /// by calling that link's <c>Get</c> with the original receiver, which is exactly the continuation an
    /// ordinary <c>[[Get]]</c> makes at that point.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ReadAfterOwnMissUncached(ObjectInstance baseObject, JsString property, bool ownMissConfirmed)
    {
        // The entry for the previous receiver has just missed (or there was none). Drop it, and every other
        // receiver entry, before resolving: only an entry recorded below may outlive this read. The chain array
        // is kept apart as a buffer for the next entry to refill, not as part of any entry.
        var spareChain = _cachedProtoChain;
        DropReceiverEntries();

        // Only ordinary receivers: a Proxy / TypedArray / IteratorResult / interop wrapper has a custom
        // [[Get]] / [[GetOwnProperty]] this lane must not bypass. And no own property on the receiver (which
        // would shadow the prototype's) — the caller usually already established that (shape slot miss /
        // GetOwnProperty undefined), so re-probe only when it didn't (a non-plain receiver reached here
        // unchecked).
        if ((baseObject._type & InternalTypes.ExoticGet) != InternalTypes.Empty
            || (!ownMissConfirmed && baseObject.ProbeOwnPropertyChecked(property) != OwnPropertyProbe.Missing))
        {
            return baseObject.Get(property, baseObject);
        }

        var link = baseObject.GetPrototypeOf();
        if (link is null)
        {
            return JsValue.Undefined;
        }

        // Decided once, then narrowed by every link the walk passes through. A false only stops the entry
        // being recorded; the walk itself stays correct and still serves the read.
        var cacheable = CanCacheAgainstReceiverVersion(baseObject, property);

        var intermediates = 0;
        while (true)
        {
            // A link whose [[Get]] deviates, or which answers own reads from its own storage through
            // TryGetOwnPropertyValue, must resolve the rest of the read itself: probing it with
            // GetOwnProperty would run the wrong algorithm for the first and materialize a descriptor the
            // second exists to avoid. Tested BEFORE the probe, so handing over costs nothing that has
            // already been paid.
            if ((link._type & (InternalTypes.ExoticGet | InternalTypes.OwnValueHook)) != InternalTypes.Empty)
            {
                return link.Get(property, baseObject);
            }

            var descriptor = link.GetOwnProperty(property);
            if (!ReferenceEquals(descriptor, PropertyDescriptor.Undefined))
            {
                if (cacheable && ObjectInstance.VersionWitnessesOwnProperty(link, property))
                {
                    PopulatePrototypeCache(baseObject, link, descriptor, intermediates, spareChain);
                }

                return ObjectInstance.UnwrapJsValue(descriptor, baseObject);
            }

            // Only now does this link become an intermediate — one whose *absence* of the name the entry
            // would have to keep proving, which is what its version has to witness.
            cacheable = cacheable && ObjectInstance.VersionWitnessesOwnProperty(link, property);

            var next = link.GetPrototypeOf();
            if (next is null)
            {
                // The whole chain was walked with ordinary semantics and nothing owns the name.
                return JsValue.Undefined;
            }

            if (++intermediates > MaxCachedPrototypeChainLinks)
            {
                // Past the recordable depth: finish the read on the normal path, uncached. Nothing above
                // has been skipped, so this is a continuation and not a restart.
                return next.Get(property, baseObject);
            }

            link = next;
        }
    }

    /// <summary>
    /// Records an entry for a resolved (receiver, chain, holder) triple. The intermediate links are re-walked
    /// rather than accumulated during the search, so the search allocates nothing on the paths that do not end
    /// in an entry — a name absent from the whole chain, or a chain carrying a link no version can witness.
    /// <para>
    /// <b>The array is reused whenever the previous entry recorded a chain of the same length</b>, which is
    /// what keeps this affordable on a site that is *polymorphic* over instances of one class. Such a site
    /// re-caches on every receiver change, and allocating per read would be a new cost the direct-prototype
    /// form never had — whereas instances of one hierarchy all sit at the same depth, so after the first read
    /// the same array is overwritten. Every slot is written below before the entry is published.
    /// </para>
    /// <para>
    /// The re-walk must land on <paramref name="holder"/>, and the entry is dropped rather than recorded if it
    /// does not — dropped, not merely left alone, because the half-written array may be the live entry's own.
    /// Only a materializing <c>GetOwnProperty</c> ran between the two walks — never script, since the
    /// descriptor is unwrapped after this returns — but a host factory is still host code, and an entry whose
    /// chain was written down from a chain that had already moved is the one failure this cache must not have.
    /// </para>
    /// </summary>
    private void PopulatePrototypeCache(ObjectInstance baseObject, ObjectInstance holder, PropertyDescriptor descriptor, int intermediates, PrototypeChainLink[]? spareChain)
    {
        PrototypeChainLink[]? chain = null;
        if (intermediates > 0)
        {
            chain = spareChain;
            if (chain is null || chain.Length != intermediates)
            {
                chain = new PrototypeChainLink[intermediates];
            }

            var link = baseObject.GetPrototypeOf();
            for (var i = 0; i < intermediates; i++)
            {
                if (link is null)
                {
                    DropPrototypeCacheEntry();
                    return;
                }

                chain[i] = new PrototypeChainLink(link, link._propertiesVersion);
                link = link.GetPrototypeOf();
            }

            if (!ReferenceEquals(link, holder))
            {
                DropPrototypeCacheEntry();
                return;
            }
        }

        _cachedProtoChain = chain;
        _cachedProtoHolder = holder;
        _cachedProtoHolderVersion = holder._propertiesVersion;
        _cachedProtoDescriptor = descriptor;
    }

    /// <summary>
    /// Disarms the entry. <see cref="ReadAfterOwnMiss"/> consults the cache only for a non-null holder, so
    /// clearing that field is what stops it being read; the rest is cleared so nothing is retained by a site
    /// that is no longer serving from it.
    /// </summary>
    private void DropPrototypeCacheEntry()
    {
        _cachedProtoHolder = null;
        _cachedProtoChain = null;
        _cachedProtoDescriptor = null;
    }

    /// <summary>
    /// Disarms every entry that holds a receiver object, the own-property entry and the wrapper entry, and the
    /// prototype entry with them. The first two hit only for the very receiver they recorded, so an entry left behind when
    /// the site resolves through another lane serves nothing, yet keeps that receiver — and everything its
    /// property values reach — alive for as long as the script's code is. A forward-linked list whose first
    /// node was read here before later nodes took the shape lane was kept whole that way, growing with every
    /// node appended. Called on the miss paths only, before the lane that resolved the read records its own
    /// entry; hits never pay for it. The shape and string-prototype entries hold no receiver and are left
    /// alone.
    /// </summary>
    private void DropReceiverEntries()
    {
        _cachedReadObject = null;
        _cachedReadDescriptor = null;
        _cachedWrapper = null;
        _cachedWrapperDescriptor = null;
        DropPrototypeCacheEntry();
    }

    /// <summary>
    /// One recorded link of a cached prototype chain: the object that occupied the position, and the
    /// <c>_propertiesVersion</c> it carried when the entry was created.
    /// </summary>
    private readonly struct PrototypeChainLink
    {
        internal readonly ObjectInstance Link;
        internal readonly uint Version;

        internal PrototypeChainLink(ObjectInstance link, uint version)
        {
            Link = link;
            Version = version;
        }
    }

    /// <summary>
    /// Whether an entry may be created for this (receiver, chain, holder, name) tuple — that is, whether the
    /// <c>_propertiesVersion</c> comparisons <see cref="TryReadFromPrototypeCache"/> makes can still prove, on a
    /// later read, that the name is absent from the receiver, absent from every link between it and the holder,
    /// and owned by the holder.
    /// <para>
    /// A version only witnesses the own properties the engine stores itself. Two kinds of object keep some of
    /// theirs elsewhere and move no version when that part of the set changes: an <b>array</b>, whose elements
    /// live in its own dense/sparse storage and are written straight there by the hot element paths, and a
    /// <b>host-defined subclass</b> with ordinary reads, whose whole own-property set lives outside the engine.
    /// So an array must not be validated by its version for an index-like name, and a host object must not be
    /// validated by its version for any name. <see cref="ObjectInstance.VersionWitnessesOwnProperty"/> is that
    /// predicate, and it is asked of <b>every</b> link — each intermediate one as the walk leaves it behind,
    /// and the holder before the entry is recorded.
    /// </para>
    /// <para>
    /// Only the receiver is exempt, and this method is where that exemption lives.
    /// <see cref="ReadFromNonPlainReceiver"/> is the only lane that reaches this cache with a host receiver, and
    /// it establishes the own miss anew before every consult — with a real <c>GetOwnProperty</c> probe, or with
    /// a <c>false</c> from <see cref="ObjectInstance.TryGetOwnPropertyValue"/>, which states the same thing — so
    /// that receiver's frozen version is never load-bearing. <b>No intermediate link has such a lane</b>: nothing
    /// re-establishes that a link three levels up still lacks the name, so an unwitnessable one is refused
    /// outright exactly as a holder is. That asymmetry is the whole reason a host prototype cannot sit in the
    /// middle of a cached chain any more than it can hold one.
    /// </para>
    /// </summary>
    private static bool CanCacheAgainstReceiverVersion(ObjectInstance receiver, JsString property)
        => (receiver._type & InternalTypes.OrdinaryGet) != InternalTypes.Empty
           || ObjectInstance.VersionWitnessesOwnProperty(receiver, property);

    /// <summary>
    /// Write-side counterpart of <see cref="GetValue"/>'s inline cache for <c>obj.prop = rhs</c>. Reuses the
    /// same version-gated own-property cache slots: when the receiver is a <see cref="InternalTypes.PlainObject"/>
    /// whose shape is unchanged and the own property is a <em>live</em> writable, non-accessor, non-custom data
    /// descriptor, the new value is written straight into the descriptor (no Reference rent, no property-key hash,
    /// no dictionary lookup) — exactly the in-place store <see cref="ObjectInstance.Set(JsValue,JsValue,JsValue)"/>
    /// performs, which by design does not bump <c>_propertiesVersion</c>.
    /// <para>
    /// The method returns <c>false</c> only from the eligibility gate, having evaluated nothing, so the caller's
    /// unchanged slow path runs. Once the base and right-hand side have been evaluated (each exactly once, in spec
    /// order) it always completes the assignment and returns <c>true</c>: either the in-place store, or — for an
    /// absent / accessor / read-only / custom-value property, or a non-<see cref="ObjectInstance"/> base — a
    /// fallback through <see cref="Engine.PutValue"/> rented from the already-resolved base+key (so a side-effecting
    /// base or RHS is never evaluated twice and prototype-setter / CreateDataProperty / strict read-only semantics
    /// are preserved).
    /// </para>
    /// </summary>
    internal bool TryAssignFast(EvaluationContext context, JintExpression right, out JsValue result)
    {
        var engine = context.Engine;

        // Same eligibility as GetValue's primary fast path, minus its custom-resolver gate, plus the
        // computed-read path's Suspendable==null gate: a static string-named, non-optional,
        // non-short-circuiting, non-super property write in a context where neither operand can suspend
        // (so no generator/async bookkeeping is needed).
        //
        // Unlike the read path, this one needs no custom-resolver gate: IReferenceResolver has no write-side
        // member, and none of its four methods is consulted while completing a property store —
        // Engine.PutValue reaches the resolver on none of its branches. The base is still evaluated through
        // the normal read path (which does consult the resolver, including for an unresolvable base), and a
        // nullish base is not an ObjectInstance so it flows to the PutValue fallback and throws there exactly
        // as the slow path would.
        if (_propertyExpression is not null
            || _determinedProperty is not JsString determinedProperty
            || _memberExpression.Optional
            || _objectExpressionCanShortCircuit
            || _objectExpression is JintSuperExpression
            || engine.ExecutionContext.Suspendable is not null)
        {
            result = JsValue.Undefined;
            return false;
        }

        // Evaluate base, then RHS — each exactly once, preserving base→key→rhs spec order. A null/undefined base
        // is simply not a PlainObject and flows to the fallback, where PutValue→ToObject throws after the RHS.
        var baseValue = _objectExpression.GetValue(context);
        var rval = right.GetValue(context);

        context.PrepareFor(_expression);

        StoreOnBase(context, baseValue, rval);
        result = rval;
        return true;
    }

    /// <summary>
    /// <see cref="TryAssignFast"/> for a statement <c>o.name = function () { ... };</c> whose value nobody reads:
    /// when the store adds a plain data property to an object under construction, the property becomes a lazy slot
    /// and the function is created on its first read (<see cref="JsObject.TryAddDeferredClosure"/>). Anything else
    /// creates the function and stores it as <see cref="TryAssignFast"/> does. Declines, having evaluated nothing,
    /// under <see cref="TryAssignFast"/>'s gates and in a frame with a private environment, whose functions would
    /// need it.
    /// </summary>
    internal bool TryAssignDeferredFunction(EvaluationContext context, JintFunctionExpression function, ref JsObject.DeferredClosureTransition? transition)
    {
        var engine = context.Engine;
        if (_propertyExpression is not null
            || _determinedProperty is not JsString determinedProperty
            || _memberExpression.Optional
            || _objectExpressionCanShortCircuit
            || _objectExpression is JintSuperExpression
            || engine.ExecutionContext.Suspendable is not null
            || engine.ExecutionContext.PrivateEnvironment is not null)
        {
            return false;
        }

        var baseValue = _objectExpression.GetValue(context);
        context.PrepareFor(_expression);

        if (baseValue is JsObject target)
        {
            var chainChecked = DeferredStoreChainHolds(target);
            if (target.TryAddDeferredClosure(determinedProperty, function.Definition, engine.ExecutionContext.LexicalEnvironment, ref transition, chainChecked))
            {
                if (!chainChecked)
                {
                    RecordDeferredStoreChain(target, determinedProperty);
                }

                return true;
            }
        }

        StoreOnBase(context, baseValue, function.GetValue(context));
        return true;
    }

    /// <summary>
    /// Whether <paramref name="target"/>'s prototype chain is still the one <see cref="RecordDeferredStoreChain"/>
    /// recorded: the same links in the same order, ending there, each with its version unmoved, so none of them has
    /// gained this node's name since it was found absent from all of them.
    /// </summary>
    private bool DeferredStoreChainHolds(JsObject target)
    {
        var chain = _deferredStoreChain;
        if (chain is null)
        {
            return false;
        }

        var reached = target.GetPrototypeOf();
        foreach (var recorded in chain)
        {
            if (!ReferenceEquals(reached, recorded.Link) || reached._propertiesVersion != recorded.Version)
            {
                return false;
            }

            reached = reached.GetPrototypeOf();
        }

        return reached is null;
    }

    /// <summary>
    /// Records the prototype chain of <paramref name="target"/>, which a deferred function store has just walked and
    /// found free of <paramref name="property"/> (every link ordinary, none owning the name), when every link's
    /// version witnesses its own properties, so that <see cref="DeferredStoreChainHolds"/> can prove the same later.
    /// </summary>
    private void RecordDeferredStoreChain(JsObject target, JsString property)
    {
        var count = 0;
        for (var link = target.GetPrototypeOf(); link is not null; link = link.GetPrototypeOf())
        {
            if (++count > MaxCachedPrototypeChainLinks || !ObjectInstance.VersionWitnessesOwnProperty(link, property))
            {
                _deferredStoreChain = null;
                return;
            }
        }

        var chain = _deferredStoreChain is { } previous && previous.Length == count ? previous : new PrototypeChainLink[count];
        var i = 0;
        for (var link = target.GetPrototypeOf(); link is not null; link = link.GetPrototypeOf())
        {
            chain[i++] = new PrototypeChainLink(link, link._propertiesVersion);
        }

        _deferredStoreChain = chain;
    }

    /// <summary>
    /// The store half of <see cref="TryAssignFast"/>: <c>[[Set]]</c> of this node's literal name on an already
    /// evaluated base, in place when the caches prove an own writable data property and through
    /// <see cref="Engine.PutValue"/> otherwise. Shared with compound assignment, whose right-hand side may have
    /// changed the object since its read, which is why nothing about the read is trusted here.
    /// </summary>
    internal void StoreOnBase(EvaluationContext context, JsValue baseValue, JsValue rval)
    {
        var engine = context.Engine;
        var determinedProperty = (JsString) _determinedProperty!;

        if (baseValue.IsObject())
        {
            var baseObject = baseValue.AsObjectNoTypeCheck();
            if ((baseObject._type & InternalTypes.ShapeMode) != InternalTypes.Empty)
            {
                // Shape-keyed write: shape-mode properties are always writable data, so a slot match is
                // an in-place store with no descriptor, no hash, no version bump. An absent own property
                // falls through to PutValue (add / inherited-setter / CreateDataProperty semantics).
                var shapeObj = Unsafe.As<JsObject>(baseObject);
                var shape = shapeObj.ShapeOf;
                int slot;
                if (ReferenceEquals(shape, _cachedShape))
                {
                    slot = _cachedShapeSlot;
                }
                else if (shape.TryGetSlot(determinedProperty.ToString(), out slot))
                {
                    DropReceiverEntries();
                    _cachedShape = shape;
                    _cachedShapeSlot = slot;
                }
                else
                {
                    DropReceiverEntries();
                    slot = -1;
                }

                if (slot >= 0)
                {
                    shapeObj.SetSlot(slot, rval);
                    return;
                }
            }
            else if ((baseObject._type & (InternalTypes.PlainObject | InternalTypes.BuiltinShapeMode)) != InternalTypes.Empty)
            {
                PropertyDescriptor? descriptor;
                if (ReferenceEquals(baseObject, _cachedReadObject)
                    && baseObject._propertiesVersion == _cachedReadVersion
                    && _cachedReadDescriptor is not null)
                {
                    descriptor = _cachedReadDescriptor;
                }
                else
                {
                    var ownDescriptor = baseObject.GetOwnProperty(determinedProperty);
                    DropReceiverEntries();
                    if (ReferenceEquals(ownDescriptor, PropertyDescriptor.Undefined))
                    {
                        // Absent own property: inherited-setter / CreateDataProperty semantics — handled by fallback.
                        descriptor = null;
                    }
                    else
                    {
                        _cachedReadObject = baseObject;
                        _cachedReadVersion = baseObject._propertiesVersion;
                        _cachedReadDescriptor = ownDescriptor;
                        descriptor = ownDescriptor;
                    }
                }

                // Re-read the flags live every store: Object.defineProperty flips Writable in place on the same
                // descriptor without bumping the version, so the writability decision must never be cached. The mask
                // must equal exactly Writable — i.e. writable, not an accessor (NonData), not custom-valued, and not one
                // whose owner must see the write (WritesThroughDefineOwnProperty, sebastienros/jint#4418).
                if (descriptor is not null
                    && (descriptor._flags & (PropertyFlag.NonData | PropertyFlag.CustomJsValue | PropertyFlag.Writable | PropertyDescriptor.WritesThroughDefineOwnProperty)) == PropertyFlag.Writable)
                {
                    descriptor._value = rval;
                    return;
                }
            }
            else
            {
                // ObjectWrapper member lane: a stored member means ObjectWrapper.Set would route through
                // SetSlow (ContainsKey ⇒ CanPut + `ownDesc.Value = value`), and the receiver-identity +
                // _propertiesVersion guard proves the cached descriptor is that stored instance. Mirror
                // SetSlow exactly: CanPut's own-descriptor branch with live flag/accessor reads
                // (defineProperty can mutate the same instance without a version bump), then store through
                // the descriptor — a ReflectionDescriptor forwards to the CLR setter, keeping conversion
                // and exception semantics. SetSlow differs only for a descriptor whose write a read-only
                // dictionary can refuse, which it asks through ReflectionDescriptor.TrySetValue because the
                // Value setter cannot report a refusal; TryGetInlineCacheableDescriptor never hands this lane
                // one (it declines dictionaries and ReflectionDescriptor.WriteCanBeRefused), so for everything
                // the lane holds the two are the same. Non-writable members (e.g. read-only CLR properties, whose
                // ReflectionDescriptor exposes no setter when not writable or interop writes are disabled)
                // fall through to the PutValue fallback so strict/sloppy failure behavior stays identical.
                // Population only consumes descriptors a read has already stored; it never resolves members
                // itself, so unstored writes keep ObjectWrapper.Set's accessor fast path untouched.
                PropertyDescriptor? descriptor;
                if (ReferenceEquals(baseObject, _cachedWrapper)
                    && baseObject._propertiesVersion == _cachedWrapperVersion)
                {
                    descriptor = _cachedWrapperDescriptor;
                }
                else if (baseObject is ObjectWrapper wrapper
                    && (descriptor = wrapper.TryGetInlineCacheableDescriptor(determinedProperty)) is not null)
                {
                    DropReceiverEntries();
                    _cachedWrapper = wrapper;
                    _cachedWrapperVersion = wrapper._propertiesVersion;
                    _cachedWrapperDescriptor = descriptor;
                }
                else
                {
                    DropReceiverEntries();
                    descriptor = null;
                }

                if (descriptor is not null)
                {
                    bool canPut;
                    if (descriptor.IsAccessorDescriptor())
                    {
                        var set = descriptor.Set;
                        canPut = set is not null && !set.IsUndefined();
                    }
                    else
                    {
                        canPut = descriptor.Writable;
                    }

                    if (canPut)
                    {
                        descriptor.Value = rval;
                        return;
                    }
                }
            }
        }

        // Fallback: complete via the normal pipeline from the already-resolved base + key (no re-evaluation).
        var reference = engine._referencePool.Rent(baseValue, determinedProperty, engine.ExecutionContext.Strict, thisValue: null);
        engine.PutValue(reference, rval);
        engine._referencePool.Return(reference);
    }

    /// <summary>
    /// Whether this member expression can be the left-hand side of a compound assignment that reads and writes
    /// through <see cref="TryReadOwnDataForCompound"/> and <see cref="StoreOnBase"/> on a base evaluated once,
    /// without renting a <see cref="Reference"/>: a literal-name, non-optional, non-super member.
    /// </summary>
    internal bool IsCompoundTargetEligible => _propertyExpression is null
                                              && _determinedProperty is JsString
                                              && !_memberExpression.Optional
                                              && !_objectExpressionCanShortCircuit
                                              && _objectExpression is not JintSuperExpression;

    /// <summary>
    /// Evaluates the base of a member a compound assignment writes: the value <see cref="EvaluateInternal"/>
    /// would have put in the left-hand side's <see cref="Reference"/>. Only called by a frame that cannot suspend.
    /// </summary>
    internal JsValue EvaluateBaseForAssignment(EvaluationContext context) => _objectExpression.GetValue(context);

    /// <summary>
    /// The read half of a compound assignment on an evaluated base: the value of an own data property, from the
    /// shape and own-property caches. Returns false, having read nothing observable, for everything else (a
    /// primitive or nullish base, an accessor, an inherited or absent property, a host object), which the caller
    /// then reads through a <see cref="Reference"/>.
    /// </summary>
    internal bool TryReadOwnDataForCompound(EvaluationContext context, JsValue baseValue, out JsValue value)
    {
        context.PrepareFor(_expression);

        if (baseValue.IsObject())
        {
            var determinedProperty = (JsString) _determinedProperty!;
            var baseObject = baseValue.AsObjectNoTypeCheck();
            var shapeFlags = baseObject._type & (InternalTypes.ShapeMode | InternalTypes.HasLazySlots);
            if (shapeFlags == InternalTypes.ShapeMode)
            {
                var shapeObj = Unsafe.As<JsObject>(baseObject);
                var shape = shapeObj.ShapeOf;
                if (ReferenceEquals(shape, _cachedShape))
                {
                    value = shapeObj.GetSlot(_cachedShapeSlot);
                    return true;
                }

                if (shape.TryGetSlot(determinedProperty.ToString(), out var slot))
                {
                    DropReceiverEntries();
                    _cachedShape = shape;
                    _cachedShapeSlot = slot;
                    value = shapeObj.GetSlot(slot);
                    return true;
                }
            }
            else if (shapeFlags == InternalTypes.Empty
                     && (baseObject._type & (InternalTypes.PlainObject | InternalTypes.BuiltinShapeMode)) != InternalTypes.Empty)
            {
                PropertyDescriptor descriptor;
                if (ReferenceEquals(baseObject, _cachedReadObject)
                    && baseObject._propertiesVersion == _cachedReadVersion
                    && _cachedReadDescriptor is not null)
                {
                    descriptor = _cachedReadDescriptor;
                }
                else
                {
                    descriptor = baseObject.GetOwnProperty(determinedProperty);
                    if (!ReferenceEquals(descriptor, PropertyDescriptor.Undefined))
                    {
                        DropReceiverEntries();
                        _cachedReadObject = baseObject;
                        _cachedReadVersion = baseObject._propertiesVersion;
                        _cachedReadDescriptor = descriptor;
                    }
                }

                // Flags read live, as the store does: defineProperty can turn the same descriptor into an
                // accessor without moving the version.
                if (!ReferenceEquals(descriptor, PropertyDescriptor.Undefined)
                    && (descriptor._flags & PropertyFlag.NonData) == PropertyFlag.None)
                {
                    value = ObjectInstance.UnwrapJsValue(descriptor, baseObject);
                    return true;
                }
            }
        }

        value = JsValue.Undefined;
        return false;
    }
}
