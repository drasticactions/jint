#nullable enable

using System.Collections.Generic;

namespace Jint.Tests.PublicInterface;

/// <summary>
/// The syntax tree of the active script or module, which a host keys its own per-script state by: two scripts
/// parsed under one name are told apart, and code a script created answers that script.
/// </summary>
public class HostActiveScriptProgramTests
{
    [Test]
    public void AHostFunctionSeesTheProgramOfTheScriptThatCalledIt()
    {
        var engine = new Engine();
        var seen = new List<Program?>();
        engine.SetValue("probe", new System.Action(() => seen.Add(engine.Advanced.GetActiveScriptOrModuleProgram())));

        var first = Engine.PrepareScript("probe();", "page.html");
        var second = Engine.PrepareScript("probe();", "page.html");
        engine.Evaluate(first);
        engine.Evaluate(second);

        seen.Should().HaveCount(2);
        seen[0].Should().BeSameAs(first.Program);
        seen[1].Should().BeSameAs(second.Program);
    }

    [Test]
    public void AFunctionAnswersTheScriptItWasDeclaredInWhoeverCallsIt()
    {
        var engine = new Engine();
        Program? seen = null;
        engine.SetValue("probe", new System.Action(() => seen = engine.Advanced.GetActiveScriptOrModuleProgram()));

        var declaring = Engine.PrepareScript("function work() { probe(); }", "page.html");
        var calling = Engine.PrepareScript("work();", "page.html");
        engine.Evaluate(declaring);
        engine.Evaluate(calling);

        seen.Should().BeSameAs(declaring.Program);
    }

    [Test]
    public void EvalCodeAnswersTheScriptThatRanIt()
    {
        var engine = new Engine();
        Program? seen = null;
        engine.SetValue("probe", new System.Action(() => seen = engine.Advanced.GetActiveScriptOrModuleProgram()));

        var script = Engine.PrepareScript("eval('probe()');", "page.html");
        engine.Evaluate(script);

        seen.Should().BeSameAs(script.Program);
    }

    [Test]
    public void NoProgramIsActiveOutsideScript()
    {
        var engine = new Engine();
        engine.Advanced.GetActiveScriptOrModuleProgram().Should().BeNull();
    }
}
