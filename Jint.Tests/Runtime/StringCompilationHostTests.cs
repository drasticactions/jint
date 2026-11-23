#nullable enable

using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace Jint.Tests.Runtime;

/// <summary>
/// The host hooks of Dynamic Code Brand Checks (https://tc39.es/proposal-dynamic-code-brand-checks/): what
/// <c>Host.EnsureCanCompileStrings(Realm, Realm, StringCompilation)</c> is told, and an object that
/// <c>Host.GetCodeForEval</c> says stands for code.
/// </summary>
public class StringCompilationHostTests
{
    [Test]
    public void EvalTellsTheHostItsSourceAndArgument()
    {
        var host = new RecordingHost();
        var engine = new Engine(options => options.UseHostFactory(_ => host));

        engine.Evaluate("eval('1 + 1')").AsNumber().Should().Be(2);

        var compilation = host.Compilations.Should().ContainSingle().Subject;
        compilation.Kind.Should().Be(StringCompilationKind.Eval);
        compilation.CodeString.Should().Be("1 + 1");
        compilation.BodyString.Should().Be("1 + 1");
        compilation.BodyArg.AsString().Should().Be("1 + 1");
        compilation.ParameterStrings.Should().BeEmpty();
        compilation.Direct.Should().BeTrue();
    }

    [Test]
    public void FunctionTellsTheHostItsPartsAndWholeSource()
    {
        var host = new RecordingHost();
        var engine = new Engine(options => options.UseHostFactory(_ => host));

        engine.Evaluate("new Function('a', 'b', 'return a + b')(1, 2)").AsNumber().Should().Be(3);

        var compilation = host.Compilations.Should().ContainSingle().Subject;
        compilation.Kind.Should().Be(StringCompilationKind.Function);
        compilation.ParameterStrings.Should().Equal("a", "b");
        compilation.BodyString.Should().Be("return a + b");
        compilation.CodeString.Should().Be("function anonymous(a,b\n) {\nreturn a + b\n}");
        compilation.ParameterArgs.Select(v => v.AsString()).Should().Equal("a", "b");
        compilation.Direct.Should().BeFalse();
    }

    [Test]
    public void AnObjectThatStandsForCodeIsCompiledWithoutItsToString()
    {
        var host = new RecordingHost();
        var engine = new Engine(options => options.UseHostFactory(_ => host));
        const string Forged = "({ toString() { throw new Error('not called'); } })";
        host.Code[engine.Evaluate(Forged).AsObject()] = "40 + 2";
        host.Code[engine.Evaluate(Forged).AsObject()] = "return 42";
        var (expression, body) = (host.Code.Keys.First(), host.Code.Keys.Last());
        engine.SetValue("expression", expression);
        engine.SetValue("body", body);

        engine.Evaluate("eval(expression)").AsNumber().Should().Be(42);
        engine.Evaluate("new Function(body)()").AsNumber().Should().Be(42);
        engine.Evaluate("eval({})").IsObject().Should().BeTrue();
        host.Compilations.Select(c => c.BodyArg).Should().Equal(expression, body);
    }

    [Test]
    public void TheFunctionConstructorConvertsItsArgumentsBeforeTheHostRefuses()
    {
        var engine = new Engine(options => options.Host.StringCompilationAllowed = false);

        engine.Execute("var calls = 0; try { new Function({ toString() { calls++; return 'return 1'; } }); } catch (e) {}");

        engine.Evaluate("calls").AsNumber().Should().Be(1);
    }

    private sealed class RecordingHost : Host
    {
        public List<StringCompilation> Compilations { get; } = [];

        public Dictionary<ObjectInstance, string> Code { get; } = new(ReferenceEqualityComparer.Instance);

        public override void EnsureCanCompileStrings(Realm callerRealm, Realm evalRealm, StringCompilation compilation)
        {
            Compilations.Add(compilation);
            base.EnsureCanCompileStrings(callerRealm, evalRealm, compilation);
        }

        public override string? GetCodeForEval(ObjectInstance value) => Code.TryGetValue(value, out var code) ? code : null;
    }
}
