using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;
// This test file's own namespace ends in `.Encounter`, which shadows the `Encounter` class name from
// `FusionRpg.Core.Delve.Encounter` -- an unqualified `Build(...)` is ambiguous with the
// enclosing namespace segment. `using static` sidesteps it instead of qualifying every call site.
using static FusionRpg.Core.Delve.Encounter.Encounter;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// D2.2 (spec-encounter-generator.md §1-4) — `Encounter.Build`: goldens for pack/party/boss,
/// determinism over 256 seeds, θ = Θ_room + thetaOffset(species). `EncounterTuningHub`/
/// `DungeonTuningHub` are configured for the whole assembly by
/// `Dungeon.DungeonHubTestBootstrap`'s module initializer — every tuning read here is the real,
/// shipped `encounter.v1.json`/`dungeon.v1.json`, not a hand-built stand-in.
/// </summary>
public class EncounterTests
{
    static readonly CreatureThreatTuning ThreatTuning = RealAnchorCorpusFixture.ThreatTuning;
    static readonly EncounterTuning Tuning = EncounterTuningHub.Tuning;
    static readonly RaidModeTuning Solo = DungeonTuningHub.Tuning.RaidModes["solo"];
    static readonly DifficultyRungTuning Hard = DungeonTuningHub.Tuning.Rungs["hard"]; // the identity row -- every delta 0

    // A small, HAND-CONTROLLED fixture corpus, independent of the real (growing) species corpus, so
    // goldens stay byte-stable regardless of what other work adds to gk-data/packs/fusion/data/seed/creatures/species/.
    static ConcreteAnchor A(string id, string threatBand, string aptitude, string reach, string tp, ElementTypeId element) => new()
    {
        SpeciesId = id,
        ThreatBand = threatBand,
        ThreatRung = ThreatTuning.Thresholds.First(t => t.Id == threatBand).Rung,
        AptitudePrimary = aptitude,
        Reach = Enum.Parse<EncounterReach>(reach, ignoreCase: true),
        TargetPreference = Enum.Parse<TargetPreference>(tp, ignoreCase: true),
        ElementPrimary = element,
        ElementSecondary = null,
        TraitPool = new[] { "fixture-trait" },
        AttackIntervalMs = 1000,
        GameTypeId = 1,
    };

    static readonly IReadOnlyList<ConcreteAnchor> FixtureCorpus = new[]
    {
        A("bastion-a", "raider", "Bulwark", "short", "backline", ElementTypeId.Fire),
        A("bastion-b", "raider", "Retribution", "short", "backline", ElementTypeId.Fire),
        A("bastion-c", "raider", "Precision", "short", "backline", ElementTypeId.Ice),
        A("force-a", "raider", "Might", "melee", "frontline", ElementTypeId.Fire),
        A("force-b", "raider", "Onslaught", "melee", "frontline", ElementTypeId.Ice),
        A("boss-tyrant", "tyrant", "Onslaught", "short", "frontline", ElementTypeId.Earth),
    };

    static EncounterSlot BastionSlot(string countBand = "few") =>
        new(Posture.Bastion, EncounterReach.Short, TargetPreference.Backline, countBand);

    static EncounterAnchor PackAnchor(string countBand = "few") => new(
        Formation.Pack,
        Slots: new[] { BastionSlot(countBand) },
        RankOrder: new[] { 0 },
        ElementSpread: ElementSpreadMode.Mono,
        ThreatWindow: new ThreatWindow(1, 10),
        BossSpeciesRef: null);

    static EncounterAnchor PartyAnchor() => new(
        Formation.Party,
        Slots: new[] { BastionSlot("lone"), new EncounterSlot(Posture.Force, EncounterReach.Melee, TargetPreference.Frontline, "lone") },
        RankOrder: new[] { 1, 0 }, // deliberately not identity, to prove RankOrder actually reorders
        ElementSpread: ElementSpreadMode.Dual,
        ThreatWindow: new ThreatWindow(1, 10),
        BossSpeciesRef: null);

    static EncounterAnchor BossAnchor() => new(
        Formation.Boss,
        Slots: Array.Empty<EncounterSlot>(),
        RankOrder: Array.Empty<int>(),
        ElementSpread: ElementSpreadMode.Rainbow,
        ThreatWindow: new ThreatWindow(1, 10),
        BossSpeciesRef: "boss-tyrant");

    // ---- goldens ----

    [Fact]
    public void Pack_golden()
    {
        var half = Build(PackAnchor(), roomTheta: 70, ElementTypeId.Fire, Solo, Hard, seed: 12345,
            FixtureCorpus, Tuning, ThreatTuning);

        Assert.Equal(Tuning.FormationPackW, half.W);
        Assert.InRange(half.Enemies.Count, 2, 3); // "few" band
        Assert.All(half.Enemies, e => Assert.Equal("wave", e.Side));
        Assert.All(half.Enemies, e => Assert.StartsWith("bastion-", e.SpeciesId));
        Assert.All(half.Enemies, e => Assert.Equal(83, e.Level)); // 70 + raider's +13, ladder spec §1's own worked row
        Assert.Equal(Formation.Pack, half.Cell.Formation);
        Assert.All(half.Cell.PostureMultiset, p => Assert.Equal(Posture.Bastion, p));
        Assert.Equal(half.Enemies.Count, half.Cell.PostureMultiset.Count);
        Assert.Empty(half.Warnings);

        // Keys are sequential wave:0.. in emit order.
        Assert.Equal(Enumerable.Range(0, half.Enemies.Count).Select(i => $"wave:{i}"), half.Enemies.Select(e => e.Key));
    }

    [Fact]
    public void Party_golden_respects_rankOrder_over_slot_declaration_order()
    {
        var half = Build(PartyAnchor(), roomTheta: 70, ElementTypeId.Fire, Solo, Hard, seed: 555,
            FixtureCorpus, Tuning, ThreatTuning);

        Assert.Equal(Tuning.FormationPartyW, half.W);
        // rankOrder = [1,0]: slot 1 (force, lone->1) emits FIRST, then slot 0 (bastion, lone->1).
        Assert.Equal(2, half.Enemies.Count);
        Assert.StartsWith("force-", half.Enemies[0].SpeciesId);
        Assert.StartsWith("bastion-", half.Enemies[1].SpeciesId);
        Assert.Equal(Formation.Party, half.Cell.Formation);
    }

    [Fact]
    public void Boss_golden()
    {
        var half = Build(BossAnchor(), roomTheta: 100, climate: null, Solo, Hard, seed: 777,
            FixtureCorpus, Tuning, ThreatTuning);

        var boss = Assert.Single(half.Enemies);
        Assert.Equal("boss-tyrant", boss.SpeciesId);
        Assert.Equal("wave:0", boss.Key);
        Assert.Equal(127, boss.Level); // 100 + tyrant's +27, ladder spec §1's own worked row
        Assert.Equal(checked(Solo.BossW + Hard.BossWDelta), half.W);
        Assert.Equal(Formation.Boss, half.Cell.Formation);
        Assert.Equal(new[] { Posture.Force }, half.Cell.PostureMultiset); // Onslaught -> Force
        Assert.Equal(Tuning.FormationBossRankSpan, boss.RankSpan); // D2.3: written only for the boss role
    }

    [Fact]
    public void Only_the_boss_setup_carries_a_rankSpan_never_a_regular_slot_pick()
    {
        var half = Build(PartyAnchor(), 70, ElementTypeId.Fire, Solo, Hard, seed: 555, FixtureCorpus, Tuning, ThreatTuning);
        Assert.All(half.Enemies, e => Assert.Null(e.RankSpan));
    }

    // ---- theta = Θ_room + thetaOffset(species), the sum nothing else computes ----

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(3000)]
    public void Every_enemy_level_is_roomTheta_plus_the_species_threat_offset(int roomTheta)
    {
        var half = Build(PackAnchor("many"), roomTheta, ElementTypeId.Fire, Solo, Hard, seed: 9,
            FixtureCorpus, Tuning, ThreatTuning);

        var expected = checked(roomTheta + ThreatTuning.OffsetFor("raider"));
        Assert.All(half.Enemies, e => Assert.Equal(expected, e.Level));
    }

    // ---- determinism over 256 seeds ----

    [Fact]
    public void Build_is_byte_identical_on_replay_across_256_seeds()
    {
        for (ulong seed = 0; seed < 256; seed++)
        {
            var a = Build(PartyAnchor(), 70, ElementTypeId.Fire, Solo, Hard, seed, FixtureCorpus, Tuning, ThreatTuning);
            var b = Build(PartyAnchor(), 70, ElementTypeId.Fire, Solo, Hard, seed, FixtureCorpus, Tuning, ThreatTuning);

            Assert.Equal(a.Enemies.Select(e => (e.Key, e.SpeciesId, e.Level)), b.Enemies.Select(e => (e.Key, e.SpeciesId, e.Level)));
            Assert.Equal(a.W, b.W);
            Assert.Equal(a.Cell.PostureMultiset, b.Cell.PostureMultiset);
            Assert.Equal(a.Cell.ElementSpread, b.Cell.ElementSpread);
        }
    }

    [Fact]
    public void Different_seeds_can_draw_differently_the_draw_actually_depends_on_the_seed()
    {
        var results = new HashSet<string>();
        for (ulong seed = 0; seed < 40; seed++)
        {
            var half = Build(PackAnchor("many"), 70, ElementTypeId.Fire, Solo, Hard, seed, FixtureCorpus, Tuning, ThreatTuning);
            results.Add(string.Join(",", half.Enemies.Select(e => e.SpeciesId)));
        }
        Assert.True(results.Count > 1, "expected the draw to vary across at least some of 40 different seeds");
    }

    // ---- spread resolution ----

    [Fact]
    public void Mono_spread_is_exactly_the_climate_element()
    {
        // "lone" (1-1), not the default "few" (2-3): the fixture corpus has exactly one Ice-element
        // bastion (bastion-c) -- mono narrows the spread to {Ice} alone, and a "few" count would
        // trip the same-species cap on a single-candidate pool, which is a fixture-sizing fact, not
        // what this test is about.
        var half = Build(PackAnchor("lone"), 70, ElementTypeId.Ice, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning);
        Assert.Equal(new HashSet<ElementTypeId> { ElementTypeId.Ice }, half.Cell.ElementSpread);
    }

    [Fact]
    public void Rainbow_spread_is_all_six_elements_regardless_of_climate()
    {
        var half = Build(BossAnchor(), 100, ElementTypeId.Fire, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning);
        Assert.Equal(Enum.GetValues<ElementTypeId>().ToHashSet(), half.Cell.ElementSpread);
    }

    [Fact]
    public void Dual_spread_is_climate_plus_exactly_one_other()
    {
        var half = Build(PartyAnchor(), 70, ElementTypeId.Fire, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning);
        Assert.Equal(2, half.Cell.ElementSpread.Count);
        Assert.Contains(ElementTypeId.Fire, half.Cell.ElementSpread);
    }

    [Fact]
    public void A_null_climate_is_all_six_regardless_of_the_anchors_own_mode()
    {
        var half = Build(PackAnchor(), 70, climate: null, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning);
        Assert.Equal(Enum.GetValues<ElementTypeId>().ToHashSet(), half.Cell.ElementSpread);
    }

    // ---- refusals ----

    [Fact]
    public void Count_below_1_after_the_rung_delta_refuses()
    {
        var badRung = Hard with { EnemyCountDeltaFight = -100 };
        var ex = Assert.Throws<EncounterRefusal>(() =>
            Build(PackAnchor(), 70, ElementTypeId.Fire, Solo, badRung, seed: 1, FixtureCorpus, Tuning, ThreatTuning));
        Assert.Contains("< 1", ex.Message);
    }

    [Fact]
    public void An_unknown_countBand_refuses_by_name()
    {
        var anchor = PackAnchor(countBand: "not-a-real-band");
        var ex = Assert.Throws<EncounterRefusal>(() =>
            Build(anchor, 70, ElementTypeId.Fire, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning));
        Assert.Contains("not-a-real-band", ex.Message);
    }

    [Fact]
    public void An_unfillable_slot_propagates_as_EncounterRefusal()
    {
        var anchor = new EncounterAnchor(Formation.Pack,
            new[] { new EncounterSlot(Posture.Bastion, EncounterReach.Siege, null, "few") }, // no siege anchors in the fixture
            new[] { 0 }, ElementSpreadMode.Mono, new ThreatWindow(1, 10), null);

        Assert.Throws<EncounterRefusal>(() =>
            Build(anchor, 70, ElementTypeId.Fire, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning));
    }

    [Fact]
    public void An_out_of_range_rankOrder_index_throws()
    {
        var anchor = PackAnchor() with { RankOrder = new[] { 5 } };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Build(anchor, 70, ElementTypeId.Fire, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning));
    }

    [Fact]
    public void A_boss_formation_with_no_bossSpeciesRef_throws()
    {
        var anchor = BossAnchor() with { BossSpeciesRef = null };
        Assert.Throws<InvalidOperationException>(() =>
            Build(anchor, 100, null, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning));
    }

    [Fact]
    public void A_bossSpeciesRef_not_in_the_corpus_throws()
    {
        var anchor = BossAnchor() with { BossSpeciesRef = "nonexistent-species" };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(anchor, 100, null, Solo, Hard, seed: 1, FixtureCorpus, Tuning, ThreatTuning));
        Assert.Contains("nonexistent-species", ex.Message);
    }

    [Fact]
    public void A_boss_below_the_floor_rung_throws()
    {
        var lowRungBoss = A("weak-boss", "nuisance", "Onslaught", "short", "frontline", ElementTypeId.Fire);
        var corpus = FixtureCorpus.Append(lowRungBoss).ToList();
        var anchor = BossAnchor() with { BossSpeciesRef = "weak-boss" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(anchor, 100, null, Solo, Hard, seed: 1, corpus, Tuning, ThreatTuning));
        Assert.Contains("below the boss floor rung", ex.Message);
    }

    // ---- null-argument validation ----

    [Fact]
    public void Null_arguments_throw()
    {
        var anchor = PackAnchor();
        Assert.Throws<ArgumentNullException>(() => Build(null!, 70, ElementTypeId.Fire, Solo, Hard, 1, FixtureCorpus, Tuning, ThreatTuning));
        Assert.Throws<ArgumentNullException>(() => Build(anchor, 70, ElementTypeId.Fire, null!, Hard, 1, FixtureCorpus, Tuning, ThreatTuning));
        Assert.Throws<ArgumentNullException>(() => Build(anchor, 70, ElementTypeId.Fire, Solo, null!, 1, FixtureCorpus, Tuning, ThreatTuning));
        Assert.Throws<ArgumentNullException>(() => Build(anchor, 70, ElementTypeId.Fire, Solo, Hard, 1, null!, Tuning, ThreatTuning));
        Assert.Throws<ArgumentNullException>(() => Build(anchor, 70, ElementTypeId.Fire, Solo, Hard, 1, FixtureCorpus, null!, ThreatTuning));
        Assert.Throws<ArgumentNullException>(() => Build(anchor, 70, ElementTypeId.Fire, Solo, Hard, 1, FixtureCorpus, Tuning, null!));
    }

    // ---- against the real corpus: no crash over a real, populated slot ----

    [Fact]
    public void Building_against_the_real_corpus_does_not_crash_for_a_populated_slot()
    {
        var banded = RealAnchorCorpusFixture.All.Where(a => a.ThreatBand is not null).ToList();
        var anchor = PackAnchor("few"); // bastion|short|backline -- 32 real candidates, per D2.1's own finding

        var half = Build(anchor, 70, ElementTypeId.Fire, Solo, Hard, seed: 2026, banded, Tuning, ThreatTuning);

        Assert.InRange(half.Enemies.Count, 2, 3);
    }
}
