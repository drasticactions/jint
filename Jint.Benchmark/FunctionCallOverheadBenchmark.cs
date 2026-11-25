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

    [Params("singleReturn", "branchy", "propertyStore", "nestedCalls", "recursive", "closureControl", "closureCounter", "moduleMethod", "protoThis", "newSmall")]
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
            _ => "function f(a, b) { var g = function () { return a; }; var c = g() + b; return c; }",
        };

        var argument = Shape == "propertyStore" ? "o" : "i";
        var source = setup + " function run(n) { var s = 0; for (var i = 0; i < n; i++) { s = s + f(" + argument + ", 7); } return s; } run(10000);";
        _row = IsolatedScript.Warm(source);
    }

    [Benchmark]
    public JsValue Run() => _row.Run();
}
