namespace Jint.Tests.Runtime;

/// <summary>
/// <c>o[key]</c> with a string key and an object <c>o</c> is [[Get]] with <c>o</c> as the receiver (GetValue of the
/// property reference, https://tc39.es/ecma262/#sec-getvalue). The read lane caches the slot a
/// shape gives the key, so the entry must not serve another key, another shape, or a key the receiver does not own.
/// </summary>
public class ComputedStringKeyReadTests
{
    [Test]
    public void TheCachedSlotServesOnlyItsKeyAndShape()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            function read(o, k) { return o[k]; }
            const a = { x: 1, y: 2 }, b = { x: 3, y: 4 }, c = { y: 5, x: 6 };
            const out = [];
            for (let i = 0; i < 3; i++) {
                out.push(read(a, 'x'), read(b, 'x'), read(a, 'y'), read(c, 'x'), read(a, 'x' + ''), read(a, ['x', 'y'][i % 2]));
            }
            out.push(read(a, 'z'), read(a, 'toString') === Object.prototype.toString, read({ ['0']: 'zero' }, '0'));
            out.join();
            """);

        result.AsString().Should().Be("1,3,2,6,1,1,1,3,2,6,1,2,1,3,2,6,1,1,,true,zero");
    }

    [Test]
    public void ReadsSeeGettersPrototypesProxiesAndLaterChanges()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            function read(o, k) { return o[k]; }
            const proto = { get p() { return 'getter:' + (this === child); } };
            const child = Object.create(proto);
            child.own = 1;
            const log = [];
            const proxy = new Proxy({ q: 1 }, { get(t, k, r) { log.push(k); return 'proxied'; } });
            const o = { v: 1 };
            const out = [read(child, 'p'), read(child, 'own'), read(proxy, 'q'), read(o, 'v')];
            o.v = 2; out.push(read(o, 'v'));
            delete o.v; out.push(read(o, 'v'));
            Object.defineProperty(o, 'v', { get() { return 'accessor'; }, configurable: true }); out.push(read(o, 'v'));
            const arr = [1, 2]; out.push(read(arr, 'length'), read('str', 'length'));
            [out.join(), log.join()].join(' | ');
            """);

        result.AsString().Should().Be("getter:true,1,proxied,1,2,,accessor,2,3 | q");
    }
}
