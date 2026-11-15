using Jint.Native;

namespace Jint.Tests.Runtime;

/// <summary>
/// Pins the numeric subtree lane (JintExpression.EvaluateNumeric): an arithmetic operand of an arithmetic,
/// relational or compound-assignment operator hands its number over as a raw double, and only the value that is
/// stored or escapes is boxed. Every observable step must stay that of the generic path: getter and valueOf order,
/// string concatenation, BigInt arithmetic and its TypeErrors, -0 and NaN, and suspension inside the subtree. The
/// expected strings are what V8 prints for the same scripts.
/// </summary>
// Forced collections affect the whole process, so run without other fixtures in flight.
[NonParallelizable]
public class NumericSubtreeLaneTests
{
    private const string Show = "function show(v) { return typeof v + ':' + (Object.is(v, -0) ? '-0' : String(v)); }";

    [TestCase(false)]
    [TestCase(true)]
    public void OperandsAndConversionsRunInSpecOrder(bool operatorOverloading)
    {
        var engine = CreateEngine(operatorOverloading);
        var result = engine.Evaluate("""
            (function () {
              var log = [];
              function v(name, value) { return { valueOf: function () { log.push('valueOf ' + name); return value; } }; }
              var o = {};
              ['a', 'b', 'c', 'd'].forEach(function (k, i) {
                Object.defineProperty(o, k, { get: function () { log.push('get ' + k); return v(k, i + 2); } });
              });
              var r1 = o.a * o.b + o.c * o.d;
              var r2 = o.a * o.b - -o.c / o.d;
              var r3 = (o.a - o.b) * (o.c % o.d) < o.a * o.b;
              var r4 = +o.a * 2 + -o.b;
              return [r1, r2, r3, r4].join(',') + ' | ' + log.join(';');
            })()
            """).AsString();

        result.Should().Be(
            "26,6.8,true,1 | get a;get b;valueOf a;valueOf b;get c;get d;valueOf c;valueOf d;"
            + "get a;get b;valueOf a;valueOf b;get c;valueOf c;get d;valueOf d;"
            + "get a;get b;valueOf a;valueOf b;get c;get d;valueOf c;valueOf d;"
            + "get a;get b;valueOf a;valueOf b;get a;valueOf a;get b;valueOf b");
    }

    [Test]
    public void ValuesMatchTheGenericOperators()
    {
        var engine = new Engine();
        var result = engine.Evaluate(Show + """
            (function () {
              var o = { x: 1.5, y: 2.5, z: 0, s: 'px', n: NaN, big: 2147483647, inf: Infinity, b1: 2n, b2: 3n };
              var out = [];
              out.push(show(o.x * o.y + o.y * o.x));
              out.push(show(o.x * o.y + o.s));
              out.push(show(o.s + o.x * o.y));
              out.push(show(o.x * o.y + o.x * o.y + o.s));
              out.push(show(o.s + o.x * o.y + o.x));
              out.push(show(-(o.z) * 1));
              out.push(show(-o.z));
              out.push(show(o.z * -1 + o.z));
              out.push(show(1 / (o.z * -5 * 1)));
              out.push(show(1 / -(o.x * o.z)));
              out.push(show(o.n * o.x + 1));
              out.push(show(-(o.n * o.x)));
              out.push(show(o.big * o.big + 1 === 4611686014132420609));
              out.push(show(o.big * o.big * o.big - o.big));
              out.push(show(o.inf - o.inf * 1));
              out.push(show(o.x * o.y % (o.y - o.x)));
              out.push(show(-o.x % o.x));
              out.push(show((o.x - o.y) * 0));
              out.push(show(o.b1 * o.b2 + o.b1));
              out.push(show(-o.b1 * o.b2));
              out.push(show(+o.s * 2 + 1));
              out.push(show(+'12' * o.x));
              out.push(show(o.x * 2 > o.y * 1));
              out.push(show(o.x * 2 <= o.y + 0.5));
              out.push(show(o.x * 2 >= o.n * 1));
              out.push(show(o.x / o.z + o.y));
              out.push(show(o.x * o.y + undefined));
              out.push(show(o.x * o.y + null));
              out.push(show(o.x * o.y + true));
              out.push(show(o.x - o.y + [3]));
              out.push(show((o.x * o.y) + { toString: function () { return 'T'; } }));
              try { out.push(show(o.b1 * o.b2 + o.x * o.y)); } catch (e) { out.push(e.constructor.name); }
              try { out.push(show(o.x * o.y - o.b1)); } catch (e) { out.push(e.constructor.name); }
              try { out.push(show(+o.b1 * 2)); } catch (e) { out.push(e.constructor.name); }
              try { out.push(show(o.x * Symbol() + 1)); } catch (e) { out.push(e.constructor.name); }
              try { out.push(show(o.b1 / (o.b1 - o.b1))); } catch (e) { out.push(e.constructor.name); }
              return out.join(' ');
            })()
            """).AsString();

        result.Should().Be(
            "number:7.5 string:3.75px string:px3.75 string:7.5px string:px3.751.5 number:-0 number:-0 number:0 "
            + "number:-Infinity number:-Infinity number:NaN number:NaN boolean:true number:9.903520300447984e+27 "
            + "number:NaN number:0.75 number:-0 number:-0 bigint:8 bigint:-6 number:NaN number:18 boolean:true "
            + "boolean:true boolean:false number:Infinity number:NaN number:3.75 number:4.75 string:-13 string:3.75T "
            + "TypeError TypeError TypeError TypeError RangeError");
    }

    [Test]
    public void CompoundAssignmentsTakeANumericRightHandSide()
    {
        var engine = new Engine();
        var result = engine.Evaluate(Show + """
            (function () {
              var out = [];
              var p = { x: 1.25, vx: 0.5, s: 'a', b: 5n, z: 0 };
              var dt = 0.1;
              p.x += p.vx * dt; out.push(show(p.x));
              p.x -= p.vx * dt + p.vx; out.push(show(p.x));
              p.x *= -p.vx * 2; out.push(show(p.x));
              p.x /= p.vx - p.vx; out.push(show(p.x));
              p.z *= -p.vx * 1; out.push(show(p.z));
              p.s += p.vx * 2; out.push(show(p.s));
              p.x = 7; p.x %= p.vx * 4 + 1; out.push(show(p.x));
              p.x = 3; p.x **= p.vx * 4; out.push(show(p.x));
              p.x = 6; p.x <<= p.vx * 2 + 1; out.push(show(p.x));
              try { p.b += p.vx * 2; out.push(show(p.b)); } catch (e) { out.push(e.constructor.name + ' ' + show(p.b)); }
              var log = [];
              var q = { get x() { log.push('get'); return { valueOf: function () { log.push('valueOf x'); return 10; } }; }, set x(v) { log.push('set ' + v); } };
              q.x += { valueOf: function () { log.push('valueOf r'); return 2; } } * 3 - 1;
              out.push(log.join(';'));
              var x = 2, y = 0.5, s = 'k';
              x += y * 3 - 1; out.push(show(x));
              x *= -(y * 2); out.push(show(x));
              s += y * 4; out.push(show(s));
              var u = 0; u *= -(y * 1); out.push(show(u));
              var bi = 2n; try { bi += y * 2; } catch (e) { out.push(e.constructor.name + ' ' + show(bi)); }
              var arr = [1.5, 2.5]; arr[0] += arr[1] * 2; out.push(show(arr[0]));
              return out.join(' ');
            })()
            """).AsString();

        result.Should().Be(
            "number:1.3 number:0.75 number:-0.75 number:-Infinity number:-0 string:a1 number:1 number:9 number:24 "
            + "TypeError bigint:5 get;valueOf r;valueOf x;set 15 number:2.5 number:-2.5 string:k2 number:-0 "
            + "TypeError bigint:2 number:6.5");
    }

    [Test]
    public void SuspensionInsideASubtreeResumesWithItsOperands()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            (function () {
              var o = { a: 1.5, b: 2.5 };
              function* g() { var r = o.a * (yield 1) + o.b * (yield 2); o.a += (yield 3) * o.b; return r + ',' + o.a + ',' + (-(yield 4) * o.b); }
              var it = g(); it.next(); it.next(4); it.next(5);
              it.next(2);
              return it.next(3).value;
            })()
            """).AsString();

        result.Should().Be("18.5,6.5,-7.5");
    }

    [Test]
    public void DeclarationsAndAssignmentsStoreNumbersUnboxed()
    {
        var engine = new Engine();
        var result = engine.Evaluate(Show + """
            (function () {
              var out = [];
              var o = { x: 1.5, y: 2.5, z: 0, s: 'q', b: 4n };
              function f() {
                var dx = o.x - o.y, dy = o.y * o.x, dz = -o.z, ds = o.s + o.x * 2, db = o.b * 2n;
                let lx = o.x * o.y, ly = -(o.z * 1), ls = o.x * 2 + o.s;
                const cx = o.x / o.y, cz = o.z * -1, cs = o.s + -o.x;
                var t;
                t = o.x * 4 - 1;
                var captured = function () { return dx + lx + cx + t; };
                out.push(show(dx), show(dy), show(dz), show(ds), show(db), show(lx), show(ly), show(ls), show(cx), show(cz), show(cs), show(t), show(captured()));
                out.push(show(typeof cx), show(1 / cz), show(1 / dz), show(1 / ly));
                try { cx = 1; } catch (e) { out.push(e.constructor.name); }
                try { cx += o.x * 2; } catch (e) { out.push(e.constructor.name); }
                try { cx++; } catch (e) { out.push(e.constructor.name); }
                out.push(show(cx));
                dx += o.x * 2; lx *= o.y - 2; t = -t; t = t + o.x * 0.5;
                out.push(show(dx), show(lx), show(t), show([dx, lx, cx].join('|')));
                t = o.s + o.x * 2; out.push(show(t));
                t = o.b * 3n; out.push(show(t));
                try { t = o.b * o.x; } catch (e) { out.push(e.constructor.name + ' ' + show(t)); }
                var fns = [];
                for (let i = 0; i < 3; i++) { let k = i * 1.5 + o.x; const c = k * 2; fns.push(function () { return k + ',' + c; }); }
                out.push(fns.map(function (g) { return g(); }).join(';'));
                var w = { wv: 0 };
                with (w) { var wv = o.x * 3; }
                out.push(show(w.wv), show(wv));
                try { (function () { 'use strict'; tdz = o.x * 2; let tdz; })(); } catch (e) { out.push(e.constructor.name); }
                try { (function () { let q = q * 2; })(); } catch (e) { out.push(e.constructor.name); }
                var a = 1, b = 2;
                a = b = o.x * o.y; out.push(show(a), show(b));
                var arr = [];
                for (var j = 0; j < 3; j++) { var m = j * o.x - 1; arr.push(m); }
                out.push(arr.join(','));
              }
              f(); f();
              return out.join(' ');
            })()
            """).AsString();

        // the function runs twice, so the second run reads the slots the first run's lanes left
        const string Once = "number:-1 number:3.75 number:-0 string:q3 bigint:8 number:3.75 number:-0 string:3q number:0.6 number:-0 string:q-1.5 number:5 number:8.35 string:number number:-Infinity number:-Infinity number:-Infinity TypeError TypeError TypeError number:0.6 number:2 number:1.875 number:-4.25 string:2|1.875|0.6 string:q3 bigint:12 TypeError bigint:12 1.5,3;3,6;4.5,9 number:4.5 undefined:undefined ReferenceError ReferenceError number:3.75 number:3.75 -1,0.5,2";
        result.Should().Be(Once + " " + Once);
    }

    [Test]
    public void IntermediateResultsAreNotAllocated()
    {
        using var engine = new Engine();
        var product = engine.Evaluate("(function (o) { return o.x * o.y; })");
        var sumOfProducts = engine.Evaluate("(function (o) { return o.x * o.y + o.y * o.x - -o.x / o.y; })");
        var compoundLeaf = engine.Evaluate("(function (o) { o.x += o.y; })");
        var compoundProduct = engine.Evaluate("(function (o) { o.x += o.y * 0.5 - o.y * o.y; })");
        var varLocals = engine.Evaluate("(function (o) { var dx = o.x - o.y, dy = o.y * 0.5; var d = dx * dx + dy * dy; d = d * 2 - dx; return d; })");
        var lexicalLocals = engine.Evaluate("(function (o) { const dx = o.x - o.y; let dy = o.y * 0.5; dy = dy * dy - dx; return dx * dx + dy; })");
        var o = engine.Evaluate("({ x: 1.25, y: 1.000001 })");

        var oneBox = Measure(() => engine.Call(product, JsValue.Undefined, [o]));
        Measure(() => engine.Call(sumOfProducts, JsValue.Undefined, [o])).Should().Be(oneBox);
        Measure(() => engine.Call(varLocals, JsValue.Undefined, [o])).Should().Be(oneBox);
        Measure(() => engine.Call(lexicalLocals, JsValue.Undefined, [o])).Should().Be(oneBox);

        var storedBox = Measure(() => engine.Call(compoundLeaf, JsValue.Undefined, [o]));
        Measure(() => engine.Call(compoundProduct, JsValue.Undefined, [o])).Should().Be(storedBox);
        oneBox.Should().BeGreaterThan(0);
    }

    private static Engine CreateEngine(bool operatorOverloading) =>
        operatorOverloading ? new Engine(static options => options.Interop.AllowOperatorOverloading = true) : new Engine();

    private static long Measure(Action action)
    {
        // warm the call sites first, so the measured calls take their steady-state lanes
        for (var i = 0; i < 100; i++)
        {
            action();
        }

        Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            action();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Collect();
        return allocated;
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }
}
