using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Derived;

public sealed record ActorResolveResult(
    EntityFinal RuntimePrimary,
    ActorDerivedSnapshot Derived,
    EntityFinal AppliedCombat)
{
    public ActorElementTypes ElementTypes { get; init; } = ActorElementTypes.Neutral;
}

/// <summary>Wraps StatSystem primary resolve + derived compose — actor-hub-ssot.md §7.</summary>
public sealed class ActorHub
{
    readonly StatSystem _stats;
    readonly DerivedComposer _composer;
    readonly List<IActorStatSubsystem> _subsystems = new();

    public ActorHub(StatSystem stats, DerivedComposer? composer = null)
    {
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _composer = composer ?? new DerivedComposer();
    }

    public StatSystem Stats => _stats;
    public DerivedComposer Composer => _composer;
    public IReadOnlyList<IActorStatSubsystem> Subsystems => _subsystems;

    public void Register(IActorStatSubsystem subsystem)
    {
        if (subsystem == null) throw new ArgumentNullException(nameof(subsystem));
        _subsystems.RemoveAll(s => string.Equals(s.SubsystemId, subsystem.SubsystemId, StringComparison.OrdinalIgnoreCase));
        _subsystems.Add(subsystem);
        _subsystems.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public ActorResolveResult Resolve(StatContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        var primary = _stats.Resolve(ctx);
        var derived = ResolveDerived(ctx);
        var applied = MergeAppliedCombat(primary, derived);
        return new ActorResolveResult(primary, derived, applied)
        {
            ElementTypes = ctx.ElementTypes
        };
    }

    /// <summary>L2b Apply-scoped derived compose — fresh per call in v1.</summary>
    public ActorDerivedSnapshot ResolveDerived(StatContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        using var _perf = PerfProbe.Measure(PerfSection.HubResolveDerived);
        var mods = new List<DerivedModifier>();
        foreach (var subsystem in _subsystems)
            subsystem.ContributeDerived(ctx, mods);
        var snapshot = _composer.Compose(mods);
        // class-system-todo.md V5/P1.10 — makes Theta=0 (an unhydrated IPowerIndexProvider) detectable
        // from emitted metrics rather than only by reading a snapshot value in a debugger.
        PerfProbe.RecordValue(DerivedStatChannels.ProgressionPower, snapshot.Get(DerivedStatChannels.ProgressionPower, 0));
        // class-system-todo.md V5/P8.2 — the regen half of "stamina binds"; the cost half has no
        // emitter anywhere in this codebase yet (action-costs is a separate, unimplemented program).
        PerfProbe.RecordValue(DerivedStatChannels.ResourceRegen("stamina"), snapshot.Get(DerivedStatChannels.ResourceRegen("stamina"), 0));
        // class-system-todo.md V5/P7.1 — poiseRegenPerRound, so guard-economy's own r = poiseRegen /
        // peerDamage is measurable. No aptitude edge feeds this channel in the shipped config yet
        // (P7.2's own named gap, still open), so this reads 0 on the real tree today — the mechanism
        // is live and correct, proven by Assert_terminationInvariant_actuallyReadsPoiseRegen_endToEnd's
        // own planted, absurd-rate fixture, not by a live nonzero reading that does not exist yet.
        PerfProbe.RecordValue(DerivedStatChannels.ResourceRegen("poise"), snapshot.Get(DerivedStatChannels.ResourceRegen("poise"), 0));
        return snapshot;
    }

    /// <summary>aura-skill T18 (GG-49): the SAME compose <see cref="ResolveDerived"/> runs, with the
    /// per-source modifier list also retained via <see cref="DerivedContributionBag"/> instead of
    /// being discarded the moment `Compose` returns — *"why did my attack drop"* is unanswerable
    /// without this. Not a second resolve: the same `mods` list feeds both the snapshot and the bag,
    /// so the two can never disagree about what contributed.</summary>
    public (ActorDerivedSnapshot Snapshot, DerivedContributionBag Contributions) ResolveDerivedWithContributions(StatContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        var mods = new List<DerivedModifier>();
        foreach (var subsystem in _subsystems)
            subsystem.ContributeDerived(ctx, mods);
        return (_composer.Compose(mods), DerivedContributionBag.From(mods));
    }

    static EntityFinal MergeAppliedCombat(EntityFinal primary, ActorDerivedSnapshot derived)
    {
        var bonusMaxHp = (long)Math.Round(derived.Get(DerivedStatChannels.ProgressionBonusMaxHp, 0));
        // `progression.bonus.atk` retired 2026-09-18 (solid-enforcement `retire-atk` R4): the channel
        // is no longer registered or composed, so `Atk` is the primary value exactly — no bonus term.
        var bonusDefenseFlat = (int)Math.Round(derived.Get(DerivedStatChannels.ProgressionBonusDefense, 0));
        var bonusArm1 = (int)Math.Round(derived.Get(DerivedStatChannels.ProgressionBonusArm1, 0));
        var bonusArm2 = (int)Math.Round(derived.Get(DerivedStatChannels.ProgressionBonusArm2, 0));

        if (bonusMaxHp == 0 && bonusDefenseFlat == 0 && bonusArm1 == 0 && bonusArm2 == 0)
            return primary;

        return new EntityFinal
        {
            Hp = primary.Hp + bonusMaxHp,
            MaxHp = primary.MaxHp + bonusMaxHp,
            Atk = primary.Atk,
            Arm1 = primary.Arm1 + bonusArm1,
            Arm1Max = primary.Arm1Max + bonusArm1,
            Arm2 = primary.Arm2 + bonusArm2,
            Arm2Max = primary.Arm2Max + bonusArm2,
            DefensePercent = primary.DefensePercent,
            DefenseFlat = primary.DefenseFlat + bonusDefenseFlat,
            Contributions = primary.Contributions
        };
    }
}

public static class ActorHubBootstrap
{
    /// <summary><paramref name="powerIndex"/> feeds <c>progression.power</c> (T3.2, defaults to Θ=0).
    /// class-system-todo.md P3.3 (2026-08-27) retired the `level`-gated bonus-flat curve
    /// <see cref="Subsystems.RpgProgressionSubsystem"/> used to own — this method no longer accepts a
    /// `level` delegate; the five `progression.bonus.*` bridge channels are allocation-sourced through
    /// <paramref name="aptitudeTuning"/>/<paramref name="aptitudeAllocation"/> below instead.
    ///
    /// <para><paramref name="aptitudeTuning"/> is opt-in (class-system-todo.md P2.4): omitting it
    /// registers no <see cref="Subsystems.AptitudeSubsystem"/> at all, so every existing caller —
    /// including the hundreds of tests that call this with no tuning hub configured — is unaffected.
    /// Pass it (typically <c>AptitudeTuningHub.Tuning</c>) to wire aptitudes in; <paramref
    /// name="aptitudeAllocation"/> defaults to <see cref="AptitudeAllocation.Empty"/>, matching P2.4's
    /// own proof that the wiring is inert until `point-economy` gives players something to spend.</para>
    /// </summary>
    /// <summary><paramref name="boundDerivedAtoms"/> is the lawn executor for the `stat.derived` atom
    /// kind (decisions.md "Derived-write lawn executor", 2026-08-30). Opt-in for the identical reason
    /// <paramref name="aptitudeTuning"/> is: omitting it registers no
    /// <see cref="Subsystems.AtomDerivedSubsystem"/> at all, so every existing caller — including the
    /// hundreds of tests that call this bare — is unaffected. Pass it to give bound `stat.derived`
    /// atoms a consumer on this host.</summary>
    /// <param name="seedResourceBaseline">
    /// When true, registers <see cref="Subsystems.ResourceBaselineSubsystem"/> so
    /// <c>resource.max.*</c>/<c>resource.regen.*</c> are Hub SSOT (UniqueActor /sheet). Default false.
    /// </param>
    public static ActorHub CreateDefault(StatSystem? stats = null,
        FusionRpg.Core.Power.IPowerIndexProvider? powerIndex = null,
        Aptitudes.AptitudeTuning? aptitudeTuning = null,
        Func<StatContext, Aptitudes.AptitudeAllocation>? aptitudeAllocation = null,
        Func<StatContext, IReadOnlyList<Subsystems.BoundDerivedAtom>>? boundDerivedAtoms = null,
        Func<StatContext, IReadOnlyList<Subsystems.StatusDerivedMod>>? statusDerivedMods = null,
        bool seedResourceBaseline = false,
        Func<StatContext, Subsystems.StarLoyaltyContribution>? starLoyalty = null,
        Func<StatContext, IReadOnlyList<Items.Consumables.DraughtMod>>? draughts = null,
        Func<StatContext, IReadOnlyDictionary<string, int>>? expeditionInjuries = null,
        Func<StatContext, IReadOnlyList<Creatures.Layers.ProjectedLayerRow>>? speciesLayers = null)
    {
        var sys = stats ?? StatSystemBootstrap.CreateDefault();
        var hub = new ActorHub(sys);
        if (seedResourceBaseline)
            hub.Register(new Subsystems.ResourceBaselineSubsystem(powerIndex));
        hub.Register(new Subsystems.RpgProgressionSubsystem(powerIndex));
        // channelmods-hub: same per-context delegate seam as aptitude/bound atoms. Opt-in, so bare
        // CreateDefault callers are unaffected; a contributor returning IsEmpty contributes nothing.
        if (starLoyalty is not null)
            hub.Register(new Subsystems.StarLoyaltySubsystem(starLoyalty));
        if (aptitudeTuning is not null)
        {
            hub.Register(new Subsystems.AptitudeSubsystem(
                aptitudeTuning,
                new FusionRpg.Core.Power.PowerLadder(FusionRpg.Core.Power.PowerTuningHub.Tuning),
                powerIndex,
                aptitudeAllocation));
        }
        if (boundDerivedAtoms is not null)
            hub.Register(new Subsystems.AtomDerivedSubsystem(boundDerivedAtoms));
        // Opt-in like the two above: a caller that passes nothing gets exactly the behaviour it had
        // before this arm existed, so the hundreds of tests calling CreateDefault() bare are unaffected.
        if (statusDerivedMods is not null)
            hub.Register(new Subsystems.StatusDerivedSubsystem(statusDerivedMods));
        // channelmods-hub T2: same opt-in seam for draughts and expedition injuries. Omitting them
        // registers nothing, so every existing caller is unaffected; production wiring lands at fuse.
        if (draughts is not null)
            hub.Register(new Subsystems.DraughtSubsystem(draughts));
        if (expeditionInjuries is not null)
            hub.Register(new Subsystems.ExpeditionInjurySubsystem(expeditionInjuries));
        // species-progression step 6.2: same opt-in seam as aptitudeAllocation/boundDerivedAtoms.
        // Omitting it registers no SpeciesLayerSubsystem at all, so every existing caller — including
        // the hundreds of tests that call this bare — is unaffected.
        if (speciesLayers is not null)
        {
            hub.Register(new Subsystems.SpeciesLayerSubsystem(
                new FusionRpg.Core.Power.PowerLadder(FusionRpg.Core.Power.PowerTuningHub.Tuning),
                powerIndex,
                speciesLayers));
        }
        return hub;
    }
}
