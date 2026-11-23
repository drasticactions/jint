using System.Diagnostics;
using Jint.Runtime;

namespace Jint.Constraints;

/// <summary>
/// The timestamp arithmetic the time-based constraints share, and the one place that decides what a
/// host-supplied <c>TimeProvider</c> resolves to.
/// </summary>
/// <remarks>
/// <para>
/// A time-based constraint is checked on the interpreter's amortized lane, so its clock read is on a path
/// <c>AGENTS.md</c> says must not get measurably slower. <c>Resolve</c> is what keeps that promise:
/// <c>TimeProvider.System</c> — and no provider at all — fold to <see langword="null"/>, and a
/// <see langword="null"/> field is the constraint's signal to read <see cref="Stopwatch.GetTimestamp"/>
/// directly, exactly as it did before the seam existed. <c>TimeProvider</c>'s own base implementations of
/// <c>GetTimestamp()</c> and <c>TimestampFrequency</c> <em>are</em> <see cref="Stopwatch.GetTimestamp"/>
/// and <see cref="Stopwatch.Frequency"/>, so the fold changes no answer — it only removes the
/// indirection from the case nobody asked to move.
/// </para>
/// </remarks>
internal static class ConstraintClock
{
    /// <summary>
    /// Converts a budget to timestamp ticks on <paramref name="frequency"/>'s scale, clamped so that adding
    /// it to a timestamp cannot overflow. Without the clamp a large budget wraps <see cref="long"/> and
    /// lands the deadline in the <em>past</em>, failing the operation immediately — the opposite of what was
    /// asked for.
    /// </summary>
    internal static long ToTimestampTicks(TimeSpan budget, long frequency)
    {
        var ticks = budget.Ticks * ((double) frequency / TimeSpan.TicksPerSecond);
        return ticks >= long.MaxValue / 2.0 ? long.MaxValue / 2 : (long) ticks;
    }

    /// <summary>
    /// The inverse of <see cref="ToTimestampTicks"/>: how long <paramref name="timestampTicks"/> of a clock
    /// running at <paramref name="frequency"/> is, saturating at <see cref="TimeSpan.MaxValue"/> rather than
    /// overflowing.
    /// </summary>
    /// <remarks>
    /// The saturation is reachable in one direction only and is harmless there: the callers use the answer
    /// to shorten an idle wait, never to lengthen one, so a saturated value simply leaves the wait at
    /// whatever bound it already had. Overflowing instead would produce a <em>negative</em> span and end a
    /// wait that has barely started, which is the failure this exists to rule out.
    /// </remarks>
    internal static TimeSpan ToTimeSpan(long timestampTicks, long frequency)
    {
        var ticks = timestampTicks * ((double) TimeSpan.TicksPerSecond / frequency);
        return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long) ticks);
    }

    /// <summary>
    /// The provider a constraint should store, or <see langword="null"/> for "read
    /// <see cref="Stopwatch.GetTimestamp"/> directly".
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="provider"/> reports a non-positive <see cref="TimeProvider.TimestampFrequency"/>.
    /// Rejected here rather than divided by later: a zero frequency turns every interval into zero ticks, so
    /// the constraint would arm a deadline equal to its own start and fail the first check of every
    /// execution. A named error where the clock is supplied beats a timeout nobody can explain.
    /// </exception>
    internal static TimeProvider? Resolve(TimeProvider? provider)
    {
        if (provider is null || ReferenceEquals(provider, TimeProvider.System))
        {
            return null;
        }

        if (provider.TimestampFrequency <= 0)
        {
            Throw.ArgumentException(
                "A TimeProvider used by an execution constraint must report a positive TimestampFrequency.",
                nameof(provider));
        }

        return provider;
    }

    /// <summary>
    /// The tick frequency the timestamps of <paramref name="provider"/> are expressed in, for a provider
    /// already put through <see cref="Resolve"/>.
    /// </summary>
    internal static long FrequencyOf(TimeProvider? provider)
        => provider is null ? Stopwatch.Frequency : provider.TimestampFrequency;
}
