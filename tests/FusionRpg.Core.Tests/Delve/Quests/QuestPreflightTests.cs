using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Quests;

/// <summary>D4.13 (spec-delve-quests.md §8) — `QuestPreflight`'s three buildable checks and
/// `QuestCoverage.WithinRegressionBand`. The full 256-seed satisfiability sweep and the live
/// autopilot-completion measurement both need `domain-catalog` (D4.15+, genuinely unbuilt) — "preflight
/// refuses a domain whose quests cannot complete" and "the band holds over the sweep" are tested here
/// at the scope this task actually built (the non-sink-anchor-count check; the band predicate's own
/// correctness), named explicitly rather than silently claimed at the full sweep's scope.</summary>
public class QuestPreflightTests
{
    static RarityRung Rung(string id, int ordinal) => new(id, ordinal, 0, 0, 0, 0, 100);
    static readonly IReadOnlyList<RarityRung> Ladder = new[] { Rung("staple", 10), Rung("frequent", 20), Rung("occasional", 30) };

    static readonly IReadOnlyDictionary<string, RewardWindow> RewardBands = new Dictionary<string, RewardWindow>(StringComparer.Ordinal)
    {
        ["modest"] = new RewardWindow("staple", "occasional"),
        ["inverted"] = new RewardWindow("occasional", "staple"), // floor above ceil -- deliberately malformed
    };

    static QuestRow Row(string id, string template, string rewardBand = "modest", PredicateNode? predicate = null) =>
        new(id, template, null, null, rewardBand, "delve", predicate);

    // ---- TreeUsesLeaf ----

    [Fact]
    public void TreeUsesLeaf_finds_a_bare_matching_leaf()
    {
        var leaf = new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: 7);
        Assert.True(QuestPreflight.TreeUsesLeaf(leaf, l => l.Id == LeafId.RoomKindIs && l.Value == 7));
    }

    [Fact]
    public void TreeUsesLeaf_returns_false_for_a_null_tree()
    {
        Assert.False(QuestPreflight.TreeUsesLeaf(null, _ => true));
    }

    [Fact]
    public void TreeUsesLeaf_recurses_through_And_Or_and_Not()
    {
        var target = new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: 7);
        var other = new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 300);
        var tree = new PredicateNode.And(new PredicateNode[]
        {
            other,
            new PredicateNode.Not(new PredicateNode.Or(new PredicateNode[] { other, target })),
        });
        bool IsBoss(PredicateNode.Leaf l) => l.Id == LeafId.RoomKindIs && l.Value == 7;
        Assert.True(QuestPreflight.TreeUsesLeaf(tree, IsBoss));
        Assert.False(QuestPreflight.TreeUsesLeaf(other, IsBoss));
    }

    // ---- CheckNoRoomKindIsBoss ----

    static bool IsBossLeaf(PredicateNode.Leaf l) => l.Id == LeafId.RoomKindIs && l.Value == 99;

    [Fact]
    public void CheckNoRoomKindIsBoss_refuses_a_quest_gating_on_the_boss_room_kind()
    {
        var quest = Row("q1", "kill-boss", predicate: new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: 99));
        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.CheckNoRoomKindIsBoss("domain.forest", new[] { quest }, IsBossLeaf));
        Assert.Equal(QuestPreflightRules.RoomKindIsBossForbidden, ex.Rule);
        Assert.Equal("q1", ex.QuestId);
        Assert.Equal("domain.forest", ex.DomainId);
    }

    [Fact]
    public void CheckNoRoomKindIsBoss_passes_a_quest_with_no_predicate_at_all()
    {
        var quest = Row("q1", "kill-boss");
        QuestPreflight.CheckNoRoomKindIsBoss("domain.forest", new[] { quest }, IsBossLeaf); // does not throw
    }

    [Fact]
    public void CheckNoRoomKindIsBoss_passes_a_predicate_that_never_names_the_boss_kind()
    {
        var quest = Row("q1", "kill-boss", predicate: new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Target, Value: 300));
        QuestPreflight.CheckNoRoomKindIsBoss("domain.forest", new[] { quest }, IsBossLeaf); // does not throw
    }

    // ---- CheckFloorNotAboveCeil ----

    [Fact]
    public void CheckFloorNotAboveCeil_refuses_an_inverted_window()
    {
        var quest = Row("q1", "kill-boss", rewardBand: "inverted");
        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.CheckFloorNotAboveCeil("domain.forest", new[] { quest }, RewardBands, Ladder));
        Assert.Equal(QuestPreflightRules.FloorAboveCeil, ex.Rule);
    }

    [Fact]
    public void CheckFloorNotAboveCeil_passes_a_well_formed_window()
    {
        var quest = Row("q1", "kill-boss", rewardBand: "modest");
        QuestPreflight.CheckFloorNotAboveCeil("domain.forest", new[] { quest }, RewardBands, Ladder); // does not throw
    }

    [Fact]
    public void CheckFloorNotAboveCeil_skips_a_row_whose_rewardBand_QuestCatalog_would_already_have_refused()
    {
        var quest = Row("q1", "kill-boss", rewardBand: "not-a-real-band");
        QuestPreflight.CheckFloorNotAboveCeil("domain.forest", new[] { quest }, RewardBands, Ladder); // does not throw -- not this check's job
    }

    // ---- CheckEnoughNonSinkAnchors / "preflight refuses a domain whose quests cannot complete" ----

    [Fact]
    public void CheckEnoughNonSinkAnchors_refuses_a_pool_that_can_never_fill_the_offer()
    {
        var templates = ObjectiveTemplateCatalog.All;
        var pool = new[] { Row("q1", "finish-under-hunger"), Row("q2", "survive-no-downed") }; // both sink-avoidance
        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.CheckEnoughNonSinkAnchors("domain.forest", pool, templates, offeredAtEntry: 2));
        Assert.Equal(QuestPreflightRules.TooFewNonSinkAnchors, ex.Rule);
    }

    [Fact]
    public void CheckEnoughNonSinkAnchors_passes_a_pool_with_enough_non_sink_anchors()
    {
        var templates = ObjectiveTemplateCatalog.All;
        var pool = new[] { Row("q1", "kill-boss"), Row("q2", "bring-creature-home-alive"), Row("q3", "finish-under-hunger") };
        QuestPreflight.CheckEnoughNonSinkAnchors("domain.forest", pool, templates, offeredAtEntry: 2); // does not throw
    }

    // ---- QuestCoverage.WithinRegressionBand / "the band holds over the sweep" ----

    [Theory]
    [InlineData(300, 300, 900, true)]  // exactly at the floor -- inclusive
    [InlineData(900, 300, 900, true)]  // exactly at the ceiling -- inclusive
    [InlineData(600, 300, 900, true)]
    [InlineData(299, 300, 900, false)] // below -- a template that quietly got harder
    [InlineData(901, 300, 900, false)] // above -- content drift, still flagged, "never a target"
    public void WithinRegressionBand_matches_the_bands_own_inclusive_bounds(long completionMilli, long min, long max, bool expected)
    {
        Assert.Equal(expected, QuestCoverage.WithinRegressionBand(completionMilli, min, max));
    }

    [Fact]
    public void WithinRegressionBand_throws_on_an_inverted_band()
    {
        Assert.Throws<ArgumentException>(() => QuestCoverage.WithinRegressionBand(500, 900, 300));
    }

    [Fact]
    public void The_real_shipped_autopilotCompletionBand_is_a_real_two_sided_range_not_a_collapsed_target()
    {
        // Reads the real, shipped dungeon.v1.json rather than a hand-copied literal (a fixture copy
        // could drift from what ships) -- pinned so a future edit that collapses the band to a single
        // value (making it a de facto target) is visible here, the acceptance line's own "never a
        // target" as a checked fact against real content, not an assumption.
        var registries = DungeonRegistryLoader.LoadAll(DungeonTestFiles.RegistryDir());
        var tuning = DungeonTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.DungeonTuningPath()), registries);
        Assert.True(tuning.QuestsAutopilotCompletionBandMinMilli < tuning.QuestsAutopilotCompletionBandMaxMilli);
        Assert.True(QuestCoverage.WithinRegressionBand(
            (tuning.QuestsAutopilotCompletionBandMinMilli + tuning.QuestsAutopilotCompletionBandMaxMilli) / 2,
            tuning.QuestsAutopilotCompletionBandMinMilli, tuning.QuestsAutopilotCompletionBandMaxMilli));
    }

    // ---- Run (D4.13's own full orchestration, closed 2026-09-07) ----------------------------------
    //
    // These fixtures deliberately use a SELF-CONSISTENT hand-built reward-band/ladder pairing
    // ("modest" -> staple/occasional, matching this file's own pre-existing `RewardBands`/`Ladder`
    // fixtures above), NOT the real shipped `DungeonTuningHub.Tuning.QuestsRewardBand` -- running
    // these two real halves together for the first time (Run calling the already-shipped
    // CheckFloorNotAboveCeil against REAL content) surfaced a genuine, pre-existing, separate finding:
    // the real `dungeon.v1.json` quests.rewardBand.*.floorRung/ceilRung values are difficulty-rung ids
    // ("very-easy".."nightmare"), not item-rarity ladder ids ("chaff".."almanac") the spec's own
    // Tunables table cites (`item-rarity.v1.json:7-18`) -- named precisely, with real reproduction
    // evidence, in the todo entry rather than silently worked around here. Isolating THIS file's own
    // tests from that separate defect keeps them focused on what they exist to prove: `Run`'s own
    // sweep/composition logic, reusing `RarityDraw`/`CheckFloorNotAboveCeil` correctly.
    static DungeonTuning Tuning => DungeonTuningHub.Tuning;

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

    static DomainRow RealDomain() => DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).Single(d => d.DomainId == "domain.fire-001");

    /// <summary>This file's own `Row` helper (above) never takes a `targetRef`/`countBand` -- every
    /// existing fixture in this file is count-less. `Run`'s own new sweep tests need a real
    /// `cleanse-fights`-shaped row, so this is a second, additive helper rather than widening `Row`'s
    /// signature under every pre-existing call site in this file.</summary>
    static QuestRow CountedRow(string id, string template, string targetRef, string countBand, string rewardBand = "modest") =>
        new(id, template, targetRef, countBand, rewardBand, "delve", null);

    /// <summary>
    /// `Run` reads `rewardBandsByMember` straight off the `tuning` PARAMETER
    /// (`tuning.QuestsRewardBand`), never from the corpus -- and every test below needs the REAL
    /// `Tuning` for graph rolling (real raid modes / countBand / PreflightSampleSeeds), so this ladder
    /// must actually resolve every one of the real `Tuning.QuestsRewardBand` rung ids or every test
    /// below would spuriously hit `RewardBandRungUnresolvable` regardless of what it exists to prove.
    ///
    /// <para><b>2026-09-08: the real item-rarity ladder, not a difficulty-rung stand-in.</b> Until this
    /// date, the real shipped `dungeon.v1.json`'s own `quests.rewardBand.*` values were themselves
    /// difficulty-rung ids ("very-easy".."nightmare"), a genuine content defect this fixture used to
    /// deliberately MATCH so tests not about that defect could reach the sweep logic they actually exist
    /// to prove — that content is now fixed (`dungeon.v3.json`, `gk-core/tools/tuning/publish.py`) to the real
    /// item-rarity ladder ids spec §12 always meant (`item-rarity.v1.json:7-18`); this fixture now
    /// carries the real ten-rung ladder for the identical isolation reason, not a workaround.</para>
    /// </summary>
    static readonly IReadOnlyList<RarityRung> RewardBandShapedLadder = new[]
    {
        Rung("chaff", 0), Rung("sprout", 1), Rung("grafted", 2), Rung("cultivated", 3), Rung("fused", 4),
        Rung("chimeric", 5), Rung("heirloom", 6), Rung("firstseed", 7), Rung("sunwoven", 8), Rung("almanac", 9),
    };

    /// <summary>A minimal, real-graph-rollable corpus: real rooms/palette/layout/tuning (so
    /// `DelveGraphRoll.Roll` really succeeds, proven independently by
    /// `DomainGraphPreflightBridgeTests.Every_real_shipped_domain_passes_row4...`), but a
    /// CALLER-SUPPLIED quest pool and a stubbed archetype/loot resolution -- every test below only
    /// varies the pool to isolate exactly one behaviour of `Run` itself.</summary>
    static QuestPreflight.QuestPreflightCorpus MinimalCorpus(DomainRow domain, IReadOnlyList<QuestRow> pool) => new(
        RoomsById: RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir()),
        RoomPaletteByDomainId: DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir()),
        QuestPoolByDomainId: new Dictionary<string, IReadOnlyList<QuestRow>>(StringComparer.Ordinal) { [domain.DomainId] = pool },
        ArchetypeEventPoolHasKind: (_, _) => false,
        LootBindingByDomainId: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
        Tables: new Dictionary<string, DropTableRow>(StringComparer.Ordinal),
        BaseTypesFor: (_, _) => Array.Empty<string>(),
        Ladder: RewardBandShapedLadder,
        BossRoomKindOrdinal: 99);

    [Fact]
    public void Run_null_arguments_throw()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var corpus = MinimalCorpus(domain, new[] { Row("q1", "kill-boss") });
        Assert.Throws<ArgumentNullException>(() => QuestPreflight.Run(null!, new[] { domain }, layouts, Tuning));
        Assert.Throws<ArgumentNullException>(() => QuestPreflight.Run(corpus, null!, layouts, Tuning));
        Assert.Throws<ArgumentNullException>(() => QuestPreflight.Run(corpus, new[] { domain }, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => QuestPreflight.Run(corpus, new[] { domain }, layouts, null!));
    }

    [Fact]
    public void Run_skips_a_domain_with_no_pool_data_supplied_at_all()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var corpus = MinimalCorpus(domain, new[] { Row("q1", "kill-boss") }) with
        {
            QuestPoolByDomainId = new Dictionary<string, IReadOnlyList<QuestRow>>(StringComparer.Ordinal), // this domain absent entirely
        };
        QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning); // does not throw
    }

    [Fact]
    public void Run_propagates_CheckNoRoomKindIsBoss_through_the_real_entry_point()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var bossGate = new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: 99); // 99 == MinimalCorpus's own BossRoomKindOrdinal
        var pool = new[] { Row("q1", "kill-boss", predicate: bossGate), Row("q2", "bring-creature-home-alive") };
        var corpus = MinimalCorpus(domain, pool);

        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning));
        Assert.Equal(QuestPreflightRules.RoomKindIsBossForbidden, ex.Rule);
        Assert.Equal("q1", ex.QuestId);
        Assert.Equal(domain.DomainId, ex.DomainId);
    }

    /// <summary>Real `rewardBand` ids ("modest"/"fair"/"rich") are never inverted in the shipped
    /// content, so proving `Run` propagates `FloorAboveCeil` needs a ladder whose OWN ordinal
    /// assignment inverts a real window -- "modest" resolves to (`sprout`, `grafted`) as of
    /// `dungeon.v3.json` (2026-09-08's content fix); this ladder swaps their ordinals
    /// (`grafted` &lt; `sprout`) so `floorOrdinal &gt; ceilOrdinal` for real, without touching the
    /// tuning content itself.</summary>
    [Fact]
    public void Run_propagates_CheckFloorNotAboveCeil_through_the_real_entry_point()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var pool = new[] { Row("q1", "kill-boss", rewardBand: "modest"), Row("q2", "bring-creature-home-alive") };
        var invertedLadder = new[] { Rung("grafted", 1), Rung("sprout", 2) }; // swapped vs. real ordinal order -- inverts "modest"'s real window
        var corpus = MinimalCorpus(domain, pool) with { Ladder = invertedLadder };

        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning));
        Assert.Equal(QuestPreflightRules.FloorAboveCeil, ex.Rule);
        Assert.Equal("q1", ex.QuestId);
    }

    /// <summary>Proves the 2026-09-07 hardening: a clean, named `QuestRefusal`, never an uncaught
    /// `KeyNotFoundException`, when the supplied ladder does not carry the real `dungeon.v3.json`
    /// `quests.rewardBand.*` rung ids.
    ///
    /// <para><b>2026-09-08 correction: this is now a SYNTHETIC mismatch, not a reproduced bug.</b> The
    /// real shipped `dungeon.v1.json` used to carry difficulty-rung ids (`"very-easy"`..`"nightmare"`)
    /// under `quests.rewardBand.*` instead of the item-rarity ladder ids spec §12 names
    /// (`item-rarity.v1.json:7-18`) — a genuine content defect, reproduced by this exact test at the
    /// time. That content is now fixed (`dungeon.v3.json`, `gk-core/tools/tuning/publish.py`): `modest` resolves
    /// to `(sprout, grafted)`, `fair` to `(cultivated, fused)`, `rich` to `(chimeric, heirloom)` — three
    /// adjacent two-rung windows climbing the real ten-rung ladder, exactly as spec §12's own citation
    /// describes. This test still needs a ladder that cannot resolve `modest`'s real floor rung to prove
    /// the refusal path stays real — this file's own pre-existing `Ladder` fixture
    /// (`staple/frequent/occasional`) still does that, since none of its three members is `"sprout"`.</para>
    /// </summary>
    [Fact]
    public void Run_refuses_reward_band_rung_unresolvable_rather_than_crashing_against_a_mismatched_ladder()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var pool = new[] { Row("q1", "kill-boss", rewardBand: "modest"), Row("q2", "bring-creature-home-alive") };
        var corpus = MinimalCorpus(domain, pool) with { Ladder = Ladder }; // this file's own staple/frequent/occasional fixture -- never resolves "sprout"

        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning));
        Assert.Equal(QuestPreflightRules.RewardBandRungUnresolvable, ex.Rule);
        Assert.Equal("q1", ex.QuestId);
        Assert.Contains("sprout", ex.Message);
    }

    /// <summary>2026-09-08, new — the real shipped content now resolves cleanly (the defect the test
    /// above used to characterize is fixed), so this proves the POSITIVE case directly: `Run` against
    /// the real `Tuning` and the real ten-rung item-rarity ladder raises neither
    /// `RewardBandRungUnresolvable` nor `FloorAboveCeil` for any of the three real reward bands.</summary>
    [Fact]
    public void Run_resolves_all_three_real_reward_bands_against_the_real_item_rarity_ladder_without_refusing()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var realLadder = new[]
        {
            Rung("chaff", 0), Rung("sprout", 1), Rung("grafted", 2), Rung("cultivated", 3), Rung("fused", 4),
            Rung("chimeric", 5), Rung("heirloom", 6), Rung("firstseed", 7), Rung("sunwoven", 8), Rung("almanac", 9),
        };
        var pool = new[]
        {
            Row("q1", "kill-boss", rewardBand: "modest"),
            Row("q2", "bring-creature-home-alive", rewardBand: "fair"),
            Row("q3", "survive-no-downed", rewardBand: "rich"),
        };
        var corpus = MinimalCorpus(domain, pool) with { Ladder = realLadder };

        QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning); // does not throw
    }

    [Fact]
    public void Run_propagates_CheckEnoughNonSinkAnchors_through_the_real_entry_point()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var pool = new[] { Row("q1", "finish-under-hunger"), Row("q2", "survive-no-downed") }; // both sink-avoidance, offeredAtEntry is 2
        var corpus = MinimalCorpus(domain, pool);

        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning));
        Assert.Equal(QuestPreflightRules.TooFewNonSinkAnchors, ex.Rule);
    }

    /// <summary>The literal new Verify line: a pool that structurally PASSES `CheckEnoughNonSinkAnchors`
    /// (two non-sink anchors, meeting `offeredAtEntry`) but whose graph-dependent anchor can never be
    /// satisfied on ANY real rolled graph (a `cleanse-fights` naming a room kind that does not exist)
    /// leaves only ONE satisfiable anchor -- below `offeredAtEntry` -- on every seed and rung the sweep
    /// samples, and `Run` refuses naming the starved template. This is exactly the DYNAMIC, per-graph
    /// proof `CheckEnoughNonSinkAnchors`'s own STATIC, pool-only count structurally cannot make.</summary>
    [Fact]
    public void Run_refuses_naming_the_starved_template_when_a_non_sink_anchor_is_unsatisfiable_on_every_real_graph()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var pool = new[]
        {
            CountedRow("q1", "cleanse-fights", "definitely-not-a-real-room-kind", "few"), // never matches any rolled room -- 0 forever
            Row("q2", "kill-boss"),
        };
        var corpus = MinimalCorpus(domain, pool);

        var ex = Assert.Throws<QuestRefusal>(() => QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning));
        Assert.Equal(QuestPreflightRules.SatisfiabilitySweepStarved, ex.Rule);
        Assert.Equal("q1", ex.QuestId);
        Assert.Equal(domain.DomainId, ex.DomainId);
        Assert.Contains("q1", ex.Message);
    }

    /// <summary>Determinism (spec §9: "pure over (pool, Facts, delveSeed, rung, tuning)") -- two
    /// independent `Run` calls over the identical inputs reach the IDENTICAL refusal, matching
    /// `DomainGraphPreflightBridgeTests.The_verdict_is_identical_across_two_independent_Build_calls...`'s
    /// own reasoning: `SeededRng.DeriveStream` is deterministic across processes, never
    /// `string.GetHashCode()`-derived.</summary>
    [Fact]
    public void Run_reaches_the_identical_refusal_across_two_independent_calls()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        var pool = new[] { CountedRow("q1", "cleanse-fights", "definitely-not-a-real-room-kind", "few"), Row("q2", "kill-boss") };

        var first = Assert.Throws<QuestRefusal>(() => QuestPreflight.Run(MinimalCorpus(domain, pool), new[] { domain }, layouts, Tuning));
        var second = Assert.Throws<QuestRefusal>(() => QuestPreflight.Run(MinimalCorpus(domain, pool), new[] { domain }, layouts, Tuning));

        Assert.Equal(first.DomainId, second.DomainId);
        Assert.Equal(first.QuestId, second.QuestId);
        Assert.Equal(first.Rule, second.Rule);
        Assert.Equal(first.Message, second.Message);
    }

    /// <summary>The healthy-pool proof: every anchor is a structural (graph-independent) template, so
    /// the sweep passes on every one of `PreflightSampleSeeds` seeds, every raid mode and every rung --
    /// `Run` returns normally (no `QuestRefusal`) rather than needing a real graph to happen to cooperate.</summary>
    [Fact]
    public void Run_passes_a_domain_whose_whole_pool_is_structurally_satisfiable_at_every_rung()
    {
        var domain = RealDomain();
        var layouts = RealLayoutCatalog();
        // kill-boss and bring-creature-home-alive are both non-sink and always-satisfiable (QuestOffer's
        // own "the other five... hold on every valid graph" set) -- one of the two is always eligible
        // in slot 0 regardless of rung, so the offer always fills to offeredAtEntry (2).
        var pool = new[] { Row("q1", "kill-boss"), Row("q2", "bring-creature-home-alive"), Row("q3", "finish-under-hunger") };
        var corpus = MinimalCorpus(domain, pool);

        QuestPreflight.Run(corpus, new[] { domain }, layouts, Tuning); // does not throw
    }

    /// <summary>Every real shipped domain's own room palette rolls (independently proven,
    /// `DomainGraphPreflightBridgeTests`) -- this proves `Run`'s own sweep reaches every one of the six
    /// without a graph-roll exception ever escaping past its own `DelveGraphRollRejection` catch, using
    /// the SAME always-satisfiable pool as the single-domain proof above.</summary>
    [Fact]
    public void Run_reaches_every_real_shipped_domain_without_a_graph_roll_exception_escaping()
    {
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var layouts = RealLayoutCatalog();
        var pool = new[] { Row("q1", "kill-boss"), Row("q2", "bring-creature-home-alive") };
        var poolByDomain = domains.ToDictionary(d => d.DomainId, IReadOnlyList<QuestRow> (d) => pool, StringComparer.Ordinal);
        var corpus = MinimalCorpus(domains[0], pool) with { QuestPoolByDomainId = poolByDomain };

        Assert.Equal(6, domains.Count);
        QuestPreflight.Run(corpus, domains, layouts, Tuning); // does not throw
    }
}
