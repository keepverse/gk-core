using System.IO;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>Task C10 — `RpgStore.RespecTreeState` (spec-tree-state.md §5, §5.1). Full reset in one
/// transaction, scoped per `(scope, scope_key)`, never refused for BEING a respec, priced in souls on
/// `TreeRespecPolicy`'s shape.</summary>
public class TreeRespecStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    const long PlayerId = 1;

    public TreeRespecStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var repoRoot = RepoRoot();
        PassiveTreeTuningHub.Configure(PassiveTreeTuningLoader.Parse(
            File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "passive-tree.v1.json"))));
    }

    static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact] // respec_clears_one_scope_key_only
    public void Respec_clears_one_scope_key_only()
    {
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-1");
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });
        _store.SaveTreeNodeState(AllocationScope.UniqueCreature, "instance:1",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        var outcome = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:1", "r-1");

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Empty(_store.LoadTreeState(AllocationScope.Commander, "player:1"));
        // The OTHER scope key is untouched -- a respec is never a roster-wide reset.
        Assert.NotEmpty(_store.LoadTreeState(AllocationScope.UniqueCreature, "instance:1"));
    }

    [Fact] // respec_is_never_refused
    public void Respec_is_never_refused_for_being_a_respec()
    {
        _store.AwardSouls(PlayerId, 1_000_000, "seed", "bank-2");
        for (var i = 0; i < 10; i++)
        {
            _store.SaveTreeNodeState(AllocationScope.Commander, "player:2",
                new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });
            var outcome = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:2", $"r-loop-{i}");
            Assert.True(outcome.Ok, outcome.Reason); // no "too many respecs" refusal, ever
        }
    }

    [Fact]
    public void Insufficient_balance_is_the_one_named_refusal()
    {
        // No souls awarded -- balance is 0, price is > 0.
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:3",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        var outcome = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:3", "r-poor");

        Assert.False(outcome.Ok);
        Assert.Equal("souls.insufficient", outcome.Reason);
        // A refused respec must not clear the node set -- the transaction rolled back.
        Assert.NotEmpty(_store.LoadTreeState(AllocationScope.Commander, "player:3"));
    }

    [Fact]
    public void Price_is_charged_in_souls_and_the_balance_drops_by_exactly_the_quoted_amount()
    {
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-4");
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:4",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        var outcome = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:4", "r-4");

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(1000 - outcome.PriceAmount, outcome.Balance.Balance);
        Assert.Equal(1000 - outcome.PriceAmount, _store.GetSoulBalance(PlayerId).Balance);
    }

    [Fact]
    public void Price_escalates_on_the_persisted_count_across_successive_respecs()
    {
        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-5");
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:5", new Dictionary<string, long> { ["a"] = 0 });
        var first = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:5", "r-5a");

        _store.SaveTreeNodeState(AllocationScope.Commander, "player:5", new Dictionary<string, long> { ["a"] = 0 });
        var second = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:5", "r-5b");

        Assert.True(first.Ok && second.Ok);
        Assert.True(second.PriceAmount > first.PriceAmount, "price must escalate, not repeat");
        Assert.Equal(1, first.RespecCount);
        Assert.Equal(2, second.RespecCount);
    }

    [Fact] // rebuying_the_same_set_after_respec_costs_the_same
    public void Rebuying_the_same_node_set_after_a_respec_costs_exactly_what_it_cost_before()
    {
        // TreeUnlockCost prices off ownedCount, which the respec's full clear resets to zero -- so
        // buying node 0 then node 1 costs the same sequence of prices before and after a respec.
        var beforeFirst = TreeUnlockCost.PriceOfNth(1, first: 5, step: 2);
        var beforeSecond = TreeUnlockCost.PriceOfNth(2, first: 5, step: 2);

        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-6");
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:6",
            new Dictionary<string, long> { ["a"] = 0, ["b"] = 0 });
        _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:6", "r-6");

        Assert.Empty(_store.LoadTreeState(AllocationScope.Commander, "player:6"));
        var afterFirst = TreeUnlockCost.PriceOfNth(1, first: 5, step: 2);
        var afterSecond = TreeUnlockCost.PriceOfNth(2, first: 5, step: 2);
        Assert.Equal(beforeFirst, afterFirst);
        Assert.Equal(beforeSecond, afterSecond);
    }

    [Fact]
    public void A_replayed_correlation_id_returns_the_same_outcome_without_charging_twice()
    {
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-7");
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:7", new Dictionary<string, long> { ["a"] = 0 });

        var first = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:7", "r-7-dup");
        var balanceAfterFirst = _store.GetSoulBalance(PlayerId).Balance;

        // Same correlation id again -- must not charge a second time.
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:7", new Dictionary<string, long> { ["a"] = 0 });
        var replay = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:7", "r-7-dup");

        Assert.Equal("replay", replay.Reason);
        Assert.Equal(balanceAfterFirst, _store.GetSoulBalance(PlayerId).Balance);
    }

    // ---- R4 (task C5, spec-tree-catalog.md §4): a catalog revision that retires an ALLOCATED node
    // grants a free full respec, at price zero -- no partial-refund path, no per-node compensation
    // table. Eligibility is derived fresh from the actor's current owned set every call (does the
    // actor currently hold a node the live catalog classifies Retired?), never a claimed/banked flag.

    static string TreeJson(string treeId) => $$"""
    {
      "treeId": "{{treeId}}",
      "category": "primary",
      "gateQuantity": "aptitude.Might@Commander",
      "shapeArchetype": "broad-and-flat",
      "tiers": 10,
      "branches": 2,
      "nodesPerTier": [2,2,2,2,2,2,2,2,2,2],
      "catalogVersion": 1,
      "enabled": true,
      "nodes": [
        {
          "id": "skill.{{treeId}}-off-t1-n0",
          "branch": "off",
          "tier": 1,
          "nodeKey": "n0",
          "prereqNodeIds": [],
          "nodeClass": "magnitude",
          "affixIds": ["affix.a"],
          "budgetShareMilli": 18,
          "atoms": [
            { "kindId": "stat.modify", "attachPoint": "Stat", "channelId": "atk", "op": "flat",
              "trigger": null, "whenJson": null, "kMicro": 12345, "scaleAxis": "PTheta",
              "unitClass": "GameUnits", "soulCurveId": null }
          ],
          "excludeProps": [], "exclusionForm": "None", "tagsJson": null,
          "enabled": true, "retiredAtRevision": null
        }
      ]
    }
    """;

    [Fact] // "grants a free full respec, at price zero" -- even with a zero soul balance
    public void A_retired_allocated_node_grants_a_free_respec_bypassing_the_price_and_the_balance_check()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might") }, PassiveTreeTuningHub.Tuning);
        _store.ImportTreeCatalog(new[] { TreeJson("fortitude") }, PassiveTreeTuningHub.Tuning); // retires "might"'s node
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:9",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 4 });
        // No souls awarded -- balance is 0, and the normal ladder price is > 0.

        var outcome = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:9", "r-9");

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(0, outcome.PriceAmount);
        Assert.Equal("forced-retirement", outcome.Reason);
        Assert.Empty(_store.LoadTreeState(AllocationScope.Commander, "player:9"));
    }

    [Fact] // a free respec never advances the paid-respec ladder
    public void A_free_respec_does_not_advance_the_paid_respec_count()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might") }, PassiveTreeTuningHub.Tuning);
        _store.ImportTreeCatalog(new[] { TreeJson("fortitude") }, PassiveTreeTuningHub.Tuning);
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:10",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });

        var free = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:10", "r-10a");
        Assert.Equal(0, free.RespecCount); // never touched -- it was never a priced respec

        // The NEXT respec (nothing retired left to hold) is priced as the actor's FIRST paid respec.
        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-10");
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:10", new Dictionary<string, long> { ["a"] = 0 });
        var paid = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:10", "r-10b");

        Assert.Equal("", paid.Reason);
        Assert.True(paid.PriceAmount > 0);
        Assert.Equal(1, paid.RespecCount); // the ladder starts at 1, as if the free respec never happened
    }

    [Fact] // taking the free respec clears the retired node -- the NEXT attempt is priced normally
    public void After_taking_the_free_respec_the_next_respec_attempt_is_priced_normally()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might") }, PassiveTreeTuningHub.Tuning);
        _store.ImportTreeCatalog(new[] { TreeJson("fortitude") }, PassiveTreeTuningHub.Tuning);
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:11",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 });
        _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:11", "r-11a"); // free

        // Nothing retired remains owned -- re-buying a LIVE node and respeccing again must be priced,
        // and must be refused for insufficient balance since no souls were ever awarded.
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:11",
            new Dictionary<string, long> { ["skill.fortitude-off-t1-n0"] = 0 });
        var second = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:11", "r-11b");

        Assert.False(second.Ok);
        Assert.Equal("souls.insufficient", second.Reason);
    }

    [Fact] // a live (never-retired) owned node never grants the free path
    public void An_actor_with_no_retired_node_never_gets_the_free_respec()
    {
        _store.ImportTreeCatalog(new[] { TreeJson("might") }, PassiveTreeTuningHub.Tuning);
        _store.SaveTreeNodeState(AllocationScope.Commander, "player:12",
            new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 }); // still LIVE, never retired

        var outcome = _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:12", "r-12");

        Assert.False(outcome.Ok); // no souls awarded -- priced normally, and refused for insufficient balance
        Assert.Equal("souls.insufficient", outcome.Reason);
    }

    [Fact]
    public void An_empty_scopeKey_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _store.RespecTreeState(PlayerId, AllocationScope.Commander, "", "r"));
    }

    [Fact]
    public void An_empty_correlationId_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _store.RespecTreeState(PlayerId, AllocationScope.Commander, "player:8", ""));
    }
}
