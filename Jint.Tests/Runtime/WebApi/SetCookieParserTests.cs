#nullable enable

using Jint.WebApi.Fetch;

namespace Jint.Tests.Runtime.WebApi;

/// <summary>
/// The <c>Set-Cookie</c> parser of RFC 6265bis §5.2, and the two name prefixes §5.6 refuses.
/// </summary>
/// <remarks>
/// It is Jint's own rather than <see cref="System.Net.CookieContainer"/>'s, which parses a
/// <b>comma-joined</b> header string and therefore has to guess where one value ends — the ambiguity an
/// <c>Expires</c> attribute creates every time. These are the rows that guessing gets wrong.
/// </remarks>
public class SetCookieParserTests
{
    private static SetCookie Parse(string header)
    {
        SetCookieParser.TryParse(header, out var cookie).Should().BeTrue();
        return cookie!;
    }

    [Test]
    public void ReadsTheNameValuePairAndTrimsIt()
    {
        var cookie = Parse("  name  =  value  ");
        cookie.Name.Should().Be("name");
        cookie.Value.Should().Be("value");
        cookie.Domain.Should().BeNull();
        cookie.Path.Should().BeNull();
        cookie.Expires.Should().BeNull();
        cookie.Secure.Should().BeFalse();
        cookie.HttpOnly.Should().BeFalse();
    }

    [Test]
    public void AValueMayContainAnEqualsSignAndACommaAndASpace()
    {
        Parse("a=b=c").Value.Should().Be("b=c");
        Parse("a=x,y").Value.Should().Be("x,y");
        Parse("a=x y").Value.Should().Be("x y");
    }

    [Test]
    public void APairWithNoEqualsSignIsAnEmptyName()
    {
        var cookie = Parse("justavalue");
        cookie.Name.Should().BeEmpty();
        cookie.Value.Should().Be("justavalue");
    }

    [Test]
    public void TheValuesTheSpecificationSaysToIgnore()
    {
        // "If the name string is empty and the value string is empty, ignore the set-cookie-string entirely."
        SetCookieParser.TryParse("=", out _).Should().BeFalse();
        SetCookieParser.TryParse("", out _).Should().BeFalse();

        // Step 1: a control character anywhere.
        SetCookieParser.TryParse("a=b\0c", out _).Should().BeFalse();
        SetCookieParser.TryParse("a=b\nc", out _).Should().BeFalse();
    }

    [Test]
    public void ReadsTheAttributesAndNormalizesThem()
    {
        var cookie = Parse("a=1; Domain=.Example.COM; Path=/x; Secure; HttpOnly");
        cookie.Domain.Should().Be("example.com", "the leading dot is dropped and the domain lowercased");
        cookie.Path.Should().Be("/x");
        cookie.Secure.Should().BeTrue();
        cookie.HttpOnly.Should().BeTrue();
    }

    [Test]
    public void APathThatIsNotAbsoluteIsIgnored()
    {
        Parse("a=1; Path=relative").Path.Should().BeNull();
        Parse("a=1; Path=").Path.Should().BeNull();
    }

    [Test]
    public void MaxAgeWinsOverExpires()
    {
        var expires = Parse("a=1; Expires=Wed, 09 Jun 2021 10:18:14 GMT").Expires;
        expires.Should().Be(new DateTimeOffset(2021, 6, 9, 10, 18, 14, TimeSpan.Zero));

        var both = Parse("a=1; Expires=Wed, 09 Jun 2021 10:18:14 GMT; Max-Age=3600").Expires;
        both.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(50));

        // "If delta-seconds is less than or equal to zero, let expiry-time be the earliest representable
        // date and time" — which is how a server deletes a cookie.
        Parse("a=1; Max-Age=0").Expires.Should().BeBefore(DateTimeOffset.UtcNow);
        Parse("a=1; Max-Age=-10").Expires.Should().BeBefore(DateTimeOffset.UtcNow);

        // A Max-Age that is not a number at all leaves the attribute unread.
        Parse("a=1; Max-Age=soon").Expires.Should().BeNull();

        // And one no arithmetic can hold saturates rather than throwing.
        Parse("a=1; Max-Age=9223372036854775807").Expires.Should().Be(DateTimeOffset.MaxValue);
    }

    [TestCase("Wed, 09 Jun 2021 10:18:14 GMT", 2021, 6, 9, 10, 18, 14)]
    [TestCase("Wed, 09-Jun-2021 10:18:14 GMT", 2021, 6, 9, 10, 18, 14)]
    [TestCase("Wednesday, 09-Jun-21 10:18:14 GMT", 2021, 6, 9, 10, 18, 14)]
    [TestCase("Sun Nov  6 08:49:37 1994", 1994, 11, 6, 8, 49, 37)]
    [TestCase("Thu, 01 Jan 1970 00:00:10 GMT", 1970, 1, 1, 0, 0, 10)]
    [TestCase("09 Jun 2021 10:18:14", 2021, 6, 9, 10, 18, 14)]
    public void TheCookieDateAlgorithmIsDeliberatelyLenient(string text, int year, int month, int day, int hour, int minute, int second)
    {
        SetCookieParser.TryParseCookieDate(text, out var value).Should().BeTrue();
        value.Should().Be(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero));
    }

    [TestCase("not a date")]
    [TestCase("Wed, 09 Jun 2021")]
    [TestCase("10:18:14")]
    [TestCase("Wed, 32 Jun 2021 10:18:14 GMT")]
    [TestCase("Wed, 29 Feb 2021 10:18:14 GMT")]
    [TestCase("Wed, 09 Jun 2021 25:18:14 GMT")]
    public void ADateThatNamesNoInstantIsNotOne(string text)
    {
        SetCookieParser.TryParseCookieDate(text, out _).Should().BeFalse();
    }

    [Test]
    public void ANamePrefixIsAPromiseAndABrokenOneIsIgnored()
    {
        // https://httpwg.org/http-extensions/draft-ietf-httpbis-rfc6265bis.html#name-cookie-name-prefixes
        SetCookieParser.TryParse("__Secure-a=1; Path=/", out _).Should().BeFalse();
        SetCookieParser.TryParse("__Secure-a=1; Secure; Path=/", out _).Should().BeTrue();

        SetCookieParser.TryParse("__Host-a=1; Secure; Path=/", out _).Should().BeTrue();
        SetCookieParser.TryParse("__Host-a=1; Path=/", out _).Should().BeFalse("it is not Secure");
        SetCookieParser.TryParse("__Host-a=1; Secure", out _).Should().BeFalse("its Path is not /");
        SetCookieParser.TryParse("__Host-a=1; Secure; Path=/x", out _).Should().BeFalse("its Path is not /");
        SetCookieParser.TryParse("__Host-a=1; Secure; Path=/; Domain=example.com", out _).Should().BeFalse("it is not host-only");

        // The check is case-sensitive, so a lookalike is an ordinary cookie.
        SetCookieParser.TryParse("__secure-a=1; Path=/", out _).Should().BeTrue();
    }

    [Test]
    public void AnUnknownAttributeIsSkippedRatherThanFailing()
    {
        var cookie = Parse("a=1; SameSite=Lax; Priority=High; Partitioned; Path=/x");
        cookie.Path.Should().Be("/x");
        cookie.Name.Should().Be("a");
    }
}
