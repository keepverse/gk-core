using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Balance.Guards;

/// <summary>Thrown by <see cref="TerminationGuard.Assert"/> — a build pair where neither side can ever
/// die (spec-balance-guard.md §2: "no later layer can repair a pool that refills faster than it
/// drains"). Carries both names and both net-attrition values so a CI failure names the pair, not just
/// the fact of failure.</summary>
public sealed class TerminationViolation : Exception
{
    public string BuildA { get; }
    public string BuildB { get; }
    public double NetAttritionA { get; }
    public double NetAttritionB { get; }

    public TerminationViolation(string buildA, string buildB, double netAttritionA, double netAttritionB)
        : base($"termination invariant violated: '{buildA}' vs '{buildB}' — net attrition A={netAttritionA:F1}, B={netAttritionB:F1} (neither side can ever die)")
    {
        BuildA = buildA;
        BuildB = buildB;
        NetAttritionA = netAttritionA;
        NetAttritionB = netAttritionB;
    }
}

public readonly record struct TerminationVerdict(int PairsChecked, int OffenceLessPairsFiltered, double MinNetAttrition);

/// <summary>
/// class-system-todo.md P5.1 — the HARD half of <c>balance-guard</c> (spec-balance-guard.md, read in
/// full this session). <see cref="Assert"/> throws: no later layer can repair a pool that refills
/// faster than it drains, so a violation here is a build-breaking economy defect, not a design choice
/// a passive or skill could someday answer (unlike <c>DominanceGuard</c>, P5.2, which is SOFT for
/// exactly the opposite reason).
///
/// <para><b>Bridges `aptitude-resolve` (Phase 2) into `deterministic-core` (Phase 4):</b> resolves each
/// <see cref="AptitudeAllocation"/> through the real <see cref="ActorHub"/>/<see cref="AptitudeSubsystem"/>
/// pipeline at the given Θ, then predicts every ordered pair with the real <see cref="Predictor"/> — no
/// combat or resolve math of its own, per spec-deterministic-core.md §2's standard carried up one
/// layer.</para>
///
/// <para><b>`baseDamage` is deliberately 0 here, not <see cref="PowerLadder.Value"/>.</b> This guard
/// compares AVENUES A BUILD PICKED (aptitude spend), not action/weapon base damage (an action-layer
/// concern this program does not own yet) — a nonzero structural baseDamage would give even a
/// zero-power-investment build real damage output through the floor term in
/// <see cref="StrikeMixture.Compute"/>'s own <c>offense = baseDamage + power</c>, which would make the
/// "offence-less pair" exemption below unreachable. With baseDamage=0, a build's entire offense comes
/// from its own allocated <c>combat.power.omni</c>, so "bought no offence at all" is exactly "power
/// resolves to 0" — testable, not assumed.</para>
///
/// <para><b>Shields are not modelled here</b> (<c>ShieldMaxHp</c> is always 0): <see cref="AptitudeAllocation"/>
/// has no notion of a shield grant, and "a shield needs a grant" (P4.3) forbids synthesizing one from a
/// capacity channel alone. A build that allocates points into shield-capacity-feeding aptitudes still
/// resolves fully; it simply carries no shield phase in this guard's own prediction, the same way
/// <c>gk-core/tools/CombatSim</c>'s own duel runner does not tick shield regen.</para>
/// </summary>
public static class TerminationGuard
{
    /// <param name="builds">Every build to cross-check, all ordered pairs.</param>
    /// <param name="theta">The single Θ this guard checks at — the guard is a snapshot at one power
    /// level, not a Θ-sweep (Θ-invariance is `deterministic-core`'s own proven property, P4.6).</param>
    /// <param name="gear">Optional equipped `stat.derived` atoms, one list per build, positionally
    /// aligned with <paramref name="builds"/>. See <see cref="ToActor"/> for why this is additive:
    /// omitting it, or passing an empty list for a build, resolves that build EXACTLY as before.</param>
    public static TerminationVerdict Assert(IReadOnlyList<AptitudeAllocation> builds, long theta,
        IReadOnlyList<IReadOnlyList<BoundDerivedAtom>>? gear = null)
    {
        if (builds is null) throw new ArgumentNullException(nameof(builds));
        if (builds.Count == 0) throw new ArgumentException("must contain at least one build", nameof(builds));
        if (theta <= 0) throw new ArgumentOutOfRangeException(nameof(theta), theta, "must be positive");
        if (gear is not null && gear.Count != builds.Count)
            throw new ArgumentException($"gear must be positionally aligned with builds ({gear.Count} vs {builds.Count})", nameof(gear));

        var actors = new Predictor.Actor[builds.Count];
        for (var i = 0; i < builds.Count; i++)
            actors[i] = ToActor($"build{i}", builds[i], theta, gear?[i]);

        var minNetAttrition = double.PositiveInfinity;
        var pairsChecked = 0;
        var filtered = 0;

        for (var i = 0; i < actors.Length; i++)
        for (var j = 0; j < actors.Length; j++)
        {
            if (i == j) continue;
            var a = actors[i];
            var b = actors[j];

            // "Two builds that bought no offence at all genuinely cannot resolve, and that must stay
            // POSSIBLE — banning it would be a hard restriction PS-8 refuses. A filter on the INPUT,
            // not a special case in the verdict" (spec-balance-guard.md §5).
            if (IsOffenceLess(a) && IsOffenceLess(b)) { filtered++; continue; }

            var prediction = Predictor.Predict(a, b);
            pairsChecked++;
            minNetAttrition = Math.Min(minNetAttrition, Math.Min(prediction.NetAttritionA, prediction.NetAttritionB));

            if (prediction.NetAttritionA <= 0 && prediction.NetAttritionB <= 0)
                throw new TerminationViolation(a.Name, b.Name, prediction.NetAttritionA, prediction.NetAttritionB);
        }

        return new TerminationVerdict(pairsChecked, filtered, minNetAttrition);
    }

    static bool IsOffenceLess(Predictor.Actor actor) =>
        actor.Snapshot.Derived.Get(DerivedStatChannels.CombatPowerOmni) <= 0.0;

    /// <summary>Resolves one <see cref="AptitudeAllocation"/> through the real derived-stat pipeline at
    /// <paramref name="theta"/> — the same <see cref="ActorHub"/>/<see cref="AptitudeSubsystem"/>/
    /// <see cref="FixedPowerIndexProvider"/> composition <c>ActorHubTests.cs</c>'s own established
    /// pattern uses, not a shortcut around it.
    ///
    /// <para><b><paramref name="gear"/> — item-todo.md P1.5, the geared corner run.</b> A build's
    /// equipped `stat.derived` atoms, reaching this pipeline through the SHIPPED
    /// <see cref="AtomDerivedSubsystem"/> at its reserved order-350 slot — the same
    /// <c>boundDerivedAtoms</c> opt-in arm <c>ActorHubBootstrap.CreateDefault</c> has carried since
    /// 2026-08-30. Deliberately NOT a new subsystem: that subsystem's own doc comment names "a second
    /// delivery path for a value the composer already owns" as the thing to avoid, and equipment is
    /// the same `stat.derived` kind it was built for.</para>
    ///
    /// <para><b>Additive by construction, not by arithmetic.</b> Null or empty gear passes
    /// <c>boundDerivedAtoms: null</c>, so <c>CreateDefault</c> registers no subsystem at all — the hub
    /// is not merely composed to the same numbers, it is the same hub, with the same
    /// <c>Subsystems</c> list, that every existing caller has always built. This matters because
    /// <see cref="ActorHub"/> is the shared derived-stat SSOT (`actor-hub-ssot.md`), not
    /// class-system's private property, and its test suite asserts on that list.</para>
    ///
    /// <para><b>Equipment does not feed Θ.</b> <paramref name="theta"/> stays the caller's, so a
    /// geared run compares gear at a FIXED power level rather than letting gear move the ladder — the
    /// same reason this guard is "a snapshot at one power level, not a Θ-sweep".</para></summary>
    internal static Predictor.Actor ToActor(string name, AptitudeAllocation allocation, long theta,
        IReadOnlyList<BoundDerivedAtom>? gear = null)
    {
        var powerIndex = new FixedPowerIndexProvider((int)theta);
        var hub = ActorHubBootstrap.CreateDefault(
            powerIndex: powerIndex,
            aptitudeTuning: AptitudeTuningHub.Tuning,
            aptitudeAllocation: _ => allocation,
            boundDerivedAtoms: gear is null || gear.Count == 0 ? null : _ => gear);
        var ctx = hub.Stats.Contexts.ForPlant(name, new EntityBaseline());
        var derived = hub.ResolveDerived(ctx);
        var snapshot = new CombatActorSnapshot(derived, ActorElementTypes.Neutral);

        var hp = derived.Get(DerivedStatChannels.ResourceMax("hp"));
        return new Predictor.Actor(name, snapshot, hp, BaseDamage: 0.0, ShieldMaxHp: 0);
    }

    sealed class FixedPowerIndexProvider : IPowerIndexProvider
    {
        readonly int _theta;
        public FixedPowerIndexProvider(int theta) => _theta = theta;
        public int ActorIndex(StatContext ctx) => _theta;
        public int ContentIndex(ContentContext ctx) => _theta;
        public PowerAxisReport Explain(StatContext ctx) => new(_theta, Array.Empty<PowerAxisContribution>());
        public int ActorIndexFor(FusionRpg.Core.Saves.SaveId save, FusionRpg.Core.Commanders.EmpireId empire) => _theta;
    }
}
