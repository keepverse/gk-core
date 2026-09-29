using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T37 (`item-upgrade-tree`) — the executor's three mandatory rules, per
/// `spec-item-upgrade-tree.md` § 2a (affix legality, implicit presented), § 4 (requirement before
/// consume) and § 5 (no private failure chance), against the mechanism the owner ruled on 2026-09-21:
/// a successor is an **authored per-base-type edge**, never a class-ladder derivation.
/// </summary>
public class ItemUpgradePolicyTests
{
    static readonly IReadOnlySet<string> ClothPool = new HashSet<string> { "armour-cloth", "shared" };
    static readonly IReadOnlySet<string> LeatherPool = new HashSet<string> { "armour-leather", "shared" };

    static ItemUpgradeNode Node(
        string id, string ladder, string frame, int rung, int rungCount, string implicitFamily,
        string? successorOf = null, IReadOnlySet<string>? pool = null) =>
        new(id, ladder, frame, rung, rungCount, implicitFamily, successorOf, pool ?? LeatherPool);

    static IReadOnlyDictionary<string, ItemUpgradeNode> Catalog(params ItemUpgradeNode[] nodes) =>
        nodes.ToDictionary(n => n.BaseTypeId, StringComparer.Ordinal);

    static ItemUpgradeNode ClothFooting(string? successorOf = "humanoid-footing-leather") =>
        Node("humanoid-footing-cloth", "armour", "humanoid", 1, 4, "implicit.cloth", successorOf, ClothPool);

    static ItemUpgradeNode LeatherFooting() =>
        Node("humanoid-footing-leather", "armour", "humanoid", 2, 4, "implicit.leather");

    static ItemUpgradeRequest Request(
        ItemUpgradeNode? input = null,
        IReadOnlyList<ItemUpgradeAffix>? affixes = null,
        RequirementProfile? requirement = null,
        int ownerLevel = 1) =>
        new(input ?? ClothFooting(),
            affixes ?? new[] { new ItemUpgradeAffix("shared", "atom.swiftness", 3, 120) },
            requirement,
            ownerLevel,
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 0));

    // ── § 1 — the authored edge, one rung up, inside its own (ladder, frame) ──────────────────────

    [Fact]
    public void An_authored_edge_moves_the_item_exactly_one_rung_inside_its_own_ladder_and_frame()
    {
        var decision = ItemUpgradePolicy.Decide(Request(), Catalog(ClothFooting(), LeatherFooting()));

        Assert.True(decision.Ok, decision.RefusalDetail);
        var plan = decision.Plan!;
        Assert.Equal("humanoid-footing-leather", plan.Successor.BaseTypeId);
        Assert.Equal(2, plan.Successor.Rung);
        Assert.Equal("armour", plan.Successor.Ladder);
        Assert.Equal("humanoid", plan.Successor.Frame);
        Assert.Null(decision.RefusalCode);
    }

    [Fact]
    public void The_plan_names_both_implicits_so_the_card_can_show_the_change_before_the_input_is_consumed()
    {
        var decision = ItemUpgradePolicy.Decide(Request(), Catalog(ClothFooting(), LeatherFooting()));

        Assert.True(decision.Ok);
        Assert.Equal("implicit.cloth", decision.Plan!.OutgoingImplicitFamily);
        Assert.Equal("implicit.leather", decision.Plan.IncomingImplicitFamily);
    }

    // ── § 2 — carried affixes, never rerolled, dropped or re-tiered ───────────────────────────────

    [Fact]
    public void Every_affix_carries_across_identically_identity_tier_and_value()
    {
        var affixes = new[]
        {
            new ItemUpgradeAffix("shared", "atom.swiftness", 3, 120),
            new ItemUpgradeAffix("shared", "atom.grit", 2, 45),
        };

        var decision = ItemUpgradePolicy.Decide(Request(affixes: affixes), Catalog(ClothFooting(), LeatherFooting()));

        Assert.True(decision.Ok, decision.RefusalDetail);
        Assert.Equal(affixes, decision.Plan!.CarriedAffixes);
        // The plan's list is its own, so a caller cannot reach the input's affixes through it.
        Assert.NotSame(affixes, decision.Plan.CarriedAffixes);
    }

    [Fact]
    public void An_affix_the_successor_pool_cannot_roll_refuses_and_the_plan_is_null()
    {
        // The spec's own ruling example: carrying an `armour-cloth`-pooled affix onto a leather
        // chassis would produce a combination the drop path can never roll, and cloth would lose its
        // only compensation. Refuse — never drop the affix, never re-tier it into the new pool.
        var affixes = new[]
        {
            new ItemUpgradeAffix("armour-cloth", "atom.swiftness", 3, 120),
            new ItemUpgradeAffix("shared", "atom.grit", 2, 45),
        };

        var decision = ItemUpgradePolicy.Decide(Request(affixes: affixes), Catalog(ClothFooting(), LeatherFooting()));

        Assert.False(decision.Ok);
        Assert.Null(decision.Plan);
        Assert.Equal("upgrade.affix-illegal-on-successor", decision.RefusalCode);
        // Refused, never relabelled: no affix was dropped and none was re-tiered into the new pool.
        Assert.Contains("atom.swiftness", decision.RefusalDetail, StringComparison.Ordinal);
    }

    // ── § 4 — the requirement check is before the consume, and refuses rather than warns ──────────

    [Fact]
    public void A_requirement_the_owner_cannot_meet_refuses_and_consumes_nothing()
    {
        var requirement = new RequirementProfile(MinimumLevel: 20, BuildTrial: null, Upkeep: null,
            RequirementProfileKind.Level);

        var decision = ItemUpgradePolicy.Decide(
            Request(requirement: requirement, ownerLevel: 12),
            Catalog(ClothFooting(), LeatherFooting()));

        Assert.False(decision.Ok);
        Assert.Null(decision.Plan);
        Assert.Equal("upgrade.requirement-unmet", decision.RefusalCode);
        Assert.Contains("Level", decision.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_requirement_the_owner_meets_passes_through_to_the_plan()
    {
        var requirement = new RequirementProfile(MinimumLevel: 20, BuildTrial: null, Upkeep: null,
            RequirementProfileKind.Level);

        var decision = ItemUpgradePolicy.Decide(
            Request(requirement: requirement, ownerLevel: 20),
            Catalog(ClothFooting(), LeatherFooting()));

        Assert.True(decision.Ok, decision.RefusalDetail);
    }

    // ── § 5 and the ladder's own edges — every refusal path, by name ───────────────────────────────

    [Fact]
    public void A_base_type_with_no_authored_edge_refuses_by_name_never_by_deriving_from_the_ladder()
    {
        var weapon = Node("humanoid-blade-a", "weapon", "humanoid", 1, 3, "implicit.blade", successorOf: null);

        var decision = ItemUpgradePolicy.Decide(Request(input: weapon), Catalog(weapon));

        Assert.False(decision.Ok);
        Assert.Null(decision.Plan);
        Assert.Equal("upgrade.no-successor", decision.RefusalCode);
        Assert.Contains("author one", decision.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_top_rung_refuses_with_its_own_code_so_a_missing_edge_and_a_finished_ladder_stay_distinguishable()
    {
        var plate = Node("humanoid-footing-plate", "armour", "humanoid", 4, 4, "implicit.plate", successorOf: null);

        var decision = ItemUpgradePolicy.Decide(Request(input: plate), Catalog(plate));

        Assert.False(decision.Ok);
        Assert.Equal("upgrade.top-rung", decision.RefusalCode);
    }

    [Fact]
    public void Standard_commander_gear_is_refused_because_it_is_not_a_progression_ladder()
    {
        var standard = Node("humanoid-standard-a", "standard", "humanoid", 1, 2, "implicit.standard",
            successorOf: "humanoid-standard-b", pool: ClothPool);

        var decision = ItemUpgradePolicy.Decide(Request(input: standard), Catalog(standard));

        Assert.False(decision.Ok);
        Assert.Null(decision.Plan);
        Assert.Equal("upgrade.standard-refused", decision.RefusalCode);
    }

    [Fact]
    public void An_edge_that_jumps_two_rungs_refuses()
    {
        var leapfrog = Node("humanoid-footing-plate", "armour", "humanoid", 3, 4, "implicit.plate");
        var input = ClothFooting(successorOf: "humanoid-footing-plate");

        var decision = ItemUpgradePolicy.Decide(Request(input: input), Catalog(input, leapfrog));

        Assert.False(decision.Ok);
        Assert.Equal("upgrade.successor-not-next-rung", decision.RefusalCode);
    }

    [Fact]
    public void An_edge_naming_a_base_type_outside_the_catalog_refuses()
    {
        var input = ClothFooting(successorOf: "humanoid-footing-missing");

        var decision = ItemUpgradePolicy.Decide(Request(input: input), Catalog(input));

        Assert.False(decision.Ok);
        Assert.Equal("upgrade.successor-unknown", decision.RefusalCode);
    }

    [Fact]
    public void A_valid_base_type_with_an_empty_affix_set_still_upgrades()
    {
        var decision = ItemUpgradePolicy.Decide(
            Request(affixes: Array.Empty<ItemUpgradeAffix>()),
            Catalog(ClothFooting(), LeatherFooting()));

        Assert.True(decision.Ok, decision.RefusalDetail);
        Assert.Empty(decision.Plan!.CarriedAffixes);
    }

    // ── T38 — the same executor on weapon / offhand / jewel, one authored edge, never a ladder ─────

    [Theory]
    [InlineData("weapon", "humanoid-blade-a", "humanoid-blade-b")]
    [InlineData("offhand", "humanoid-focus-a", "humanoid-bulwark-a")]
    [InlineData("jewel", "plant-talisman-a", "plant-talisman-b")]
    public void An_authored_edge_on_a_weapon_offhand_or_jewel_ladder_upgrades_through_the_same_executor(
        string ladder, string inputId, string successorId)
    {
        // The class ladder for these three is a STYLE / category axis (blade→blunt→launcher is
        // melee→ranged; focus→bulwark is does-it-guard). The owner ruled 2026-09-21 that they upgrade
        // through an authored per-base-type edge instead, so the executor is the same one armour uses
        // — one lookup, no second executor — and the affix/implicit rules bind identically.
        var shared = new HashSet<string> { "shared", $"{ladder}-pool" };
        var input = Node(inputId, ladder, "humanoid", 1, 3, "implicit.old", successorId, shared);
        var successor = Node(successorId, ladder, "humanoid", 2, 3, "implicit.new", null, shared);

        var decision = ItemUpgradePolicy.Decide(
            Request(input: input, affixes: new[] { new ItemUpgradeAffix("shared", "atom.swiftness", 3, 120) }),
            Catalog(input, successor));

        Assert.True(decision.Ok, decision.RefusalDetail);
        Assert.Equal(successorId, decision.Plan!.Successor.BaseTypeId);
        Assert.Equal(ladder, decision.Plan.Successor.Ladder);
        Assert.Equal("implicit.old", decision.Plan.OutgoingImplicitFamily);
        Assert.Equal("implicit.new", decision.Plan.IncomingImplicitFamily);
    }

    [Fact]
    public void A_non_armour_base_type_without_an_edge_is_never_derived_from_its_class_ladder()
    {
        // The trap the ruling closes: the weapon ladder's next rung (`blunt`) EXISTS in the catalog and
        // would be a perfectly resolvable successor — the executor must still refuse, because
        // blade→blunt is melee→ranged, not power.
        var blade = Node("humanoid-blade-a", "weapon", "humanoid", 1, 3, "implicit.blade", successorOf: null);
        var blunt = Node("humanoid-blunt-a", "weapon", "humanoid", 2, 3, "implicit.blunt");

        var decision = ItemUpgradePolicy.Decide(Request(input: blade), Catalog(blade, blunt));

        Assert.False(decision.Ok);
        Assert.Null(decision.Plan);
        Assert.Equal("upgrade.no-successor", decision.RefusalCode);
        Assert.Contains("style axis", decision.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offhand_edge_still_obeys_the_affix_legality_rule()
    {
        var pool = new HashSet<string> { "offhand-pool" };
        var focus = Node("humanoid-focus-a", "offhand", "humanoid", 1, 2, "implicit.focus", "humanoid-bulwark-a", pool);
        var bulwark = Node("humanoid-bulwark-a", "offhand", "humanoid", 2, 2, "implicit.bulwark", null, pool);

        var decision = ItemUpgradePolicy.Decide(
            Request(input: focus, affixes: new[] { new ItemUpgradeAffix("weapon-pool", "atom.swiftness", 1, 10) }),
            Catalog(focus, bulwark));

        Assert.False(decision.Ok);
        Assert.Equal("upgrade.affix-illegal-on-successor", decision.RefusalCode);
    }

    [Fact]
    public void An_edge_across_frames_refuses_at_runtime_where_the_frame_is_what_can_be_read()
    {
        // The runtime reads frame and role only (`RpgStore.GetBaseType`), so the frame is the shape
        // check that actually binds there; a cross-LADDER edge is a content-time defect (T38's closure
        // check + ItemSeedValidator), covered by the rung-data case below.
        var otherFrame = Node("plant-footing-leather", "armour", "plant", 2, 4, "implicit.leather");
        var input = ClothFooting(successorOf: "plant-footing-leather");

        var decision = ItemUpgradePolicy.Decide(Request(input: input), Catalog(input, otherFrame));

        Assert.False(decision.Ok);
        Assert.Equal("upgrade.successor-not-same-frame", decision.RefusalCode);
    }

    [Fact]
    public void A_content_time_caller_that_declares_rung_data_still_gets_the_ladder_and_rung_checks()
    {
        var otherLadder = Node("humanoid-blade-b", "weapon", "humanoid", 2, 3, "implicit.blade");
        var input = ClothFooting(successorOf: "humanoid-blade-b");

        var decision = ItemUpgradePolicy.Decide(Request(input: input), Catalog(input, otherLadder));

        Assert.False(decision.Ok);
        Assert.Equal("upgrade.successor-not-same-ladder-and-frame", decision.RefusalCode);
    }

    [Fact]
    public void A_production_node_carries_no_rung_data_and_is_not_asked_to_invent_one()
    {
        // What the executor passes at runtime: the runtime cannot read a base type's class or band, so
        // LadderRungCount is 0 and the rung/ladder checks are skipped rather than failed. The ruling's
        // authority is the authored edge, so a no-rung-data node with an edge upgrades.
        var input = new ItemUpgradeNode("item.humanoid-footing-cloth-001", "", "humanoid", 0, 0,
            "", "item.humanoid-footing-leather-001", new HashSet<string>(), "footing");
        var successor = new ItemUpgradeNode("item.humanoid-footing-leather-001", "", "humanoid", 0, 0,
            "", null, new HashSet<string>(), "footing");

        var decision = ItemUpgradePolicy.Decide(
            new ItemUpgradeRequest(input, Array.Empty<ItemUpgradeAffix>(), null, 1,
                AptitudeAllocation.Single(AllocationScope.Commander, "Might", 0)),
            Catalog(input, successor));

        Assert.True(decision.Ok, decision.RefusalDetail);
        Assert.Null(decision.RefusalCode);
    }

    [Fact]
    public void Rule_1_reads_the_affix_family_role_allow_list_at_runtime()
    {
        // The production predicate (spec § 2a, resolved 2026-09-21): an affix is legal when its family's
        // own `roles` list contains the successor's role — the same filter the drop path applies.
        var families = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["atom.for-footing"] = new HashSet<string> { "footing" },
            ["atom.for-jewel"] = new HashSet<string> { "jewel-major" },
        };
        var legality = new RoleAllowListLegality(id => families.TryGetValue(id, out var r) ? r : new HashSet<string>());
        var input = ClothFooting();
        var successor = new ItemUpgradeNode("humanoid-footing-leather", "armour", "humanoid", 2, 4,
            "implicit.leather", null, new HashSet<string>(), "footing");

        var allowed = ItemUpgradePolicy.Decide(
            Request(affixes: new[] { new ItemUpgradeAffix("armour-cloth", "atom.for-footing", 2, 40) }),
            Catalog(input, successor), legality);
        var refused = ItemUpgradePolicy.Decide(
            Request(affixes: new[] { new ItemUpgradeAffix("armour-cloth", "atom.for-jewel", 2, 40) }),
            Catalog(input, successor), legality);

        Assert.True(allowed.Ok, allowed.RefusalDetail);
        Assert.False(refused.Ok);
        Assert.Equal("upgrade.affix-illegal-on-successor", refused.RefusalCode);
        Assert.Contains("affix-family-roles", refused.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_affix_family_the_predicate_cannot_read_is_a_refusal_never_a_pass()
    {
        var legality = new RoleAllowListLegality(_ => new HashSet<string>());
        var input = ClothFooting();
        var successor = new ItemUpgradeNode("humanoid-footing-leather", "armour", "humanoid", 2, 4,
            "implicit.leather", null, new HashSet<string>(), "footing");

        var decision = ItemUpgradePolicy.Decide(
            Request(affixes: new[] { new ItemUpgradeAffix("armour-cloth", "atom.unknown", 1, 1) }),
            Catalog(input, successor), legality);

        Assert.False(decision.Ok);
    }

    [Fact]
    public void The_decision_is_deterministic_no_private_failure_chance_and_no_reroll()
    {
        var catalog = Catalog(ClothFooting(), LeatherFooting());
        var request = Request();

        var first = ItemUpgradePolicy.Decide(request, catalog);
        var second = ItemUpgradePolicy.Decide(request, catalog);

        // `Decide` takes no RNG and no tuning: the same inputs give the same decision, so there is no
        // die to fail and nothing that could re-roll the affixes it carries (an upgrade's only risk is
        // the ladder's, in `craft-risk-ladder`). The plan's affix LIST is a fresh copy each call — by
        // design, so a caller cannot reach the input's affixes through it — so the content is compared.
        Assert.Equal(first.Ok, second.Ok);
        Assert.Equal(first.RefusalCode, second.RefusalCode);
        Assert.Equal(first.Plan!.Successor, second.Plan!.Successor);
        Assert.Equal(first.Plan.CarriedAffixes, second.Plan.CarriedAffixes);
    }
}
