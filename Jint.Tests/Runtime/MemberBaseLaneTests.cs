namespace Jint.Tests.Runtime;

/// <summary>
/// The member lanes that work on a base evaluated once: a member call whose base is not a plain identifier or
/// <c>this</c> (<c>p.acc.add(v)</c>), a compound assignment to a literal-name member (<c>this.x -= v.x</c>), and
/// the prototype-member cache those reads share, which serves every receiver of one chain rather than the last
/// one it saw. Each must stay observably identical to the Reference path: the base evaluated once, a getter run
/// once, the right-hand side able to change the object between the read and the write, and the errors of a
/// nullish base or a non-callable value in the same order.
/// </summary>
public class MemberBaseLaneTests
{
    private static string Run(string script) => new Engine().Evaluate(script).AsString();

    [Test]
    public void AMemberCallOnAMemberBaseResolvesThePrototypeMethodForEveryReceiver()
    {
        const string script = """
            function V(x) { this.x = x; }
            V.prototype.get = function () { return this.x; };
            var items = [];
            for (var i = 0; i < 40; i++) { items.push({ v: new V(i) }); }
            items[25].v.get = function () { return 'own'; };
            var out = [];
            for (var round = 0; round < 2; round++) {
                for (var i = 20; i < 30; i++) { out.push(items[i].v.get()); }
            }
            out.join(',');
            """;
        Run(script).Should().Be("20,21,22,23,24,own,26,27,28,29,20,21,22,23,24,own,26,27,28,29");
    }

    [Test]
    public void AMemberCallEvaluatesItsBaseOnceAndBindsThisToIt()
    {
        const string script = """
            var calls = 0, target = { who: function () { return this === target; } };
            function base() { calls++; return target; }
            var ok = true;
            for (var i = 0; i < 10; i++) { ok = ok && base().who(); }
            ok + ':' + calls;
            """;
        Run(script).Should().Be("true:10");
    }

    [Test]
    public void ANonCallableMemberThrowsAfterTheArgumentsWithItsGetterRunOnce()
    {
        const string script = """
            var gets = 0, args = 0, inner = {};
            Object.defineProperty(inner, 'm', { get: function () { gets++; return 5; } });
            var o = { inner: inner }, out = [];
            for (var i = 0; i < 3; i++) {
                try { o.inner.m(args++); } catch (e) { out.push(e instanceof TypeError); }
            }
            out.join(',') + ':' + gets + ':' + args;
            """;
        Run(script).Should().Be("true,true,true:3:3");
    }

    [Test]
    public void AMemberCallOnANullishBaseThrowsBeforeTheArguments()
    {
        const string script = """
            var o = { a: null }, args = 0, out = [];
            for (var i = 0; i < 3; i++) {
                try { o.a.m(args++); } catch (e) { out.push(e instanceof TypeError && /'m'/.test(e.message)); }
            }
            out.join(',') + ':' + args;
            """;
        Run(script).Should().Be("true,true,true:0");
    }

    [Test]
    public void AMemberCallOnAPrimitiveBaseUsesItsPrototype()
    {
        const string script = """
            var o = { n: 1.25, s: 'ab' }, out = [];
            for (var i = 0; i < 3; i++) { out.push(o.n.toFixed(1), o.s.toUpperCase(), o.s.concat(i)); }
            out.join(',');
            """;
        Run(script).Should().Be("1.3,AB,ab0,1.3,AB,ab1,1.3,AB,ab2");
    }

    [Test]
    public void APrototypeMethodSiteServesNewArraysAndAnArrayWithAnOwnMethod()
    {
        const string script = """
            var out = [];
            for (var i = 0; i < 6; i++) {
                var a = [];
                if (i === 4) { a.push = function () { return 'own'; }; }
                out.push(a.push(i));
            }
            out.join(',');
            """;
        Run(script).Should().Be("1,1,1,1,own,1");
    }

    [Test]
    public void APrototypeMethodSiteFollowsReceiversOfDifferentChains()
    {
        const string script = """
            function A() {} A.prototype.name = function () { return 'A'; };
            function B() {} B.prototype = Object.create(A.prototype); B.prototype.name = function () { return 'B'; };
            function C() {} C.prototype = Object.create(A.prototype);
            var rs = [new A(), new B(), new C(), new B(), new A(), new C()], out = [];
            for (var round = 0; round < 2; round++) {
                for (var i = 0; i < rs.length; i++) { out.push(rs[i].name()); }
                if (round === 0) { C.prototype.name = function () { return 'C'; }; }
            }
            out.join('');
            """;
        Run(script).Should().Be("ABABAAABCBAC");
    }

    [Test]
    public void ACompoundAssignmentEvaluatesItsBaseOnce()
    {
        const string script = """
            var calls = 0, t = { x: 1 };
            function base() { calls++; return t; }
            for (var i = 0; i < 5; i++) { base().x += 2; }
            t.x + ':' + calls;
            """;
        Run(script).Should().Be("11:5");
    }

    [Test]
    public void ACompoundAssignmentToAnAccessorRunsTheGetterAndTheSetterOnce()
    {
        const string script = """
            var gets = 0, sets = [], v = 10, o = {};
            Object.defineProperty(o, 'x', { get: function () { gets++; return v; }, set: function (n) { sets.push(n); v = n; } });
            for (var i = 0; i < 3; i++) { o.x *= 2; }
            gets + ':' + sets.join(',');
            """;
        Run(script).Should().Be("3:20,40,80");
    }

    [Test]
    public void ACompoundAssignmentWritesWhatTheObjectIsAfterTheRightHandSide()
    {
        const string script = """
            'use strict';
            var out = [];
            var a = { x: 1 };
            a.x += (delete a.x, 1);
            out.push(a.x, Object.keys(a).join());
            var b = { x: 1 }, seen = [];
            b.x += (Object.defineProperty(b, 'x', { set: function (n) { seen.push(n); }, configurable: true }), 2);
            out.push(seen.join());
            var c = { x: 1 };
            try { c.x += (Object.defineProperty(c, 'x', { writable: false }), 1); } catch (e) { out.push(e instanceof TypeError, c.x); }
            out.join('|');
            """;
        Run(script).Should().Be("2|x|3|true|1");
    }

    [Test]
    public void ACompoundAssignmentToAnInheritedPropertyCreatesAnOwnOne()
    {
        const string script = """
            function P() {} P.prototype.x = 5;
            var o = new P();
            o.x -= 1;
            o.x + ':' + P.prototype.x + ':' + o.hasOwnProperty('x');
            """;
        Run(script).Should().Be("4:5:true");
    }

    [Test]
    public void ACompoundAssignmentOnANullishBaseThrowsBeforeTheRightHandSide()
    {
        const string script = """
            var o = { a: undefined }, rhs = 0, out = [];
            for (var i = 0; i < 3; i++) {
                try { o.a.x += rhs++; } catch (e) { out.push(e instanceof TypeError); }
            }
            out.join(',') + ':' + rhs;
            """;
        Run(script).Should().Be("true,true,true:0");
    }

    [Test]
    public void ACompoundAssignmentOnAPrimitiveBaseFailsOnlyInStrictCode()
    {
        const string script = """
            var o = { s: 'ab' };
            var sloppy = (function () { return o.s.length += 1; })();
            var strict = (function () { 'use strict'; try { o.s.length += 1; return 'no error'; } catch (e) { return e instanceof TypeError; } })();
            sloppy + ':' + strict + ':' + o.s;
            """;
        Run(script).Should().Be("3:true:ab");
    }

    [Test]
    public void CompoundAssignmentsOverManyReceiversOfOneLayout()
    {
        const string script = """
            function V(x, y) { this.x = x; this.y = y; }
            V.prototype.sub = function (v) { this.x -= v.x; this.y -= v.y; return this; };
            V.prototype.scale = function (f) { this.x *= f; this.y *= f; return this; };
            var vs = [], d = new V(1, 2), sum = 0;
            for (var i = 0; i < 50; i++) { vs.push(new V(i, 2 * i)); }
            for (var round = 0; round < 3; round++) {
                for (var i = 0; i < vs.length; i++) { vs[i].sub(d).scale(2); }
            }
            for (var i = 0; i < vs.length; i++) { sum += vs[i].x + vs[i].y; }
            var s = { t: 'a' };
            for (var i = 0; i < 3; i++) { s.t += i; }
            sum + ':' + s.t;
            """;
        // Each component c goes to 8c - 14k for k = 1 (x) and k = 2 (y): sum over i of 8i - 14 + 16i - 28.
        Run(script).Should().Be("27300:a012");
    }
}
