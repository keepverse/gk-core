using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>Task B5 — `rpg_tree_node_state` / `RpgStore.PassiveTree.cs` (spec-tree-state.md §1, §2,
/// §6).</summary>
public class PassiveTreeStateTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public PassiveTreeStateTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void A_node_owned_but_never_soul_levelled_persists_a_row_with_soul_level_zero()
    {
        // §1.1's own named case: row presence means owned, and soul_level = 0 is a REAL state.
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        var loaded = _store.LoadTreeState(AllocationScope.Commander, "player:1");

        Assert.True(loaded.ContainsKey("skill.might-off-t1-n0"));
        Assert.Equal(0L, loaded["skill.might-off-t1-n0"]);
    }

    [Fact]
    public void A_node_never_owned_has_no_row_at_all()
    {
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 3 });

        var loaded = _store.LoadTreeState(AllocationScope.Commander, "player:1");

        Assert.False(loaded.ContainsKey("skill.might-off-t2-n0"));
        Assert.Single(loaded);
    }

    [Fact]
    public void Save_is_a_full_delete_then_insert_a_respec_actually_removes_rows()
    {
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["a"] = 5, ["b"] = 2 });
        Assert.Equal(2, _store.LoadTreeState(AllocationScope.Commander, "player:1").Count);

        // Respec: save an EMPTY set -- both prior rows must actually be gone, not left stale.
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long>());

        Assert.Empty(_store.LoadTreeState(AllocationScope.Commander, "player:1"));
    }

    [Fact]
    public void Different_scopes_do_not_bleed_into_each_other()
    {
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["a"] = 1 });
        _store.SaveTreeNodeState(AllocationScope.UniqueCreature, "instance:abc",
            new Dictionary<string, long> { ["b"] = 2 });

        var commander = _store.LoadTreeState(AllocationScope.Commander, "player:1");
        var unique = _store.LoadTreeState(AllocationScope.UniqueCreature, "instance:abc");

        Assert.True(commander.ContainsKey("a"));
        Assert.False(commander.ContainsKey("b"));
        Assert.True(unique.ContainsKey("b"));
        Assert.False(unique.ContainsKey("a"));
    }

    [Fact]
    public void A_negative_soul_level_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _store.SaveTreeNodeState(
            AllocationScope.Commander, "player:1", new Dictionary<string, long> { ["a"] = -1 }));
    }

    [Fact]
    public void Soul_level_round_trips_at_64_bit_range()
    {
        // §7: soul_level is a SQLite INTEGER (64-bit), read with GetInt64, never GetInt32.
        const long bigSoulLevel = 3_000_000_000L; // exceeds int.MaxValue
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["a"] = bigSoulLevel });

        var loaded = _store.LoadTreeState(AllocationScope.Commander, "player:1");
        Assert.Equal(bigSoulLevel, loaded["a"]);
    }

    // ---- §6: LoadTreeStateBatch — one query, one lock, one connection for a whole squad ----

    [Fact]
    public void LoadTreeStateBatch_serves_a_six_actor_squad_in_one_call()
    {
        var keys = new (AllocationScope, string)[]
        {
            (AllocationScope.Commander, "player:1"),
            (AllocationScope.UniqueCreature, "instance:1"),
            (AllocationScope.UniqueCreature, "instance:2"),
            (AllocationScope.UniqueCreature, "instance:3"),
            (AllocationScope.UniqueCreature, "instance:4"),
            (AllocationScope.UniqueCreature, "instance:5"),
        };
        foreach (var (scope, key) in keys)
            _store.SaveTreeNodeState(scope, key, new Dictionary<string, long> { [$"node-{key}"] = 1 });

        var batch = _store.LoadTreeStateBatch(keys);

        Assert.Equal(6, batch.Count);
        foreach (var (scope, key) in keys)
            Assert.True(batch[(scope, key)].ContainsKey($"node-{key}"));
    }

    [Fact]
    public void LoadTreeStateBatch_returns_an_empty_set_for_a_key_with_no_saved_state()
    {
        var keys = new (AllocationScope, string)[] { (AllocationScope.Commander, "player:never-saved") };

        var batch = _store.LoadTreeStateBatch(keys);

        Assert.True(batch.ContainsKey((AllocationScope.Commander, "player:never-saved")));
        Assert.Empty(batch[(AllocationScope.Commander, "player:never-saved")]);
    }

    [Fact]
    public void LoadTreeStateBatch_does_not_cross_contaminate_similarly_named_keys()
    {
        _store.SaveTreeNodeState(AllocationScope.UniqueCreature, "instance:1",
            new Dictionary<string, long> { ["a"] = 1 });
        _store.SaveTreeNodeState(AllocationScope.UniqueCreature, "instance:2",
            new Dictionary<string, long> { ["a"] = 2 });

        var batch = _store.LoadTreeStateBatch(new (AllocationScope, string)[]
        {
            (AllocationScope.UniqueCreature, "instance:1"),
            (AllocationScope.UniqueCreature, "instance:2"),
        });

        Assert.Equal(1L, batch[(AllocationScope.UniqueCreature, "instance:1")]["a"]);
        Assert.Equal(2L, batch[(AllocationScope.UniqueCreature, "instance:2")]["a"]);
    }

    [Fact]
    public void LoadTreeStateBatch_on_an_empty_key_list_returns_empty_without_querying()
    {
        var batch = _store.LoadTreeStateBatch(Array.Empty<(AllocationScope, string)>());
        Assert.Empty(batch);
    }

    [Fact]
    public void LoadTreeStateBatch_serves_2000_keys_without_hitting_sqlites_expression_depth_limit()
    {
        // Task C8's volume proof caught this for real: an unchunked OR-chain over 2,000 keys throws
        // "Expression tree is too large (maximum depth 1000)" before a single row comes back. Pinned
        // here at the exact scale that broke it, not just the 6-actor squad case above.
        var keys = new (AllocationScope, string)[2000];
        for (var i = 0; i < 2000; i++)
        {
            keys[i] = (AllocationScope.UniqueCreature, $"instance:{i}");
            _store.SaveTreeNodeState(AllocationScope.UniqueCreature, $"instance:{i}",
                new Dictionary<string, long> { [$"node-{i}"] = i });
        }

        var batch = _store.LoadTreeStateBatch(keys);

        Assert.Equal(2000, batch.Count);
        for (var i = 0; i < 2000; i += 137) // sampled, not exhaustive
            Assert.Equal(i, batch[(AllocationScope.UniqueCreature, $"instance:{i}")][$"node-{i}"]);
    }
}
