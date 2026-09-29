using System.Reflection;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Uniques;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// spec-uniques.md / ssot-uniques.md G1, module 17. The per-row half: the class's structural facts,
/// the three hard devices, and the premise the whole class rests on — <b>a unique may break every rule
/// that lives in the generator, and no rule that lives in the machine.</b>
///
/// <para>Tuning is the REAL <c>gk-core/data/tuning/uniques.v1.json</c> in every test that reads one; the
/// synthetic containers are synthetic because a concrete unique container does not exist yet (the seed
/// corpus is 144 seeds, and rolling one is the runtime generator's job, not this module's).</para>
/// </summary>
public class UniqueTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    internal static UniqueTuning Tuning() =>
        UniqueTuning.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "uniques.v1.json")));

    // heirloom, from the shipped ladder: window t3-t5, count-band floor 1 + 2 = 3.
    static readonly RarityRungWindow Heirloom = new("heirloom", 3, 5, 3);
    static readonly RarityRungWindow Grafted = new("grafted", 1, 3, 1);

    static AtomRow Atom(
        string family, int tier, string kind = "stat.modify", string? paramsJson = null, string? whenJson = null) =>
        new()
        {
            AtomId = AtomRow.DeriveId(family, "", tier),
            KindId = kind,
            FamilyId = family,
            Tier = tier,
            ParamsJson = paramsJson ?? """{"channel":"maxHp","op":"Flat","amount":{"min":45,"max":45,"roll":"fixed"}}""",
            WhenJson = whenJson ?? "{}",
        };

    static ContainerRow Container(
        params ContainerAtomRow[] atoms) => new()
        {
            ContainerId = "item.kiln-nozzle",
            Kind = ContainerKind.Item,
            Slot = "armament-primary",
            Rarity = "heirloom",
            Atoms = atoms,
        };

    static UniqueRow Row(
        UniqueCounterPressure cp = UniqueCounterPressure.Narrow,
        long budgetAe = 0,
        UniqueAcquisition acq = UniqueAcquisition.SourceLocked) =>
        new("item.kiln-nozzle", "item.plant-muzzle-a-001", cp, budgetAe, "offense", acq);

    static Func<string, AtomRow?> Lookup(params AtomRow[] atoms)
    {
        var byId = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        return id => byId.TryGetValue(id, out var a) ? a : null;
    }

    static bool Fired(IReadOnlyList<AtomRejection> fails, string ruleId) =>
        fails.Any(f => f.Reason == AtomRejectionReason.ContentRuleViolated &&
                       f.Detail.Contains(ruleId, StringComparison.Ordinal));

    // ---------------------------------------------------------------------------------------------
    // G1's premise, against the SHIPPED validator
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole class turns on this: a fixed-core magnitude far outside the tier band loads clean,
    /// because <c>ContainerValidator</c> checks the window only inside the POOL loop and
    /// <c>ValidateOverrides</c> never compares a magnitude against a band.
    /// </summary>
    [Fact]
    public void A_fixed_core_atom_out_of_band_loads_clean()
    {
        var atom = Atom("atom.unique-kiln-heat", tier: 1);
        var c = Container(new ContainerAtomRow(1, atom.AtomId,
            // t1's shipped band tops out near 12; this is an order of magnitude past it.
            """{"amount":{"min":120,"max":138,"roll":"onInstantiate"}}"""))
            with { MinTier = 3, MaxTier = 3 };

        var check = ContainerValidator.Validate(c, Lookup(atom), _ => null);

        Assert.True(check.IsOk, check.Detail);
    }

    /// <summary>The same fact from the negative side: the identical tier IS refused from the pool.</summary>
    [Fact]
    public void Tier_out_of_window_fires_only_from_the_pool_loop()
    {
        var coreAtom = Atom("atom.unique-kiln-heat", tier: 1);
        var poolAtom = Atom("atom.searing-strike", tier: 1);
        var affix = new AffixRow("affix.searing-strike.t1", AffixClass.Suffix,
            new[] { new AffixRefRow(0, poolAtom.AtomId) });

        var c = Container(new ContainerAtomRow(1, coreAtom.AtomId)) with
        {
            MinTier = 3,
            MaxTier = 3,
            SuffixRolls = 1,
            Pool = new[] { new ContainerPoolRow(affix.AffixId, 10) },
        };

        var check = ContainerValidator.Validate(
            c, Lookup(coreAtom, poolAtom), id => id == affix.AffixId ? affix : null);

        Assert.Equal(AtomRejectionReason.TierOutOfWindow, check.Reason);
    }

    /// <summary><c>ValidateOverrides</c> checks well-formedness only — never a band.</summary>
    [Fact]
    public void An_override_is_never_band_checked_but_a_malformed_one_is_refused()
    {
        var atom = Atom("atom.vitality", tier: 4);

        var wellFormed = ContainerValidator.Validate(
            Container(new ContainerAtomRow(1, atom.AtomId, """{"amount":{"min":9000,"max":9000,"roll":"fixed"}}""")),
            Lookup(atom), _ => null);
        Assert.True(wellFormed.IsOk, wellFormed.Detail);

        var inverted = ContainerValidator.Validate(
            Container(new ContainerAtomRow(1, atom.AtomId, """{"amount":{"min":90,"max":9,"roll":"fixed"}}""")),
            Lookup(atom), _ => null);
        Assert.False(inverted.IsOk);
    }

    // ---------------------------------------------------------------------------------------------
    // Structural facts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_unique_is_never_promotable_even_though_every_rung_now_is()
    {
        // D7 lifted ssot-rarity rule 7 -- every rung promotes from a lower one now.
        foreach (var rung in RarityLadder.RungIds)
            Assert.Equal(1, RarityLadder.PromoteFrom(rung));

        // The class rule is structural and survives that lift.
        Assert.False(UniqueLimits.UniquesArePromotable);
    }

    [Fact]
    public void The_structural_limits_carry_their_agents_md_exemption_reason_in_code()
    {
        var xml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Uniques", "UniqueRow.cs"));

        // AGENTS.md: a structural limit is exempt from the no-hard-ceilings rule AND MUST SAY SO.
        Assert.Contains("STRUCTURAL", xml, StringComparison.Ordinal);
        Assert.Contains("not a progression ceiling", xml, StringComparison.Ordinal);
        // The L0 zero-weight comment is the one a reviewer will otherwise read as a coverage gap.
        Assert.Contains("there is no draw for a weight to modify", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Prefix_plus_suffix_rolls_above_one_is_shape_invalid_and_no_pool_rolls_column_is_read()
    {
        var atom = Atom("atom.vitality", tier: 4);
        var affix = new AffixRow("affix.a", AffixClass.Mixed, new[] { new AffixRefRow(0, atom.AtomId) });
        var c = Container(new ContainerAtomRow(1, atom.AtomId)) with
        {
            PrefixRolls = 1,
            SuffixRolls = 1,
            Pool = new[] { new ContainerPoolRow(affix.AffixId, 10) },
            MinTier = 4,
            MaxTier = 4,
        };

        var fails = UniqueValidator.Validate(
            Row(), c, Heirloom, 70, "armament-primary", Lookup(atom), Tuning());

        Assert.True(Fired(fails, UniqueRules.Shape));

        // "no code path reads a `pool_rolls` column" -- the column does not exist on the shipped row.
        Assert.Null(typeof(ContainerRow).GetProperty("PoolRolls", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(ContainerRow).GetProperty("PrefixRolls", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(ContainerRow).GetProperty("SuffixRolls", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Min_tier_must_equal_max_tier_when_a_variance_pool_exists()
    {
        var atom = Atom("atom.vitality", tier: 4);
        var affix = new AffixRow("affix.a", AffixClass.Suffix, new[] { new AffixRefRow(0, atom.AtomId) });
        var c = Container(new ContainerAtomRow(1, Atom("atom.unique-x", 3).AtomId)) with
        {
            MinTier = 3,
            MaxTier = 5,
            SuffixRolls = 1,
            Pool = new[] { new ContainerPoolRow(affix.AffixId, 10) },
        };

        var fails = UniqueValidator.Validate(
            Row(), c, Heirloom, 70, "armament-primary", Lookup(Atom("atom.unique-x", 3), atom), Tuning());

        Assert.True(Fired(fails, UniqueRules.Shape));
    }

    [Fact]
    public void An_instantiate_spread_wider_than_fifteen_percent_is_refused_and_exactly_fifteen_is_not()
    {
        var atom = Atom("atom.unique-kiln-heat", tier: 3);
        var t = Tuning();

        // midpoint 100, half-width 15 -> exactly 150 permille: allowed.
        var ok = UniqueValidator.Validate(
            Row(), Container(new ContainerAtomRow(1, atom.AtomId,
                """{"amount":{"min":85,"max":115,"roll":"onInstantiate"}}""")),
            Heirloom, 70, "armament-primary", Lookup(atom), t);
        Assert.False(Fired(ok, UniqueRules.Shape));

        // midpoint 100, half-width 30 -> 300 permille: refused.
        var bad = UniqueValidator.Validate(
            Row(), Container(new ContainerAtomRow(1, atom.AtomId,
                """{"amount":{"min":70,"max":130,"roll":"onInstantiate"}}""")),
            Heirloom, 70, "armament-primary", Lookup(atom), t);
        Assert.True(Fired(bad, UniqueRules.Shape));
    }

    [Fact]
    public void More_than_three_identity_atoms_is_shape_invalid_and_seq_zero_does_not_count()
    {
        var t = Tuning();
        var atoms = Enumerable.Range(0, 4).Select(i => Atom($"atom.unique-{i}", 3)).ToArray();

        // seq 0 is the base type's inherited base stat, plus three identity atoms = at the cap.
        var atCap = UniqueValidator.Validate(
            Row(), Container(atoms.Select((a, i) => new ContainerAtomRow(i, a.AtomId)).ToArray()),
            Heirloom, 70, "armament-primary", Lookup(atoms), t);
        Assert.False(Fired(atCap, UniqueRules.Shape));

        var fifth = atoms.Append(Atom("atom.unique-4", 3)).ToArray();
        var over = UniqueValidator.Validate(
            Row(), Container(fifth.Select((a, i) => new ContainerAtomRow(i, a.AtomId)).ToArray()),
            Heirloom, 70, "armament-primary", Lookup(fifth), t);
        Assert.True(Fired(over, UniqueRules.Shape));
    }

    // ---------------------------------------------------------------------------------------------
    // Device 1 — counter-pressure, checked against the content
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Counter_pressure_drawback_is_checked_against_content_not_trusted()
    {
        var t = Tuning();
        var positive = Atom("atom.unique-a", 3);
        var negative = Atom("atom.unique-brittle", 1,
            paramsJson: """{"channel":"maxHp","op":"Flat","amount":{"min":-60,"max":-60,"roll":"fixed"}}""");

        var lying = UniqueValidator.Validate(
            Row(UniqueCounterPressure.Drawback),
            Container(new ContainerAtomRow(1, positive.AtomId)),
            Heirloom, 70, "armament-primary", Lookup(positive), t);
        Assert.True(Fired(lying, UniqueRules.CounterPressure));

        var honest = UniqueValidator.Validate(
            Row(UniqueCounterPressure.Drawback),
            Container(new ContainerAtomRow(1, positive.AtomId), new ContainerAtomRow(2, negative.AtomId)),
            Heirloom, 70, "armament-primary", Lookup(positive, negative), t);
        Assert.False(Fired(honest, UniqueRules.CounterPressure));
    }

    /// <summary>
    /// Sign carries meaning PER KIND (definitions §2): the check asks the kind, it does not assume a
    /// negative number is a cost. A negative on a board kind is a malformed row, not a drawback.
    /// </summary>
    [Fact]
    public void A_negative_number_on_an_unsigned_kind_is_not_a_drawback()
    {
        var boardAtom = Atom("atom.unique-tile", 1, kind: "box.set",
            paramsJson: """{"boxType":-1}""");

        Assert.False(UniqueValidator.HasNegativeMagnitude(boardAtom, null));

        var statAtom = Atom("atom.unique-brittle", 1,
            paramsJson: """{"channel":"maxHp","op":"Flat","amount":{"min":-60,"max":-60,"roll":"fixed"}}""");
        Assert.True(UniqueValidator.HasNegativeMagnitude(statAtom, null));
    }

    [Fact]
    public void Counter_pressure_conditional_needs_a_real_predicate()
    {
        var t = Tuning();
        var bare = Atom("atom.unique-a", 3, whenJson: """{"trigger":"OnDamageDealt","chance":1000}""");
        var gated = Atom("atom.unique-b", 3, whenJson:
            """{"trigger":"OnDamageDealt","predicate":{"leaf":"hpBelowMilli","subject":"target","value":300}}""");

        var lying = UniqueValidator.Validate(
            Row(UniqueCounterPressure.Conditional), Container(new ContainerAtomRow(1, bare.AtomId)),
            Heirloom, 70, "armament-primary", Lookup(bare), t);
        Assert.True(Fired(lying, UniqueRules.CounterPressure));

        var honest = UniqueValidator.Validate(
            Row(UniqueCounterPressure.Conditional), Container(new ContainerAtomRow(1, gated.AtomId)),
            Heirloom, 70, "armament-primary", Lookup(gated), t);
        Assert.False(Fired(honest, UniqueRules.CounterPressure));
    }

    [Fact]
    public void Counter_pressure_narrow_is_measured_against_the_rung_baseline()
    {
        var t = Tuning();

        // heirloom: baseline 3 AE. 60% of 300 is 180 AE x 100. One t3 atom prices at
        // TierMidpoint(3)=45 against the reference tier 4 (midpoint 92) -> 48. Under.
        var small = Atom("atom.unique-small", 3);
        var under = UniqueValidator.Validate(
            Row(UniqueCounterPressure.Narrow), Container(new ContainerAtomRow(1, small.AtomId)),
            Heirloom, 70, "armament-primary", Lookup(small), t);
        Assert.False(Fired(under, UniqueRules.CounterPressure));

        // Three t5 atoms price at 187 each -> 561, well past 180.
        var big = Enumerable.Range(0, 3).Select(i => Atom($"atom.unique-big-{i}", 5)).ToArray();
        var over = UniqueValidator.Validate(
            Row(UniqueCounterPressure.Narrow),
            Container(big.Select((a, i) => new ContainerAtomRow(i + 1, a.AtomId)).ToArray()),
            Heirloom, 70, "armament-primary", Lookup(big), t);
        Assert.True(Fired(over, UniqueRules.CounterPressure));
    }

    /// <summary>
    /// §3.2's corollary and the spec's own success criterion: <b>a hand-authored item that only rolls
    /// higher numbers is not a unique — it is a rare with a name.</b> The refusal is not a fourth rule;
    /// it is that such an item cannot satisfy ANY of the three counter-pressure arms, whichever it
    /// declares. Asserted across all three rather than on one, because a class that could be forged by
    /// picking the right declaration would not be a class.
    /// </summary>
    [Fact]
    public void A_unique_that_only_rolls_higher_numbers_is_refused_whichever_arm_it_declares()
    {
        var t = Tuning();
        // Three fat, positive, unconditional raw-stat lines. No cost, no predicate, and far past 60%
        // of the rung baseline -- the definition of "a rare with a name".
        var bigger = Enumerable.Range(0, 3).Select(i => Atom($"atom.just-bigger-{i}", 5)).ToArray();
        var container = Container(bigger.Select((a, i) => new ContainerAtomRow(i + 1, a.AtomId)).ToArray());

        foreach (var arm in Enum.GetValues<UniqueCounterPressure>())
        {
            var fails = UniqueValidator.Validate(
                Row(arm), container, Heirloom, 70, "armament-primary", Lookup(bigger), t);
            Assert.True(Fired(fails, UniqueRules.CounterPressure), $"{arm} was not refused");
        }

        // And the budget catches it a second time, independently of the declaration.
        Assert.True(Fired(
            UniqueValidator.Validate(Row(UniqueCounterPressure.Drawback), container, Heirloom, 70,
                "armament-primary", Lookup(bigger), t),
            UniqueRules.Budget));
    }

    // ---------------------------------------------------------------------------------------------
    // Device 2 — the budget
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Budget_refuses_above_baseline_plus_one_point_five_ae()
    {
        var t = Tuning();
        Assert.Equal(150, t.BudgetPremiumAeHundredths);
        Assert.Equal(300, UniqueBudget.RungBaselineAeHundredths(Heirloom));
        Assert.Equal(450, UniqueBudget.AllowanceAeHundredths(Heirloom, t));

        // Three t5 identity atoms = 561, past 450.
        var big = Enumerable.Range(0, 3).Select(i => Atom($"atom.unique-big-{i}", 5)).ToArray();
        var fails = UniqueValidator.Validate(
            Row(UniqueCounterPressure.Conditional, budgetAe: 561),
            Container(big.Select((a, i) => new ContainerAtomRow(i + 1, a.AtomId)).ToArray()),
            Heirloom, 70, "armament-primary", Lookup(big), t);

        Assert.True(Fired(fails, UniqueRules.Budget));
    }

    [Fact]
    public void The_declared_budget_must_agree_with_the_summed_content_in_both_directions()
    {
        var t = Tuning();
        var atom = Atom("atom.unique-a", 4);   // t4 midpoint 92 against reference tier 4 -> 100.
        var container = Container(new ContainerAtomRow(1, atom.AtomId));

        foreach (var (declared, shouldFire) in new[] { (100L, false), (124L, false), (200L, true), (10L, true) })
        {
            var fails = UniqueValidator.Validate(
                Row(UniqueCounterPressure.Narrow, budgetAe: declared), container,
                Heirloom, 70, "armament-primary", Lookup(atom), t);
            Assert.Equal(shouldFire, Fired(fails, UniqueRules.Budget));
        }
    }

    [Fact]
    public void The_drift_tolerance_is_definitions_seven_s_shared_number_and_a_copy_that_drifts_throws()
    {
        Assert.Equal(ContentValidation.DriftTolerancePercent, Tuning().BudgetDriftTolerancePercent);

        var drifted = """
            {"rungFloorOrdinal":30,"maxIdentityAtoms":3,"identitySpreadPerMille":150,
             "budgetPremiumAeHundredths":150,"budgetDriftTolerancePercent":40,"narrowCeilingPerMille":600,
             "maxRolesPerFrame":8,"forbiddenRoles":["jewel-minor-a"],"parityLowerBoundPerMille":250,
             "parityUpperBoundPerMille":750,"outOfBandMagnitudeCapPerMille":1500}
            """;
        Assert.Throws<UniqueTuningRejection>(() => UniqueTuning.Parse(drifted));
    }

    // ---------------------------------------------------------------------------------------------
    // Rung eligibility, reachability, roles, sets
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_rung_below_the_floor_is_ineligible_and_the_key_is_derived_not_authored()
    {
        var t = Tuning();
        Assert.False(t.IsRungEligible(10));
        Assert.False(t.IsRungEligible(20));
        // D4.23 moved the real floor 30 -> 80 (spec-unique-pipeline.md §5 point 5) -- boundary values
        // read off the real tuning's own floor rather than a hardcoded 30, so this stays correct
        // whichever ordinal a future balance pass authors here.
        Assert.False(t.IsRungEligible(t.RungFloorOrdinal - 10));
        Assert.True(t.IsRungEligible(t.RungFloorOrdinal));
        Assert.True(t.IsRungEligible(t.RungFloorOrdinal + 100));

        var atom = Atom("atom.unique-a", 2);
        var fails = UniqueValidator.Validate(
            Row(), Container(new ContainerAtomRow(1, atom.AtomId)),
            Grafted with { RarityId = "sprout" }, 20, "armament-primary", Lookup(atom), t);
        Assert.True(Fired(fails, UniqueRules.RungIneligible));
    }

    /// <summary>D4.23 (spec-unique-pipeline.md §5 point 5) — "the 95 are enabled: false ... never
    /// re-runged, never deleted": a disabled row below the floor is NOT flagged ineligible, the exact
    /// exemption this task added to this check.</summary>
    [Fact]
    public void A_disabled_rung_below_the_floor_is_not_flagged_ineligible()
    {
        var t = Tuning();
        var atom = Atom("atom.unique-a", 2);
        var fails = UniqueValidator.Validate(
            Row() with { Enabled = false }, Container(new ContainerAtomRow(1, atom.AtomId)),
            Grafted with { RarityId = "sprout" }, 20, "armament-primary", Lookup(atom), t);
        Assert.False(Fired(fails, UniqueRules.RungIneligible));
    }

    [Fact]
    public void Unique_eligible_is_registered_with_a_decided_shape_naming_this_module()
    {
        Assert.True(RarityBudgetKeys.IsRegistered(UniqueLimits.EligibilityBudgetKey));
        var def = RarityBudgetKeys.All.Single(k => k.Key == UniqueLimits.EligibilityBudgetKey);
        Assert.Equal("uniques (17)", def.ConsumerModule);
    }

    [Fact]
    public void Drop_acquisition_above_ordinal_ninety_is_unreachable_and_d7_did_not_change_it()
    {
        var t = Tuning();
        var atom = Atom("atom.unique-a", 4);
        var c = Container(new ContainerAtomRow(1, atom.AtomId));

        var refused = UniqueValidator.Validate(
            Row(acq: UniqueAcquisition.Drop), c, new RarityRungWindow("sunwoven", 4, 5, 4), 90,
            "armament-primary", Lookup(atom), t);
        Assert.True(Fired(refused, UniqueRules.Unreachable));

        foreach (var acq in new[] { UniqueAcquisition.SourceLocked, UniqueAcquisition.Deterministic })
        {
            var ok = UniqueValidator.Validate(
                Row(acq: acq), c, new RarityRungWindow("sunwoven", 4, 5, 4), 90,
                "armament-primary", Lookup(atom), t);
            Assert.False(Fired(ok, UniqueRules.Unreachable));
        }

        // And it is about ACQUISITION, not promotion: a plain drop at ordinal 80 is fine.
        var lowerRung = UniqueValidator.Validate(
            Row(acq: UniqueAcquisition.Drop), c, new RarityRungWindow("firstseed", 3, 5, 4), 80,
            "armament-primary", Lookup(atom), t);
        Assert.False(Fired(lowerRung, UniqueRules.Unreachable));
    }

    [Fact]
    public void No_unique_on_either_jewel_minor_role()
    {
        var t = Tuning();
        Assert.Equal(2, t.ForbiddenRoles.Count);
        Assert.Contains("jewel-minor-a", t.ForbiddenRoles);
        Assert.Contains("jewel-minor-b", t.ForbiddenRoles);

        var atom = Atom("atom.unique-a", 4);
        foreach (var role in t.ForbiddenRoles)
        {
            var fails = UniqueValidator.Validate(
                Row(), Container(new ContainerAtomRow(1, atom.AtomId)), Heirloom, 70, role, Lookup(atom), t);
            Assert.True(Fired(fails, UniqueRules.RoleForbidden));
        }
    }

    [Fact]
    public void A_unique_may_not_be_a_set_member()
    {
        var atom = Atom("atom.unique-a", 4);
        var fails = UniqueValidator.Validate(
            Row(), Container(new ContainerAtomRow(1, atom.AtomId)), Heirloom, 70, "armament-primary",
            Lookup(atom), Tuning(), isSetMember: true);

        Assert.True(Fired(fails, UniqueRules.SetMembership));
    }

    // ---------------------------------------------------------------------------------------------
    // Reason codes, ids, and the two "never" boundaries
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_unique_rule_raises_one_code_with_a_namespaced_rule_id()
    {
        UniqueRules.EnsureRegistered();

        var ruleIds = typeof(UniqueRules)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name != nameof(UniqueRules.Namespace))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(ruleIds);
        foreach (var id in ruleIds)
        {
            Assert.StartsWith("unique.", id, StringComparison.Ordinal);
            Assert.True(ContentRuleNamespaces.IsRegistered(id), id);
            Assert.Equal(AtomRejectionReason.ContentRuleViolated, AtomRejection.ContentRule(id, "x").Reason);
        }
    }

    /// <summary>
    /// spec-uniques.md's "Ask first" list wanted <c>ContentRuleViolated</c> added to a 34-member enum.
    /// It is already there, and the closed list must not grow a member for this module either.
    /// </summary>
    [Fact]
    public void No_reason_code_is_minted_for_this_module()
    {
        var names = Enum.GetNames<AtomRejectionReason>();
        Assert.Contains(nameof(AtomRejectionReason.ContentRuleViolated), names);
        // AtomRejectionReason is pinned once, AtomKindRegistryTests (population-pin SE3.3,
        // 2026-09-19); reads that same live enum length, never a second literal.
        Assert.Equal(Enum.GetValues<AtomRejectionReason>().Length, names.Length);
        Assert.DoesNotContain(names, n => n.StartsWith("Unique", StringComparison.Ordinal));
    }

    [Fact]
    public void The_seed_id_and_the_container_id_convert_both_ways_and_a_bad_one_throws()
    {
        Assert.Equal("item.ember-harvest-30-001", UniqueContainerIds.FromSeedId("unique.ember-harvest-30-001"));
        Assert.Equal("unique.ember-harvest-30-001", UniqueContainerIds.ToSeedId("item.ember-harvest-30-001"));

        Assert.Throws<ArgumentException>(() => UniqueContainerIds.FromSeedId("item.already-a-container"));
        Assert.Throws<ArgumentException>(() => UniqueContainerIds.FromSeedId("unique.Not Kebab"));
    }

    /// <summary>
    /// The design's own load-bearing sentence (§6.4): <b>Instantiator needs no unique branch.</b> If it
    /// grows one, the class stopped being data.
    /// </summary>
    [Fact]
    public void Instantiator_has_no_unique_branch()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "FusionRpg.Core", "Effects", "Atoms", "Instantiator.cs"));

        Assert.DoesNotContain("Unique", source, StringComparison.Ordinal);
        Assert.DoesNotContain("unique", source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// spec-uniques.md's "Never" list: no new container kind, no new atom kind.
    ///
    /// <para>⚠ The lane's own figure is stale and is NOT asserted here: ssot-uniques.md §4.3 and
    /// spec-uniques.md both say <c>AtomKindRegistry.KindCount = 12</c>, and the shipped registry is
    /// <b>18</b>. That is the vocabulary growing under other lanes, not a defect — what matters to
    /// this module is that <c>damage.convert</c> (§4.3's named 13th-kind request, still blocked on a
    /// damage applier) is not among them and that nothing HERE added a kind.</para>
    ///
    /// <para>⚠ Superseded 2026-09-07: a conversion-shaped kind now DOES exist —
    /// <c>element.convert</c> (D56, spec-element-conversion.md, `AttachPoint.Element`) — added by
    /// passive-tree, nothing to do with this lane's own long-blocked <c>damage.convert</c> ask. The
    /// broad "no kind id contains 'convert'" check this test used to carry is retired along with it
    /// (kept as the historical record above, not deleted): it was always a proxy for "damage.convert
    /// specifically doesn't exist," and a real, reviewed, differently-scoped conversion kind landing
    /// elsewhere is not a violation of THIS module's own "Never" list.</para>
    /// </summary>
    [Fact]
    public void This_module_adds_no_container_kind_and_no_atom_kind()
    {
        // 7, not 6, since party-dungeon D2.6 (spec-encounter-generator.md §6) added ContainerKind.Enemy
        // as its own reviewed seventh kind -- this test's own claim is "the UNIQUES module adds
        // neither," which still holds; the total just moved for an unrelated, reviewed reason.
        // 7 -> 11 (2026-09-07): container-kind-expansion (X7) added Gem/Charm/Combo/Consumable --
        // item's own four, still none of them this module's, so the claim still holds.
        // 11 -> 13 (achievement-title T3): EmpireTitle/ActorTitle are achievement-title's own two.
        // 13 -> 14 (empire-development Task 1.3a, merged 2026-09-16): ContainerKind.Relic,
        // loam-relics-and-wonders' own reviewed kind. Still none of the three this module's, so the
        // claim the assertion protects still holds.
        // 14 -> 15 (species-progression SP3.2): ContainerKind.SpeciesProgression, likewise none of
        // the three this module's own.
        Assert.Equal(15, Enum.GetNames<ContainerKind>().Length);
        Assert.Equal(AtomKindRegistry.KindCount, AtomKindRegistry.All.Count);
        Assert.Null(AtomKindRegistry.Get("damage.convert"));
    }

    /// <summary>
    /// ⭐ ssot-uniques.md §4.3 and §9.14 call the D6 quarantine *"the largest single constraint on what
    /// this lane can author, larger than SC2"* — every <c>combat.*</c> channel binding nowhere. It is
    /// <b>lifted</b>: <c>stat.derived</c> is Full on the lawn AND in battle. Asserted rather than
    /// assumed, because the lane doc still says otherwise and a builder reading only the lane would
    /// author around a wall that is gone.
    ///
    /// <para>Sim moved too: mechanism-wiring E5 (2026-09-06) opened `None → Partial` — the fold
    /// (`ActorDerivedLookup`) honoured `Flat`/`Increased` and not `Replace`/`Flag`
    /// (`EffectOfflineKitTests.The_four_derived_ops_decide_Full_versus_Partial`) — then sim-hub-parity
    /// (T15, 2026-09-13) closed the gap to `Full`. Renamed from `..._is_still_refused_for_sim`, then
    /// from `..._and_partially_in_sim`, neither of which stayed true.</para>
    /// </summary>
    [Fact]
    public void A_stat_derived_identity_atom_binds_fully_on_lawn_battle_and_sim()
    {
        var derived = AtomKindRegistry.Get("stat.derived");
        Assert.NotNull(derived);
        Assert.Equal(RuntimeState.Full, derived!.SupportIn(RuntimeId.Lawn));
        Assert.Equal(RuntimeState.Full, derived.SupportIn(RuntimeId.Battle));

        // Sim opened to Partial (mechanism-wiring E5, 2026-09-06) once ActorDerivedLookup's
        // contribution fold gave it a real, if partial, consumer, then to Full (sim-hub-parity, T15,
        // 2026-09-13) once that fold routed through the real DerivedComposer instead of a plain
        // OverlayAdd sum.
        Assert.Equal(RuntimeState.Full, derived.SupportIn(RuntimeId.Sim));
    }

    /// <summary>
    /// ⏸ D39 (*"add Override, this is funny feature"*) is <b>not landed</b>, and the spec is explicit
    /// that the op must not land without its consumer. Pinned so "we added it" and "we did not" stay
    /// distinguishable, and so the day it lands this test is the reminder that the damage applier is
    /// part of the same ask.
    /// </summary>
    [Fact]
    public void D39s_override_op_and_the_thirteenth_kind_are_both_still_absent()
    {
        Assert.Equal(new[] { "flat", "increased", "more" }, AtomRowValidator.StatOps);
        Assert.Null(AtomKindRegistry.Get("damage.convert"));
    }

    // ---------------------------------------------------------------------------------------------
    // Device 3 — parity, and the one simulator
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// spec-uniques.md: <b>"Never write a second parity simulator."</b> Asserted structurally — this
    /// module declares no seed, no roll count, no tier band table and no RNG of its own, and every
    /// number it reports comes out of module 7's harness.
    ///
    /// <para><b>One narrow, named exemption (D4.26):</b> <c>ExtendSlotRoll.cs</c> reads
    /// <c>SeededRng</c> directly for a real GAMEPLAY roll gate (the rung-80 extend-action-slot draw)
    /// — not a second pricing/parity simulator. It declares no seed of its own (takes the caller's),
    /// no roll-count and no tier-band table; it is one boolean gate, structurally identical to the
    /// dozens of other `SeededRng.DeriveStream` call sites elsewhere in this codebase, and it exists
    /// only because `IAtomRandom.NextPerMille` tops out at 1000 and cannot express the tunable's
    /// 100-per-million rate (confirmed by a dedicated search: zero other per-million integer gates
    /// exist anywhere yet). A narrow named exemption, not a wildcard — the next file that wants this
    /// exemption needs its own justification, not just this comment's precedent.</para>
    /// </summary>
    [Fact]
    public void No_second_simulator_exists_in_this_module()
    {
        var dir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Uniques");
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            if (Path.GetFileName(file) == "ExtendSlotRoll.cs") continue;

            var src = File.ReadAllText(file);
            Assert.DoesNotContain("SeededRng", src, StringComparison.Ordinal);
            Assert.DoesNotContain("new Random", src, StringComparison.Ordinal);
        }

        // And the magnitudes it prices against are the harness's own table, not a copy.
        Assert.Equal(5, RarityOverlapSimulator.TierCount);
        Assert.Equal((170, 205), RarityOverlapSimulator.TierBand(5));
        Assert.Equal(187, RarityOverlapSimulator.TierMidpoint(5));
    }

    /// <summary>
    /// §9.2's ask, literally: the same measurement with a fixed-value item on one side. A magnitude
    /// below the rung's whole window is beaten every time; one above it, never.
    /// </summary>
    [Fact]
    public void Parity_is_the_harness_with_a_fixed_value_on_one_side()
    {
        var t = Tuning();
        var rung = new RarityRungWindow("heirloom", 3, 5, 3);

        Assert.Equal(1000, UniqueParityMetric.MeasurePerMille(rung, 0));
        Assert.Equal(0, UniqueParityMetric.MeasurePerMille(rung, 10_000));

        Assert.Equal(UniqueParityVerdict.Trophy, UniqueParityMetric.VerdictOf(1000, t));
        Assert.Equal(UniqueParityVerdict.StrictlyBetter, UniqueParityMetric.VerdictOf(0, t));
        Assert.Equal(UniqueParityVerdict.InBand, UniqueParityMetric.VerdictOf(500, t));
    }

    /// <summary>
    /// The rolled side draws ONE affix — parity is measured within one channel family, and the
    /// one-atom-per-group rule means a single family's total is one affix however many the rung draws.
    /// </summary>
    [Fact]
    public void The_parity_window_draws_one_affix_whatever_the_rungs_count_band_is()
    {
        foreach (var count in new[] { 0, 1, 3, 5 })
            Assert.Equal(1, UniqueParityMetric.SingleFamilyWindow(new RarityRungWindow("x", 3, 5, count)).AffixCount);
    }

    [Fact]
    public void The_parity_report_now_declares_a_threshold_because_the_harness_exists()
    {
        var t = Tuning();
        var report = UniqueParityMetric.Measure(
            Array.Empty<UniqueSeed>(), _ => null, t);

        Assert.True(report.HasThreshold);
        Assert.Equal(t.ParityLowerBoundPerMille, report.LowerBoundPerMille);
        Assert.Equal(t.ParityUpperBoundPerMille, report.UpperBoundPerMille);
        Assert.Contains("RarityOverlapSimulator", report.Basis, StringComparison.Ordinal);
        Assert.Contains("No second simulator", report.Basis, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Tuning parser — it refuses rather than defaults
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("rungFloorOrdinal")]
    [InlineData("maxIdentityAtoms")]
    [InlineData("identitySpreadPerMille")]
    [InlineData("budgetPremiumAeHundredths")]
    [InlineData("narrowCeilingPerMille")]
    [InlineData("maxRolesPerFrame")]
    [InlineData("forbiddenRoles")]
    [InlineData("parityLowerBoundPerMille")]
    [InlineData("parityUpperBoundPerMille")]
    [InlineData("outOfBandMagnitudeCapPerMille")]
    public void Stripping_any_key_from_the_real_tuning_file_throws_rather_than_defaulting(string key)
    {
        var path = Path.Combine(RepoRoot(), "data", "tuning", "uniques.v1.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

        using var buffer = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!string.Equals(p.Name, key, StringComparison.Ordinal))
                    p.WriteTo(w);
            w.WriteEndObject();
        }

        Assert.Throws<UniqueTuningRejection>(() =>
            UniqueTuning.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray())));
    }

    [Fact]
    public void An_inverted_parity_band_and_an_unknown_forbidden_role_are_both_refused_at_load()
    {
        const string inverted = """
            {"rungFloorOrdinal":30,"maxIdentityAtoms":3,"identitySpreadPerMille":150,
             "budgetPremiumAeHundredths":150,"budgetDriftTolerancePercent":25,"narrowCeilingPerMille":600,
             "maxRolesPerFrame":8,"forbiddenRoles":["jewel-minor-a"],"parityLowerBoundPerMille":800,
             "parityUpperBoundPerMille":250,"outOfBandMagnitudeCapPerMille":1500}
            """;
        Assert.Throws<UniqueTuningRejection>(() => UniqueTuning.Parse(inverted));

        const string ghostRole = """
            {"rungFloorOrdinal":30,"maxIdentityAtoms":3,"identitySpreadPerMille":150,
             "budgetPremiumAeHundredths":150,"budgetDriftTolerancePercent":25,"narrowCeilingPerMille":600,
             "maxRolesPerFrame":8,"forbiddenRoles":["trinket"],"parityLowerBoundPerMille":250,
             "parityUpperBoundPerMille":750,"outOfBandMagnitudeCapPerMille":1500}
            """;
        Assert.Throws<UniqueTuningRejection>(() => UniqueTuning.Parse(ghostRole));
    }
}
