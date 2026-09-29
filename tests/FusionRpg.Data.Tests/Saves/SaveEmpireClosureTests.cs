using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.41 — the closure contracts (spec-save-identity.md §Testing strategy /
/// "Contracts"): every Tier A row is owned by an empire its save actually seeded, and every
/// `rpg_world_factions.faction_id` that names an empire is an empire of its world's save. These are
/// `(save_id, empire_id)` JOIN closures, asserted — never a row count of anything.
///
/// <para>Each check is a violation LIST, and three tests plant the violation the check exists to catch
/// (a null owner, an owner no save seeded, a world of an unseeded save). A closure check that cannot
/// fail is not a check: the planted cases are what prove these read the right thing, and the
/// non-vacuity assertions are what prove the green ones are not passing over an empty database.</para>
/// </summary>
[Trait("VerificationId", "data.save-empires")]
public class SaveEmpireClosureTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SaveEmpireClosureTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static readonly CreatureSpeciesDef CatalogSpecies =
        CreatureSpeciesCatalog.All.First(s => s.DeployMode != CreatureDeployMode.HypnoAlly);

    static EmpireRef HumanOwner(long save) => new(new SaveId(save), EmpireId.Dave);

    static EmpireRef ZombossOwner(long save) => new(new SaveId(save), EmpireId.Zomboss);

    [Fact]
    public void Tier_A_rows_of_a_real_state_all_resolve_to_a_seeded_empire()
    {
        var save = _store.GetCurrentPlayerId();
        _store.SeedRpgProgressionDemo(save);   // the one XP write path (TryApplyXpUnlocked), per empire rows
        _store.MintForEmpire(HumanOwner(save), CatalogSpecies.SpeciesId, seed: 40);
        _store.MintForEmpire(ZombossOwner(save), CatalogSpecies.SpeciesId, seed: 41);

        Assert.Empty(TierAViolations());

        // Non-vacuity: each classified table the closure reads actually carries rows.
        Assert.True(Scalar("SELECT COUNT(*) FROM rpg_unique_actors;") > 0);
        Assert.True(Scalar("SELECT COUNT(*) FROM rpg_actor_progression;") > 0);
        Assert.True(Scalar("SELECT COUNT(*) FROM rpg_xp_ledger;") > 0);
    }

    [Fact]
    public void A_specimen_with_no_owner_is_reported()
    {
        var save = _store.GetCurrentPlayerId();
        var id = _store.MintForEmpire(HumanOwner(save), CatalogSpecies.SpeciesId, seed: 42).Actor.InstanceId;

        Exec("UPDATE rpg_unique_actors SET empire_id = NULL WHERE instance_id = $i;", ("$i", id));

        Assert.Contains($"specimen:{id}", TierAViolations());
    }

    [Fact]
    public void A_specimen_owned_by_an_empire_no_save_seeded_is_reported()
    {
        var save = _store.GetCurrentPlayerId();
        var id = _store.MintForEmpire(HumanOwner(save), CatalogSpecies.SpeciesId, seed: 43).Actor.InstanceId;

        Exec("UPDATE rpg_unique_actors SET empire_id = 'empire-no-save-seeded' WHERE instance_id = $i;",
            ("$i", id));

        Assert.Contains($"specimen:{id}", TierAViolations());
    }

    [Fact]
    public void A_progression_row_owned_by_an_empire_no_save_seeded_is_reported()
    {
        var save = _store.GetCurrentPlayerId();
        _store.SeedRpgProgressionDemo(save);

        Exec("UPDATE rpg_actor_progression SET empire_id = 'empire-no-save-seeded' WHERE save_id = $s;",
            ("$s", save));
        Exec("UPDATE rpg_xp_ledger SET empire_id = 'empire-no-save-seeded' WHERE save_id = $s;", ("$s", save));

        var violations = TierAViolations();
        Assert.Contains(violations, v => v.StartsWith("progression:", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith("ledger:", StringComparison.Ordinal));
    }

    /// <summary>The world half: the authored template's factions ARE this save's empire ids (`dave`,
    /// `zomboss`; `wild` is not an empire id and is not this check's subject), so a seeded save's world
    /// is a non-vacuous pass, and a world belonging to a save with no seeded empires is the planted
    /// violation.</summary>
    [Fact]
    public void A_world_of_a_seeded_save_names_only_its_own_empires()
    {
        var save = _store.GetCurrentPlayerId();
        Assert.True(_store.CreateWorld(
            save, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 7, worldId: "w-closure")).Ok);

        Assert.Empty(WorldViolations());
        // Non-vacuity: some faction does name an empire, so the empty list above is a real answer.
        Assert.True(Scalar("SELECT COUNT(*) FROM rpg_world_factions WHERE faction_id IN (SELECT empire_id FROM rpg_save_empires);") > 0);
    }

    [Fact]
    public void A_world_of_an_unseeded_save_naming_an_empire_is_reported()
    {
        // The migration's own historical shape: a legacy rows-only save (`players` row, no empires —
        // `CreateUnseededPlayerForTest` is the store's production path for it). Its world may name an
        // empire id that exists in the registry, but no row of `rpg_save_empires` binds it to THIS save,
        // which is exactly the closure this contract asserts.
        var unseeded = _store.CreateUnseededPlayerForTest("Legacy");
        Assert.Empty(_store.EmpiresOf(unseeded.Id));

        Assert.True(_store.CreateWorld(
            unseeded.Id, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 8, worldId: "w-legacy")).Ok);

        Assert.NotEmpty(WorldViolations());
    }

    /// <summary>Every Tier A owner that is not a `(save_id, empire_id)` row of `rpg_save_empires`,
    /// one line per offending row. Never a count: the planted-violation tests read these entries.</summary>
    List<string> TierAViolations()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 'specimen:' || a.instance_id
            FROM rpg_unique_actors a
            WHERE a.empire_id IS NULL
               OR NOT EXISTS (SELECT 1 FROM rpg_save_empires e
                              WHERE e.save_id = a.player_id AND e.empire_id = a.empire_id)
            UNION ALL
            SELECT 'progression:' || p.save_id || ':' || p.empire_id || ':' || p.kind || ':' || p.type_id
            FROM rpg_actor_progression p
            WHERE NOT EXISTS (SELECT 1 FROM rpg_save_empires e
                              WHERE e.save_id = p.save_id AND e.empire_id = p.empire_id)
            UNION ALL
            SELECT 'ledger:' || CAST(l.id AS TEXT)
            FROM rpg_xp_ledger l
            WHERE NOT EXISTS (SELECT 1 FROM rpg_save_empires e
                              WHERE e.save_id = l.save_id AND e.empire_id = l.empire_id);
            """;
        var violations = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) violations.Add(r.GetString(0));
        return violations;
    }

    /// <summary>Every faction of a world whose id names an empire of SOME save but not of the world's
    /// own save. A faction id that names no empire (`wild`) is not this check's subject.</summary>
    List<string> WorldViolations()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT wf.world_id || ':' || wf.faction_id
            FROM rpg_world_factions wf
            JOIN rpg_worlds w ON w.world_id = wf.world_id
            WHERE wf.faction_id IN (SELECT DISTINCT empire_id FROM rpg_save_empires)
              AND NOT EXISTS (SELECT 1 FROM rpg_save_empires e
                              WHERE e.save_id = w.player_id AND e.empire_id = wf.faction_id)
            ORDER BY wf.world_id, wf.faction_id;
            """;
        var violations = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) violations.Add(r.GetString(0));
        return violations;
    }

    long Scalar(string sql)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
