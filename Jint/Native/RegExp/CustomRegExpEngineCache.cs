using System.Collections.Concurrent;
using Jint.Runtime.RegExp;

namespace Jint.Native.RegExp;

/// <summary>
/// Process-wide bounded cache of custom-engine compilations (<see cref="JintRegExpEngine"/>), keyed by pattern
/// and flags, the only inputs of a compilation. A compiled program is immutable and the interpreter keeps its
/// match state on the stack, so one program serves every realm and thread.
/// <para>
/// Without it, every construction recompiles: <c>RegExp.prototype[Symbol.split]</c> constructs a new splitter
/// per call, so splitting many strings on a <c>/u</c> pattern spent most of its time compiling the same pattern.
/// </para>
/// <para>
/// Bounded like <see cref="RegExpParseCache"/>: cleared once it holds <see cref="Capacity"/> entries, so a flood of
/// distinct dynamic patterns cannot grow it without limit.
/// </para>
/// </summary>
internal static class CustomRegExpEngineCache
{
    private const int Capacity = 256;

    private static readonly ConcurrentDictionary<Key, JintRegExpEngine> _cache = new();

    private readonly record struct Key(string Pattern, RegExpFlags Flags);

    public static bool TryGet(string pattern, RegExpFlags flags, out JintRegExpEngine engine)
        => _cache.TryGetValue(new Key(pattern, flags), out engine!);

    public static void Add(string pattern, RegExpFlags flags, JintRegExpEngine engine)
    {
        if (_cache.Count >= Capacity)
        {
            _cache.Clear();
        }

        _cache.TryAdd(new Key(pattern, flags), engine);
    }
}
