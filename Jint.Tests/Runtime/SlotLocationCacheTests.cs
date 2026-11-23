namespace Jint.Tests.Runtime;

/// <summary>
/// Pins SlotLocationCache behavior for handler trees shared across function instances
/// (per-engine definition reuse): a node whose cached environment is unreachable from the
/// current chain must re-resolve against it — declining forever would silently disable every
/// slot-backed fast lane from the second re-evaluation of a prepared script onward.
/// </summary>
public class SlotLocationCacheTests
{
    [Test]
    public void RepeatedPreparedEvaluationsKeepProducingCorrectResults()
    {
        var engine = new Engine();
        var prepared = Engine.PrepareScript("""
            function f() {
                var n = 0;
                for (var i = 0; i < 1000; i++) { n += i; }
                return n;
            }
            f();
            """);

        // the cached-definition handler tree serves a fresh function instance (fresh env) per
        // evaluation; each must resolve slots against its own chain
        for (var e = 0; e < 5; e++)
        {
            engine.Evaluate(prepared).AsNumber().Should().Be(499500);
        }
    }

    [Test]
    public void AlternatingFunctionInstancesSharingOneTreeStayCorrect()
    {
        var engine = new Engine();
        // two instances of the same nested declaration alternate calls: the shared tree's
        // slot caches flip between the instances' environments and must stay correct
        var result = engine.Evaluate("""
            (function () {
                function make(base) {
                    function inner() {
                        var acc = 0;
                        for (var i = 0; i < 100; i++) { acc += base; }
                        return acc;
                    }
                    return inner;
                }
                var a = make(1), b = make(1000);
                var sum = 0;
                for (var r = 0; r < 4; r++) { sum += a() + b(); }
                return sum;
            })()
            """).AsNumber();

        result.Should().Be(4 * (100 + 100_000));
    }

    [Test]
    public void AlternatingClosureInstancesReadingOuterBindingsStayCorrect()
    {
        var engine = new Engine();
        // hops-1 closure reads through the chain memo: two closure instances of the same
        // definition alternate at ONE callsite, so the shared node's pinned chain flips
        // between the instances' environments on every call and must re-validate, not
        // serve the other instance's binding
        var result = engine.Evaluate("""
            (function () {
                function make(table) {
                    return function () {
                        var acc = '';
                        for (var i = 0; i < 8; i++) { acc += table[i % table.length]; }
                        return acc;
                    };
                }
                var a = make(['A']), b = make(['B']);
                var got = '';
                for (var r = 0; r < 3; r++) { got += a() + '|' + b() + ';'; }
                return got;
            })()
            """).AsString();

        result.Should().Be("AAAAAAAA|BBBBBBBB;AAAAAAAA|BBBBBBBB;AAAAAAAA|BBBBBBBB;");
    }

    [Test]
    public void MethodsMadeForEveryObjectReadTheirOwnObjectsBindings()
    {
        var engine = new Engine();
        // the constructor's methods run under a chain that is new for every object, so the shared nodes switch
        // environments on every call: each read must land in the calling object's environment, at hop 0 (the
        // method's own variables), hop 1 (the constructor's) and hop 2 (through a block in the method)
        var result = engine.Evaluate("""
            (function () {
                function Counter(start) {
                    var count = start, step = start * 10;
                    this.bump = function () {
                        var before = count;
                        { let twice = step * 2; count += step + twice - step * 2; }
                        return before + ':' + count;
                    };
                }
                var counters = [];
                for (var i = 1; i <= 4; i++) { counters.push(new Counter(i)); }
                var out = [];
                for (var round = 0; round < 3; round++) {
                    for (var c = 0; c < counters.length; c++) { out.push(counters[c].bump()); }
                }
                return out.join(',');
            })()
            """).AsString();

        result.Should().Be("1:11,2:22,3:33,4:44,11:21,22:42,33:63,44:84,21:31,42:62,63:93,84:124");
    }

    [Test]
    public void ARecordedLayoutStopsServingAfterABindingIsInjected()
    {
        var engine = new Engine();
        // the readers switch closure instances, which records the layout of their chain; a sloppy direct eval
        // that injects a var anywhere must stop it from serving, and a reader whose own environment declares the
        // name is never served the outer binding
        var result = engine.Evaluate("""
            (function () {
                var out = [];
                function make(value) {
                    var name = value;
                    return function (x) { var local = x; return name + '/' + local; };
                }
                function shadowing(value) {
                    var name = value;
                    return function (x) { var name = 'own'; return name + '/' + x; };
                }
                var a = make('a'), b = make('b');
                out.push(a(1), b(2), a(3), b(4));
                (function () { var unrelated = 0; eval("var injected = 1;"); })();
                out.push(a(5), b(6));
                var s = shadowing('outer'), t = shadowing('outer');
                out.push(s(7), t(8), s(9));
                return out.join(',');
            })()
            """).AsString();

        result.Should().Be("a/1,b/2,a/3,b/4,a/5,b/6,own/7,own/8,own/9");
    }

    [Test]
    public void SloppyDirectEvalShadowingVarInvalidatesChainMemoMidLoop()
    {
        var engine = new Engine();
        // sloppy direct eval injects `captured` into the enclosing FUNCTION env mid-loop:
        // the pre-eval iterations pin a chain memo to the outer binding; post-injection the
        // epoch bump must force the walk to see the new shadowing binding immediately
        var result = engine.Evaluate("""
            (function () {
                var captured = 'outer';
                function reader() {
                    var seen = '';
                    for (var i = 0; i < 6; i++) {
                        seen += captured;
                        if (i === 2) { eval("var captured = 'inner';"); }
                        seen += ',';
                    }
                    return seen;
                }
                return reader() + '|' + captured;
            })()
            """).AsString();

        // i=0..2 read the outer binding (the eval at i===2 runs AFTER the read); the
        // injected var initializes to 'inner' and shadows from the i=3 read onward, and
        // the outer binding must remain untouched
        result.Should().Be("outer,outer,outer,inner,inner,inner,|outer");
    }

    [Test]
    public void SloppyDirectEvalHoistedVarShadowsFromInjectionOn()
    {
        var engine = new Engine();
        // hoisting-only variant: `var captured;` without initializer still creates the
        // shadowing binding (initialized to undefined by EvalDeclarationInstantiation),
        // flipping subsequent memo-guarded reads from 'outer' to undefined
        var result = engine.Evaluate("""
            (function () {
                var captured = 'outer';
                function reader() {
                    var seen = '';
                    for (var i = 0; i < 4; i++) {
                        seen += captured + ',';
                        if (i === 1) { eval("var captured;"); }
                    }
                    return seen;
                }
                return reader();
            })()
            """).AsString();

        result.Should().Be("outer,outer,undefined,undefined,");
    }

    [Test]
    public void WithBlockAroundClosureReadIsNeverSkipped()
    {
        var engine = new Engine();
        // the same identifier node runs both under a with-ObjectEnvironment and without it:
        // a chain pinned outside the with must never validate inside it (the with-object can
        // shadow dynamically), and property addition mid-loop must be honored immediately
        var result = engine.Evaluate("""
            (function () {
                var captured = 'free';
                var obj = {};
                function reader(useWith) {
                    var seen = '';
                    if (useWith) {
                        with (obj) {
                            for (var i = 0; i < 4; i++) {
                                seen += captured + ',';
                                if (i === 1) { obj.captured = 'shadowed'; }
                            }
                        }
                    } else {
                        for (var j = 0; j < 2; j++) { seen += captured + ','; }
                    }
                    return seen;
                }
                // warm the memo without the with-block, then run under it, then without again
                return reader(false) + '|' + reader(true) + '|' + reader(false);
            })()
            """).AsString();

        result.Should().Be("free,free,|free,free,shadowed,shadowed,|free,free,");
    }

    [Test]
    public void DeeplyNestedClosureChainsResolveCorrectly()
    {
        var engine = new Engine();
        // reads at hop distances beyond MaxChainDepth must keep falling through to full
        // resolution and stay correct; distances within the bound exercise the memo
        var result = engine.Evaluate("""
            (function () {
                var deep = 'root';
                function l1() {
                    var a1 = 1;
                    function l2() {
                        var a2 = 2;
                        function l3() {
                            var a3 = 3;
                            function l4() {
                                var a4 = 4;
                                function l5() {
                                    var acc = '';
                                    for (var i = 0; i < 3; i++) {
                                        acc += deep + (a1 + a2 + a3 + a4);
                                    }
                                    return acc;
                                }
                                return l5();
                            }
                            return l4();
                        }
                        return l3();
                    }
                    return l2();
                }
                return l1() + '|' + l1();
            })()
            """).AsString();

        result.Should().Be("root10root10root10|root10root10root10");
    }

    [Test]
    public void PooledLoopEnvironmentsReattachedAcrossEntriesKeepMemoValid()
    {
        var engine = new Engine();
        // let-bound loop bodies get per-node pooled block environments re-attached across
        // loop entries: the pinned chain's start link is the SAME instance on a fresh entry,
        // so the memo re-validates against current outer pointers and must stay correct
        // across function instance switches too
        var result = engine.Evaluate("""
            (function () {
                function make(base) {
                    return function () {
                        var acc = 0;
                        for (let i = 0; i < 50; i++) {
                            const step = 1;
                            acc += base + step;
                        }
                        return acc;
                    };
                }
                var a = make(10), b = make(1000);
                return a() + b() + a();
            })()
            """).AsNumber();

        result.Should().Be(50 * 11 + 50 * 1001 + 50 * 11);
    }

    [Test]
    public void SlotLanesSurviveRepeatedPreparedEvaluations()
    {
        var engine = new Engine();
        var prepared = Engine.PrepareScript("""
            function f() {
                var n = 0;
                for (var i = 0; i < 20000; i++) { n += 1; }
                return n;
            }
            f();
            """);

        // evaluation 1 uses the first-build tree, 2 the freshly cached tree; from 3 on the
        // cached tree runs under a different environment than the one that populated its
        // slot caches — the lanes must re-resolve and stay unboxed instead of falling to
        // the materializing generic path (which allocates a JsNumber per iteration here)
        for (var e = 0; e < 3; e++)
        {
            engine.Evaluate(prepared);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        engine.Evaluate(prepared);
        var perEvaluation = GC.GetAllocatedBytesForCurrentThread() - before;

        perEvaluation.Should().BeLessThan(150_000, $"expected slot lanes to stay live across re-evaluations; allocated {perEvaluation} bytes in one evaluation");
    }
}
