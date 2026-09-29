using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World.Turn;

/// <summary>A fake resolver returning a fixed, District-kind, CoreTaken outcome — the real
/// DistrictAssaultResolver needs a full board/combat simulation neither this test nor this module owns;
/// this proves BattleReporting.Fight's OWN new wiring against a controlled outcome, not the siege
/// combat math itself (already covered elsewhere).</summary>
sealed class FixedOutcomeResolver : FusionRpg.Core.World.Turn.IBattleResolver
{
    public FusionRpg.Core.World.Turn.BattleOutcome Outcome { get; set; } = new();
    public FusionRpg.Core.World.Turn.BattleOutcome Resolve(
        FusionRpg.Core.World.Turn.BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed) => Outcome;
}

/// <summary>
/// `siege-loot` (drop-tables module 4, `spec-siege-loot.md`) — base-defense's first `loot_source`
/// binding, built inert-but-correct against `BattleReporting.Fight`'s own already-shipped District-only
/// exit branch (found in review: the original candidate call sites, `DistrictAssaultResolver`/
/// `DistrictAssaultPhase`, are under another session's active edit; `BattleReporting.Fight` is clean
/// and already computes the exact `EngagementExit.CoreTaken` signal needed).
/// </summary>
public class SiegeLootTests
{
    static PowerTuning Power() => PowerTuning.Build(
        schemaVersion: 1, version: 1,
        cMilli: 80_000, bMilli: 400, pinIndex: 20, pinValue: 680,
        wdMilli: 1000, waMilli: 25_000, wrMilli: 250, wzMilli: 1000,
        wmMilli: 5000, wwMilli: 5000, wfMilli: 25_000);

    [Fact]
    public void A_won_siege_resolves_a_real_loot_source_turn_qualified()
    {
        Assert.True(FusionRpg.Core.World.Turn.SiegeLoot
            .TryResolve("district-7", turn: 12, dangerBand: 6, "warcamp", Power(), out var source).IsOk);

        Assert.NotNull(source);
        Assert.Equal("siege-assault", source!.SourceKind);
        Assert.Equal("district-7:12", source.SourceId);
        Assert.Equal("drop.siege-assault.warcamp", source.TableId);
        Assert.Equal(30, source.ContentLevel); // mapLevel(6) with the shipped Wm=5
    }

    [Fact]
    public void A_retaken_district_resolves_a_distinct_source_id_each_time()
    {
        Assert.True(FusionRpg.Core.World.Turn.SiegeLoot
            .TryResolve("district-7", turn: 12, dangerBand: 6, "warcamp", Power(), out var first).IsOk);
        Assert.True(FusionRpg.Core.World.Turn.SiegeLoot
            .TryResolve("district-7", turn: 30, dangerBand: 6, "warcamp", Power(), out var second).IsOk);

        Assert.NotEqual(first!.SourceId, second!.SourceId);
        Assert.NotEqual(
            LootCorrelation.Derive(first.SourceKind, first.SourceId),
            LootCorrelation.Derive(second.SourceKind, second.SourceId));
    }

    [Fact]
    public void A_blank_sector_id_is_refused()
    {
        var rejection = FusionRpg.Core.World.Turn.SiegeLoot
            .TryResolve("  ", turn: 1, dangerBand: 4, "stable", Power(), out var source);
        Assert.Equal(AtomRejectionReason.BadParamValue, rejection.Reason);
        Assert.Null(source);
    }

    [Fact]
    public void A_blank_sector_type_id_is_refused()
    {
        var rejection = FusionRpg.Core.World.Turn.SiegeLoot
            .TryResolve("district-1", turn: 1, dangerBand: 4, "  ", Power(), out var source);
        Assert.Equal(AtomRejectionReason.BadParamValue, rejection.Reason);
        Assert.Null(source);
    }

    [Fact]
    public void The_known_source_kind_and_correlation_arm_are_both_registered()
    {
        Assert.Contains("siege-assault", DropTableValidator.KnownSourceKinds);
        Assert.Equal("loot:siege:district-7:12", LootCorrelation.Derive("siege-assault", "district-7:12"));
    }

    [Fact]
    public void BattleReporting_Fight_resolves_siege_loot_on_a_core_taken_district_exit_when_tuning_is_supplied()
    {
        // A direct unit test of the wired call, bypassing DistrictAssaultPhase/TurnEngine.Step's own
        // top-level threading -- that last hop is NOT wired (see class doc): DistrictAssaultPhase.cs
        // is under another session's active edit, so BattleReporting.Fight's own new parameter is
        // proven correct here and threaded the rest of the way once that concurrent work resolves.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var attackerId = world.Entities.First().EntityId;
        var sectorId = "ember-hollow";

        var resolver = new FixedOutcomeResolver
        {
            Outcome = new FusionRpg.Core.World.Turn.BattleOutcome
            {
                BattleId = "b-1",
                WinnerEntityId = attackerId,
                Exit = FusionRpg.Core.World.Turn.EngagementExit.CoreTaken,
            },
        };
        var request = new FusionRpg.Core.World.Turn.BattleRequest
        {
            BattleId = "b-1",
            Kind = FusionRpg.Core.World.Turn.BattleKinds.District,
            LocationId = sectorId,
            AttackerEntityId = attackerId,
        };
        var report = new FusionRpg.Core.World.Turn.TurnReport();

        FusionRpg.Core.World.Turn.BattleReporting.Fight(
            world, request, resolver, report, "assaults", seed: 1, turn: 5, powerTuning: Power());

        Assert.Contains(report.Entries, e => e.Detail.StartsWith("siege.loot:", StringComparison.Ordinal));
    }

    [Fact]
    public void BattleReporting_Fight_resolves_no_siege_loot_when_no_tuning_is_supplied_byte_identical_to_today()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        var attackerId = world.Entities.First().EntityId;
        var resolver = new FixedOutcomeResolver
        {
            Outcome = new FusionRpg.Core.World.Turn.BattleOutcome
            {
                BattleId = "b-2",
                WinnerEntityId = attackerId,
                Exit = FusionRpg.Core.World.Turn.EngagementExit.CoreTaken,
            },
        };
        var request = new FusionRpg.Core.World.Turn.BattleRequest
        {
            BattleId = "b-2",
            Kind = FusionRpg.Core.World.Turn.BattleKinds.District,
            LocationId = "ember-hollow",
            AttackerEntityId = attackerId,
        };
        var report = new FusionRpg.Core.World.Turn.TurnReport();

        FusionRpg.Core.World.Turn.BattleReporting.Fight(world, request, resolver, report, "assaults", seed: 1, turn: 5);

        Assert.DoesNotContain(report.Entries, e => e.Detail.StartsWith("siege.loot:", StringComparison.Ordinal));
    }

    [Fact]
    public void At_least_two_real_district_type_tables_differ_substantively_not_just_by_id()
    {
        var corpus = FusionRpg.Core.Tests.Items.DropVolumeCorpusTests.Corpus();
        var byId = corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);

        var bossLair = byId["drop.siege-assault.boss-lair"];
        var stable = byId["drop.siege-assault.stable"];

        var bossBonusGroup = bossLair.Groups.Single(g => g.GroupKey == "siege-bonus");
        var stableBonusGroup = stable.Groups.Single(g => g.GroupKey == "siege-bonus");

        Assert.DoesNotContain(bossBonusGroup.Entries, e => e.Kind == DropEntryKind.Nothing);
        Assert.Contains(stableBonusGroup.Entries, e => e.Kind == DropEntryKind.Nothing);

        Assert.All(bossLair.Groups.SelectMany(g => g.Entries).Where(e => e.Kind == DropEntryKind.Table),
            e => Assert.Equal(AffixChannels.Boss, e.AffixChannel));
        Assert.All(stable.Groups.SelectMany(g => g.Entries).Where(e => e.Kind == DropEntryKind.Table),
            e => Assert.Equal(AffixChannels.Drop, e.AffixChannel));
    }

    [Fact]
    public void The_real_siege_assault_corpus_validates_against_the_import_validator()
    {
        var corpus = FusionRpg.Core.Tests.Items.DropVolumeCorpusTests.Corpus();
        var verdict = DropTableValidator.Validate(corpus.Sources, corpus.Tables, FusionRpg.Core.Tests.Items.DropVolumeTests.Tuning());
        Assert.True(verdict.IsOk, verdict.ToString());
    }
}
