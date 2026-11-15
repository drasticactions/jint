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
    public void AKeptEnvironmentAndTheOwnDescriptorsShareAField()
    {
        var engine = new Engine();
        engine.Execute("function make() { return function (x) { var y = x * 2; return y; }; } var f = make(); var g = make();");
        var f = (ScriptFunction) engine.GetValue("f");

        // the environment first, then descriptors: both survive, and the calls keep reusing it
        engine.Evaluate("f(1) + f(2)").AsNumber().Should().Be(6);
        f._envReuse.Should().NotBeNull();
        f._rare.Should().BeNull();
        engine.Evaluate("""
            Object.defineProperty(f, 'length', { value: 7 });
            f.prototype.k = 1;
            [f.length, f.name === '', Object.getOwnPropertyNames(f).join(), f(3), f(4)].join()
            """).AsString().Should().Be("7,true,length,name,prototype,arguments,caller,6,8");
        f._envReuse.Should().NotBeNull();
        engine.Evaluate("delete f.length; delete f.name; [f(5), f.hasOwnProperty('length'), f.hasOwnProperty('name')].join()").AsString().Should().Be("10,false,false");

        // descriptors first, then the environment
        engine.Evaluate("g.length + ':' + g.prototype.constructor.length").AsString().Should().Be("1:1");
        engine.Evaluate("g(1) + g(2) + g(3)").AsNumber().Should().Be(12);
        ((ScriptFunction) engine.GetValue("g"))._envReuse.Should().NotBeNull();
        engine.Evaluate("[g.length, Object.getOwnPropertyNames(g).join(), g(10)].join()").AsString().Should().Be("1,length,name,prototype,arguments,caller,20");
    }

    [Test]
    public void SymbolKeyedPropertiesOfAnyObjectBehaveAsOwnProperties()
    {
        var engine = new Engine();
        engine.Evaluate("""
            var a = Symbol('a'), b = Symbol('b');
            var results = [];
            function check(o) {
              o.s = 1; o[a] = 2; o[1] = 3; o[b] = 4;
              var keys = Reflect.ownKeys(o).filter(function (k) { return k === a || k === b || k === 's' || k === '1'; });
              var first = keys.map(String).join('/');
              delete o[a];
              var second = Object.getOwnPropertySymbols(o).map(String).join('/');
              o[a] = 5;
              results.push(first + ' ' + second + ' ' + o[a] + o[b] + (a in o) + Object.getOwnPropertySymbols(o).length);
            }
            check({});
            check(function () { });
            check(() => 1);
            check([]);
            check(new Map());
            check(Object.create(null));
            results.join('|')
            """).AsString().Should().Be(string.Join("|", Enumerable.Repeat("1/s/Symbol(a)/Symbol(b) Symbol(b) 54true2", 6)));

        // the built-ins' own symbols still answer, and script can replace them
        engine.Evaluate("""
            var arr = [1, 2];
            var spread = [...arr].length;
            var tag = Object.prototype.toString.call(new Map());
            var inst = arr instanceof Array;
            var it = Array.prototype[Symbol.iterator];
            Array.prototype[Symbol.iterator] = function* () { yield 9; };
            var replaced = [...arr].join();
            Array.prototype[Symbol.iterator] = it;
            [spread, tag, inst, replaced, [...arr].join(), typeof Math[Symbol.toStringTag]].join()
            """).AsString().Should().Be("2,[object Map],true,9,1,2,string");
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

    [Test]
    public void AFunctionsOwnNameLengthAndPrototypeKeepTheirDescriptorsAcrossDefinesAndDeletes()
    {
        var engine = new Engine();
        engine.Evaluate("""
            var f = function () { };
            var out = [];
            function d(p) { var x = Object.getOwnPropertyDescriptor(f, p); return x ? String(x.value) + ':' + x.writable + x.configurable : 'none'; }
            Object.defineProperty(f, 'length', { value: 7 });
            out.push(d('name'), d('length'));
            var proto = f.prototype;
            out.push(d('length'), d('name'), f.prototype === proto);
            delete f.name;
            out.push(d('name'), d('length'), f.prototype === proto);
            delete f.length;
            out.push(d('length'), f.prototype === proto);
            Object.defineProperty(f, 'name', { value: 'again' });
            out.push(d('name'), d('length'), f.prototype === proto, Object.getOwnPropertyNames(f).sort().join('|'));
            out.join()
            """).AsString().Should().Be("f:falsetrue,7:falsetrue,7:falsetrue,f:falsetrue,true,none,7:falsetrue,true,none,true,again:falsefalse,none,true,arguments|caller|name|prototype");

        engine.Evaluate("""
            var g = function () { };
            var p = g.prototype;
            [g.name, g.length, g.prototype === p, Object.getOwnPropertyNames(g).join('|')].join()
            """).AsString().Should().Be("g,0,true,length|name|prototype|arguments|caller");
        engine.Evaluate("[Math.max.name, Math.max.length, Array.name, Array.length, typeof Array.prototype.map].join()")
            .AsString().Should().Be("max,2,Array,1,function");
    }

    [Test]
    public void AFunctionKeepsTheRealmItWasCreatedIn()
    {
        var engine = new Engine();
        engine.Execute("var f = function () { return function () { }; };");
        var f = (ScriptFunction) engine.GetValue("f");
        var made = (ScriptFunction) engine.Invoke(f);
        made._realm.Should().BeSameAs(f._realm);
        made._scriptOrModule.Should().BeSameAs(f._scriptOrModule);
        made._functionOrigin.Should().BeSameAs(f._functionOrigin);
        engine.Evaluate("new ShadowRealm().evaluate('(function () { return new Error() instanceof Error; })')()").AsBoolean().Should().BeTrue();
    }
}
