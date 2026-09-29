using System.Linq;
using System.Net.Http.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// T74/A33 (`spec-battle-holder-wiring.md` acceptance 1): on the REAL host, a specimen holding a granted
/// action above rung 1 pays that action's cost at its <b>effective</b> rung, not the authored-rung
/// fallback.
///
/// <para><b>Why the real host, and why E2E.</b> The acceptance names `RpgApiFactory` — the real
/// bootstrap, not a hand-rolled host — and that factory lives here (same placement reasoning as
/// `UnlockTuningActivationTests`, T63). The host's own `WebMatchService` is what resolves the fight,
/// through the same `ResolveAndIngest` path a real match takes, board and all.</para>
///
/// <para><b>Why this fails on pre-T74 code, by construction.</b> Two runs share one setup and one seed;
/// the ONLY difference is the holder's ladder. Run A holds the action at earn count 1 (effective rung 1,
/// the authored price) and Run B holds the same action at earn count 10 (effective rung 10, x18.151 from
/// the shipped rung table, which is ~9x the holder's whole pool). Before T74 nothing passed
/// `unlockStateFor`, so both runs read the authored rung and both attack — the "B never attacks"
/// assertion could not hold.</para>
///
/// <para><b>The price is derived, never a magic number.</b> The holder's `qi` pool is
/// `BattleRuleset.BaseResourceMax(level, "qi", maxHp)` — the same SSOT the Hub seeds resource baselines
/// from (measured here: 106 for this level-5 holder, matching the ~105 the Core fixture reports). Half of
/// it is affordable at rung 1; ~9x it is not, at any rung above 1.</para>
/// </summary>
[Collection("e2e")]
public class BattleHolderRungHostTests : IAsyncLifetime
{
    const long PlayerId = 1;
    const int Level = 5;
    const string ActionId = "skill.t74-holder-rung";

    readonly RpgApiFactory _factory;

    public BattleHolderRungHostTests(RpgApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        var r = await _factory.CreateClient().PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_held_action_prices_at_its_holders_effective_rung_not_the_authored_one()
    {
        using var boot = _factory.CreateClient();       // boots the real host (and configures UnlockTuningPolicy)

        using var store = _factory.OpenStore();     // the same memory databases Program.cs's own store opened
        store.Init();

        var actor = store.CreateUniqueActor(PlayerId, side: "plant", typeId: 1);
        SeedCostedAction(store, out var baseCost);

        var seed = 20260919UL;
        var setup = Setup(actor.InstanceId);
        var svc = _factory.Services.GetRequiredService<WebMatchService>();

        // Run A: the holder has earned to rung 1 — the authored rung, so the price is the authored one.
        store.SaveUnlockState(Owner(actor.InstanceId), UnlockState.FromPersisted(1, new[] { new HeldUnlock(ActionId, 1) }));
        var baseline = await svc.RunPlannedMatchAsync(PlayerId, "t74-rung-a", "t74-rung-a", setup, seed);
        Assert.True(baseline.Ok, baseline.Reason);
        var baselineDamage = DamageOf(baseline.Outcome!.Report);

        // Run B: the SAME holder, the SAME action, the SAME setup and seed — only its ladder moved.
        store.SaveUnlockState(Owner(actor.InstanceId), UnlockState.FromPersisted(10, new[] { new HeldUnlock(ActionId, 10) }));
        var raised = await svc.RunPlannedMatchAsync(PlayerId, "t74-rung-b", "t74-rung-b", setup, seed);
        Assert.True(raised.Ok, raised.Reason);
        var raisedDamage = DamageOf(raised.Outcome!.Report);

        // Rung 1: the action costs half the holder's own pool, so the holder commits it and lands its hit.
        Assert.True(baselineDamage > 0,
            $"rung 1 must be affordable: base cost {baseCost} against the holder's own qi pool. " +
            $"The holder dealt {baselineDamage} damage.");

        // Rung 10: the same action costs ~9x that pool. A holder whose only held action is unaffordable
        // never commits anything (ActionCostsCooldownsAdoptionTests' own proven behaviour), so it deals none.
        Assert.Equal(0, raisedDamage);
    }

    /// <summary>Acceptance 2: an actor with no `UnlockState` row reads the authored rung, byte-identical
    /// to today. Same setup and seed, empty ladder vs a rung-1 ladder — the two reports must be identical,
    /// which is also what makes the rung the only cause in the A/B above.</summary>
    [Fact]
    public async Task A_rung_one_holder_is_the_authored_rung_fallback_by_construction()
    {
        using var boot = _factory.CreateClient();

        using var store = _factory.OpenStore();
        store.Init();
        var actor = store.CreateUniqueActor(PlayerId, side: "plant", typeId: 1);
        SeedCostedAction(store, out _);

        var seed = 20260920UL;
        var setup = Setup(actor.InstanceId);
        var svc = _factory.Services.GetRequiredService<WebMatchService>();

        var emptyLadder = await svc.RunPlannedMatchAsync(PlayerId, "t74-fallback-a", "t74-fallback-a", setup, seed);
        Assert.True(emptyLadder.Ok, emptyLadder.Reason);

        store.SaveUnlockState(Owner(actor.InstanceId), UnlockState.FromPersisted(1, new[] { new HeldUnlock(ActionId, 1) }));
        var rungOne = await svc.RunPlannedMatchAsync(PlayerId, "t74-fallback-b", "t74-fallback-b", setup, seed);
        Assert.True(rungOne.Ok, rungOne.Reason);

        Assert.Equal(DamageOf(emptyLadder.Outcome!.Report), DamageOf(rungOne.Outcome!.Report));
        Assert.Equal(emptyLadder.Outcome!.Report.Outcome, rungOne.Outcome!.Report.Outcome);
        Assert.Equal(emptyLadder.Outcome!.Report.Rounds, rungOne.Outcome!.Report.Rounds);
    }

    static OwnerScope Owner(string instanceId) => new(OwnerKind.UniqueActor, instanceId);

    static long DamageOf(BattleReport report) =>
        report.Actors.Where(a => a.Key == "squad:0").Sum(a => a.DamageDealt);

    /// <summary>
    /// A costed skill the host's own resolver can really bind. The container holds ONE real seeded atom —
    /// because a container the host cannot resolve is a hard throw in `BattleRunState`, and the store
    /// ships no actions to copy one from. `MaxRange` is `int.MaxValue` and `RequiresLineOfSight` stays
    /// false, matching the engine's own basic-attack row (`BasicAttack.cs:29`): a web match resolves on a
    /// real `NormalBattleBoard`, and an action left at `ActionRow`'s default `MaxRange = 0` is refused by
    /// the engine's own range gate every round without ever being declared. The single `qi` cost is half
    /// the holder's pool: affordable at rung 1, ~9x the pool at rung 10.
    /// </summary>
    static void SeedCostedAction(RpgStore store, out int baseCost)
    {
        var maxHp = BattleRuleset.BaseHp(Level);
        baseCost = (int)Math.Max(1, BattleRuleset.BaseResourceMax(Level, "qi", maxHp) / 2);

        // The container's atom is AUTHORED here, not borrowed from whatever the store happens to hold.
        // It used to take the first enabled atom; that made this fixture depend on ambient content, and
        // another test importing the real atom seed turned that pick into one the resolver could not
        // bind (`BattleRunState.BindContainers` throws when a held action's container resolves to
        // nothing). One `stat.modify` atom compiles to a real Def and binds, in any store.
        var atomId = AtomRow.DeriveId("atom." + ActionId.Replace('.', '-'), "", 1);
        Assert.True(store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom." + ActionId.Replace('.', '-'),
            Variant = "",
            Tier = 1,
            Name = "T74 holder rung",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":1}""",
        }).IsOk);

        var containerCheck = store.UpsertContainer(new ContainerRow
        {
            ContainerId = ActionId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(containerCheck.IsOk, containerCheck.ToString());

        var actionCheck = store.UpsertAction(new ActionRow
        {
            ActionId = ActionId,
            Name = "T74 holder rung",
            Kind = ActionKind.Skill,
            Rung = 1,
            Enabled = true,
            Grantable = true,
            ContainerId = ActionId,
            Envelope = ActionEnvelope.NoOp with { ActionId = ActionId },
            MinRange = 0,
            MaxRange = int.MaxValue,
            RequiresLineOfSight = false,
        });
        Assert.True(actionCheck.IsOk, actionCheck.ToString());

        var costCheck = store.UpsertCost(new ActionCostRow(ActionId, "qi", ValueSpec.Of(baseCost), ActionCostTiming.OnCommit));
        Assert.True(costCheck.IsOk, costCheck.ToString());
    }

    /// <summary>One squad holder that equips the costed action and one enemy weak enough that a single
    /// landed hit decides the fight. `HubInputs` stays null, so the holder's pool is the plain
    /// level-derived baseline `BaseResourceMax` reads.</summary>
    static BattleSetup Setup(string instanceId) => new()
    {
        WaveId = "t74-holder-rung",
        Squad = new[]
        {
            new BattleActorSetup
            {
                Key = "squad:0", Side = "squad", SpeciesId = "peashooterzombie", Level = Level,
                MaxHp = BattleRuleset.BaseHp(Level), Atk = BattleRuleset.BaseAtk(Level),
                Defense = BattleRuleset.BaseDefense(Level), SpecimenId = instanceId,
                EquippedActionIds = new[] { ActionId },
            },
        },
        Wave = new[]
        {
            new BattleActorSetup
            {
                Key = "wave:0", Side = "wave", SpeciesId = "normalzombie", Level = 1,
                MaxHp = 1, Atk = 1, Defense = 0,
            },
        },
    };
}
