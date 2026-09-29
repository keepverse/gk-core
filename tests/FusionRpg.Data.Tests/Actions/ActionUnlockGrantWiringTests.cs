using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// T59.7 (spec-action-instance-and-grant.md §4): `AwardUniqueActorXpUnlocked`'s new `LevelsGained`
/// field wired to a real `ActionUnlockGrantService` roll, through both real production call sites'
/// shared shape (`AwardUniqueActorXp` here; the expedition reward apply mirrors it identically).
///
/// <para>`UnlockTuningPolicy`/`ActionFamilyMapPolicy` are process-wide statics (matching
/// `RungPolicy`/`CreatureSpeciesCatalog`'s own established shape) — configured once here, never reset,
/// the same convention every other `*Policy`/`*Hub` in this codebase already follows. Every EXISTING
/// XP-award test in this project is unaffected regardless, because `TryRollActionUnlocks` no-ops
/// whenever `UnlockTuningPolicy.Tuning` is unset — this file is what turns it on.</para>
/// </summary>
public class ActionUnlockGrantWiringTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    static ActionUnlockGrantWiringTests()
    {
        // DeltaMilli: 1000 -- NO decay, so every roll (not just the first) stays at a 100% chance.
        // A real DeltaMilli < 1000 decays the chance per earn (chance(n) = p1 * delta^n) -- correct
        // ratchet behavior, but wrong for THIS test, which wants every one of several rolls to
        // deterministically succeed so it can assert on how many grants landed, not on which ones did.
        UnlockTuningPolicy.Configure(new UnlockTuning(
            P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));
        ActionFamilyMapPolicy.Configure(new Dictionary<string, IReadOnlyList<string>>());
    }

    public ActionUnlockGrantWiringTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    void SeedAction(string actionId)
    {
        var containerId = "skill." + actionId.Replace('.', '-');
        var containerCheck = _store.UpsertContainer(new ContainerRow { ContainerId = containerId, Kind = ContainerKind.Skill });
        Assert.True(containerCheck.IsOk, containerCheck.Detail);

        var actionCheck = _store.UpsertAction(new ActionRow
        {
            ActionId = actionId, Name = actionId, Kind = ActionKind.Skill, Rung = 1,
            Enabled = true, Grantable = true, ContainerId = containerId,
        });
        Assert.True(actionCheck.IsOk, actionCheck.Detail);
    }

    /// <summary>
    /// The row `gk-data/packs/fusion/data/seed/actions/authored-basics.json` imports on every real boot: `Kind = Basic`,
    /// scope `general` — and `Grantable = true`, because `ActionCorpusComposer.cs:186` stamps that flag
    /// unconditionally for composed corpus rows (ADG-F5). `act.attack` is exactly this shape, which is
    /// why a grantability filter must test the kind too.
    /// </summary>
    void SeedBasic(string actionId)
    {
        var containerId = "skill." + actionId.Replace('.', '-');
        var containerCheck = _store.UpsertContainer(new ContainerRow { ContainerId = containerId, Kind = ContainerKind.Skill });
        Assert.True(containerCheck.IsOk, containerCheck.Detail);

        var actionCheck = _store.UpsertAction(new ActionRow
        {
            ActionId = actionId, Name = actionId, Kind = ActionKind.Basic, Rung = 1,
            Enabled = true, Grantable = true, ContainerId = containerId,
        });
        Assert.True(actionCheck.IsOk, actionCheck.Detail);
    }

    [Fact]
    public void ALevelGainWithNoImportedActionsIsALegalNoOpAndAwardStillSucceeds()
    {
        var actor = _store.CreateUniqueActor(playerId: 1, side: "plant", typeId: 1);

        var (ok, reason, updated, _) = _store.AwardUniqueActorXp(actor.InstanceId, delta: 1_000_000);

        Assert.True(ok, reason);
        Assert.NotNull(updated);
        Assert.True(updated!.Level > 1); // liveness -- a real level gain actually happened
    }

    [Fact]
    public void ARealLevelGainGrantsAnImportedActionToTheSpecimen()
    {
        SeedAction("action.wiring-test.only");
        var actor = _store.CreateUniqueActor(playerId: 1, side: "plant", typeId: 1);

        var (ok, reason, updated, _) = _store.AwardUniqueActorXp(actor.InstanceId, delta: 1_000_000);
        Assert.True(ok, reason);
        Assert.True(updated!.Level > 1);

        var grants = _store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId)); // matches WebMatchService.EquippedActionIdsFor's own real read scope (action-grant-owner-kind-durability, fixed 2026-09-07)
        Assert.Contains(grants, g => g.ActionId == "action.wiring-test.only");
    }

    /// <summary>
    /// ADG-F5, through the real path: a real `RpgStore` whose `rpg_action` catalog holds a Basic row
    /// still awards the level-up, and the unlock roll grants only rows the grant write path accepts.
    ///
    /// <para>Pre-fix this is the exact shape ADG-F4 measured on a live store: the roll's candidate set
    /// was the whole catalog, it reached the basic, `UpsertGrant`'s `ActionValidator.ValidateGrant`
    /// refused it (`BasicCollision`), and the grant delegate's throw
    /// (`RpgStore.UniqueActors.cs:2127-2134`) rolled the XP award's own transaction back — so `ok` came
    /// back false and a real level vanished. `SeedBasic` reproduces the production row's shape
    /// (`Kind = Basic`, `grantable = 1`, scope general), and the assertion below reads the store back
    /// rather than the roll's own return value.</para>
    ///
    /// <para>The catalog's un-grantable ids are DERIVED from the store through
    /// `ActionValidator.IsGrantable` — the same predicate the roll filters with — instead of naming
    /// `act.attack`: the contract is "no grant exists for a row the write path refuses", not "this one
    /// id is special".</para>
    /// </summary>
    [Fact]
    public void ACatalogHoldingABasicStillAwardsTheLevelUpAndGrantsOnlyGrantableActions()
    {
        SeedBasic("act.attack");
        SeedAction("action.wiring-test.empty-safe");
        var actor = _store.CreateUniqueActor(playerId: 1, side: "plant", typeId: 1);

        var (ok, reason, updated, _) = _store.AwardUniqueActorXp(actor.InstanceId, delta: 1_000_000);

        Assert.True(ok, reason);
        Assert.True(updated!.Level > 1); // liveness: a real level gain actually happened

        var notGrantable = _store.ListActionIds()
            .Select(_store.GetAction)
            .Where(a => a is not null && !ActionValidator.IsGrantable(a!))
            .Select(a => a!.ActionId)
            .ToList();
        Assert.Contains("act.attack", notGrantable); // liveness: the catalog really does hold a refused row

        var grants = _store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId));
        Assert.Contains(grants, g => g.ActionId == "action.wiring-test.empty-safe");
        foreach (var g in grants) Assert.DoesNotContain(g.ActionId, notGrantable);
    }

    /// <summary>Acceptance: "a level gain that crosses N thresholds in one award attempts N rolls,
    /// each pricing independently." Three distinct actions, `AlwaysAccepts`-shaped tuning (every roll
    /// succeeds) — a level gain crossing >= 3 thresholds must grant all three, not one.</summary>
    [Trait("Category", "Heavy")]
    [Fact]
    public void ALevelGainCrossingMultipleThresholdsAttemptsOneRollPerLevelGained()
    {
        SeedAction("action.wiring-test.a");
        SeedAction("action.wiring-test.b");
        SeedAction("action.wiring-test.c");
        var actor = _store.CreateUniqueActor(playerId: 1, side: "plant", typeId: 1);

        var (ok, reason, updated, _) = _store.AwardUniqueActorXp(actor.InstanceId, delta: 100_000_000);
        Assert.True(ok, reason);
        var levelsGained = updated!.Level - 1;
        Assert.True(levelsGained >= 3, $"expected a huge XP delta to cross at least 3 levels; got {levelsGained}");

        var grants = _store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId)); // matches WebMatchService.EquippedActionIdsFor's own real read scope (action-grant-owner-kind-durability, fixed 2026-09-07)
        Assert.Equal(3, grants.Count); // exactly the 3 available candidates, no more, no fewer
    }
}
