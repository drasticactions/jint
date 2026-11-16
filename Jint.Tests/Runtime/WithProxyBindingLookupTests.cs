#nullable enable

namespace Jint.Tests.Runtime;

/// <summary>
/// The traps a <c>with (proxy)</c> object sees for a name: resolution asks HasBinding, which is a has trap and, when
/// the property is there, a get of @@unscopables
/// (https://tc39.es/ecma262/#sec-object-environment-records-hasbinding-n); a read asks GetBindingValue, a has trap
/// and a get (https://tc39.es/ecma262/#sec-object-environment-records-getbindingvalue-n-s); a write asks
/// SetMutableBinding, a has trap and a set (https://tc39.es/ecma262/#sec-object-environment-records-setmutablebinding-n-v-s).
/// A name the object no longer holds costs one has trap before resolution goes on outward
/// (https://tc39.es/ecma262/#sec-resolvebinding), also after earlier lookups found it there and the identifier
/// remembered the environment. Every function is called several times so that the lookups a call site remembers
/// answer too. Engines differ from the specification here (V8 skips the has trap of GetBindingValue and
/// SetMutableBinding); the expectations follow the specification, as test262's
/// language/statements/with/*-with-proxy-env.js tests do.
/// </summary>
public class WithProxyBindingLookupTests
{
    // `watched` names the keys the traps log; `run(f)` calls f and returns its result followed by the traps it ran.
    private const string Prelude = """
        var log = [];
        var target = {};
        var watched = {};
        var p = new Proxy(target, {
          has: function (t, k) { if (watched[k]) log.push('has:' + k); return Reflect.has(t, k); },
          get: function (t, k) { if (k === Symbol.unscopables) log.push('get:@@unscopables'); else if (watched[k]) log.push('get:' + k); return Reflect.get(t, k); },
          set: function (t, k, v) { log.push('set:' + k); return Reflect.set(t, k, v); },
          deleteProperty: function (t, k) { log.push('delete:' + k); return Reflect.deleteProperty(t, k); }
        });
        function run(fn) { log = []; var r; try { r = fn(); } catch (e) { r = e.constructor.name; } return String(r) + ' ' + log.join(' '); }
        var x = 'outer';
        function own() { return 'own'; }
        watched.x = true;

        """;

    private static string Run(string script) => new Engine().Evaluate(Prelude + script).AsString();

    private const string Found = "has:x get:@@unscopables has:x get:x";

    [Test]
    public void ACallOfANameTheObjectProvidesResolvesItOnceAndReadsItOnce()
    {
        Run("""
            target.x = own;
            var f; with (p) { f = function () { return x(); }; }
            [run(f), run(f), run(f)].join(' | ')
            """).Should().Be($"own {Found} | own {Found} | own {Found}");
    }

    [Test]
    public void ACallOfANameTheObjectNoLongerHasRunsOneHasTrap()
    {
        Run("""
            target.x = own;
            var f; with (p) { f = function () { return x(); }; }
            var a = [run(f), run(f)];
            delete target.x; x = own;
            a.push(run(f), run(f));
            target.x = own;
            a.push(run(f));
            a.join(' | ')
            """).Should().Be($"own {Found} | own {Found} | own has:x | own has:x | own {Found}");
    }

    [Test]
    public void ACallOfANameTheObjectNeverHadRunsOneHasTrap()
    {
        Run("""
            watched.zz = true;
            var f; with (p) { f = function () { return zz(); }; }
            [run(f), run(f)].join(' | ')
            """).Should().Be("ReferenceError has:zz | ReferenceError has:zz");
    }

    [Test]
    public void ATaggedTemplateAndANewExpressionResolveTheirCalleeOnce()
    {
        Run("""
            target.x = own;
            var f, g; with (p) { f = function () { return x`t`; }; g = function () { return typeof new x(); }; }
            var a = [run(f), run(g), run(f), run(g)];
            delete target.x; x = own;
            a.push(run(f), run(g), run(f), run(g));
            a.join(' | ')
            """).Should().Be($"own {Found} | object {Found} | own {Found} | object {Found} | own has:x | object has:x | own has:x | object has:x");
    }

    [Test]
    public void AReadRunsOneHasTrapOnceTheObjectLostTheName()
    {
        Run("""
            target.x = 1;
            var f; with (p) { f = function () { return x; }; }
            var a = [run(f), run(f)];
            delete target.x;
            a.push(run(f), run(f));
            a.join(' | ')
            """).Should().Be($"1 {Found} | 1 {Found} | outer has:x | outer has:x");
    }

    [Test]
    public void AReadAtStatementLevelOfTheWithBlockRunsOneHasTrapOnceTheObjectLostTheName()
    {
        Run("""
            target.x = 1;
            var a = [];
            with (p) { for (var i = 0; i < 4; i++) { if (i === 2) delete target.x; log = []; a.push(x + ' ' + log.join(' ')); } }
            a.join(' | ')
            """).Should().Be($"1 {Found} | 1 {Found} | outer has:x | outer has:x");
    }

    [Test]
    public void TypeofRunsTheTrapsOfARead()
    {
        Run("""
            watched.zz = true;
            target.x = 1;
            var f, g; with (p) { f = function () { return typeof x; }; g = function () { return typeof zz; }; }
            var a = [run(f), run(f), run(g), run(g)];
            delete target.x;
            a.push(run(f), run(f));
            a.join(' | ')
            """).Should().Be($"number {Found} | number {Found} | undefined has:zz | undefined has:zz | string has:x | string has:x");
    }

    [Test]
    public void AnAssignmentResolvesOnceAndChecksTheBindingBeforeTheSet()
    {
        Run("""
            target.x = 1;
            var f; with (p) { f = function () { x = 2; return target.x; }; }
            var a = [run(f), run(f)];
            delete target.x;
            a.push(run(f), x);
            a.join(' | ')
            """).Should().Be("2 has:x get:@@unscopables has:x set:x | 2 has:x get:@@unscopables has:x set:x | undefined has:x | 2");
    }

    [Test]
    public void ACompoundAssignmentAndAnUpdateResolveOnceThenReadAndWrite()
    {
        const string ReadWrite = "has:x get:@@unscopables has:x get:x has:x set:x";
        Run("""
            target.x = 1;
            var f, g; with (p) { f = function () { x += 1; return target.x; }; g = function () { x++; return target.x; }; }
            var a = [run(f), run(g), run(f), run(g)];
            delete target.x; x = 10;
            a.push(run(f), run(g), x);
            a.join(' | ')
            """).Should().Be($"2 {ReadWrite} | 3 {ReadWrite} | 4 {ReadWrite} | 5 {ReadWrite} | undefined has:x | undefined has:x | 12");
    }

    [Test]
    public void DeletingANameRunsOneHasTrapOnceTheObjectLostIt()
    {
        Run("""
            var f; with (p) { f = function () { return delete x; }; }
            target.x = 1;
            var a = [run(f)];
            target.x = 1;
            a.push(run(f), run(f), run(f));
            a.join(' | ')
            """).Should().Be("true has:x get:@@unscopables delete:x | true has:x get:@@unscopables delete:x | false has:x | false has:x");
    }

    [Test]
    public void ANameUnscopablesBlocksResolvesOutwardAfterOneHasTrap()
    {
        Run("""
            target.x = own;
            var outerFn = function () { return 'outer'; };
            var f, g; with (p) { f = function () { return typeof x; }; g = function () { return x(); }; }
            var a = [run(f), run(g), run(f), run(g)];
            target[Symbol.unscopables] = { x: true }; x = outerFn;
            a.push(run(f), run(g), run(f), run(g));
            delete target[Symbol.unscopables];
            a.push(run(g));
            a.join(' | ')
            """).Should().Be(
            $"function {Found} | own {Found} | function {Found} | own {Found} | "
            + "function has:x get:@@unscopables | outer has:x get:@@unscopables | function has:x get:@@unscopables | outer has:x get:@@unscopables | "
            + $"own {Found}");
    }

    [Test]
    public void ABindingTheUnscopablesLookupRemovesReadsAsUndefinedInSloppyCodeAndThrowsInStrictCode()
    {
        Run("""
            Object.defineProperty(target, Symbol.unscopables, { get: function () { delete target.x; return undefined; }, configurable: true });
            var f, g; with (p) { f = function () { return x; }; g = function () { 'use strict'; return x; }; }
            target.x = 1;
            var a = [run(f)];
            target.x = 1;
            a.push(run(g));
            a.join(' | ')
            """).Should().Be("undefined has:x get:@@unscopables has:x | ReferenceError has:x get:@@unscopables has:x");
    }
}
