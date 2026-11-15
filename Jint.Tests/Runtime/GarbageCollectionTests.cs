using System.Text;
using Acornima.Ast;
using Jint.Native;
using Jint.Runtime.Interop;
using Jint.Runtime.Interpreter;

namespace Jint.Tests.Runtime;

// This needs to run without any parallelization because it uses garbage collector state, which cannot be
// isolated: a collection sees the whole process. NUnit runs the non-parallel fixtures in a shift of their
// own, on one worker, so this runs with nothing else in flight — including the other fixtures that also
// read GC state (FinalizationRegistryTests, SharedObjectShapeTests and the two Atomics ones), which are
// non-parallel for the same reason.
[NonParallelizable]
public class GarbageCollectionTests
{
    /// <summary>
    /// Memory a script allocates inside a function, and nothing outside it names, must be collectable once
    /// the call returns — no interpreter cache may go on holding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reachability, not residency.</b> This differenced two <c>GC.GetTotalMemory(forceFullCollection:
    /// true)</c> readings taken either side of the call and required the delta to stay under a 10 MB
    /// epsilon. That is a residency reading of a whole process, so anything else the runner allocates
    /// between the two lands in the number — and the allocation here is a hundred one-megabyte strings,
    /// which are large-object allocations whose segments a forced collection frees without returning. It
    /// failed the macOS leg of a pull request that changed one row of a markdown file and could not touch
    /// the GC at all, and a re-run cleared it; each occurrence costs a twenty-minute CI leg.
    /// </para>
    /// <para>
    /// So the question is asked directly. The script hands the array it built to a host callback that keeps
    /// only a <see cref="WeakReference"/> to it, never a strong one, and after a full collection that
    /// reference says outright whether anything still reaches it. There is no epsilon to tune and no
    /// residue term to be confused by, and the statement is the stronger one: it names the array rather than
    /// an amount of bytes that resembles it. This is the shape
    /// <see cref="PreparedScriptsDoNotRetainSourceTextByDefault"/> was converted to for the same reason, and
    /// the one <see cref="AFiveArgumentCallSiteRetainsNoCallee"/> already used.
    /// </para>
    /// <para>
    /// The magnitude is no longer load-bearing — a reference is alive or it is not, whatever it weighs — but
    /// it is kept because the large-object path is the scenario the regression class is about.
    /// <see cref="AnAllocationTheScriptStillHoldsIsNotCollected"/> is the control that stops this passing
    /// for the wrong reason.
    /// </para>
    /// </remarks>
    [Test]
    public void InternalCachingDoesNotPreventGarbageCollection()
    {
        var engine = new Engine();

        var allocation = AllocateInsideAFunction(engine, keepInAGlobal: false);

        Collect();

        allocation.IsAlive.Should().BeFalse(
            "nothing outside the call names the array, so only an interpreter cache could still reach it");

        // The engine owns the caches under test, so it has to outlive the collection — otherwise this could
        // pass because the engine died rather than because it let go.
        GC.KeepAlive(engine);
    }

    /// <summary>
    /// The control, and the reason the test above can be trusted: the same allocation, still named by a
    /// global, must survive. Without it a harness that failed to allocate at all — or that observed the
    /// wrong value — would satisfy the assertion above for the wrong reason.
    /// </summary>
    [Test]
    public void AnAllocationTheScriptStillHoldsIsNotCollected()
    {
        var engine = new Engine();

        var allocation = AllocateInsideAFunction(engine, keepInAGlobal: true);

        Collect();

        allocation.IsAlive.Should().BeTrue("a global still names the array, so it must not be collected");

        GC.KeepAlive(engine);
    }

    /// <summary>
    /// Runs the allocation and hands back a weak reference to the array the script built, having kept no
    /// strong one. <c>NoInlining</c> so the value cannot stay rooted in the caller's frame across the
    /// collection.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AllocateInsideAFunction(Engine engine, bool keepInAGlobal)
    {
        WeakReference observed = null;

        // Only the first call is recorded, so the second one below can churn the argument buffer without
        // overwriting what this is holding.
        engine.SetValue("observe", new Action<JsValue>(value => observed ??= new WeakReference(value)));

        engine.Execute($$"""
            var kept = null;
            function allocate() {
                // ~200 MB, because .NET strings are UTF-16; every element is a large-object allocation.
                var block = Array.from({ length: 100 })
                    .map(() => ' '.repeat(1 * 1024 * 1024));
                observe(block);
                {{(keepInAGlobal ? "kept = block;" : "")}}
            }
            allocate();
            """);

        return observed;
    }

    private static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    /// <summary>
    /// A host that hands the engine a fresh script per operation - <c>Evaluate(string)</c> and
    /// <c>Execute(string)</c> both parse a new one on every call - must not leave that engine holding
    /// every script it has ever run.
    /// </summary>
    /// <remarks>
    /// The set recording which scripts an engine has already evaluated is what decides whether the next
    /// run of one counts as a re-evaluation, and it keys on - and so holds - that script's whole AST. It
    /// was the one handler-tree cache without the ceiling its three siblings have, so a long-lived engine
    /// fed ad-hoc sources grew by roughly half a kilobyte per call forever and gave none of it back short
    /// of being dropped (issue #4094). Reachability, not residency: the first script's AST is named by a
    /// <see cref="WeakReference"/> and by nothing else, so this asks whether the engine still reaches it
    /// rather than whether the heap merely looks smaller.
    /// <see cref="AnEvaluatedScriptIsStillHeldBelowTheCeiling"/> is the control that stops it passing
    /// because the observation cannot see retention at all.
    /// </remarks>
    [Test]
    public void AnEngineFedEndlessFreshScriptsDoesNotRetainThemAll()
    {
        var engine = new Engine();

        var first = EvaluateAFreshScript(engine, 0);

        // One script past the ceiling, so the set has had to reset since the one above went in.
        for (var i = 1; i <= Engine.HandlerTreeCacheCeiling; i++)
        {
            EvaluateAFreshScript(engine, i);
        }

        Collect();

        var firstScriptStillReachable = first.IsAlive;
        firstScriptStillReachable.Should().BeFalse("an engine must not go on holding the AST of every script it has ever run");

        engine.EvaluatedScriptCount.Should().BeLessThanOrEqualTo(Engine.HandlerTreeCacheCeiling);

        // ...and not because the engine itself died.
        GC.KeepAlive(engine);
    }

    /// <summary>
    /// The control, and the reason the test above can be trusted: below the ceiling an evaluated script
    /// <em>is</em> still recorded - that is what the set is for, and it is the state the unbounded engine
    /// never left. Without it, an observation that could not see retention at all would satisfy the
    /// assertion above for the wrong reason.
    /// </summary>
    [Test]
    public void AnEvaluatedScriptIsStillHeldBelowTheCeiling()
    {
        var engine = new Engine();

        var first = EvaluateAFreshScript(engine, 0);

        // A second script, so the first is no longer merely the most recent thing the engine touched.
        EvaluateAFreshScript(engine, 1);

        Collect();

        var firstScriptStillReachable = first.IsAlive;
        firstScriptStillReachable.Should().BeTrue("below the ceiling the engine still records the script it evaluated");

        engine.EvaluatedScriptCount.Should().Be(2);

        GC.KeepAlive(engine);
    }

    /// <summary>
    /// A realm canonicalizes one template object per tagged-template Parse Node, and it used to do so in a
    /// dictionary strong on both ends - so an engine fed a fresh source per operation accumulated a frozen
    /// array and its raw array per call, ~1.35 KB a time, forever (issue #4117).
    /// </summary>
    /// <remarks>
    /// The ceiling that bounds the handler-tree caches is no remedy here, because evicting a <em>live</em>
    /// site is script-visible. What is not observable is losing an object no script holds any more, which is
    /// what this asserts - and then that the site rebuilds a proper frozen template object rather than a
    /// husk, since that rebuild path exists only because the value is weak.
    /// <see cref="ACollectionBetweenEvaluationsDoesNotBreakSiteIdentity"/> is the control.
    /// </remarks>
    [Test]
    public void ATaggedTemplateObjectNoScriptHoldsIsNotRetainedByTheRealm()
    {
        var engine = new Engine();
        var prepared = Engine.PrepareScript(TaggedTemplateSite);

        var templateObject = RunAndForget(engine, prepared);

        // so that the engine's most recent result is not the template object itself
        engine.Evaluate("1 + 1");

        Collect();

        var stillReachable = templateObject.IsAlive;
        stillReachable.Should().BeFalse("no script holds the template object, so the realm must not either");

        engine.SetValue("rebuilt", engine.Evaluate(prepared));
        engine.Evaluate("rebuilt.length === 2 && rebuilt[0] === 'head' && rebuilt[1] === 'tail' && rebuilt.raw[0] === 'head' && Object.isFrozen(rebuilt)")
            .AsBoolean().Should().BeTrue("a site whose object was collected must rebuild a proper frozen template object");

        GC.KeepAlive(engine);

        // NoInlining so the result cannot stay rooted in the caller's frame across the collection.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference RunAndForget(Engine engine, Prepared<Script> prepared)
            => new WeakReference(engine.Evaluate(prepared));
    }

    /// <summary>
    /// The control, and the one real hazard of a weak value: a collection between two runs of the same site
    /// must not hand the second run a different object while the first is still held. Without it the test
    /// above would be satisfied by a cache that had simply stopped caching.
    /// </summary>
    [Test]
    public void ACollectionBetweenEvaluationsDoesNotBreakSiteIdentity()
    {
        var engine = new Engine();
        var prepared = Engine.PrepareScript(TaggedTemplateSite);

        var first = engine.Evaluate(prepared);

        Collect();

        var second = engine.Evaluate(prepared);

        ReferenceEquals(first, second).Should().BeTrue("the template object is still held, so the site must still name it");

        GC.KeepAlive(engine);
    }

    /// <summary>
    /// Why the realm's template map holds a <see cref="WeakReference{T}"/> rather than the
    /// <c>JsArray</c> itself.
    /// </summary>
    /// <remarks>
    /// A <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/> keeps its value
    /// alive on <em>key</em> reachability, and the key here is a parse node inside a prepared script the host
    /// shares across engines. A table holding the template object directly would therefore have that shared
    /// AST pin the object - and through it the engine and realm that built it - for as long as the host kept
    /// the script. Measured while choosing the shape: holding the array directly leaves 20 of 20 engines
    /// alive here; the weak reference leaves none. Same hazard as
    /// <see cref="SharedPreparedScriptDoesNotRetainEngines"/>, reached through a different table.
    /// </remarks>
    [Test]
    public void ASharedPreparedScriptWithATaggedTemplateDoesNotRetainEngines()
    {
        var prepared = Engine.PrepareScript("function t(s) { return s; } t`head${1}tail`;");

        const int count = 20;
        var references = new List<WeakReference>(count);
        for (var i = 0; i < count; i++)
        {
            references.Add(EvaluateOnceAndForget(prepared));
        }

        Collect();

        var aliveCount = references.Count(static r => r.IsAlive);
        aliveCount.Should().Be(0, $"{aliveCount} of {count} engines were not collected - the shared site's template object still pins the engine that built it.");

        // The shared script is the subject of the claim, so it has to outlive the collection.
        GC.KeepAlive(prepared);

        // NoInlining so the engine reference cannot be stack-rooted in this frame across the collection.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference EvaluateOnceAndForget(Prepared<Script> prepared)
        {
            var engine = new Engine();
            engine.Execute(prepared);
            return new WeakReference(engine);
        }
    }

    /// <summary>
    /// One tagged-template site, reached through a function call rather than merely parsed, handing its
    /// template object back as the script's completion value.
    /// </summary>
    private const string TaggedTemplateSite = "function t(s) { return s; } function f() { return t`head${1}tail`; } f();";

    /// <summary>
    /// Parses and runs a script nothing else names, handing back a weak reference to its AST and keeping
    /// no strong one. <c>NoInlining</c> so the prepared script cannot stay rooted in the caller's frame
    /// across the collection.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference EvaluateAFreshScript(Engine engine, int ordinal)
    {
        var prepared = Engine.PrepareScript("1 + " + ordinal);
        engine.Execute(prepared);
        return new WeakReference(prepared.Program);
    }

    [Test]
    public void PreparedScriptsDoNotRetainSourceTextByDefault()
    {
        // Regression test for #2560: by default a prepared script must not keep the full source text alive
        // (it was retained per function node to back Function.prototype.toString()). With many large cached
        // scripts that duplicated hundreds of MB of source strings.
        //
        // Retention is a reachability property, so this proves it by reachability. Each arm builds a large
        // source string, prepares a script from it and drops the string; what comes back is the
        // Prepared<Script> and a WeakReference to that exact instance. After a full collection the weak
        // reference answers the question outright: with RetainFunctionSourceText on, the prepared script has
        // to be keeping the string reachable, and with it off nothing may be.
        //
        // Nothing here reads a heap size, and that is the point. This compared GC.GetTotalMemory across the
        // two arms until #3435 and within each arm after it, and both forms are a residency reading of a
        // whole process, so whatever else the runner happens to be holding lands in the number. On the macOS
        // CI leg that residue was large enough to fail the test in both directions within one hour, on pull
        // requests that touched no engine file: once reading 8.0 MB of a ~20 MB saving, once reading the
        // retaining arm as holding too little to be retaining anything at all (#3641). A reachability answer
        // has no residue term to be confused by, and it is the stronger statement anyway - it names the
        // string rather than an amount of bytes that resembles it.

        const int commentChars = 400_000; // ~800 KB of source, so each arm's string is a large-object allocation

        var retaining = PrepareAndDropTheSource(commentChars, retainSourceText: true);
        var byDefault = PrepareAndDropTheSource(commentChars, retainSourceText: false);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        // The control, and what keeps the claim below from passing for the wrong reason: with retention asked
        // for, the prepared script must be what is holding the string, because nothing else holds it any more.
        retaining.Source.IsAlive.Should().BeTrue(
            "RetainFunctionSourceText did not keep the source text alive, so this test is no longer measuring what it claims to measure");

        // And it is the whole text that survived, not a fragment of it.
        ((string) retaining.Source.Target).Length.Should().Be(retaining.SourceLength,
            "the retained source text is the very string that was parsed, whole");

        // The claim itself: the default holds the prepared script and lets the source text go.
        byDefault.Source.IsAlive.Should().BeFalse(
            "a prepared script must not keep the source text it was parsed from alive by default");

        // A weak reference to the string the host handed in cannot see a copy of it, and a copy costs the same
        // bytes, so the trees are asked as well: text a function node can still produce is text the prepared
        // script is still holding. This materializes that text on the retaining arm - which is what releases
        // its own reference to the full string - so it has to run after the reachability assertions, not before.
        FunctionSourceText(byDefault.Prepared).Should().BeNull(
            "the default must publish no source text onto the function node either, not even a per-function copy");
        FunctionSourceText(retaining.Prepared).Should().NotBeNull(
            "the retaining arm publishes the function's own text onto its node");

        // Both prepared scripts are the subject of every assertion above, so both have to outlive the collection.
        GC.KeepAlive(retaining.Prepared);
        GC.KeepAlive(byDefault.Prepared);
    }

    /// <summary>
    /// One arm of <see cref="PreparedScriptsDoNotRetainSourceTextByDefault"/>: a prepared script, and what is
    /// left of the string it was parsed from once the frame that built that string is gone.
    /// </summary>
    private readonly record struct PreparedSource(Prepared<Script> Prepared, WeakReference Source, int SourceLength);

    /// <summary>
    /// Prepares a script from a large, freshly built source string and returns everything about that string
    /// except a strong reference to it. NoInlining so it cannot stay stack-rooted in the calling frame across
    /// the collection that follows.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static PreparedSource PrepareAndDropTheSource(int commentChars, bool retainSourceText)
    {
        var options = new ScriptPreparationOptions
        {
            ParsingOptions = new ScriptParsingOptions { RetainFunctionSourceText = retainSourceText },
        };

        // A tiny function wrapped around a large comment: the source string is big (~commentChars * 2 bytes,
        // UTF-16) while the AST it parses to is tiny, so the string is unmistakably the thing whose fate is
        // being watched. The comment names the arm as well, so the two arms cannot be one shared instance.
        var sb = new StringBuilder(commentChars + 64);
        sb.Append("function big() {\n  /* retain=").Append(retainSourceText).Append(' ');
        sb.Append('x', commentChars);
        sb.Append(" */\n  return 0;\n}\n");
        var source = sb.ToString();

        return new PreparedSource(
            Engine.PrepareScript(source, options: options),
            new WeakReference(source),
            source.Length);
    }

    /// <summary>
    /// The source text the prepared script's single function node can still produce, or <see langword="null"/>
    /// when the parse retained none. Asked because a weak reference to the string the host handed in proves
    /// only that <em>that instance</em> was let go: a regression that sliced a per-function copy at preparation
    /// time would leave the original collectable while holding the same bytes under a different identity.
    /// </summary>
    private static string FunctionSourceText(Prepared<Script> prepared)
    {
        var function = prepared.Program.Body.OfType<FunctionDeclaration>().Single();
        var state = (JintFunctionDefinition.State) function.UserData;
        return state.SourceText.GetValue(function);
    }

    /// <summary>
    /// The script the two shared-prepared-script retention tests run. It covers every cache shape: an ordinary
    /// function (single-slot env cache), a direct-recursive one (bounded RecursiveEnvPool), a let/const block
    /// (block env cache), a for-let loop (loop env cache), for-of/for-in with let head (per-iteration env cache on
    /// the JintForInForOfStatement handler) and a Function-constructor function (definition-level env parked on
    /// the realm-cached dynamic State, which must die with the realm).
    /// </summary>
    private const string RetentionScript = """
        function f(x) { var y = x + 1; return y; }
        function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
        function b(x) { { let y = x + 1; const z = y * 2; f(y + z); } }
        function l(x) { var sum = 0; for (let i = 0; i < 3; i++) { sum += i; } return sum; }
        function fo(arr) { var sum = 0; for (let v of arr) { sum += v; } return sum; }
        function fi(obj) { var keys = ''; for (let k in obj) { keys += k; } return keys; }
        var dyn = new Function('a', 'return a + 1');
        f(1); f(2); fib(8); b(1); b(2); l(1); l(2);
        fo([1, 2, 3]); fo([4, 5]); fi({ a: 1, b: 2 }); fi({ c: 3 });
        dyn(1); dyn(2);
        """;

    [Test]
    public void SharedPreparedScriptDoesNotRetainEngines()
    {
        // Regression test for #2560 (secondary cause, #2413): a prepared script shared across many engines
        // must not pin those engines via environment reuse caches. Function environments are cached on the
        // ScriptFunction instance and block environments on the JintBlockStatement handler instance (both
        // per engine) rather than on state shared through the AST, so once an engine is dropped its cached
        // environments — and the engine/realm they reference — become collectable even while the prepared
        // script stays cached.

        var prepared = Engine.PrepareScript(RetentionScript);

        AssertNoEngineRetained(prepared);
    }

    [Test]
    public void SharedParseOnlyPreparedScriptDoesNotRetainEngines()
    {
        // Same claim, made where it is harder to keep: with StaticAnalysis off nothing is on the AST when the
        // first engine starts, so the function and block state the sibling test finds pre-published is instead
        // published onto the shared tree by the engines themselves, one node at a time, while they run. That is
        // exactly the shape #2560 was about — engine-owned state reaching cross-engine shared storage — so the
        // engine-neutrality of what gets published has to hold node for node, not merely by construction at
        // preparation time.

        var prepared = Engine.PrepareScript(RetentionScript, options: new ScriptPreparationOptions { StaticAnalysis = false });
        prepared.Program.ShouldCarryNothing();

        AssertNoEngineRetained(prepared);
    }

    [Test]
    public void SharedParseOnlyPreparedModuleDoesNotRetainEngines()
    {
        // The module half of the same claim: a module's bindings live in a module environment rather than the
        // global one, and its evaluation runs through the module record, but the tree it publishes onto is the
        // same shared tree.
        var prepared = Engine.PrepareModule(
            RetentionScript.Replace("var dyn", "export const dyn"),
            options: new ModulePreparationOptions { StaticAnalysis = false });

        prepared.Program.ShouldCarryNothing();

        const int count = 20;
        var references = new List<WeakReference>(count);
        for (var i = 0; i < count; i++)
        {
            references.Add(ImportOnceAndForget(prepared));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var aliveCount = references.Count(static r => r.IsAlive);
        prepared.Program.ShouldCarryPublishedInterpreterState();

        aliveCount.Should().Be(0, $"{aliveCount} of {count} engines were not collected — the shared prepared module still pins engines.");

        // NoInlining so the engine reference cannot be stack-rooted in this frame across the GC.Collect calls.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference ImportOnceAndForget(Prepared<Module> prepared)
        {
            var engine = new Engine();
            engine.Modules.Add("main", x => x.AddModule(prepared));
            engine.Modules.Import("main");
            return new WeakReference(engine);
        }
    }

    [Test]
    public void SharedPreparedScriptConvertingFunctionsToDelegatesDoesNotRetainEngines()
    {
        // The delegate a JavaScript function converts to is memoized in two process-wide tables, the lower of
        // which is keyed on the function's AST node — shared, and outliving every engine that runs a prepared
        // script. Both are keyed by the target delegate type as well since #3434, so the AST node now holds one
        // compiled binder per delegate type rather than one in total; this is the pin that those binders stayed
        // engine-neutral, taking their target as a parameter rather than closing over the engine that built them.

        var prepared = Engine.PrepareScript("""
            host.a(function (x) { { let y = x; return String(y); } });
            host.b(function (x) { { let y = x; return String(y); } });
            """);

        const int count = 20;
        var references = new List<WeakReference>(count);
        for (var i = 0; i < count; i++)
        {
            references.Add(ConvertOnceAndForget(prepared));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var aliveCount = references.Count(static r => r.IsAlive);
        prepared.Program.ShouldCarryPublishedInterpreterState();

        aliveCount.Should().Be(0, $"{aliveCount} of {count} engines were not collected — the shared delegate binder cache still pins engines.");

        // NoInlining so the engine reference cannot be stack-rooted in this frame across the GC.Collect calls.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference ConvertOnceAndForget(Prepared<Script> prepared)
        {
            var engine = new Engine();
            engine.SetValue("host", new DelegateConversionTargetTypeTests.BothHost());
            engine.Execute(prepared);
            return new WeakReference(engine);
        }
    }

    private static void AssertNoEngineRetained(Prepared<Script> prepared)
    {
        const int count = 20;
        var references = new List<WeakReference>(count);
        for (var i = 0; i < count; i++)
        {
            // Run inside a helper so no strong reference to the engine survives on this frame.
            references.Add(RunOnceAndForget(prepared));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var aliveCount = references.Count(static r => r.IsAlive);

        // Doubles as the keep-alive for the prepared script, and as the assertion that there was ever anything
        // shared to leak: a run that published nothing onto the tree would satisfy the collection check for the
        // wrong reason.
        prepared.Program.ShouldCarryPublishedInterpreterState();

        aliveCount.Should().Be(0, $"{aliveCount} of {count} engines were not collected — the shared prepared script still pins engines.");

        // NoInlining so the engine reference cannot be stack-rooted in this frame across the GC.Collect calls.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference RunOnceAndForget(Prepared<Script> prepared)
        {
            var engine = new Engine();
            engine.Execute(prepared);
            return new WeakReference(engine);
        }
    }

    [Test]
    public void NestedTypeAccessDoesNotRetainEngines()
    {
        // Regression test: the accessors backing static member access on a type reference live in a cache
        // shared by the whole process and keyed only by (declaring type, member name). A nested type
        // resolves to an accessor that holds a type reference — an object owned by the engine that created
        // it — so caching that accessor kept the engine, its realm and all of its intrinsics reachable for
        // the lifetime of the process. Only the engine that first resolved the member was pinned, so a
        // single engine is enough to observe it: with the leak in place this one is never collected.

        var reference = RunOnceAndForget();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        reference.IsAlive.Should().BeFalse("the engine is still pinned by the shared static member accessor cache.");

        // NoInlining so the engine reference cannot be stack-rooted in this frame across the GC.Collect calls.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference RunOnceAndForget()
        {
            var engine = new Engine();
            engine.SetValue("Holder", TypeReference.CreateTypeReference<NestedTypeLeakHolder>(engine));
            engine.Evaluate("Holder.Inner");
            return new WeakReference(engine);
        }
    }

    [Test]
    public void ALayoutWithLazySlotsDoesNotRetainEnginesOrPerObjectState()
    {
        // A JsObjectLayout is meant to live in a static readonly field for the whole process, and one with
        // lazy slots carries delegates and is referenced from every unmaterialized slot of every object built
        // from it. If a factory, the engine's per-prototype layout memo or the sentinel reached back to the
        // engine or to the per-object state, every item of every batch ever created would be immortal.

        var layout = Jint.Native.JsObjectLayout.CreateBuilder()
            .Add("id")
            .AddLazy("value", static (_, state) => Jint.Native.JsString.Create((string) state))
            .Build();

        var references = new List<WeakReference>();
        for (var i = 0; i < 10; i++)
        {
            references.AddRange(RunOnceAndForget(layout, i));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var aliveCount = references.Count(static r => r.IsAlive);
        GC.KeepAlive(layout);

        aliveCount.Should().Be(0, $"{aliveCount} of {references.Count} engines/objects/states were not collected — the shared layout still pins them.");

        // NoInlining so nothing stays stack-rooted in this frame across the GC.Collect calls.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference[] RunOnceAndForget(Jint.Native.JsObjectLayout layout, int i)
        {
            var engine = new Engine();
            // A distinct state object per item, and only half of the items materialized, so both the resolved
            // and the still-unmaterialized shapes are covered.
            var state = new string('x', 16 + i);
            var obj = Jint.Native.JsObject.Create(engine, layout, [Jint.Native.JsNumber.Create(i), null], state);
            engine.SetValue("o", obj);
            if (i % 2 == 0)
            {
                engine.Evaluate("o.value");
            }

            return [new WeakReference(engine), new WeakReference(state), new WeakReference(obj)];
        }
    }

    /// <summary>
    /// A warmed call site keeping its last callee is a documented, bounded cost of the built-in call cache —
    /// one callee per site, which is a realm intrinsic or a host function far more often than not. It has to
    /// stay bounded to the sites that cache can actually serve. Five arguments is more than
    /// <c>CallFromRegisters</c> has registers for and more than the built-in lane's two, so neither lane can
    /// ever arm here, and remembering the callee would buy a pooled engine nothing but one retained object
    /// graph per such site, for its whole life. The callee is a bound function, which the built-in lane does
    /// record at a site it serves (<see cref="ATwoArgumentCallSiteStillRetainsItsCallee"/>).
    /// </summary>
    [Test]
    public void AFiveArgumentCallSiteRetainsNoCallee()
    {
        AssertCalleeRetention("f(1, 2, 3, 4, 5);", BoundFactory, expectRetained: false);
    }

    /// <summary>
    /// The same for a spread, which makes the argument count a runtime quantity neither lane can express,
    /// regardless of how few arguments the spread happens to produce.
    /// </summary>
    [Test]
    public void ASpreadCallSiteRetainsNoCallee()
    {
        AssertCalleeRetention("f(...[1, 2]);", BoundFactory, expectRetained: false);
    }

    /// <summary>
    /// An interpreted callee is not retained even by a site both lanes serve: the built-in lane never has a
    /// shape for it, and the register lane is armed for its definition rather than for the closure, so the
    /// site holds no closure and none of the environment a closure keeps alive.
    /// </summary>
    [Test]
    public void ATwoArgumentCallSiteRetainsNoInterpretedCallee()
    {
        AssertCalleeRetention("f(1, 2);", ClosureFactory, expectRetained: false);
    }

    /// <summary>
    /// The control, and the reason the three above can be trusted: a site the built-in lane <em>does</em>
    /// serve still retains a callee it records, which is the documented one-entry-per-site cost. Without it,
    /// a harness that simply failed to warm anything would satisfy those assertions for the wrong reason — as
    /// one written against <c>Execute(string)</c> does, since a re-parsed script gets a fresh handler tree
    /// every time and so never reaches the caches at all.
    /// </summary>
    [Test]
    public void ATwoArgumentCallSiteStillRetainsItsCallee()
    {
        AssertCalleeRetention("f(1, 2);", BoundFactory, expectRetained: true);
    }

    // A factory, so each call produces a distinct function rather than one hoisted declaration the global
    // binding would keep alive on its own. The closure's body reads no identifier, because a warmed
    // identifier site inside the callee caches the environment it resolved in — a separate retention, of
    // the function's own call environment and so of the function, that would mask this one.
    private const string ClosureFactory = "function make() { return function (a, b, c, d, e) { return 1; }; }";
    private const string BoundFactory = "function make() { return Math.max.bind(null); }";

    private static void AssertCalleeRetention(string callSite, string factory, bool expectRetained)
    {
        var engine = new Engine();
        engine.Execute(factory);

        // Prepared once and run twice: the handler-tree caches are keyed on the AST node and engage only
        // on a second evaluation of the same program on the same engine.
        var prepared = Engine.PrepareScript(callSite);

        var reference = WarmTheSiteAndForget(engine, prepared);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        reference.IsAlive.Should().Be(expectRetained, expectRetained
            ? $"`{callSite}` arms the built-in call cache, whose one-entry-per-site retention is by design"
            : $"`{callSite}` must not remember the callee it dispatched");

        // The engine owns the handler tree under test and the prepared script owns the node it is keyed
        // on, so both have to outlive the collection.
        GC.KeepAlive(engine);
        GC.KeepAlive(prepared);

        // NoInlining so the callee cannot stay stack-rooted in this frame across the GC.Collect calls.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference WarmTheSiteAndForget(Engine engine, Prepared<Script> prepared)
        {
            engine.Execute("var f = make();");
            var reference = new WeakReference(engine.GetValue("f"));

            // Dispatching through the site is what runs the probe; the second run is what makes the
            // handler tree, and so anything it cached, outlive the evaluation.
            engine.Execute(prepared);
            engine.Execute(prepared);

            // Now nothing script-visible holds the callee, so only an interpreter cache can.
            engine.Execute("f = undefined;");
            return reference;
        }
    }

    /// <summary>
    /// A constructor's methods read its bindings through closures, so each object's method runs under a chain
    /// that is new for every object, and the identifier and operand caches in the method bodies move from one
    /// object's environment to the next. What a cache memoized about an earlier chain went on pinning that chain
    /// after the cache had moved: the hops-1-3 chain memos of an identifier read and of a numeric operand, which
    /// the layout lane does not replace; the hop-0 environment of a read that the slot cache serves from the
    /// third object on (the for-in's object); and the global read's chain memo, kept between republishes. Each
    /// pinned an object of the second run or so for good, and through it everything that object reached — a
    /// tween queue linked forwards from it, without limit. A last object, which the page keeps, runs every site
    /// once more, so a cache may keep only that object's environment.
    /// </summary>
    [Test]
    public void MethodSitesMovingToAnotherObjectsEnvironmentReleaseTheOldOne()
    {
        AssertDroppedEnvironmentsAreCollected(
            """
            var limit = 10;
            function Tween(payload) {
                var held = payload;
                var start = 0;
                for (var key in payload) { }
                track(payload);
                this.update = function (t) { return t > start && t < limit ? held : null; };
            }
            function drive(tween) { tween.update(1); return tween.update(2); }
            function run(n) { for (var i = 0; i < n; i++) { drive(new Tween({ i: i })); } }
            """,
            "kept = new Tween({ i: -1 }); drive(kept);");
    }

    /// <summary>
    /// A method whose environment nothing can capture parks that environment with the engine after its first
    /// call, for the first call of the next object's method, and a site calling each object's own method
    /// remembers what it called. Both kept the last method called, and so the environment it closed over,
    /// for as long as no other object's method ran: the parked environment its function, the call site the
    /// closure itself. Neither may outlive the objects the page let go of, so the last object here is made
    /// and kept but its method is never called.
    /// </summary>
    [Test]
    public void AParkedEnvironmentAndACallSiteKeepNoMethodOfAnObjectLetGo()
    {
        AssertDroppedEnvironmentsAreCollected(
            """
            function Tween(payload) {
                var held = payload;
                track(payload);
                this.update = function () { return 1; };
                this.get = function () { return held; };
            }
            function drive(tween) { return tween.update(); }
            function run(n) { for (var i = 0; i < n; i++) { drive(new Tween({ i: i })); } }
            """,
            "kept = new Tween({ i: -1 });");
    }

    private static void AssertDroppedEnvironmentsAreCollected(string declarations, string keepOne)
    {
        var engine = new Engine();
        var tracked = new List<WeakReference>();
        engine.SetValue("track", new Action<JsValue>(value => tracked.Add(new WeakReference(value))));
        engine.Execute("var kept = null;");

        // Declared functions keep their body trees, so their sites stay warm from one evaluation to the next.
        engine.Execute(declarations);
        RunAndDrop(engine, keepOne);

        Collect();

        tracked.Should().HaveCount(101);
        var alive = tracked.Take(100).Select((reference, i) => (reference, i)).Where(x => x.reference.IsAlive).Select(x => x.i).ToList();
        alive.Should().BeEmpty("only `kept` is named, and an interpreter cache may hold no other object's environment");
        tracked[100].IsAlive.Should().BeTrue("the control: `kept` still names the last object");

        GC.KeepAlive(engine);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void RunAndDrop(Engine engine, string keepOne)
        {
            engine.Execute("run(50);");
            engine.Execute("run(50);");
            engine.Execute(keepOne);
        }
    }

    /// <summary>
    /// A warmed member-read site keeps one receiver, its last — and only while that receiver's entry can still
    /// serve. The first receiver here resolves through an entry that pins it (a dictionary-mode object's own
    /// property, or a member inherited from a prototype); every later one is shape-mode with the member as an
    /// own slot, which the shape entry serves without naming any object. The pinning entry left behind by the
    /// switch was never replaced, because only its own lane wrote it: so a site that read the head of a
    /// forward-linked list once — a tween queue whose head was the first <c>curTween.chain(next)</c> receiver —
    /// kept the whole list alive as it grew, without limit.
    /// </summary>
    [TestCase("var first = { value: 1, extra: 0 }; delete first.extra;")]
    [TestCase("var first = Object.create({ value: 1 });")]
    public void AMemberReadSiteReleasesAReceiverOnceAnotherLaneServesIt(string firstReceiver)
    {
        AssertReceiverRetention(firstReceiver, thenReadAShapeModeObject: true, expectRetained: false);
    }

    /// <summary>
    /// The control for <see cref="AMemberReadSiteReleasesAReceiverOnceAnotherLaneServesIt"/>: with no later
    /// receiver, the same site still retains the first one — the documented one-entry-per-site cost — so the
    /// harness does warm a pinning entry and can see it.
    /// </summary>
    [TestCase("var first = { value: 1, extra: 0 }; delete first.extra;")]
    public void AMemberReadSiteStillRetainsItsLastReceiver(string firstReceiver)
    {
        AssertReceiverRetention(firstReceiver, thenReadAShapeModeObject: false, expectRetained: true);
    }

    /// <summary>
    /// A member inherited from a prototype is served by an entry that names no receiver: the own miss is proved
    /// afresh on every read, so the entry serves every receiver of the chain and keeps none of them, not even
    /// the last one.
    /// </summary>
    [Test]
    public void AMemberReadSiteServingAPrototypeMemberRetainsNoReceiver()
    {
        AssertReceiverRetention("var first = Object.create({ value: 1 });", thenReadAShapeModeObject: false, expectRetained: false);
    }

    private static void AssertReceiverRetention(string firstReceiver, bool thenReadAShapeModeObject, bool expectRetained)
    {
        var engine = new Engine();

        // A declared function's body tree lives as long as the function, so its member site stays warm
        // across evaluations. The call sites are re-parsed every time and cache nothing.
        engine.Execute("function read(o) { return o.value; }");

        var reference = ReadAndForget(engine, firstReceiver, thenReadAShapeModeObject);

        Collect();

        reference.IsAlive.Should().Be(expectRetained, expectRetained
            ? "the site's last receiver is its one retained entry"
            : "the shape entry served the last read, so no entry may still hold the first receiver");

        GC.KeepAlive(engine);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference ReadAndForget(Engine engine, string firstReceiver, bool thenReadAShapeModeObject)
        {
            engine.Execute(firstReceiver);
            var reference = new WeakReference(engine.GetValue("first"));
            engine.Evaluate("read(first)").AsNumber().Should().Be(1);
            engine.Evaluate("read(first)").AsNumber().Should().Be(1);
            if (thenReadAShapeModeObject)
            {
                engine.Evaluate("read({ value: 2 })").AsNumber().Should().Be(2);
                engine.Evaluate("read({ value: 3 })").AsNumber().Should().Be(3);
            }

            engine.Execute("first = undefined;");
            return reference;
        }
    }

    /// <summary>
    /// A tween queue's shape: each node's method saves its own <c>arguments</c>, which holds the next node, so
    /// the list is linked forwards only and a node nothing earlier names must be collectable. The constructor's
    /// methods keep its environment alive, and that environment has an arguments object of its own: the scan
    /// that decides whether a function needs one counts the method's <c>arguments</c>, and under the debugger
    /// every call gets one. The object went back to the pool while the binding still named it, the next call to
    /// rent it was the method call that saved it, and so every node's environment reached the previous node's:
    /// the whole list stayed alive.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void AForwardLinkedChainOfSavedArgumentsIsCollectedFromItsHead(bool debugger)
    {
        var engine = new Engine(options => options.Debugger.Enabled = debugger);
        engine.Execute("function Node() { var saved = null; this.chain = function () { saved = arguments; }; }");
        engine.Execute("var tail = new Node(); function append(n) { for (var i = 0; i < n; i++) { var node = new Node(); tail.chain(node); tail = node; } }");

        var head = CaptureTheHeadAndMoveOn(engine);

        Collect();

        head.IsAlive.Should().BeFalse("only `tail` is named, and the list links forwards from the head to it");

        GC.KeepAlive(engine);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static WeakReference CaptureTheHeadAndMoveOn(Engine engine)
        {
            var reference = new WeakReference(engine.GetValue("tail"));
            engine.Execute("append(100)");
            engine.Execute("append(100)");
            return reference;
        }
    }

    /// <summary>
    /// Only ever used by <see cref="NestedTypeAccessDoesNotRetainEngines"/>: the accessor cache is
    /// process-wide, so a type another test also resolves would let that test's engine take the blame.
    /// </summary>
    private sealed class NestedTypeLeakHolder
    {
        public sealed class Inner
        {
        }
    }

    private static long CurrentlyUsedMemory()
    {
        // Just try to ensure that everything possible gets collected.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static string BytesToString(long bytes)
        => $"{(bytes / 1024.0 / 1024.0),6:0.0} MB";
}
