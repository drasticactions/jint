namespace Jint.Tests.Runtime;

/// <summary>
/// An assignment to a const (or to a class's inner name) is a TypeError in strict and sloppy code alike, and an
/// assignment to a let, const or class binding before its declaration ran is a ReferenceError: SetMutableBinding of
/// https://tc39.es/ecma262/#sec-declarative-environment-records, with a const created as a strict immutable binding
/// whatever the strictness of the code declaring it. A function whose environment keeps its bindings by name (one
/// with a direct eval, a function declaration, or more bindings than the slot layout takes) creates its top-level
/// consts through a different path than one with slots, so every case runs in both.
/// </summary>
public class ImmutableBindingAssignmentTests
{
    // Every case is a function body returning a string; `r(e)` names the error it caught.
    private static string Run(string body, bool strict, string extraDeclaration)
    {
        var engine = new Engine();
        var source = "function r(e) { return 'caught ' + e.constructor.name; }\n"
                     + "(function () { " + (strict ? "'use strict'; " : "") + extraDeclaration + "\n" + body + "\n})()";
        return engine.Evaluate(source).AsString();
    }

    private static readonly string[] Scopes =
    [
        "",
        "eval('');",
        "function declared() {}",
        "var " + string.Join(",", Enumerable.Range(0, 40).Select(i => "pad" + i)) + ";",
    ];

    // Each body runs in sloppy and strict code (a `with` statement in sloppy code only) and in every scope above.
    private static void Check(string body, string expected)
    {
        foreach (var scope in Scopes)
        {
            Run(body, strict: false, scope).Should().Be(expected, $"scope: '{scope}'");
            if (!body.Contains("with ("))
            {
                Run(body, strict: true, scope).Should().Be(expected, $"strict, scope: '{scope}'");
            }
        }
    }

    [TestCase("const c = 1; try { c = 2 } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { c += 2 } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { c **= 2 } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 's'; try { c += 't' } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = null; try { c ??= 2 } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 0; try { c ||= 2 } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { c &&= 2 } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { c++ } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { --c } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { [c] = [2] } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { [...c] = [2] } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { ({ c } = { c: 2 }) } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { ({ a: c = 3 } = {}) } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { for (c of [2]) {} } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { for (c in { k: 1 }) {} } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { for ([c] of [[2]]) {} } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { for ([c] in { k: 1 }) {} } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; var f = () => { c = 2 }; try { f() } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; function f() { c++ } try { f() } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; function f() { eval(''); c = 2 } try { f() } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { eval('c = 2') } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { eval('c++') } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { eval('for (c of [2]) {}') } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; try { eval('\"use strict\"; c = 2') } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; function f() { eval('c = 2') } try { f() } catch (e) { return r(e) } return 'no throw ' + c")]
    [TestCase("const c = 1; with ({}) { try { c = 2 } catch (e) { return r(e) } } return 'no throw ' + c")]
    [TestCase("try { eval('const z = 1; z = 2') } catch (e) { return r(e) } return 'no throw'")]
    [TestCase("{ const c = 1; try { c = 2 } catch (e) { return r(e) } eval(''); return 'no throw ' + c }")]
    [TestCase("{ const c = 1; try { eval('c = 2') } catch (e) { return r(e) } return 'no throw ' + c }")]
    [TestCase("switch (1) { case 1: const c = 1; try { c = 2 } catch (e) { return r(e) } eval(''); return 'no throw ' + c }")]
    [TestCase("const c = 1; var g = function* () { try { c = 2 } catch (e) { yield r(e) } yield 'no throw' }; return g().next().value")]
    [TestCase("class C { m() { try { C = 1 } catch (e) { return r(e) } return 'no throw' } } return new C().m()")]
    public void AssignmentToAConstThrowsATypeError(string body)
    {
        Check(body, "caught TypeError");
    }

    [TestCase("try { x = 1 } catch (e) { return r(e) } let x = 2; return 'no throw'")]
    [TestCase("try { x = 1 } catch (e) { return r(e) } const x = 2; return 'no throw'")]
    [TestCase("try { C = 1 } catch (e) { return r(e) } class C {} return 'no throw'")]
    [TestCase("try { x++ } catch (e) { return r(e) } let x = 1; return 'no throw'")]
    [TestCase("try { x += 1 } catch (e) { return r(e) } const x = 1; return 'no throw'")]
    [TestCase("try { [x] = [1] } catch (e) { return r(e) } let x; return 'no throw'")]
    [TestCase("try { for (x of [1]) {} } catch (e) { return r(e) } let x; return 'no throw'")]
    [TestCase("try { for (x in { k: 1 }) {} } catch (e) { return r(e) } const x = 1; return 'no throw'")]
    [TestCase("var f = () => { x = 1 }; try { f() } catch (e) { return r(e) } let x; return 'no throw'")]
    [TestCase("try { eval('x = 1') } catch (e) { return r(e) } let x; return 'no throw'")]
    [TestCase("{ try { x = 1 } catch (e) { return r(e) } eval(''); const x = 1; return 'no throw' }")]
    public void AssignmentBeforeTheDeclarationThrowsAReferenceError(string body)
    {
        Check(body, "caught ReferenceError");
    }

    [TestCase("let x = 1; x = 2; x += 3; x++; return '' + x", "6")]
    [TestCase("let x = 1; eval('x = 5'); return '' + x", "5")]
    [TestCase("let x = 1; for (x of [7]) {} return '' + x", "7")]
    [TestCase("let x; for (x in { k: 1 }) {} return x", "k")]
    [TestCase("var v; for (v of [8]) {} return '' + v", "8")]
    [TestCase("var o = { v: 0 }; with (o) { for (v of [5]) {} } return '' + o.v", "5")]
    [TestCase("class C {} C = 5; return '' + C", "5")]
    [TestCase("{ class K {} K = 2; return typeof K }", "number")]
    public void AssignmentToAMutableBindingStores(string body, string expected)
    {
        Check(body, expected);
    }

    [Test]
    public void AssignmentToAFunctionExpressionNameIsIgnoredInSloppyCodeAndThrowsInStrictCode()
    {
        // Its name is an immutable binding that is not strict, so only strict code rejects the store.
        foreach (var scope in Scopes)
        {
            Run("var f = function q() { q = 1; return typeof q }; return f()", strict: false, scope).Should().Be("function");
            Run("var f = function q() { try { q = 1 } catch (e) { return r(e) } return typeof q }; return f()", strict: true, scope)
                .Should().Be("caught TypeError");
        }
    }

    [Test]
    public void ForOfHeadThatIsAnUndeclaredNameCreatesAGlobalInSloppyCode()
    {
        var engine = new Engine();
        engine.Evaluate("(function () { for (undeclaredHead of [7]) {} })(); undeclaredHead").AsNumber().Should().Be(7);
        engine.Evaluate("""
            (function () { 'use strict'; try { for (undeclaredStrictHead of [7]) {} } catch (e) { return e.constructor.name } return 'no throw' })()
            """).AsString().Should().Be("ReferenceError");
    }

    [Test]
    public void GlobalConstRejectsAssignmentFromSloppyFunctionsAndEval()
    {
        var engine = new Engine();
        engine.Execute("const gc = 1; function r(e) { return 'caught ' + e.constructor.name; }");
        engine.Evaluate("(function () { try { gc = 2 } catch (e) { return r(e) } return 'no throw' })()").AsString().Should().Be("caught TypeError");
        engine.Evaluate("(function () { try { eval('gc = 2') } catch (e) { return r(e) } return 'no throw' })()").AsString().Should().Be("caught TypeError");
        engine.Evaluate("(function () { try { for (gc in { k: 1 }) {} } catch (e) { return r(e) } return 'no throw' })()").AsString().Should().Be("caught TypeError");
        engine.Evaluate("gc").AsNumber().Should().Be(1);
    }
}
