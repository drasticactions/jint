#nullable enable

using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime;

namespace Jint.Tests.Runtime;

/// <summary>
/// A statement <c>this.name = function () { ... };</c> in a constructor stores a lazy slot, and the function is
/// created on the first read of the property. Nothing about the property or the function may tell the difference.
/// Only a constructor whose instances are built in shape mode does this, which starts after a few instances, so
/// every test builds some first.
/// </summary>
public class DeferredClosureTests
{
    private const string Constructor = """
        function T(x) {
          var v = x;
          this.get = function () { return v; };
          this.set = function (n) { v = n; return this; };
          this.self = function () { return this; };
          this.args = function () { return arguments.length; };
          this.strictThis = function () { 'use strict'; return this; };
          this.ctor = function (a, b) { this.a = a; };
        }
        """;

    private static Engine Create()
    {
        var engine = new Engine();
        engine.Execute(Constructor);
        engine.Execute("for (var w = 0; w < 20; w++) new T(0);");
        return engine;
    }

    private static bool HasLazyFlag(JsObject obj) => (obj._type & InternalTypes.HasLazySlots) != InternalTypes.Empty;

    [Test]
    public void TheMethodsAreLazySlotsUntilRead()
    {
        var engine = Create();
        var t = (JsObject) engine.Evaluate("var t = new T(1); t");

        HasLazyFlag(t).Should().BeTrue();
        var sentinel = t.GetSlot(0);
        sentinel.Should().BeOfType<JsObject.DeferredClosures>();
        // One sentinel for all the methods one constructor call installs.
        for (var i = 1; i < 6; i++)
        {
            t.GetSlot(i).Should().BeSameAs(sentinel);
        }

        engine.Evaluate("t.get()").AsNumber().Should().Be(1);
        t.GetSlot(0).Should().BeOfType<ScriptFunction>();
        t.GetSlot(1).Should().BeSameAs(sentinel);

        engine.Evaluate("Object.keys(t).forEach(function (k) { t[k]; })");
        HasLazyFlag(t).Should().BeFalse();
    }

    [Test]
    public void ObjectsBuiltAlikeShareTheirDefinitions()
    {
        var engine = Create();
        var a = (JsObject) engine.Evaluate("new T(1)");
        var b = (JsObject) engine.Evaluate("new T(2)");
        var first = (JsObject.DeferredClosures) a.GetSlot(0);
        var second = (JsObject.DeferredClosures) b.GetSlot(0);

        second.Should().NotBeSameAs(first);
        second.Definitions.Should().BeSameAs(first.Definitions);
    }

    [Test]
    public void AMethodBehavesAsTheFunctionTheStatementCreated()
    {
        var engine = Create();
        engine.Evaluate("""
            var t = new T(1);
            [Object.keys(t).join(), t.get === t.get, typeof t.get, JSON.stringify(t.get.name), t.get.length, t.ctor.length,
             Object.getPrototypeOf(t.get) === Function.prototype, t.ctor.prototype.constructor === t.ctor].join(' ')
            """).AsString().Should().Be("get,set,self,args,strictThis,ctor true function \"\" 0 2 true true");
        engine.Evaluate("""
            var t = new T(1);
            [t.get(), t.set(5) === t, t.get(), t.args(1, 2, 3), t.self() === t, t.strictThis.call(undefined) === undefined,
             new t.ctor(7).a, new T(2).get !== t.get].join(' ')
            """).AsString().Should().Be("1 true 5 3 true true 7 true");
        engine.Evaluate("""
            var t = new T(1);
            var d = Object.getOwnPropertyDescriptor(t, 'args');
            [typeof d.value, d.writable, d.enumerable, d.configurable, d.value === t.args, t.hasOwnProperty('self')].join(' ')
            """).AsString().Should().Be("function true true true true true");
    }

    [Test]
    public void EveryWayOfReadingTheObjectSeesTheFunctions()
    {
        var engine = Create();
        engine.Evaluate("var t = new T(3); delete t.set; ['set' in t, t.get(), typeof t.self, Object.keys(t).join()].join(' ')")
            .AsString().Should().Be("false 3 function get,self,args,strictThis,ctor");
        engine.Evaluate("var t = new T(4); Object.freeze(t); [Object.isFrozen(t), t.get(), Object.getOwnPropertyDescriptor(t, 'get').writable].join(' ')")
            .AsString().Should().Be("true 4 false");
        engine.Evaluate("var t = new T(5); [Object.values(t).map(function (f) { return typeof f; }).join(), Object.entries(t).length].join(' ')")
            .AsString().Should().Be("function,function,function,function,function,function 6");
        engine.Evaluate("var t = new T(6); var c = Object.assign({}, t); var s = { ...t }; [c.get(), c.get === t.get, s.get(), s.get === t.get].join(' ')")
            .AsString().Should().Be("6 true 6 true");
        engine.Evaluate("[JSON.stringify(new T(8)), JSON.stringify(Object.assign(new T(9), { q: 1 }))].join(' ')")
            .AsString().Should().Be("{} {\"q\":1}");
        engine.Evaluate("var t = new T(10); t.get = 3; [t.get, typeof t.set].join(' ')").AsString().Should().Be("3 function");
        engine.Evaluate("var t = new T(11); Object.defineProperty(t, 'get', { enumerable: false }); [typeof t.get, t.get(), Object.keys(t).join()].join(' ')")
            .AsString().Should().Be("function 11 set,self,args,strictThis,ctor");
        engine.Evaluate("var t = new T(14); [Reflect.get(t, 'get')(), Reflect.has(t, 'set'), Reflect.ownKeys(t).length].join(' ')")
            .AsString().Should().Be("14 true 6");
        engine.Evaluate("var t = new T(15); var r; with (t) { r = get(); } r").AsNumber().Should().Be(15);
        engine.Evaluate("var t = new T(16); var child = Object.create(t); [child.get(), child.get === t.get].join(' ')").AsString().Should().Be("16 true");
    }

    [Test]
    public void APrototypeThatOwnsTheNameGetsTheStore()
    {
        var engine = new Engine();
        engine.Evaluate("""
            var calls = 0;
            function P() { this.m = function () { return 1; }; this.r = function () { return 2; }; }
            Object.defineProperty(P.prototype, 'm', { set: function (f) { calls++; this._m = f; }, get: function () { return this._m; }, configurable: true });
            Object.defineProperty(P.prototype, 'r', { value: 'ro', writable: false });
            for (var w = 0; w < 20; w++) new P();
            calls = 0;
            var p = new P();
            [calls, typeof p.m, p.m(), p.hasOwnProperty('m'), p.r, p.hasOwnProperty('r')].join(' ')
            """).AsString().Should().Be("1 function 1 false ro false");
        engine.Evaluate("""
            function S() { 'use strict'; this.r = function () {}; }
            S.prototype = Object.create(null, { r: { value: 1, writable: false } });
            for (var w = 0; w < 20; w++) { try { new S(); } catch (e) { } }
            var threw; try { new S(); threw = false; } catch (e) { threw = e instanceof TypeError; } threw
            """).AsBoolean().Should().BeTrue();
        engine.Evaluate("""
            var trapped = [];
            function X() { this.f = function () { return 'f'; }; }
            X.prototype = new Proxy({}, { set: function (tgt, key, v, r) { trapped.push(key); return Reflect.set(tgt, key, v, r); } });
            for (var w = 0; w < 20; w++) new X();
            trapped = [];
            var x = new X();
            [trapped.join(), typeof x.f].join(' ')
            """).AsString().Should().Be("f function");
    }

    [Test]
    public void TheFunctionClosesOverTheLiveEnvironment()
    {
        var engine = new Engine();
        engine.Evaluate("""
            function L() { var z = 1; this.f = function () { return z; }; z = 2; var self = this; this.g = function () { return self === this; }; }
            for (var w = 0; w < 20; w++) new L();
            var l = new L();
            [l.f(), l.g(), l.g.call({})].join(' ')
            """).AsString().Should().Be("2 true false");
        engine.Evaluate("""
            class C { #p = 1; constructor() { this.f = function () { return 'cf'; }; this.g = () => this.#p; } }
            for (var w = 0; w < 20; w++) new C();
            var c = new C();
            [c.f(), c.g()].join(' ')
            """).AsString().Should().Be("cf 1");
        engine.Evaluate("""
            function M() { this.a = 1; this.b = function () {}; this.c = 2; for (var i = 0; i < 20; i++) this['k' + i] = function () { return i; }; }
            for (var w = 0; w < 20; w++) new M();
            var m = new M();
            [Object.getOwnPropertyNames(m).slice(0, 5).join(), m.k19(), m.k0 === m.k0, typeof m.b, Object.getOwnPropertyNames(new M()).length].join(' ')
            """).AsString().Should().Be("a,b,c,k0,k1 20 true function 23");
    }

    [Test]
    public void AnAssignmentWhoseValueIsReadCreatesTheFunction()
    {
        var engine = new Engine();
        var o = (JsObject) engine.Evaluate("function U() { var f = this.f = function () { return 1; }; this.g = f; } for (var w = 0; w < 20; w++) new U(); new U()");
        o.GetSlot(0).Should().BeOfType<ScriptFunction>();
        engine.Evaluate("var u = new U(); u.f === u.g").AsBoolean().Should().BeTrue();
    }
}
