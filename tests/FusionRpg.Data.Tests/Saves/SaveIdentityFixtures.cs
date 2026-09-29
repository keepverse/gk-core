using System;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Sqlite.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.16 — fixtures for step 2's classification, built **once** from real production
/// calls (the spec's "captured once as a fixture script"), so the input is a state real play could have
/// produced. Every helper takes an already-`Init`ed <see cref="DataTestStore"/>.
/// </summary>
static class SaveIdentityFixtures
{
    /// <summary>A real species the Zomboss deploy can mint (the catalog's own first non-hypno def).</summary>
    public static readonly CreatureSpeciesDef ZombieSpecies = CreatureSpeciesCatalog.All.First(s =>
        s.DeployMode != CreatureDeployMode.HypnoAlly);

    /// <summary>Opens a connection straight at the store's hot database (the migration's own input).</summary>
    public static SqliteConnection Open(DataTestStore test) =>
        SqliteConnectionFactory.Open(test.Store.HotPath);

    /// <summary>What step 2 decides about this store's one legacy `Zomboss` row.</summary>
    public static SaveIdentity.LegacyZombossVerdict Classify(DataTestStore test)
    {
        using var db = Open(test);
        return SaveIdentity.FindLegacyZombossRow(db);
    }

    /// <summary>
    /// Re-runs the migration after `Init` has already run it once (SE4.20): clears the marker so the
    /// gate is open, then migrates. On a store `Init` built, the Tier A tables are already widened, so
    /// step 4 does not rename; the other steps re-run over whatever the test built.
    /// </summary>
    public static bool MigrateAgain(
        DataTestStore test,
        Action<string>? backupFailure = null,
        int? failAfterStep = null,
        Action<SqliteConnection>? failureInjector = null)
    {
        using var db = Open(test);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM settings WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", SaveIdentity.MarkerKey);
            cmd.ExecuteNonQuery();
        }
        return SaveIdentity.MigrateForTest(db, test.DataDir, log: null,
            backupFailure, failAfterStep, failureInjector);
    }

    /// <summary>Origin/mapping mirror of `MintForEmpire` (SE4.22 deleted `MintForZomboss`), for a fixture
    /// that must mint under a raw player id rather than a real `EmpireRef` — either the pre-R3 unseeded
    /// shape itself, or a real save whose specimen carries `origin: "zomboss"` regardless (the SE4.16
    /// name-collision scenario: origin is provenance, not ownership).</summary>
    public static CreatureSpecimenDto MintZombossOriginSpecimen(DataTestStore test, long playerId)
    {
        var s = ZombieSpecies;
        var (specimen, _) = test.Store.MintCreature(playerId, new CreatureMintSpec
        {
            SpeciesId = s.SpeciesId,
            Side = s.Side,
            GameTypeId = s.GameTypeId,
            Rarity = s.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = s.ElementPrimary.ToElementId(),
            ElementSecondary = s.ElementSecondary?.ToElementId(),
            Origin = "zomboss",
        });
        return specimen;
    }

    /// <summary>
    /// The pure Zomboss-only shape: a normal save, then the pre-R3 Zomboss player row and one Zomboss
    /// mint — the row's own writes and nothing else. Returns the Zomboss row's id.
    /// </summary>
    public static long ZombossOnly(DataTestStore test)
    {
        test.Store.CreatePlayer("Hero");
        var zomboss = test.Store.CreateUnseededPlayerForTest("Zomboss");
        MintZombossOriginSpecimen(test, zomboss.Id);
        return zomboss.Id;
    }

    /// <summary>A normal summon under <paramref name="playerId"/> — the same mint mapping, `origin` the
    /// only difference from a Zomboss mint.</summary>
    public static void Summon(DataTestStore test, long playerId)
    {
        var s = ZombieSpecies;
        test.Store.MintCreature(playerId, new CreatureMintSpec
        {
            SpeciesId = s.SpeciesId,
            Side = s.Side,
            GameTypeId = s.GameTypeId,
            Rarity = s.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = s.ElementPrimary.ToElementId(),
            ElementSecondary = s.ElementSecondary?.ToElementId(),
            Origin = "summon",
        });
    }

    /// <summary>The pre-R3 Zomboss row plus one Zomboss-minted specimen, with no second save created.</summary>
    public static (long ZombossId, string SpecimenId) ZombossWithSpecimen(DataTestStore test)
    {
        var zomboss = test.Store.CreateUnseededPlayerForTest("Zomboss");
        var specimen = MintZombossOriginSpecimen(test, zomboss.Id);
        return (zomboss.Id, specimen.Actor.InstanceId);
    }

    /// <summary>Starts a real run owned by <paramref name="saveId"/> and returns its match key.</summary>
    public static string StartRunFor(DataTestStore test, long saveId, string matchKey)
    {
        Assert.True(test.Store.SetCurrentPlayer(saveId));
        test.Store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            MatchKey = matchKey,
            Payload = new { levelName = "test", levelType = "adventure" },
        });
        return matchKey;
    }

    /// <summary>Deploys an existing specimen into <paramref name="matchKey"/> and returns its board ptr.</summary>
    public static string Deploy(DataTestStore test, string instanceId, string matchKey)
    {
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(test.Store.TryBeginUniqueDeploy(instanceId, corr, matchKey).Ok);
        var ptr = "ptr-" + Guid.NewGuid().ToString("N");
        Assert.True(test.Store.TryAckUniqueSpawn(corr, ptr, matchKey).Ok);
        return ptr;
    }

    /// <summary>
    /// The state `board.end`'s recovery leaves, except the receipt survives: the specimen is back on the
    /// roster with no match key, its lawn session is gone, and a lawn-XP receipt still names the match.
    /// The receipt is written here directly because the award is tuning-gated on `specimenLawnKill`,
    /// which this assembly's test bootstrap leaves at 0 — the migration's input is the row, not how it
    /// was produced.
    /// </summary>
    public static void RecoverToRosterKeepingReceipt(DataTestStore test, string instanceId, string matchKey)
    {
        using var db = Open(test);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE rpg_unique_actors SET match_key = NULL, phase = 'Roster' WHERE instance_id = $i;";
            cmd.Parameters.AddWithValue("$i", instanceId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM rpg_unique_lawn_sessions WHERE instance_id = $i;";
            cmd.Parameters.AddWithValue("$i", instanceId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO rpg_unique_lawn_xp_receipts
                  (instance_id, match_key, occurrence_id, reason, xp, created_utc)
                VALUES($i, $m, 'occ-1', 'specimen_lawn_kill', 18, $t);
                """;
            cmd.Parameters.AddWithValue("$i", instanceId);
            cmd.Parameters.AddWithValue("$m", matchKey);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A pre-R3 store: old-shaped Tier A tables with rows, a human save (id 1), a hand-seeded Zomboss row
    /// (id 2) with no evidence, and a row owned by no player. `Init` then migrates it.
    /// </summary>
    public static DataTestStore LegacyShape()
    {
        return DataTestStore.CreateWithPreInitHot(seed =>
        {
            Exec(seed, "CREATE TABLE players (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_utc TEXT NOT NULL, world_seed INTEGER NOT NULL DEFAULT 0);");
            Exec(seed, "CREATE TABLE settings (key TEXT PRIMARY KEY, json TEXT NOT NULL, updated_utc TEXT NOT NULL);");
            Exec(seed, "CREATE TABLE rpg_actor_progression (player_id INTEGER NOT NULL, kind TEXT NOT NULL, type_id INTEGER NOT NULL, level INTEGER NOT NULL DEFAULT 1, xp INTEGER NOT NULL DEFAULT 0, highest_level INTEGER NOT NULL DEFAULT 1, demotion_count INTEGER NOT NULL DEFAULT 0, revision INTEGER NOT NULL DEFAULT 0, updated_utc TEXT NOT NULL, through_ledger_id INTEGER NOT NULL DEFAULT 0, xp_by_reason_json TEXT, scope_key TEXT, PRIMARY KEY (player_id, kind, type_id));");
            Exec(seed, "CREATE TABLE rpg_xp_ledger (id INTEGER PRIMARY KEY AUTOINCREMENT, player_id INTEGER NOT NULL, kind TEXT NOT NULL, type_id INTEGER NOT NULL, run_id INTEGER NOT NULL DEFAULT 0, t TEXT NOT NULL, delta INTEGER NOT NULL, reason TEXT NOT NULL, activity_fact_id INTEGER, level_before INTEGER NOT NULL, xp_before INTEGER NOT NULL, level_after INTEGER NOT NULL, xp_after INTEGER NOT NULL, demotion_before INTEGER NOT NULL, demotion_after INTEGER NOT NULL, payload_json TEXT, dedupe_key TEXT NOT NULL, UNIQUE (player_id, kind, type_id, reason, dedupe_key));");
            Exec(seed, "INSERT INTO players(id,name,created_utc,world_seed) VALUES(1,'Human','t',1),(2,'Zomboss','t',2);");
            Exec(seed, "INSERT INTO settings(key,json,updated_utc) VALUES('current_player_id','1','t');");
            Exec(seed, "INSERT INTO rpg_actor_progression(player_id,kind,type_id,level,xp,updated_utc) VALUES(1,'player',0,3,50,'t'),(1,'plant',0,2,10,'t'),(999,'player',0,5,90,'t');");
            Exec(seed, "INSERT INTO rpg_xp_ledger(player_id,kind,type_id,t,delta,reason,level_before,xp_before,level_after,xp_after,demotion_before,demotion_after,dedupe_key) VALUES(1,'player',0,'t',50,'kill',1,0,3,50,0,0,'k1'),(999,'player',0,'t',90,'kill',1,0,5,90,0,0,'k3');");
        });
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
