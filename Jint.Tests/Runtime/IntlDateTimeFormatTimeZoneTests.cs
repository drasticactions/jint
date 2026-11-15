#nullable enable

using Jint.Runtime;

namespace Jint.Tests.Runtime;

/// <summary>
/// An <c>Intl.DateTimeFormat</c> without a <c>timeZone</c> option formats in, and reports, the engine's time zone
/// (<see cref="Options.TimeZone"/>), not the machine's; and <c>timeZoneName</c> writes the names browsers write with
/// ICU's English data.
/// </summary>
public class IntlDateTimeFormatTimeZoneTests
{
    private const string January = "Date.UTC(2026, 0, 15, 12)";
    private const string July = "Date.UTC(2026, 6, 15, 12)";

    private static Engine CreateEngine(string timeZone)
        => new(options => options.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZone));

    private static string Evaluate(Engine engine, string script) => engine.Evaluate(script).AsString();

    [TestCase("Asia/Tokyo", "Asia/Tokyo")]
    [TestCase("America/New_York", "America/New_York")]
    [TestCase("Etc/UTC", "UTC")]
    [TestCase("UTC", "UTC")]
    public void ResolvedOptionsReportsTheEngineTimeZone(string engineZone, string expected)
    {
        var engine = CreateEngine(engineZone);
        Evaluate(engine, "new Intl.DateTimeFormat().resolvedOptions().timeZone").Should().Be(expected);
    }

    [TestCase("Asia/Tokyo", "9:00 PM")]
    [TestCase("America/New_York", "7:00 AM")]
    [TestCase("Europe/London", "12:00 PM")]
    public void FormatUsesTheEngineTimeZone(string engineZone, string expected)
    {
        var engine = CreateEngine(engineZone);
        Evaluate(engine, $"new Intl.DateTimeFormat('en-US', {{ hour: 'numeric', minute: 'numeric' }}).format(new Date({January}))")
            .Should().Be(expected);
        Evaluate(engine, $"new Intl.DateTimeFormat('en-US', {{ hour: 'numeric', minute: 'numeric' }}).format({January})")
            .Should().Be(expected);
        Evaluate(engine, $"new Date({January}).toLocaleTimeString('en-US', {{ hour: 'numeric', minute: 'numeric' }})")
            .Should().Be(expected);
    }

    [Test]
    public void FormatOfUndefinedReadsTheEngineClock()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var clock = new FixedTimeSystem(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero), zone);
        var engine = new Engine(options =>
        {
            options.TimeZone = zone;
            options.TimeSystem = clock;
        });

        Evaluate(engine, "new Intl.DateTimeFormat('en-US', { dateStyle: 'short', timeStyle: 'short' }).format()")
            .Should().Be("2/3/01, 1:05 PM");
    }

    private sealed class FixedTimeSystem(DateTimeOffset now, TimeZoneInfo zone)
        : DefaultTimeSystem(zone, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The names Chromium and Node.js (ICU 78, CLDR 48) write for 15 January and 15 July 2026.
    [TestCase("UTC", "short", "UTC", "UTC")]
    [TestCase("UTC", "long", "Coordinated Universal Time", "Coordinated Universal Time")]
    [TestCase("UTC", "shortOffset", "GMT+0", "GMT+0")]
    [TestCase("UTC", "longOffset", "GMT+00:00", "GMT+00:00")]
    [TestCase("UTC", "shortGeneric", "GMT+0", "GMT+0")]
    [TestCase("UTC", "longGeneric", "GMT+00:00", "GMT+00:00")]
    [TestCase("America/New_York", "short", "EST", "EDT")]
    [TestCase("America/New_York", "long", "Eastern Standard Time", "Eastern Daylight Time")]
    [TestCase("America/New_York", "shortOffset", "GMT-5", "GMT-4")]
    [TestCase("America/New_York", "longOffset", "GMT-05:00", "GMT-04:00")]
    [TestCase("America/New_York", "shortGeneric", "ET", "ET")]
    [TestCase("America/New_York", "longGeneric", "Eastern Time", "Eastern Time")]
    [TestCase("Asia/Tokyo", "short", "GMT+9", "GMT+9")]
    [TestCase("Asia/Tokyo", "long", "Japan Standard Time", "Japan Standard Time")]
    [TestCase("Asia/Tokyo", "shortOffset", "GMT+9", "GMT+9")]
    [TestCase("Asia/Tokyo", "longOffset", "GMT+09:00", "GMT+09:00")]
    [TestCase("Asia/Tokyo", "shortGeneric", "Japan Time", "Japan Time")]
    [TestCase("Asia/Tokyo", "longGeneric", "Japan Standard Time", "Japan Standard Time")]
    [TestCase("Europe/London", "short", "GMT", "GMT+1")]
    [TestCase("Europe/London", "long", "Greenwich Mean Time", "British Summer Time")]
    [TestCase("Europe/London", "shortOffset", "GMT+0", "GMT+1")]
    [TestCase("Europe/London", "longOffset", "GMT+00:00", "GMT+01:00")]
    [TestCase("Europe/London", "shortGeneric", "United Kingdom Time", "United Kingdom Time")]
    [TestCase("Europe/London", "longGeneric", "United Kingdom Time", "United Kingdom Time")]
    [TestCase("Europe/Berlin", "short", "GMT+1", "GMT+2")]
    [TestCase("Australia/Sydney", "long", "Australian Eastern Daylight Time", "Australian Eastern Standard Time")]
    [TestCase("Asia/Kolkata", "shortOffset", "GMT+5:30", "GMT+5:30")]
    public void TimeZoneNameIsTheOneBrowsersWrite(string zone, string style, string january, string july)
    {
        // Once as the timeZone option and once as the engine's own zone, under a different engine zone each time.
        var script = $$"""
            [{{January}}, {{July}}].map(t => new Intl.DateTimeFormat('en-US', { timeZone: tz, timeZoneName: '{{style}}' })
                .formatToParts(t).find(p => p.type === 'timeZoneName').value).join('/')
            """;
        var engine = CreateEngine(zone == "Asia/Tokyo" ? "America/New_York" : "Asia/Tokyo");
        engine.SetValue("tz", zone);
        Evaluate(engine, script).Should().Be($"{january}/{july}");

        var engineInZone = CreateEngine(zone);
        engineInZone.SetValue("tz", Jint.Native.JsValue.Undefined);
        Evaluate(engineInZone, script).Should().Be($"{january}/{july}");
    }

    [Test]
    public void ARepeatedHourIsNamedForTheInstant()
    {
        // 1:30 AM happens twice in New York on 1 November 2026, first in daylight and then in standard time.
        var engine = CreateEngine("UTC");
        Evaluate(engine, """
            [Date.UTC(2026, 10, 1, 5, 30), Date.UTC(2026, 10, 1, 6, 30)]
                .map(t => new Intl.DateTimeFormat('en-US', { timeZone: 'America/New_York', timeStyle: 'long' }).format(t))
                .join(' / ')
            """).Should().Be("1:30:00 AM EDT / 1:30:00 AM EST");
    }

    [Test]
    public void ARangeNamesEachEndForItsOwnOffset()
    {
        var engine = CreateEngine("UTC");
        Evaluate(engine, """
            new Intl.DateTimeFormat('en-US', { timeZone: 'America/New_York', hour: 'numeric', timeZoneName: 'short' })
                .formatRange(Date.UTC(2026, 2, 1), Date.UTC(2026, 6, 1))
            """).Should().Be("2/28/2026, 7 PM EST\u2009\u2013\u20096/30/2026, 8 PM EDT");
    }
}
