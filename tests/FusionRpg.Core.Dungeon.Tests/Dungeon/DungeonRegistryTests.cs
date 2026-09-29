using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using Xunit;

namespace FusionRpg.Core.Tests.Dungeon;

/// <summary>D1.1/D1.2 — the nine registry files, their loader and their catalogs
/// (spec-dungeon-registries.md). Reads the real, shipped
/// <c>gk-data/packs/fusion/data/seed/dungeon/_registry/*.json</c> — a fixture copy could drift from what ships.</summary>
public class DungeonRegistryTests
{
    static DungeonRegistries LoadReal() => DungeonRegistryLoader.LoadAll(DungeonTestFiles.RegistryDir());

    [Fact]
    public void All_nine_registry_files_parse_and_validate()
    {
        var registries = LoadReal();
        // Canonical site (population-pin SE3.3, 2026-09-19) -- each count is owned by the real shipped
        // registry file it is read from (gk-data/packs/fusion/data/seed/dungeon/_registry/*.json, a hand-authored closed
        // set per AGENTS.md, not a `gk-core/data/tuning/*.json` the `immutable` marker kind is scoped to);
        // Configuring_the_hub_wires_every_one_of_the_nine_catalogs below compares its own catalog
        // counts against THESE, never a second copy of the same literal.
        // pin: closed-vocabulary RoomKindCatalog — a new room kind is a reviewed change
        Assert.Equal(11, registries.RoomKinds.Count);
        // pin: closed-vocabulary DoorKindCatalog — a new door kind is a reviewed change
        Assert.Equal(4, registries.DoorKinds.Count);
        // pin: closed-vocabulary OverrideTagCatalog — a new override tag is a reviewed change
        Assert.Equal(5, registries.OverrideTags.Count);
        // pin: closed-vocabulary ObjectiveTemplateCatalog — a new objective template is a reviewed change
        Assert.Equal(9, registries.ObjectiveTemplates.Count);
        // pin: closed-vocabulary DifficultyRungCatalog — a new difficulty rung is a reviewed change
        Assert.Equal(10, registries.DifficultyRungs.Count);
        // pin: closed-vocabulary DispositionCatalog — a new disposition is a reviewed change
        Assert.Equal(4, registries.Disposition.Count);
        // pin: closed-vocabulary InteractionVerbCatalog — a new interaction verb is a reviewed change
        Assert.Equal(6, registries.InteractionVerbs.Count);
        // pin: closed-vocabulary RaidModeCatalog — a new raid mode is a reviewed change
        Assert.Equal(3, registries.RaidModes.Count);
        // pin: closed-vocabulary BandCatalog — a new band is a reviewed change
        Assert.Equal(20, registries.Bands.Count);
    }

    [Fact]
    public void Every_id_across_every_registry_is_unique_and_lowercase_kebab()
    {
        var registries = LoadReal();
        bool IsKebab(string id) => id.Length > 0 && id == id.ToLowerInvariant()
            && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
            && id[0] != '-' && id[^1] != '-';

        foreach (var id in registries.RoomKinds.Select(r => r.RoomKindId)) Assert.True(IsKebab(id), id);
        foreach (var id in registries.DoorKinds.Select(r => r.DoorKindId)) Assert.True(IsKebab(id), id);
        foreach (var id in registries.OverrideTags) Assert.True(IsKebab(id), id);
        foreach (var id in registries.ObjectiveTemplates.Select(r => r.ObjectiveTemplateId)) Assert.True(IsKebab(id), id);
        foreach (var id in registries.DifficultyRungs.Select(r => r.RungId)) Assert.True(IsKebab(id), id);
        foreach (var id in registries.Disposition) Assert.True(IsKebab(id), id);
        foreach (var id in registries.InteractionVerbs.Select(r => r.VerbId)) Assert.True(IsKebab(id), id);
        foreach (var id in registries.RaidModes) Assert.True(IsKebab(id), id);
    }

    [Fact]
    public void Difficulty_rung_ordinals_are_1_to_10_contiguous()
    {
        var ordinals = LoadReal().DifficultyRungs.Select(r => r.Ordinal).OrderBy(o => o).ToList();
        Assert.Equal(Enumerable.Range(1, 10), ordinals);
    }

    [Fact]
    public void The_shipped_room_kind_ids_match_the_spec_exactly()
    {
        var ids = LoadReal().RoomKinds.Select(r => r.RoomKindId).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "boss", "cache", "curio", "elite", "fight", "merchant", "rest", "shrine", "trap", "unknown", "wild" }
                .OrderBy(x => x, StringComparer.Ordinal),
            ids);
    }

    [Fact]
    public void Exactly_one_room_kind_allows_the_boss_row()
    {
        Assert.Equal("boss", Assert.Single(LoadReal().RoomKinds.Where(r => r.BossRowAllowed)).RoomKindId);
    }

    [Fact]
    public void Only_unknown_carries_unknownResolvesTo_and_it_names_cache_merchant_fight()
    {
        var registries = LoadReal();
        foreach (var kind in registries.RoomKinds)
        {
            if (kind.RoomKindId == "unknown")
                Assert.Equal(new[] { "cache", "merchant", "fight" }, kind.UnknownResolvesTo);
            else
                Assert.Empty(kind.UnknownResolvesTo);
        }
    }

    [Fact]
    public void The_objective_templates_are_the_nine_of_ideal_11_3()
    {
        var ids = LoadReal().ObjectiveTemplates.Select(t => t.ObjectiveTemplateId).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "bring-creature-home-alive", "cleanse-fights", "explore-rooms", "extract-with-item-kind",
                "finish-under-hunger", "gather-curio-kind", "kill-boss", "spend-no-provision", "survive-no-downed"
            }.OrderBy(x => x, StringComparer.Ordinal),
            ids);
    }

    [Fact]
    public void Sink_avoidance_is_true_on_exactly_the_three_named_templates()
    {
        var sinkAvoidance = LoadReal().ObjectiveTemplates.Where(t => t.SinkAvoidance).Select(t => t.ObjectiveTemplateId)
            .OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "finish-under-hunger", "spend-no-provision", "survive-no-downed" }.OrderBy(x => x, StringComparer.Ordinal),
            sinkAvoidance);
    }

    [Fact]
    public void Destroy_and_garrison_carry_their_base_defense_decision_numbers()
    {
        var registries = LoadReal();
        Assert.Equal(12, registries.InteractionVerbs.Single(v => v.VerbId == "destroy").Decision);
        Assert.Equal(15, registries.InteractionVerbs.Single(v => v.VerbId == "garrison").Decision);
        foreach (var verb in registries.InteractionVerbs.Where(v => v.VerbId is "open" or "disarm" or "pray" or "loot"))
            Assert.Null(verb.Decision);
    }

    [Fact]
    public void Every_band_member_has_a_display_name_and_no_member_is_a_spelled_number()
    {
        var registries = LoadReal();
        var spelled = new HashSet<string> { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
        foreach (var (name, def) in registries.Bands)
        {
            Assert.NotEmpty(def.Members);
            foreach (var m in def.Members)
            {
                Assert.True(def.DisplayNames.ContainsKey(m), $"{name}.{m} has no display name");
                Assert.DoesNotContain(m, spelled);
            }
        }
    }

    [Fact]
    public void The_twenty_band_vocabularies_are_exactly_the_ones_this_registry_owns()
    {
        Assert.Equal(BandCatalog.BandNames.OrderBy(x => x, StringComparer.Ordinal),
            LoadReal().Bands.Keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void CountBand_is_the_S2_12_vocabulary_never_spelled_numbers()
    {
        Assert.Equal(new[] { "lone", "few", "several", "many" }, LoadReal().Bands["countBand"].Members);
    }

    [Fact]
    public void NerveStage_matches_delve_attritions_three_stages()
    {
        Assert.Equal(new[] { "unsettled", "shaken", "afflicted" }, LoadReal().Bands["nerveStage"].Members);
    }

    // ---------------------------------------------------------------------------------------
    // Malformed-file red tests — one per Validate() rule, each a from-string fixture (never a
    // second copy of the real file).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RoomKindCatalog_rejects_a_duplicate_id()
    {
        const string json = """{"roomKinds":{"fight":{"climateNeutral":false,"secretEligible":false,"bossRowAllowed":true,"neverAdjacentTo":[],"unknownResolvesTo":[]}}}""";
        var rows = RoomKindCatalog.Parse(json);
        var dup = rows.Concat(rows).ToList();
        Assert.Throws<DungeonRegistryRejection>(() => RoomKindCatalog.Validate(dup));
    }

    [Fact]
    public void RoomKindCatalog_rejects_zero_bossRowAllowed_kinds()
    {
        const string json = """{"roomKinds":{"fight":{"climateNeutral":false,"secretEligible":false,"bossRowAllowed":false,"neverAdjacentTo":[],"unknownResolvesTo":[]}}}""";
        var ex = Assert.Throws<DungeonRegistryRejection>(() => RoomKindCatalog.Validate(RoomKindCatalog.Parse(json)));
        Assert.Contains("exactly one", ex.Message);
    }

    [Fact]
    public void RoomKindCatalog_rejects_two_bossRowAllowed_kinds()
    {
        const string json = """
        {"roomKinds":{
          "boss":{"climateNeutral":true,"secretEligible":false,"bossRowAllowed":true,"neverAdjacentTo":[],"unknownResolvesTo":[]},
          "boss2":{"climateNeutral":true,"secretEligible":false,"bossRowAllowed":true,"neverAdjacentTo":[],"unknownResolvesTo":[]}
        }}
        """;
        Assert.Throws<DungeonRegistryRejection>(() => RoomKindCatalog.Validate(RoomKindCatalog.Parse(json)));
    }

    [Fact]
    public void RoomKindCatalog_rejects_neverAdjacentTo_naming_an_unknown_id()
    {
        const string json = """{"roomKinds":{"fight":{"climateNeutral":false,"secretEligible":false,"bossRowAllowed":true,"neverAdjacentTo":["ghost-kind"],"unknownResolvesTo":[]}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => RoomKindCatalog.Validate(RoomKindCatalog.Parse(json)));
    }

    [Fact]
    public void RoomKindCatalog_rejects_unknownResolvesTo_on_a_non_unknown_kind()
    {
        const string json = """{"roomKinds":{"fight":{"climateNeutral":false,"secretEligible":false,"bossRowAllowed":true,"neverAdjacentTo":[],"unknownResolvesTo":["fight"]}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => RoomKindCatalog.Validate(RoomKindCatalog.Parse(json)));
    }

    [Fact]
    public void RoomKindCatalog_rejects_a_kind_that_is_both_bossRowAllowed_and_secretEligible()
    {
        const string json = """{"roomKinds":{"boss":{"climateNeutral":true,"secretEligible":true,"bossRowAllowed":true,"neverAdjacentTo":[],"unknownResolvesTo":[]}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => RoomKindCatalog.Validate(RoomKindCatalog.Parse(json)));
    }

    [Fact]
    public void RoomKindCatalog_rejects_a_non_kebab_id()
    {
        const string json = """{"roomKinds":{"Fight":{"climateNeutral":false,"secretEligible":false,"bossRowAllowed":true,"neverAdjacentTo":[],"unknownResolvesTo":[]}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => RoomKindCatalog.Validate(RoomKindCatalog.Parse(json)));
    }

    [Fact]
    public void DifficultyRungCatalog_rejects_a_gap_in_ordinals()
    {
        const string json = """{"rungs":{"a":{"ordinal":1},"b":{"ordinal":3}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => DifficultyRungCatalog.Validate(DifficultyRungCatalog.Parse(json)));
    }

    [Fact]
    public void DifficultyRungCatalog_rejects_a_duplicate_ordinal()
    {
        const string json = """{"rungs":{"a":{"ordinal":1},"b":{"ordinal":1}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => DifficultyRungCatalog.Validate(DifficultyRungCatalog.Parse(json)));
    }

    [Fact]
    public void BandCatalog_rejects_a_missing_band()
    {
        var real = new Dictionary<string, BandDef>(LoadReal().Bands);
        real.Remove("nerveStage");
        Assert.Throws<DungeonRegistryRejection>(() => BandCatalog.Validate(real));
    }

    [Fact]
    public void BandCatalog_rejects_an_extra_band()
    {
        var real = new Dictionary<string, BandDef>(LoadReal().Bands)
        {
            ["bogusBand"] = new BandDef { BandName = "bogusBand", Members = new[] { "x" }, DisplayNames = new Dictionary<string, string> { ["x"] = "X" } }
        };
        Assert.Throws<DungeonRegistryRejection>(() => BandCatalog.Validate(real));
    }

    [Fact]
    public void BandCatalog_rejects_a_spelled_number_member()
    {
        const string json = """{"bands":{"countBand":{"members":["one","two"],"displayNames":{"one":"One","two":"Two"}}}}""";
        var parsed = new Dictionary<string, BandDef>(LoadReal().Bands) { ["countBand"] = BandCatalog.Parse(json)["countBand"] };
        Assert.Throws<DungeonRegistryRejection>(() => BandCatalog.Validate(parsed));
    }

    [Fact]
    public void BandCatalog_rejects_a_member_with_no_display_name()
    {
        const string json = """{"bands":{"countBand":{"members":["lone"],"displayNames":{}}}}""";
        var parsed = new Dictionary<string, BandDef>(LoadReal().Bands) { ["countBand"] = BandCatalog.Parse(json)["countBand"] };
        Assert.Throws<DungeonRegistryRejection>(() => BandCatalog.Validate(parsed));
    }

    [Fact]
    public void InteractionVerbCatalog_rejects_a_non_positive_decision_number()
    {
        const string json = """{"interactionVerbs":{"garrison":{"decision":0}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => InteractionVerbCatalog.Validate(InteractionVerbCatalog.Parse(json)));
    }

    [Fact]
    public void ObjectiveTemplateCatalog_rejects_an_unknown_targetKind()
    {
        const string json = """{"objectiveTemplates":{"foo":{"targetKind":"nonsense","sinkAvoidance":false}}}""";
        Assert.Throws<DungeonRegistryRejection>(() => ObjectiveTemplateCatalog.Parse(json));
    }

    // ---------------------------------------------------------------------------------------
    // Hub discipline — a catalog read before Configure is a startup error, not a runtime null.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Get_on_an_unknown_id_throws_ArgumentException_after_configure()
    {
        RoomKindCatalog.Configure(LoadReal().RoomKinds);
        Assert.Throws<ArgumentException>(() => RoomKindCatalog.Get("not-a-real-kind"));
        Assert.False(RoomKindCatalog.IsKnown("not-a-real-kind"));
        Assert.True(RoomKindCatalog.IsKnown("boss"));
    }

    [Fact]
    public void Configuring_the_hub_wires_every_one_of_the_nine_catalogs()
    {
        var registries = LoadReal();
        DungeonTuningHub.Configure(DungeonTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.DungeonTuningPath()), registries));
        DungeonRegistryHub.Configure(registries);

        // Compares against the SAME LoadReal() registries this test just configured from, not a second
        // copy of the literal counts (population-pin SE3.3, 2026-09-19) -- this test's job is proving
        // Configure() wired every entry through, not re-pinning a count All_nine_registry_files_
        // parse_and_validate already owns.
        Assert.Equal(registries.RoomKinds.Count, RoomKindCatalog.All.Count);
        Assert.Equal(registries.DoorKinds.Count, DoorKindCatalog.All.Count);
        Assert.Equal(registries.OverrideTags.Count, OverrideTagCatalog.All.Count);
        Assert.Equal(registries.ObjectiveTemplates.Count, ObjectiveTemplateCatalog.All.Count);
        Assert.Equal(registries.DifficultyRungs.Count, DifficultyRungCatalog.All.Count);
        Assert.Equal(registries.Disposition.Count, DispositionCatalog.All.Count);
        Assert.Equal(registries.InteractionVerbs.Count, InteractionVerbCatalog.All.Count);
        Assert.Equal(registries.RaidModes.Count, RaidModeCatalog.All.Count);
        Assert.Equal(registries.Bands.Count, BandCatalog.All.Count);

        // RoomKindDef joins DungeonTuningHub at first read — proves the join actually resolves,
        // not just that Configure ran without throwing.
        var fight = RoomKindCatalog.Get("fight");
        Assert.True(fight.WeightMilli > 0);
    }
}
