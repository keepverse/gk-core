using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.9 (spec-delve-quests.md §1) — `QuestCatalog.Load`: the nine real templates round-trip,
/// a red test per malformed row, `countBand`/`rewardBand`/`scope` resolve against the real registries
/// (`BandCatalog`/`ObjectiveTemplateCatalog`, both already configured for the whole assembly by
/// `Dungeon.DungeonHubTestBootstrap`'s module initializer — no bootstrap of my own needed).</summary>
public class QuestCatalogTests
{
    static readonly IReadOnlyList<ObjectiveTemplateDef> Templates = ObjectiveTemplateCatalog.All;
    static readonly IReadOnlyList<string> Scopes = BandCatalog.Get("questScope").Members;
    static readonly IReadOnlyList<string> RewardBands = BandCatalog.Get("rewardBand").Members;
    static readonly IReadOnlyList<string> CountBandMembers = BandCatalog.Get("countBand").Members;

    static int NoStatus(string id) => -1; // no real status catalog needed for these tests
    static readonly Func<string, int> StatusBit = NoStatus;

    static QuestCatalogLoad Load(params QuestRow[] rows) =>
        QuestCatalog.Load(rows, Templates, Scopes, RewardBands, CountBandMembers, StatusBit);

    // A real, valid row per template -- the fixture "the nine templates round-trip" builds against.
    static readonly IReadOnlyDictionary<string, QuestRow> ValidByTemplate = new Dictionary<string, QuestRow>(StringComparer.Ordinal)
    {
        ["explore-rooms"] = new("quest.explore", "explore-rooms", null, "many", "modest", "delve", null),
        ["cleanse-fights"] = new("quest.cleanse", "cleanse-fights", "fight", "few", "fair", "delve", null),
        ["gather-curio-kind"] = new("quest.gather", "gather-curio-kind", "shrine", "several", "modest", "delve", null),
        ["kill-boss"] = new("quest.kill-boss", "kill-boss", null, null, "rich", "delve", null),
        ["extract-with-item-kind"] = new("quest.extract", "extract-with-item-kind", "weapon", null, "fair", "delve", null),
        ["bring-creature-home-alive"] = new("quest.bring-home", "bring-creature-home-alive", null, null, "modest", "roster", null),
        ["finish-under-hunger"] = new("quest.hunger", "finish-under-hunger", null, null, "fair", "delve", null),
        ["survive-no-downed"] = new("quest.survive", "survive-no-downed", null, null, "fair", "delve", null),
        ["spend-no-provision"] = new("quest.spend-no-provision", "spend-no-provision", null, null, "modest", "delve", null),
    };

    // ---- the real registries, sanity-pinned ----

    [Fact]
    public void The_real_registry_carries_exactly_the_nine_templates_the_spec_names()
    {
        var ids = Templates.Select(t => t.ObjectiveTemplateId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expected = new[]
        {
            "bring-creature-home-alive", "cleanse-fights", "explore-rooms", "extract-with-item-kind",
            "finish-under-hunger", "gather-curio-kind", "kill-boss", "spend-no-provision", "survive-no-downed",
        }.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, ids);
    }

    [Fact]
    public void The_real_registry_carries_the_spec_own_questScope_and_rewardBand_vocabularies()
    {
        Assert.Equal(new[] { "delve", "domain", "roster" }, Scopes);
        Assert.Equal(new[] { "modest", "fair", "rich" }, RewardBands);
    }

    [Fact]
    public void CountBands_real_shipped_members_are_lone_few_several_many_not_the_specs_own_stale_prose()
    {
        // spec-delve-quests.md §Tunables and spec-dungeon-seed-contract.md §1.5 both cite
        // "few/some/most/all" -- the REAL shipped bands.v1.json (this program's own authority) says
        // lone/few/several/many, the same vocabulary capture.statusBonusMilli already uses (D4.6).
        // This loader is immune to the drift by construction (it never hardcodes a member name), so
        // this test exists only to name the drift, not because the loader needs fixing for it.
        Assert.Equal(new[] { "lone", "few", "several", "many" }, CountBandMembers);
    }

    // ---- the nine templates round-trip ----

    [Fact]
    public void All_nine_real_templates_round_trip_with_zero_rejections()
    {
        var rows = ValidByTemplate.Values.ToArray();
        var result = QuestCatalog.Load(rows, Templates, Scopes, RewardBands, CountBandMembers, StatusBit);

        Assert.Empty(result.Rejections);
        Assert.Equal(9, result.Catalog.Count);
        foreach (var row in rows)
            Assert.Equal(row, result.Catalog.Resolve(row.QuestId));
    }

    [Fact]
    public void A_quest_with_a_null_predicate_is_always_eligible()
    {
        var row = ValidByTemplate["kill-boss"];
        var result = Load(row);
        Assert.Same(PredicateCompiler.Always, result.Catalog.PredicateFor(row.QuestId));
    }

    [Fact]
    public void Resolve_of_an_unknown_id_returns_null_never_throws()
    {
        var result = Load(ValidByTemplate["kill-boss"]);
        Assert.Null(result.Catalog.Resolve("quest.does-not-exist"));
    }

    // ---- a red test per malformed row ----

    [Fact]
    public void A_duplicate_quest_id_refuses_by_name()
    {
        var row = ValidByTemplate["kill-boss"];
        var result = Load(row, row with { });
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.DuplicateId));
        Assert.Equal(1, result.Catalog.Count);
    }

    [Fact]
    public void An_unknown_template_id_refuses_by_name()
    {
        var row = ValidByTemplate["kill-boss"] with { TemplateId = "not-a-real-template" };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.BadTemplate));
        Assert.Equal(0, result.Catalog.Count);
    }

    [Fact]
    public void A_room_kind_template_without_a_targetRef_refuses_by_name()
    {
        var row = ValidByTemplate["cleanse-fights"] with { TargetRef = null };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.TargetRefRequired));
    }

    [Fact]
    public void A_none_kind_template_with_a_targetRef_refuses_by_name()
    {
        var row = ValidByTemplate["kill-boss"] with { TargetRef = "boss-1" };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.TargetRefNotAllowed));
    }

    [Fact]
    public void A_countable_template_without_a_countBand_refuses_by_name()
    {
        var row = ValidByTemplate["explore-rooms"] with { CountBand = null };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.CountBandRequired));
    }

    [Fact]
    public void One_of_the_six_count_less_templates_with_a_countBand_refuses_by_name()
    {
        var row = ValidByTemplate["kill-boss"] with { CountBand = "few" };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.CountBandNotAllowed));
    }

    [Fact]
    public void An_unknown_countBand_member_refuses_by_name()
    {
        var row = ValidByTemplate["explore-rooms"] with { CountBand = "not-a-real-band" };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.BadCountBand));
    }

    [Fact]
    public void An_unknown_rewardBand_refuses_by_name()
    {
        var row = ValidByTemplate["kill-boss"] with { RewardBand = "legendary" };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.BadRewardBand));
    }

    [Fact]
    public void An_unknown_scope_refuses_by_name()
    {
        var row = ValidByTemplate["kill-boss"] with { Scope = "world" };
        var result = Load(row);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(QuestRules.BadScope));
    }

    [Fact]
    public void A_row_that_fails_to_compile_its_predicate_refuses_and_is_not_added()
    {
        var badTree = new PredicateNode.Leaf(LeafId.HasStatus, Subject.Self); // no Text set -- HasStatus needs a named id
        var row = ValidByTemplate["kill-boss"] with { Predicate = badTree };
        var result = Load(row);
        Assert.NotEmpty(result.Rejections);
        Assert.Equal(0, result.Catalog.Count);
    }

    [Fact]
    public void Six_bad_rows_and_three_good_rows_in_one_call_yield_six_rejections_and_three_catalog_entries()
    {
        var good = new[] { ValidByTemplate["kill-boss"], ValidByTemplate["explore-rooms"], ValidByTemplate["survive-no-downed"] };
        var bad = new[]
        {
            ValidByTemplate["kill-boss"] with { QuestId = "quest.bad-1", TemplateId = "nope" },
            ValidByTemplate["kill-boss"] with { QuestId = "quest.bad-2", TargetRef = "x" },
            ValidByTemplate["explore-rooms"] with { QuestId = "quest.bad-3", CountBand = null },
            ValidByTemplate["kill-boss"] with { QuestId = "quest.bad-4", CountBand = "few" },
            ValidByTemplate["kill-boss"] with { QuestId = "quest.bad-5", RewardBand = "nope" },
            ValidByTemplate["kill-boss"] with { QuestId = "quest.bad-6", Scope = "nope" },
        };
        var result = Load(good.Concat(bad).ToArray());
        Assert.Equal(6, result.Rejections.Count);
        Assert.Equal(3, result.Catalog.Count);
    }
}
