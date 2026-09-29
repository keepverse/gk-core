namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>Every atom family this catalog carries, and how many atoms/affixes reference it.</summary>
public sealed record FamilyCoverage(string FamilyId, int AtomCount, int AffixCount);

/// <summary>
/// One container's own roll budget against what its pool can actually supply. A budget the pool
/// cannot fill is a real content gap — a rarity that asks for 3 prefix rolls from a pool with only 1
/// eligible prefix-class affix will draw the same affix repeatedly (or fail to fill at all, depending
/// on the resolver's own no-repeat rule), never the varied roster a rarity band implies.
/// </summary>
public sealed record ContainerFillRate(
    string ContainerId, int PrefixRollsNeeded, int PrefixEligibleAffixes,
    int SuffixRollsNeeded, int SuffixEligibleAffixes)
{
    /// <summary>True when the pool has at least as many eligible affixes as the budget asks for, on
    /// BOTH sides — never partial credit, since a starved suffix budget is exactly as real a gap as
    /// a starved prefix one.</summary>
    public bool MeetsBudget =>
        PrefixEligibleAffixes >= PrefixRollsNeeded && SuffixEligibleAffixes >= SuffixRollsNeeded;
}

/// <summary>
/// `affix-metrics` (T3.8, `affix-library` coverage and roll health) — the metrics half this module's
/// own acceptance line names: family coverage and container fill rate. Pure, Core-only, no I/O,
/// mirroring `ContentValidation.Lint`'s own shape exactly (explicit lists in, a report out) — the
/// SAME reason that module stays testable without a database applies here.
///
/// <para><b>"Register with declared targets" (2026-09-06):</b> a RICHER numeric target — how many
/// affixes SHOULD exist per family, what fill rate is "healthy" beyond merely meeting its own
/// budget — is still a balance judgement this module cannot make for itself, the same class of
/// decision T6.2's own curve-input boundary and T4.8's own `OwnerKind` boundary already named this
/// session. But a NARROWER floor needs no balance authority at all — it falls straight out of the
/// two shapes above: a family with atoms but zero affixes referencing it is unreachable content,
/// full stop (<see cref="FamilyCoverage.AffixCount"/> `== 0`), and a container that cannot fill its
/// own declared roll budget is starved regardless of what "healthy" means (<see
/// cref="ContainerFillRate.MeetsBudget"/> `== false`). <see cref="AffixMetricsGateEvaluator"/> below
/// registers exactly that floor against <c>gk-core/data/tuning/affix-metrics.v1.json</c>, shipped with both
/// gates `false` (measure-only) — matching seedsmith's own established "new metrics are measure-only
/// until calibrated" rule for its W1 registry — so promoting either gate to `true` is the real,
/// separate balance decision, deferred exactly like every other one this session named rather than
/// invented.</para>
/// </summary>
public static class ContentMetrics
{
    /// <summary>Every family named by an atom OR reachable through an affix's own concrete refs —
    /// a family with atoms but zero affixes referencing it is exactly as real a coverage gap as one
    /// with affixes but no atoms (the latter cannot happen if affixes were validated first, but this
    /// module does not assume that — it reports what it is handed).</summary>
    public static IReadOnlyList<FamilyCoverage> FamilyCoverageOf(
        IReadOnlyList<AtomRow> atoms, IReadOnlyList<AffixRow> affixes)
    {
        var atomsById = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var atomCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in atoms)
            atomCounts[a.FamilyId] = atomCounts.GetValueOrDefault(a.FamilyId) + 1;

        var affixCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var affix in affixes)
        {
            // One family may appear more than once in a single affix's own refs (a multi-atom
            // bundle spanning two tiers of the same family) — counted once per AFFIX, not once per
            // ref, since the question is "how many affixes touch this family," not "how many refs."
            var familiesTouched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in affix.Refs)
            {
                if (r.AtomId is not null && atomsById.TryGetValue(r.AtomId, out var atom))
                    familiesTouched.Add(atom.FamilyId);
                else if (r.IsSlot && r.SlotAtomPattern is not null)
                {
                    // A slotted ref names a pattern like "atom.elemental-power.$E1" — the family is
                    // everything before the placeholder, the same split AffixValidator's own
                    // SubstitutePattern already uses.
                    var placeholder = "$" + r.SlotName;
                    var idx = r.SlotAtomPattern.IndexOf(placeholder, StringComparison.Ordinal);
                    if (idx > 0) familiesTouched.Add(r.SlotAtomPattern[..idx].TrimEnd('.'));
                }
            }
            foreach (var family in familiesTouched)
                affixCounts[family] = affixCounts.GetValueOrDefault(family) + 1;
        }

        var everyFamily = atomCounts.Keys.Union(affixCounts.Keys, StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal);
        return everyFamily
            .Select(f => new FamilyCoverage(f, atomCounts.GetValueOrDefault(f), affixCounts.GetValueOrDefault(f)))
            .ToList();
    }

    /// <summary>One fill-rate row per container that actually has a pool (`prefixRolls +
    /// suffixRolls > 0`) — a fixed-core-only container, like `patron.aura`, has nothing to fill and
    /// is correctly absent from this report rather than reported as a 0-of-0 non-finding.</summary>
    public static IReadOnlyList<ContainerFillRate> ContainerFillRatesOf(
        IReadOnlyList<ContainerRow> containers, IReadOnlyList<AffixRow> affixes)
    {
        var affixesById = affixes.ToDictionary(a => a.AffixId, StringComparer.Ordinal);
        var rows = new List<ContainerFillRate>();

        foreach (var c in containers)
        {
            if (c.PrefixRolls <= 0 && c.SuffixRolls <= 0) continue;

            var prefixEligible = 0;
            var suffixEligible = 0;
            foreach (var p in c.Pool)
            {
                if (!affixesById.TryGetValue(p.AffixId, out var affix)) continue;
                if (affix.Class is AffixClass.Prefix or AffixClass.Mixed) prefixEligible++;
                if (affix.Class is AffixClass.Suffix or AffixClass.Mixed) suffixEligible++;
            }

            rows.Add(new ContainerFillRate(c.ContainerId, c.PrefixRolls, prefixEligible, c.SuffixRolls, suffixEligible));
        }

        return rows.OrderBy(r => r.ContainerId, StringComparer.Ordinal).ToList();
    }
}

/// <summary>The two structural gates <see cref="AffixMetricsGateEvaluator"/> knows about, loaded from
/// <c>data/tuning/affix-metrics.v{n}.json</c>. `false` means measure-only: the finding is still
/// reported, it just never fails a `--gate` run.</summary>
public sealed record AffixMetricsTargets(bool FamilyCoverageGates, bool ContainerFillRateGates)
{
    /// <summary>The shipped v1 default — both gates off, per this file's own class doc.</summary>
    public static readonly AffixMetricsTargets MeasureOnly = new(false, false);
}

/// <summary>One structural finding: an unreachable family or a starved pool, told whether ITS OWN
/// gate is armed today.</summary>
public sealed record AffixMetricsFinding(string Kind, string Id, string Detail, bool Gates);

/// <summary>
/// `affix-metrics`'s own "register with declared targets" half (see the class doc above for why the
/// two checks here are structural floors, not invented balance numbers). Pure — takes the reports
/// <see cref="ContentMetrics.FamilyCoverageOf"/>/<see cref="ContentMetrics.ContainerFillRatesOf"/>
/// already computed, never re-derives them, mirroring every other "measure, then gate" split in this
/// program (`seedsmith`'s own metric registry, `creatures metrics --gate`).
/// </summary>
public static class AffixMetricsGateEvaluator
{
    public static IReadOnlyList<AffixMetricsFinding> Evaluate(
        IReadOnlyList<FamilyCoverage> familyCoverage,
        IReadOnlyList<ContainerFillRate> fillRates,
        AffixMetricsTargets targets)
    {
        var findings = new List<AffixMetricsFinding>();

        foreach (var f in familyCoverage)
            if (f.AffixCount == 0)
                findings.Add(new AffixMetricsFinding(
                    "Coverage/UnreachableFamily", f.FamilyId,
                    $"{f.AtomCount} atom(s) in family '{f.FamilyId}', zero affixes reference it",
                    targets.FamilyCoverageGates));

        foreach (var r in fillRates)
            if (!r.MeetsBudget)
                findings.Add(new AffixMetricsFinding(
                    "Distribution/StarvedPool", r.ContainerId,
                    $"prefix {r.PrefixEligibleAffixes}/{r.PrefixRollsNeeded} eligible, " +
                    $"suffix {r.SuffixEligibleAffixes}/{r.SuffixRollsNeeded} eligible",
                    targets.ContainerFillRateGates));

        return findings;
    }

    /// <summary>True only when a finding whose OWN gate is armed exists — the exact `--gate`
    /// semantics `creatures metrics --gate` already established (filter to `gates=True`, not "any
    /// finding at all").</summary>
    public static bool AnyGatingFinding(IReadOnlyList<AffixMetricsFinding> findings) =>
        findings.Any(f => f.Gates);
}
