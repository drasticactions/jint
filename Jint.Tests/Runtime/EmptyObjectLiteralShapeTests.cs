using Jint.Native;
using Jint.Runtime;

namespace Jint.Tests.Runtime;

/// <summary>
/// An empty object literal site that has made sixteen objects starts the next ones in shape-building
/// mode, so properties assigned afterwards intern a shared hidden class instead of a dictionary. What a
/// script can observe — key order, attributes, deletes, accessors, prototype changes, objects used as
/// maps — is the same either way.
/// </summary>
public class EmptyObjectLiteralShapeTests
{
    private const string MakeRecords = """
        function make(i) { var o = {}; o.x = i; o.y = i * 2; return o; }
        for (var i = 0; i < 20; i++) make(i);
        """;

    [Test]
    public void AHotSiteStartsItsObjectsInShapeBuildingMode()
    {
        var engine = new Engine();
        var cold = engine.Evaluate("(function () { var o = {}; o.x = 1; return o; })()").Should().BeOfType<JsObject>().Which;
        (cold._type & InternalTypes.ShapeMode).Should().Be(InternalTypes.Empty);

        engine.Execute(MakeRecords);
        var first = engine.Evaluate("make(1)").Should().BeOfType<JsObject>().Which;
        var second = engine.Evaluate("make(2)").Should().BeOfType<JsObject>().Which;
        (first._type & InternalTypes.ShapeMode).Should().NotBe(InternalTypes.Empty);
        first.ShapeOf.Should().BeSameAs(second.ShapeOf);
        engine.Evaluate("JSON.stringify(make(3))").AsString().Should().Be("""{"x":3,"y":6}""");
    }

    [Test]
    public void ObjectsOfAHotSiteBehaveAsDictionaryObjectsDo()
    {
        const string Script = """
            (function () {
                var out = [];
                for (var i = 0; i < 40; i++) {
                    var o = {};
                    o.b = 1; o[2] = 'two'; o.a = 3; o[0] = 'zero';
                    var keys = Object.keys(o).join();
                    delete o.b; o.b = 4;
                    var afterDelete = Object.keys(o).join();
                    Object.defineProperty(o, 'hidden', { value: 5, enumerable: false });
                    Object.defineProperty(o, 'g', { get: function () { return this.a + 1; }, enumerable: true, configurable: true });
                    var forIn = []; for (var k in o) forIn.push(k);
                    o.__proto__ = { inherited: 6 };
                    out.push([keys, afterDelete, forIn.join(), o.hidden, o.g, o.inherited, JSON.stringify(o)].join('|'));
                }
                return out.every(function (r) { return r === out[0]; }) ? out[0] : 'differs: ' + out.join(' / ');
            })()
            """;

        new Engine().Evaluate(Script).AsString()
            .Should().Be("""0,2,b,a|0,2,a,b|0,2,a,b,g|5|4|6|{"0":"zero","2":"two","a":3,"b":4,"g":4}""");
    }

    [Test]
    public void AnObjectUsedAsAMapKeepsEveryKey()
    {
        const string Script = """
            (function () {
                var sums = [];
                for (var round = 0; round < 30; round++) {
                    var map = {};
                    for (var i = 0; i < 200; i++) map['k' + round + '_' + i] = i;
                    var sum = 0, count = 0;
                    for (var k in map) { sum += map[k]; count++; }
                    sums.push(count + ':' + sum);
                }
                return sums.every(function (s) { return s === '200:19900'; });
            })()
            """;

        new Engine().Evaluate(Script).AsBoolean().Should().BeTrue();
    }

    [Test]
    public void ALayoutAlreadyInternedSurvivesTheFanOutLimit()
    {
        var engine = new Engine();
        engine.Execute(MakeRecords);

        // Objects of the same prototype whose first keys fill the shared root's fan-out limit.
        engine.Execute("for (var i = 0; i < 100; i++) { var m = {}; m['first' + i] = i; }");

        var record = engine.Evaluate("make(5)").Should().BeOfType<JsObject>().Which;
        (record._type & InternalTypes.ShapeMode).Should().NotBe(InternalTypes.Empty);
        engine.Evaluate("var r = make(6); r.x + r.y").AsNumber().Should().Be(18);
    }
}
