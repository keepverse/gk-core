using System;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// E1b (spec-mechanism-wiring.md §12 q1, closed by the owner 2026-09-05): a status contributes
/// EVERYTHING it writes, `status.resist.*` included, so <see cref="CoreStatus.ResistanceEvaluator"/>
/// reads a defender's CURRENT resist channel — a status that raises `status.resist.dot` makes the
/// NEXT status applied to that host harder to apply. That makes application order significant, and this
/// is the dedicated feedback-path test the closure requires.
///
/// <para>Stand-ins for the spec's own `warding`/`wither` naming: `rally` is the shipped status that
/// already declares <see cref="CoreStatus.StatusPayloadKind.ModifyStat"/> (a resist-granting buff), and
/// `wither` is the shipped `dot`-category status (`StatusCategoryRegistry.cs:8`). No shipped content
/// authors a `stat` overlay today (verified: `grep '"stat"' gk-data/packs/fusion/data/seed/` returns nothing), so this test
/// authors one directly via <see cref="CoreStatus.StatusApplyInput.StatMods"/> — a constraint on FUTURE
/// authoring, not a regression on anything shipped.</para>
/// </summary>
public class StatusDerivedOrderSensitivityTests
{
    const string ResistDot = "status.resist.dot";

    /// <summary>One ActorHub + StatusRuntime pair, wired exactly as production wires them (the injector
    /// swaps only the "reach the live runtime" half): `statusDerivedMods` reads THIS runtime's live
    /// instances via <see cref="CoreStatus.StatusDerivedModReader"/>, and the runtime's own
    /// `ActorDerivedResolve` reads back through the SAME hub — so a status applied a moment ago is
    /// visible to the very next `Apply` call's resist roll, exactly as a live match would see it.</summary>
    static CoreStatus.StatusRuntime BuildWiredRuntime()
    {
        CoreStatus.StatusRuntime? runtimeRef = null;
        var hub = ActorHubBootstrap.CreateDefault(
            statusDerivedMods: ctx => CoreStatus.StatusDerivedModReader.Read(runtimeRef, ctx));

        CoreStatus.ActorDerivedResolve resolve = (ptr, attackerLess) =>
            attackerLess || string.IsNullOrWhiteSpace(ptr)
                ? ActorDerivedSnapshot.AttackerLess()
                : hub.ResolveDerived(new StatContext { EntityKey = ptr! });

        var runtime = new CoreStatus.StatusRuntime(CoreStatus.StatusCatalogBootstrap.CreateDefault(), resolve);
        runtimeRef = runtime;
        return runtime;
    }

    static CoreStatus.StatusApplyInput RallyGrantingResist(string hostPtr, string grantId) => new(
        "rally", hostPtr, AttackerPtr: null, GrantId: grantId,
        BaseMagnitude: 0, BaseDuration: 5000, PeriodMs: 0, DurationMs: 5000,
        AttackerLess: true,
        StatMods: new[] { new CoreStatus.StatusStatMod(ResistDot, "increased", 0.30) });

    static CoreStatus.StatusApplyInput WitherDot(string hostPtr, string grantId) => new(
        "wither", hostPtr, AttackerPtr: null, GrantId: grantId,
        BaseMagnitude: 20, BaseDuration: 5000, PeriodMs: 1000, DurationMs: 5000,
        AttackerLess: true);

    [Fact]
    public void Warding_then_wither_differs_from_wither_then_warding()
    {
        var runtime = BuildWiredRuntime();
        var now = DateTimeOffset.UtcNow;
        var rng = new CoreStatus.FixedStatusRng(0.0);

        // Order A: the resist-granting status ("rally") lands FIRST. By the time "wither" is
        // evaluated, the host's status.resist.dot is already 0.30 -- ResistanceEvaluator.ComputeDelta
        // reads it as part of totalResist, so wither's own Delta is measurably lower.
        runtime.Apply(RallyGrantingResist("Z1", "g-rally"), rng, now);
        var witherAfterWarding = runtime.Apply(WitherDot("Z1", "g-wither"), rng, now);

        // Order B: the SAME two statuses, reversed, on a fresh host. "wither" resolves before "rally"
        // exists, so its Delta is computed against a defender with no resist bump yet -- applying
        // "rally" afterward cannot retroactively change a roll that already happened.
        var witherBeforeWarding = runtime.Apply(WitherDot("Z2", "g-wither2"), rng, now);
        runtime.Apply(RallyGrantingResist("Z2", "g-rally2"), rng, now);

        // The documented difference (spec-mechanism-wiring.md §4.1 "One real behaviour change, named."):
        // warding-first makes wither's Delta lower by exactly the categoryResist term rally contributed
        // (0.30), because AttackerLess:true isolates ComputeDelta's totalResist to categoryResist alone.
        Assert.True(witherAfterWarding.EvalResult.Delta < witherBeforeWarding.EvalResult.Delta,
            "a status raising status.resist.dot must make the NEXT status applied harder, not the one already resolved");
        Assert.Equal(0.30, witherBeforeWarding.EvalResult.Delta - witherAfterWarding.EvalResult.Delta, 3);
    }

    /// <summary>The read that makes order-sensitivity possible terminates rather than recursing:
    /// resolving the defender's snapshot for the SECOND apply is a dictionary lookup over already-stored
    /// instances (`StatusRuntime.ForHost`), never a nested `Apply`/resolve.</summary>
    [Fact]
    public void The_second_apply_reads_a_dictionary_lookup_not_a_nested_resolve()
    {
        var runtime = BuildWiredRuntime();
        var now = DateTimeOffset.UtcNow;
        var rng = new CoreStatus.FixedStatusRng(0.0);

        runtime.Apply(RallyGrantingResist("Z1", "g-rally"), rng, now);
        // Two calls with the host in the identical state must agree -- a re-entrant or accumulating
        // read would drift between them.
        var first = runtime.Apply(WitherDot("Z1", "g-wither-a"), rng, now);
        var second = runtime.Apply(WitherDot("Z1", "g-wither-b"), rng, now);
        Assert.Equal(first.EvalResult.Delta, second.EvalResult.Delta);
    }
}
