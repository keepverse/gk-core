using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>Task G2 — `rpg_gate_counter` / `RpgStore.GateCounters.cs` (spec-gate-counters.md §4).</summary>
[Trait("VerificationId", "data.gate-counter")]
public class GateCounterStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public GateCounterStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly GateCounterKey Wither = new("player", "player:1", "status_applied", "wither");

    [Fact]
    public void A_subject_with_no_credit_ever_flushed_reads_zero_never_an_error()
    {
        Assert.Equal(0L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));
    }

    [Fact]
    public void No_row_exists_until_the_first_flush_sparsity()
    {
        // Table exists (EnsureHotSchema ran in Init()) but has no rows for a fresh store.
        Assert.Equal(0L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));

        _store.FlushGateCounters(new Dictionary<GateCounterKey, long> { [Wither] = 1 });

        Assert.Equal(1L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));
    }

    [Fact]
    public void A_flush_is_additive_across_multiple_windows()
    {
        _store.FlushGateCounters(new Dictionary<GateCounterKey, long> { [Wither] = 3 });
        _store.FlushGateCounters(new Dictionary<GateCounterKey, long> { [Wither] = 4 });

        Assert.Equal(7L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));
    }

    [Fact]
    public void An_empty_flush_is_a_no_op()
    {
        _store.FlushGateCounters(new Dictionary<GateCounterKey, long>());
        Assert.Equal(0L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));
    }

    [Fact]
    public void Different_owners_and_subjects_never_bleed_into_each_other()
    {
        var poison = Wither with { SubjectId = "poison" };
        var player2 = Wither with { OwnerKey = "player:2" };

        _store.FlushGateCounters(new Dictionary<GateCounterKey, long>
        {
            [Wither] = 1,
            [poison] = 2,
            [player2] = 5,
        });

        Assert.Equal(1L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));
        Assert.Equal(2L, _store.LoadGateCounter("player", "player:1", "status_applied", "poison"));
        Assert.Equal(5L, _store.LoadGateCounter("player", "player:2", "status_applied", "wither"));
    }

    [Fact]
    public void LoadGateCountersForOwner_returns_every_subject_that_actually_has_a_row()
    {
        _store.FlushGateCounters(new Dictionary<GateCounterKey, long>
        {
            [Wither] = 1,
            [Wither with { SubjectId = "poison" }] = 2,
        });

        var all = _store.LoadGateCountersForOwner("player", "player:1", "status_applied");

        Assert.Equal(2, all.Count);
        Assert.Equal(1L, all["wither"]);
        Assert.Equal(2L, all["poison"]);
        Assert.False(all.ContainsKey("leech")); // sparse -- never credited, never present
    }

    [Fact]
    public void A_negative_delta_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            _store.FlushGateCounters(new Dictionary<GateCounterKey, long> { [Wither] = -1 }));
    }

    [Fact]
    public void Flushing_past_long_MaxValue_throws_rather_than_wrapping()
    {
        _store.FlushGateCounters(new Dictionary<GateCounterKey, long> { [Wither] = long.MaxValue });

        Assert.Throws<OverflowException>(() =>
            _store.FlushGateCounters(new Dictionary<GateCounterKey, long> { [Wither] = 1 }));
    }

    [Fact]
    public void The_table_has_exactly_the_five_columns_the_spec_names()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(rpg_gate_counter);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read())
            columns.Add(r.GetString(1));

        Assert.Equal(
            new[] { "owner_kind", "owner_key", "quantity", "subject_id", "count" }.OrderBy(x => x, StringComparer.Ordinal),
            columns.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Reset_clears_rpg_gate_counter()
    {
        _store.FlushGateCounters(new Dictionary<GateCounterKey, long> { [Wither] = 9 });
        Assert.Equal(9L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));

        _store.Reset();

        Assert.Equal(0L, _store.LoadGateCounter("player", "player:1", "status_applied", "wither"));
    }
}
