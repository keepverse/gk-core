using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Balance.Guards;

/// <summary>One ordered matchup's predicted win share — win rate only, never fight length, total
/// damage or kill time (spec-balance-guard.md §7 "Never... measure duration").</summary>
public readonly record struct DominanceArrow(string AttackerName, string DefenderName, double WinShareAttacker);

public readonly record struct DominanceReport(
    bool IsDominant,
    IReadOnlyList<string> DominantBuildNames,
    IReadOnlyList<DominanceArrow> Matrix,
    CoverageReport Coverage);

/// <summary>
/// class-system-todo.md P5.2 — the SOFT half of <c>balance-guard</c> (spec-balance-guard.md, read in
/// full alongside P5.1). <see cref="Measure"/> RETURNS, never throws: a passive scaling damage with
/// damage taken, a reflect build, a counter-action or an anti-turtle status can all still fill a
/// dominant corner (the action/passive/skill layer, unbuilt) — unlike the termination invariant, this
/// is not an economy identity no later layer can repair, so it is reported, not blocking.
///
/// <para><b>No clock, ever</b> (spec-balance-guard.md §7): every <see cref="Predictor.Predict"/> call
/// here uses the default no-<c>roundLimit</c> overload. Ideal §0.1.2's own retraction is the reason —
/// a clock manufactures a pass by penalising survival and cc builds for playing correctly, which is
/// exactly the failure mode this guard exists to avoid reproducing.</para>
/// </summary>
public static class DominanceGuard
{
    /// <summary>"Beats" — the conventional win-rate reading, matching spec-balance-guard.md's own
    /// "no row... beats every other, on win rate": a strict majority, not a coin flip or worse. Not a
    /// balance dial (a fixed mathematical definition of "wins the matchup" — moving it would redefine
    /// what "dominant" means, not adjust how the game feels), and named to say so and stay out of the
    /// magic-numbers audit's balance-vocabulary match on "threshold".</summary>
    public const double MajorityWinShare = 0.5;

    /// <param name="gear">Optional equipped `stat.derived` atoms, one list per build, positionally
    /// aligned with <paramref name="builds"/> — item-todo.md P1.5's geared corner run. Additive:
    /// omitting it resolves every corner exactly as before, because
    /// <see cref="TerminationGuard.ToActor"/> then registers no equipment subsystem at all (see its
    /// own doc for why that is by construction rather than by arithmetic).</param>
    public static DominanceReport Measure(IReadOnlyList<AptitudeAllocation> builds, long theta,
        IReadOnlyList<IReadOnlyList<BoundDerivedAtom>>? gear = null)
    {
        if (builds is null) throw new ArgumentNullException(nameof(builds));
        if (builds.Count == 0) throw new ArgumentException("must contain at least one build", nameof(builds));
        if (theta <= 0) throw new ArgumentOutOfRangeException(nameof(theta), theta, "must be positive");
        if (gear is not null && gear.Count != builds.Count)
            throw new ArgumentException($"gear must be positionally aligned with builds ({gear.Count} vs {builds.Count})", nameof(gear));

        var actors = new Predictor.Actor[builds.Count];
        for (var i = 0; i < builds.Count; i++)
            actors[i] = TerminationGuard.ToActor($"corner{i}", builds[i], theta, gear?[i]);

        var matrix = new List<DominanceArrow>();
        var winShareAgainst = new Dictionary<(int, int), double>();
        for (var i = 0; i < actors.Length; i++)
        for (var j = 0; j < actors.Length; j++)
        {
            if (i == j) continue;
            // No roundLimit argument -- the no-clock overload, on purpose (see type doc).
            var prediction = Predictor.Predict(actors[i], actors[j]);
            winShareAgainst[(i, j)] = prediction.WinShareA;
            matrix.Add(new DominanceArrow(actors[i].Name, actors[j].Name, prediction.WinShareA));
        }

        var dominant = new List<string>();
        for (var i = 0; i < actors.Length; i++)
        {
            var beatsEveryOther = true;
            for (var j = 0; j < actors.Length; j++)
            {
                if (i == j) continue;
                if (winShareAgainst[(i, j)] <= MajorityWinShare) { beatsEveryOther = false; break; }
            }
            if (beatsEveryOther && actors.Length > 1) dominant.Add(actors[i].Name);
        }

        return new DominanceReport(dominant.Count > 0, dominant, matrix, StandardCoverage());
    }

    /// <summary>The coverage this guard always reports, given Phase 4's own shipped scope — see
    /// <see cref="CoverageReport"/>'s own doc for the citations behind each value. The reserved-family
    /// list matches <c>docs/research/class-system/_baseline-dominance.json</c>'s own <c>coverage</c>
    /// block exactly (read this session, not re-derived) — the POC's own prior, already-validated
    /// enumeration of every action/resource-economy channel this guard's baseDamage=0 / no-actions
    /// scope cannot exercise, rather than a shorter list guessed independently. <b>W14
    /// (battle-derived-wire T18):</b> <c>loadout.slots</c> was appended 2026-09-23 — it is registered
    /// and read, but fed by no production site and untouchable by this no-actions predictor, so it
    /// belongs in the coverage report. The tracked
    /// <c>docs/research/class-system/_baseline-dominance.json</c> <c>coverage.reservedFamilies</c> has
    /// not been regenerated with it; <c>scripts/regen-class-system-baselines.ps1</c> owns that file.</summary>
    public static CoverageReport StandardCoverage() => new(
        ElementAxis: "NEUTRALISED -- StrikeMixture is omni-only (P4.1); every corner here is a 1-D slice of a live element axis",
        ReservedFamilies: BuildReservedFamilies());

    /// <summary>
    /// DERIVED from <see cref="DerivedStatChannels.ResourceIds"/>, never hand-listed — Phase 0,
    /// 2026-09-02. The previous version enumerated eleven resource channels by hand and so silently
    /// omitted three (`resource.efficiency.hp/spirit`, and every `poise` channel once `poise` became
    /// the sixth resource). That is not cosmetic: a channel missing from this list is treated as
    /// EXERCISED by the guard, so the six-resource coverage pass moved six aptitudes to 0/11 wins
    /// purely because their new points landed in channels the predictor cannot read and this list did
    /// not excuse. Deriving it means a seventh resource is covered by construction.
    ///
    /// <para><b>The one exception, and why it is not drift:</b> <c>resource.max.hp</c> and
    /// <c>resource.regen.hp</c> ARE read by the prediction path (<c>Predictor</c> reads hp regen,
    /// <c>TerminationGuard</c> reads hp), so they are genuinely exercised and must NOT be reserved.
    /// Every other resource channel has no prediction reader: `efficiency` has none at all until the
    /// action-cost layer ships (`spec-action-costs.md` §1), and `max`/`regen` for the other five are
    /// pools no closed-form duel spends.</para>
    /// </summary>
    static IReadOnlyList<string> BuildReservedFamilies()
    {
        var reserved = new List<string> { "move.range" };

        // W14 (battle-derived-wire T18): registered, read by the three loadout validators, and fed by
        // NO production site — the equip/atom layer that would grant an extend-slot item is unbuilt,
        // so every actor composes it at 0 and no action economy can exercise it. Unlike the families
        // below it carries no aptitude tuning edges today; the class doc's "confirmed present in
        // aptitudes edges" provenance sentence is scoped to those, not to this entry.
        reserved.Add(DerivedStatChannels.LoadoutSlots);

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            reserved.Add(DerivedStatChannels.ResourceEfficiency(id));   // no reader for ANY id
            if (id == "hp") continue;                                   // max/regen/gen for hp ARE read
            reserved.Add(DerivedStatChannels.ResourceMax(id));
            reserved.Add(DerivedStatChannels.ResourceRegen(id));
            // resource.restore.hp is OverlayCombatMath's heal term and is exercised; the other five have no
            // consumer until the action layer grants a non-hp resource (0.8, 2026-09-02).
            reserved.Add(DerivedStatChannels.ResourceRestore(id));
        }

        foreach (var category in DerivedStatChannels.ActionCategories)
        {
            reserved.Add(DerivedStatChannels.SkillCooldown(category));
            reserved.Add(DerivedStatChannels.SkillEffectiveness(category));
        }

        reserved.Sort(StringComparer.Ordinal);
        return reserved;
    }
}
