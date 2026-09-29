using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Delve.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.World;
using FusionRpg.Core.World.District;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// T74/A33 (`spec-battle-holder-wiring.md` §2c) — the Core half of the wiring. `BattleEngine.Resolve`
/// already prices a held action at its holder's rung; these two seams are how a Core-only resolver,
/// which may never read a store (`guard-dal.ps1`), is handed the pair by the layer that has one.
///
/// <para>The pricing consequence itself is proven against the real engine by
/// <c>ActionCostsCooldownsAdoptionTests</c> (low rung vs high rung, same setup, different costs) and
/// end-to-end by the E2E real-host observable. What is proven here is the part those cannot see:
/// the parameter actually THREADS through each wrapper, and the siege hop asks only for real holders.</para>
/// </summary>
public class BattleHolderWiringSeamTests
{
    /// <summary>A real action whose authored `Rung` is 1 but whose paid cost scales with the holder's
    /// effective rung — the exact fixture `ActionCostsCooldownsAdoptionTests` uses. 40 `qi` at rung 1 is
    /// affordable for `CloseSetup()`'s own squad:0; at rung 5 the shipped `action-rungs.v2.json`
    /// multiplier makes it 145, above that actor's real `qi` max, so a rung-5 holder never fires it.</summary>
    static CompiledAction RungScaledSkill(string actionId, int baseCost) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: new[] { new CompiledActionCost("qi", ValueSpec.Of(baseCost), ActionCostTiming.OnCommit) },
        Scopes: Array.Empty<ActionScopeRow>());

    static readonly UnlockTuning RungTestTuning =
        new(P1Milli: 1000, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);

    static UnlockState StateAt(string actionId, int earnCount) =>
        UnlockState.FromPersisted(earnCount, new[] { new HeldUnlock(actionId, earnCount) });

    /// <summary>`DelveBattle.Run` is a thin, profile-pinning wrapper — but it is the wrapper every real
    /// delve fight goes through, so a pair the wrapper dropped would be a pair production never got.
    /// Same-actor/same-seed/same-setup across the two runs; the only variable is the unlock state.</summary>
    [Fact]
    public void DelveBattle_run_threads_the_unlock_pair_to_the_pricing_engine()
    {
        const string actionId = "skill.rung-scaled";
        var catalog = ActionCatalog.Build(new[] { RungScaledSkill(actionId, baseCost: 40) });
        var setup = BattleGoldenTests.CloseSetup() with
        {
            Squad = BattleGoldenTests.CloseSetup().Squad.Select((a, i) => i == 0
                ? a with { EquippedActionIds = new[] { actionId } }
                : a).ToArray(),
        };

        var traceLow = new BattleTrace();
        DelveBattle.Run(setup, seed: 5501, trace: traceLow, actionCatalog: catalog,
            unlockStateFor: key => key == "squad:0" ? StateAt(actionId, 1) : UnlockState.Empty(),
            unlockTuning: RungTestTuning);

        var traceHigh = new BattleTrace();
        DelveBattle.Run(setup, seed: 5501, trace: traceHigh, actionCatalog: catalog,
            unlockStateFor: key => key == "squad:0" ? StateAt(actionId, 5) : UnlockState.Empty(),
            unlockTuning: RungTestTuning);

        Assert.Contains(traceLow.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
        Assert.DoesNotContain(traceHigh.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
    }

    /// <summary>With no provider at all (every pre-T74 Core caller) the wrapper must stay on the
    /// authored-rung path — the same run as the low-rung one above, reached through the default.</summary>
    [Fact]
    public void DelveBattle_run_with_no_pair_keeps_the_authored_rung_fallback()
    {
        const string actionId = "skill.rung-scaled";
        var catalog = ActionCatalog.Build(new[] { RungScaledSkill(actionId, baseCost: 40) });
        var setup = BattleGoldenTests.CloseSetup() with
        {
            Squad = BattleGoldenTests.CloseSetup().Squad.Select((a, i) => i == 0
                ? a with { EquippedActionIds = new[] { actionId } }
                : a).ToArray(),
        };

        var trace = new BattleTrace();
        DelveBattle.Run(setup, seed: 5501, trace: trace, actionCatalog: catalog);

        Assert.Contains(trace.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
    }

    static WorldEntity Legion(string id, string owner, params (string Species, int Level, long Hp, string? Instance)[] members) => new()
    {
        EntityId = id,
        Kind = WorldEntityKind.Warband,
        OwnerFactionId = owner,
        AtSectorId = "s1",
        Members = members.Select(m => new WorldEntityMember
        {
            SpeciesId = m.Species, Level = m.Level, Hp = m.Hp, InstanceId = m.Instance,
        }).ToList(),
    };

    static BoardProjection Board() => new()
    {
        SectorId = "s1",
        WorldSeed = 42,
        SectorTypeId = "home",
        DevelopmentLevel = 0,
        AttackerEdge = FusionRpg.Core.World.District.BoardEdge.North,
        Slots = Array.Empty<SlotProjection>(),
    };

    /// <summary>
    /// The siege resolver's own hop: `BattleEngine.Resolve` asks by ACTOR KEY, the injected provider
    /// answers by `InstanceId`, and this is the one mapping between them. A real fight runs, so the
    /// engine really asks — and every ask must name a squad member's own specimen. The defender's
    /// member carries no `InstanceId` (a non-player force), so it must never reach the provider at all:
    /// asking for it would be asking someone else's ladder to price a stranger.
    /// </summary>
    [Fact]
    public void The_siege_resolver_asks_its_provider_only_for_real_holders_instance_ids()
    {
        var asked = new List<string>();
        var resolver = new DistrictAssaultResolver
        {
            UnlockStateFor = instanceId => { asked.Add(instanceId); return UnlockState.Empty(); },
            UnlockTuning = RungTestTuning,
        };

        var attacker = Legion("e-a", "player",
            ("peashooterzombie", 3, 300, "spec.a"), ("conezombie", 3, 300, "spec.b"));
        var defender = Legion("e-d", "zomboss", ("normalzombie", 3, 300, null));
        var request = new BattleRequest
        {
            BattleId = "b1", Kind = BattleKinds.District, LocationId = "s1",
            AttackerEntityId = attacker.EntityId, DefenderEntityId = defender.EntityId,
            DefenderStationary = true, Board = Board(),
        };

        resolver.Resolve(request, new[] { attacker, defender }, seed: 123);

        Assert.NotEmpty(asked); // the engine really evaluates affordability, so the seam is really reached
        Assert.Contains("spec.a", asked);
        Assert.Contains("spec.b", asked);
        Assert.All(asked, id => Assert.Contains(id, new[] { "spec.a", "spec.b" }));
    }

    /// <summary>The null-provider default: no read at all, so the resolver behaves exactly as it did
    /// before this seam existed. Asserted by completing a real fight with no provider and getting the
    /// same shape a provider-less siege always produced.</summary>
    [Fact]
    public void The_siege_resolver_with_no_provider_still_resolves_a_real_fight()
    {
        var attacker = Legion("e-a", "player", ("peashooterzombie", 3, 300, "spec.a"));
        var defender = Legion("e-d", "zomboss", ("normalzombie", 3, 300, null));
        var request = new BattleRequest
        {
            BattleId = "b1", Kind = BattleKinds.District, LocationId = "s1",
            AttackerEntityId = attacker.EntityId, DefenderEntityId = defender.EntityId,
            DefenderStationary = true, Board = Board(),
        };

        var outcome = new DistrictAssaultResolver().Resolve(request, new[] { attacker, defender }, seed: 123);

        Assert.Equal(2, outcome.Sides.Count);
    }
}
