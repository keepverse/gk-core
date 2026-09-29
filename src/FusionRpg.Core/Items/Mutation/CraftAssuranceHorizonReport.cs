using System.Text;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// species-gear-chain T46 (`craft-assurance` h) — R-G1's own two failure modes, from Lost Ark's
/// honing (spec-craft-assurance.md § "The two failure modes"): <i>"a guarantee reachable only in
/// theory"</i>, and <i>"a guarantee cheap enough to dominate"</i>. A REPORT, computed from shipped
/// tuning and the shipped loot corpus only — it <b>asserts nothing</b>
/// (<c>validation-ssot.md</c>: the right ratio is a balance judgement, and pinning it would be
/// pinning a reading, not a contract). Same "computed, never authored" posture
/// <see cref="CraftingHorizonReport"/> already established for module 14's own horizon table.
/// </summary>
/// <param name="TargetLevel">The <c>+n</c> this row is gambling for.</param>
/// <param name="SuccessMilli">The unassisted roll chance at this level, per-mille.</param>
/// <param name="ExpectedAttemptsMilli">
/// 1000×1000/SuccessMilli — the mean of a geometric distribution with success probability
/// <c>SuccessMilli/1000</c>, in per-mille attempts. <b>A lower bound, not the true expectation:</b> it
/// ignores a failed attempt's own downgrade risk knocking the target back a level, which can only
/// raise the true expected attempt count. Stated so the number is not over-trusted, the same
/// discipline <see cref="CraftingHorizonReport"/>'s own doc comment already applies to its "N ≈ 0.19"
/// conclusion.
/// </param>
/// <param name="AssureChargesForCertainty">
/// How many <c>assurance.assure</c> charges raise <see cref="SuccessMilli"/> to 1000‰ (certainty) —
/// <c>ceil((1000 − SuccessMilli) / assureBonusMilli)</c>. Zero when the roll is already certain.
/// </param>
/// <param name="ExpectedBossKillsForCertaintyMilli">
/// <see cref="AssureChargesForCertainty"/> divided by the REAL shipped per-kill yield of
/// <c>assurance.assure</c> at the source the caller named, in per-mille kills. <c>long.MaxValue</c>
/// when the source's yield is zero — R-G1's first failure mode ("reachable only in theory"), printed
/// literally rather than silently produced as a divide-by-zero or a false finite number.
/// </param>
/// <param name="ExpectedAttemptsWithDowngradeMilli">
/// Spec § Design 7's <b>downgrade risk</b> column, which <see cref="ExpectedAttemptsMilli"/>
/// deliberately excludes: the expected attempts to ADVANCE from this level, with a failed attempt at
/// or above <c>enhancement.downgradeFromLevel</c> inside a <c>canDowngrade</c> band falling one level
/// and having to be re-earned (<c>EnhancePolicy.Resolve</c>'s own rule — the same condition, read from
/// the same tuning). Equal to <see cref="ExpectedAttemptsMilli"/> wherever a failure cannot downgrade,
/// and strictly greater wherever it can; an unwarded gambler is assumed (a loaded ward removes the
/// downgrade, which is the mechanic's whole point).
/// </param>
/// <param name="ExpectedWearPerMilleOfMaxGambling">
/// Spec § Design 7's <b>craft wear</b> column, in per-mille of max durability: the gamble route's
/// expected attempts × the caller's <c>craftWearPerAttemptMilli</c>. Null when the caller passed no
/// rate, so every pre-existing caller's output is unchanged. ⚠ The rate only applies once an item's
/// crafted potential is exhausted (<c>CraftRiskPolicy.CanDecay</c>) and is suppressed by
/// <c>assurance.protect</c> — this column is the price per post-exhaustion attempt, not a claim that
/// every attempt wears.
/// </param>
/// <param name="ExpectedWearPerMilleOfMaxCertainty">
/// The same wear unit for the certainty route: ONE attempt at the rate (the roll always succeeds, but
/// the craft still happens). Printed beside the gamble column so the wear knob's price is visible on
/// both routes; scales linearly with the rate.
/// </param>
public readonly record struct GambleVsAssuranceRow(
    int TargetLevel, int SuccessMilli, long ExpectedAttemptsMilli,
    long AssureChargesForCertainty, long ExpectedBossKillsForCertaintyMilli,
    long ExpectedAttemptsWithDowngradeMilli = 0,
    long? ExpectedWearPerMilleOfMaxGambling = null,
    long? ExpectedWearPerMilleOfMaxCertainty = null);

/// <summary>One (<see cref="LootSourceRow.Key"/>, assurance id) pair's expected per-draw yield, in
/// per-mille — spec §6's own "visibility, not proof" closure: the validator cannot prove a TABLE is
/// only reached from a boss, so this prints the real corpus's own per-source rate for review instead.</summary>
public readonly record struct AssuranceSourceYieldRow(string SourceKey, string MaterialId, long ExpectedPerMille);

public static class CraftAssuranceHorizonReport
{
    /// <summary>
    /// One level's row. <paramref name="assureYieldPerMille"/> is the caller's own choice of which
    /// real corpus source's rate to farm against (<see cref="ExpectedRefIdPerMille"/>) — this function
    /// stays agnostic about WHICH source, since the spec never mandates a canonical farming source and
    /// baking in an aggregation policy across sources would be inventing a rule the spec does not
    /// state. <c>checked</c> throughout; widened before multiplying, divided last.
    /// </summary>
    public static GambleVsAssuranceRow Row(
        int targetLevel, EnhancementTuning enhance, int assureBonusMilli, long assureYieldPerMille,
        long? craftWearPerAttemptMilli = null)
    {
        if (assureBonusMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(assureBonusMilli), assureBonusMilli, "a per-mille bonus cannot be negative");
        if (assureYieldPerMille < 0)
            throw new ArgumentOutOfRangeException(nameof(assureYieldPerMille), assureYieldPerMille, "a per-mille yield cannot be negative");
        if (craftWearPerAttemptMilli is < 0L)
            throw new ArgumentOutOfRangeException(nameof(craftWearPerAttemptMilli), craftWearPerAttemptMilli,
                "a per-mille wear rate cannot be negative");

        var successMilli = EnhancePolicy.SuccessMilli(targetLevel, enhance);
        if (successMilli <= 0)
            throw new InvalidOperationException(
                $"+{targetLevel} resolved a zero success chance — D7 forbids a luck wall, so this would be one");

        var expectedAttemptsMilli = checked(1000L * 1000L / successMilli);

        var remainingMilli = 1000L - successMilli;
        var chargesForCertainty = remainingMilli > 0 && assureBonusMilli > 0
            ? checked((remainingMilli + assureBonusMilli - 1) / assureBonusMilli)
            : 0L;

        var expectedBossKillsMilli = chargesForCertainty <= 0
            ? 0L
            : assureYieldPerMille > 0
                ? checked(checked(chargesForCertainty * 1000L) * 1000L / assureYieldPerMille)
                : long.MaxValue;

        // § Design 7's downgrade-risk column, and the craft-wear pair built on it. The wear rate is
        // per-mille of max per attempt (CraftRiskPolicy.WearFor), so E attempts cost E × rate — rounded
        // UP to per-mille (the per-attempt ceil can only make the real total larger, never smaller).
        var attemptsWithDowngradeMilli = ExpectedAttemptsWithDowngradeMilli(targetLevel, enhance);
        long? wearGambling = craftWearPerAttemptMilli is { } rate
            ? checked((attemptsWithDowngradeMilli * rate + 999) / 1000)
            : null;
        long? wearCertainty = craftWearPerAttemptMilli;

        return new GambleVsAssuranceRow(
            targetLevel, successMilli, expectedAttemptsMilli, chargesForCertainty, expectedBossKillsMilli,
            attemptsWithDowngradeMilli, wearGambling, wearCertainty);
    }

    /// <summary>
    /// § Design 7's <b>downgrade risk</b>: the expected attempts, per-mille, to advance from
    /// <paramref name="targetLevel"/> (to <c>+1</c>), with a failed attempt falling one level when the
    /// level's band sets <c>canDowngrade</c> AND the level is at or above
    /// <c>enhancement.downgradeFromLevel</c> — <see cref="EnhancePolicy.Resolve"/>'s own condition, read
    /// from the same tuning. Where a failure cannot downgrade this is exactly
    /// <c>1000×1000/SuccessMilli</c> (the geometric mean); where it can, the expectation satisfies
    /// <c>E = (1 + q·E(prev))/p</c>, so it is strictly larger — the direction the shipped lower bound
    /// documents but does not compute. An unwarded gambler is assumed: a loaded ward removes the
    /// downgrade, which is the mechanic's whole point. <c>checked</c> throughout: an open-ended peril
    /// band's compounding expectation throws rather than wrapping.
    /// </summary>
    public static long ExpectedAttemptsWithDowngradeMilli(int targetLevel, EnhancementTuning enhance)
    {
        ArgumentNullException.ThrowIfNull(enhance);
        if (targetLevel < 1)
            throw new ArgumentOutOfRangeException(nameof(targetLevel), targetLevel, "levels start at 1");

        long current = 0;
        long? previous = null;
        for (var level = 1; level <= targetLevel; level++)
        {
            var success = EnhancePolicy.SuccessMilli(level, enhance);
            if (success <= 0)
                throw new InvalidOperationException(
                    $"+{level} resolved a zero success chance — D7 forbids a luck wall, so this would be one");

            var band = EnhancePolicy.BandFor(level, enhance);
            var downgrades = band.CanDowngrade && level >= enhance.DowngradeFromLevel;

            current = downgrades && previous is { } prev
                ? checked(((1000L + checked(1000L - success) * prev / 1000) * 1000) / success)
                : checked(1000L * 1000L / success);
            previous = current;
        }

        return current;
    }

    /// <summary>
    /// Expected count of ONE named material id per draw of <paramref name="table"/>, in per-mille —
    /// the same numerator/denominator shape <see cref="DropTableDraw.ExpectedEquipmentPerMille"/>
    /// already proved for <c>Kind == Equipment</c>, generalised to "this exact ref id" for §Design 7's
    /// per-source yield. A separate function rather than widening the shipped one: that one is tested
    /// and load-bearing for the Θ-pin calibration table (`DropVolumeCorpusTests`), and
    /// <c>Kind == Equipment</c> vs. <c>RefId == x</c> are different filters over the same entries, not
    /// a variant worth forking a proven function for. Unscaled (no volume term) — Θ scales DRAW COUNT,
    /// never composition (the same reason <c>ExpectedEquipmentPerMille</c> takes a volume scale but
    /// this omits one: a per-source yield COMPARISON is about composition, not about how many rolls a
    /// given Θ buys). Reads <see cref="DropTableDraw.EffectiveWeight"/> — the same ilvl-band gate the
    /// shipped function uses — so an entry elsewhere in the same table with a <c>MinIlvl</c>/
    /// <c>MaxIlvl</c> band is neither over- nor under-counted at this <paramref name="itemLevel"/>.
    /// </summary>
    public static long ExpectedRefIdPerMille(
        DropTableRow table, string refId, int itemLevel, Func<string, DropTableRow?> lookupTable,
        int depth = 0, int maxDepth = 3)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (lookupTable is null) throw new ArgumentNullException(nameof(lookupTable));
        if (depth > maxDepth)
            throw new InvalidOperationException($"drop table '{table.TableId}' nests deeper than {maxDepth}");
        if (!table.Enabled) return 0;

        long total = 0;
        foreach (var g in table.Groups)
        {
            long groupTotal = 0;
            foreach (var e in g.Entries)
                groupTotal = checked(groupTotal + DropTableDraw.EffectiveWeight(e, itemLevel));
            if (groupTotal <= 0) continue;

            long numerator = 0;
            foreach (var e in g.Entries)
            {
                var w = DropTableDraw.EffectiveWeight(e, itemLevel);
                if (w == 0) continue;

                if (e.Kind == DropEntryKind.Material && string.Equals(e.RefId, refId, StringComparison.Ordinal))
                {
                    numerator = checked(numerator + w * 1000L);
                }
                else if (e.Kind == DropEntryKind.Table)
                {
                    var nested = lookupTable(e.RefId);
                    if (nested is null) continue;
                    var nestedMilli = ExpectedRefIdPerMille(nested, refId, itemLevel, lookupTable, depth + 1, maxDepth);
                    numerator = checked(numerator + w * nestedMilli);
                }
            }

            total = checked(total + numerator / groupTotal);
        }

        return total;
    }

    /// <summary>
    /// §Design 7's per-source yield: for every shipped <see cref="LootSourceRow"/> and every one of
    /// the three <see cref="MaterialCatalog.AssuranceVerbs"/> ids, the expected per-mille yield of ONE
    /// draw against that source's table — printed so a boss-only consumable dropping from a
    /// <c>drop</c>-channel (trash) source is visible in review, the exact gap §6 says the validator
    /// alone cannot close.
    /// </summary>
    public static IReadOnlyList<AssuranceSourceYieldRow> PerSourceYield(
        IReadOnlyList<LootSourceRow> sources, IReadOnlyDictionary<string, DropTableRow> byId)
    {
        if (sources is null) throw new ArgumentNullException(nameof(sources));
        if (byId is null) throw new ArgumentNullException(nameof(byId));

        var rows = new List<AssuranceSourceYieldRow>();
        foreach (var s in sources)
        {
            if (!byId.TryGetValue(s.TableId, out var table)) continue;
            foreach (var verb in MaterialCatalog.AssuranceVerbs)
            {
                var refId = MaterialCatalog.AssuranceId(verb);
                var milli = ExpectedRefIdPerMille(
                    table, refId, s.ContentLevel, id => byId.TryGetValue(id, out var t) ? t : null);
                rows.Add(new AssuranceSourceYieldRow(s.Key, refId, milli));
            }
        }
        return rows;
    }

    /// <summary>
    /// Renders both halves as text — the report's own "prints, never asserts" surface. No I/O: takes
    /// already-loaded tuning and an already-parsed corpus (tunables-ssot.md: Core never reads a file).
    /// </summary>
    public static string Render(
        int fromLevel, int toLevel, EnhancementTuning enhance, int assureBonusMilli, long assureYieldPerMille,
        IReadOnlyList<AssuranceSourceYieldRow> sourceYield, long? craftWearPerAttemptMilli = null)
    {
        if (toLevel < fromLevel)
            throw new ArgumentOutOfRangeException(nameof(toLevel), toLevel, "toLevel must be >= fromLevel");

        var sb = new StringBuilder();
        sb.AppendLine("gamble-vs-assurance (R-G1) — expected attempts to gamble vs. expected boss kills for certainty");
        for (var level = fromLevel; level <= toLevel; level++)
        {
            var row = Row(level, enhance, assureBonusMilli, assureYieldPerMille, craftWearPerAttemptMilli);
            sb.Append(
                $"+{row.TargetLevel}: success={row.SuccessMilli}‰ gambleAttemptsMilli={row.ExpectedAttemptsMilli} "
                + $"gambleAttemptsWithDowngradeMilli={row.ExpectedAttemptsWithDowngradeMilli} "
                + $"assureChargesForCertainty={row.AssureChargesForCertainty} bossKillsForCertaintyMilli={row.ExpectedBossKillsForCertaintyMilli}");
            if (row.ExpectedWearPerMilleOfMaxGambling is { } gamblingWear && row.ExpectedWearPerMilleOfMaxCertainty is { } certaintyWear)
                sb.Append($" craftWearPerMilleOfMaxGambling={gamblingWear} craftWearPerMilleOfMaxCertainty={certaintyWear}");
            sb.AppendLine();
        }

        sb.AppendLine("per-source assurance yield (‰ per draw)");
        foreach (var y in sourceYield)
            sb.AppendLine($"{y.SourceKey} {y.MaterialId} {y.ExpectedPerMille}‰");

        return sb.ToString();
    }
}
