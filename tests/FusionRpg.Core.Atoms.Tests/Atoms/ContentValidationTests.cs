using System.Linq;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E14b's three content checks (spec-authoring-and-validation.md).
///
/// <para><b>Validations fail; lints warn.</b> A budget breach is a mistake. A tier gap is usually a
/// typo and occasionally deliberate. Filing them together means either blocking on a guess or
/// shrugging at a real error, so they are kept apart and the report says which is which.</para>
/// </summary>
public class ContentValidationTests
{
    static AtomRow Atom(
        string family, int tier, int amount, string variant = "", string channel = "maxHp",
        string? power = null, string? note = null) => new()
    {
        AtomId = AtomRow.DeriveId(family, variant, tier),
        KindId = "stat.modify",
        FamilyId = family,
        Variant = variant,
        Tier = tier,
        Name = $"{family} t{tier}",
        ParamsJson = $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""",
        PowerJson = power,
        PowerNote = note,
        PowerOverrideJson = note is null ? null : power,
    };

    static ContainerRow Container(string id, params string[] atomIds) => new()
    {
        ContainerId = id,
        Kind = ContainerKind.Item,
        Atoms = atomIds.Select((a, i) => new ContainerAtomRow(i, a)).ToList(),
    };

    // ---- the budget --------------------------------------------------------------------------------

    [Fact]
    public void A_container_over_its_rarity_ceiling_fails_and_is_named()
    {
        var atoms = new[] { Atom("atom.huge", 1, 100_000) };
        var container = Container("item.overspent", atoms[0].AtomId) with { Rarity = "common" };

        var report = ContentValidation.Budget(
            new[] { container }, _ => atoms, _ => 10);

        Assert.False(report.Ok);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("item.overspent", failure.Subject);
        Assert.Contains("over", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_inside_its_ceiling_passes()
    {
        var atoms = new[] { Atom("atom.small", 1, 10) };
        var container = Container("item.thrifty", atoms[0].AtomId) with { Rarity = "common" };

        var report = ContentValidation.Budget(new[] { container }, _ => atoms, _ => 100_000);

        Assert.True(report.Ok);
        Assert.Equal(1, report.Evaluated);
    }

    [Fact]
    public void A_pass_that_evaluated_nothing_says_so_rather_than_looking_green()
    {
        // The honest caveat the spec insisted on. At E14b's position the only containers are E11's
        // migration output and none carry a rarity, so the budget check genuinely enumerates almost
        // nothing — and a silent green would read as "the content is within budget".
        var container = Container("trait.critical-hunter"); // no rarity

        var report = ContentValidation.Budget(new[] { container }, _ => Array.Empty<AtomRow>(), _ => 100);

        Assert.True(report.Ok);
        Assert.Equal(0, report.Evaluated);
        Assert.Contains("0 evaluated", report.Render("budget"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rarity_with_no_ceiling_is_skipped_rather_than_treated_as_zero()
    {
        // A missing ceiling read as 0 would fail every container naming that rarity, loudly and
        // wrongly, the moment a band was added to the content without a budget curve.
        var atoms = new[] { Atom("atom.any", 1, 50) };
        var container = Container("item.x", atoms[0].AtomId) with { Rarity = "mythic" };

        var report = ContentValidation.Budget(new[] { container }, _ => atoms, _ => null);

        Assert.True(report.Ok);
        Assert.Equal(0, report.Evaluated);
    }

    // ---- the budget, rung-keyed (A-G1, spec-tier-access-gate.md §3.2) --------------------------------

    [Fact]
    public void A_container_over_its_rung_ceiling_fails_and_is_named()
    {
        var atoms = new[] { Atom("atom.huge", 1, 100_000) };
        var container = Container("skill.overspent", atoms[0].AtomId);

        var report = ContentValidation.Budget(
            new[] { container }, _ => atoms, _ => 1, _ => 10L);

        Assert.False(report.Ok);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("skill.overspent", failure.Subject);
        Assert.Contains("rung 1", failure.Detail, StringComparison.Ordinal);
        Assert.Contains("over", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_inside_its_rung_ceiling_passes()
    {
        var atoms = new[] { Atom("atom.small", 1, 10) };
        var container = Container("skill.thrifty", atoms[0].AtomId);

        var report = ContentValidation.Budget(new[] { container }, _ => atoms, _ => 1, _ => 100_000L);

        Assert.True(report.Ok);
        Assert.Equal(1, report.Evaluated);
    }

    [Fact]
    public void A_container_with_no_rung_is_skipped_rather_than_treated_as_zero()
    {
        var container = Container("skill.unrunged"); // rungOf returns null for it below

        var report = ContentValidation.Budget(
            new[] { container }, _ => Array.Empty<AtomRow>(), _ => null, _ => 100L);

        Assert.True(report.Ok);
        Assert.Equal(0, report.Evaluated);
    }

    [Fact]
    public void A_rung_with_no_ceiling_loaded_is_skipped_rather_than_treated_as_zero()
    {
        // A rung table with no powerBudgetMilli column (v1) resolves every rung to null -- read as
        // "no ceiling data source", never as "budgets nothing" (the same direction the rarity-keyed
        // overload already takes for a missing ceiling).
        var atoms = new[] { Atom("atom.any", 1, 50) };
        var container = Container("skill.x", atoms[0].AtomId);

        var report = ContentValidation.Budget(new[] { container }, _ => atoms, _ => 5, _ => null);

        Assert.True(report.Ok);
        Assert.Equal(0, report.Evaluated);
    }

    // ---- power drift ---------------------------------------------------------------------------------

    [Fact]
    public void Stored_power_matching_the_computation_does_not_drift()
    {
        var atom = Atom("atom.steady", 1, 45);
        var priced = CostFunction.Price(atom).Power;
        var stored = atom with { PowerJson = priced.ToJson() };

        var report = ContentValidation.Drift(new[] { stored });

        Assert.True(report.Ok);
        Assert.Equal(1, report.Evaluated);
    }

    [Fact]
    public void Stored_power_far_from_the_computation_fails_when_nothing_explains_it()
    {
        var atom = Atom("atom.wrong", 1, 45,
            power: new PowerVector(0, 99_999, 0, 0, 0).ToJson());

        var report = ContentValidation.Drift(new[] { atom });

        Assert.False(report.Ok);
        Assert.Contains(report.Failures, f => f.Rule == "drift");
    }

    [Fact]
    public void The_same_drift_is_reported_but_allowed_when_a_note_explains_it()
    {
        // "Computed base plus stored override" is only honest if the override has to say why. The
        // running list of overrides is also the running list of shapes the cost function is bad at.
        var atom = Atom("atom.explained", 1, 45,
            power: new PowerVector(0, 99_999, 0, 0, 0).ToJson(),
            note: "prices the crit pair, which the per-atom formula halves");

        var report = ContentValidation.Drift(new[] { atom });

        Assert.True(report.Ok);
        Assert.NotEmpty(report.Warnings);
        // Both categories this kind touches drift, and each is reported with the note — the note
        // explains the atom, not one category of it.
        Assert.All(report.Warnings,
            w => Assert.Contains("allowed by note", w.Detail, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(100, 100, false)]
    [InlineData(100, 120, false)]  // 20% — inside
    [InlineData(100, 126, true)]   // 26% — outside
    [InlineData(100, 80, false)]   // 20% under
    [InlineData(100, 70, true)]    // 30% under
    [InlineData(1, 2, false)]      // below the floor, where a percentage means nothing
    [InlineData(0, 1, false)]
    public void The_tolerance_is_twenty_five_percent_with_a_floor(int stored, int computed, bool drifted)
    {
        // 25% for a stated reason: the cost function is knowingly wrong by ~12.5% on multiplicative
        // pairs, so 5% would fail every crit and element atom on day one, and 50% cannot detect a
        // real mistake. 25% catches order-of-magnitude errors — what the units trap produces.
        Assert.Equal(drifted, ContentValidation.Drifted(stored, computed));
    }

    [Fact]
    public void An_atom_that_no_longer_prices_at_all_is_a_failure_not_a_zero()
    {
        var atom = Atom("atom.gone", 1, 45, power: new PowerVector(1, 1, 1, 1, 1).ToJson())
            with { KindId = "no.such.kind" };

        var report = ContentValidation.Drift(new[] { atom });

        Assert.False(report.Ok);
        Assert.Contains("no longer prices", report.Failures.First().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_atom_with_no_stored_power_is_not_drift()
    {
        // E9's backfill leaves an unpriced atom NULL. That is a gap in pricing, not a disagreement
        // about a price, and reporting it here would drown the real signal.
        var report = ContentValidation.Drift(new[] { Atom("atom.unpriced", 1, 45) });

        Assert.True(report.Ok);
        Assert.Equal(0, report.Evaluated);
    }

    // ---- the lints -------------------------------------------------------------------------------------

    [Fact]
    public void A_tier_gap_warns_and_never_blocks()
    {
        var atoms = new[] { Atom("atom.ember", 1, 10), Atom("atom.ember", 2, 20), Atom("atom.ember", 4, 40) };

        var report = ContentValidation.Lint(atoms, Array.Empty<ContainerRow>());

        Assert.True(report.Ok); // a lint never blocks
        Assert.Contains(report.Warnings, w => w.Rule == "tier-gap" && w.Detail.Contains("3"));
    }

    [Fact]
    public void Tier_gaps_are_keyed_on_family_and_variant_not_family_alone()
    {
        // elemental_power holds seven variants over five tiers. A family-level check would hide a
        // real gap in one variant and invent false ones across the rest.
        var atoms = new[]
        {
            Atom("atom.elemental-power", 1, 10, variant: "fire"),
            Atom("atom.elemental-power", 2, 20, variant: "fire"),
            Atom("atom.elemental-power", 1, 10, variant: "ice"),
            Atom("atom.elemental-power", 2, 20, variant: "ice"),
        };

        var report = ContentValidation.Lint(atoms, Array.Empty<ContainerRow>());

        Assert.DoesNotContain(report.Warnings, w => w.Rule == "tier-gap");
    }

    [Fact]
    public void A_real_gap_inside_one_variant_is_still_found()
    {
        // The other half — without it the previous test could be passing because the lint is dead.
        var atoms = new[]
        {
            Atom("atom.elemental-power", 1, 10, variant: "fire"),
            Atom("atom.elemental-power", 3, 30, variant: "fire"),
            Atom("atom.elemental-power", 1, 10, variant: "ice"),
        };

        var report = ContentValidation.Lint(atoms, Array.Empty<ContainerRow>());

        var gap = Assert.Single(report.Warnings.Where(w => w.Rule == "tier-gap"));
        Assert.Equal("atom.elemental-power|fire", gap.Subject);
    }

    [Fact]
    public void A_tier_that_is_not_stronger_than_the_one_below_it_warns()
    {
        var atoms = new[] { Atom("atom.flat", 1, 50), Atom("atom.flat", 2, 50) };

        var report = ContentValidation.Lint(atoms, Array.Empty<ContainerRow>());

        Assert.Contains(report.Warnings, w => w.Rule == "flat-tier");
    }

    [Fact]
    public void Two_families_writing_the_same_channel_and_op_warn()
    {
        var atoms = new[] { Atom("atom.vigour", 1, 10), Atom("atom.vitality", 1, 10) };

        var report = ContentValidation.Lint(atoms, Array.Empty<ContainerRow>());

        Assert.Contains(report.Warnings, w => w.Rule == "duplicate-affix");
    }

    [Fact]
    public void Two_families_on_different_channels_do_not_warn()
    {
        var atoms = new[] { Atom("atom.vigour", 1, 10, channel: "maxHp"),
                            Atom("atom.might", 1, 10, channel: "atk") };

        var report = ContentValidation.Lint(atoms, Array.Empty<ContainerRow>());

        Assert.DoesNotContain(report.Warnings, w => w.Rule == "duplicate-affix");
    }

    [Fact]
    public void A_pool_group_with_one_member_warns()
    {
        var container = Container("item.ring") with
        {
            Pool = new[] { new ContainerPoolRow("atom.a.t1", 100, "solo") },
        };

        var report = ContentValidation.Lint(Array.Empty<AtomRow>(), new[] { container });

        Assert.Contains(report.Warnings, w => w.Rule == "lonely-group");
    }

    [Fact]
    public void An_atom_no_container_references_warns()
    {
        var atoms = new[] { Atom("atom.dead", 1, 10), Atom("atom.used", 1, 10) };
        var container = Container("item.ring", "atom.used.t1");

        var report = ContentValidation.Lint(atoms, new[] { container });

        var orphan = Assert.Single(report.Warnings.Where(w => w.Rule == "orphan"));
        Assert.Equal("atom.dead.t1", orphan.Subject);
    }

    [Fact]
    public void An_affix_no_container_pool_references_warns()
    {
        // T3.8 (affix-metrics): the container-reachability half of "unreachable affix" — the richer
        // tag-eligibility check waits on module 8 (eligibility-tags), not yet built.
        var used = new AffixRow("affix.used", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.used.t1") });
        var dead = new AffixRow("affix.dead", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.dead.t1") });
        var container = Container("item.ring") with
        {
            Pool = new[] { new ContainerPoolRow("affix.used", 100) },
        };

        var report = ContentValidation.Lint(
            Array.Empty<AtomRow>(), new[] { container }, new[] { used, dead });

        var orphan = Assert.Single(report.Warnings.Where(w => w.Rule == "orphan-affix"));
        Assert.Equal("affix.dead", orphan.Subject);
    }

    [Fact]
    public void No_affix_catalog_supplied_reports_no_orphan_affixes()
    {
        // Same "safe direction" OrphanAtoms already established: an omitted affix catalog must never
        // manufacture false positives against data the caller never supplied.
        var container = Container("item.ring") with
        {
            Pool = new[] { new ContainerPoolRow("affix.unknown", 100) },
        };

        var report = ContentValidation.Lint(Array.Empty<AtomRow>(), new[] { container });

        Assert.DoesNotContain(report.Warnings, w => w.Rule == "orphan-affix");
    }

    // ---- over the real shipped corpus ------------------------------------------------------------------

    static (IReadOnlyList<AtomRow> Atoms, IReadOnlyList<ContainerRow> Containers) ShippedSeed()
    {
        var root = RepoRoot();
        var files = new[] { "atoms", "containers" }
            .Select(d => Path.Combine(root, "data", "seed", d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        return (collected.Content.Atoms, collected.Content.Containers);
    }

    [Fact]
    public void Every_shipped_atom_can_be_priced()
    {
        // The check that makes the rest of E14b mean anything. An unpriceable atom is invisible to
        // the budget — it costs nothing — so a family that cannot be priced is a family that cannot
        // be over budget, which is the quietest possible way for the ceiling to stop working.
        var (atoms, _) = ShippedSeed();
        Assert.NotEmpty(atoms);

        var lookupPool = RealLookupPool();
        var unpriceable = atoms
            .Select(a => (a.AtomId, Priced: CostFunction.Price(a, lookupPool: lookupPool)))
            .Where(x => !x.Priced.Ok)
            .Select(x => $"{x.AtomId}: {x.Priced.Verdict.Reason}")
            .ToList();

        Assert.True(unpriceable.Count == 0, string.Join(Environment.NewLine, unpriceable));
    }

    [Fact]
    public void The_shipped_corpus_lints_clean_or_says_exactly_what_it_found()
    {
        // Lints never block, so this asserts the report is legible rather than empty. Orphans are
        // expected right now: E11's migrated defs are granted directly and belong to no container.
        var (atoms, containers) = ShippedSeed();

        var report = ContentValidation.Lint(atoms, containers);

        Assert.True(report.Ok); // a lint never blocks
        Assert.All(report.Findings, f => Assert.False(f.Blocking));
        Assert.DoesNotContain(report.Warnings, w => w.Rule == "tier-gap");
        Assert.DoesNotContain(report.Warnings, w => w.Rule == "flat-tier");
    }

    [Fact]
    public void The_shipped_corpus_has_no_unexplained_power_drift()
    {
        // Nothing in the seed carries stored power yet — E9's backfill runs against a database — so
        // this reports zero evaluated today. It is here so it starts failing the moment the corpus
        // grows a price that disagrees with the formula, rather than being written then.
        var (atoms, _) = ShippedSeed();

        var report = ContentValidation.Drift(atoms);

        Assert.True(report.Ok, string.Join(Environment.NewLine, report.Failures));
    }

    static string RepoRoot() => ContentRoot.Path;

    /// <summary>Real bug found 2026-09-08 (atom-family-expansion): `Every_shipped_atom_can_be_priced`
    /// called `CostFunction.Price(a)` with no `lookupPool` at all, so any pool-referencing
    /// `stat.derived` atom was reported unpriceable by name — a wiring gap in this test's own setup
    /// (E30's channel-pool mechanism already ships, per DESIGN-GATE.md's own "L2 shipped as E30"
    /// correction), not a real product defect. Never visible before because no shipped atom used a
    /// pool-reference channel until this session's `tier-bands-coverage` module unblocked several
    /// (`evd-flinch`/`evd-harden`/`evd-seal`/`shld-breach`, all real element-variant evasion/shield
    /// families). Loads the real, already-shipped `gk-data/packs/fusion/data/seed/channel-pools/pools.v1.json` once.</summary>
    static Func<string, ChannelPoolRow?> RealLookupPool()
    {
        var path = Path.Combine(RepoRoot(), "data", "seed", "channel-pools", "pools.v1.json");
        var rejection = ChannelPoolFile.TryParse(File.ReadAllText(path), out var pools);
        Assert.True(rejection.IsOk, rejection.Detail);
        var byId = pools.ToDictionary(p => p.PoolId, StringComparer.Ordinal);
        return id => byId.TryGetValue(id, out var row) ? row : null;
    }

    [Fact]
    public void Clean_content_produces_no_findings_at_all()
    {
        // Proves the lints are not simply firing on everything.
        var atoms = new[] { Atom("atom.ember", 1, 10), Atom("atom.ember", 2, 20) };
        var container = Container("item.ring", "atom.ember.t1", "atom.ember.t2");

        var report = ContentValidation.Lint(atoms, new[] { container });

        Assert.Empty(report.Findings);
    }
}
