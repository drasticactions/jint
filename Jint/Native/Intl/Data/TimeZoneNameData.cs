namespace Jint.Native.Intl.Data;

/// <summary>
/// The English names of a time zone that <c>timeZoneName</c> writes, read from the embedded <c>TimeZoneNames.txt</c>.
/// </summary>
internal static class TimeZoneNameData
{
    /// <summary>
    /// One zone's names. A null name is written as the localized GMT format of the offset instead.
    /// </summary>
    internal sealed record ZoneNames(
        int StandardOffsetMinutes,
        string? ShortStandard,
        string? ShortDaylight,
        string? LongStandard,
        string? LongDaylight,
        string? ShortGeneric,
        string? LongGeneric);

    private static readonly Lazy<Dictionary<string, ZoneNames>> _zones = new(Load);

    /// <summary>
    /// The names of <paramref name="timeZone"/>, or of the zone it links to (for a legacy POSIX zone, the zone it
    /// follows) when the table has no row of its own.
    /// </summary>
    internal static ZoneNames? Find(string timeZone)
    {
        var zones = _zones.Value;
        if (zones.TryGetValue(timeZone, out var names))
        {
            return names;
        }

        return TimeZoneData.TryGetFallbackTarget(timeZone, out var zone) && zones.TryGetValue(zone, out names) ? names : null;
    }

    private static Dictionary<string, ZoneNames> Load()
    {
        const string resourceName = "Jint.Native.Intl.Data.TimeZoneNames.txt";
        using var stream = typeof(TimeZoneNameData).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException("Could not load embedded Intl time zone name data.");

        using var reader = new StreamReader(stream);
        var zones = new Dictionary<string, ZoneNames>(512, StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var columns = line.Split('\t');
            if (columns.Length != 8)
            {
                continue;
            }

            zones[columns[0]] = new ZoneNames(
                int.Parse(columns[1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture),
                NullIfEmpty(columns[2]),
                NullIfEmpty(columns[3]),
                NullIfEmpty(columns[4]),
                NullIfEmpty(columns[5]),
                NullIfEmpty(columns[6]),
                NullIfEmpty(columns[7]));
        }

        return zones;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
