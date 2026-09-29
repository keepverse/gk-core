using FusionRpg.Core.Balance.Guards;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §3: "The harness emits its own coverage block, shaped like
/// <c>DominanceGuard.StandardCoverage()</c>, naming the axes it did not exercise. A measurement without
/// its coverage is a claim." §10 adds two more things this block must name: the six mechanism-node
/// classes blocked on <c>mechanism-wiring</c>, and the A10a/A10b split (§10.1) so an A10a pass is never
/// reported as an A10b pass.
///
/// <para><b>Never a private re-derivation.</b> <see cref="ElementAxis"/> and
/// <see cref="ReservedFamilies"/> come straight from <c>DominanceGuard.StandardCoverage()</c> -- the
/// SAME shipped, already-validated enumeration <c>gk-core/tools/HybridViability</c> and every other closed-form
/// tool in the program reports, never a second hand-written list that could drift from it.</para>
/// </summary>
public sealed record HarnessCoverage(
    string ElementAxis,
    IReadOnlyList<string> ReservedFamilies,
    IReadOnlyList<string> BlockedMechanismClasses,
    string A10Split,
    string StalemateHorizon,
    string WaveOpponentNote);

public static class Coverage
{
    /// <summary>
    /// spec §10 "Blocked on mechanism-wiring, and named by their inert line" -- the exact six rows of
    /// that table, reproduced here as static coverage content (this module does not compute these; it
    /// REPORTS them, so a null measurement is never mistaken for one). Verified against code this
    /// session per the spec's own citations. One row (stat.derived in Sim) was amended 2026-09-06 after
    /// mechanism-wiring E5 landed and partially unblocked it -- kept at six entries, reworded to name
    /// what is still genuinely blocked (Replace/Flag composition), not deleted, since spec-squad-
    /// harness.md's own table was updated the same way.
    /// </summary>
    public static readonly IReadOnlyList<string> SixBlockedMechanismClasses = new[]
    {
        "Anything triggered by OnDamageTaken, OnSpawn or OnDeath -- zero hits for any of the three across " +
        "src/FusionRpg.Core/Battle/ and src/FusionRpg.Core/Actions/; OnDamageDealt is the only trigger Battle raises",

        "stat.derived re-evaluating per hit (M1 conditional scaling) -- AtomKindRegistry.cs:535, AtomTriggers.None",

        "stat.derived using Replace/Flag ops in Sim -- amended 2026-09-06 (mechanism-wiring E5): no longer a " +
        "blanket block, AtomKindRegistry.cs:561's SIM slot moved None -> RuntimeState.Partial once " +
        "ActorDerivedLookup's contribution fold gave it a real consumer. Flat/Increased now compose correctly; " +
        "Replace/Flag still silently miscompose as if Flat (BoundDerivedAtom carries no Priority field) -- " +
        "proven by EffectOfflineKitTests.The_four_derived_ops_decide_Full_versus_Partial. A node using " +
        "Flat/Increased in Sim is now scorable; one using Replace/Flag still reads a silently wrong number",

        "A status's derived-channel write composing (§4a layer parity) -- ActorHubBootstrap.CreateDefault registers " +
        "three subsystems and the third is conditional on boundDerivedAtoms",

        "Battle's derived recompose beyond construction -- BattleRunState.RecomposeDerived has one caller, the " +
        "construction-time foreach (var aura in setup.ActiveAuras) loop",

        "M7 Retaliation / reflect -- reflect lives in CombatDamageDispatcher.TryReflect, reached only from " +
        "DispatchInstant on the LAWN; Battle applies HP through DamageApplyPipeline.Apply instead and has zero " +
        "hits for 'reflect' in src/FusionRpg.Core/Battle/. Not measurable at squad scope today, despite doc 05 " +
        "ranking it #2 'ship content today' -- true on the lawn, not in Battle.",
    };

    /// <summary>spec §10.1: "The coverage block names the split, so an A10a pass is never reported as an
    /// A10b pass." A10a (the static Erosion, this module's own scope, F3) runs through the shipped
    /// resolver via <c>BattleActorSetup.ChannelMods</c> with no new wiring. A10b (Erosion as designed --
    /// a stacking OnDamageDealt status with its own StatMods) has no delivery vehicle in Battle today:
    /// BattleStatusSpec carries no StatMods at all, and BattleDerivedModifierLedger.Add has exactly one
    /// caller, the construction-time aura loop. This module never claims to measure A10b.</summary>
    public const string A10SplitNote =
        "A10a (static Erosion via BattleActorSetup.ChannelMods, no new wiring -- this module's scope) is " +
        "measurable today. A10b (Erosion as designed: a stacking OnDamageDealt status with its own StatMods) " +
        "has no delivery vehicle in Battle -- BattleStatusSpec carries no StatMods, and " +
        "BattleDerivedModifierLedger.Add has exactly one caller (the construction-time ActiveAuras loop). " +
        "An A10a pass is never reported as an A10b pass.";

    /// <summary>spec §8/decisions.md:103: BattleEngine has a horizon the closed form does not, so a
    /// trial harness cannot inherit the no-clock property. Named here so a reader of the coverage block
    /// sees the caveat beside every trial-based column, not just in the module's own doc comments.</summary>
    public const string StalemateHorizonNote =
        "BattleEngine has a horizon (Resolve's maxBattleTick) the closed form does not. Stalemates are " +
        "excluded from the win-share denominator and reported as their own count (decisions.md:103: " +
        "win rate is the metric, never fight length -- and never under a clock). A cell whose stalemate " +
        "rate exceeds 20% is flagged lowConfidence and refused, not scored, because past that the horizon " +
        "-- not the build -- is deciding.";

    /// <summary>spec §2: squad-vs-wave answers a different question than squad-vs-squad (D46: mirror
    /// squads are the primary verdict) and is reported separately, never mixed into the transfer table.
    /// Wave content is authored (real species, real elements) -- the §3 element-neutrality claim covers
    /// only this harness's OWN generated rosters, never wave opponents.</summary>
    public const string WaveOpponentNoteText =
        "squad-vs-squad (mirror squads) is the primary verdict (D46). --opponent wave is reported " +
        "separately and never enters _scope-transfer.json or _squad-scope.json: WaveCatalog content is " +
        "authored with real species and real elements, so it is exempt from (and would invalidate) the " +
        "element-neutrality claim the transfer comparison depends on. WaveCatalog also pins level to " +
        "content index (1/3/6/10), so a wave run only answers 'how does this squad do against THAT " +
        "authored content at THAT Θ', not the scope-transfer question.";

    public static HarnessCoverage Standard()
    {
        var baseline = DominanceGuard.StandardCoverage();
        return new HarnessCoverage(
            ElementAxis: baseline.ElementAxis,
            ReservedFamilies: baseline.ReservedFamilies,
            BlockedMechanismClasses: SixBlockedMechanismClasses,
            A10Split: A10SplitNote,
            StalemateHorizon: StalemateHorizonNote,
            WaveOpponentNote: WaveOpponentNoteText);
    }
}
