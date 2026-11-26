using System.Text;
using System.Runtime.CompilerServices;
using Jint.Collections;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Kernels;

/// <summary>An object literal (https://tc39.es/ecma262/#sec-object-initializer), built as JintObjectExpression builds it.</summary>
internal sealed class KernelObjectLiteral : KernelExpression
{
    private const byte Init = 0;
    private const byte Spread = 1;
    private const byte Method = 2;
    private const byte Accessor = 3;

    // Objects an empty literal makes before it starts them in shape-building mode, as JintEmptyObjectExpression.
    private const int ShapePromoteThreshold = 16;

    private readonly byte[] _kinds;
    private readonly KernelExpression?[] _values;
    private readonly KernelExpression?[] _keys;
    private readonly JsString?[] _names;
    private readonly bool[] _namesFunctions;
    private readonly bool[] _setsPrototype;
    private readonly JintFunctionDefinition?[] _accessors;

    // The last syntax element the interpreter leaves before a spread's copy or a computed key's conversion.
    private readonly Node[] _stepLocations;
    private readonly bool _canBuildFast;
    private readonly bool _shapedNormalTarget;
    private readonly Key[]? _fastKeys;
    private readonly bool _shapeBacked;
    private readonly bool _empty;
    private int _evaluations;

    private Shape? _cachedShape;
    private ObjectInstance? _cachedShapeProto;

    private KernelObjectLiteral(ObjectExpression expression, KernelExpression?[] values, KernelExpression?[] keys, bool startHot) : base(expression)
    {
        var properties = expression.Properties;
        var count = properties.Count;
        _empty = count == 0;
        _evaluations = startHot ? ShapePromoteThreshold : 0;
        _values = values;
        _keys = keys;
        _kinds = new byte[count];
        _names = new JsString?[count];
        _namesFunctions = new bool[count];
        _setsPrototype = new bool[count];
        _accessors = new JintFunctionDefinition?[count];

        // The same analysis as JintObjectExpression's constructor.
        _canBuildFast = true;
        _shapedNormalTarget = true;
        var hasSpread = false;
        for (var i = 0; i < count; i++)
        {
            string? propName = null;
            if (properties[i] is ObjectProperty p)
            {
                if (!p.Computed && p.Key is Literal literal)
                {
                    propName = AstExtensions.LiteralKeyToString(literal);
                }
                else if (!p.Computed && p.Key is Identifier identifier)
                {
                    propName = identifier.Name;
                    _canBuildFast &= !string.Equals(propName, "__proto__", StringComparison.Ordinal);
                }

                _names[i] = propName is null ? null : JsString.Create(propName);
                if (p.Kind is PropertyKind.Init)
                {
                    _kinds[i] = p.Method ? Method : Init;
                    _canBuildFast &= p.Value is Expression && !p.Value.IsFunctionDefinition();
                    _namesFunctions[i] = p.Value is Expression value && value.IsAnonymousFunctionDefinition();
                    _setsPrototype[i] = string.Equals(propName, "__proto__", StringComparison.Ordinal) && !p.Computed && !p.Shorthand;
                }
                else
                {
                    _kinds[i] = Accessor;
                    _canBuildFast = false;
                }

                if (p.Kind != PropertyKind.Init || p.Method || (propName is not null && propName.Length > 0 && char.IsDigit(propName[0])))
                {
                    _shapedNormalTarget = false;
                }
            }
            else
            {
                _kinds[i] = Spread;
                _canBuildFast = false;
                hasSpread = true;
            }

            _canBuildFast &= propName != null;
        }

        if (!hasSpread)
        {
            _shapedNormalTarget = false;
        }

        _stepLocations = new Node[count];
        Node location = expression;
        for (var i = 0; i < count; i++)
        {
            if (properties[i] is SpreadElement spread)
            {
                location = KernelLocation.After(spread.Argument) ?? location;
                _stepLocations[i] = location;
                continue;
            }

            var property = (ObjectProperty) properties[i];
            if (property.Computed)
            {
                location = KernelLocation.After((Expression) property.Key) ?? location;
            }

            _stepLocations[i] = location;
            if (_kinds[i] == Init)
            {
                location = KernelLocation.After(property.Value as Expression) ?? location;
            }
        }

        if (_canBuildFast && !_empty)
        {
            var fastKeys = new Key[count];
            var shapeable = true;
            for (var i = 0; i < count; i++)
            {
                var name = _names[i]!.ToString();
                fastKeys[i] = name;
                shapeable &= !Shape.IsIntegerIndexLikeKey(name);
            }

            var distinct = true;
            for (var i = 1; i < count && distinct; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (fastKeys[i] == fastKeys[j])
                    {
                        distinct = false;
                        break;
                    }
                }
            }

            _fastKeys = fastKeys;
            _shapeBacked = shapeable && distinct;
        }
    }

    /// <summary>The literal with its values and computed keys compiled by <paramref name="compile"/>.</summary>
    internal static KernelObjectLiteral Create(ObjectExpression expression, Func<Expression, KernelExpression> compile, bool startHot)
    {
        var properties = expression.Properties;
        var values = new KernelExpression?[properties.Count];
        var keys = new KernelExpression?[properties.Count];
        for (var i = 0; i < values.Length; i++)
        {
            if (properties[i] is SpreadElement spread)
            {
                values[i] = compile(spread.Argument);
                continue;
            }

            var property = (ObjectProperty) properties[i];
            if (property.Method || property.Kind != PropertyKind.Init)
            {
                // The method or accessor evaluates its own computed key.
                continue;
            }

            if (property.Computed)
            {
                keys[i] = compile((Expression) property.Key);
            }

            values[i] = compile((Expression) property.Value);
        }

        return new KernelObjectLiteral(expression, values, keys, startHot);
    }

    /// <summary>Whether every property of the literal can be built by the kernel.</summary>
    internal static bool IsSupported(ObjectExpression expression)
    {
        foreach (var property in expression.Properties)
        {
            if (property is ObjectProperty { Kind: PropertyKind.Init, Method: false } p && p.Value is not Expression)
            {
                return false;
            }

            if (property is ObjectProperty { Value: ClassExpression })
            {
                return false;
            }
        }

        return true;
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        if (_empty)
        {
            return CreateEmpty(frame.Engine);
        }

        if (_canBuildFast)
        {
            return _shapeBacked ? BuildShaped(frame) : BuildDictionary(frame);
        }

        return BuildNormal(frame);
    }

    private JsObject CreateEmpty(Engine engine)
    {
        if (_evaluations >= ShapePromoteThreshold)
        {
            return engine.RunningRealm.Intrinsics.Object.ConstructShapeBuilding();
        }

        _evaluations++;
        return new JsObject(engine);
    }

    private JsObject BuildShaped(KernelFrame frame)
    {
        var engine = frame.Engine;
        var keys = _fastKeys!;
        var proto = engine.RunningRealm.Intrinsics.Object.PrototypeObject;
        var shape = _cachedShape;
        if (shape is null || !ReferenceEquals(_cachedShapeProto, proto))
        {
            shape = engine.GetEmptyShape(proto);
            for (var i = 0; i < keys.Length; i++)
            {
                shape = shape.Add(in keys[i]);
            }

            _cachedShape = shape;
            _cachedShapeProto = proto;
        }

        var obj = new JsObject(engine);
        obj.InitShape(shape);
        var values = _values;
        for (var i = 0; i < values.Length; i++)
        {
            obj.SetSlot(i, Box(values[i]!.Evaluate(frame, out var n), n));
        }

        return obj;
    }

    private JsObject BuildDictionary(KernelFrame frame)
    {
        var keys = _fastKeys!;
        var obj = new JsObject(frame.Engine);
        var properties = new PropertyDictionary(keys.Length, checkExistingKeys: true);
        var values = _values;
        for (var i = 0; i < values.Length; i++)
        {
            var value = Box(values[i]!.Evaluate(frame, out var n), n);
            properties[keys[i]] = new PropertyDescriptor(value, PropertyFlag.ConfigurableEnumerableWritable);
        }

        obj.SetProperties(properties);
        return obj;
    }

    /// <summary>https://tc39.es/ecma262/#sec-object-initializer-runtime-semantics-propertydefinitionevaluation</summary>
    private ObjectInstance BuildNormal(KernelFrame frame)
    {
        var engine = frame.Engine;
        var objects = engine.RunningRealm.Intrinsics.Object;
        ObjectInstance obj = _shapedNormalTarget ? objects.ConstructShapeBuilding() : objects.Construct(_kinds.Length);
        var properties = ((ObjectExpression) _expression).Properties;
        for (var i = 0; i < _kinds.Length; i++)
        {
            switch (_kinds[i])
            {
                case Spread:
                    {
                        var source = Box(_values[i]!.Evaluate(frame, out var n), n);
                        frame.Context.PrepareFor(_stepLocations[i]);
                        if (source is ObjectInstance instance)
                        {
                            instance.CopyDataProperties(obj, excludedItems: null);
                        }
                        else if (!source.IsNullOrUndefined())
                        {
                            TypeConverter.ToObject(engine.Realm, source).CopyDataProperties(obj, excludedItems: null);
                        }

                        break;
                    }

                case Method:
                    ClassDefinition.MethodDefinitionEvaluation(engine, obj, (ObjectProperty) properties[i], enumerable: true);
                    break;

                case Init:
                    {
                        var name = Name(frame, i);
                        var value = Box(_values[i]!.Evaluate(frame, out var n), n);
                        if (_setsPrototype[i])
                        {
                            if (value.IsObject() || value.IsNull())
                            {
                                obj.SetPrototypeOf(value);
                            }

                            break;
                        }

                        if (_namesFunctions[i])
                        {
                            ((Function) value).SetFunctionName(name);
                        }

                        obj.CreateDataPropertyOrThrow(name, value);
                        break;
                    }

                default:
                    DefineAccessor(engine, obj, (ObjectProperty) properties[i], i);
                    break;
            }
        }

        return obj;
    }

    /// <summary>The key of property <paramref name="index"/>: its name, or its computed key converted.</summary>
    private JsValue Name(KernelFrame frame, int index)
    {
        if (_names[index] is { } name)
        {
            return name;
        }

        var key = Box(_keys[index]!.Evaluate(frame, out var n), n);
        frame.Context.PrepareFor(_stepLocations[index]);
        return TypeConverter.ToPropertyKey(key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DefineAccessor(Engine engine, ObjectInstance obj, ObjectProperty property, int index)
    {
        var propName = _names[index] ?? TypeConverter.ToPropertyKey(property.TryGetKey(engine));
        var function = _accessors[index] ??= new JintFunctionDefinition((IFunction) property.Value, sourceTextNode: property);
        var intrinsics = engine.Realm.Intrinsics.Function;
        var closure = intrinsics.OrdinaryFunctionCreate(
            intrinsics.PrototypeObject,
            function,
            function.ThisMode,
            engine.ExecutionContext.LexicalEnvironment,
            engine.ExecutionContext.PrivateEnvironment);

        closure.SetFunctionName(propName, property.Kind == PropertyKind.Get ? "get" : "set");
        closure.MakeMethod(obj);

        var descriptor = new GetSetPropertyDescriptor(
            get: property.Kind == PropertyKind.Get ? closure : null,
            set: property.Kind == PropertyKind.Set ? closure : null,
            PropertyFlag.Enumerable | PropertyFlag.Configurable);

        obj.DefinePropertyOrThrow(propName, descriptor);
    }
}

/// <summary>An array literal, holes and spreads included, built as JintArrayExpression builds it.</summary>
internal sealed class KernelArrayLiteral : KernelExpression
{
    private readonly KernelExpression?[] _elements;
    private readonly KernelSpreadList? _spreads;

    public KernelArrayLiteral(ArrayExpression expression, KernelExpression?[] elements) : base(expression)
    {
        _elements = elements;
        if (KernelSpreadList.SpreadsOf(expression.Elements) is { } spreads)
        {
            _spreads = new KernelSpreadList(expression, expression.Elements, spreads, elements);
        }
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        if (_spreads is not null)
        {
            return new JsArray(frame.Engine, _spreads.BuildArray(frame, rent: false, out _));
        }

        var elements = _elements;
        var values = elements.Length == 0 ? [] : new JsValue[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            if (elements[i] is { } element)
            {
                values[i] = Box(element.Evaluate(frame, out var n), n);
            }
        }

        return new JsArray(frame.Engine, values);
    }
}

/// <summary>
/// A list of values with spreads (https://tc39.es/ecma262/#sec-runtime-semantics-argumentlistevaluation), made as
/// ExpressionCache makes one: an array whose iteration is the built-in one is read by index.
/// </summary>
internal sealed class KernelSpreadList
{
    private readonly Node[] _locations;
    private readonly KernelExpression?[] _items;
    private readonly SpreadElement?[] _spreads;
    private readonly JintSpreadExpression?[] _handlers;

    /// <summary>The list of <paramref name="items"/>, read after <paramref name="start"/>, the nodes they compile.</summary>
    public KernelSpreadList(Node start, IReadOnlyList<Expression?> nodes, SpreadElement?[] spreads, KernelExpression?[] items)
    {
        _items = items;
        _spreads = spreads;
        _handlers = new JintSpreadExpression?[items.Length];
        _locations = new Node[items.Length];
        var location = start;
        for (var i = 0; i < items.Length; i++)
        {
            location = KernelLocation.After(nodes[i] is SpreadElement spread ? spread.Argument : nodes[i]) ?? location;
            _locations[i] = location;
        }
    }

    /// <summary>The spread elements among <paramref name="nodes"/>, by index, or null where there is none.</summary>
    internal static SpreadElement?[]? SpreadsOf<T>(in NodeList<T> nodes) where T : Node?
    {
        SpreadElement?[]? spreads = null;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is SpreadElement spread)
            {
                (spreads ??= new SpreadElement?[nodes.Count])[i] = spread;
            }
        }

        return spreads;
    }

    /// <summary>
    /// The values in an array of their count, which a list of at most four values whose only spread is the last
    /// takes from the engine's pool where <paramref name="rent"/> lets it (<paramref name="rented"/>).
    /// </summary>
    internal JsValue[] BuildArray(KernelFrame frame, bool rent, out bool rented)
    {
        rented = false;
        var items = _items;
        var last = items.Length - 1;
        if (last > 2 || _spreads[last] is null || Array.Exists(items, item => item is null))
        {
            return Build(frame).ToArray();
        }

        for (var i = 0; i < last; i++)
        {
            if (_spreads[i] is not null)
            {
                return Build(frame).ToArray();
            }
        }

        var engine = frame.Engine;
        var a0 = last > 0 ? KernelExpression.Box(items[0]!.Evaluate(frame, out var n0), n0) : null;
        var a1 = last > 1 ? KernelExpression.Box(items[1]!.Evaluate(frame, out var n1), n1) : null;
        var value = KernelExpression.Box(items[last]!.Evaluate(frame, out var n), n);
        frame.Context.PrepareFor(_locations[last]);
        JsValue? iteratorMethod = null;
        if (value is JsArray array && array.HasOriginalIterator(out iteratorMethod))
        {
            var length = array.GetLength();
            if (engine._hasMaxArraySize)
            {
                ArrayInstance.ValidateListLength(engine, (ulong) last + length);
            }

            if (length <= int.MaxValue - 2)
            {
                var count = last + (int) length;
                rented = rent && count <= 4;
                var values = rented ? engine._jsValueArrayPool.RentArray(count) : new JsValue[count];
                if (last > 0)
                {
                    values[0] = a0!;
                    if (last > 1)
                    {
                        values[1] = a1!;
                    }
                }

                for (var j = 0; j < (int) length; j++)
                {
                    values[last + j] = array.GetElement((uint) j);
                }

                return values;
            }
        }

        var target = new List<JsValue>(last + 4);
        if (last > 0)
        {
            target.Add(a0!);
            if (last > 1)
            {
                target.Add(a1!);
            }
        }

        var iterator = (_handlers[last] ??= new JintSpreadExpression(_spreads[last]!)).GetIterator(engine.Realm, value, iteratorMethod);
        ExpressionCache.SpreadInto(engine, target, iterator);
        if (engine._hasMaxArraySize)
        {
            ArrayInstance.ValidateListLength(engine, (uint) target.Count);
        }

        return target.ToArray();
    }

    /// <summary>The values in order, a hole as null.</summary>
    internal List<JsValue> Build(KernelFrame frame)
    {
        var engine = frame.Engine;
        var limited = engine._hasMaxArraySize;
        var items = _items;
        var target = new List<JsValue>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is not { } item)
            {
                target.Add(null!);
                continue;
            }

            var value = KernelExpression.Box(item.Evaluate(frame, out var n), n);
            if (_spreads[i] is not { } spread)
            {
                target.Add(value);
                continue;
            }

            frame.Context.PrepareFor(_locations[i]);
            JsValue? iteratorMethod = null;
            if (value is JsArray array && array.HasOriginalIterator(out iteratorMethod))
            {
                var length = array.GetLength();
                if (limited)
                {
                    ArrayInstance.ValidateListLength(engine, (ulong) (uint) target.Count + length);
                }

                for (uint j = 0; j < length; ++j)
                {
                    target.Add(array.GetElement(j));
                }
            }
            else
            {
                var iterator = (_handlers[i] ??= new JintSpreadExpression(spread)).GetIterator(engine.Realm, value, iteratorMethod);
                ExpressionCache.SpreadInto(engine, target, iterator);
            }
        }

        if (limited)
        {
            ArrayInstance.ValidateListLength(engine, (uint) target.Count);
        }

        return target;
    }
}

/// <summary>A template literal, built as JintTemplateLiteralExpression builds it.</summary>
internal sealed class KernelTemplate : KernelExpression
{
    private readonly string?[] _quasis;
    private readonly KernelExpression[] _substitutions;
    private readonly Node[] _locations;

    public KernelTemplate(TemplateLiteral expression, KernelExpression[] substitutions) : base(expression)
    {
        _substitutions = substitutions;
        _quasis = new string?[expression.Quasis.Count];
        for (var i = 0; i < _quasis.Length; i++)
        {
            _quasis[i] = expression.Quasis[i].Value.Cooked;
        }

        _locations = new Node[substitutions.Length];
        Node location = expression;
        for (var i = 0; i < _locations.Length; i++)
        {
            location = KernelLocation.After(expression.Expressions[i]) ?? location;
            _locations[i] = location;
        }
    }

    internal override JsValue? Evaluate(KernelFrame frame, out double number)
    {
        number = 0;
        var context = frame.Context;
        using var sb = new ValueStringBuilder();
        var quasis = _quasis;
        var substitutions = _substitutions;
        JsString? deferred = null;
        var pending = 0;
        long offset = 0;
        for (var i = 0; i < quasis.Length; i++)
        {
            var cooked = quasis[i];
            JintTemplateLiteralExpression.CheckLength(context, offset + sb.Length + (cooked?.Length ?? 0));
            sb.Append(cooked);
            if (i >= substitutions.Length)
            {
                continue;
            }

            var value = Box(substitutions[i].Evaluate(frame, out var n), n);
            string text;
            if (value.IsString() && value is JsString str)
            {
                text = str._value;
                if (text is null || text.Length >= JsString.MinDeferredConcatenationLength)
                {
                    if (str.Length >= JsString.MinDeferredConcatenationLength)
                    {
                        JintTemplateLiteralExpression.CheckLength(context, offset + sb.Length + str.Length);
                        deferred = JintTemplateLiteralExpression.Join(context, deferred, sb.AsSpan(pending), str);
                        pending = sb.Length;
                        offset = deferred.Length - (long) pending;
                        continue;
                    }

                    text = str.ToString();
                }
            }
            else
            {
                context.PrepareFor(_locations[i]);
                text = TypeConverter.ToStringNonString(value);
            }

            JintTemplateLiteralExpression.CheckLength(context, offset + sb.Length + text.Length);
            sb.Append(text);
        }

        if (deferred is null)
        {
            return JsString.Create(sb.ToString());
        }

        return JintTemplateLiteralExpression.Join(context, deferred, sb.AsSpan(pending), right: null);
    }
}
