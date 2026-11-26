using BenchmarkDotNet.Attributes;
using Jint.Native;

namespace Jint.Benchmark;

/// <summary>
/// Ten thousand calls of one small function shape per row, on its own warmed engine; singleReturn is the control row,
/// whose body stays on the interpreter.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "Gen0", "Gen1", "Gen2")]
public class FunctionCallOverheadBenchmark
{
    private IsolatedScript _row;

    [Params("singleReturn", "branchy", "propertyStore", "nestedCalls", "recursive", "closureControl", "closureCounter", "moduleMethod", "protoThis", "newSmall",
        "tryFinally", "forOfArray", "handIterator", "stringSwitch", "closureSometimes", "arrayPush", "dispatchTable", "typeofChecks",
        "argumentsRead", "restParameter", "applyArguments", "defaultParameter", "destructuredParameter", "spreadCall")]
    public string Shape { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var setup = Shape switch
        {
            "singleReturn" => "function f(a, b) { return a + b; }",
            "branchy" => "function f(a, b) { var c = a * 2; if (c > b) { c = c - b; } else { c = b - c; } return c; }",
            "propertyStore" => "var o = { x: 0, y: 1 }; function f(p, b) { p.x = p.x + b; var y = p.y; return y; }",
            "nestedCalls" => "function g(a, b) { var s = a + b; s = s * 2; return s; } function f(a, b) { var t = g(a, 1); var u = g(b, t); return u; }",
            "recursive" => "function r(n) { var s = 0; if (n > 0) { s = r(n - 1) + n; } return s; } function f(a, b) { var x = r(a & 7); x = x + b; return x; }",
            "closureCounter" => "var f = (function () { var c = 0; return function (a, b) { c = c + b; var r = c + a; return r; }; })();",
            "moduleMethod" => "var M = (function () { var count = 0; function helper(x) { var y = x * 2; return y; } return { get: function (a, b) { count = count + 1; return helper(a) + count + b; } }; })(); function f(a, b) { var r = M.get(a, b); return r; }",
            "protoThis" => "function P(k) { this.k = k; } P.prototype.get = function (a, b) { var y = a + this.k; y = y + b; return y; }; var p = new P(1); function f(a, b) { var r = p.get(a, b); return r; }",
            "newSmall" => "function Pt(x, y) { this.x = x; this.y = y; } function f(a, b) { var q = new Pt(a, b); return q.x; }",
            "tryFinally" => "var log = { n: 0 }; function f(a, b) { var r; try { r = a + b; } finally { log.n = r; } return r; }",
            "forOfArray" => "var arr = [1, 2, 3, 4]; function f(a, b) { var s = a; for (var x of arr) { s += x; } return s + b; }",
            "handIterator" => "var arr = [1, 2, 3]; function iter(a) { var i = 0; return { next: function () { return i < a.length ? { done: false, value: a[i++] } : { done: true }; } }; } function f(a, b) { var it = iter(arr), r = it.next(), s = a; try { for (; !r.done; r = it.next()) { s += r.value; } } finally { s += b; } return s; }",
            "stringSwitch" => "var keys = ['a', 'bb', 'ccc', 'd']; function f(a, b) { var k = keys[a & 3], r; switch (k) { case 'a': r = 1; break; case 'bb': r = 2; break; case 'ccc': r = 3; break; default: r = 4; } return r + b; }",
            "closureSometimes" => "function f(a, b) { if (a % 8 !== 0) { return a + b; } var g = function () { return a; }; return g() + b; }",
            "arrayPush" => "function f(a, b) { var arr = []; for (var i = 0; i < 4; i++) { arr.push(i + a); } return arr.length + b; }",
            "dispatchTable" => "var handlers = [function (x) { return x + 1; }, function (x) { return x * 2; }, function (x) { return x - 3; }]; function f(a, b) { var r = handlers[a % 3](b); return r; }",
            "typeofChecks" => "var cfg = { n: 1 }; function f(a, b) { var r = 0; if (typeof a === 'number') { r += a; } if (typeof cfg.n !== 'undefined') { r += b; } if (typeof missingGlobal === 'undefined') { r += 1; } return r; }",
            "argumentsRead" => "function f(a, b) { var s = 0; for (var i = 0; i < arguments.length; i++) { s += arguments[i]; } return s; }",
            "restParameter" => "function f(...xs) { var r = xs.length + xs[0]; return r; }",
            "applyArguments" => "function g(a, b) { var r = a + b; return r; } function f(a, b) { var r = g.apply(this, arguments); return r; }",
            "defaultParameter" => "function f(a, b, c = a + 1) { var r = b + c; return r; }",
            "destructuredParameter" => "var pt = { x: 1, y: 2 }; function g({ x, y }, b) { var r = x + y + b; return r; } function f(a, b) { var r = g(pt, a); return r; }",
            "spreadCall" => "var extra = [1, 2]; function g(a, b, c) { var r = a + b + c; return r; } function f(a, b) { var r = g(a, ...extra); return r; }",
            _ => "function f(a, b) { var g = function () { return a; }; var c = g() + b; return c; }",
        };

        var argument = Shape == "propertyStore" ? "o" : "i";
        var source = setup + " function run(n) { var s = 0; for (var i = 0; i < n; i++) { s = s + f(" + argument + ", 7); } return s; } run(10000);";
        _row = IsolatedScript.Warm(source);
    }

    [Benchmark]
    public JsValue Run() => _row.Run();
}
