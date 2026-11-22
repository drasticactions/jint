namespace Jint.Tests.Runtime;

/// <summary>
/// Pins the typed array length cache of JintMemberExpression: one site (<c>len</c>) reads <c>x.length</c> warm, and each
/// change that makes the built-in getter no longer the answer is seen by the very next read at that site.
/// </summary>
public class TypedArrayLengthLaneTests
{
    private static string Run(string body)
    {
        var engine = new Engine();
        return engine.Evaluate($$"""
            (function () {
                function len(x) { return x.length; }
                var a = new Uint8Array(4);
                var warm = 0;
                for (var i = 0; i < 20; i++) { warm += len(a); }
                var out = [warm];
                {{body}}
                return out.map(String).join('|');
            })()
            """).AsString();
    }

    [Test]
    public void ServesEveryKindAndLengthThroughOneSite()
    {
        Run("""
            out.push(len(new Float64Array(7)), len(new Int8Array(0)), len(new BigInt64Array(3)), len(a.subarray(1)));
            """).Should().Be("80|7|0|3|3");
    }

    [Test]
    public void AnOwnLengthOnTheArrayShadowsTheGetter()
    {
        Run("""
            Object.defineProperty(a, 'length', { value: 99 });
            out.push(len(a), len(new Uint8Array(2)));
            """).Should().Be("80|99|2");
    }

    [Test]
    public void ALengthOnTheKindsPrototypeShadowsTheGetter()
    {
        Run("""
            Object.defineProperty(Uint8Array.prototype, 'length', { value: 5, configurable: true });
            out.push(len(a), len(new Int16Array(3)));
            delete Uint8Array.prototype.length;
            out.push(len(a));
            """).Should().Be("80|5|3|4");
    }

    [Test]
    public void ARedefinedGetterOnTheTypedArrayPrototypeIsCalled()
    {
        Run("""
            var proto = Object.getPrototypeOf(Uint8Array.prototype);
            var builtin = Object.getOwnPropertyDescriptor(proto, 'length');
            Object.defineProperty(proto, 'length', { get: function () { return 'redefined ' + this.byteLength; }, configurable: true });
            out.push(len(a));
            Object.defineProperty(proto, 'length', builtin);
            out.push(len(a));
            delete proto.length;
            out.push(len(a));
            Object.defineProperty(proto, 'length', builtin);
            """).Should().Be("80|redefined 4|4|undefined");
    }

    [Test]
    public void AChangedPrototypeChainIsFollowed()
    {
        Run("""
            Object.setPrototypeOf(a, { length: 'plain' });
            out.push(len(a));
            Object.setPrototypeOf(a, Uint8Array.prototype);
            out.push(len(a));
            Object.setPrototypeOf(a, Object.getPrototypeOf(Uint8Array.prototype));
            out.push(len(a));
            var mid = Object.create(Uint8Array.prototype);
            Object.setPrototypeOf(a, mid);
            out.push(len(a));
            mid.length = 'mid';
            Object.defineProperty(mid, 'length', { value: 'mid' });
            out.push(len(a));
            Object.setPrototypeOf(Uint8Array.prototype, { length: 'replaced %TypedArray%.prototype' });
            out.push(len(new Uint8Array(1)));
            """).Should().Be("80|plain|4|4|4|mid|replaced %TypedArray%.prototype");
    }

    [Test]
    public void AProxyInTheChainIsAsked()
    {
        Run("""
            var proto = Object.getPrototypeOf(a);
            Object.setPrototypeOf(a, new Proxy(proto, { get: function (t, k, r) { return k === 'length' ? 'proxied' : Reflect.get(t, k, r); } }));
            out.push(len(a));
            Object.setPrototypeOf(a, proto);
            out.push(len(a));
            """).Should().Be("80|proxied|4");
    }

    [Test]
    public void SymbolKeysLeaveTheLengthAlone()
    {
        Run("""
            var s = Symbol('length');
            a[s] = 1;
            Uint8Array.prototype[s] = 2;
            out.push(len(a));
            delete Uint8Array.prototype[s];
            """).Should().Be("80|4");
    }

    [Test]
    public void ALengthFollowsItsBuffer()
    {
        Run("""
            var rab = new ArrayBuffer(4, { maxByteLength: 16 });
            var tracking = new Uint8Array(rab);
            out.push(len(tracking));
            rab.resize(12);
            out.push(len(tracking));
            var fixed = new Uint8Array(rab, 8, 4);
            rab.resize(10);
            out.push(len(fixed));
            a.buffer.transfer();
            out.push(len(a));
            """).Should().Be("80|4|12|0|0");
    }
}
