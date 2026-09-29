using System.Linq;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.12 — a save owns its empires as data, seeded once from the authored registry.
/// Store tests run in memory (testing-standard.md); nothing here asserts a count of empires.
/// </summary>
[Trait("VerificationId", "data.save-empires")]
public class SaveEmpiresStoreTests
{
    static RpgStore Fresh() => DataTestStore.Create().Store;

    [Fact]
    public void A_fresh_boot_has_save_1_with_the_registry_empires_and_no_zomboss_player_row()
    {
        var store = Fresh();

        var empires = store.EmpiresOf(1);
        Assert.Contains(empires, e => e.Empire == EmpireId.Dave && e.Controller == EmpireController.Human);
        Assert.Contains(empires, e => e.Empire == EmpireId.Zomboss && e.Controller == EmpireController.Ai);
        Assert.DoesNotContain(store.ListPlayers(), p => p.Name == "Zomboss");
    }

    [Fact]
    public void Every_save_has_exactly_one_human_empire()
    {
        var store = Fresh();
        var second = store.CreatePlayer("Second");

        foreach (var save in store.ListPlayers())
        {
            var empires = store.EmpiresOf(save.Id);
            Assert.Equal(1, empires.Count(e => e.Controller == EmpireController.Human));
        }
        Assert.Equal(EmpireId.Dave, store.HumanEmpireOf(second.Id));
    }

    [Fact]
    public void A_save_with_no_seeded_empires_throws_rather_than_guessing_dave()
    {
        var store = Fresh();
        // A row that is not a save in the R3 sense: no empires were seeded for it. Before SE4.22 deleted
        // EnsureZombossPlayer, that was the exact shape it built for Zomboss's own (now-gone) row.
        var unseeded = store.CreateUnseededPlayerForTest("Zomboss");

        Assert.Empty(store.EmpiresOf(unseeded.Id));
        Assert.Throws<SaveEmpiresNotSeeded>(() => store.HumanEmpireOf(unseeded.Id));
    }

    [Fact]
    public void Seeding_is_idempotent_across_a_save_switch()
    {
        var store = Fresh();
        var second = store.CreatePlayer("Second");

        Assert.True(store.SetCurrentPlayer(second.Id));
        Assert.True(store.SetCurrentPlayer(1));
        Assert.True(store.SetCurrentPlayer(second.Id));

        // One row per (save, empire) — a re-seed never duplicates and never re-attributes.
        Assert.Equal(store.EmpiresOf(second.Id).Count,
            store.EmpiresOf(second.Id).Select(e => e.Empire).Distinct().Count());
        Assert.Equal(EmpireId.Dave, store.HumanEmpireOf(second.Id));
    }

    [Fact]
    public void Is_live_save_is_true_for_a_real_save_and_false_for_a_missing_one()
    {
        var store = Fresh();
        Assert.True(store.IsLiveSave(1));
        Assert.False(store.IsLiveSave(999_999));
    }

    /// <summary>save-identity SE4.29 (fixes D2): after the migration archives a Zomboss-only legacy
    /// row, it is never listed, never selectable, and never the lowest-id fallback.</summary>
    [Fact]
    public void After_the_migration_archives_a_Zomboss_only_row_the_save_list_has_no_Zomboss()
    {
        using var testStore = DataTestStore.Create();
        var zombossId = SaveIdentityFixtures.ZombossOnly(testStore);
        var store = testStore.Store;

        Assert.True(SaveIdentityFixtures.MigrateAgain(testStore));

        Assert.DoesNotContain(store.ListPlayers(), p => p.Name == "Zomboss");
        Assert.False(store.IsLiveSave(zombossId));
        Assert.False(store.SetCurrentPlayer(zombossId));

        // The lowest-id fallback: an invalid/missing `current_player_id` never resolves to an
        // archived row, even when it is the lowest id (a real production caller reaches this only
        // by a corrupted setting, exercised directly here rather than left unreachable in a test).
        using (var db = FusionRpg.Data.Sqlite.SqliteConnectionFactory.Open(store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM settings WHERE key = 'current_player_id';";
            cmd.ExecuteNonQuery();
        }
        Assert.NotEqual(zombossId, store.GetCurrentPlayerId());
        Assert.True(store.IsLiveSave(store.GetCurrentPlayerId()));
    }
}
