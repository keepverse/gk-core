using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>
/// D4.13's own row-8 production wiring (party-dungeon-todo.md, 2026-09-07 citation correction) — closes
/// `DomainPreflightInputs.CheckQuests`, the last of the five delegated rows that was still a stub
/// (`DomainRealPipelineTests.cs`). Rows 4/6/7 stubbed to always pass, matching
/// `DomainEventPreflightBridgeTests`'s own established isolation style — this file's only job is row 8.
///
/// <para><b>2026-09-08: uses the real item-rarity `Ladder` fixture, not a difficulty-rung stand-in.</b>
/// The real shipped `dungeon.v1.json` used to carry difficulty-rung ids under
/// `quests.rewardBand.*.floorRung`/`.ceilRung` (found and named precisely in `QuestPreflightTests.cs`'s
/// own `Run_refuses_reward_band_rung_unresolvable...` test and the todo entry) — this file's own
/// `RewardBandShapedLadder` used to deliberately MATCH that defect so `CheckFloorNotAboveCeil` never
/// refused, isolating row 8's own wiring. That content defect is now fixed (`dungeon.v3.json`); this
/// fixture now carries the real item-rarity ladder for the same isolation reason, not a workaround.</para>
/// </summary>
public class DomainQuestPreflightBridgeTests
{
    static DungeonTuning Tuning => DungeonTuningHub.Tuning;
    static readonly IReadOnlyList<string> EventKinds = BandCatalog.Get("eventKind").Members;
    static readonly IReadOnlyList<string> RepeatScopes = BandCatalog.Get("repeatScope").Members;
    static readonly IReadOnlyList<string> OutcomeOrdinals = BandCatalog.Get("outcomeOrdinal").Members;
    static readonly IReadOnlyList<string> OverrideTags = OverrideTagCatalog.All;
    static int NoStatus(string id) => -1;

    static IReadOnlyList<string> ReadRealItemDropBands()
    {
        var path = Path.Combine(DungeonTestFiles.RepoRoot(), "data", "seed", "items", "_registry", "bands.v1.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("dropBand").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    static EventCatalog RealEventCatalog()
    {
        var rows = EventSeedFile.LoadAll(DungeonTestFiles.EventsDir());
        var result = EventCatalog.Load(rows, EventKinds, RepeatScopes, OutcomeOrdinals, ReadRealItemDropBands(), OverrideTags, NoStatus);
        Assert.Empty(result.Rejections);
        return result.Catalog;
    }

    static LayoutTemplateCatalog RealLayoutCatalog()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());
        var bandDefs = new Dictionary<string, BandDef>
        {
            ["depthBand"] = new BandDef { BandName = "depthBand", Members = Tuning.DepthBandRows.Keys.ToList() },
            ["widthBand"] = new BandDef { BandName = "widthBand", Members = Tuning.WidthBandCols.Keys.ToList() },
            ["branchiness"] = new BandDef { BandName = "branchiness", Members = Tuning.BranchinessPathWalks.Keys.ToList() },
            ["density"] = new BandDef
            {
                BandName = "density",
                Members = Tuning.GateDensityPerRoomMilli.Keys
                    .Union(Tuning.SecretDensityPerRoomMilli.Keys).Union(Tuning.OneWayDensityPerRoomMilli.Keys).ToList(),
            },
        };
        var load = LayoutTemplateCatalog.Load(rows, bandDefs, Tuning.RaidModes.Keys.ToList());
        Assert.Empty(load.Rejections);
        return load.Catalog;
    }

    static QuestCatalog RealQuestCatalog()
    {
        var rows = QuestSeedFile.LoadAll(DungeonTestFiles.QuestsDir());
        var result = QuestCatalog.Load(
            rows, ObjectiveTemplateCatalog.All,
            BandCatalog.Get("questScope").Members, BandCatalog.Get("rewardBand").Members, BandCatalog.Get("countBand").Members,
            NoStatus);
        Assert.Empty(result.Rejections);
        return result.Catalog;
    }

    /// <summary>Each real domain's own `questPool` ids, resolved against the real catalog -- the exact
    /// job <see cref="QuestPreflight.QuestPreflightCorpus.QuestPoolByDomainId"/>'s own doc comment
    /// names as the caller's responsibility (`DomainRow` never carries a resolved pool).</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<QuestRow>> RealQuestPoolByDomainId(QuestCatalog catalog)
    {
        var poolIds = FusionRpg.Core.Delve.Domains.DomainSeedFile.LoadQuestPools(DungeonTestFiles.DomainsDir());
        var result = new Dictionary<string, IReadOnlyList<QuestRow>>(StringComparer.Ordinal);
        foreach (var (domainId, ids) in poolIds)
            result[domainId] = ids.Select(id => catalog.Resolve(id) ?? throw new InvalidOperationException($"quest '{id}' named by {domainId}'s questPool does not resolve")).ToList();
        return result;
    }

    /// <summary>2026-09-08: the real item-rarity ladder, not a difficulty-rung stand-in. The shipped
    /// `dungeon.v1.json`'s own `quests.rewardBand.*` values used to be difficulty-rung ids
    /// ("very-easy".."nightmare"), a genuine content defect this fixture used to deliberately MATCH so
    /// `CheckFloorNotAboveCeil` would not refuse and this file's own tests could isolate row 8's wiring
    /// in peace (see `QuestPreflightTests.cs`'s own `Run_refuses_reward_band_rung_unresolvable...` for
    /// where that defect was found and reproduced). That content is now fixed (`dungeon.v3.json`,
    /// `gk-core/tools/tuning/publish.py`) to the real item-rarity ladder ids spec §12 always meant
    /// (`item-rarity.v1.json:7-18`) — this fixture now matches it directly for the same reason.</summary>
    static readonly IReadOnlyList<RarityRung> RewardBandShapedLadder = new RarityRung[]
    {
        new("chaff", 0, 0, 0, 0, 0, 100), new("sprout", 1, 0, 0, 0, 0, 100), new("grafted", 2, 0, 0, 0, 0, 100),
        new("cultivated", 3, 0, 0, 0, 0, 100), new("fused", 4, 0, 0, 0, 0, 100), new("chimeric", 5, 0, 0, 0, 0, 100),
        new("heirloom", 6, 0, 0, 0, 0, 100), new("firstseed", 7, 0, 0, 0, 0, 100), new("sunwoven", 8, 0, 0, 0, 0, 100),
        new("almanac", 9, 0, 0, 0, 0, 100),
    };

    static QuestPreflight.QuestPreflightCorpus RealCorpus()
    {
        var eventCatalog = RealEventCatalog();
        var questCatalog = RealQuestCatalog();
        var lootTables = LootCorpusReader.Merge(
            Directory.EnumerateFiles(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "seed", "loot"), "*.json")
                .Select(p => LootCorpusReader.Parse(File.ReadAllText(p))))
            .Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);
        var baseTypes = BaseTypeSeedFile.LoadAll(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "seed", "items", "base-types"));
        IReadOnlyList<string> BaseTypesFor(string frame, string role) =>
            baseTypes.Where(b => b.Frame == frame && b.Role == role).Select(b => b.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

        return new QuestPreflight.QuestPreflightCorpus(
            RoomsById: RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir()),
            RoomPaletteByDomainId: FusionRpg.Core.Delve.Domains.DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir()),
            QuestPoolByDomainId: RealQuestPoolByDomainId(questCatalog),
            ArchetypeEventPoolHasKind: QuestArchetypeEventBridge.Build(RoomEventPoolSeedFile.LoadAll(DungeonTestFiles.RoomsDir()), eventCatalog),
            LootBindingByDomainId: FusionRpg.Core.Delve.Domains.DomainSeedFile.LoadLootBindings(DungeonTestFiles.DomainsDir()),
            Tables: lootTables,
            BaseTypesFor: BaseTypesFor,
            Ladder: RewardBandShapedLadder,
            BossRoomKindOrdinal: 0);
    }

    static FusionRpg.Core.Delve.Domains.DomainPreflightInputs InputsIsolatingRow8(Func<FusionRpg.Core.Delve.Domains.DomainRow, IReadOnlyList<FusionRpg.Core.Delve.Domains.DomainRefusal>> checkQuests)
    {
        var domains = FusionRpg.Core.Delve.Domains.DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        return new(
            DangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 },
            KnownLayoutIds: domains.Select(d => d.LayoutTemplateId).ToHashSet(StringComparer.Ordinal),
            KnownSpeciesIds: domains.Select(d => d.BossSpeciesRef).ToHashSet(StringComparer.Ordinal),
            ThreatBandOrdinalFor: _ => 99,
            BossFloorRungOrdinal: 0,
            CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
            CellsPaletteFills: _ => Array.Empty<(string, string)>(),
            CheckGraphs: _ => Array.Empty<FusionRpg.Core.Delve.Domains.DomainRefusal>(),
            CheckObjects: _ => Array.Empty<FusionRpg.Core.Delve.Domains.DomainRefusal>(),
            CheckEncounters: _ => Array.Empty<FusionRpg.Core.Delve.Domains.DomainRefusal>(),
            CheckEvents: _ => Array.Empty<FusionRpg.Core.Delve.Domains.DomainRefusal>(),
            CheckQuests: checkQuests,
            KnownDropTableIds: Array.Empty<string>(),
            BoundLootKinds: Array.Empty<string>(),
            LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal),
            OfferedRungCountFor: _ => 1);
    }

    [Fact]
    public void Build_null_arguments_throw()
    {
        var layouts = RealLayoutCatalog();
        var corpus = RealCorpus();
        Assert.Throws<ArgumentNullException>(() => FusionRpg.Core.Delve.Domains.DomainQuestPreflight.Build(null!, layouts, Tuning));
        Assert.Throws<ArgumentNullException>(() => FusionRpg.Core.Delve.Domains.DomainQuestPreflight.Build(corpus, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => FusionRpg.Core.Delve.Domains.DomainQuestPreflight.Build(corpus, layouts, null!));
    }

    /// <summary>The real end-to-end proof, through the actual `DomainPreflight.Run` entry point, over
    /// all six real domains and their real 24-anchor questPool each -- whatever the real, honest verdict
    /// is. If it refuses, every refusal must trace to `domain.quest:*` (this test isolates row 8 alone)
    /// and name a real domain id, proving the bridge is REAL, not a rubber stamp either way.
    ///
    /// <para><b>Discovered result: zero refusals.</b> The real six domains' real 24-anchor pools, real
    /// room/event content and real loot bindings all pass the full sweep (32 seeds × 3 raid modes × 10
    /// rungs, per domain) cleanly -- this is a genuine, reproduced finding (not a vacuous pass; the
    /// explicit pool-size assertion below proves the sweep is actually reached with real, non-trivial
    /// pools, and `Run_reaches_every_real_shipped_domain_without_a_graph_roll_exception_escaping`
    /// independently proves the same six domains' graphs really roll).</para>
    /// </summary>
    [Fact]
    public void Every_real_shipped_domain_is_checked_through_the_real_DomainPreflight_Run_entry_point()
    {
        var domains = FusionRpg.Core.Delve.Domains.DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var layouts = RealLayoutCatalog();
        var corpus = RealCorpus();
        var checkQuests = FusionRpg.Core.Delve.Domains.DomainQuestPreflight.Build(corpus, layouts, Tuning);

        Assert.Equal(6, domains.Count);
        // The load-bearing "not vacuous" proof: every real domain's own pool is genuinely non-trivial
        // before the sweep ever runs -- an empty-refusals result below is a real pass, not a silent skip.
        Assert.All(domains, d => Assert.True(corpus.QuestPoolByDomainId.TryGetValue(d.DomainId, out var pool) && pool.Count >= 20,
            $"{d.DomainId} should carry a real, non-trivial questPool"));

        var refusals = FusionRpg.Core.Delve.Domains.DomainPreflight.Run(domains, InputsIsolatingRow8(checkQuests));

        Assert.All(refusals, r => Assert.StartsWith("domain.quest:", r.Rule));
        var refusedIds = refusals.Select(r => r.DomainId).ToHashSet(StringComparer.Ordinal);
        Assert.All(refusedIds, id => Assert.Contains(id, domains.Select(d => d.DomainId)));
        Assert.Empty(refusals); // the discovered, reproduced result -- see this test's own doc comment
    }

    /// <summary>Negative control: a `QuestRefusal` from one of the three already-shipped row/pool checks
    /// (a hand-crafted pool with too few non-sink anchors, on a real domain's real graph content) reaches
    /// the real entry point as `domain.quest:quest.too-few-non-sink-anchors`, proving the try/catch
    /// translation is real, not a silent swallow.</summary>
    [Fact]
    public void A_pool_that_fails_a_static_check_is_refused_domain_quest_through_the_real_entry_point()
    {
        var domains = FusionRpg.Core.Delve.Domains.DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var domain = domains.Single(d => d.DomainId == "domain.fire-001");
        var layouts = RealLayoutCatalog();
        var corpus = RealCorpus() with
        {
            QuestPoolByDomainId = new Dictionary<string, IReadOnlyList<QuestRow>>(StringComparer.Ordinal)
            {
                [domain.DomainId] = new[] { new QuestRow("q1", "finish-under-hunger", null, null, "modest", "delve", null) }, // one sink-avoidance anchor, offeredAtEntry is 2
            },
        };
        var checkQuests = FusionRpg.Core.Delve.Domains.DomainQuestPreflight.Build(corpus, layouts, Tuning);

        var refusals = FusionRpg.Core.Delve.Domains.DomainPreflight.Run(new[] { domain }, InputsIsolatingRow8(checkQuests));

        Assert.Single(refusals);
        Assert.Equal($"domain.quest:{QuestPreflightRules.TooFewNonSinkAnchors}", refusals[0].Rule);
        Assert.Equal(domain.DomainId, refusals[0].DomainId);
    }

    /// <summary>Positive control: a domain whose pool is entirely structural (graph-independent)
    /// anchors passes row 8 cleanly through the real entry point -- proves the bridge is not a
    /// guaranteed-refuse sweep either.</summary>
    [Fact]
    public void A_wholly_structural_pool_passes_row8_through_the_real_entry_point()
    {
        var domains = FusionRpg.Core.Delve.Domains.DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var domain = domains.Single(d => d.DomainId == "domain.fire-001");
        var layouts = RealLayoutCatalog();
        var corpus = RealCorpus() with
        {
            QuestPoolByDomainId = new Dictionary<string, IReadOnlyList<QuestRow>>(StringComparer.Ordinal)
            {
                [domain.DomainId] = new[]
                {
                    new QuestRow("q1", "kill-boss", null, null, "modest", "delve", null),
                    new QuestRow("q2", "bring-creature-home-alive", null, null, "modest", "delve", null),
                },
            },
        };
        var checkQuests = FusionRpg.Core.Delve.Domains.DomainQuestPreflight.Build(corpus, layouts, Tuning);

        var refusals = FusionRpg.Core.Delve.Domains.DomainPreflight.Run(new[] { domain }, InputsIsolatingRow8(checkQuests));

        Assert.Empty(refusals);
    }
}
