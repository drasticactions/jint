#nullable enable

using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime;

namespace Jint.Tests.Runtime;

/// <summary>
/// The first call <c>o.m(...)</c> of a deferred closure whose body cannot reach its own function object runs
/// without creating the function the slot defers, which a later read or call then creates as before. Nothing a
/// script or a host can see may tell the difference.
/// </summary>
public class DeferredClosureCallTests
{
    private const string Constructor = """
        function T(x) {
          var v = x;
          this.get = function () { return v; };
          this.set = function (n) { v = n; return this; };
          this.self = function () { return this; };
          this.strictThis = function () { 'use strict'; return this; };
          this.me = function () { return this.me; };
          this.callee = function () { return arguments.callee; };
          this.strictArgs = function () { 'use strict'; return arguments.length; };
          this.ev = function (s) { return eval(s); };
          this.mk = function () { return function () { return v; }; };
          this.nt = function () { return new.target; };
          this.fail = function () { throw new Error('boom'); };
          this.rec = function (n) { return n === 0 ? 0 : this.rec(n - 1) + 1; };
        }
        """;

    // Builds enough objects for the constructor to build in shape mode, and calls every method once so that each
    // definition has its State: before that, a call creates the function as it always did.
    private const string Warm = """
        for (var w = 0; w < 20; w++) new T(0);
        (function () {
          var t = new T(0);
          t.get(); t.set(1); t.self(); t.strictThis(); t.me(); t.callee(); t.strictArgs(); t.ev('1'); t.mk()(); t.nt();
          try { t.fail(); } catch (e) { }
          t.rec(1);
        })();
        """;

    private static Engine Create(Action<Options>? options = null)
    {
        var engine = options is null ? new Engine() : new Engine(options);
        engine.Execute(Constructor);
        engine.Execute(Warm);
        return engine;
    }

    private static int SlotOf(Engine engine, string name) => (int) engine.Evaluate($"Object.keys(new T(0)).indexOf('{name}')").AsNumber();

    [Test]
    public void AFirstCallLeavesTheSlotDeferred()
    {
        var engine = Create();
        var t = (JsObject) engine.Evaluate("var t = new T(1); t");
        var slot = SlotOf(engine, "get");

        engine.Evaluate("t.get()").AsNumber().Should().Be(1);
        t.GetSlot(slot).Should().BeOfType<JsObject.DeferredClosures>();

        // The second call creates the slot's function, as a read does.
        engine.Evaluate("t.get()").AsNumber().Should().Be(1);
        t.GetSlot(slot).Should().BeOfType<ScriptFunction>();
        engine.Evaluate("t.get === t.get").AsBoolean().Should().BeTrue();
    }

    [Test]
    public void AReadAfterTheCallCreatesOneFunction()
    {
        var engine = Create();
        engine.Evaluate("""
            var t = new T(1);
            var before = t.get();
            var f = t.get;
            [before, f === t.get, typeof f, JSON.stringify(f.name), f.length, t.set.length,
             Object.getPrototypeOf(f) === Function.prototype, f.prototype.constructor === f, f.toString() === new T(0).get.toString(), f()].join(' ')
            """).AsString().Should().Be("1 true function \"\" 0 1 true true true 1");
        engine.Evaluate("""
            var t = new T(1);
            t.set(2);
            var d = Object.getOwnPropertyDescriptor(t, 'set');
            [t.get(), typeof d.value, d.writable, d.enumerable, d.configurable, d.value === t.set, Object.keys(t).length].join(' ')
            """).AsString().Should().Be("2 function true true true true 12");
    }

    [Test]
    public void TheCallSeesTheReceiverAndTheEnvironment()
    {
        var engine = Create();
        engine.Evaluate("""
            var t = new T(3);
            [t.self() === t, t.strictThis() === t, t.set(4) === t, t.get(), t.mk()(), t.nt() === undefined, t.strictArgs(1, 2)].join(' ')
            """).AsString().Should().Be("true true true 4 4 true 2");

        // The callee is the value before the arguments run, whatever they store.
        engine.Evaluate("""
            var t = new T(5);
            var r = t.get((t.get = null, 0));
            [r, t.get].join(' ')
            """).AsString().Should().Be("5 ");
        engine.Evaluate("var t = new T(6); var r = t.get(t.get); [r, typeof t.get, t.get()].join(' ')").AsString().Should().Be("6 function 6");
    }

    [Test]
    public void AMethodThatReadsItsOwnFunctionGetsTheSlotsFunction()
    {
        var engine = Create();
        engine.Evaluate("var t = new T(1); t.me() === t.me").AsBoolean().Should().BeTrue();
        engine.Evaluate("var t = new T(1); t.callee() === t.callee").AsBoolean().Should().BeTrue();
        engine.Evaluate("var t = new T(1); var c = t.callee(); c === t.callee").AsBoolean().Should().BeTrue();
        engine.Evaluate("var t = new T(1); t.ev('arguments.callee') === t.ev").AsBoolean().Should().BeTrue();
        engine.Evaluate("var t = new T(7); t.ev('v')").AsNumber().Should().Be(7);

        var t = (JsObject) engine.Evaluate("var t = new T(1); t.callee(); t");
        t.GetSlot(SlotOf(engine, "callee")).Should().BeOfType<ScriptFunction>();
    }

    [Test]
    public void AStrictArgumentsObjectHasNoCallee()
    {
        var engine = Create();
        engine.Evaluate("""
            function S() { 'use strict'; this.c = function () { return arguments.callee; }; this.n = function () { return arguments.length; }; }
            for (var w = 0; w < 20; w++) new S();
            var s0 = new S(); try { s0.c(); } catch (e) { } s0.n();
            var s = new S();
            var threw; try { s.c(); threw = false; } catch (e) { threw = e instanceof TypeError; }
            [threw, s.n(1, 2, 3)].join(' ')
            """).AsString().Should().Be("true 3");
        var s = (JsObject) engine.Evaluate("var s = new S(); s.n(); s");
        s.GetSlot(1).Should().BeOfType<JsObject.DeferredClosures>();
    }

    [Test]
    public void AnErrorFromTheFirstCallHasTheSameStack()
    {
        var engine = Create();
        const string Throw = "var s; try { t.fail(); } catch (e) { s = e.stack; } s";
        engine.Execute("var t = new T(1);");
        var first = engine.Evaluate(Throw).AsString();
        engine.Execute("var t = new T(1); t.fail;");
        var later = engine.Evaluate(Throw).AsString();

        first.Should().Be(later);
        first.Should().Contain("t.fail");

        engine.Execute("var t = new T(1);");
        var exception = Assert.Throws<JavaScriptException>(() => engine.Evaluate("t.fail()"));
        engine.Execute("var t = new T(1); t.fail;");
        var materialized = Assert.Throws<JavaScriptException>(() => engine.Evaluate("t.fail()"));
        exception!.Message.Should().Be(materialized!.Message);
        exception.Location.Start.Should().Be(materialized.Location.Start);
        exception.JavaScriptStackTrace.Should().Be(materialized.JavaScriptStackTrace);
    }

    [Test]
    public void TheRecursionLimitCountsTheFirstCall()
    {
        static int Deepest(Engine engine, string prefix)
        {
            for (var n = 1; n < 50; n++)
            {
                try
                {
                    engine.Evaluate($"var r = new T(0); {prefix} r.rec({n})");
                }
                catch (RecursionDepthOverflowException)
                {
                    return n;
                }
            }

            return -1;
        }

        var engine = Create(options => options.Constraints.MaxRecursionDepth = 10);
        var unmaterialized = Deepest(engine, "");
        var materialized = Deepest(engine, "r.rec;");

        unmaterialized.Should().BeGreaterThan(0);
        unmaterialized.Should().Be(materialized);
    }

    [Test]
    public void ADebuggerSeesTheSlotsFunction()
    {
        var engine = Create(options => options.Debugger.Enabled = true);
        var t = (JsObject) engine.Evaluate("var t = new T(1); t.get(); t");
        t.GetSlot(SlotOf(engine, "get")).Should().BeOfType<ScriptFunction>();
    }
}
