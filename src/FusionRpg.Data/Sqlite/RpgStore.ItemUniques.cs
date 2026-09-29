using FusionRpg.Core.Items.Uniques;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// <c>item_unique</c> — ssot-uniques.md §5.2's nine columns, keyed 1:1 on an ordinary
/// <c>effect_container</c> (module 17).
///
/// <para><b>The container and its base type stay ordinary.</b> §4.2 rejected both alternatives on
/// purpose: a boolean on the base type has no home for <c>counter_pressure</c>, <c>acquisition</c>,
/// <c>budget_ae</c>, the display parent or the flavour key, and deriving the class from a shape
/// (<c>pool_rolls ≤ 1</c> plus a fat core) makes it a class anyone can forge. So the flag is a table,
/// and bind, frame filter, role gate, requirement gate and socket capacity all need no branch.</para>
///
/// <para>⚠ <b><c>derived_from</c> carries no FK, and that is a wiring gap, not a design one.</b>
/// §5.2 wants <c>FK → item_base_type</c>. ⚠ <b>Corrected 2026-09-23:</b> this paragraph used to say
/// there was no <c>item_base_type</c> table "in the shipped schema" — no longer true. The table
/// exists (<see cref="EnsureBaseTypeSchemaUnlocked"/>, populated at boot by <c>Program.cs</c>'s
/// <c>ImportBaseTypes</c>) and its <c>id</c> is a primary key, so the FK is possible; it was never
/// added, and adding one is a schema change. The reference is checked instead by
/// <see cref="UniqueCorpusValidator"/> against the loaded base-type registry, which is where the role
/// and frame rules already resolve. The column is ready for the constraint the day the table exists.</para>
///
/// <para>⛔ <b>Nothing here special-cases the instance path.</b> A unique instance is an ordinary
/// <c>effect_instance</c> (§5.4) and <c>Instantiator</c> gains no unique branch — the sentence the
/// whole design is held to. This file writes one template-side row and reads it back; it touches no
/// instance, no binding and no op log.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureItemUniqueSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- ssot-uniques.md §5.2. Nine columns, one table, no widening of anyone else's row.
            -- Consumers: the import validator (every §6 check), item-card (sourceKind), drop-volume
            -- (the acquisition channel) and item-surfaces (the flavour line).
            CREATE TABLE IF NOT EXISTS item_unique (
              container_id    TEXT PRIMARY KEY,          -- item.{slug}; kind must be 'item'
              -- The parent base type, for display and inherited class/frame flavour. No FK: the
              -- item_base_type table exists (2026-09-23) but no FK was ever added, and adding one is
              -- a schema change.
              derived_from    TEXT    NOT NULL,
              counter_pressure TEXT   NOT NULL,          -- drawback | conditional | narrow
              budget_ae       INTEGER NOT NULL,          -- AE x 100; SC4 forbids floats in content
              power_axis      TEXT    NOT NULL,          -- one of core.v1.json's five powerCategories
              acquisition     TEXT    NOT NULL,          -- drop | source-locked | deterministic
              enhance_scope   TEXT    NOT NULL DEFAULT 'magnitude-only',
              flavour_key     TEXT,                      -- a KEY, never a literal. NULL is allowed.
              enabled         INTEGER NOT NULL DEFAULT 1,
              revision        INTEGER NOT NULL DEFAULT 1,
              FOREIGN KEY (container_id) REFERENCES effect_container(container_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_item_unique_axis ON item_unique(power_axis);
            CREATE INDEX IF NOT EXISTS ix_item_unique_acquisition ON item_unique(acquisition);
            """);
    }

    static string Wire(UniqueCounterPressure v) => v switch
    {
        UniqueCounterPressure.Drawback => "drawback",
        UniqueCounterPressure.Conditional => "conditional",
        UniqueCounterPressure.Narrow => "narrow",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    static UniqueCounterPressure ReadCounterPressure(string v) => v switch
    {
        "drawback" => UniqueCounterPressure.Drawback,
        "conditional" => UniqueCounterPressure.Conditional,
        "narrow" => UniqueCounterPressure.Narrow,
        _ => throw new InvalidOperationException($"item_unique.counter_pressure '{v}' is not one of the three"),
    };

    static string Wire(UniqueAcquisition v) => v switch
    {
        UniqueAcquisition.Drop => "drop",
        UniqueAcquisition.SourceLocked => "source-locked",
        UniqueAcquisition.Deterministic => "deterministic",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    static UniqueAcquisition ReadAcquisition(string v) => v switch
    {
        "drop" => UniqueAcquisition.Drop,
        "source-locked" => UniqueAcquisition.SourceLocked,
        "deterministic" => UniqueAcquisition.Deterministic,
        _ => throw new InvalidOperationException($"item_unique.acquisition '{v}' is not one of the three"),
    };

    static string Wire(UniqueEnhanceScope v) => v switch
    {
        UniqueEnhanceScope.MagnitudeOnly => "magnitude-only",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    static UniqueEnhanceScope ReadEnhanceScope(string v) => v switch
    {
        "magnitude-only" => UniqueEnhanceScope.MagnitudeOnly,
        _ => throw new InvalidOperationException($"item_unique.enhance_scope '{v}' is not a known scope"),
    };

    /// <summary>
    /// Write one <c>item_unique</c> row. The container must already exist — the FK says so, and a
    /// unique whose container does not exist is a flag on nothing.
    /// </summary>
    public void UpsertItemUnique(UniqueRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            ExecIn(db, tx, """
                INSERT INTO item_unique
                  (container_id, derived_from, counter_pressure, budget_ae, power_axis, acquisition,
                   enhance_scope, flavour_key, enabled, revision)
                VALUES ($id, $from, $cp, $ae, $axis, $acq, $scope, $flavour, $enabled, $rev)
                ON CONFLICT(container_id) DO UPDATE SET
                  derived_from = excluded.derived_from,
                  counter_pressure = excluded.counter_pressure,
                  budget_ae = excluded.budget_ae,
                  power_axis = excluded.power_axis,
                  acquisition = excluded.acquisition,
                  enhance_scope = excluded.enhance_scope,
                  flavour_key = excluded.flavour_key,
                  enabled = excluded.enabled,
                  revision = excluded.revision;
                """,
                ("$id", row.ContainerId), ("$from", row.DerivedFrom),
                ("$cp", Wire(row.CounterPressure)), ("$ae", row.BudgetAeHundredths),
                ("$axis", row.PowerAxis), ("$acq", Wire(row.Acquisition)),
                ("$scope", Wire(row.EnhanceScope)),
                ("$flavour", (object?)row.FlavourKey ?? DBNull.Value),
                ("$enabled", row.Enabled ? 1 : 0), ("$rev", row.Revision));
            tx.Commit();
        }
    }

    public UniqueRow? GetItemUnique(string containerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT container_id, derived_from, counter_pressure, budget_ae, power_axis, acquisition,
                       enhance_scope, flavour_key, enabled, revision
                FROM item_unique WHERE container_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", containerId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public IReadOnlyList<UniqueRow> ListItemUniques()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT container_id, derived_from, counter_pressure, budget_ae, power_axis, acquisition,
                       enhance_scope, flavour_key, enabled, revision
                FROM item_unique ORDER BY container_id;
                """;
            var result = new List<UniqueRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(Read(r));
            return result;
        }
    }

    /// <summary>
    /// §3.8's enforcement, as a query rather than a promise: is this container referenced by an
    /// <c>item_set_member</c> row? Supplied to <c>UniqueValidator</c>, which raises
    /// <c>ContentRuleViolated{unique.set-membership}</c> on a true.
    /// </summary>
    public bool IsUniqueSetMember(string containerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM item_set_member WHERE container_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", containerId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// Seed the <c>unique_eligible</c> budget key for every rung on the seeded ladder — 1 at or above
    /// the tuning floor, 0 below it.
    ///
    /// <para>Its own method, <b>not</b> folded into <c>SeedRarityLadder</c>, following modules 14, 15
    /// and 16: module 7's seeding must never grow a dependency on a later module's tuning file.</para>
    ///
    /// <para>Reads the <c>rarity</c> table's own ordinals rather than deriving them from list position,
    /// because the ladder is pre-spaced by 10 precisely so a rung can be inserted later without
    /// renumbering, and an index-derived ordinal would silently stop matching the day one is.</para>
    /// </summary>
    public void SeedUniqueEligible(UniqueTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        foreach (var rung in ListRarities())
            SetRarityBudget(rung.RarityId, FusionRpg.Core.Items.Uniques.UniqueLimits.EligibilityBudgetKey,
                tuning.IsRungEligible(rung.Ordinal) ? 1 : 0);
    }

    static UniqueRow Read(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        ReadCounterPressure(r.GetString(2)),
        r.GetInt64(3),
        r.GetString(4),
        ReadAcquisition(r.GetString(5)),
        ReadEnhanceScope(r.GetString(6)),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.GetInt32(8) != 0,
        r.GetInt32(9));
}
