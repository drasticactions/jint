using Jint.Runtime;
using Jint.Runtime.Interpreter.Kernels;

namespace Jint.Tests.Runtime;

/// <summary>
/// Scripts whose functions run on the interpreter, on kernel trees and as compiled code must give the same values,
/// errors, locations and statement checks.
/// </summary>
public class FunctionKernelTests
{
    /// <summary>How a script's functions run.</summary>
    public enum Mode
    {
        Interpreter,

        // On a kernel tree from the first call, never compiled.
        Tree,

        // Compiled on the first call.
        Code,

        // On the interpreter for four calls, then on the tree until it has checked a hundred statements, then compiled.
        CodeWarm,

        // As CodeWarm, compiled after a thousand statements: statements the tree hardly ran stay on the tree.
        CodeLate,
    }

    private static readonly Mode[] CodeModes = [Mode.Tree, Mode.Code, Mode.CodeWarm, Mode.CodeLate];

    private static readonly string[] Scripts =
    [
        // parameters, locals, arithmetic, bitwise operators and comparisons over Numbers and other values
        """
        function arith(a, b) {
          var c = a * b, d = a - b, e;
          e = c / (d || 1) + a % 3;
          if (a > b) { e = e + 1; } else if (a == b) { e = -e; } else { e--; }
          var f = (a & 7) | (b << 2) ^ (a >>> 1);
          return e + f * 0.5 + (a < b) + !b + (a === b) + (a != b);
        }
        function test() {
          var out = [], vals = [0, -0, 1, -3.5, 7, NaN, Infinity, "4", "x", null, undefined, true, 2 ** 53];
          for (var i = 0; i < vals.length; i++) { for (var j = 0; j < vals.length; j++) { out.push(arith(vals[i], vals[j])); } }
          return out.join(",");
        }
        """,
        // valueOf and toString run by arithmetic, comparisons and concatenation, in order, and a throw out of one
        """
        var log = [];
        var o1 = { valueOf: function () { log.push("v1"); return 3; } };
        var o2 = { toString: function () { log.push("s2"); return "7"; } };
        var o3 = { valueOf: function () { log.push("v3"); throw new Error("boom"); } };
        function mix(a, b) {
          var s = a + b;
          var t = a * b;
          var u = a < b, w = a == b;
          log.push(typeof s, s, t, u, w);
          return s;
        }
        function test() {
          log = [];
          var vals = [1, "a", o1, o2, null, undefined];
          for (var i = 0; i < vals.length; i++) { for (var j = 0; j < vals.length; j++) { mix(vals[i], vals[j]); } }
          try { mix(o3, 1); } catch (e) { log.push(e.message); }
          return log.join("|");
        }
        """,
        // property reads and stores: own data, prototype accessors, frozen objects, primitives, order, strict mode
        """
        var log = [];
        var proto = { set p(v) { log.push("set p " + v); }, get q() { log.push("get q"); return 5; } };
        function store(o, v) {
          o.x = v;
          o.p = v + 1;
          var q = o.q;
          o.y = o.x + q;
          return o.y;
        }
        function storeStrict(o, v) {
          "use strict";
          o.x = v;
          var q = o.q;
          o.y = q === undefined ? v : q;
          return o.y;
        }
        function storeOrder(o) {
          o.z = (log.push("rhs"), 1);
          return o.z;
        }
        function test() {
          log = [];
          var a = Object.create(proto), b = { x: 1, y: 2 }, f = Object.freeze({ x: 1 }), arr = [1, 2, 3];
          log.push(store(a, 1), store(b, 2), store(f, 3), store("str", 4), store(5, 5), store(arr, 6), a.x, b.y, f.x);
          var targets = [a, b, f, "str", null, undefined, arr];
          for (var i = 0; i < targets.length; i++) {
            try { log.push(storeStrict(targets[i], i)); } catch (e) { log.push(e.constructor.name + ":" + e.message); }
            try { log.push(storeOrder(targets[i])); } catch (e) { log.push(e.constructor.name + ":" + e.message); }
          }
          arr.length = 1; log.push(arr.length, arr.join());
          return log.join("|");
        }
        """,
        // calls of interpreted and compiled functions, built-ins and methods, with five or too few arguments, recursion
        """
        var counter = { n: 0, bump: function (k) { this.n += k; return this.n; } };
        function add3(a, b, c) { var s = a + b; s = s + (c === undefined ? 0 : c); return s; }
        function five(a, b, c, d, e) { var t = a + b + c; t = t + d + e; return t; }
        function rec(n) { var r = 0; if (n > 0) { r = rec(n - 1) + n; } return r; }
        function calls(i) {
          var a = add3(i, 1), b = add3(i, 2, 3), c = five(1, 2, 3, 4, i);
          var m = Math.max(a, b), s = String(c);
          counter.bump(1);
          var r = rec(i % 20);
          return a + b + c + m + s + r + counter.n;
        }
        function test() {
          var out = [];
          for (var i = 0; i < 30; i++) { out.push(calls(i)); }
          return out.join(",");
        }
        """,
        // names of an enclosing function's environment and of the global object, read and written
        """
        var total = 0;
        var api = (function () {
          var hits = 0, scale = 3;
          function inc(v) { hits = hits + 1; var r = v * scale; total = total + r; return r + hits; }
          return { inc: inc, hits: function () { return hits; } };
        })();
        function test() {
          var out = [];
          for (var i = 0; i < 20; i++) { out.push(api.inc(i)); }
          out.push(api.hits(), total);
          return out.join(",");
        }
        """,
        // a compiled function entered again while it runs, through valueOf
        """
        var depth = 0;
        var o = { valueOf: function () { return depth++ < 3 ? g(5) : 1; } };
        function g(x) { var r = x * 2; r = r + 1; return r; }
        function test() {
          depth = 0;
          var out = [];
          for (var i = 0; i < 4; i++) { out.push(g(o), g(i)); }
          return out.join(",");
        }
        """,
        // an error thrown by a callee crosses compiled frames to the caller: its location and stack
        """
        function thrower(x) { var y = x + 1; if (y > 8) { throw new Error("too big " + y); } return y; }
        function middle(x) { var z = thrower(x) * 2; z = z + 1; return z; }
        function test() {
          var out = [];
          for (var i = 0; i < 10; i++) { out.push(middle(i)); }
          return out.join(",");
        }
        """,
        // errors the runtime raises in compiled code: a property of undefined, calling what is not a function
        """
        function read(o) { var v = o.a; v = v.b; return v; }
        function callIt(o) { var r = o.f(1); r = r + 1; return r; }
        function test() {
          var out = [];
          for (var i = 0; i < 6; i++) { out.push(read({ a: { b: i } }), callIt({ f: Math.abs })); }
          try { read({}); } catch (e) { out.push(e.constructor.name + ":" + e.message); }
          try { callIt({ f: 3 }); } catch (e) { out.push(e.constructor.name + ":" + e.message); }
          out.push(callIt({ f: function (x) { return x * 10; } }));
          return out.join("|") + read(null);
        }
        """,
        // calls as operands, and Numbers handed to parameters unboxed
        """
        var log = [];
        function id(x) { log.push(typeof x); return x; }
        function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
        function sum(n) { var t = 0; t += n > 0 ? sum(n - 1) + n : 0; return t; }
        function mixed(a, b) { var r = id(a) * id(b) - id(a); r += id(b) === 0 ? id("z") : id(b) / id(a); return r; }
        function keep(x) { return function () { return x; }; }
        function param(x) { x += id(1); x -= (x = 50, id(2)); return x; }
        function zeroes(x, y) { var r = 1 / x; r = r + 1 / y; return r; }
        function big(x) { var r = 1; r += id(BigInt(x)); return r; }
        function down(n) { return n > 0 ? down(n - 1) + 1 : boom(n); }
        function boom(n) { throw new Error("boom " + n); }
        function test() {
          log = [];
          var out = [fib(12), sum(6)];
          for (var i = 0; i < 4; i++) {
            out.push(mixed(i, i - 1), mixed("2", i), keep(i * 0.5)(), keep(-0)(), param(i), param("s"), zeroes(-0, 0), zeroes(i - i, -(i - i)));
            try { out.push(big(i)); } catch (e) { out.push(e.constructor.name + ":" + e.message); }
          }
          return out.join(",") + log.join("");
        }
        function run() { var r = test(); return r + down(3); }
        """,
        // one definition with different outer environments, and a binding an eval injects between calls
        """
        var k = 1;
        function make(m) { return function (x) { var y = x * m + k; return y; }; }
        function outerEval(code) { function inner(x) { var y = x + k; return y; } var a = inner(1); eval(code); var b = inner(1); return a + "/" + b; }
        function Obj(i) { this.get = function (x) { var y = x * 2 + i; return y; }; }
        function test() {
          var out = [], g = [make(2), make(3), make(4)];
          for (var i = 0; i < 9; i++) { out.push(g[i % 3](i), g[(i * 2) % 3](i)); }
          for (var j = 0; j < 40; j++) { out.push(new Obj(j).get(j)); }
          out.push(outerEval(""), outerEval("var k = 5;"), outerEval(""));
          return out.join(",");
        }
        """,
        // stacks taken inside kernel calls of kernels, from the callee, an interpreted closure and a caught error
        """
        var stacks = [];
        function leaf(n) { if (n === 3) { stacks.push(new Error("at " + n).stack); } return n + 1; }
        function mid(n) { var r = leaf(n) * 2; return r; }
        function top(n) { return n < 1 ? mid(n) + 0 : top(n - 1) + mid(n); }
        function closure(n) { var f = function () { return new Error("c" + n).stack; }; return f(); }
        function viaClosure(n) { var s = closure(n); stacks.push(s); return n; }
        function deep(n) { return n > 0 ? deep(n - 1) + 1 : viaClosure(n) + 0; }
        function bad(n) { var o = n > 2 ? null : { x: n }; return o.x; }
        function tryBad(n) { try { return bad(n); } catch (e) { return e.message + e.stack; } }
        function catcher(n) { var r = ""; r = r + tryBad(n); return r; }
        function test() {
          stacks = [];
          var out = [top(4), deep(3)];
          for (var i = 0; i < 5; i++) { out.push(catcher(i)); }
          return out.join(",") + stacks.join("~");
        }
        """,
        // functions without parameters or locals, whose call environments hold no slots, and a call of a literal return
        """
        var x = 2;
        function one() { return 1; }
        var get = () => { return x + one(); };
        var o = { x: 2, get: function () { return this.x + one(); }, twice: function () { return this.get() * 2 + get(); } };
        function own() { return this.x * 3 + one(); }
        var p = { x: 5, own: own };
        function test() {
          var out = [];
          for (var i = 0; i < 30; i++) { o.x = i; x = i * 2; p.x = i; out.push(o.twice(), o.get(), get(), p.own()); }
          return out.join(",");
        }
        """,
        // a bare call as a whole branch of a returned conditional, in sloppy and strict functions, and calls in tests
        """
        var log = [];
        function f(x) { var y = x + 1; return y; }
        function g(n) { var y = n * 2; log.push(n); return y; }
        function h(c, x, n) { return c ? f(x) + n : g(n); }
        function hs(c, x, n) { "use strict"; return c ? f(x) + n : g(n); }
        function tests(a, b) { var r = 0; if (f(a) > g(b) && !f(b)) { r = 1; } else if (f(a) == "3" || g(a) < f(b)) { r = 2; } return r + (f(a) !== g(b)); }
        function nothing(a) { var b = a + 1; if (b > 3) { return; } }
        function test() {
          log = [];
          var out = [];
          for (var i = 0; i < 20; i++) { out.push(h(i & 1, i, 3), hs(i & 2, i, 4), tests(i % 5, i % 3), nothing(i)); }
          return out.join(",") + log.join("");
        }
        """,
        // calls nested in arguments, of interpreted functions and of built-ins, each argument compiled once
        """
        function inc(x) { var y = x + 1; return y; }
        function deep(a) {
          var r = inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(inc(a))))))))))))))))))))))));
          var m = Math.max(Math.max(Math.max(Math.max(Math.max(Math.max(Math.max(Math.max(Math.max(Math.max(a, 1), 2), 3), 4), 5), 6), 7), 8), 9), inc(a));
          return r + m;
        }
        function test() {
          var out = [];
          for (var i = 0; i < 12; i++) { out.push(deep(i), deep("s" + i)); }
          return out.join(",");
        }
        """,
        // recursion deeper than the frames a kernel keeps, mutual recursion, methods binding this, closures of many calls
        """
        function down(n) { var m = n - 1; return n > 0 ? down(m) + 1 : 0; }
        function even(n) { return n === 0 ? true : odd(n - 1) + 0 === 1; }
        function odd(n) { return n === 0 ? false : even(n - 1); }
        var obj = { k: 3, m: function (x) { var t = x + this.k; return t; } };
        function viaThis(x) { var r = obj.m(x) * 2; r = r + obj.m(1); return r; }
        function counter(start) { var c = start; return function (d) { var e = d + 1; c = c + e; return c; }; }
        function test() {
          var out = [down(70), down(33), even(10), even(7)];
          var a = counter(1), b = counter(100);
          for (var i = 0; i < 25; i++) { out.push(viaThis(i), a(i), b(i)); }
          return out.join(",");
        }
        """,
        // this in methods: prototype methods, replaced and shadowed ones, primitives and undefined as this, arrows
        """
        function P(k) { this.k = k; }
        P.prototype.get = function (x) { var y = x + this.k; return y; };
        P.prototype.set = function (x) { this.k = this.k + x; return this.k; };
        var lit = { k: 7, get: function (x) { var y = x * this.k; return y; } };
        function loose() { var t = typeof this; return t + (this === undefined); }
        function strict() { "use strict"; var t = typeof this; return t + (this === undefined); }
        function arrowThis(x) { var f = () => this.k + x; return f(); }
        function callGet(o, x) { var r = o.get(x); r = r + 0; return r; }
        function callSet(o, x) { var r = o.set(x); r = r + 0; return r; }
        var math = { add: function (a, b) { return a + b; }, top: function (x) { return Math.abs(x - 5) * 2; } };
        function test() {
          var out = [], p = new P(1), q = new P(2), realAbs = Math.abs;
          for (var i = 0; i < 30; i++) {
            var sum = math.add(i, 1), top = math.top(i), text = math.add("s", i);
            out.push(callGet(p, i), callGet(q, i), callSet(p, 1), callGet(lit, i), sum, top, text);
            if (i === 12) { math.add = function (a, b) { return a - b; }; Math.abs = Math.sign; }
            if (i === 18) { Math.abs = realAbs; }
            if (i === 10) { P.prototype.get = function (x) { var y = x - this.k; return y; }; }
            if (i === 15) { q.get = function (x) { var y = 1000 + x; return y; }; }
            if (i === 20) { Object.setPrototypeOf(p, { get: function (x) { var y = -x; return y; }, set: function (x) { return x * 2; } }); }
            if (i === 25) { delete q.get; }
          }
          out.push(loose.call(5), loose.call(undefined), strict.call(5), strict.call(undefined), loose(), strict(), arrowThis.call(p, 3), arrowThis.call(lit, 4));
          return out.join(",");
        }
        """,
        // reads of dictionary objects, inherited data, getters, lazily made methods and frozen objects
        """
        var log = [];
        var base = { b: 2, get g() { log.push("g"); return 3; }, m: function (x) { var y = x + 1; return y; } };
        function read(o) { var v = o.b; v = v + o.c; return v; }
        function viaGetter(o) { var v = o.g; v = v * 2; return v; }
        function callM(o, x) { var r = o.m(x); r = r + 0; return r; }
        function test() {
          log = [];
          var d = { b: 1, c: 2, x: 0 }; delete d.x;
          var inh = Object.create(base); inh.c = 5;
          var frozen = Object.freeze({ b: 4, c: 6, m: function (x) { var y = x * 3; return y; } });
          var out = [];
          for (var i = 0; i < 30; i++) {
            d.c = i;
            out.push(read(d), read(inh), read(frozen), viaGetter(inh), callM(inh, i), callM(frozen, i), callM({ m: function (x) { return x - 1; } }, i));
            if (i === 12) { base.b = 20; }
            if (i === 18) { Object.defineProperty(base, "b", { get: function () { log.push("b"); return 30; } }); }
            if (i === 24) { inh.b = 40; }
          }
          return out.join(",") + log.join("");
        }
        """,
        // closures called many times: counters, closures that outlive their calls, instances sharing a definition
        """
        function counter(start) { var c = start; return function () { c = c + 1; return c; }; }
        function adder(k) { return function (x) { var y = x + k; return y; }; }
        var module = (function () {
          var count = 0;
          function helper(x) { var y = x * 2; return y; }
          return { get: function (x) { count = count + 1; return helper(x) + count; }, count: function () { return count; } };
        })();
        function rec(f, n) { var r = f(n); if (n > 0) { r = r + rec(f, n - 1); } return r; }
        function test() {
          var out = [], cs = [counter(0), counter(100)], as = [adder(1), adder(2), adder(3)];
          for (var i = 0; i < 40; i++) {
            out.push(cs[i % 2](), as[i % 3](i), module.get(i));
            if (i % 7 === 0) { cs.push(counter(i)); as.push(adder(i)); }
          }
          var self = function (n) { var m = n - 1; return n > 0 ? self(m) + n : 0; };
          out.push(rec(as[0], 5), rec(cs[1], 3), self(40), module.count());
          return out.join(",");
        }
        """,
        // bodies that make closures: kept past the call, named by declarations and assignments, arrows reading this
        """
        var kept = [];
        function make(k) { var t = k * 2; var f = function (x) { return x + t; }; kept.push(f); return f; }
        function arrows(k) { var g = (x) => x * k + this.n; var r = g(2); return r + g.name; }
        function assign(k) { var h; h = function () { return k; }; var o = { m: null }; o.m = function () { return k + 1; }; return h.name + h() + o.m.name + o.m(); }
        function inner(k) { var s = 0; [1, 2, 3].forEach(function (v) { s = s + v * k; }); return s; }
        function capturedAndLocal(a, b) { var hidden = a + b; var seen = a * b; var get = function () { return seen; }; seen = seen + hidden; return get() + hidden; }
        function test() {
          kept = [];
          var out = [], host = { n: 5, arrows: arrows };
          for (var i = 0; i < 30; i++) { out.push(make(i)(1), host.arrows(i), assign(i), inner(i), capturedAndLocal(i, 3)); }
          for (var j = 0; j < kept.length; j++) { out.push(kept[j](100)); }
          return out.join(",");
        }
        """,
        // constructors: this.x stores, prototype methods, returning objects or primitives, and new.target
        """
        function Pt(x, y) { this.x = x; this.y = y; }
        Pt.prototype.len = function () { var s = this.x * this.x + this.y * this.y; return s; };
        function Boxed(v) { this.v = v; return { boxed: v }; }
        function Prim(v) { this.v = v; return 5; }
        function Counted(v) { var n = v + 1; this.n = n; this.m = function () { return n; }; }
        function test() {
          var out = [];
          for (var i = 0; i < 30; i++) {
            var p = new Pt(i, 1), b = new Boxed(i), q = new Prim(i), c = new Counted(i);
            out.push(p.len(), p.x, b.boxed, b.v, q.v, c.n, c.m(), p instanceof Pt, Object.keys(p).join(""));
          }
          try { Pt.prototype.len.call(null); } catch (e) { out.push(e.constructor.name); }
          return out.join(",");
        }
        """,
        // constructor stores that add properties: setters, read-only and later-added members of the prototype chain
        """
        var log = [];
        function A(v) { this.a = v; this.b = v + 1; }
        function S(v) { "use strict"; this.a = v; this.b = v * 2; }
        function N(v) { this.a = v; Object.preventExtensions(this); this.b = v; }
        function test() {
          log = [];
          var out = [];
          for (var i = 0; i < 40; i++) {
            var x = new A(i);
            out.push(x.a, x.b, Object.keys(x).join(""), Object.getOwnPropertyNames(x).length);
            try { var y = new S(i); out.push(y.a, y.b); } catch (e) { out.push(e.constructor.name); }
            var n = new N(i); out.push(n.a, n.b, Object.isExtensible(n));
            if (i === 10) { Object.defineProperty(A.prototype, "b", { set: function (v) { log.push("set b " + v); }, configurable: true }); }
            if (i === 15) { delete A.prototype.b; }
            if (i === 20) { Object.defineProperty(Object.prototype, "b", { value: 1, writable: false, configurable: true }); }
            if (i === 25) { delete Object.prototype.b; }
            if (i === 30) { S.prototype.a = 0; Object.freeze(S.prototype); }
          }
          return out.join(",") + log.join("|");
        }
        """,
        // stacks thrown from methods, closures and constructors, and eval and with around closures that read names
        """
        var k = 1;
        function Thrower(n) { this.n = n; if (n > 25) { throw new Error("ctor " + n); } }
        Thrower.prototype.fail = function (x) { var y = x + this.n; if (y > 30) { throw new Error("method " + y); } return y; };
        function closeOver(n) { var t = n; return function (x) { var y = x + t + k; if (y > 40) { throw new Error("closure " + y); } return y; }; }
        function viaEval(code, f) { eval(code); return f(1) + k; }
        function viaWith(o, f) { with (o) { return f(2) + k; } }
        function test() {
          var out = [], cs = [closeOver(1), closeOver(2)];
          for (var i = 0; i < 30; i++) {
            try { out.push(new Thrower(i).fail(i)); } catch (e) { out.push(e.message + e.stack); }
            try { out.push(cs[i % 2](i)); } catch (e) { out.push(e.message + e.stack); }
            if (i % 9 === 0) { out.push(viaEval("var k = 100;", cs[0]), viaEval("", cs[1]), viaWith({ k: 50 }, cs[0])); }
          }
          return out.join(",");
        }
        """,
    ];

    private static IEnumerable<TestCaseData> Cases()
    {
        for (var i = 0; i < Scripts.Length; i++)
        {
            foreach (var mode in CodeModes)
            {
                yield return new TestCaseData(i, mode);
            }
        }
    }

    [TestCaseSource(nameof(Cases))]
    public void CodeMatchesTheInterpreter(int index, Mode mode)
    {
        var script = Scripts[index];
        var call = "var results = []; for (var round = 0; round < 12; round++) results.push("
                   + (script.Contains("function run()", StringComparison.Ordinal) ? "run()" : "test()")
                   + "); results.join('|')";

        var compiled = Run(script, call, mode, out var codeChecks, out var kernelRuns, out var codeRuns);
        var interpreted = Run(script, call, Mode.Interpreter, out var interpreterChecks, out var interpreterRuns, out _);

        compiled.Should().Be(interpreted);
        codeChecks.Should().Be(interpreterChecks);
        kernelRuns.Should().BeGreaterThan(0, "the script's functions should have run on kernels");
        interpreterRuns.Should().Be(0);
        if (mode == Mode.Tree || !KernelCode.Supported)
        {
            codeRuns.Should().Be(0);
        }
        else if (mode == Mode.Code)
        {
            codeRuns.Should().BeGreaterThan(0, "the script's functions should have run as compiled code");
        }

        // An exact constraint that lets statements pass between its checks, as a memory limit does.
        Run(script, call, mode, out var skippingChecks, out _, out _, skip: 7).Should().Be(interpreted);
        Run(script, call, Mode.Interpreter, out var skippingInterpreterChecks, out _, out _, skip: 7);
        skippingChecks.Should().Be(skippingInterpreterChecks);

        // Without an exact constraint.
        Run(script, call, mode, exact: false).Should().Be(Run(script, call, Mode.Interpreter, exact: false));
    }

    [TestCase(6)]
    [TestCase(10)]
    public void MemoryLimitedEnginesMatchTheInterpreter(int index)
    {
        var script = Scripts[index];
        var call = "var results = []; for (var round = 0; round < 12; round++) results.push("
                   + (script.Contains("function run()", StringComparison.Ordinal) ? "run()" : "test()")
                   + "); results.join('|')";

        string RunWith(Mode mode)
        {
            var engine = new Engine(o => o.LimitMemory(1_000_000_000));
            Configure(engine, mode);
            try
            {
                engine.Execute(script);
                return engine.Evaluate(call).ToString();
            }
            catch (JavaScriptException e)
            {
                return $"{e.Error} at {e.Location.Start.Line}:{e.Location.Start.Column} {e.JavaScriptStackTrace}";
            }
        }

        var interpreted = RunWith(Mode.Interpreter);
        foreach (var mode in CodeModes)
        {
            RunWith(mode).Should().Be(interpreted, mode.ToString());
        }
    }

    [Test]
    public void HostCallbacksSeeTheFramesOfKernelCalls()
    {
        const string Script = """
            var seen = [];
            function inner(n) { var t = n + 1; if (n === 0) { seen.push(trace() + "|" + depth()); } return t; }
            function outer(n) { return n > 0 ? outer(n - 1) + n : inner(n) + 0; }
            function test() { seen = []; var r = outer(4) + outer(3); return r + seen.join("~"); }
            """;

        string RunWith(Mode mode)
        {
            var engine = new Engine();
            Configure(engine, mode);
            engine.SetValue("trace", new Func<string>(() => engine.Diagnostics.StackTrace));
            engine.SetValue("depth", new Func<int>(() => engine.CallStack.Count));
            engine.Execute(Script);
            var results = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                results.Add(engine.Evaluate("test()").ToString());
            }

            return string.Join("|", results);
        }

        var interpreted = RunWith(Mode.Interpreter);
        foreach (var mode in CodeModes)
        {
            RunWith(mode).Should().Be(interpreted, mode.ToString());
        }
    }

    [TestCase("function f(a) { var b = a + 1; return b; }", true)]
    [TestCase("function f(a) { if (a > 5) { throw new Error('x'); } var b = a + 1; return b; }", true)]
    [TestCase("function f(a) { return a + 1; }", false)]
    [TestCase("function f(a) { return this.x + a; }", true)]
    [TestCase("function f(a) { return a < 1 ? a : f(a - 1) + 1; }", true)]
    [TestCase("function f(a) { var g = function () { return a; }; return g(); }", true)]
    [TestCase("function f(a) { var b = a + 1; return () => b; }", true)]
    [TestCase("function f(a) { var b = a + 1; return () => arguments[0] + b; }", false)]
    [TestCase("function f(a) { function g() { return a; } return g(); }", false)]
    [TestCase("function f(a) { var C = class { }; return new C(); }", false)]
    [TestCase("function f(a) { var b; try { b = a + 1; } catch (e) { } return b; }", false)]
    [TestCase("function f(a) { var b = arguments.length; return b + a; }", false)]
    [TestCase("function f(a) { var b = eval('a'); return b; }", false)]
    [TestCase("function* f(a) { var b = a + 1; yield b; }", false)]
    [TestCase("async function f(a) { var b = a + 1; return b; }", false)]
    [TestCase("function f(a) { var b = a + 1; with (b) { } return b; }", false)]
    [TestCase("function f(a) { var b = 0; for (var i = 0; i < a; i++) { b += i; } return b; }", false)]
    public void OnlyTheSimplestBodiesAreCompiled(string declaration, bool compiled)
    {
        var engine = new Engine();
        engine._functionKernelThreshold = 1;
        engine.Execute(declaration);
        engine.Execute("for (var i = 0; i < 3; i++) { var r = f(i); if (r && r.next) r.next(); }");
        (engine._functionKernelRuns > 0).Should().Be(compiled);
    }

    [Test]
    public void BodiesRunOnTreesThenAsCode()
    {
        var engine = new Engine();
        engine._functionKernelThreshold.Should().Be(FunctionKernel.DefaultThreshold);
        engine._functionKernelCodeThreshold.Should().Be(KernelCode.Supported ? FunctionKernel.DefaultCodeThreshold : -1);
        engine._functionKernelCodeInBackground = false;
        engine.Execute("function f(a) { var b = a * 2; return b + 1; }");
        engine.Evaluate($"var s = 0; for (var i = 0; i < {FunctionKernel.DefaultThreshold - 1}; i++) s += f(i); s");
        engine._functionKernelRuns.Should().Be(0, "the interpreter runs the first calls");
        engine.Evaluate("f(4)").AsNumber().Should().Be(9);
        engine._functionKernelRuns.Should().Be(1);
        engine._functionKernelCodeRuns.Should().Be(0, "a tree runs the body until it has run it often enough");
        engine.Evaluate($"var t = 0; for (var i = 0; i < {FunctionKernel.DefaultCodeThreshold - 2}; i++) t += f(i); t");
        engine._functionKernelCodeRuns.Should().Be(0);
        engine.Evaluate($"var t = 0; for (var i = 0; i < 10; i++) t += f(i); t").AsNumber().Should().Be(100);
        if (KernelCode.Supported)
        {
            engine._functionKernelCodeRuns.Should().BeGreaterThan(0, "the body has run often enough on its tree");
        }
        else
        {
            engine._functionKernelCodeRuns.Should().Be(0, "where code is not compiled, bodies stay trees");
        }
    }

    [Test]
    public void BodiesCompiledInTheBackgroundRunAsCodeOnceCompiled()
    {
        var engine = new Engine();
        engine._functionKernelThreshold = 1;
        engine._functionKernelCodeThreshold = 1;
        engine.Execute("function f(a) { var b = a * 2; return b + 1; }");
        var deadline = DateTime.UtcNow + TestBudgets.WedgeCeiling;
        while (engine._functionKernelCodeRuns == 0 && KernelCode.Supported && DateTime.UtcNow < deadline)
        {
            engine.Evaluate("f(2)").AsNumber().Should().Be(5);
            Thread.Sleep(1);
        }

        engine.Evaluate("f(4)").AsNumber().Should().Be(9);
        if (KernelCode.Supported)
        {
            engine._functionKernelCodeRuns.Should().BeGreaterThan(0);
        }
    }

    [TestCase(Mode.Tree)]
    [TestCase(Mode.Code)]
    public void AKernelKeepsNoClosureItsCallsRanIn(Mode mode)
    {
        var engine = new Engine();
        Configure(engine, mode);
        engine.Execute("function make(o) { return function (x) { var y = x + 1; return o.n + y; }; } function run(f) { var s = 0; s = s + f(1); s = s + f(2); return s; }");
        var reference = CallAndForget(engine);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        reference.IsAlive.Should().BeFalse("a kernel's call frames let go of a closure's environment when its call ends");
        engine._functionKernelRuns.Should().BeGreaterThan(0);
        GC.KeepAlive(engine);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference CallAndForget(Engine engine)
        {
            var held = engine.Evaluate("({ n: 5 })");
            engine.SetValue("held", held);
            engine.Execute("var f = make(held); held = undefined; for (var i = 0; i < 10; i++) run(f); f = undefined;");
            return new WeakReference(held);
        }
    }

    [Test]
    public void AnEngineCompilesABoundedNumberOfBodiesToCode()
    {
        var engine = new Engine();
        engine._functionKernelThreshold = 1;
        engine._functionKernelCodeThreshold = 0;
        engine._functionKernelCodeInBackground = false;
        engine._functionKernelCodeBudget = 1;
        engine.Execute("function f(a) { var b = a + 1; return b; } function g(a) { var b = a + 2; return b; }");
        engine.Evaluate("f(1) + g(1) + f(2) + g(2)").AsNumber().Should().Be(12);
        engine._functionKernelCodeBudget.Should().Be(KernelCode.Supported ? 0 : 1);
        engine._functionKernelRuns.Should().Be(4);
        engine._functionKernelCodeRuns.Should().Be(KernelCode.Supported ? 2 : 0);
    }

    [Test]
    public void DebuggingRunsTheInterpreter()
    {
        var engine = new Engine(o => o.Debugger.Enabled = true);
        engine._functionKernelThreshold = 1;
        engine.Execute("function f(a) { var b = a * 2; return b + 1; } var s = 0; for (var i = 0; i < 10; i++) s += f(i);");
        engine.GetValue("s").AsNumber().Should().Be(100);
        engine._functionKernelRuns.Should().Be(0);
    }

    [Test]
    public void CoverageRunsTheInterpreter()
    {
        var engine = new Engine(o => o.Coverage.Enabled = true);
        engine._functionKernelThreshold = 1;
        engine.Execute("function g(a) { var b = a * 2; return b + 1; } function f(a) { var c = g(a) + g(a + 1); return c; }");
        engine.Evaluate("var s = 0; for (var i = 0; i < 10; i++) { s += f(i); s = s + g(i); } s").AsNumber().Should().Be(320);
        engine._functionKernelRuns.Should().Be(0);
    }

    [Test]
    public void DiscardedBodiesAreCompiledAgain()
    {
        var engine = new Engine();
        engine._functionKernelThreshold = 1;
        engine.Execute("function f(a) { var b = a * 2; return b + 1; }");
        engine.Evaluate("f(1)").AsNumber().Should().Be(3);
        engine._functionKernelRuns.Should().Be(1);
        engine.Advanced.DiscardInterpreterCaches();
        engine.Evaluate("f(2)").AsNumber().Should().Be(5);
        engine._functionKernelRuns.Should().Be(2);
    }

    [Test]
    public void AnEngineCompilesABoundedNumberOfBodies()
    {
        var engine = new Engine();
        engine._functionKernelThreshold = 1;
        engine._functionKernelBudget = 2;
        engine.Execute("function f(a) { var b = a + 1; return b; } function g(a) { var b = a + 2; return b; } function h(a) { var b = a + 3; return b; }");
        engine.Evaluate("f(1) + g(1) + h(1)").AsNumber().Should().Be(9);
        engine._functionKernelRuns.Should().Be(2);
        engine._functionKernelBudget.Should().Be(0);
    }

    [TestCase("var progress = ''; function step(t, i) { if (i % 3) { t += i; progress = t; } else t--; return t; } function f() { var t = 0; for (var i = 0; i < 300; i++) { t = step(t, i); } return t; } f();")]
    [TestCase("var progress = ''; function g(a, b) { var c = a * 2; c = c - b; return c; } function f() { var t = 0; for (var i = 0; i < 300; i += 2) { t = g(t, i) % 1000; progress = t; } return t; } f();")]
    [TestCase("var progress = ''; function P(k) { this.k = k; } P.prototype.add = function (t, i) { var r = t + this.k; progress = r; return r + i; }; function mk(p) { var n = 0; return function (t, i) { n = n + 1; var r = p.add(t, i) % 1000; return r + n; }; } function f() { var t = 0, g = mk(new P(2)); for (var i = 0; i < 300; i++) { t = g(t, i) % 997; } return t; } f();")]
    public void StatementLimitStopsAtTheSameStatement(string script)
    {
        for (var limit = 1; limit < 80; limit += 3)
        {
            foreach (var mode in CodeModes)
            {
                Outcome(mode).Should().Be(Outcome(Mode.Interpreter), $"the limit of {limit} statements on {mode}");
            }

            string Outcome(Mode mode)
            {
                var engine = new Engine(o => o.LimitStatements(limit));
                Configure(engine, mode);
                try
                {
                    return engine.Evaluate(script).ToString();
                }
                catch (StatementsCountOverflowException)
                {
                    return "limit at " + engine.GetValue("progress");
                }
            }
        }
    }

    [Test]
    public void RecursionLimitStopsAtTheSameDepth()
    {
        const string Script = "var depth = 0; function f(n) { depth = n; var m = n + 1; f(m); } f(0);";
        foreach (var mode in CodeModes)
        {
            Outcome(mode).Should().Be(Outcome(Mode.Interpreter), mode.ToString());
        }

        static string Outcome(Mode mode)
        {
            var engine = new Engine(o => o.Constraints.MaxRecursionDepth = 40);
            Configure(engine, mode);
            try
            {
                engine.Execute(Script);
                return "no limit";
            }
            catch (RecursionDepthOverflowException e)
            {
                return e.CallChain + " at " + engine.GetValue("depth");
            }
        }
    }

    [Test]
    public void RecursionMovesToAFreshStackUnderAStackCount()
    {
        foreach (var mode in CodeModes)
        {
            var engine = new Engine(o => o.Constraints.MaxExecutionStackCount = 1000);
            Configure(engine, mode);
            engine.Execute("var count = 0; function f() { count++; f(); var x = null; return x; }");
            var result = engine.Evaluate("var r; try { f(); r = 'none'; } catch (e) { r = e.constructor.name; } r + (count >= 1000)").ToString();
            result.Should().Be("RangeErrortrue", mode.ToString());
        }
    }

    [Test]
    public void DeepRecursionEndsInARangeError()
    {
        foreach (var mode in CodeModes)
        {
            var engine = new Engine();
            Configure(engine, mode);
            engine.Execute("function f(n) { var m = n + 1; m = f(m); return m; }");
            var result = engine.Evaluate("var r; try { f(0); r = 'none'; } catch (e) { r = e.constructor.name; } r").ToString();
            result.Should().Be("RangeError", mode.ToString());
        }
    }

    private static void Configure(Engine engine, Mode mode)
    {
        engine._functionKernelThreshold = mode switch
        {
            Mode.Interpreter => -1,
            Mode.CodeWarm or Mode.CodeLate => 5,
            _ => 1,
        };
        engine._functionKernelCodeThreshold = mode == Mode.Tree || !KernelCode.Supported ? -1 : FunctionKernel.DefaultCodeThreshold;
        engine._functionKernelCodeInBackground = false;
        engine._loopKernelCodeWork = mode switch
        {
            Mode.Code => 0,
            Mode.CodeWarm => 100,
            Mode.CodeLate => 1000,
            _ => -1,
        };
    }

    private static string Run(string script, string call, Mode mode, bool exact = true)
        => Run(script, call, mode, out _, out _, out _, exact);

    private static string Run(string script, string call, Mode mode, out int checks, out int kernelRuns, out int codeRuns, bool exact = true, int skip = 0)
    {
        var counter = new CountingConstraint(skip);
        var engine = new Engine(o =>
        {
            if (exact)
            {
                o.AddConstraint(counter);
            }
        });
        Configure(engine, mode);

        string result;
        try
        {
            engine.Execute(script);
            result = engine.Evaluate(call).ToString();
        }
        catch (JavaScriptException e)
        {
            result = $"{e.Error} at {e.Location.Start.Line}:{e.Location.Start.Column} {e.JavaScriptStackTrace}";
        }

        checks = counter.Checks;
        kernelRuns = engine._functionKernelRuns;
        codeRuns = engine._functionKernelCodeRuns;
        return result;
    }

    private sealed class CountingConstraint(int skip) : Constraint
    {
        public int Checks { get; private set; }

        public override int StatementsBeforeNextCheck => skip;

        public override void Check() => Checks++;

        public override void Reset()
        {
        }
    }
}
