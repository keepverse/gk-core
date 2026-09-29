using FusionRpg.Contracts;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Regression locks: the exactly-once reward transaction (a crashed collect retry must never
/// double-pay) and the soul trim over a mixed earn/spend ledger.
///
/// <para><c>DiskSemantics</c>: <c>Soul_trim_keeps_a_mixed_earn_spend_ledger_consistent</c> drives the
/// archive entry point <c>TrimSoulLedgerTails</c>, which refuses the memory plan, through
/// <c>DataTestStore.CreateFileBacked()</c> (docs/contributing/testing-standard.md R2), so the default
/// profile skips the class and it runs at <c>full</c>/nightly/gate.</para>
/// </summary>
[Trait("Category", "DiskSemantics")]
public class ExpeditionRewardApplyTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ExpeditionRewardApplyTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s => s.Side == "zombie");

    CreatureMintSpec Spec() => new()
    {
        SpeciesId = CatalogSpecies.SpeciesId,
        Side = "zombie",
        GameTypeId = CatalogSpecies.GameTypeId,
        Rarity = "chaff",
        Variant = "normal",
        ElementPrimary = "fire",
        TraitIds = new List<string> { "swift" },
        Origin = "expedition"
    };

    [Fact]
    public void Reward_apply_is_exactly_once_across_retries()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var instanceId = specimen.Actor.InstanceId;
        var (_, _, row) = _store.DispatchExpedition(1, "reward-1", "scout-30m", new[] { instanceId }, 1);

        var rewards = new RpgStore.ExpeditionRewardApply(
            EventSouls: 120,
            Materials: new[] { ("shard.common", 2L) },
            SpecimenXp: new[] { (instanceId, 30L) },
            WildMints: new[] { Spec() });

        var first = _store.ApplyExpeditionRewards(row!.Id, 1, ExpeditionStates.Collected, rewards);
        Assert.True(first.Applied);
        Assert.Single(first.Minted);
        var balance = _store.GetSoulBalance(1).Balance;
        var xp = _store.ListCreatureRoster(1).Items.Single(s => s.Profile.InstanceId == instanceId).Actor.Xp;
        Assert.Equal(120, balance);
        Assert.Equal(30.0, xp);

        // Crash-retry: the state gate refuses and writes NOTHING — no souls, xp, mints, materials.
        var retry = _store.ApplyExpeditionRewards(row.Id, 1, ExpeditionStates.Collected, rewards);
        Assert.False(retry.Applied);
        Assert.Equal("expedition.closed", retry.Reason);
        Assert.Empty(retry.Minted);
        Assert.Equal(balance, _store.GetSoulBalance(1).Balance);
        Assert.Equal(xp, _store.ListCreatureRoster(1).Items.Single(s => s.Profile.InstanceId == instanceId).Actor.Xp);
        Assert.Equal(2, _store.ListCreatureMaterials(1).Single(m => m.MaterialId == "shard.common").Qty);
        Assert.Equal(2, _store.ListCreatureRoster(1).Items.Count); // original + one wild join, never two
    }

    [Fact]
    public void Expedition_souls_past_headroom_throw_instead_of_silently_clamping()
    {
        // T3.5 (spec-caps-reconcile.md §2.1, §11.2a): before this task, AwardSouls threw on excess but
        // this path silently clamped via Math.Min(rewards.EventSouls, MaxSoulAward) -- "two policies
        // for one ceiling, and the silent one is on the reward path." Now both throw on the SAME
        // dynamic headroom, proven here by pushing balance to within 100 of long.MaxValue via the
        // audited AwardSouls path, then asking the expedition path for more than that headroom.
        var (specimen, _) = _store.MintCreature(1, Spec());
        var (_, _, row) = _store.DispatchExpedition(1, "reward-headroom", "scout-30m", new[] { specimen.Actor.InstanceId }, 1);

        _store.AwardSouls(1, long.MaxValue - 100, "seed", "near-ceiling");
        var balanceBefore = _store.GetSoulBalance(1).Balance;

        var rewards = new RpgStore.ExpeditionRewardApply(
            EventSouls: 1000, // headroom is only 100 -- this must be refused, not shorted to 100
            Materials: Array.Empty<(string, long)>(),
            SpecimenXp: Array.Empty<(string, long)>(),
            WildMints: Array.Empty<CreatureMintSpec>());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _store.ApplyExpeditionRewards(row!.Id, 1, ExpeditionStates.Collected, rewards));

        // Refused, not shorted: the transaction rolled back entirely -- balance untouched and the
        // expedition still open (a future retry with a legal amount can still succeed).
        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.Equal(ExpeditionStates.Dispatched, _store.TryGetExpedition(row!.Id)!.State);
    }

    [Fact]
    public void Bad_material_in_rewards_applies_nothing()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var (_, _, row) = _store.DispatchExpedition(1, "reward-2", "scout-30m", new[] { specimen.Actor.InstanceId }, 1);

        Assert.ThrowsAny<Exception>(() => _store.ApplyExpeditionRewards(
            row!.Id, 1, ExpeditionStates.Collected,
            new RpgStore.ExpeditionRewardApply(50, new[] { ("essence.plasma", 1L) },
                Array.Empty<(string, long)>(), Array.Empty<CreatureMintSpec>())));

        // Validation failed before the transaction: still open, still collectable, no souls.
        Assert.Equal(ExpeditionStates.Dispatched, _store.TryGetExpedition(row!.Id)!.State);
        Assert.Equal(0, _store.GetSoulBalance(1).Balance);
    }

    [Fact]
    public void Reset_wipes_the_wave_cd_tables()
    {
        // 2026-08-21 review I5: surviving log/expedition rows would resurrect matches for wiped
        // players against recycled AUTOINCREMENT run ids on the next boot sweep.
        var (specimen, _) = _store.MintCreature(1, Spec());
        _store.DispatchExpedition(1, "reset-exp", "scout-30m", new[] { specimen.Actor.InstanceId }, 1);
        _store.AppendWebMatchLog(1, "reset-log", "reset-match", "{}", 1, 1, 1, 1);
        _store.AddCreatureMaterials(1, new[] { ("shard.common", 1L) });

        _store.Reset();

        Assert.Empty(_store.ListUnresolvedWebMatches());
        Assert.Null(_store.TryGetWebMatchLog(1, "reset-log"));
        Assert.Empty(_store.ListExpeditions(1));
        Assert.Empty(_store.ListCreatureMaterials(1));
        Assert.False(_store.HasActiveExpeditionMembership(specimen.Actor.InstanceId));
    }

    [Fact]
    public void Seed_parsing_is_defensive()
    {
        Assert.Equal(42UL, RpgStore.ParseSeed("42"));
        Assert.Equal(0UL, RpgStore.ParseSeed("not-a-seed"));
        Assert.Equal(0UL, RpgStore.ParseSeed(null));
        Assert.Equal(0UL, RpgStore.ParseSeed("-5"));
    }

    [Fact]
    public void Soul_trim_keeps_a_mixed_earn_spend_ledger_consistent()
    {
        // This one case exercises `TrimSoulLedgerTails`, a filesystem-backed archive entry point
        // (the overflow lands in a segment archive under `archive/`), so its subject IS the file
        // substrate — it uses the leak-proof file-backed helper rather than the class's memory store.
        using var file = DataTestStore.CreateFileBacked();
        var store = file.Store;

        for (var i = 0; i < 20; i++)
            store.AwardSouls(1, 100, "seed", "mixed-earn-" + i);
        for (var i = 0; i < 5; i++)
            Assert.True(store.TrySpendSouls(1, 50, "summon", "mixed-spend-" + i).Ok);

        var before = store.GetSoulBalance(1);
        Assert.Equal(20 * 100 - 5 * 50, before.Balance);

        store.TrimSoulLedgerTails(retainOverride: 8);

        var after = store.GetSoulBalance(1);
        Assert.Equal(before.Balance, after.Balance);
        Assert.Equal(before.EarnedTotal, after.EarnedTotal);
        Assert.Equal(before.SpentTotal, after.SpentTotal);
        Assert.Equal(8, store.ListSoulLedger(1, 100).Items.Count);

        // Post-trim economy still works end to end.
        store.AwardSouls(1, 10, "seed", "post-trim");
        Assert.Equal(after.Balance + 10, store.GetSoulBalance(1).Balance);
    }
}
