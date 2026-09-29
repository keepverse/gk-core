using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Battle;

/// <summary>
/// battle-hub-fuse T5 — the battle-side Hub composer replacing
/// <see cref="BattleStatComposer.Compose"/>. Builds a Hub from the setup's own fields (level,
/// defense, elements, traits, tempo) plus the builders' <see cref="BattleHubInputs"/>, and returns
/// the composed <see cref="ActorDerivedSnapshot"/> the engine resolves with.
///
/// <para><b>Routes through <c>ActorHubBootstrap.CreateDefault</c> since solid-remediation T3.2 (D5).</b>
/// It previously hand-rolled its own <c>new ActorHub(...)</c> and registered a set that differed from
/// every other mode's, so the same specimen composed from a different subsystem set depending on where
/// it was standing — rule 4 of the battle-engine responsibility register.</para>
///
/// <para><b>What the old comment here argued, and why it did not survive T3.1.</b> It said the bypass
/// was deliberate because "battle composes no progression or status channels, so those subsystems stay
/// unregistered and parity with the old composer is channel-exact". The load-bearing clause is the
/// last one: that is a <b>byte-identity argument</b> made during the 2026-09-13 fusion to hold
/// <c>BattleGoldenTests</c> still while <c>BattleStatComposer</c> was being deleted. `decisions.md`
/// ratifies that battle composes <i>through ActorHub</i> — the defect it declares retired is DUAL
/// COMPOSE — and says nothing about which subsystems battle registers. So the path was ratified and
/// the set was not.</para>
///
/// <para>Battle's own four subsystems are registered on top, which is contribution, not a second fold:
/// <see cref="ActorHub.Register"/> dedupes by <c>SubsystemId</c> and sorts by <c>Order</c>, so the
/// composed result depends only on WHICH subsystems are present, never on the order they were added.</para>
///
/// <para>⚠️ <c>statusDerivedMods</c> is deliberately NOT passed. Battle already owns that
/// responsibility through <see cref="BattleStatModifierLedger"/>, fed from
/// <c>StatusStatPayload.ToModifiers</c> in <c>BattleRunState</c>; registering
/// <c>StatusDerivedSubsystem</c> as well would apply every status stat mod TWICE. That two-mechanism
/// split (battle's ledger vs the lawn's subsystem) is a real rule-2 divergence and is recorded as a
/// finding — it is not closed by silently adding a second consumer here.</para>
/// </summary>
public static class BattleHubCompose
{
    /// <summary>Where trait channel mods are read from when a caller does not pass one (E12, re-homed
    /// off the deleted <c>BattleStatComposer.Traits</c> in T6). Defaults to the migrated set, which
    /// supplies `critical-hunter` and falls through to `TraitBattleCatalog` for the other thirteen.</summary>
    public static TraitAtomSource Traits { get; private set; } = TraitAtomSource.Shipped();

    public static void UseTraits(TraitAtomSource source) =>
        Traits = source ?? throw new ArgumentNullException(nameof(source));

    public static void ResetTraits() => Traits = TraitAtomSource.Shipped();

    /// <summary>W17 (battle-derived-wire T15, `battle-wire-remainder` BWR1.6): the SAME compose, plus
    /// the ActorHub <c>AppliedCombat</c> merge — <c>hub.Resolve(ctx)</c>, not <c>hub.ResolveDerived(ctx)</c>.
    /// Battle consumes the merge by the DELTA between the merged primary and the pre-merge primary
    /// (<c>AppliedCombat − RuntimePrimary</c>), which IS the merge's own result, never a second fold of
    /// <see cref="DerivedStatChannels.ProgressionBonusMaxHp"/>/<see cref="DerivedStatChannels.ProgressionBonusDefense"/>.
    /// See <c>ActorState</c>'s constructor for where each term lands.</summary>
    public static ActorResolveResult Resolve(BattleActorSetup setup, TraitAtomSource? traits = null)
    {
        var (hub, ctx) = BuildHub(setup, traits);
        var resolved = hub.Resolve(ctx);
        OverlayChannelMods(setup, hub, resolved.Derived);
        return resolved;
    }

    static (ActorHub Hub, StatContext Ctx) BuildHub(BattleActorSetup setup, TraitAtomSource? traits)
    {
        if (setup is null) throw new ArgumentNullException(nameof(setup));
        var inputs = setup.HubInputs;
        var theta = setup.ThetaActor ?? setup.Level;

        var powerIndex = new FixedPowerIndexProvider(theta);

        // The one registration path (T3.2). Every opt-in below is passed exactly when the hand-rolled
        // version registered its subsystem, so the ONLY set difference is what CreateDefault adds
        // unconditionally: RpgProgressionSubsystem, which writes `progression.power` and
        // `progression.realm` and no combat channel at all.
        var hub = ActorHubBootstrap.CreateDefault(
            StatSystemBootstrap.CreateDefault(),
            powerIndex: powerIndex,
            // Gated on the allocation, not merely on the tuning being available: CreateDefault would
            // otherwise register AptitudeSubsystem for every battle actor with an empty allocation,
            // which is a registration the hand-rolled version did not make.
            aptitudeTuning: inputs?.Aptitude is null ? null : AptitudeTuningHub.Tuning,
            aptitudeAllocation: inputs?.Aptitude is { } allocation ? _ => allocation : null,
            boundDerivedAtoms: inputs?.BoundAtoms is { } bound ? _ => bound : null,
            // See the class doc: battle's BattleStatModifierLedger already owns this responsibility.
            statusDerivedMods: null,
            seedResourceBaseline: true,
            starLoyalty: inputs?.StarLoyalty is { } starLoyalty ? _ => starLoyalty : null,
            draughts: inputs?.Draughts is { } draughts ? _ => draughts : null,
            expeditionInjuries: inputs?.Injuries is { } injuries ? _ => injuries : null,
            // species-progression `species-layer-delivery` step 6.2 (SP6.7): same opt-in shape as
            // every other Hub contribution above -- registers SpeciesLayerSubsystem only when the
            // builder actually supplied rows, Theta from the SAME FixedPowerIndexProvider(theta)
            // every other subsystem here already reads.
            speciesLayers: inputs?.SpeciesLayers is { } speciesLayers ? _ => speciesLayers : null);

        // Battle's own four, contributed on top of the shared set.
        hub.Register(new BattleBaselineSubsystem(_ => (theta, setup.Defense)));
        hub.Register(new BattleAffinitySubsystem(_ =>
            (setup.Atk, setup.Defense, setup.ElementPrimary, setup.ElementSecondary)));
        hub.Register(new BattleTraitSubsystem(traits ?? Traits, _ => setup.TraitIds));
        hub.Register(new BattleTempoSubsystem(_ => setup.AttackIntervalMs));

        var ctx = new StatContextFactory().ForBattle(
            setup.Key,
            new EntityBaseline { MaxHp = setup.MaxHp, Atk = setup.Atk },
            side: string.Equals(setup.Side, "wave", StringComparison.Ordinal) ? StatSide.Zombie : StatSide.Plant,
            typeId: setup.TypeId);
        return (hub, ctx);
    }

    /// <summary>DEBT — battle-hub-fuse: the caller-overlay fold, kept byte-identical until T6 deletes
    /// the ChannelMods producers with the composer. Unknown-channel refusal matches the old composer.</summary>
    static void OverlayChannelMods(BattleActorSetup setup, ActorHub hub, ActorDerivedSnapshot snapshot)
    {
        foreach (var mod in setup.ChannelMods)
        {
            if (!hub.Composer.Registry.TryResolveChannel(mod.ChannelId, out _))
                throw new ArgumentException($"Unknown combat channel id '{mod.ChannelId}'.");
            snapshot.Set(mod.ChannelId, snapshot.Get(mod.ChannelId, 0) + mod.Amount);
        }
    }

    /// <summary>The derived half only — every existing caller's exact behaviour: `ResolveDerived`, with
    /// NO primary `StatSystem` resolve, so a caller that has not configured `StatsTuningHub` (e.g.
    /// `gk-core/tools/ProveAptitude`) keeps working. Battle's `ActorState` uses <see cref="Resolve"/> for the
    /// `AppliedCombat` merge.</summary>
    /// <summary>Test-only seam: the registered subsystem SET battle composes with. W4's decision (battle's
    /// own derived ledger owns status→`combat.*`, so `StatusDerivedSubsystem` must NOT be registered) and
    /// W5's registration are both facts about this set, and `BattleHubCompose` otherwise exposes only the
    /// composed snapshot — so a test cannot assert either without it.</summary>
    internal static ActorHub BuildHubForTest(BattleActorSetup setup) => BuildHub(setup, traits: null).Hub;

    public static ActorDerivedSnapshot Compose(BattleActorSetup setup, TraitAtomSource? traits = null)
    {
        var (hub, ctx) = BuildHub(setup, traits);
        var snapshot = hub.ResolveDerived(ctx);
        OverlayChannelMods(setup, hub, snapshot);
        return snapshot;
    }
}
