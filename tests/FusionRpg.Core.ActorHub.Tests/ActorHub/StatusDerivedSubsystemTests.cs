using FusionRpg.Contracts;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>
/// `mechanism-wiring` G1. A status may author `stat.&lt;channel&gt;.&lt;op&gt;` against a DERIVED channel —
/// `StatusStatPayload.IsKnownChannel` accepts `combat.*` — but every one of those writes went into the
/// PRIMARY session bag, which no registered subsystem reads. The status resolved to nothing.
///
/// <para>This is the node class §3.5 measured as the only one that rescues a focused build, so the
/// falsifier arm matters as much as the positive one: <see cref="Three_subsystems_compose_nothing"/>
/// is the state of the world before this subsystem existed, and it is asserted rather than described
/// so the fix cannot quietly stop being load-bearing.</para>
/// </summary>
public class StatusDerivedSubsystemTests
{
    const string Defense = "combat.defense.omni";
    const string ResistDot = "status.resist.dot";

    static StatContext Ctx(FusionRpg.Core.Stats.Derived.ActorHub hub) =>
        hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

    static FusionRpg.Core.Stats.Derived.ActorHub HubWith(params StatusDerivedMod[] mods) =>
        ActorHubBootstrap.CreateDefault(statusDerivedMods: _ => mods);

    [Fact]
    public void A_status_derived_write_reaches_the_composed_value()
    {
        var hub = HubWith(new StatusDerivedMod(Defense, DerivedModifierOp.Flat, 25, "status:warding#1"));
        Assert.Equal(25, hub.ResolveDerived(Ctx(hub)).Get(Defense));
    }

    /// <summary>The falsifier. Without the fourth subsystem the same mod composes nothing — this is
    /// what shipped before G1, and it is why a mechanism node could bind and change no number.</summary>
    [Fact]
    public void Three_subsystems_compose_nothing()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        Assert.Equal(0, hub.ResolveDerived(Ctx(hub)).Get(Defense));
    }

    [Fact]
    public void Two_stacks_withdraw_independently()
    {
        // SourceId names the INSTANCE, not the status id, so one expiring cannot withdraw the other's
        // contribution. Coexist stacking (`ember`) is the shipped case this protects.
        var both = HubWith(
            new StatusDerivedMod(Defense, DerivedModifierOp.Flat, 10, "status:ember#1"),
            new StatusDerivedMod(Defense, DerivedModifierOp.Flat, 10, "status:ember#2"));
        Assert.Equal(20, both.ResolveDerived(Ctx(both)).Get(Defense));

        var one = HubWith(new StatusDerivedMod(Defense, DerivedModifierOp.Flat, 10, "status:ember#2"));
        Assert.Equal(10, one.ResolveDerived(Ctx(one)).Get(Defense));
    }

    [Fact]
    public void An_empty_delegate_contributes_nothing_and_registers_nothing_extra()
    {
        var hub = ActorHubBootstrap.CreateDefault(statusDerivedMods: _ => System.Array.Empty<StatusDerivedMod>());
        Assert.Equal(0, hub.ResolveDerived(Ctx(hub)).Get(Defense));
        // Opt-in: passing nothing leaves the hub exactly as it was before this arm existed.
        Assert.DoesNotContain(ActorHubBootstrap.CreateDefault().Subsystems, s => s.SubsystemId == "l2b.derived");
    }

    [Fact]
    public void Empty_SourceId_is_skipped_never_minted_as_blank_contribution()
    {
        var hub = HubWith(
            new StatusDerivedMod(Defense, DerivedModifierOp.Flat, 40, ""),
            new StatusDerivedMod(Defense, DerivedModifierOp.Flat, 60, "   "),
            new StatusDerivedMod(Defense, DerivedModifierOp.Flat, 70, "status:kept#1"));

        var (snap, contributions) = hub.ResolveDerivedWithContributions(Ctx(hub));
        Assert.Equal(70, snap.Get(Defense));
        var one = Assert.Single(contributions.ContributionsFor(Defense));
        Assert.Equal("status:kept#1", one.SourceId);
    }

    /// <summary>
    /// `more` is refused, never coerced. `StatusStatPayload.Ops` allows it because it is meaningful on
    /// a PRIMARY channel; there is no `More` on the derived side (definitions.md §14). Coercing it to
    /// `Flat` is how a wrong number ships looking correct.
    /// </summary>
    [Theory]
    [InlineData("flat", true)]
    [InlineData("increased", true)]
    [InlineData("more", false)]
    [InlineData("replace", false)]
    [InlineData(null, false)]
    public void More_is_refused_on_the_derived_side(string? op, bool parses) =>
        Assert.Equal(parses, StatusDerivedSubsystem.TryParseOp(op, out _));

    /// <summary>
    /// E1b — the resist feedback path, closed by the owner 2026-09-05: a status contributes everything
    /// it writes, `status.resist.*` included. `ResistanceEvaluator` already reads the defender's
    /// snapshot, so a host carrying a resist-granting status is measurably harder to afflict next.
    ///
    /// <para>This changes no shipped content — no status in `gk-data/packs/fusion/data/seed/` authors a stat overlay — so
    /// the order-sensitivity it introduces is a constraint on future authoring, not a regression.</para>
    /// </summary>
    [Fact]
    public void A_resist_granting_status_raises_the_hosts_resist_channel()
    {
        var warded = HubWith(new StatusDerivedMod(ResistDot, DerivedModifierOp.Increased, 0.30, "status:warding#1"));
        var bare = ActorHubBootstrap.CreateDefault();

        // `status.resist.dot` composes SumIncreased under DerivedStatPolicy.CategoryResistCap,
        // so the op is Increased and the value is a fraction -- a Flat mod on this channel is
        // ignored by its compose kind, which is itself worth pinning.
        Assert.Equal(0.30, warded.ResolveDerived(Ctx(warded)).Get(ResistDot), 3);
        Assert.Equal(0, bare.ResolveDerived(Ctx(bare)).Get(ResistDot));
        Assert.True(warded.ResolveDerived(Ctx(warded)).Get(ResistDot)
                    <= FusionRpg.Core.Stats.Derived.DerivedStatPolicy.CategoryResistCap,
            "a status may not push a category resist past its shipped cap");
    }

    /// <summary>The feedback terminates: resolving contributions is a delegate read and a fold, never a
    /// nested resolve. Asserted by resolving twice and getting the same answer — a re-entrant path
    /// would either differ or not return.</summary>
    [Fact]
    public void The_resist_feedback_terminates_and_is_idempotent()
    {
        var hub = HubWith(new StatusDerivedMod(ResistDot, DerivedModifierOp.Increased, 0.30, "status:warding#1"));
        var ctx = Ctx(hub);
        Assert.Equal(hub.ResolveDerived(ctx).Get(ResistDot), hub.ResolveDerived(ctx).Get(ResistDot));
    }
}
