namespace Jint.Tests.Runtime;

/// <summary>
/// Pins the Number-key lanes of typed arrays (JsTypedArray.GetElementOfNumberKey and SetElementOfNumberKey): a computed read
/// or write with a Number key is the integer-indexed element access of the key's numeric value, whatever the prototype chain
/// holds, and the write converts the value before it checks the index.
/// </summary>
public class TypedArrayNumberKeyLaneTests
{
    [Test]
    public void NumberKeysReadAndWriteElementsAndNeverThePrototype()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            (function () {
                'use strict';
                Object.defineProperty(Uint8Array.prototype, '5', { get: function () { return 'proto'; }, set: function () { throw new Error('setter'); }, configurable: true });
                Object.defineProperty(Object.prototype, '-1', { value: 'object-proto', configurable: true });
                var a = new Uint8Array(4);
                for (var i = 0; i < a.length; i++) { a[i] = i * 100; }
                a[5] = 7;
                a[-1] = 7;
                a[1.5] = 7;
                a[-0] = 9;
                var r = [a.join(','), a[5], a[-1], a[1.5], a[-0], a[0], a[3], a[NaN], a[Infinity]];
                delete Uint8Array.prototype['5'];
                delete Object.prototype['-1'];
                return r.map(String).join('|');
            })()
            """).AsString();

        result.Should().Be("9,100,200,44|undefined|undefined|undefined|9|9|44|undefined|undefined");
    }

    [Test]
    public void AWriteConvertsTheValueBeforeTheIndexIsChecked()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            (function () {
                var log = [];
                var a = new Float64Array(2);
                var v = { valueOf: function () { log.push('valueOf'); return 2.5; } };
                a[10] = v;
                a[1] = v;
                var big = new BigInt64Array(1);
                big[0] = 5n;
                var threw = false;
                try { big[0] = 1; } catch (e) { threw = e instanceof TypeError; }
                return log.join(',') + '|' + a[1] + '|' + big[0] + '|' + threw;
            })()
            """).AsString();

        result.Should().Be("valueOf,valueOf|2.5|5|true");
    }

    [Test]
    public void ADetachedBufferReadsUndefinedAndIgnoresWrites()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            (function () {
                var a = new Int16Array(2);
                a[0] = 3;
                var before = a[0];
                a.buffer.transfer();
                a[0] = 4;
                return before + '|' + a[0] + '|' + a.length;
            })()
            """).AsString();

        result.Should().Be("3|undefined|0");
    }
}
