using FusionRpg.Core.Activity;
using FusionRpg.Core.Progression;
using Xunit;

namespace FusionRpg.Core.Tests;

public class RpgXpAwardMapTests
{
    // [InlineData] requires compile-time constants, so RpgXpAwards' config-backed properties
    // (gk-core/data/tuning/progression.v3.json) can't sit here directly — literals mirror its current
    // awards.kill/mower/plantPlace/zombieSpawn; Kill_award_uses_power_scale_one below asserts the
    // live value instead.
    [Theory]
    [InlineData(PvzActivityKinds.ZombieKilled, null, null, RpgActorKinds.Player, 0, 12.0, RpgXpReasons.Kill)]
    [InlineData(PvzActivityKinds.MowerUsed, null, null, RpgActorKinds.Player, 0, -30.0, RpgXpReasons.Mower)]
    [InlineData(PvzActivityKinds.PlantPlaced, null, 7, RpgActorKinds.Plant, 7, 8.0, RpgXpReasons.PlantPlace)]
    [InlineData(PvzActivityKinds.ZombieSpawned, null, 3, RpgActorKinds.Zombie, 3, 9.0, RpgXpReasons.ZombieSpawn)]
    public void Maps_activity_to_the_expected_type_award(
        string kind, string? result, int? typeId,
        string expectKind, int expectTypeId, double expectDelta, string expectReason)
    {
        // species-build T1.2: PlantPlaced/ZombieSpawned typeId 7/3 both resolve in the compiled
        // roster the assembly bootstrap configures, so the returned list may ALSO carry a
        // Species-kind award alongside the type award asserted here — that addition is covered
        // separately below, this test only asserts the pre-existing type-level contract still holds.
        var awards = RpgXpAwardMap.FromActivity(kind, result, typeId);
        var a = Assert.Single(awards, x => x.Kind == expectKind);
        Assert.Equal(expectTypeId, a.TypeId);
        Assert.Equal(expectDelta, a.Delta);
        Assert.Equal(expectReason, a.Reason);
        Assert.Equal(1.0, a.PowerScale);
    }

    [Fact]
    public void PlantPlaced_awardsTheResolvedSpecies_onlyWithAnExplicitEmpireGeneralClaim()
    {
        // typeId 7 resolves to 'fumeshroom' in the compiled roster (ContractTuningTestBootstrap
        // configures both the real species roster and species-progression tuning for the assembly).
        var awards = RpgXpAwardMap.FromActivity(
            PvzActivityKinds.PlantPlaced, null, 7, sourceKind: "creature.progression.v1", sourceId: "general:fumeshroom");
        var species = Assert.Single(awards, a => a.Kind == RpgActorKinds.Species);
        Assert.Equal("fumeshroom", species.ScopeKey);
        Assert.Equal(RpgXpReasons.PlantPlace, species.Reason);
        Assert.Equal(SpeciesProgressionTuningHub.Tuning.PlacementAward, species.Delta);
    }

    [Fact]
    public void ZombieSpawned_awardsTheResolvedSpecies_onlyWithAnExplicitEmpireGeneralClaim()
    {
        var awards = RpgXpAwardMap.FromActivity(
            PvzActivityKinds.ZombieSpawned, null, 3, sourceKind: "creature.progression.v1", sourceId: "general:polevaulterzombie");
        var species = Assert.Single(awards, a => a.Kind == RpgActorKinds.Species);
        Assert.Equal(RpgXpReasons.ZombieSpawn, species.Reason);
        Assert.Equal(SpeciesProgressionTuningHub.Tuning.PlacementAward, species.Delta);
    }

    [Fact]
    public void UniqueExtraSpawn_doesNotAwardEmpireSpeciesProgression()
    {
        var awards = RpgXpAwardMap.FromActivity(
            PvzActivityKinds.ZombieSpawned, null, 3,
            """{"type":3,"source":"extra","instanceId":"specimen-7"}""",
            sourceKind: "creature.progression.v1", sourceId: "unique:specimen-7:occurrence-1");

        Assert.Contains(awards, a => a.Kind == RpgActorKinds.Zombie);
        Assert.DoesNotContain(awards, a => a.Kind == RpgActorKinds.Species);
    }

    [Fact]
    public void Unique_or_commander_claims_never_fall_back_to_empire_species()
    {
        var unique = RpgXpAwardMap.FromActivity(
            PvzActivityKinds.ZombieSpawned, null, 3,
            sourceKind: "creature.progression.v1", sourceId: "unique:specimen-7:occurrence-1");
        var commander = RpgXpAwardMap.FromActivity(
            PvzActivityKinds.ZombieSpawned, null, 3,
            sourceKind: "creature.progression.v1", sourceId: "commander:dave");

        Assert.DoesNotContain(unique, a => a.Kind == RpgActorKinds.Species);
        Assert.DoesNotContain(commander, a => a.Kind == RpgActorKinds.Species);
    }

    [Fact]
    public void Placement_without_a_typed_source_claim_does_not_award_empire_species()
    {
        var awards = RpgXpAwardMap.FromActivity(PvzActivityKinds.PlantPlaced, null, 7);

        Assert.Contains(awards, a => a.Kind == RpgActorKinds.Plant);
        Assert.DoesNotContain(awards, a => a.Kind == RpgActorKinds.Species);
    }

    [Fact]
    public void Explicit_general_claim_wins_over_opaque_payload_instance_fields()
    {
        var awards = RpgXpAwardMap.FromActivity(
            PvzActivityKinds.PlantPlaced, null, 7,
            payloadJson: "{\"type\":7,\"source\":\"extra\",\"instanceId\":\"opaque\"}",
            sourceKind: "creature.progression.v1", sourceId: "general:fumeshroom");

        Assert.Contains(awards, a => a.Kind == RpgActorKinds.Species && a.ScopeKey == "fumeshroom");
    }

    [Fact]
    public void PlantPlaced_noSpeciesAward_whenTheTypeIdIsNotInTheRoster()
    {
        // Collision/unknown-type safety (T1.2 acceptance): an unmapped id must never throw, and must
        // still deliver the ordinary type award untouched.
        var awards = RpgXpAwardMap.FromActivity(PvzActivityKinds.PlantPlaced, null, -999);
        Assert.DoesNotContain(awards, a => a.Kind == RpgActorKinds.Species);
        Assert.Contains(awards, a => a.Kind == RpgActorKinds.Plant);
    }

    [Fact]
    public void Kill_award_uses_power_scale_one()
    {
        var a = Assert.Single(RpgXpAwardMap.FromActivity(PvzActivityKinds.ZombieKilled, null, 3, """{"type":3}"""));
        Assert.Equal(1.0, a.PowerScale);
        Assert.Equal(RpgXpAwards.Kill, a.Delta);
    }

    [Theory]
    [InlineData("defeat")]
    [InlineData("lose")]
    [InlineData("loss")]
    [InlineData("lost")]
    public void MatchEnded_defeat_aliases_award_defeat(string result)
    {
        var a = Assert.Single(RpgXpAwardMap.FromActivity(PvzActivityKinds.MatchEnded, result, null));
        Assert.Equal(RpgXpAwards.Defeat, a.Delta);
        Assert.Equal(RpgXpReasons.Defeat, a.Reason);
    }

    [Theory]
    [InlineData("victory")]
    [InlineData("win")]
    [InlineData(null)]
    public void MatchEnded_non_defeat_no_award(string? result)
    {
        Assert.Empty(RpgXpAwardMap.FromActivity(PvzActivityKinds.MatchEnded, result, null));
    }

    [Fact]
    public void Unknown_kind_empty()
    {
        Assert.Empty(RpgXpAwardMap.FromActivity("Nope", null, null));
    }
}
