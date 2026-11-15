namespace Jint.Tests.Runtime;

/// <summary>
/// A parameter list of identifiers ending in a rest identifier (<c>(a, ...rest)</c>) binds in fixed slots, the rest
/// slot taking a new array of the arguments after the identifiers
/// (https://tc39.es/ecma262/#sec-functiondeclarationinstantiation over a list iterator of the arguments, which nothing
/// can observe), and a call whose arguments end in one spread of an array copies the array by index only while its
/// iteration is the built-in one (https://tc39.es/ecma262/#sec-runtime-semantics-argumentlistevaluation). Each case is
/// run with and without a debugger, which binds through the general instantiation.
/// </summary>
public class RestParameterCallTests
{
    private static Engine NewEngine(bool debugger) => new(options => options.Debugger.Enabled = debugger);

    [TestCase(false)]
    [TestCase(true)]
    public void TheRestArrayHoldsTheArgumentsAfterTheIdentifiers(bool debugger)
    {
        var engine = NewEngine(debugger);
        var result = engine.Evaluate("""
            function f(a, ...r) { return a + ':' + JSON.stringify(r) + ':' + Array.isArray(r); }
            const g = (...t) => t;
            function k(a, b, c, ...t) { return [a, b, c, t.length].join(); }
            [f(), f(1), f(1, 2), f(1, 2, 3, 4, 5, 6, 7), JSON.stringify(g()), JSON.stringify(g(1, 2, 3, 4, 5)), k(1), k(1, 2, 3, 4, 5)].join(' | ');
            """);

        result.AsString().Should().Be("undefined:[]:true | 1:[]:true | 1:[2]:true | 1:[2,3,4,5,6,7]:true | [] | [1,2,3,4,5] | 1,,,0 | 1,2,3,2");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EachCallGetsItsOwnRestArray(bool debugger)
    {
        var engine = NewEngine(debugger);
        var result = engine.Evaluate("""
            function h(...t) { t.push(9); return t; }
            function keep(...t) { return () => t.length; }
            const x = h(1), y = h(1);
            const k1 = keep(1, 2, 3), k2 = keep();
            [x === y, JSON.stringify(x), JSON.stringify(y), k1(), k2()].join();
            """);

        result.AsString().Should().Be("false,[1,9],[1,9],3,0");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ARestParameterLeavesTheArgumentsObjectUnmappedAndAVarOfItsNameBound(bool debugger)
    {
        var engine = NewEngine(debugger);
        var result = engine.Evaluate("""
            function m(a, ...t) { a = 5; t[0] = 6; return arguments[0] + ',' + arguments[1] + ',' + arguments.length; }
            function v(...r) { var r; return JSON.stringify(r); }
            function w(...r) { var q = r; return q.length; }
            [m(1, 2), v(1, 2), w(1, 2, 3), m.length, ((a, b, ...c) => 0).length].join(' | ');
            """);

        result.AsString().Should().Be("1,2,2 | [1,2] | 3 | 1 | 2");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ASpreadOfAnArrayCallsAReplacedIterator(bool debugger)
    {
        var engine = NewEngine(debugger);
        var result = engine.Evaluate("""
            const log = [];
            function j() { return JSON.stringify(Array.prototype.slice.call(arguments)); }
            const forward = (...t) => j(...t);
            const arr = [1, 2, 3];
            const before = [j(...arr), j(0, ...arr), j(0, 9, ...arr), forward(4, 5)];
            const saved = Array.prototype[Symbol.iterator];
            Array.prototype[Symbol.iterator] = function () { log.push('iterator'); return saved.call(this); };
            const replaced = [j(...arr), j(0, ...arr), forward(4, 5)];
            Array.prototype[Symbol.iterator] = saved;
            const arrayIterator = Object.getPrototypeOf([][Symbol.iterator]());
            const next = arrayIterator.next;
            arrayIterator.next = function () { log.push('next'); return next.call(this); };
            const replacedNext = j(0, ...[7]);
            arrayIterator.next = next;
            const own = [1, 2];
            own[Symbol.iterator] = function* () { yield 'own'; };
            [before.join(), replaced.join(), replacedNext, j(1, ...own), log.join()].join(' | ');
            """);

        result.AsString().Should().Be("[1,2,3],[0,1,2,3],[0,9,1,2,3],[4,5] | [1,2,3],[0,1,2,3],[4,5] | [0,7] | [1,\"own\"] | iterator,iterator,iterator,next,next");
    }

    [Test]
    public void ArgumentsBeforeATrailingSpreadAreEvaluatedFirst()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            const order = [];
            function o(x) { order.push(x); return x; }
            function j() { return arguments.length; }
            const n = j(o(1), o(2), ...(order.push('spread'), [3, 4]));
            let thrown = '';
            try { j(o(5), ...null); } catch (e) { thrown = e.constructor.name; }
            [n, order.join(), thrown].join(' | ');
            """);

        result.AsString().Should().Be("4 | 1,2,spread,5 | TypeError");
    }
}
