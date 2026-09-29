using FusionRpg.Core.Items.Consumables;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// <c>consumable_def</c> and <c>rpg_run_draught</c> — ssot-consumables.md §5.2–5.3, module 18 —
/// against a real SQLite store, not a mock.
///
/// <para>The two properties that matter are failure mode 7 (the sealed-run exploit) and §5.3's
/// determinism-input rule, and both are proven by driving the store rather than by review.</para>
/// </summary>
public class RunDraughtStoreTests : IDisposable
{
    const string Player = "player-1";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public RunDraughtStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static ConsumableDefRow Def(string id, string group = "atom.might|", int cost = 1) =>
        new(id, ConsumableClass.Draught, new[] { UseContext.Dispatch }, 3, group, cost);

    // ---- consumable_def -------------------------------------------------------------------------------

    [Fact]
    public void A_def_row_round_trips_including_both_nullable_seam_columns()
    {
        var row = Def("consumable.k2-001") with
        {
            UseContexts = new[] { UseContext.Menu, UseContext.Dispatch },
            GrantsActionId = null,
            CooldownKey = null,
        };
        _store.UpsertConsumableDef(row);

        var back = _store.GetConsumableDef("consumable.k2-001");
        Assert.NotNull(back);
        Assert.Equal(ConsumableClass.Draught, back!.ClassId);
        Assert.Equal(new[] { UseContext.Menu, UseContext.Dispatch }, back.UseContexts);
        Assert.Equal(3, back.Grade);
        Assert.Equal("atom.might|", back.ExclusionGroup);
        Assert.Null(back.GrantsActionId);
        Assert.Null(back.CooldownKey);

        // …and the seam is writable the day the action layer names one, with no schema change.
        _store.UpsertConsumableDef(row with { GrantsActionId = "action.quaff", CooldownKey = "cd.tonic" });
        var seamed = _store.GetConsumableDef("consumable.k2-001")!;
        Assert.Equal("action.quaff", seamed.GrantsActionId);
        Assert.Equal("cd.tonic", seamed.CooldownKey);
    }

    [Fact]
    public void The_use_context_wire_form_is_stable_so_two_logs_of_one_row_are_byte_comparable()
    {
        _store.UpsertConsumableDef(Def("consumable.k1-001") with
        {
            UseContexts = new[] { UseContext.Dispatch, UseContext.Menu },
        });
        Assert.Equal("menu,dispatch", ReadColumn("SELECT use_context FROM consumable_def;"));
    }

    [Fact]
    public void consumable_def_carries_no_scalar_effect_column()
    {
        // Success criterion 1, asserted at the schema rather than in prose: a heal_amount column is the
        // one thing that would make the later absorption a migration.
        var cols = TableColumns("consumable_def");
        Assert.Equal(
            new[]
            {
                "class_id", "container_id", "cooldown_key", "enabled", "exclusion_group",
                "grade", "grants_action_id", "manifest_cost", "revision", "use_context",
            },
            cols);
    }

    [Fact]
    public void rpg_run_draught_is_keyed_on_run_kind_run_id_seq_so_the_snapshot_has_a_stable_order()
    {
        Assert.Equal(
            new[] { "consumed_utc", "container_id", "qty", "run_id", "run_kind", "seq" },
            TableColumns("rpg_run_draught"));
        Assert.Equal(new[] { "run_kind", "run_id", "seq" }, PrimaryKey("rpg_run_draught"));
    }

    [Fact]
    public void effect_binding_carries_no_duration_which_is_why_a_timed_buff_must_be_a_status()
    {
        // §4.5, as a schema assertion. No expiry, no duration, no until-tick — so v1's lifetime is a
        // lifecycle (bind at run start, withdraw by source) and this module builds no clock.
        var cols = TableColumns("effect_binding");
        foreach (var absent in new[] { "expires_utc", "duration_ms", "until_tick", "expiry" })
            Assert.DoesNotContain(absent, cols);
        Assert.Contains("source", cols);
    }

    // ---- the dispatch spend -----------------------------------------------------------------------------

    [Fact]
    public void A_draught_is_spent_in_the_same_transaction_as_the_stock_decrement()
    {
        _store.AdjustStock(Player, "consumable.k2-001", 5);
        _store.AdjustStock(Player, "consumable.k2-002", 2);

        var result = _store.TrySpendDraughts(Player, "expedition", 42, new[]
        {
            new DraughtManifestEntry("consumable.k2-001", 2),
            new DraughtManifestEntry("consumable.k2-002", 1),
        });

        Assert.True(result.Ok);
        Assert.Equal("", result.Reason);
        Assert.Equal(2, result.RowsWritten);
        Assert.Equal(3, _store.StockQty(Player, "consumable.k2-001"));
        Assert.Equal(1, _store.StockQty(Player, "consumable.k2-002"));

        var rows = _store.ListRunDraughts("expedition", 42);
        Assert.Equal(new[] { 0, 1 }, rows.Select(r => r.Seq).ToArray());
        Assert.Equal(new[] { "consumable.k2-001", "consumable.k2-002" }, rows.Select(r => r.ContainerId).ToArray());
        Assert.Equal(new[] { 2, 1 }, rows.Select(r => r.Qty).ToArray());
    }

    [Fact]
    public void An_insufficient_stack_rolls_the_WHOLE_manifest_back_so_nothing_is_half_spent()
    {
        _store.AdjustStock(Player, "consumable.k2-001", 5);
        _store.AdjustStock(Player, "consumable.k2-002", 1);

        var result = _store.TrySpendDraughts(Player, "expedition", 7, new[]
        {
            new DraughtManifestEntry("consumable.k2-001", 2),
            new DraughtManifestEntry("consumable.k2-002", 9),   // more than held
        });

        Assert.False(result.Ok);
        Assert.Equal("stock.insufficient", result.Reason);
        // the FIRST line's decrement is gone too — no peek-and-keep
        Assert.Equal(5, _store.StockQty(Player, "consumable.k2-001"));
        Assert.Equal(1, _store.StockQty(Player, "consumable.k2-002"));
        Assert.Empty(_store.ListRunDraughts("expedition", 7));
    }

    [Fact]
    public void A_non_positive_qty_refuses_the_whole_manifest_and_writes_nothing()
    {
        _store.AdjustStock(Player, "consumable.k2-001", 5);
        var result = _store.TrySpendDraughts(Player, "expedition", 8, new[]
        {
            new DraughtManifestEntry("consumable.k2-001", 1),
            new DraughtManifestEntry("consumable.k2-001", 0),
        });

        Assert.False(result.Ok);
        Assert.Equal("draught.nonpositive", result.Reason);
        Assert.Equal(5, _store.StockQty(Player, "consumable.k2-001"));
        Assert.Empty(_store.ListRunDraughts("expedition", 8));
    }

    [Fact]
    public void A_retry_on_a_sealed_run_is_a_replay_and_spends_nothing()
    {
        _store.AdjustStock(Player, "consumable.k2-001", 5);
        var manifest = new[] { new DraughtManifestEntry("consumable.k2-001", 2) };

        Assert.True(_store.TrySpendDraughts(Player, "expedition", 11, manifest).Ok);
        Assert.Equal(3, _store.StockQty(Player, "consumable.k2-001"));

        var again = _store.TrySpendDraughts(Player, "expedition", 11, manifest);
        Assert.True(again.Ok);
        Assert.Equal("replay", again.Reason);
        Assert.Equal(3, _store.StockQty(Player, "consumable.k2-001"));   // unchanged
        Assert.Single(_store.ListRunDraughts("expedition", 11));
    }

    [Fact]
    public void Run_draughts_are_written_before_the_seed_resolves_because_the_seal_runs_inside_the_transaction()
    {
        _store.AdjustStock(Player, "consumable.k2-001", 3);

        var sawRows = -1;
        var result = _store.TrySpendDraughts(Player, "expedition", 21,
            new[] { new DraughtManifestEntry("consumable.k2-001", 1) },
            seal: db =>
            {
                // the seal is what reads the manifest to build the sealed input; the rows must already
                // be there when it runs, or the run is sealed from an empty squad
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM rpg_run_draught WHERE run_kind='expedition' AND run_id=21;";
                sawRows = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            });

        Assert.True(result.Ok);
        Assert.Equal(1, sawRows);
    }

    [Fact]
    public void A_throwing_seal_rolls_back_the_stock_too_so_a_failed_dispatch_costs_nothing()
    {
        _store.AdjustStock(Player, "consumable.k2-001", 4);

        Assert.ThrowsAny<Exception>(() => _store.TrySpendDraughts(Player, "expedition", 33,
            new[] { new DraughtManifestEntry("consumable.k2-001", 2) },
            seal: _ => throw new InvalidOperationException("the run failed to seal")));

        Assert.Equal(4, _store.StockQty(Player, "consumable.k2-001"));
        Assert.Empty(_store.ListRunDraughts("expedition", 33));
    }

    [Fact]
    public void Recall_refunds_no_draught_because_no_refund_path_exists_at_all()
    {
        // ⛔ Failure mode 7 from the other side, and asserted STRUCTURALLY rather than by not calling
        // one: dispatch-and-instantly-recall must not be a free outcome preview, so the store carries
        // no refund, no credit and no unspend for a draught.
        var names = typeof(RpgStore).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Draught", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ListRunDraughts", "TrySpendDraughts" }, names);
    }

    [Fact]
    public void Two_runs_keep_separate_manifests_and_a_battle_run_does_not_collide_with_an_expedition()
    {
        _store.AdjustStock(Player, "consumable.k2-001", 10);
        _store.TrySpendDraughts(Player, "expedition", 1, new[] { new DraughtManifestEntry("consumable.k2-001", 1) });
        _store.TrySpendDraughts(Player, "battle", 1, new[] { new DraughtManifestEntry("consumable.k2-001", 2) });

        Assert.Equal(1, _store.ListRunDraughts("expedition", 1).Single().Qty);
        Assert.Equal(2, _store.ListRunDraughts("battle", 1).Single().Qty);
        Assert.Equal(7, _store.StockQty(Player, "consumable.k2-001"));
    }

    [Fact]
    public void Listing_defs_is_ordinal_ordered_so_a_catalog_load_is_reproducible()
    {
        _store.UpsertConsumableDef(Def("consumable.k3-002"));
        _store.UpsertConsumableDef(Def("consumable.k1-001", "atom.vitality|"));
        _store.UpsertConsumableDef(Def("consumable.k2-003", "atom.ferocity|"));

        Assert.Equal(
            new[] { "consumable.k1-001", "consumable.k2-003", "consumable.k3-002" },
            _store.ListConsumableDefs().Select(d => d.ContainerId).ToArray());
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    string ReadColumn(string sql)
    {
        using var db = OpenRead();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    IReadOnlyList<string> TableColumns(string table)
    {
        using var db = OpenRead();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var cols = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        cols.Sort(StringComparer.Ordinal);
        return cols;
    }

    IReadOnlyList<string> PrimaryKey(string table)
    {
        using var db = OpenRead();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var pk = new List<(int Ord, string Name)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ord = r.GetInt32(5);
            if (ord > 0) pk.Add((ord, r.GetString(1)));
        }

        return pk.OrderBy(p => p.Ord).Select(p => p.Name).ToList();
    }

    SqliteConnection OpenRead()
    {
        return SqliteConnectionFactory.Open(_store.HotPath);
    }
}
