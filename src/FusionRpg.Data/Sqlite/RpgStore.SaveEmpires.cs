using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>A save's empire row: which empires it has and who decides for each.</summary>
public sealed record SaveEmpire(long SaveId, EmpireId Empire, EmpireController Controller);

/// <summary>
/// Thrown when a save has no <c>human</c> empire. Never guessed: a save whose empires were not seeded
/// (a row that predates the seed, or a Zomboss legacy row) has no answer, and a guess of "dave" would
/// hand it the human empire's rows.
/// </summary>
public sealed class SaveEmpiresNotSeeded : InvalidOperationException
{
    public SaveEmpiresNotSeeded(long saveId)
        : base($"save {saveId} has no seeded empires in rpg_save_empires") { }
}

/// <summary>
/// `save-identity` SE4.12 — a save owns its empires as data, and this is the one place that data is
/// written and read. Which empires a save gets is the authored registry (<see cref="NewSaveEmpiresHub"/>);
/// the seeding is idempotent so the same save can be seeded at creation, at boot and on a save switch.
/// </summary>
public sealed partial class RpgStore
{
    void EnsureSaveEmpiresSchemaUnlocked(SqliteConnection db) => Exec(db, """
        CREATE TABLE IF NOT EXISTS rpg_save_empires (
          save_id     INTEGER NOT NULL,
          empire_id   TEXT    NOT NULL,
          controller  TEXT    NOT NULL,
          created_utc TEXT    NOT NULL,
          PRIMARY KEY (save_id, empire_id)
        );
        """);

    /// <summary>
    /// The one seeder: a new save's empires come from the registry, never a literal list. Idempotent
    /// (<c>INSERT OR IGNORE</c> on the primary key), so a boot, a save switch and a creation can each
    /// call it without a second copy of "what a new save contains".
    /// </summary>
    internal static void SeedSaveEmpiresUnlocked(SqliteConnection db, long saveId)
    {
        var registry = ResolveNewSaveEmpires();
        var now = ServerClock.UtcNowDateTime.ToString("o");
        foreach (var row in registry.Rows)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO rpg_save_empires(save_id, empire_id, controller, created_utc)
                VALUES($s, $e, $c, $t);
                """;
            cmd.Parameters.AddWithValue("$s", saveId);
            cmd.Parameters.AddWithValue("$e", row.EmpireId);
            cmd.Parameters.AddWithValue("$c", ControllerId(row.Controller));
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The save's human empire. Reads the <c>human</c> row; a save without one throws rather than
    /// guessing "dave" — the code never assumes the human empire is a particular faction (R3/R17).
    /// </summary>
    public EmpireId HumanEmpireOf(long saveId)
    {
        foreach (var empire in EmpiresOf(saveId))
        {
            if (empire.Controller == EmpireController.Human) return empire.Empire;
        }
        throw new SaveEmpiresNotSeeded(saveId);
    }

    /// <summary>Every empire of one save, in a stable order (empire id).</summary>
    public IReadOnlyList<SaveEmpire> EmpiresOf(long saveId)
    {
        using var db = Open();
        return EmpiresOfUnlocked(db, saveId);
    }

    internal static IReadOnlyList<SaveEmpire> EmpiresOfUnlocked(SqliteConnection db, long saveId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT save_id, empire_id, controller FROM rpg_save_empires WHERE save_id=$s ORDER BY empire_id;";
        cmd.Parameters.AddWithValue("$s", saveId);
        var list = new List<SaveEmpire>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SaveEmpire(r.GetInt64(0), new EmpireId(r.GetString(1)), ParseController(r.GetString(2))));
        return list;
    }

    /// <summary>A save that exists and is not archived. Archived rows are history and are never listed
    /// or selected as a save (spec-save-identity.md step 6 / D2, SE4.29). <c>SetCurrentPlayer</c> calls
    /// the unlocked form over its own already-open connection.</summary>
    public bool IsLiveSave(long id)
    {
        using var db = Open();
        return IsLiveSaveUnlocked(db, id);
    }

    internal static bool IsLiveSaveUnlocked(SqliteConnection db, long id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM players WHERE id=$p AND archived_utc IS NULL;";
        cmd.Parameters.AddWithValue("$p", id);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>
    /// The registry a store seeds from: <see cref="NewSaveEmpiresHub"/> when the host configured it, else
    /// the authored file found by walking up from the running image — the same <c>FindUp</c> lookup every
    /// other seed reader in this assembly uses. There is no hardcoded fallback: a run that cannot find the
    /// file throws naming the path, because a save seeded empty would silently resolve no empire at all.
    ///
    /// <para><b>Why the store resolves it too.</b> `Init` seeds at boot, and `Init` is reached by every
    /// host — the server, the tools, and every test factory that builds its own store. Requiring each of
    /// them to remember a `Configure` call is how the E2E host broke once already; resolving here makes an
    /// unconfigured host impossible rather than a habit.</para>
    /// </summary>
    static NewSaveEmpires ResolveNewSaveEmpires()
    {
        if (NewSaveEmpiresHub.IsConfigured) return NewSaveEmpiresHub.Current;

        const string fileName = "new-save-empires.v1.json";
        var registryDir = Seed.SeedImportRunner.FindUp(
            AppContext.BaseDirectory, "data", "seed", "saves", "_registry");
        var path = registryDir is null ? null : Path.Combine(registryDir, fileName);
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException(
                $"{fileName} was not found above '{AppContext.BaseDirectory}'. " +
                "Call NewSaveEmpiresHub.Configure(...) or run from the repository.");

        return NewSaveEmpires.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// The save's human empire, or <c>null</c> when it has none (an unseeded row). The one non-throwing
    /// reader, for writers that must leave a column NULL rather than guess — never a general reader:
    /// <see cref="HumanEmpireOf"/> is the loud one.
    /// </summary>
    internal static EmpireId? HumanEmpireOfOrNull(SqliteConnection db, long saveId)
    {
        foreach (var empire in EmpiresOfUnlocked(db, saveId))
        {
            if (empire.Controller == EmpireController.Human) return empire.Empire;
        }
        return null;
    }

    /// <summary>
    /// The one specimen-ownership predicate (spec-save-identity.md "One ownership predicate"): compares
    /// <c>(player_id, empire_id)</c> <b>strictly</b>. A NULL <c>empire_id</c> never matches (a row whose
    /// owner is not a save owns nothing), and another empire of the same save is not the owner — which
    /// is the defect the migration would otherwise hand the human player (SE4.14's D6-adjacent risk).
    /// No production caller yet: SE4.24/SE4.25 wire it after SE4.20.
    /// </summary>
    internal static bool OwnsSpecimenUnlocked(SqliteConnection db, EmpireRef owner, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT player_id, empire_id FROM rpg_unique_actors WHERE instance_id=$i;";
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
        if (r.IsDBNull(1)) return false;
        return r.GetInt64(0) == owner.Save.Value
            && string.Equals(r.GetString(1), owner.Empire.Value, StringComparison.Ordinal);
    }

    /// <summary>Test-only seam (InternalsVisibleTo): the ownership predicate over this store's own
    /// connection, so a test never has to hold a raw connection. SE4.24/SE4.25 call the unlocked form
    /// inside their own transaction.</summary>
    internal bool OwnsSpecimenForTest(EmpireRef owner, string instanceId)
    {
        using var db = Open();
        return OwnsSpecimenUnlocked(db, owner, instanceId);
    }

    /// <summary>The public form of the one ownership predicate, for a Server-layer production caller
    /// (`ItemEquipEndpoints`, save-identity SE4.25) that holds no connection of its own — the same read
    /// <see cref="OwnsSpecimenForTest"/> gives Data.Tests.</summary>
    public bool OwnsSpecimen(EmpireRef owner, string instanceId)
    {
        using var db = Open();
        return OwnsSpecimenUnlocked(db, owner, instanceId);
    }

    /// <summary>
    /// save-identity SE4.28 ("Injector ownership") — a specimen's own empire AND that empire's
    /// controller, for `UniqueActorService.DeployAsync` to stamp onto the `pvz.spawn.extra` payload
    /// (`empireId`/`controller`) at the one call that decides the deploy, so the Injector reads
    /// ownership rather than inferring it by elimination. Null when the specimen has no `empire_id` yet
    /// (pre-R3 legacy) or names an empire `rpg_save_empires` does not carry for its save — never a
    /// guess: the payload simply omits the fields, and the Injector's own fallback (mechanical side)
    /// decides, exactly like an unregistered ptr already does.
    /// </summary>
    public (EmpireId Empire, EmpireController Controller)? SpecimenOwnerEmpire(string instanceId)
    {
        using var db = Open();
        return SpecimenOwnerEmpireUnlocked(db, instanceId);
    }

    /// <summary>The unlocked form of <see cref="SpecimenOwnerEmpire"/>, for a caller that already
    /// holds a connection inside its own transaction (`species-progression` SP1.2's world-turn
    /// provider, and any future caller resolving a specimen's owner mid-transaction).</summary>
    internal (EmpireId Empire, EmpireController Controller)? SpecimenOwnerEmpireUnlocked(
        SqliteConnection db, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        long saveId;
        EmpireId empireId;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT player_id, empire_id FROM rpg_unique_actors WHERE instance_id=$i;";
            cmd.Parameters.AddWithValue("$i", instanceId.Trim());
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(1)) return null;
            saveId = r.GetInt64(0);
            empireId = new EmpireId(r.GetString(1));
        }
        foreach (var empire in EmpiresOfUnlocked(db, saveId))
            if (empire.Empire == empireId) return (empireId, empire.Controller);
        return null;
    }

    /// <summary>
    /// save-identity SE4.23 ("AI-empire specimens never touch a human-only table") — whether
    /// <paramref name="instanceId"/>'s OWN stored <c>empire_id</c> is <paramref name="playerId"/>'s save's
    /// human empire. Scopes the contract gate (<c>TryBeginUniqueDeploy</c>) and spawn-activity recording
    /// (<c>UniqueActorService.DeployAsync</c>) to the human empire only. A row with no <c>empire_id</c>
    /// yet (pre-R3 legacy, or a save with no seeded empires) reads as human — the pre-existing behaviour
    /// every such row already had, never newly refused by this predicate.
    /// </summary>
    internal static bool IsHumanEmpireSpecimenUnlocked(SqliteConnection db, long playerId, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT empire_id FROM rpg_unique_actors WHERE instance_id=$i;";
        cmd.Parameters.AddWithValue("$i", instanceId);
        var v = cmd.ExecuteScalar();
        if (v is null || v is DBNull) return true;
        var human = HumanEmpireOfOrNull(db, playerId);
        return human.HasValue && string.Equals((string)v, human.Value.Value, StringComparison.Ordinal);
    }

    /// <summary>The locked form of <see cref="IsHumanEmpireSpecimenUnlocked"/>, for a Server-layer caller
    /// (<c>UniqueActorService.DeployAsync</c>) that holds no connection of its own.</summary>
    public bool IsHumanEmpireSpecimen(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return true;
        using var db = Open();
        var actor = ReadUniqueActorUnlocked(db, instanceId.Trim());
        if (actor is null) return true;
        return IsHumanEmpireSpecimenUnlocked(db, actor.PlayerId, instanceId.Trim());
    }

    /// <summary>
    /// Test-only seam (InternalsVisibleTo): fabricates a player row with NO seeded empires — the
    /// physical shape a pre-R3 install could have left (<see cref="CreatePlayer"/> always seeds; this
    /// bypasses that). Before SE4.22, <c>EnsureZombossPlayer</c> was production code that built exactly
    /// this shape for Zomboss's own row; now that no player row represents Zomboss, only the migration's
    /// own test fixtures (a legacy "Zomboss" row `save-identity`'s classification must detect) still need
    /// it.
    /// </summary>
    internal PlayerDto CreateUnseededPlayerForTest(string name)
    {
        using var db = Open();
        long id;
        using (var tx = db.BeginTransaction())
        {
            id = InsertPlayerUnlocked(db, name);
            tx.Commit();
        }
        EnsurePvzStatsRevisionUnlocked(db, id);
        EnsurePvzActivityRevisionUnlocked(db, id);
        EnsureOnboardingStoryRowUnlocked(db, id);
        return GetPlayerUnlocked(db, id)!;
    }

    static string ControllerId(EmpireController controller) => EmpireControllerTokens.Of(controller);

    static EmpireController ParseController(string value) => value switch
    {
        EmpireControllerTokens.Human => EmpireController.Human,
        EmpireControllerTokens.Ai => EmpireController.Ai,
        _ => throw new InvalidOperationException($"rpg_save_empires carries an unknown controller '{value}'"),
    };
}

/// <summary>
/// The storage/wire spelling of <see cref="EmpireController"/> — the two tokens
/// <c>data/seed/saves/_registry/new-save-empires.v*.json</c> carries and
/// <c>rpg_save_empires.controller</c> stores, declared once next to their parser so a DTO, the spawn
/// payload and the store cannot drift apart. <see cref="NewSaveEmpires.Controller"/> parses these back.
/// </summary>
public static class EmpireControllerTokens
{
    public const string Human = "human";
    public const string Ai = "ai";

    public static string Of(EmpireController controller) => controller switch
    {
        EmpireController.Human => Human,
        EmpireController.Ai => Ai,
        _ => throw new ArgumentOutOfRangeException(nameof(controller), controller, "unknown empire controller"),
    };
}
