namespace Jint.Tests.Runtime;

/// <summary>
/// Reads of bindings that their environment keeps by name (a scope with function declarations or with more
/// bindings than the slot layout takes, which is what a bundled script's wrapper function is) go through a cached
/// dictionary entry. These pin that the cache follows every change a script can make to such a scope.
/// </summary>
public class DictionaryBindingLayoutTests
{
    // A wrapper with a function declaration and many vars, so its bindings are kept by name.
    private static string Scope(string body)
    {
        var names = string.Join(",", Enumerable.Range(0, 40).Select(i => "pad" + i));
        return "(function () { var " + names + "; function declared() { return 1; }\n" + body + "\n})()";
    }

    [Test]
    public void ClosureReadsSeeAssignmentsToTheOuterBinding()
    {
        var engine = new Engine();
        var result = engine.Evaluate(Scope("""
            var counter = 0;
            function read() { return counter; }
            var seen = [];
            for (var i = 0; i < 5; i++) { counter = i * 10; seen.push(read()); }
            return seen.join(',') + ':' + declared();
            """)).AsString();

        result.Should().Be("0,10,20,30,40:1");
    }

    [Test]
    public void CalleeReadThroughTheCacheFollowsReassignment()
    {
        var engine = new Engine();
        var result = engine.Evaluate(Scope("""
            var f = function () { return 'a'; };
            function call() { return f(); }
            var got = call() + call();
            f = function () { return 'b'; };
            return got + call();
            """)).AsString();

        result.Should().Be("aab");
    }

    [Test]
    public void EvalInjectedShadowIsSeen()
    {
        var engine = new Engine();
        var result = engine.Evaluate(Scope("""
            var x = 'outer';
            function make() {
                return function (code) { if (code) eval(code); return function () { return x; }; };
            }
            var reader = make()(null);
            var first = reader() + reader();
            var shadowing = make();
            var inner = shadowing("var x = 'inner'");
            return first + '|' + inner() + '|' + reader();
            """)).AsString();

        result.Should().Be("outerouter|inner|outer");
    }

    [Test]
    public void DeletedEvalBindingFallsBackToTheOuterOne()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            var x = 'global';
            (function () {
                var pad0, pad1, pad2, pad3, pad4, pad5, pad6, pad7, pad8, pad9, pad10, pad11, pad12;
                function declared() {}
                eval("var x = 'local'");
                function read() { return x; }
                var before = read() + read();
                delete x;
                return before + '|' + read() + read();
            })()
            """).AsString();

        result.Should().Be("locallocal|globalglobal");
    }

    [Test]
    public void ManyBindingsAddedAfterTheFirstReadKeepReadsCorrect()
    {
        var engine = new Engine();
        // the eval adds enough names to reallocate the scope's dictionary after the first reads were cached
        var adds = string.Join(";", Enumerable.Range(0, 100).Select(i => "var e" + i + " = " + i));
        var result = engine.Evaluate(Scope("var v = 'v';\nfunction read() { return v; }\nvar a = read() + read();\neval('" + adds + "');\nv = 'w';\nreturn a + read() + read() + e99;"))
            .AsString();

        result.Should().Be("vvww99");
    }

    [Test]
    public void SeparateInstancesOfTheScopeReadTheirOwnBindings()
    {
        var engine = new Engine();
        var names = string.Join(",", Enumerable.Range(0, 40).Select(i => "pad" + i));
        var result = engine.Evaluate(
            "function module(tag) { var " + names + "; function declared() {} var value = tag; return function () { return value; }; }\n" +
            "var a = module('a'), b = module('b');\n" +
            "var got = '';\n" +
            "for (var i = 0; i < 4; i++) { got += a() + b(); }\n" +
            "got;").AsString();

        result.Should().Be("abababab");
    }

    [Test]
    public void UninitializedLexicalBindingStillThrows()
    {
        var engine = new Engine();
        var result = engine.Evaluate(Scope("""
            function read() { return later; }
            var errors = '';
            for (var i = 0; i < 3; i++) { try { read(); } catch (e) { errors += e.name + ';'; } }
            let later = 'set';
            return errors + read();
            """)).AsString();

        result.Should().Be("ReferenceError;ReferenceError;ReferenceError;set");
    }

    [Test]
    public void AnImportReadThroughTheCacheIsTheExportedValue()
    {
        // A bundler's chunk: a module scope that keeps its bindings by name (imports and declarations past the list
        // dictionary's size), an imported function passed as an argument from a closure called repeatedly, and an
        // inner function shadowing the import's name. The import's own entry only reserves the name, so a read cached
        // from it read undefined from the second call on.
        var engine = new Engine();
        engine.Modules.Add("lib", """
            export var log = [];
            export function each(node, flags, collection, key, render) {
                log.push(typeof key);
                var items = collection();
                for (var y = 0; y < items.length; y++) { render({}, () => items[y]); }
            }
            export function index(item, i) { return i; }
            export function get(f) { return f(); }
            export function r(x) { return x; }
            export function b(x) { return x; }
            """);
        engine.Modules.Add("main", """
            import { each, index as i, get, r, b, log } from 'lib';
            function sidebar(t, n) {
                each(0, 21, () => n.contents, i, (t, n) => {
                    each(0, 21, () => get(n).children, i, (e, t) => { let i = () => 1; });
                });
            }
            var section = { children: [1, 2] };
            for (var k = 0; k < 2; k++) { sidebar({}, { contents: [section, section, section] }); }
            export const result = log.join();
            """);

        var ns = engine.Modules.Import("main");
        ns.Get("result").AsString().Should().Be(string.Join(",", Enumerable.Repeat("function", 8)));
    }
}
