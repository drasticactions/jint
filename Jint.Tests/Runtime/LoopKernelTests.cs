using Jint.Runtime;
using Jint.Runtime.Interpreter.Kernels;

namespace Jint.Tests.Runtime;

/// <summary>
/// A loop kernel stands in for the interpreter's handlers of a <c>for</c> loop, so each script here runs on the
/// interpreter's handlers, on kernel trees, and on the code kernels are compiled to (from the loop's start, and from an
/// iteration the tree has begun), and must produce the same value or the same error at the same place, and check the
/// same number of statements.
/// </summary>
public class LoopKernelTests
{
    /// <summary>How a script's loops run.</summary>
    public enum Mode
    {
        Interpreter,
        Tree,

        // Compiled before the first iteration.
        Code,

        // Compiled once the tree has checked two statements, entered at the head of the loop it is running then.
        CodeMidLoop,

        // Compiled once the tree has checked a hundred statements: after the calls the tree made, through the lanes
        // the tree took; a script whose loops do less stays on the tree.
        CodeWarm,

        // Compiled once the tree has checked a thousand statements, enough for the statements it hardly ran to stay on
        // the tree; a script whose loops do less stays on the tree.
        CodeLate,
    }

    private static readonly Mode[] KernelModes = [Mode.Tree, Mode.Code, Mode.CodeMidLoop, Mode.CodeWarm, Mode.CodeLate];

    private static readonly string[] Scripts =
    [
        // numbers, -0, NaN, the arithmetic, bitwise and shift operators
        """
        function test() {
          var out = [], a = 0, b = -0, c = NaN, d = 7.5;
          for (var i = -3; i < 4; i++) {
            out.push(i % 2, -i % 2, i / 0, (i * 0) / -1, d % i, i & 5, i | 2, i ^ -1, i << 31, i >> 1, i >>> 1, ~i, -i, +i, !i);
            a += i * d; b = b * -1; c = c + i;
          }
          out.push(a, 1 / b, c, 0.1 + 0.2, 2 ** 53 + 1, 1e308 * 10, -1e308 * 10);
          return out.join(",");
        }
        """,
        // comparisons of numbers and of other values, which convert through valueOf in the interpreter
        """
        function test() {
          var log = [], o = { valueOf: function () { log.push("v"); return 3; } }, s = "10";
          var vals = [1, 3, NaN, -0, 0, "3", s, null, undefined, true, o];
          for (var i = 0; i < vals.length; i++) {
            for (var j = 0; j < vals.length; j++) {
              var x = vals[i], y = vals[j];
              log.push(x < y, x <= y, x > y, x >= y, x == y, x != y, x === y, x !== y);
            }
          }
          return log.join("");
        }
        """,
        // strings: concatenation, += on an owned string, conversions in ++
        """
        function test() {
          var s = "", t = "a", n = "5", out = [];
          for (var i = 0; i < 20; i++) { s += i; t = t + "b"; if (i % 5 == 0) { n++; out.push(n); } }
          var u = "x"; for (var k = 0; k < 3; k++) { u++; } out.push(u);
          return s + "|" + t + "|" + out.join(",");
        }
        """,
        // typed arrays of every element type, out-of-range and fractional keys, stores that convert
        """
        function test() {
          var types = [Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array, Float32Array, Float64Array];
          var out = [];
          for (var t = 0; t < types.length; t++) {
            var a = new types[t](6), sum = 0;
            for (var i = -1; i < 8; i++) { a[i] = i * 77.7 - 100; }
            for (var j = -2; j < 9; j += 0.5) { sum += a[j] === undefined ? 1000 : a[j]; }
            a[2] = "12"; a[3] = { valueOf: function () { return 9; } };
            out.push(sum, a[2], a[3], a.length);
          }
          var view = new Uint16Array(new ArrayBuffer(16), 4, 3);
          for (var v = 0; v < 5; v++) { view[v] = v + 1; out.push(view[v]); }
          return out.join(",");
        }
        """,
        // Uint8Array elements read and written in place: conversions of stored values, keys that are not indices, a
        // view at an offset, the name changed to another array in the loop, and lengths a getter or an own property
        // answers
        """
        function test() {
          var out = [], buf = new ArrayBuffer(16), a = new Uint8Array(buf, 4, 8), b = new Uint8Array(3), c = a;
          var values = [-1, 256.7, NaN, Infinity, -Infinity, 1e10, -0, 0.5, 255.9];
          for (var i = 0; i < 9; i++) { a[i] = values[i]; a[i - 0.5] = 7; a[-0] = a[0] + 1; out.push(a[i], a[NaN], a[8], a.length); }
          for (var j = 0; j < 6; j++) { if (j == 3) c = b; c[j] = j + 100; out.push(c[j], c.length); }
          out.push(new Uint8Array(buf).join(":"), b.join(":"));
          var d = new Uint8Array(4), e = new Uint8Array(4);
          Object.defineProperty(e, "length", { value: 2 });
          for (var k = 0; k < d.length; k++) { d[k] = d[k - 1] + 3; out.push(d[k], e.length); }
          Object.defineProperty(Object.getPrototypeOf(Uint8Array.prototype), "length", { get: function () { return 3; }, configurable: true });
          for (var m = 0; m < 5 && m < d.length; m++) { out.push(d[m], d.length); }
          return out.join(",");
        }
        """,
        // Math functions computed in place for Numbers, and called for anything else (with valueOf observed), the
        // function replaced or the name Math shadowed while the loop runs
        """
        function test() {
          var out = [], log = [], o = { valueOf: function () { log.push("v"); return -2.5; } };
          var args = [-3.5, -0, 0, NaN, Infinity, -Infinity, 2.25, "-7.5", o, null, undefined, "x", true];
          for (var i = 0; i < args.length; i++) {
            var a = args[i];
            var b = 1 / Math.abs(a), c = Math.floor(a), d = 1 / Math.ceil(a), e = Math.sqrt(a), f = Math.abs(-i);
            out.push(b, c, d, e, f);
          }
          var saved = Math.abs;
          for (var j = 0; j < 4; j++) { if (j == 2) Math.abs = function (x) { return "own" + x; }; var g = Math.abs(-j); out.push(g); }
          Math.abs = saved;
          var m = Math;
          for (var k = 0; k < 4; k++) { if (k == 2) m = { abs: function (x) { return x * 10; } }; var h = m.abs(-k); out.push(h); }
          return out.join(",") + log.join("");
        }
        """,
        // small functions computed in place for Numbers: a comparison and arithmetic over their parameters and Math,
        // called with other values, replaced, with Math replaced, and called with too few arguments
        """
        var near = function (a, b) { return Math.abs(a - b) < 0.25; };
        function sq(x) { return -x * x + Math.floor(x / 2) % 3; }
        var realMath = Math, realNear = near;
        function test() {
          var out = [], log = [], o = { valueOf: function () { log.push("v"); return 1; } };
          var args = [0, 0.1, 1, -0, NaN, Infinity, "1", o, null, 2.2];
          for (var i = 0; i < args.length; i++) {
            var a = near(args[i], 1), b = sq(args[i]), c = near(args[i]);
            out.push(a, b, c);
          }
          var savedFloor = Math.floor;
          Math.floor = Math.ceil;
          for (var j = 0; j < 4; j++) { var d = sq(j + 0.5), e = Math.floor(j + 0.5); out.push(d, e); }
          Math.floor = savedFloor;
          for (var k = 0; k < 5; k++) {
            if (k == 2) near = function (a, b) { return a + b; };
            if (k == 3) { Math = { abs: function (x) { return "own"; }, floor: function (x) { return "own floor"; } }; }
            var f = near(k, 1), g = sq(k);
            out.push(f, g);
          }
          Math = realMath;
          near = realNear;
          return out.join(",") + log.join("");
        }
        """,
        // a detached buffer and a resizable one, changed while the loop runs
        """
        function test() {
          var out = [];
          var a = new Uint8Array(8);
          for (var i = 0; i < 8; i++) { a[i] = i + 1; if (i == 3) { a.buffer.transfer(); } out.push(a[i], a.length); }
          var rab = new ArrayBuffer(4, { maxByteLength: 16 }), b = new Uint8Array(rab);
          for (var j = 0; j < 10; j++) { b[j] = j; if (j == 2) rab.resize(12); if (j == 6) rab.resize(2); out.push(b[j], b.length); }
          return out.join(",");
        }
        """,
        // arrays: holes read through the prototype, elements changing, length changing
        """
        function test() {
          Array.prototype[3] = "proto";
          var a = [1, 2, , , 5], out = [];
          for (var i = 0; i < a.length + 2; i++) { out.push(a[i]); if (i == 1) a[4] = 50; if (i == 2) a.length = 4; }
          delete Array.prototype[3];
          var b = [0, 1, 2]; for (var j = 0; j < b.length; j++) { if (b.length < 6) b.push(j); out.push(b[j]); }
          var c = [1, 2, 3]; Object.defineProperty(c, 1, { get: function () { out.push("get"); return 20; } });
          for (var k = 0; k < c.length; k++) { out.push(c[k]); }
          return out.join(",");
        }
        """,
        // object properties: shape-mode objects, getters, prototypes, a proxy, primitives as bases
        """
        function test() {
          var calls = 0, out = [];
          var objs = [{ x: 1, y: 2 }, { y: 3, x: 4 }, Object.create({ x: 5 }), { get x() { calls++; return 6; } },
                      new Proxy({ x: 7 }, { get: function (t, k) { calls += 10; return t[k]; } }), "str", 8, [9]];
          for (var i = 0; i < objs.length; i++) { out.push(objs[i].x, objs[i].length); }
          for (var j = 0; j < 3; j++) { var o = objs[j]; out.push(o.x + o.y); o.x = 100 + j; }
          return out.join(",") + "|" + calls;
        }
        """,
        // built-in calls: Math, a replaced Math.abs, Math.random's state, a valueOf that throws
        """
        function test() {
          var out = [];
          for (var i = -2; i < 3; i++) { out.push(Math.abs(i - 0.5), Math.floor(i / 2), Math.max(i, 0), Math.min(i, -i), Math.sqrt(i)); }
          var real = Math.abs; var n = 0;
          for (var j = 0; j < 4; j++) { if (j == 2) Math.abs = function (v) { n++; return -v; }; out.push(Math.abs(j) > 1); }
          Math.abs = real;
          for (var k = 0; k < 3; k++) { out.push(Math.abs({ valueOf: function () { return -k; } })); }
          try { for (var m = 0; m < 3; m++) { Math.abs(Symbol()); } } catch (e) { out.push(e.constructor.name); }
          return out.join(",") + "|" + n;
        }
        """,
        // global variables, an accessor on the global object, a global lexical declaration
        """
        var gx = 1, gy = "s";
        Object.defineProperty(globalThis, "gacc", { get: function () { gx++; return gx; }, configurable: true });
        function test() {
          var out = [];
          for (var i = 0; i < 5; i++) { out.push(gx + i, gy, gacc, typeof gz); gx += 2; }
          return out.join(",");
        }
        """,
        // break, continue, labels, return, nested loops
        """
        function test() {
          var out = [];
          outer: for (var i = 0; i < 5; i++) {
            for (var j = 0; j < 5; j++) {
              if (j == 1) continue;
              if (j == 3) continue outer;
              if (i == 3) break outer;
              out.push(i * 10 + j);
            }
            out.push("end" + i);
          }
          for (var k = 0; ; k++) { if (k > 4) break; out.push(k); }
          for (var m = 0; m < 10; m++) { if (m == 2) { out.push("ret"); return out.join(","); } }
          return "unreachable";
        }
        """,
        // a labelled block and a labelled loop inside the kernel's loop
        """
        function test() {
          var out = [];
          for (var i = 0; i < 4; i++) {
            blk: { if (i == 1) break blk; out.push("b" + i); }
            inner: for (var j = 0; j < 3; j++) { if (j == i) continue inner; if (j > i) break inner; out.push(i + ":" + j); }
          }
          return out.join(",");
        }
        """,
        // errors thrown from inside the loop: their messages, what catches them, and where they point
        """
        function test() {
          var out = [];
          for (var i = 0; i < 3; i++) {
            try { if (i == 1) null.x; else undefined[i]; } catch (e) { out.push(e.message); }
          }
          var c = 1;
          try { for (var j = 0; j < 3; j++) { if (j == 2) throw new Error("e" + j); out.push(j); } } catch (e) { out.push(e.message, e.stack.split("\n")[1]); }
          return out.join("|");
        }
        """,
        """
        function test() {
          for (var i = 0; i < 3; i++) {
            if (i == 2) { undefinedName + 1; }
          }
        }
        """,
        """
        function test() {
          var o = {};
          for (var i = 0; i < 3; i++) { o = i == 2 ? null : o; o.x = i; }
        }
        """,
        // an error out of a later statement of a block, of a nested block, and of the loop's own test and update
        """
        function test() {
          var out = [];
          for (var i = 0; i < 3; i++) {
            out.push(i);
            if (i == 2) { out.push("x"); { out.push("y"); null.x; } }
          }
        }
        """,
        // an error that takes its location from the statement it is thrown in (the break keeps the loop off the
        // interpreter's tight lane, which reports it at the loop)
        """
        function test() {
          var out = [];
          for (var i = 0; i < 3; i++) {
            out.push(i);
            if (i == 2) { out.push("x"); { out.push("y"); out instanceof 5; } }
            if (i > 5) break;
          }
        }
        """,
        """
        function test() {
          var out = [];
          for (var j = 0; j < 3 && (j < 2 || out instanceof 5); j++) { out.push(j); { out.push(j + 1); } }
        }
        """,
        // the same out of code entered at the head of the inner loop, inside a block of the outer loop's tree
        """
        function test() {
          var out = [];
          for (var j = 0; j < 3 && (j < 2 || out instanceof 5); j++) { out.push(j); { for (var q = 0; q < 2; q++) { out.push(q); } } }
        }
        """,
        """
        function test() {
          var out = [], n = [1, 2, 3];
          try { for (var i = 0; i < n.length; i++) { out.push(i); if (i == 1) n = null; } } catch (e) { out.push(e.message, e.stack.split("\n")[1]); }
          var m = { k: 0 };
          for (var j = 0; j < 3; j = m.k++) { out.push(j); if (j == 1) m = undefined; }
        }
        """,
        // const and the temporal dead zone, let in the body, closures over the loop variable
        """
        function test() {
          var out = [], fns = [];
          const k = 5;
          for (var i = 0; i < 3; i++) { let j = i * 2; fns.push(function () { return i + j; }); out.push(k + j); }
          for (var m = 0; m < fns.length; m++) { out.push(fns[m]()); }
          try { for (var n = 0; n < 2; n++) { out.push(late); } } catch (e) { out.push(e.constructor.name); }
          let late = 1;
          try { for (var p = 0; p < 2; p++) { k = p; } } catch (e) { out.push(e.constructor.name); }
          return out.join(",");
        }
        """,
        // eval inside the loop declaring a var that shadows an outer binding
        """
        var shadowed = "global";
        function test() {
          var out = [];
          for (var i = 0; i < 4; i++) { out.push(shadowed); if (i == 1) eval("var shadowed = 'local'"); }
          return out.join(",");
        }
        """,
        // a sloppy eval elsewhere moves the injection epoch; one in the loop's own function shadows a closure's binding
        """
        function noisy(s) { return eval(s); }
        function outer() {
          var x = 1, out = [];
          function inner() {
            for (var i = 0; i < 6; i++) { out.push(x + noisy("1 + 1")); if (i == 2) eval("var x = 5"); out.push(x); }
          }
          inner();
          return out.join(",") + "|" + x;
        }
        function test() {
          var t = 0, out = [];
          for (var i = 0; i < 8; i++) { t += noisy("i => i"); out.push(typeof t, noisy("2") * i); }
          return out.join(",") + "|" + outer();
        }
        """,
        // with
        """
        function test() {
          var out = [], x = "outer", o = { };
          with (o) { for (var i = 0; i < 4; i++) { out.push(x); if (i == 1) o.x = "with"; } }
          return out.join(",");
        }
        """,
        // the arguments object and mapped parameters
        """
        function test(a, b) {
          var out = [];
          for (var i = 0; i < 3; i++) { arguments[0] = i * 10; out.push(a, b, arguments.length); b = i; }
          return out.join(",");
        }
        function run() { return test(1, 2); }
        """,
        // recursion into the same loop, and a loop in a callee
        """
        function test(depth) {
          var out = 0;
          depth = depth || 0;
          for (var i = 0; i < 4; i++) { out += i; if (depth < 3 && i == 2) out += test(depth + 1) * 100; }
          return out;
        }
        """,
        // generators and async functions keep the interpreter's loop
        """
        function* gen() { for (var i = 0; i < 4; i++) { yield i * 2; } }
        function test() {
          var out = [];
          for (var g = gen(), r = g.next(); !r.done; r = g.next()) { out.push(r.value); }
          return out.join(",");
        }
        """,
        // the update and test may be any expression; a loop with an empty body; a var with no initializer
        """
        function test() {
          var out = [], i, j;
          for (i = 0, j = 10; i < j; i += 3, j--) { var z; out.push(i, j, z); }
          for (var k = 0; k < 3; ++k);
          out.push(k);
          for (var a = [1, 2, 3], s = 0; a.length; s += a.pop()) {}
          out.push(s);
          return out.join(",");
        }
        """,
        // calls of interpreted functions: arguments, this, a kept arguments object, recursion, errors and their stacks
        """
        var saved = [];
        function keep() { saved.push(arguments); return arguments.length; }
        function add(a, b, c) { return a + b + (c === undefined ? 0 : c); }
        function thrower(n) { if (n == 2) throw new Error("t" + n); return n; }
        class K {}
        var o = { v: 3, m: function (x) { return this.v + x; }, get g() { saved.push("g"); return add; } };
        function test() {
          var out = [];
          for (var i = 0; i < 4; i++) {
            out.push(keep(i, i + 1), add(i, 1), add(i, 1, 2, 3), o.m(i), o.g(i, i), keep());
            try { thrower(i); } catch (e) { out.push(e.message, e.stack.split("\n").slice(0, 3).join("/")); }
          }
          for (var j = 0; j < saved.length; j++) { out.push(typeof saved[j] == "string" ? saved[j] : saved[j][0] + ":" + saved[j].length); }
          try { for (var k = 0; k < 2; k++) { K(); } } catch (e) { out.push(e.constructor.name, e.message); }
          try { for (var m = 0; m < 2; m++) { o.nothing(m); } } catch (e) { out.push(e.message); }
          try { for (var n = 0; n < 2; n++) { var notFn = 5; notFn(n); } } catch (e) { out.push(e.message); }
          var bound = add.bind(null, 100); for (var p = 0; p < 2; p++) { out.push(bound(p, 1)); }
          return out.join(",");
        }
        """,
        """
        function deep(n) { for (var i = 0; i < 1; i++) { return n <= 0 ? 0 : 1 + deep(n - 1); } }
        function test() { try { return deep(300); } catch (e) { return e.constructor.name + ":" + e.message; } }
        """,
        // function bodies: returns of literals, values and nothing, a return out of a loop, a tail call, a constructor
        """
        "use strict";
        function lit(x) { if (x) { return 1; } return 2; }
        function val(x) { var y = x * 2; return y + 1; }
        function none(x) { x + 1; }
        function early(n) { for (var i = 0; i < n; i++) { if (i == 3) return i; } return -1; }
        function tail(n, acc) { if (n == 0) return acc; return tail(n - 1, acc + n); }
        function Ctor(v) { this.v = v; if (v > 5) return { other: v }; }
        function thrower(x) { if (x == 4) throw new TypeError("bad " + x); return x; }
        function test() {
          var out = [];
          for (var i = 0; i < 10; i++) {
            out.push(lit(i), val(i), none(i), early(i), tail(i * 100, 0), new Ctor(i).v, new Ctor(i).other);
            try { out.push(thrower(i)); } catch (e) { out.push(e.message, e.stack.split("\n")[1]); }
          }
          return out.join(",");
        }
        """,
        // the kernels' own hot shapes, as the WebGL conformance pages write them
        """
        function test() {
          var data = new Uint8Array(32 * 32 * 4), k = 0, failed = 0, tol = 2;
          for (var p = 0; p < data.length; ++p) data[p] = p % 255 == 7 ? 9 : 0;
          var exceptions = [ { x: 1, y: 2, r: 108, g: 72, b: 36, a: 9 } ];
          for (var y = 0; y < 32; ++y) {
            for (var x = 0; x < 32; ++x) {
              var index = (y * 32 + x) * 4;
              var is_exception = false;
              for (var ii = 0; ii < exceptions.length; ++ii) {
                if (exceptions[ii].x == x && exceptions[ii].y == y) {
                  is_exception = true;
                  if (Math.abs(data[index] - exceptions[ii].r) > tol || Math.abs(data[index + 1] - exceptions[ii].g) > tol) failed++;
                }
              }
              if (is_exception) continue;
              for (var i = 0; i < 4; ++i) { if (data[index + i] != 0) { k++; } }
            }
          }
          var color = [255, 0, 0, 255], errorRange = [0, 0, 0, 0], buf = new Uint8Array(64), bad = -1;
          for (var q = 0; q < 16; ++q) { buf[q * 4] = 255; buf[q * 4 + 3] = 255; }
          buf[37] = 1;
          for (var i2 = 0; i2 < 4 * 4; ++i2) {
            var offset = i2 * 4;
            for (var j = 0; j < color.length; ++j) {
              if (Math.abs(buf[offset + j] - color[j]) > errorRange[j]) { bad = i2; break; }
            }
          }
          return k + "," + failed + "," + bad;
        }
        """,
        // loops that count a name to a limit, which the tree tests and updates in place: limits that change, that are
        // not Numbers (one converting through valueOf), lengths that change or that a getter or an own property answers,
        // counters that are not Numbers, that the body changes, that are constants, and every comparison and step
        """
        function test() {
          var out = [], log = [], n = 5, s = "4", o = { valueOf: function () { log.push("v"); return 3; } };
          for (var i = 0; i < n; i++) { if (i == 2) n = 4; out.push(i); }
          for (var i2 = 0; i2 < s; i2++) { if (i2 == 2) s = "x"; out.push(i2); }
          for (var i3 = 0; i3 < o; i3++) out.push(i3);
          for (var i4 = "0"; i4 < 3; i4++) out.push(typeof i4, i4);
          for (var i5 = 0; i5 <= 10; i5 += 3) { if (i5 == 3) i5 = "4"; out.push(i5); }
          for (var i6 = 9; i6 >= 0; i6 -= 2.5) out.push(i6);
          for (var i7 = 6; i7 > 0; i7--) out.push(i7);
          for (var i8 = 0; i8 != 6; ++i8) { if (i8 == 3) i8 = 4; out.push(i8); }
          for (var i9 = 0; i9 !== 3; i9++) out.push(i9);
          for (var i10 = 1; i10 == 1; i10++) out.push(i10);
          for (var i11 = -2; i11 < NaN; i11++) out.push("never");
          for (var i12 = 0; i12 < undefined; i12++) out.push("never");
          for (var i13 = 0; i13 < 3; --i13) { if (i13 < -2) i13 = 10; out.push(i13); }
          var a = [1, 2, 3];
          for (var j = 0; j < a.length; j++) { if (j == 1) a.push(9); if (j == 3) a.length = 2; out.push(a[j]); }
          var t = new Uint8Array(6), u = new Uint8Array(3);
          for (var k = 0; k < t.length; k++) { if (k == 2) t = u; out.push(k, t.length); }
          var v = new Uint8Array(5);
          Object.defineProperty(v, "length", { value: 2 });
          for (var m = 0; m < v.length; m++) out.push(m);
          var w = { length: 3 };
          for (var q = 0; q < w.length; q++) { if (q == 1) w = new Uint8Array(4); out.push(q); }
          var proto = Object.getPrototypeOf(Uint8Array.prototype), desc = Object.getOwnPropertyDescriptor(proto, "length"), x = new Uint8Array(6);
          for (var r = 0; r < x.length; r++) {
            if (r == 2) Object.defineProperty(proto, "length", { get: function () { log.push("g"); return 4; }, configurable: true });
            out.push(r);
          }
          Object.defineProperty(proto, "length", desc);
          for (var c = 0; c < 3; c++) { out.push(c); }
          try { (function () { const z = 0; for (; z < 3; z++) out.push(z); })(); } catch (e) { out.push(e.name); }
          var big = 9007199254740990;
          for (var b = big; b < big + 2; b++) out.push(b);
          return out.join(",") + log.join("");
        }
        """,
        // calls computed in place by the tree too: arguments evaluated in order before the callee runs, a callee that
        // turns out not to be inlinable, a statement limit inside a call, Math functions of Numbers and of other values
        """
        var near = function (a, b) { return Math.abs(a - b) < 0.5; };
        function mix(a, b, c, d) { return a * b - c / d + -a; }
        function test() {
          var out = [], log = [];
          function arg(v) { log.push(v); return v; }
          for (var i = 0; i < 6; i++) {
            var a = near(arg(i), arg(i / 2)), b = mix(i, 2, i + 1, 4 - i), c = mix(i, 2, 3), d = mix(-i, i, "2", 1);
            var e = Math.sqrt(i - 2), f = Math.floor(-i / 3), g = Math.ceil(i + 0.5), h = 1 / Math.abs(-0 * i);
            var p = Math.abs({ valueOf: function () { log.push("o"); return -i; } }), q = Math.abs("" + -i), r = Math.abs();
            out.push(a, b, c, d, e, f, g, h, p, q, r);
          }
          var calls = 0;
          var counted = function (a) { calls++; return a; };
          for (var j = 0; j < 3; j++) { var x = counted(j); if (j == 1) near = counted; var y = near(j, 1); out.push(x, y); }
          return out.join(",") + log.join("") + calls;
        }
        """,
        // operands read in the node that uses them: names, literals, arithmetic and elements of typed arrays (at an
        // offset, out of range, at -0 and fractional keys) and of arrays (holes, objects, strings) in comparisons,
        // element keys, stores, assignments and arguments, with values that are not Numbers and a valueOf observed
        """
        function f(a, b) { return a - b * 2; }
        function test() {
          var out = [], log = [], o = { valueOf: function () { log.push("v"); return 2; } };
          var buf = new ArrayBuffer(12), t = new Uint8Array(buf, 2, 6), arr = [1, , "3", o, 5.5, null, -0];
          for (var i = 0; i < 9; i++) {
            t[i + 0.5] = 9; t[i - 1] = i * 40; t[i] = arr[i % 7];
            var a = t[i - 1], b = arr[i], c = t[i + 1] + arr[i - 2], d = f(t[i], arr[i]), e = arr[i * 1] < t[i - 0];
            if (t[i] < arr[i]) out.push("lt"); else if (arr[i] == t[i - 2]) out.push("eq");
            if (i - 1 < o) out.push("o");
            if (arr[i] > -1) out.push("gt");
            out.push(a, b, c, d, e, t[-0], t[i / 2]);
          }
          var u = new Uint16Array(4), g = [], h = 0;
          for (var j = 0; j < 4; j++) { u[j] = j * 70000; g[j] = u[j] / 3; h += u[j + 1] - u[j]; out.push(g[j], h, u[j] > 1000); }
          out.push(new Uint8Array(buf).join(":"));
          return out.join(",") + log.join("");
        }
        """,
        // a loop entered once per call, its names resolved as the run is entered: names of the function, of a closure
        // and globals, a binding an eval injects between runs, a global shadowed while the loop runs, and recursion
        """
        var g = 1, h = 2;
        function outer() {
          var o = 10;
          function inner(n) { var t = 0; for (var i = 0; i < n; i++) { t += i * o + g; } return t; }
          return inner;
        }
        function withEval(n, code) { var t = 0; if (code) eval(code); for (var i = 0; i < n; i++) { t += g + h; } return t; }
        function rec(n) { var t = 0; for (var i = 0; i < 2; i++) { t += n; if (n > 0 && i == 0) t += rec(n - 1); } return t; }
        function shadow(n) { var t = 0; for (var i = 0; i < n; i++) { if (i == 1) globalThis.h = 7; t += h; } h = 2; return t; }
        function test() {
          var out = [], inner = outer();
          for (var r = 0; r < 6; r++) {
            var a = inner(r), b = withEval(3, r == 2 ? "var g = 100;" : r == 4 ? "var h = 5;" : ""), c = rec(r), d = shadow(3);
            out.push(a, b, c, d);
          }
          return out.join(",");
        }
        """,
    ];

    private static IEnumerable<TestCaseData> Cases()
    {
        for (var i = 0; i < Scripts.Length; i++)
        {
            foreach (var mode in KernelModes)
            {
                yield return new TestCaseData(i, mode);
            }
        }
    }

    [TestCaseSource(nameof(Cases))]
    public void KernelMatchesTheInterpreter(int index, Mode mode)
    {
        var script = Scripts[index];
        // Called often enough for the functions' bodies to run on kernels too.
        var call = "var results = []; for (var round = 0; round < 12; round++) results.push("
                   + (script.Contains("function run()", StringComparison.Ordinal) ? "run()" : "test()")
                   + "); results.join('|')";

        var withKernels = Run(script, call, mode, out var kernelChecks, out var kernelRuns, out var codeRuns);
        var withoutKernels = Run(script, call, Mode.Interpreter, out var interpreterChecks, out _, out _);

        withKernels.Should().Be(withoutKernels);
        kernelChecks.Should().Be(interpreterChecks);
        kernelRuns.Should().BeGreaterThan(0, "the script's loops should have run on kernels");
        if (mode == Mode.Tree)
        {
            codeRuns.Should().Be(0, "the tree mode compiles nothing");
        }
        else if (mode is Mode.Code or Mode.CodeMidLoop)
        {
            codeRuns.Should().BeGreaterThan(0, "the script's loops should have run as compiled code");
        }

        // An exact constraint that lets statements pass between its checks, as a memory limit does.
        Run(script, call, mode, out var skippingChecks, out _, out _, skip: 7).Should().Be(withoutKernels);
        Run(script, call, Mode.Interpreter, out var skippingInterpreterChecks, out _, out _, skip: 7);
        skippingChecks.Should().Be(skippingInterpreterChecks);

        // Without an exact constraint the interpreter runs its own tight loop lanes.
        Run(script, call, mode, exact: false).Should().Be(Run(script, call, Mode.Interpreter, exact: false));
    }

    [Test]
    public void LoopsStayTreesWhereCodeIsNotCompiled()
    {
        // A runtime without a JIT (Native AOT) answers false, and engines there start with the threshold that keeps
        // every kernel a tree; the tree mode of the tests above is that configuration.
        var engine = new Engine();
        engine._loopKernelCodeThreshold.Should().Be(KernelCode.Supported ? KernelCode.DefaultThreshold : -1);
        KernelCode.Supported.Should().Be(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled);

        Run("function test() { var t = 0; for (var i = 0; i < 5000; i++) { t += i; } return t; }", "test()", Mode.Tree, out _, out var runs, out var codeRuns)
            .Should().Be("12497500");
        runs.Should().Be(1);
        codeRuns.Should().Be(0);
    }

    [Test]
    public void LongLoopsAreCompiled()
    {
        var engine = new Engine();
        engine.Evaluate("function test(n) { var t = 0; for (var i = 0; i < n; i++) { t += i; } return t; }");
        engine.Evaluate("test(10)").AsNumber().Should().Be(45);
        engine._loopKernelCodeRuns.Should().Be(0, "a loop of a few iterations stays a tree");
        engine.Evaluate("test(" + (KernelCode.DefaultThreshold * 20) + ")");
        engine._loopKernelCodeRuns.Should().Be(1, "the loop is compiled once the tree has done enough work, and entered mid-loop");
        engine.Evaluate("test(10)").AsNumber().Should().Be(45);
        engine._loopKernelCodeRuns.Should().Be(2, "once compiled, the loop runs as code from its start");
    }

    [TestCase("var progress = ''; function f() { var t = 0; for (var i = 0; i < 1000; i++) { if (i % 3) { t += i; progress = t; } else t--; } return t; } f();")]
    [TestCase("var progress = ''; function g(a, b) { return a * 2 - b; } function f() { var t = 0; for (var i = 0; i < 1000; i += 2) { t = g(t, i) % 1000; progress = t; } return t; } f();")]
    public void StatementLimitStopsAtTheSameStatement(string script)
    {
        for (var limit = 1; limit < 60; limit++)
        {
            foreach (var mode in KernelModes)
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
        const string Script = "var depth = 0; function f(n) { for (var i = 0; i < 2; i++) { depth = n; if (i == 1) f(n + 1); } } f(0);";
        foreach (var mode in KernelModes)
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

    private static void Configure(Engine engine, Mode mode)
    {
        // Function bodies stay on the interpreter, so that each loop runs on its own kernel.
        engine._functionKernelThreshold = -1;
        engine._loopKernelsDisabled = mode == Mode.Interpreter;
        engine._loopKernelCodeThreshold = mode switch
        {
            Mode.Code => 0,
            Mode.CodeMidLoop or Mode.CodeWarm or Mode.CodeLate => KernelCode.DefaultThreshold,
            _ => -1,
        };
        engine._loopKernelCodeWork = mode switch
        {
            Mode.CodeMidLoop => 2,
            Mode.CodeWarm => 100,
            Mode.CodeLate => 1000,
            _ => -1,
        };
    }

    private static string Run(string script, string call, Mode mode, bool exact = true)
        => Run(script, call, mode, out _, out _, out _, exact);

    private static string Run(string script, string call, Mode mode, out int checks, out int runs, out int codeRuns, bool exact = true, int skip = 0)
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
        runs = engine._loopKernelRuns;
        codeRuns = engine._loopKernelCodeRuns;
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
