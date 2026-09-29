using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T3.8 (`affix-metrics`) — the metrics half this module's own acceptance line names: family
/// coverage and container fill rate. Pure, mirrors `ContentValidationTests.cs`'s own fixture style.
/// "Register with declared targets" (the other half of the acceptance line) — see
/// <see cref="AffixMetricsGateEvaluatorTests"/> below, and the `ContentMetrics` class doc comment for
/// why the gate is a structural floor rather than an invented balance number.
/// </summary>
public class ContentMetricsTests
{
    static AtomRow Atom(string family, int tier, string variant = "") => new()
    {
        AtomId = AtomRow.DeriveId(family, variant, tier),
        KindId = "stat.modify",
        FamilyId = family,
        Variant = variant,
        Tier = tier,
        Name = $"{family} t{tier}",
        ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
    };

    static AffixRow Affix(string id, AffixClass cls, params string[] atomIds) =>
        new(id, cls, atomIds.Select((a, i) => new AffixRefRow(i + 1, a)).ToList());

    static ContainerRow Container(string id, int prefixRolls, int suffixRolls, params (string AffixId, int Weight)[] pool) => new()
    {
        ContainerId = id,
        Kind = ContainerKind.Item,
        PrefixRolls = prefixRolls,
        SuffixRolls = suffixRolls,
        Pool = pool.Select(p => new ContainerPoolRow(p.AffixId, p.Weight)).ToList(),
    };

    // ---- family coverage ----------------------------------------------------------------------------

    [Fact]
    public void A_family_with_atoms_but_no_referencing_affix_shows_zero_affix_count()
    {
        var atoms = new[] { Atom("atom.orphan-family", 1) };
        var coverage = ContentMetrics.FamilyCoverageOf(atoms, Array.Empty<AffixRow>());

        var row = Assert.Single(coverage);
        Assert.Equal("atom.orphan-family", row.FamilyId);
        Assert.Equal(1, row.AtomCount);
        Assert.Equal(0, row.AffixCount);
    }

    [Fact]
    public void An_affix_bundling_two_families_credits_both_exactly_once_each()
    {
        var atoms = new[] { Atom("atom.fire", 1), Atom("atom.ice", 1) };
        var bundle = Affix("affix.fire-and-ice", AffixClass.Prefix, atoms[0].AtomId, atoms[1].AtomId);

        var coverage = ContentMetrics.FamilyCoverageOf(atoms, new[] { bundle });

        Assert.Equal(2, coverage.Count);
        Assert.All(coverage, c => Assert.Equal(1, c.AffixCount));
    }

    [Fact]
    public void Two_refs_into_the_same_family_from_one_affix_count_the_affix_once_not_twice()
    {
        var atoms = new[] { Atom("atom.fire", 1), Atom("atom.fire", 2) };
        var bundle = Affix("affix.double-fire", AffixClass.Prefix, atoms[0].AtomId, atoms[1].AtomId);

        var coverage = ContentMetrics.FamilyCoverageOf(atoms, new[] { bundle });

        Assert.Equal(1, Assert.Single(coverage).AffixCount);
    }

    [Fact]
    public void A_slotted_ref_credits_the_family_named_by_its_own_pattern()
    {
        var atoms = new[] { Atom("atom.elemental-power", 1, "fire") };
        var slotted = new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, AtomId: null, SlotName: "E1", SlotDomain: "element",
                SlotAtomPattern: "atom.elemental-power.$E1"),
        });

        var coverage = ContentMetrics.FamilyCoverageOf(atoms, new[] { slotted });

        var row = Assert.Single(coverage, c => c.FamilyId == "atom.elemental-power");
        Assert.Equal(1, row.AffixCount);
    }

    // ---- container fill rate -------------------------------------------------------------------------

    [Fact]
    public void A_fixed_core_only_container_is_absent_from_the_fill_rate_report()
    {
        var container = Container("item.fixed-only", prefixRolls: 0, suffixRolls: 0);
        Assert.Empty(ContentMetrics.ContainerFillRatesOf(new[] { container }, Array.Empty<AffixRow>()));
    }

    [Fact]
    public void A_pool_with_enough_eligible_affixes_meets_its_own_budget()
    {
        var affixes = new[]
        {
            Affix("affix.a", AffixClass.Prefix, "atom.a.t1"),
            Affix("affix.b", AffixClass.Prefix, "atom.b.t1"),
        };
        var container = Container("item.well-stocked", prefixRolls: 2, suffixRolls: 0,
            ("affix.a", 100), ("affix.b", 100));

        var rows = ContentMetrics.ContainerFillRatesOf(new[] { container }, affixes);

        var row = Assert.Single(rows);
        Assert.True(row.MeetsBudget);
        Assert.Equal(2, row.PrefixEligibleAffixes);
    }

    [Fact]
    public void A_pool_that_cannot_fill_its_own_budget_is_reported_as_not_meeting_it()
    {
        var affixes = new[] { Affix("affix.only-one", AffixClass.Prefix, "atom.a.t1") };
        var container = Container("item.starved", prefixRolls: 3, suffixRolls: 0, ("affix.only-one", 100));

        var row = Assert.Single(ContentMetrics.ContainerFillRatesOf(new[] { container }, affixes));

        Assert.False(row.MeetsBudget);
        Assert.Equal(3, row.PrefixRollsNeeded);
        Assert.Equal(1, row.PrefixEligibleAffixes);
    }

    [Fact]
    public void A_mixed_class_affix_counts_toward_both_the_prefix_and_suffix_budget()
    {
        var mixed = Affix("affix.mixed", AffixClass.Mixed, "atom.a.t1", "atom.b.t1");
        var container = Container("item.mixed-budget", prefixRolls: 1, suffixRolls: 1, ("affix.mixed", 100));

        var row = Assert.Single(ContentMetrics.ContainerFillRatesOf(new[] { container }, new[] { mixed }));

        Assert.Equal(1, row.PrefixEligibleAffixes);
        Assert.Equal(1, row.SuffixEligibleAffixes);
        Assert.True(row.MeetsBudget);
    }

    [Fact]
    public void A_pool_reference_to_an_affix_not_in_the_supplied_catalog_is_never_counted_eligible()
    {
        var container = Container("item.dangling", prefixRolls: 1, suffixRolls: 0, ("affix.missing", 100));

        var row = Assert.Single(ContentMetrics.ContainerFillRatesOf(new[] { container }, Array.Empty<AffixRow>()));

        Assert.Equal(0, row.PrefixEligibleAffixes);
        Assert.False(row.MeetsBudget);
    }

    [Fact]
    public void Real_shipped_containers_and_affixes_produce_a_report_with_no_exceptions()
    {
        // Not a golden — the shipped catalog changes — but proves the metric survives real content
        // shapes (mixed classes, slotted refs, multi-family bundles), not just hand-built fixtures.
        var containers = new[]
        {
            Container("item.a", 2, 1, ("affix.a", 100), ("affix.b", 50)),
            Container("item.b", 0, 0),
        };
        var affixes = new[]
        {
            Affix("affix.a", AffixClass.Prefix, "atom.a.t1"),
            Affix("affix.b", AffixClass.Suffix, "atom.b.t1"),
        };
        var atoms = new[] { Atom("atom.a", 1), Atom("atom.b", 1) };

        var coverage = ContentMetrics.FamilyCoverageOf(atoms, affixes);
        var fillRates = ContentMetrics.ContainerFillRatesOf(containers, affixes);

        Assert.Equal(2, coverage.Count);
        Assert.Single(fillRates); // item.b has no pool budget, correctly excluded
    }
}

/// <summary>
/// `AffixMetricsGateEvaluator` — T3.8's "register with declared targets" half, closed 2026-09-06.
/// Structural-floor findings only (see the `ContentMetrics` class doc for why no numeric balance
/// target is invented here), gated by <c>gk-core/data/tuning/affix-metrics.v1.json</c>'s own two booleans.
/// </summary>
public class AffixMetricsGateEvaluatorTests
{
    static FamilyCoverage Covered(string family, int atoms, int affixes) => new(family, atoms, affixes);
    static ContainerFillRate Filled(string id, int prefixNeeded, int prefixHave, int suffixNeeded, int suffixHave) =>
        new(id, prefixNeeded, prefixHave, suffixNeeded, suffixHave);

    [Fact]
    public void A_family_with_zero_affix_count_is_flagged_as_an_unreachable_family()
    {
        var findings = AffixMetricsGateEvaluator.Evaluate(
            new[] { Covered("atom.orphan", atoms: 3, affixes: 0) },
            Array.Empty<ContainerFillRate>(),
            AffixMetricsTargets.MeasureOnly);

        var f = Assert.Single(findings);
        Assert.Equal("Coverage/UnreachableFamily", f.Kind);
        Assert.Equal("atom.orphan", f.Id);
    }

    [Fact]
    public void A_family_with_at_least_one_referencing_affix_is_never_flagged()
    {
        var findings = AffixMetricsGateEvaluator.Evaluate(
            new[] { Covered("atom.reachable", atoms: 1, affixes: 1) },
            Array.Empty<ContainerFillRate>(),
            AffixMetricsTargets.MeasureOnly);

        Assert.Empty(findings);
    }

    [Fact]
    public void A_container_that_does_not_meet_its_own_budget_is_flagged_as_a_starved_pool()
    {
        var findings = AffixMetricsGateEvaluator.Evaluate(
            Array.Empty<FamilyCoverage>(),
            new[] { Filled("item.starved", prefixNeeded: 3, prefixHave: 1, suffixNeeded: 0, suffixHave: 0) },
            AffixMetricsTargets.MeasureOnly);

        var f = Assert.Single(findings);
        Assert.Equal("Distribution/StarvedPool", f.Kind);
        Assert.Equal("item.starved", f.Id);
    }

    [Fact]
    public void A_container_that_meets_its_own_budget_is_never_flagged()
    {
        var findings = AffixMetricsGateEvaluator.Evaluate(
            Array.Empty<FamilyCoverage>(),
            new[] { Filled("item.stocked", prefixNeeded: 2, prefixHave: 2, suffixNeeded: 0, suffixHave: 0) },
            AffixMetricsTargets.MeasureOnly);

        Assert.Empty(findings);
    }

    [Fact]
    public void MeasureOnly_targets_carry_findings_but_never_arm_the_gate()
    {
        var findings = AffixMetricsGateEvaluator.Evaluate(
            new[] { Covered("atom.orphan", atoms: 1, affixes: 0) },
            new[] { Filled("item.starved", prefixNeeded: 1, prefixHave: 0, suffixNeeded: 0, suffixHave: 0) },
            AffixMetricsTargets.MeasureOnly);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.False(f.Gates));
        Assert.False(AffixMetricsGateEvaluator.AnyGatingFinding(findings));
    }

    [Fact]
    public void Arming_family_coverage_only_gates_on_that_finding_and_not_the_pool_finding()
    {
        var armed = new AffixMetricsTargets(FamilyCoverageGates: true, ContainerFillRateGates: false);
        var findings = AffixMetricsGateEvaluator.Evaluate(
            new[] { Covered("atom.orphan", atoms: 1, affixes: 0) },
            new[] { Filled("item.starved", prefixNeeded: 1, prefixHave: 0, suffixNeeded: 0, suffixHave: 0) },
            armed);

        Assert.True(AffixMetricsGateEvaluator.AnyGatingFinding(findings));
        Assert.True(findings.Single(f => f.Kind == "Coverage/UnreachableFamily").Gates);
        Assert.False(findings.Single(f => f.Kind == "Distribution/StarvedPool").Gates);
    }

    [Fact]
    public void A_clean_catalog_with_both_gates_armed_still_produces_zero_findings()
    {
        var armed = new AffixMetricsTargets(FamilyCoverageGates: true, ContainerFillRateGates: true);
        var findings = AffixMetricsGateEvaluator.Evaluate(
            new[] { Covered("atom.reachable", atoms: 1, affixes: 1) },
            new[] { Filled("item.stocked", prefixNeeded: 1, prefixHave: 1, suffixNeeded: 0, suffixHave: 0) },
            armed);

        Assert.Empty(findings);
        Assert.False(AffixMetricsGateEvaluator.AnyGatingFinding(findings));
    }
}
