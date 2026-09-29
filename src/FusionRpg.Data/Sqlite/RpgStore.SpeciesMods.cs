using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One append-only row of an empire's species-mod ledger (layer 1b).</summary>
public sealed record SpeciesModRow(
    long ModId,
    long SaveId,
    EmpireId Empire,
    string SpeciesId,
    SpeciesModMechanism Mechanism,
    string CorrelationId,
    string InstanceId,
    long CatalogRevision,
    string CreatedUtc);

/// <summary>
/// Thrown when a ledger append names an <c>(save_id, empire_id)</c> that <c>rpg_save_empires</c> does not
/// hold. The closure is save-identity's contract: a mod belongs to an empire that exists.
/// </summary>
public sealed class SpeciesModEmpireNotInSave : InvalidOperationException
{
    public SpeciesModEmpireNotInSave(EmpireRef owner)
        : base($"empire '{owner.Empire.Value}' is not an empire of save {owner.Save.Value}") { }
}

/// <summary>
/// `species-progression` SP0.1 — the layer-1b ledger, **born keyed `(save_id, empire_id)`** so
/// `save-identity`'s migration owes it nothing (spec-species-mod-ledger.md, G3). Append-only, one row per
/// caused fact, idempotent on `(mechanism, correlation_id)`.
/// </summary>
public sealed partial class RpgStore
{
    void EnsureSpeciesModSchemaUnlocked(SqliteConnection db) => Exec(db, """
        CREATE TABLE IF NOT EXISTS rpg_player_species_mod (
          mod_id           INTEGER PRIMARY KEY AUTOINCREMENT,
          save_id          INTEGER NOT NULL,
          empire_id        TEXT    NOT NULL,
          species_id       TEXT    NOT NULL,
          mechanism        TEXT    NOT NULL,
          correlation_id   TEXT    NOT NULL,
          instance_id      TEXT    NOT NULL,
          catalog_revision INTEGER NOT NULL,
          created_utc      TEXT    NOT NULL,
          UNIQUE (mechanism, correlation_id)
        );
        CREATE INDEX IF NOT EXISTS ix_player_species_mod_owner
          ON rpg_player_species_mod(save_id, empire_id, species_id);
        """);

    /// <summary>
    /// Appends one ledger row. Returns false when the same <c>(mechanism, correlation_id)</c> is already
    /// there — a replayed fusion writes once. Throws <see cref="SpeciesModEmpireNotInSave"/> when the owner
    /// is not an empire of the save, and the caller's transaction rolls back.
    /// </summary>
    internal bool AppendSpeciesModUnlocked(
        SqliteConnection db, EmpireRef owner, string speciesId, SpeciesModMechanism mechanism,
        string correlationId, string instanceId, long catalogRevision)
    {
        if (string.IsNullOrWhiteSpace(speciesId))
            throw new ArgumentException("speciesId must not be empty", nameof(speciesId));
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("correlationId must not be empty", nameof(correlationId));
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("instanceId must not be empty", nameof(instanceId));

        using (var check = db.CreateCommand())
        {
            check.CommandText =
                "SELECT COUNT(*) FROM rpg_save_empires WHERE save_id=$s AND empire_id=$e;";
            check.Parameters.AddWithValue("$s", owner.Save.Value);
            check.Parameters.AddWithValue("$e", owner.Empire.Value);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                throw new SpeciesModEmpireNotInSave(owner);
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO rpg_player_species_mod(
              save_id, empire_id, species_id, mechanism, correlation_id, instance_id,
              catalog_revision, created_utc)
            VALUES($s, $e, $sp, $m, $c, $i, $rev, $t);
            """;
        cmd.Parameters.AddWithValue("$s", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        cmd.Parameters.AddWithValue("$sp", speciesId.Trim());
        cmd.Parameters.AddWithValue("$m", mechanism.Token());
        cmd.Parameters.AddWithValue("$c", correlationId.Trim());
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        cmd.Parameters.AddWithValue("$rev", catalogRevision);
        cmd.Parameters.AddWithValue("$t", ServerClock.UtcNowDateTime.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Appends one ledger row in its own transaction — the public seam. The fusion path calls the unlocked
    /// form inside its own transaction (SP0.3), so the ledger row and the instance commit together.
    /// </summary>
    public bool AppendSpeciesMod(
        EmpireRef owner, string speciesId, SpeciesModMechanism mechanism,
        string correlationId, string instanceId, long catalogRevision)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var written = AppendSpeciesModUnlocked(
                db, owner, speciesId, mechanism, correlationId, instanceId, catalogRevision);
            tx.Commit();
            return written;
        }
    }

    /// <summary>
    /// The atoms of one species' pick source for the paying empire: the empire's ledger instance if it has
    /// one, else the delayed <see cref="SpeciesRollPreview"/>. <b>The one function</b> the fusion preview
    /// endpoint and the fusion transaction both call, so the atoms offered are exactly the atoms accepted
    /// (spec-species-mod-ledger.md behaviour 3). Empty when neither exists.
    /// </summary>
    public IReadOnlyList<InstanceAtomRow> PickSourceAtoms(long saveId, string speciesId)
    {
        if (string.IsNullOrWhiteSpace(speciesId)) return Array.Empty<InstanceAtomRow>();
        var owner = new EmpireRef(new SaveId(saveId), HumanEmpireOf(saveId));
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return PickSourceAtomsUnlocked(db, owner, speciesId);
        }
    }

    internal IReadOnlyList<InstanceAtomRow> PickSourceAtomsUnlocked(
        SqliteConnection db, EmpireRef owner, string speciesId)
    {
        SpeciesModRow? ledger = null;
        foreach (var row in ListSpeciesModsUnlocked(db, owner))
        {
            if (string.Equals(row.SpeciesId, speciesId, StringComparison.Ordinal)) ledger = row;
        }

        if (ledger is not null)
            return GetInstance(ledger.InstanceId)?.Atoms ?? Array.Empty<InstanceAtomRow>();

        var preview = FusionRpg.Core.Creatures.Materialise.SpeciesRollPreview.For(
            speciesId, GetContainer, GetAtom, GetAffix, DomainMembers,
            WorldSeedOfUnlocked(db, owner.Save.Value), GetCatalogRevision(),
            FusionRpg.Core.Power.PowerTuningHub.Tuning.Curve.PinIndex,
            FusionRpg.Core.Power.PowerTuningHub.Tuning);
        return preview.IsOk ? preview.Instance!.Atoms : Array.Empty<InstanceAtomRow>();
    }

    /// <summary>Test-only seam (InternalsVisibleTo): how many `effect_instance` rows carry one origin.
    /// SP0.4's regression uses it to prove a save that never fused has no species-origin roll at all.</summary>
    internal int CountEffectInstancesWithOriginForTest(string origin)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM effect_instance WHERE origin=$o;";
            cmd.Parameters.AddWithValue("$o", origin);
            return (int)Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    static long WorldSeedOfUnlocked(SqliteConnection db, long saveId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT world_seed FROM players WHERE id=$p;";
        cmd.Parameters.AddWithValue("$p", saveId);
        var v = cmd.ExecuteScalar();
        if (v is long l) return l;
        return v is null || v is DBNull ? 0L : Convert.ToInt64(v);
    }

    /// <summary>
    /// species-progression step 6.2, Transport (SP6.3) — the 1a core + 1b rows one save's
    /// `speciesLayers` field carries. <c>Mod</c> is empire-scoped and keyed by that empire's real
    /// <see cref="EmpireId.Value"/> (never a literal <c>{dave, zomboss}</c> list) — an empire with NO
    /// ledger rows is simply absent from the dictionary, not present with an empty inner map. <c>Base</c>
    /// (1a) is delivered for exactly the species that have at least one 1b row in ANY empire of this
    /// save: 1a's whole purpose is to accompany 1b/2b for a species an actor's selector actually names
    /// (`layer-source-selector`'s own table — "1a core of speciesId + 1b of (S,E,speciesId)"), so a
    /// species neither empire has touched has nothing to accompany yet and is not shipped. 2b (`empire`
    /// in the wire shape) stays the caller's own empty object until SP6.10 (step 6.3's own cutover) —
    /// this method answers only 1a/1b, matching this task's own scope.
    /// </summary>
    public (IReadOnlyDictionary<string, IReadOnlyList<Core.Creatures.Layers.ProjectedLayerRow>> Base,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<Core.Creatures.Layers.ProjectedLayerRow>>> Mod)
        SpeciesLayerTransport(long saveId)
    {
        var baseSpecies = new SortedSet<string>(StringComparer.Ordinal);
        var mod = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<Core.Creatures.Layers.ProjectedLayerRow>>>(StringComparer.Ordinal);

        foreach (var empire in EmpiresOf(saveId))
        {
            var owner = new EmpireRef(new SaveId(saveId), empire.Empire);
            var ledgerRows = ListSpeciesMods(owner);
            if (ledgerRows.Count == 0) continue; // absent, not present-with-nothing (acceptance line 2)

            var perSpecies = new Dictionary<string, IReadOnlyList<Core.Creatures.Layers.ProjectedLayerRow>>(StringComparer.Ordinal);
            foreach (var ledger in ledgerRows)
            {
                var instance = GetInstance(ledger.InstanceId);
                if (instance is null) continue; // an orphaned reference is skipped, never a crash
                var template = GetContainer("species-passive." + ledger.SpeciesId);
                if (template is null) continue; // no 1a template -- ProjectPlayerMod needs one for its own seq de-dup

                var rows = Core.Creatures.Layers.SpeciesLayerProjector.ProjectPlayerMod(
                    ledger.SpeciesId, ledger.Mechanism.Token(), template, instance, GetAtom);
                if (rows.Count == 0) continue;
                perSpecies[ledger.SpeciesId] = rows;
                baseSpecies.Add(ledger.SpeciesId);
            }
            if (perSpecies.Count > 0) mod[empire.Empire.Value] = perSpecies;
        }

        var baseRows = new Dictionary<string, IReadOnlyList<Core.Creatures.Layers.ProjectedLayerRow>>(StringComparer.Ordinal);
        foreach (var speciesId in baseSpecies)
        {
            var template = GetContainer("species-passive." + speciesId); // already proven present above
            if (template is null) continue;
            var rows = Core.Creatures.Layers.SpeciesLayerProjector.ProjectBase(speciesId, template, GetAtom);
            if (rows.Count > 0) baseRows[speciesId] = rows;
        }

        return (baseRows, mod);
    }

    /// <summary>
    /// species-progression `species-layer-delivery` step 6.2 (SP6.7) — the 1a + 1b rows for exactly
    /// ONE `(owner, speciesId)` pair, for a caller (world-turn, web-squad) that already knows a
    /// specimen's own species and OWNER empire (`layer-source-selector`'s own answer) and does not
    /// need the whole save's transport. Reuses the SAME projection calls
    /// <see cref="SpeciesLayerTransport"/> uses; no second implementation.
    /// </summary>
    public IReadOnlyList<Core.Creatures.Layers.ProjectedLayerRow> SpeciesLayersForSpecimen(EmpireRef owner, string speciesId)
    {
        if (string.IsNullOrWhiteSpace(speciesId)) return Array.Empty<Core.Creatures.Layers.ProjectedLayerRow>();
        var template = GetContainer("species-passive." + speciesId);
        if (template is null) return Array.Empty<Core.Creatures.Layers.ProjectedLayerRow>(); // no 1a template at all

        var rows = new List<Core.Creatures.Layers.ProjectedLayerRow>(
            Core.Creatures.Layers.SpeciesLayerProjector.ProjectBase(speciesId, template, GetAtom));

        SpeciesModRow? ledger = null;
        foreach (var row in ListSpeciesMods(owner))
        {
            if (string.Equals(row.SpeciesId, speciesId, StringComparison.Ordinal)) ledger = row;
        }
        if (ledger is not null)
        {
            var instance = GetInstance(ledger.InstanceId);
            if (instance is not null)
            {
                rows.AddRange(Core.Creatures.Layers.SpeciesLayerProjector.ProjectPlayerMod(
                    speciesId, ledger.Mechanism.Token(), template, instance, GetAtom));
            }
        }
        return rows;
    }

    /// <summary>One empire's ledger rows, oldest first. Never another save's, never another empire's.</summary>
    public IReadOnlyList<SpeciesModRow> ListSpeciesMods(EmpireRef owner)
    {
        using var db = Open();
        return ListSpeciesModsUnlocked(db, owner);
    }

    internal IReadOnlyList<SpeciesModRow> ListSpeciesModsUnlocked(SqliteConnection db, EmpireRef owner)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT mod_id, save_id, empire_id, species_id, mechanism, correlation_id, instance_id,
                   catalog_revision, created_utc
            FROM rpg_player_species_mod
            WHERE save_id=$s AND empire_id=$e
            ORDER BY mod_id;
            """;
        cmd.Parameters.AddWithValue("$s", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        var list = new List<SpeciesModRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SpeciesModRow(
                r.GetInt64(0), r.GetInt64(1), new EmpireId(r.GetString(2)), r.GetString(3),
                ParseMechanism(r.GetString(4)), r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetString(8)));
        }
        return list;
    }

    static SpeciesModMechanism ParseMechanism(string token) => token switch
    {
        "fusion-pick" => SpeciesModMechanism.FusionPick,
        _ => throw new InvalidOperationException(
            $"rpg_player_species_mod carries an unknown mechanism '{token}'"),
    };
}
