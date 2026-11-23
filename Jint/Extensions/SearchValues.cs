using System.Runtime.CompilerServices;

namespace System.Buffers;

internal readonly struct StringSearchValues
{
    private readonly SearchValues<string> _data;

    internal StringSearchValues(ReadOnlySpan<string> input, StringComparison comparer)
    {
        _data = SearchValues.Create(input, comparer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(string s) => _data.Contains(s);
}
