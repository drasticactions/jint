#nullable enable

using System.Text;
using Jint.Native;
using Jint.Runtime;

namespace Jint.Tests.Runtime;

// Chains of member accesses, calls, tagged templates and `new` expressions, and chains of conditional operators down
// their alternates. Bundles are long in these as much as in operators: YouTube's player bundle holds a builder chain
// of 169 calls, 338 levels of tree, which failed with "Script nesting exceeds maximum depth of 256 levels". Node
// runs every chain below; at about ten times their length it throws a RangeError for most of them.
public partial class ScriptNestingTests
{
    // Room to build a chain thousands of levels deep at the top of the stack, which a kernel then runs at the bottom
    // with more stack than the guard's reserve holds: a deep kernel tree of small frames did not end the process on
    // less.
    private const int KernelCallStackSize = 8 * 1024 * 1024;

    private static readonly string[] Subscripts = ["member", "computed", "call", "optional", "curry", "tagged", "new", "mixed"];

    private const string SubscriptPrelude = """
        var m = { v: 7 }; m.b = m;
        var o = { n: 0, c: function () { this.n++; return this; } };
        function k() { k.n++; return k; } k.n = 0;
        function t(strings) { t.n++; return t; } t.n = 0;
        function C() { this.n = 0; } C.prototype.c = function () { this.n++; return this; };
        var ns = { C: C };
        var q = { n: 0, a: function () { this.n++; return this; } }; q.b = { c: function () { return q; } };

        """;

    public static TestCases<string, string, string> EverySubscriptPositionAndEntry
    {
        get
        {
            var cases = new TestCases<string, string, string>();
            foreach (var shape in Subscripts)
            {
                foreach (var position in Positions)
                {
                    foreach (var entry in Entries)
                    {
                        cases.Add(shape, position, entry);
                    }
                }
            }

            return cases;
        }
    }

    [TestCaseSource(nameof(EverySubscriptPositionAndEntry))]
    public void AThousandLevelChainOfMemberAccessesAndCallsRuns(string shape, string position, string entry) => DedicatedThread.Run(() =>
    {
        var links = LinksForLevels(shape, 1000);
        var engine = new Engine();
        RunThrough(engine, entry, SubscriptPrelude + ExpressionScript(SubscriptChain(shape, links), position));

        engine.GetValue("r").AsNumber().Should().Be(SubscriptChainValue(shape, links));
    }, maxStackSize: DeepCallStackSize);

    [TestCaseSource(nameof(EverySubscriptPositionAndEntry))]
    public void AHundredThousandLevelChainOfMemberAccessesAndCallsFailsCleanly(string shape, string position, string entry) => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        var exception = Caught.Exception(() => RunThrough(engine, entry, SubscriptPrelude + ExpressionScript(SubscriptChain(shape, LinksForLevels(shape, 100_000)), position)));

        // As for an operator chain: where the chain is first walked decides which clean failure it is.
        var expected = position switch
        {
            "eval" => typeof(InsufficientExecutionStackException),
            "function" when entry != "PrepareScript" => typeof(JavaScriptException),
            _ => typeof(ScriptPreparationException),
        };

        exception.Should().BeOfType(expected);
        if (exception is JavaScriptException javaScriptException)
        {
            javaScriptException.Error.AsObject().Get("name").ToString().Should().Be("RangeError");
        }
    }, maxStackSize: SmallStackSize);

    // As for an operator chain, a function built at the top of the stack runs again from the bottom of a recursion
    // that spent the stack down to the guard's RangeError, and a chain this long recurses past what the guard leaves.
    [TestCase("member")]
    [TestCase("call")]
    [TestCase("optional")]
    [TestCase("tagged")]
    [TestCase("mixed")]
    public void ADeepChainOfMemberAccessesAndCallsCalledFromTheBottomOfTheStackThrowsARangeErrorTheScriptCanCatch(string shape) => DedicatedThread.Run(() =>
    {
        var links = LinksForLevels(shape, 1000);
        var engine = new Engine();
        engine.Execute(SubscriptPrelude + $$"""
            function f() { return ({{SubscriptChain(shape, links)}}); }
            var first = f();
            var caught = 0;
            function dive(d) {
                try { return dive(d + 1); }
                catch (e) { if (e instanceof RangeError) { caught++; } return f(); }
            }
            var r = dive(0);
            """);

        engine.GetValue("first").AsNumber().Should().Be(SubscriptChainValue(shape, links));
        engine.GetValue("caught").AsNumber().Should().BeGreaterThan(0);
    }, maxStackSize: DeepCallStackSize);

    // https://tc39.es/ecma262/#sec-evaluatecall: a call through a property reference gets the reference's base as its
    // this value, wherever in the chain it sits, and past the probes a long chain carries.
    [Test]
    public void EveryCallOfALongChainGetsTheObjectItReadTheMethodFrom() => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        engine.Execute($$"""
            function make(i) {
                return { i: i, next: function () { if (this.i !== i) { throw new Error('this is ' + this.i + ' in ' + i); } return make(i + 1); } };
            }
            var r = make(0){{Repeat(".next()", 600)}}.i;
            """);

        engine.GetValue("r").AsNumber().Should().Be(600);
    }, maxStackSize: DeepCallStackSize);

    // https://tc39.es/ecma262/#sec-function-calls-runtime-semantics-evaluation: the callee before its arguments, and
    // each call before the arguments of the next.
    [Test]
    public void ALongChainEvaluatesItsCalleesAndArgumentsInOrder() => DedicatedThread.Run(() =>
    {
        var builder = new StringBuilder("var log = []; function at(i) { log.push(i); return i; } var o = { c: function (x) { log.push('c' + x); return this; } };\no");
        var expected = new List<string>();
        for (var i = 0; i < 600; i++)
        {
            builder.Append(".c(at(").Append(i).Append("))");
            expected.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            expected.Add("c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var engine = new Engine();
        engine.Execute(builder.Append(';').ToString());

        engine.Evaluate("log.join()").AsString().Should().Be(string.Join(",", expected));
    }, maxStackSize: DeepCallStackSize);

    // https://tc39.es/ecma262/#sec-optional-chaining-evaluation: a nullish base ends the whole chain, which is
    // undefined, and nothing after it runs; parentheses end the chain, so what follows them reads undefined.
    [Test]
    public void ALongOptionalChainShortCircuitsWhereverItsBaseIsNullish() => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        engine.Execute($$"""
            var o = { n: 0, c: function () { this.n++; return this; } };
            var z = null;
            var fromTheBottom = z{{Repeat("?.c()", 600)}}.n;
            var fromTheMiddle = o{{Repeat(".c()", 300)}}.none{{Repeat("?.c()", 300)}}.n;
            var callsMade = o.n;
            var afterTheParentheses;
            try { (o{{Repeat(".c()", 300)}}.none{{Repeat("?.c()", 300)}}).n; } catch (e) { afterTheParentheses = e.constructor.name; }
            var withoutTheQuestionMark;
            try { o{{Repeat(".c()", 300)}}.none{{Repeat(".c()", 300)}}; } catch (e) { withoutTheQuestionMark = e.constructor.name; }
            """);

        engine.Evaluate("typeof fromTheBottom").AsString().Should().Be("undefined");
        engine.Evaluate("typeof fromTheMiddle").AsString().Should().Be("undefined");
        engine.GetValue("callsMade").AsNumber().Should().Be(300);
        engine.GetValue("afterTheParentheses").AsString().Should().Be("TypeError");
        engine.GetValue("withoutTheQuestionMark").AsString().Should().Be("TypeError");
    }, maxStackSize: DeepCallStackSize);

    // A probe in a chain is no node of its own to the script: an error names what it named in a short chain, and a
    // stack trace has the frames it had.
    [Test]
    public void AnErrorInALongChainReadsAsInAShortOne() => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        engine.Execute($$"""
            var o = { c: function () { return this; } };
            function boom() { throw new Error('boom'); }
            o.boom = boom;
            function message(f) { try { f(); } catch (e) { return e.constructor.name + ': ' + e.message; } }
            function frames(f) { try { f(); } catch (e) { return e.stack.split('\n').map(function (l) { return l.trim().split(' ')[1].replace(/:\d+:\d+$/, ''); }).join(); } }
            var shortMissing = message(function () { o.c().c().none(); });
            var longMissing = message(function () { o{{Repeat(".c()", 600)}}.none(); });
            var shortUndefined = message(function () { o.c().none.c(); });
            var longUndefined = message(function () { o{{Repeat(".c()", 600)}}.none.c(); });
            function shortThrow() { return o.c().c().boom(); }
            function longThrow() { return o{{Repeat(".c()", 600)}}.boom(); }
            var shortFrames = frames(shortThrow);
            var longFrames = frames(longThrow);
            """);

        engine.GetValue("longMissing").AsString().Should().Be(engine.GetValue("shortMissing").AsString());
        engine.GetValue("longUndefined").AsString().Should().Be(engine.GetValue("shortUndefined").AsString());
        engine.GetValue("longFrames").AsString().Should().Be(engine.GetValue("shortFrames").AsString().Replace("shortThrow", "longThrow"));
        engine.GetValue("longFrames").AsString().Should().StartWith("boom,longThrow,frames");
    }, maxStackSize: DeepCallStackSize);

    // Generators and async functions resume by replaying the expression they suspended in, through every link and
    // probe between the statement and the yield or await.

    [Test]
    public void AGeneratorResumesInsideALongChainOfCalls() => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        engine.Execute($$"""
            var o = { n: 0, c: function () { this.n++; return this; } };
            function* g() { return o{{Repeat(".c()", 300)}}.c(yield 'paused'){{Repeat(".c()", 300)}}.n; }
            var it = g(); var first = it.next(); var last = it.next(1);
            """);

        engine.Evaluate("first.value").AsString().Should().Be("paused");
        engine.Evaluate("last.done").AsBoolean().Should().BeTrue();
        engine.Evaluate("last.value").AsNumber().Should().Be(601);
    }, maxStackSize: DeepCallStackSize);

    [Test]
    public void AnAsyncFunctionResumesInsideALongChainOfCalls() => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        engine.Execute($$"""
            var o = { n: 0, c: function () { this.n++; return this; } };
            async function a() { return o{{Repeat(".c()", 300)}}.c(await Promise.resolve(1)){{Repeat(".c()", 300)}}.n; }
            """);

        engine.Evaluate("a()").UnwrapIfPromise().AsNumber().Should().Be(601);
    }, maxStackSize: DeepCallStackSize);

    [TestCaseSource(nameof(EveryPositionAndEntry))]
    public void AThousandArmConditionalChainRuns(string position, string entry) => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        RunThrough(engine, entry, "var v = 999, tested = 0; function is(i) { tested++; return v === i; }\n" + ExpressionScript(ConditionalChain(1000), position));

        engine.GetValue("r").AsNumber().Should().Be(999);
        engine.GetValue("tested").AsNumber().Should().Be(1000);
    }, maxStackSize: DeepCallStackSize);

    [TestCaseSource(nameof(EveryPositionAndEntry))]
    public void AHundredThousandArmConditionalChainFailsCleanly(string position, string entry) => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        var exception = Caught.Exception(() => RunThrough(engine, entry, "var v = -1; function is(i) { return v === i; }\n" + ExpressionScript(ConditionalChain(100_000), position)));

        // The parser recurses on a conditional's alternate, so on this stack it is the parser that refuses, and a
        // preparation hands on what it refused with. A direct eval's source is parsed when it runs.
        if (entry == "PrepareScript" && position != "eval")
        {
            exception.Should().BeOfType<ScriptPreparationException>().Which.InnerException.Should().BeOfType<InsufficientExecutionStackException>();
        }
        else
        {
            exception.Should().BeOfType<InsufficientExecutionStackException>();
        }
    }, maxStackSize: SmallStackSize);

    [Test]
    public void ADeepConditionalChainCalledFromTheBottomOfTheStackThrowsARangeErrorTheScriptCanCatch() => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        engine.Execute($$"""
            var v = 999; function is(i) { return v === i; }
            function f() { return ({{ConditionalChain(1000)}}); }
            var first = f();
            var caught = 0;
            function dive(d) {
                try { return dive(d + 1); }
                catch (e) { if (e instanceof RangeError) { caught++; } return f(); }
            }
            var r = dive(0);
            """);

        engine.GetValue("first").AsNumber().Should().Be(999);
        engine.GetValue("r").AsNumber().Should().Be(999);
        engine.GetValue("caught").AsNumber().Should().BeGreaterThan(0);
    }, maxStackSize: DeepCallStackSize);

    [Test]
    public void AGeneratorResumesAtTheEndOfALongConditionalChain() => DedicatedThread.Run(() =>
    {
        var engine = new Engine();
        var chain = ConditionalChain(600).Replace(": -1", ": (yield 'paused') + 1", StringComparison.Ordinal);
        engine.Execute($"var v = -1; function is(i) {{ return v === i; }} function* g() {{ return {chain}; }} var it = g(); var first = it.next(); var last = it.next(41);");

        engine.Evaluate("first.value").AsString().Should().Be("paused");
        engine.Evaluate("last.value").AsNumber().Should().Be(42);
    }, maxStackSize: DeepCallStackSize);

    // A loop's kernel compiles its body to nodes of its own, which recurse with no stack probe: a chain deeper than a
    // probe interval runs on the interpreter's nodes instead. Compiled at the top of the stack and run again from the
    // bottom of a recursion, a kernel holding the whole chain ended the process.
    [TestCase("+")]
    [TestCase("||")]
    [TestCase("member")]
    public void ALoopHoldingADeepChainRunsFromTheBottomOfTheStack(string shape) => DedicatedThread.Run(() =>
    {
        var (chain, value) = shape switch
        {
            "+" => (Chain("+", 1000), "1000"),
            "||" => (Chain("||", 1000), "1"),
            _ => ("m" + Repeat(".b", 5000) + ".v", "7"),
        };

        var engine = new Engine();
        engine.Execute(SubscriptPrelude + $$"""
            function f() { var s; for (var i = 0; i < 3; i++) { s = {{chain}}; } return s; }
            var first = f();
            var caught = 0;
            function dive(d) {
                try { return dive(d + 1); }
                catch (e) { if (e instanceof RangeError) { caught++; } return f(); }
            }
            var r = dive(0);
            """);

        engine.GetValue("first").ToString().Should().Be(value);
        engine.GetValue("r").ToString().Should().Be(value);
        engine.GetValue("caught").AsNumber().Should().BeGreaterThan(0);
    }, maxStackSize: KernelCallStackSize);

    // A call cannot have a probe in front of the member access it calls, so where one sits at a probe depth the
    // member below it takes the probe: a chain of calls carries one every 65 levels at most, and the walk counts on
    // that. Each chain here starts at every alignment of the interval under 120 and more levels of other nesting,
    // which the walk allows; the tree is read rather than run from the bottom of the stack.
    [TestCase("call")]
    [TestCase("member")]
    [TestCase("mixed")]
    [TestCase("tagged")]
    public void ALongChainOfMemberAccessesAndCallsKeepsEveryRunOfUnprobedNodesWithinTheLimit(string shape) => DedicatedThread.Run(() =>
    {
        var links = shape switch
        {
            "call" => ".c()",
            "member" => ".b",
            "mixed" => ".c().b.c()",
            _ => "``",
        };

        for (var above = 120; above < 120 + SyntaxNestingProbeInterval + 1; above++)
        {
            var expression = UnprobedCalls(above, (shape == "tagged" ? "o.t" : "o") + Repeat(links, 600 / links.Length * 2) + ".s");

            var (result, longestUnprobedRun) = RunAndMeasureTheDeepOperand(expression);

            result.Should().Be("y");
            longestUnprobedRun.Should().BeLessThanOrEqualTo(SyntaxNesting.MaxUnprobedDepth, $"{above} levels above");
            longestUnprobedRun.Should().BeLessThanOrEqualTo(above + SyntaxNesting.SubscriptProbeInterval + 3, $"{above} levels above");
        }
    }, maxStackSize: DeepCallStackSize);

    public static TestCases<string, string> EveryPositionAndEntry
    {
        get
        {
            var cases = new TestCases<string, string>();
            foreach (var position in Positions)
            {
                foreach (var entry in Entries)
                {
                    cases.Add(position, entry);
                }
            }

            return cases;
        }
    }

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static string ExpressionScript(string expression, string position) => position switch
    {
        "script" => $"var r = ({expression});",
        "function" => $"function f() {{ return ({expression}); }} var r = f();",
        "eval" => $"var r = eval('({expression})');",
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
    };

    /// <summary>How many links of <paramref name="shape"/> make about <paramref name="levels"/> levels of tree.</summary>
    private static int LinksForLevels(string shape, int levels) => shape switch
    {
        "call" or "optional" or "new" => levels / 2,
        "mixed" => levels / 5,
        _ => levels,
    };

    /// <summary>
    /// <paramref name="links"/> links of one kind of chain over <see cref="SubscriptPrelude"/>'s objects, whose value
    /// shows that each link ran: a member read of the object itself, a method call counting its calls on the object it
    /// was read from, a call or tagged template of a function returning itself, a chain on a constructed object, and
    /// a chain alternating calls and member reads.
    /// </summary>
    private static string SubscriptChain(string shape, int links) => shape switch
    {
        "member" => "m" + Repeat(".b", links) + ".v",
        "computed" => "m" + Repeat("[\"b\"]", links) + ".v",
        "call" => "o" + Repeat(".c()", links) + ".n",
        "optional" => "o" + Repeat("?.c()", links) + "?.n",
        "curry" => "k" + Repeat("()", links) + ".n",
        "tagged" => "t" + Repeat("``", links) + ".n",
        "new" => "new ns.C()" + Repeat(".c()", links) + ".n",
        "mixed" => "q" + Repeat(".a().b.c()", links) + ".n",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static double SubscriptChainValue(string shape, int links) => shape is "member" or "computed" ? 7 : links;

    /// <summary>
    /// A chain of <paramref name="arms"/> conditionals, each the alternate of the one before, testing <c>v</c> against
    /// its position through <c>is</c>; <c>-1</c> when no arm matches.
    /// </summary>
    private static string ConditionalChain(int arms)
    {
        var builder = new StringBuilder(arms * 16);
        for (var i = 0; i < arms; i++)
        {
            builder.Append("is(").Append(i).Append(") ? ").Append(i).Append(" : ");
        }

        return builder.Append("-1").ToString();
    }
}
