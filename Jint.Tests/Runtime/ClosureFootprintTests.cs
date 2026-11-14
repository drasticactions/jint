#nullable enable

using Jint.Native.Function;

namespace Jint.Tests.Runtime;

/// <summary>
/// A closure a script makes for every object (a method a constructor defines) defers what most closures never
/// use: its empty own <c>name</c> is a descriptor only once read, what only methods, classes and constructors
/// carry is held apart, and its first call keeps no environment for a next one.
/// </summary>
public class ClosureFootprintTests
{
    [Test]
    public void AnAnonymousFunctionsEmptyNameBehavesAsAnOwnProperty()
    {
        var engine = new Engine();
        engine.Evaluate("""
            var o = {};
            o.f = function () { };
            var d = Object.getOwnPropertyDescriptor(o.f, 'name');
            [d.value === '', d.writable, d.enumerable, d.configurable, Object.getOwnPropertyNames(o.f).indexOf('name') >= 0, o.f.hasOwnProperty('name')].join()
            """).AsString().Should().Be("true,false,false,true,true,true");

        // NamedEvaluation still names an anonymous function, and a defined or deleted name is honoured
        engine.Evaluate("var named = function () { }; named.name").AsString().Should().Be("named");
        engine.Evaluate("""
            var g = (0, function () { });
            var before = g.name;
            Object.defineProperty(g, 'name', { value: 'renamed' });
            var h = (0, function () { });
            delete h.name;
            [before === '', g.name, h.hasOwnProperty('name'), h.name].join()
            """).AsString().Should().Be("true,renamed,false,");
        engine.Evaluate("String((0, function () { }).bind(null).name)").AsString().Should().Be("bound ");
        engine.Evaluate("var k = (0, () => 1); k.name === '' && k.hasOwnProperty('name')").AsBoolean().Should().BeTrue();
    }

    [Test]
    public void AClosureKeepsAnEnvironmentOnlyFromItsSecondCall()
    {
        var engine = new Engine();
        engine.Execute("function make() { return function (x) { var y = x + 1; return y; }; } var f = make();");
        var f = (ScriptFunction) engine.GetValue("f");

        engine.Evaluate("f(1)").AsNumber().Should().Be(2);
        f._envReuse.Should().BeNull();

        engine.Evaluate("f(2)").AsNumber().Should().Be(3);
        f._envReuse.Should().NotBeNull();

        engine.Evaluate("f(3) + f(4)").AsNumber().Should().Be(9);
    }

    [Test]
    public void APlainClosureHasNoRareData()
    {
        var engine = new Engine();
        engine.Execute("""
            function Make() { this.m = function () { return 1; }; }
            var plain = new Make().m;
            plain();
            plain.length; plain.name;
            var method = { m() { return super.toString === Object.prototype.toString; } }.m;
            function sloppy() { }
            sloppy.arguments;
            class C { #p = 1; static s() { } get p() { return this.#p; } }
            """);

        ((ScriptFunction) engine.GetValue("plain"))._rare.Should().BeNull();
        ((ScriptFunction) engine.GetValue("method"))._rare.Should().NotBeNull();
        ((ScriptFunction) engine.GetValue("sloppy"))._rare.Should().NotBeNull();

        engine.Evaluate("method()").AsBoolean().Should().BeTrue();
        engine.Evaluate("new C().p").AsNumber().Should().Be(1);
        engine.Evaluate("sloppy.arguments === null && Object.getOwnPropertyNames(sloppy).join()").AsString().Should().Be("length,name,prototype,arguments,caller");
        engine.Evaluate("delete sloppy.caller; Object.getOwnPropertyNames(sloppy).join()").AsString().Should().Be("length,name,prototype,arguments");
    }
}
