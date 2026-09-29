using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>Task C8 — spec-tree-state.md §7: 2,000 actors × 40 nodes must store 80,000 rows, not
/// `2000 * 2000 * 40` (a cross-join defect) or any other multiple. `long` on both sides, `checked`
/// products, `GetInt64` never `GetInt32` (already true of `LoadTreeState`/`LoadTreeStateBatch`,
/// re-asserted here at volume).</summary>
public class TreeStateVolumeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public TreeStateVolumeTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Trait("Category", "Heavy")]
    [Fact]
    public void Two_thousand_actors_times_forty_nodes_stores_exactly_eighty_thousand_rows()
    {
        const int actorCount = 2000;
        const int nodesPerActor = 40;

        var keys = new (AllocationScope, string)[actorCount];
        for (var a = 0; a < actorCount; a++)
        {
            var nodes = new Dictionary<string, long>();
            for (var n = 0; n < nodesPerActor; n++)
                nodes[$"skill.might-off-t1-n{n}"] = 0;
            keys[a] = (AllocationScope.UniqueCreature, $"instance:{a}");
            _store.SaveTreeNodeState(AllocationScope.UniqueCreature, $"instance:{a}", nodes);
        }

        // The REAL total row count in the table -- not just the sum over the keys this test wrote,
        // so a hypothetical cross-join defect that also wrote rows under keys never touched here
        // would still be caught.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_tree_node_state;";
        var rowCount = (long)cmd.ExecuteScalar()!;

        // 80,000, never 3.1 million (2000 x 2000 x 40 -- a cross-join defect) and never 2000 (a
        // last-write-wins defect that collapsed every actor onto one row).
        Assert.Equal((long)actorCount * nodesPerActor, rowCount);
        Assert.Equal(80_000L, rowCount);

        // The batch read agrees with the raw count for the keys it was asked about.
        var batch = _store.LoadTreeStateBatch(keys);
        long batchRowCount = 0;
        checked { foreach (var rows in batch.Values) batchRowCount += rows.Count; }
        Assert.Equal(rowCount, batchRowCount);
    }

    [Fact]
    public void LoadTreeStateBatch_reads_soul_level_as_a_64_bit_integer_at_volume()
    {
        // GetInt64, never GetInt32 -- re-asserted at the volume this task is scoped to, not just the
        // single-row case B5 already covers.
        const long bigSoulLevel = 5_000_000_000L; // exceeds int.MaxValue
        var keys = new (AllocationScope, string)[200];
        for (var i = 0; i < 200; i++)
        {
            keys[i] = (AllocationScope.UniqueCreature, $"instance:{i}");
            _store.SaveTreeNodeState(AllocationScope.UniqueCreature, $"instance:{i}",
                new Dictionary<string, long> { ["a"] = bigSoulLevel + i });
        }

        var batch = _store.LoadTreeStateBatch(keys);

        Assert.Equal(200, batch.Count);
        for (var i = 0; i < 200; i++)
            Assert.Equal(bigSoulLevel + i, batch[(AllocationScope.UniqueCreature, $"instance:{i}")]["a"]);
    }
}
