using System.Runtime.CompilerServices;

namespace Jint.Runtime.Environments;

/// <summary>
/// Where an identifier read found a binding that its environment keeps by name rather than in a slot: an environment
/// with more bindings than the slot layout takes or with function declarations, which is what the scope of a bundled
/// or minified script is (a module wrapper function declaring hundreds of names that every inner function reads).
/// The memo pins the chain from the reading environment the way <see cref="SlotLayoutMemo"/> does, by the slot-name
/// arrays of the environments in between, and the binding by its entry in the holder's dictionary.
/// </summary>
/// <remarks>
/// An environment's name set is its slot names plus its dictionary keys, and its slot names never change, so the
/// intermediates do not own the name while each has the recorded slot names (none of which is the name) and no
/// dictionary entry of the name. Sloppy direct eval and AnnexB block functions add names to an existing environment
/// through its dictionary and bump <see cref="Engine._envBindingInjectionEpoch"/>. The holder is whatever declarative
/// environment the chain reaches: it owns the binding exactly when its dictionary still holds an entry stored under
/// the recorded name at the recorded index (see <see cref="Collections.StringDictionarySlim{TValue}.GetValueRefAt"/>),
/// so another instance of the same scope is served as well, and a removed, moved or reallocated entry is a miss.
/// </remarks>
internal sealed class DictionaryBindingLayout
{
    private readonly int _hops;
    private readonly Key[]? _names0;
    private readonly Key[]? _names1;
    private readonly Key[]? _names2;
    private readonly int _entryIndex;
    private readonly string _entryName;
    private readonly int _injectionEpoch;
    private readonly Key _key;

    private DictionaryBindingLayout(int hops, Key[]? names0, Key[]? names1, Key[]? names2, int entryIndex, string entryName, int injectionEpoch, Key key)
    {
        _hops = hops;
        _names0 = names0;
        _names1 = names1;
        _names2 = names2;
        _entryIndex = entryIndex;
        _entryName = entryName;
        _injectionEpoch = injectionEpoch;
        _key = key;
    }

    /// <summary>
    /// The layout from <paramref name="env"/> to <paramref name="target"/>, whose dictionary holds the binding of
    /// <paramref name="key"/>, or null when there is none: the binding is not in a hashed dictionary, the chain is
    /// longer than the slot caches walk, or an environment on it is not declarative or could own the name.
    /// </summary>
    internal static DictionaryBindingLayout? TryCreate(Engine engine, Environment env, DeclarativeEnvironment target, Key key)
    {
        if (target._dictionary?._dictionary is not { } dictionary)
        {
            return null;
        }

        var entryIndex = dictionary.FindEntryIndex(key, out var entryName);
        if (entryIndex < 0)
        {
            return null;
        }

        Key[]? names0 = null;
        Key[]? names1 = null;
        Key[]? names2 = null;
        var hops = 0;
        for (var e = env; !ReferenceEquals(e, target); e = e._outerEnv)
        {
            if (e is null
                || hops == SlotLocationCache.MaxChainDepth - 1
                || e is not DeclarativeEnvironment intermediate
                || SlotLayoutMemo.DictionaryOwns(intermediate, key)
                || intermediate._slotNames is { } slotNames && SlotLayoutMemo.Contains(slotNames, key))
            {
                return null;
            }

            var names = intermediate._slotNames;
            switch (hops)
            {
                case 0: names0 = names; break;
                case 1: names1 = names; break;
                default: names2 = names; break;
            }

            hops++;
        }

        return new DictionaryBindingLayout(hops, names0, names1, names2, entryIndex, entryName!, engine._envBindingInjectionEpoch, key);
    }

    /// <summary>The binding when the chain from <paramref name="env"/> still has this layout and its holder still has the entry, otherwise a null ref.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref Binding Follow(Engine engine, Environment env)
    {
        if (engine._envBindingInjectionEpoch == _injectionEpoch)
        {
            var holder = _hops == 0 ? env : Walk(env);
            if (holder is DeclarativeEnvironment { _dictionary._dictionary: { } dictionary })
            {
                return ref dictionary.GetValueRefAt(_entryIndex, _entryName);
            }
        }

        return ref Unsafe.NullRef<Binding>();
    }

    private Environment? Walk(Environment env)
    {
        if (!Matches(env, _names0))
        {
            return null;
        }

        var e = env._outerEnv;
        if (_hops > 1)
        {
            if (!Matches(e, _names1))
            {
                return null;
            }

            e = e!._outerEnv;
            if (_hops > 2)
            {
                if (!Matches(e, _names2))
                {
                    return null;
                }

                e = e!._outerEnv;
            }
        }

        return e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Matches(Environment? e, Key[]? names)
    {
        return e is DeclarativeEnvironment declarative
               && ReferenceEquals(declarative._slotNames, names)
               && !SlotLayoutMemo.DictionaryOwns(declarative, _key);
    }
}
