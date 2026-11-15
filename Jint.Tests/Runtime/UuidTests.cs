using Jint.Tests.Runtime.Domain;

namespace Jint.Tests.Runtime;

public class UuidTests : IDisposable
{
    private readonly Engine _engine;

    public UuidTests()
    {
        _engine = new Engine(o => o.AddObjectConverter(new UuidConverter()))
                .SetValue("copy", new Func<Guid, Guid>(v => new Guid(v.ToByteArray())))
            ;
        UuidConstructor.CreateUuidConstructor(_engine);
    }

    void IDisposable.Dispose()
    {
    }

    private object RunTest(string source)
    {
        return _engine.Evaluate(source).ToObject();
    }

    [Test]
    public void Empty()
    {
        RunTest($"Uuid.parse('{Guid.Empty}')").Should().Be(Guid.Empty);
        RunTest($"Uuid.Empty").Should().Be(Guid.Empty);
    }

    [Test]
    public void Random()
    {
        var actual = RunTest($"new Uuid()");
        actual.Should().NotBe(Guid.Empty);
        actual.Should().BeOfType<Guid>();
    }

    [Test]
    public void Copy()
    {
        _engine.Evaluate("const g = new Uuid();");
        _engine.Evaluate("g.toString()").AsString().Should().Be(_engine.Evaluate("copy(g).toString()").AsString());
    }

    // JsUuid is a host value of type String that is not a JsString: the string lanes of a member call, a length
    // read, a computed key and a template literal convert it like any other string-typed value.
    [Test]
    public void HostStringTypedValueTakesTheStringLanes()
    {
        _engine.Evaluate("const g = new Uuid(); const text = g.toString();");
        var text = _engine.Evaluate("text").AsString();
        _engine.Evaluate("copy(g).slice(0, 8)").AsString().Should().Be(text.Substring(0, 8));
        _engine.Evaluate("copy(g).length").AsNumber().Should().Be(text.Length);
        _engine.Evaluate("`<${copy(g)}>`").AsString().Should().Be("<" + text + ">");
        _engine.Evaluate("`${'x'.repeat(300)}${copy(g)}`").AsString().Should().Be(new string('x', 300) + text);
        _engine.Evaluate("({ [text]: 5 })[copy(g)]").AsNumber().Should().Be(5);
        _engine.Evaluate("var s = ''; for (var i = 0; i < 3; i++) s += copy(g).charAt(i); s").AsString().Should().Be(text.Substring(0, 3));
    }
}