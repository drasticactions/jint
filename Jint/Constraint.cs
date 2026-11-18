namespace Jint;

/// <summary>
/// A constraint that engine can check for validate during statement execution.
/// </summary>
public abstract class Constraint
{
    /// <summary>
    /// Called before each statement to check if your requirements are met; if not - throws an exception.
    /// </summary>
    public abstract void Check();

    /// <summary>
    /// Called before script is run. Useful when you use an engine object for multiple executions.
    /// </summary>
    public abstract void Reset();

    /// <summary>
    /// Whether the engine may check this constraint every N statements instead of before every single one.
    /// This is the sole input to that classification, so a constraint decides its own check cadence here.
    /// Defaults to <see langword="false"/>, which is how every user-derived constraint behaved before this
    /// property existed; the built-in constraints each override it and say why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Amortization is sound only when <see cref="Check"/> <b>observes external state without consuming
    /// it</b> — a wall clock, a <see cref="System.Threading.CancellationToken"/>, a flag another thread
    /// sets. For such a constraint, checking less often changes nothing but detection latency, which stays
    /// bounded (the engine also re-checks whenever control returns from host code, so a single long CLR
    /// call cannot stretch it).
    /// </para>
    /// <para>
    /// It is <b>not</b> sound when the call frequency is itself the semantics. A constraint that counts its
    /// own invocations (a statement budget) must stay exact, because skipping calls changes what it
    /// measures. Neither is it sound for a budget over a quantity that can grow without bound between two
    /// checks — an allocation cap, for instance, can be blown arbitrarily far past its limit inside one
    /// statement, so the built-in memory-limit constraint deliberately stays exact.
    /// </para>
    /// <para>
    /// Returning <see langword="true"/> also keeps the interpreter's tight-loop lanes armed, since those
    /// lanes drive amortized constraints at their own bounded cadence but cannot run exact ones.
    /// </para>
    /// </remarks>
    public virtual bool IsAmortizable => false;

    /// <summary>
    /// After a <see cref="Check"/> that passed, how many of the statements that follow may run without this
    /// constraint being checked again. Defaults to 0: checked before every statement. The engine checks its exact
    /// constraints together and, after a round in which all passed, skips the smallest of their answers in
    /// statements, so a constraint that answers more than 0 is checked less often only when every other exact
    /// constraint allows it too. Every <see cref="Reset"/> ends a skip.
    /// </summary>
    /// <remarks>
    /// For a budget whose <see cref="Check"/> costs a read of some counter, with the room left large enough that a
    /// few statements cannot use it up in the ordinary case: answering a small number while the room is large, and 0
    /// close to the limit, keeps where a runaway loop stops while sparing most of the reads.
    /// </remarks>
    public virtual int StatementsBeforeNextCheck => 0;
}
