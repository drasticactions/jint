#nullable enable

using Jint.Native;
using Jint.Runtime.Environments;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Tests.Runtime;

/// <summary>
/// What a script does with the methods a constructor makes for every object (the module pattern of tween and
/// animation libraries) comes out the same while the engine spends less on them: the call environment of a method
/// nothing can capture serves the first call of the same method of the next object, a member-heavy constructor sizes
/// its objects' slot storage once, a global read under a new call environment each time keeps no memo per call,
/// <c>instanceof</c> through the inherited <c>Symbol.hasInstance</c> makes no call, and <c>parseFloat</c> of a number
/// gives the number.
/// </summary>
public class PerObjectMethodTests
{
    private const string Tween = """
        var log = [];
        function Tween(start) {
            var _value = start;
            var _end = 0;
            var _chained = [];
            this.to = function (end) { var before = _value; _end = end; return this; };
            this.step = function (t) { var v = _value + (_end - _value) * t; _value = v; return v; };
            this.chain = function () { _chained = arguments; return this; };
            this.chained = function () { return _chained.length; };
            this.peer = function (other) { var mine = _value; return other.value() + mine; };
            this.value = function () { var v = _value; return v; };
            this.fail = function () { var v = _value; throw new Error('fail ' + v); };
        }
        """;

    [Test]
    public void EachObjectsMethodsSeeTheirOwnObject()
    {
        var engine = new Engine();
        engine.Execute(Tween);
        engine.Evaluate("""
            var out = [];
            for (var i = 0; i < 50; i++) {
                var a = new Tween(i), b = new Tween(100 + i);
                a.to(i + 10).chain(b, b);
                out.push([a.step(0.5), a.peer(b), a.chained(), b.chained()].join(':'));
                try { b.fail(); } catch (e) { out.push(e.message); }
                out.push(b.value(), a.value());
            }
            out.slice(0, 8).join() + '|' + out.slice(-4).join()
            """).AsString().Should().Be("5:105:2:0,fail 100,100,5,6:107:2:0,fail 101,101,6|54:203:2:0,fail 149,149,54");
    }

    [Test]
    public void TheFirstCallOfAnotherObjectsMethodTakesTheEnvironmentTheLastOneLeft()
    {
        var engine = new Engine();
        var seen = new List<Environment>();
        engine.SetValue("capture", new Action(() => seen.Add(engine.ExecutionContext.LexicalEnvironment)));
        engine.Execute("""
            function Make(v) { this.m = function (x) { var y = x + v; capture(); return y; }; }
            var objects = [];
            for (var i = 0; i < 4; i++) objects.push(new Make(i));
            var sums = objects.map(function (o, i) { return o.m(i); }).join();
            """);

        engine.Evaluate("sums").AsString().Should().Be("0,2,4,6");
        seen.Should().HaveCount(4);
        seen.Should().AllBeOfType<FunctionEnvironment>();
        seen.Cast<object>().Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(1);

        // a method with a closure inside keeps an environment of its own per call: the closure may outlive it
        seen.Clear();
        engine.Execute("""
            function Keep(v) { this.m = function (x) { var y = x + v; capture(); return function () { return y; }; }; }
            var kept = [new Keep(1), new Keep(2), new Keep(3)].map(function (o, i) { return o.m(i); });
            """);
        engine.Evaluate("kept.map(function (f) { return f(); }).join()").AsString().Should().Be("1,3,5");
        seen.Cast<object>().Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(3);
    }

    [Test]
    public void AnArgumentsObjectASavedCallLeftKeepsItsValues()
    {
        var engine = new Engine();
        engine.Execute("""
            function Keep() { var saved; this.save = function (a, b) { saved = arguments; a = a + '!'; return this; }; this.read = function () { return saved[0] + saved[1] + saved.length; }; }
            var objects = [];
            for (var i = 0; i < 20; i++) objects.push(new Keep().save('a' + i, 'b' + i, 3));
            """);

        engine.Evaluate("objects[0].read() + ',' + objects[19].read()").AsString().Should().Be("a0!b03,a19!b193");
    }

    [Test]
    public void ANestedCallOfTheSameMethodOfAnotherObjectGetsAnotherEnvironment()
    {
        var engine = new Engine();
        engine.Execute("""
            function Node(v, next) { this.sum = function () { var own = v; return next ? own + next.sum() : own; }; }
            var list = null;
            for (var i = 1; i <= 30; i++) list = new Node(i, list);
            """);

        engine.Evaluate("list.sum()").AsNumber().Should().Be(465);
        engine.Evaluate("new Node(5, new Node(6, null)).sum() + list.sum()").AsNumber().Should().Be(476);
    }

    [Test]
    public void AMemberHeavyConstructorsObjectsKeepTheirMembers()
    {
        var engine = new Engine();
        engine.Execute("""
            function Wide(i) { for (var k = 0; k < 40; k++) this['m' + k] = i * 100 + k; }
            var objects = [];
            for (var i = 0; i < 30; i++) objects.push(new Wide(i));
            objects[3].extra = 'x';
            """);

        engine.Evaluate("objects[29].m39 + ',' + objects[0].m4 + ',' + Object.keys(objects[3]).length + ',' + objects[3].extra")
            .AsString().Should().Be("2939,4,41,x");
        engine.Evaluate("Object.keys(objects[7]).slice(30).join()").AsString().Should().Be("m30,m31,m32,m33,m34,m35,m36,m37,m38,m39");
    }

    [Test]
    public void AGlobalReadUnderANewEnvironmentEachCallFollowsShadowing()
    {
        var engine = new Engine();
        engine.Execute("""
            var g = 'global';
            function read() { var local = 1; return g; }
            function each() { var seen = ''; for (var k in { a: 1, b: 2 }) { seen += g; } return seen; }
            for (var i = 0; i < 300; i++) { read(); each(); }
            function shadowed() { eval("var g = 'eval'"); return (function () { var local = 1; return g; })(); }
            """);

        engine.Evaluate("read() + ',' + each() + ',' + shadowed() + ',' + read()").AsString().Should().Be("global,globalglobal,eval,global");
        engine.Execute("g = 'changed'");
        engine.Evaluate("read() + ',' + each()").AsString().Should().Be("changed,changedchanged");
    }

    [Test]
    public void InstanceofThroughTheInheritedHasInstanceIsOrdinaryHasInstance()
    {
        var engine = new Engine();
        engine.Evaluate("""
            function A() { }
            var bound = A.bind(null);
            var custom = { [Symbol.hasInstance]: function (v) { return v === 1; } };
            function B() { }
            Object.defineProperty(B, Symbol.hasInstance, { value: function () { return true; } });
            var badPrototype = function () { };
            badPrototype.prototype = 1;
            var threw;
            try { ({}) instanceof badPrototype; } catch (e) { threw = e instanceof TypeError; }
            [new A() instanceof A, new A() instanceof bound, [] instanceof Array, 1 instanceof custom, 2 instanceof custom, 3 instanceof B, ({}) instanceof A, threw].join()
            """).AsString().Should().Be("true,true,true,true,false,true,false,true");
    }

    [TestCase("parseFloat(-0)", 0, false)]
    [TestCase("parseFloat(0)", 0, false)]
    [TestCase("parseFloat(1e21)", 1e21, false)]
    [TestCase("parseFloat(1e-7)", 1e-7, false)]
    [TestCase("parseFloat(0.1 + 0.2)", 0.30000000000000004, false)]
    [TestCase("parseFloat(-123.456)", -123.456, false)]
    [TestCase("parseFloat(5e-324)", 5e-324, false)]
    [TestCase("parseFloat(Infinity)", double.PositiveInfinity, false)]
    [TestCase("parseFloat(-Infinity)", double.NegativeInfinity, false)]
    [TestCase("parseFloat(NaN)", double.NaN, false)]
    [TestCase("parseFloat(-0, 10)", 0, false)]
    public void ParseFloatOfANumberIsTheNumber(string script, double expected, bool negativeZero)
    {
        var value = new Engine().Evaluate(script).AsNumber();
        if (double.IsNaN(expected))
        {
            double.IsNaN(value).Should().BeTrue();
            return;
        }

        value.Should().Be(expected);
        double.IsNegative(value).Should().Be(negativeZero || (expected < 0));
    }

    [Test]
    public void ParseFloatOfOtherValuesStillGoesThroughTheString()
    {
        new Engine().Evaluate("[parseFloat('  -0'), 1 / parseFloat('-0'), parseFloat(new Number(2.5)), parseFloat({ toString: function () { return '7px'; } }), parseFloat(true)].join()")
            .AsString().Should().Be("0,-Infinity,2.5,7,NaN");
    }
}
