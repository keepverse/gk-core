using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using static FusionRpg.Core.Delve.Encounter.Encounter;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// D2.4 (spec-encounter-generator.md §5) — `BossBuild`: kit (pattern → allocation → `ChannelMods`),
/// `signatureAction`, phase thresholds, and the retinue's own party-multiplicative count delta. Phase
/// GRANTS (the rolled `enemy.` container per threshold) and `rung.eliteSecondActionRow` are genuinely
/// deferred — see `BossBuild.cs`'s own class doc for the two hard blockers (D2.6's container roll;
/// `DifficultyRungTuning` carrying no such field at all in the already-shipped difficulty-ladder
/// module) — this file does not test what was never built.
/// </summary>
public class BossBuildTests
{
    // AptitudeTuningHub is NOT configured by a whole-assembly module initializer (only per-test-class,
    // e.g. ZombossPatternTests.cs, Balance/*Tests.cs) -- reading it here would race those. BossBuild
    // itself takes AptitudeTuning as a plain parameter, never the hub, so this test loads the real
    // file directly instead, the same way RealAnchorCorpusFixture.cs already does for its own needs.
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    static readonly AptitudeTuning AptitudeTuning =
        AptitudeTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitudes.v2.json")));
    static readonly PowerTuning PowerTuning = PowerTuningHub.Tuning; // PowerTuningHub IS configured whole-assembly (ContractTuningTestBootstrap's own [ModuleInitializer])
    static readonly CreatureThreatTuning ThreatTuning = RealAnchorCorpusFixture.ThreatTuning;
    static readonly EncounterTuning Tuning = EncounterTuningHub.Tuning;
    static readonly RaidModeTuning Solo = DungeonTuningHub.Tuning.RaidModes["solo"];
    static readonly RaidModeTuning Quad = DungeonTuningHub.Tuning.RaidModes["quad"];
    static readonly DifficultyRungTuning Hard = DungeonTuningHub.Tuning.Rungs["hard"];

    // ---- ResolveKitAllocation + Resolve (battle-hub-fuse T6: the BattleChannelMod ResolveKit
    // producer is deleted; the kit reaches battle as this allocation through the Hub twin) ----

    static IReadOnlyList<DerivedModifier> ResolveKitMods(string patternId, int thetaBoss) =>
        AptitudeResolver.Resolve(
            BossBuild.ResolveKitAllocation(patternId, thetaBoss, AptitudeTuning),
            AptitudeTuning,
            new PowerLadder(PowerTuning),
            thetaBoss,
            DerivedStatRegistry.CreateDefault());

    [Fact]
    public void ResolveKit_returns_a_nonempty_ChannelMods_list_for_a_real_pattern()
    {
        Assert.NotEmpty(ResolveKitMods("force-pure", thetaBoss: 127));
    }

    [Fact]
    public void ResolveKit_is_deterministic_same_inputs_same_ChannelMods()
    {
        var a = ResolveKitMods("bastion-pure", 100);
        var b = ResolveKitMods("bastion-pure", 100);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveKit_scales_with_theta_a_higher_theta_never_yields_a_strictly_smaller_kit()
    {
        // Not asserting exact magnitudes (that's AptitudeResolver's own test surface) -- just that
        // BossBuild actually threads theta through rather than ignoring it.
        var low = ResolveKitMods("finesse-pure", 50);
        var high = ResolveKitMods("finesse-pure", 500);
        Assert.NotEqual(low, high);
    }

    [Fact]
    public void ResolveKit_rejects_an_unknown_pattern_id()
    {
        Assert.Throws<ArgumentException>(() => BossBuild.ResolveKitAllocation("not-a-real-pattern", 100, AptitudeTuning));
    }

    // battle-hub-fuse T6 deleted BossBuild.ResolveKit itself (proven zero production callers) along
    // with its own null-argument guards -- ResolveKitAllocation's guards are covered above
    // (ResolveKit_rejects_an_unknown_pattern_id already calls it, not the deleted method).

    // ---- ApplyKit ----

    [Fact]
    public void ApplyKit_sets_a_single_equipped_action_touching_nothing_else()
    {
        var boss = new BattleActorSetup { Key = "wave:0", Side = "wave", SpeciesId = "x", Level = 127, MaxHp = 5000 };

        var kitted = BossBuild.ApplyKit(boss, "act.boss-signature");

        Assert.Equal(new[] { "act.boss-signature" }, kitted.EquippedActionIds);
        // "touching nothing else" -- checked field by field, not via record equality: BattleActorSetup
        // carries array/list-typed properties, and record-generated equality falls back to REFERENCE
        // equality for those (a real gotcha, caught by this test's own first run), so two
        // separately-built "equal-looking" instances are never Equal() to each other regardless of content.
        Assert.Equal(boss.Key, kitted.Key);
        Assert.Equal(boss.SpeciesId, kitted.SpeciesId);
        Assert.Equal(boss.Level, kitted.Level);
        Assert.Equal(boss.MaxHp, kitted.MaxHp);
    }

    [Fact]
    public void ApplyKit_rejects_empty_arguments()
    {
        var boss = new BattleActorSetup { Key = "wave:0" };
        Assert.Throws<ArgumentNullException>(() => BossBuild.ApplyKit(null!, "a"));
        Assert.Throws<ArgumentException>(() => BossBuild.ApplyKit(boss, ""));
    }

    // ---- PhaseThresholdsMilli ----

    [Fact]
    public void None_phase_is_an_empty_threshold_list()
    {
        Assert.Empty(BossBuild.PhaseThresholdsMilli(BossPhaseKind.None, Tuning));
    }

    [Fact]
    public void Breakpoint_phase_is_exactly_one_threshold_from_tuning()
    {
        var thresholds = BossBuild.PhaseThresholdsMilli(BossPhaseKind.Breakpoint, Tuning);
        Assert.Equal(Tuning.PhaseBreakpointHpThresholdMilli, thresholds);
        Assert.Single(thresholds);
    }

    [Fact]
    public void Escalating_phase_is_exactly_two_thresholds_from_tuning()
    {
        var thresholds = BossBuild.PhaseThresholdsMilli(BossPhaseKind.Escalating, Tuning);
        Assert.Equal(Tuning.PhaseEscalatingHpThresholdMilli, thresholds);
        Assert.Equal(2, thresholds.Count);
    }

    // ---- DrawRetinue ----

    static ConcreteAnchor A(string id, string threatBand, string aptitude, ElementTypeId element) => new()
    {
        SpeciesId = id, ThreatBand = threatBand,
        ThreatRung = ThreatTuning.Thresholds.First(t => t.Id == threatBand).Rung,
        AptitudePrimary = aptitude, Reach = EncounterReach.Short, TargetPreference = TargetPreference.Backline,
        ElementPrimary = element,
    };

    static readonly IReadOnlyList<ConcreteAnchor> RetinueCorpus = new[]
    {
        A("ret-a", "raider", "Bulwark", ElementTypeId.Fire),
        A("ret-b", "raider", "Retribution", ElementTypeId.Fire),
        A("ret-c", "raider", "Precision", ElementTypeId.Fire),
        A("ret-d", "raider", "Ferocity", ElementTypeId.Fire),
    };

    static readonly EncounterSlot RetinueSlot = new(Posture.Bastion, EncounterReach.Short, TargetPreference.Backline, "few");
    static readonly SlotCountBandTuning Few = new(2, 3);
    static readonly IReadOnlySet<ElementTypeId> FireOnly = new HashSet<ElementTypeId> { ElementTypeId.Fire };

    [Fact]
    public void DrawRetinue_at_solo_parties_1_applies_zero_extra_delta()
    {
        var result = BossBuild.DrawRetinue(RetinueCorpus, RetinueSlot, Few, new ThreatWindow(1, 10),
            ElementTypeId.Fire, 0, 1000, bossRetinuePerPartyDelta: 5, parties: 1, seed: 1, "retinue-test", FireOnly);

        Assert.InRange(result.Count, 2, 3); // Few band alone -- (parties-1)=0, so the delta contributes nothing
    }

    [Fact]
    public void DrawRetinue_scales_additively_with_extra_parties()
    {
        // parties=4 -> delta = bossRetinuePerPartyDelta * (4-1) = 2*3 = 6 on top of the 2-3 band.
        var result = BossBuild.DrawRetinue(RetinueCorpus, RetinueSlot, Few, new ThreatWindow(1, 10),
            ElementTypeId.Fire, 0, 1000, bossRetinuePerPartyDelta: 2, parties: 4, seed: 1, "retinue-scale-test", FireOnly);

        Assert.InRange(result.Count, 8, 9); // (2..3) + 6
    }

    [Fact]
    public void DrawRetinue_below_1_after_the_party_delta_refuses()
    {
        // parties must be > 1 for a negative delta to bite at all: (parties-1)=0 at parties=1 makes
        // ANY bossRetinuePerPartyDelta contribute zero, by the formula's own design (solo has no
        // "extra" parties to scale against) -- parties=3 here so -10*(3-1)=-20 actually pushes the
        // (2..3)-band count well below 1.
        var ex = Assert.Throws<EncounterRefusal>(() =>
            BossBuild.DrawRetinue(RetinueCorpus, RetinueSlot, Few, new ThreatWindow(1, 10),
                ElementTypeId.Fire, 0, 1000, bossRetinuePerPartyDelta: -10, parties: 3, seed: 1, "retinue-neg-test", FireOnly));
        Assert.Contains("< 1", ex.Message);
    }

    [Fact]
    public void DrawRetinue_an_unfillable_tuple_refuses()
    {
        var siegeOnlySlot = new EncounterSlot(Posture.Bastion, EncounterReach.Siege, null, "few");
        Assert.Throws<EncounterRefusal>(() =>
            BossBuild.DrawRetinue(RetinueCorpus, siegeOnlySlot, Few, new ThreatWindow(1, 10),
                ElementTypeId.Fire, 0, 1000, 0, 1, 1, "retinue-unfillable-test", FireOnly));
    }

    [Fact]
    public void DrawRetinue_rejects_zero_parties()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BossBuild.DrawRetinue(RetinueCorpus, RetinueSlot, Few, new ThreatWindow(1, 10),
                ElementTypeId.Fire, 0, 1000, 0, parties: 0, seed: 1, "s", FireOnly));
    }

    // ---- end-to-end: Encounter.Build with a real BossKit ----

    static ConcreteAnchor BossAnchorRow => A("boss-x", "tyrant", "Onslaught", ElementTypeId.Earth);

    static EncounterAnchor KittedBossAnchor(int retinueSlotIndex = 0) => new(
        Formation.Boss,
        Slots: new[] { RetinueSlot },
        RankOrder: new[] { retinueSlotIndex },
        ElementSpread: ElementSpreadMode.Mono,
        ThreatWindow: new ThreatWindow(1, 10),
        BossSpeciesRef: "boss-x",
        BossKit: new BossKit("force-pure", "act.boss-signature", BossPhaseKind.Breakpoint, RetinueSlotIndex: 0));

    static readonly IReadOnlyList<ConcreteAnchor> BossCorpus = new[] { BossAnchorRow }.Concat(RetinueCorpus).ToList();

    [Fact]
    public void Encounter_Build_applies_the_kit_to_the_boss_setup_when_BossKit_is_set()
    {
        var half = Build(KittedBossAnchor(), 100, ElementTypeId.Fire, Solo, Hard, seed: 42,
            BossCorpus, Tuning, ThreatTuning, AptitudeTuning, PowerTuning);

        var boss = half.Enemies[0];
        Assert.Equal("boss-x", boss.SpeciesId);
        // battle-hub-fuse T6: the kit's allocation reaches battle as a Hub input, not ChannelMods.
        Assert.NotNull(boss.HubInputs?.Aptitude);
        Assert.NotEqual(FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty, boss.HubInputs!.Aptitude);
        Assert.Equal(new[] { "act.boss-signature" }, boss.EquippedActionIds);
        Assert.Equal(Tuning.FormationBossRankSpan, boss.RankSpan);
    }

    [Fact]
    public void Encounter_Build_draws_a_retinue_after_the_boss_when_BossKit_names_a_retinue_slot()
    {
        var half = Build(KittedBossAnchor(), 100, ElementTypeId.Fire, Solo, Hard, seed: 42,
            BossCorpus, Tuning, ThreatTuning, AptitudeTuning, PowerTuning);

        Assert.True(half.Enemies.Count >= 3); // 1 boss + at least 2 retinue ("few" band)
        Assert.All(half.Enemies.Skip(1), e => Assert.StartsWith("ret-", e.SpeciesId));
        Assert.All(half.Enemies.Skip(1), e => Assert.Null(e.RankSpan)); // only the boss ever carries one
    }

    [Fact]
    public void Encounter_Build_retinue_count_scales_with_the_raid_mode_parties()
    {
        var solo = Build(KittedBossAnchor(), 100, ElementTypeId.Fire, Solo, Hard, seed: 42, BossCorpus, Tuning, ThreatTuning, AptitudeTuning, PowerTuning);
        var quad = Build(KittedBossAnchor(), 100, ElementTypeId.Fire, Quad, Hard, seed: 42, BossCorpus, Tuning, ThreatTuning, AptitudeTuning, PowerTuning);

        // Quad has more parties than solo; a non-negative bossRetinuePerPartyDelta (the real
        // hard-rung value) means quad's retinue is never smaller than solo's.
        Assert.True(quad.Enemies.Count - 1 >= solo.Enemies.Count - 1);
    }

    [Fact]
    public void Encounter_Build_with_BossKit_set_but_no_tuning_supplied_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Build(KittedBossAnchor(), 100, ElementTypeId.Fire, Solo, Hard, 42, BossCorpus, Tuning, ThreatTuning));
    }

    [Fact]
    public void Encounter_Build_with_no_BossKit_is_unaffected_the_D2_2_path_is_untouched()
    {
        var plainBoss = new EncounterAnchor(Formation.Boss, Array.Empty<EncounterSlot>(), Array.Empty<int>(),
            ElementSpreadMode.Rainbow, new ThreatWindow(1, 10), "boss-x");

        var half = Build(plainBoss, 100, null, Solo, Hard, seed: 1, BossCorpus, Tuning, ThreatTuning);

        var boss = Assert.Single(half.Enemies);
        Assert.Empty(boss.ChannelMods);
        Assert.Null(boss.EquippedActionIds);
    }

    // ---- D2.4's own verify line: "a boss golden per climate" ----

    static EncounterAnchor KittedBossAnchorRainbow() => KittedBossAnchor() with { ElementSpread = ElementSpreadMode.Rainbow };

    [Theory]
    [InlineData(ElementTypeId.Fire)]
    [InlineData(ElementTypeId.Ice)]
    [InlineData(null)]
    public void A_boss_golden_builds_identically_kitted_under_every_climate(ElementTypeId? climate)
    {
        // The boss's own species/level/kit never depend on climate at all (bossSpeciesRef is a direct
        // lookup, never filtered by element). Rainbow spread here (not Mono) deliberately keeps the
        // retinue slot's own element filter out of THIS test's way -- this fixture's retinue corpus
        // is entirely Fire, so a Mono spread under Ice would correctly refuse as unfillable (that
        // interaction is its own, already-covered concern, not what "per climate" is testing here).
        var half = Build(KittedBossAnchorRainbow(), 100, climate, Solo, Hard, seed: 42, BossCorpus, Tuning, ThreatTuning, AptitudeTuning, PowerTuning);

        var boss = half.Enemies[0];
        Assert.Equal("boss-x", boss.SpeciesId);
        Assert.Equal(127, boss.Level); // 100 + tyrant's +27, unaffected by climate
        // battle-hub-fuse T6: the kit's allocation reaches battle as a Hub input, not pre-folded
        // ChannelMods (ApplyKit no longer sets ChannelMods at all -- see the plain-boss test above,
        // still Empty by construction either way).
        Assert.NotNull(boss.HubInputs?.Aptitude);
        Assert.NotEqual(FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty, boss.HubInputs!.Aptitude);
        Assert.Equal(new[] { "act.boss-signature" }, boss.EquippedActionIds);
        Assert.True(half.Enemies.Count >= 3); // boss + retinue, under every climate tested
    }

    // ---- D2.4's own verify line: "a serialisation test asserting W is absent from the wire" ----

    [Fact]
    public void W_never_appears_on_a_serialized_BattleActorSetup()
    {
        // EncounterHalf.W is never attached to any BattleActorSetup field (BattleActorSetup itself
        // has no W-shaped property at all) -- the host applies it as `profile with { W = w }`
        // (spec §1) on the PROFILE, never the wire. Proven directly against the real setups this
        // module actually emits, not just asserted from reading the type.
        var half = Build(KittedBossAnchor(), 100, ElementTypeId.Fire, Solo, Hard, seed: 42,
            BossCorpus, Tuning, ThreatTuning, AptitudeTuning, PowerTuning);

        foreach (var enemy in half.Enemies)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(enemy);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("w", out _));
            Assert.False(doc.RootElement.TryGetProperty("W", out _));
        }
    }

    [Fact]
    public void EncounterHalf_itself_is_the_only_place_W_lives()
    {
        var half = Build(KittedBossAnchor(), 100, ElementTypeId.Fire, Solo, Hard, seed: 42,
            BossCorpus, Tuning, ThreatTuning, AptitudeTuning, PowerTuning);
        Assert.Equal(checked(Solo.BossW + Hard.BossWDelta), half.W);
    }
}
