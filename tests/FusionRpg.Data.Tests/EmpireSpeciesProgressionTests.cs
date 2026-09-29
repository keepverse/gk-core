using System;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `ai-empire-species` EP4.13 - the one species-level reader. It reads the re-keyed
/// `rpg_actor_progression` (rebuilt once by `save-identity`, existing rows landing on
/// `(SaveId, EmpireId.Dave)`), so a Dave read is the pre-migration value and another save's crediting can
/// never move it.
/// </summary>
public class EmpireSpeciesProgressionTests : IDisposable
{
    const int FumeshroomCreatureTypeId = 60007;
    const int PolevaulterCreatureTypeId = 10003;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public EmpireSpeciesProgressionTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>Writes a species row directly - the STATE the progression writer leaves - so the reader
    /// can be tested without driving a whole award path.</summary>
    void SeedSpeciesRow(long saveId, string empireId, int typeId, long level)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_actor_progression(save_id, empire_id, kind, type_id, level, xp, highest_level, updated_utc)
            VALUES ($s, $e, 'species', $t, $l, 0, $l, $t0);
            """;
        cmd.Parameters.AddWithValue("$s", saveId);
        cmd.Parameters.AddWithValue("$e", empireId);
        cmd.Parameters.AddWithValue("$t", typeId);
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$t0", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_dave_read_equals_the_row_the_progression_writer_kept()
    {
        SeedSpeciesRow(1, EmpireId.Dave.Value, FumeshroomCreatureTypeId, 7);

        var viaReader = _store.SpeciesLevelOf(new SaveId(1), EmpireId.Dave, FumeshroomCreatureTypeId);
        var viaProgressionRow = _store.GetRpgActor(1, RpgActorKinds.Species, FumeshroomCreatureTypeId)!.Level;

        Assert.Equal(7, viaReader);
        Assert.Equal(viaProgressionRow, viaReader);
    }

    [Fact]
    public void A_species_nobody_has_levelled_reads_level_one()
    {
        Assert.Equal(1, _store.SpeciesLevelOf(new SaveId(1), EmpireId.Dave, FumeshroomCreatureTypeId));
        Assert.Equal(1, _store.SpeciesLevelOf(new SaveId(1), EmpireId.Zomboss, PolevaulterCreatureTypeId));
    }

    [Fact]
    public void One_saves_crediting_leaves_another_save_at_level_one()
    {
        // The same species, two saves: R3 keys every row (SaveId, EmpireId), so a level on save A is
        // invisible to save B - the property Zomboss's own track depends on.
        SeedSpeciesRow(1, EmpireId.Zomboss.Value, PolevaulterCreatureTypeId, 12);

        Assert.Equal(12, _store.SpeciesLevelOf(new SaveId(1), EmpireId.Zomboss, PolevaulterCreatureTypeId));
        Assert.Equal(1, _store.SpeciesLevelOf(new SaveId(2), EmpireId.Zomboss, PolevaulterCreatureTypeId));
    }

    [Fact]
    public void Zomboss_and_the_human_are_read_separately_within_one_save()
    {
        SeedSpeciesRow(1, EmpireId.Dave.Value, PolevaulterCreatureTypeId, 3);
        SeedSpeciesRow(1, EmpireId.Zomboss.Value, PolevaulterCreatureTypeId, 9);

        Assert.Equal(3, _store.SpeciesLevelOf(new SaveId(1), EmpireId.Dave, PolevaulterCreatureTypeId));
        Assert.Equal(9, _store.SpeciesLevelOf(new SaveId(1), EmpireId.Zomboss, PolevaulterCreatureTypeId));
    }

    // ---- EP4.14 (R1): the two species XP paths credit the side's empire ---------------------------

    const int FumeshroomGameTypeId = 7;
    const int PolevaulterGameTypeId = 3;

    long StartRun(long saveId)
    {
        _store.SetCurrentPlayer(saveId);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            MatchKey = "m-" + Guid.NewGuid().ToString("N"),
            Payload = new { levelName = "test", levelType = "adventure" },
        });
        return _store.ListRuns(saveId).Single().Id;
    }

    void Append(long saveId, long runId, string kind, string sourceId, string payload, string dedupe) =>
        _store.AppendPvzActivityFact(saveId, new PvzActivityAppendRequest
        {
            Kind = kind, RunId = runId, SourceKind = "creature.progression.v1", SourceId = sourceId,
            PayloadJson = payload, DedupeKey = dedupe,
        });

    void PlacePlant(long saveId, long runId, string dedupe) =>
        Append(saveId, runId, PvzActivityKinds.PlantPlaced, "general:fumeshroom",
            $$"""{"type":{{FumeshroomGameTypeId}}}""", dedupe);

    void SpawnZombie(long saveId, long runId, string dedupe) =>
        Append(saveId, runId, PvzActivityKinds.ZombieSpawned, "general:polevaulterzombie",
            $$"""{"type":{{PolevaulterGameTypeId}}}""", dedupe);

    /// <summary>Placements needed to cross ONE species level from level 1 — read from the loaded species
    /// curve and its configured placement award, never a literal count.</summary>
    int PlacementsForOneSpeciesLevel()
    {
        var award = SpeciesProgressionTuningHub.Tuning.PlacementAward;
        return (int)((RpgXpCurve.XpToNext(RpgActorKinds.Species, 1) + award - 1) / award);
    }

    /// <summary>One empire's species row of this save, through the store's own wide empire read — the
    /// public `GetRpgActor` resolves only the human empire.</summary>
    RpgActorProgressionDto? EmpireSpecies(long saveId, EmpireId empire, int creatureTypeId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.ReadEmpireActorUnlocked(
            db, new EmpireRef(new SaveId(saveId), empire), RpgActorKinds.Species, creatureTypeId);
    }

    [Fact]
    public void A_zombie_spawn_and_a_zombie_run_completion_credit_Zomboss_while_the_humans_rows_stay_history()
    {
        var player = _store.CreatePlayer("EmpireSpeciesR1");
        var save = new SaveId(player.Id);
        var runId = StartRun(player.Id);

        // A pre-R1 save already has a human zombie row: R1 leaves it exactly as history, never rewrites
        // it, and never grows it again.
        SeedSpeciesRow(player.Id, EmpireId.Dave.Value, PolevaulterCreatureTypeId, 5);

        for (var i = 0; i < PlacementsForOneSpeciesLevel(); i++)
        {
            PlacePlant(player.Id, runId, $"plant-{i}");
            SpawnZombie(player.Id, runId, $"zombie-{i}");
        }
        Append(player.Id, runId, PvzActivityKinds.MatchEnded, "feature", """{"result":"victory"}""", "end-1");

        // The zombie species earned for Zomboss's empire of THIS save, through both XP paths.
        var zomboss = EmpireSpecies(player.Id, EmpireId.Zomboss, PolevaulterCreatureTypeId);
        Assert.NotNull(zomboss);
        Assert.True(zomboss!.Level >= 2,
            $"the zombie spawns + run completion should have crossed a Zomboss species level (level {zomboss.Level})");

        // The human's zombie row is the level-5 history it was seeded with: unchanged, not grown.
        var humanZombie = EmpireSpecies(player.Id, EmpireId.Dave, PolevaulterCreatureTypeId);
        Assert.NotNull(humanZombie);
        Assert.Equal(5, humanZombie!.Level);
        Assert.Equal(0, humanZombie.Xp);

        // The plant species earned for the human, and Zomboss has no plant row at all.
        var humanPlant = EmpireSpecies(player.Id, EmpireId.Dave, FumeshroomCreatureTypeId);
        Assert.NotNull(humanPlant);
        Assert.True(humanPlant!.Level >= 2);
        Assert.Equal(1, _store.SpeciesLevelOf(save, EmpireId.Zomboss, FumeshroomCreatureTypeId));
    }

    [Fact]
    public void A_replayed_completion_credits_Zomboss_species_once()
    {
        var player = _store.CreatePlayer("EmpireSpeciesReplay");
        var runId = StartRun(player.Id);
        SpawnZombie(player.Id, runId, "zombie-0");

        var matchEnd = new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded, RunId = runId,
            PayloadJson = """{"result":"victory"}""", DedupeKey = "end-1",
        };
        _store.AppendPvzActivityFact(player.Id, matchEnd);
        var afterFirst = EmpireSpecies(player.Id, EmpireId.Zomboss, PolevaulterCreatureTypeId)!;

        _store.AppendPvzActivityFact(player.Id, matchEnd); // the identical fact, replayed
        var afterReplay = EmpireSpecies(player.Id, EmpireId.Zomboss, PolevaulterCreatureTypeId)!;

        Assert.Equal(afterFirst.Level, afterReplay.Level);
        Assert.Equal(afterFirst.Xp, afterReplay.Xp);
        Assert.Equal(afterFirst.Revision, afterReplay.Revision);
    }

    [Fact]
    public void A_zombie_fact_for_an_unresolvable_run_credits_nothing()
    {
        var player = _store.CreatePlayer("EmpireSpeciesNoRun");

        var result = _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieSpawned, RunId = 999999,
            SourceKind = "creature.progression.v1", SourceId = "general:polevaulterzombie",
            PayloadJson = $$"""{"type":{{PolevaulterGameTypeId}}}""", DedupeKey = "no-run-1",
        });

        // Reported by the caller's own empty result — the same signal SE4.21 gives every unresolved run.
        Assert.Empty(result.Progression);
        Assert.Null(EmpireSpecies(player.Id, EmpireId.Zomboss, PolevaulterCreatureTypeId));
    }
}
