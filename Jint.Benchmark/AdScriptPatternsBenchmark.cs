using BenchmarkDotNet.Attributes;
using Jint.Native;

namespace Jint.Benchmark;

/// <summary>
/// The shapes of compiled ad-tech scripts (Scripts/ad-script-patterns.js) that a page runs in a resize handler: each row
/// runs one shape on its own sloppy-mode engine that has evaluated the script once.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "Gen0", "Gen1", "Gen2")]
public class AdScriptPatternsBenchmark
{
    private IsolatedScript _row;

    [Params("dispatcher", "iteratorTryFinally", "pushApply", "restThroughArguments", "concatSpread", "spreadPlugins", "mergeUnique",
        "protobufRead", "symbolTaggedArrays", "forOfEntries", "schemaValidator")]
    public string Shape { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "ad-script-patterns.js"));
        var units = Shape == "schemaValidator" ? 200 : 10_000;
        _row = IsolatedScript.Warm(Engine.PrepareScript($"CASES.{Shape}({units})"), () =>
        {
            var engine = new Engine();
            engine.Execute(source);
            return engine;
        });
    }

    [Benchmark]
    public JsValue Run() => _row.Run();
}
