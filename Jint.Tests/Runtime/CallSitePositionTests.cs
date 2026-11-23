namespace Jint.Tests.Runtime;

/// <summary>
/// The line and column a stack frame reports for the call that made it, which are V8's (Chromium 158 gives every
/// expected value here, for the same scripts): the property name of a member call, the start of an identifier callee,
/// the opening parenthesis of the arguments for any other callee or an optional call, and the start of a <c>new</c>
/// expression.
/// </summary>
public class CallSitePositionTests
{
    // where(depth) returns "line:column" of the frame depth levels below its own: by default, of the call to where.
    private const string Prelude = "function where(depth) { var l = new Error().stack.split('\\n'); for (var i = 0; i < l.length; i++) if (/\\bwhere\\b/.test(l[i])) return /:(\\d+:\\d+)\\)?$/.exec(l[i + (depth || 1)])[1]; }\nvar o = { m: where, ['k']: where, f() { return where; } };\n";

    [TestCase("o.m()", "5:3")]
    [TestCase("o\n    .\n       m  ()", "7:8")]
    [TestCase("   where  ()", "5:4")]
    [TestCase("o  ['k']  ()", "5:11")]
    [TestCase("o['k']()", "5:7")]
    [TestCase("o.f()  ()", "5:8")]
    [TestCase("o.f()()", "5:6")]
    [TestCase("o?.m()", "5:4")]
    [TestCase("where?.()", "5:8")]
    [TestCase("(function () { return where(2); })()", "5:35")]
    [TestCase("(function () { return where(2); }\n)\n  ()", "7:3")]
    [TestCase("(0, o.m)()", "5:9")]
    [TestCase("o.m(1,\n   2)", "5:3")]
    [TestCase("  new (function () { this.s = where(2); })().s", "5:3")]
    public void FrameReportsTheCallSiteAtV8sPosition(string call, string expected)
    {
        var engine = new Engine();
        var position = engine.Evaluate(Prelude + "var x;\nvar r =\n" + call + ";\nr").AsString();
        position.Should().Be(expected);
    }

    [Test]
    public void ConstructorFrameReportsTheStartOfTheNewExpression()
    {
        var engine = new Engine();
        var position = engine.Evaluate(Prelude + "function N() { this.s = where(2); }\nvar x =   new   N().s" + ";\nx").AsString();
        position.Should().Be("4:11");
    }

    [Test]
    public void BuiltInFrameThatRanNoScriptReportsItsCallSite()
    {
        var engine = new Engine();
        var stack = engine.Evaluate("var s;\ntry { JSON.parse('{'); } catch (e) { s = e.stack; }\ns").AsString();
        stack.Split('\n')[0].Should().EndWith(":2:12)");
    }
}
