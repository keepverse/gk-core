using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// standing-compose (T9) — <c>ProjectStanding</c> must price every Hub combat writer
/// <see cref="CombatPowerMembership"/> includes (aptitude, star/loyalty, ...), not equip+tree atoms
/// alone (the former HF-standing gap). Bootstrap mirrors <see cref="EquippedHubParityTests"/>'s own
/// established in-memory summon fixture.
/// </summary>
public class ProjectStandingTests : IDisposable
{
    readonly RpgStore _store;
    readonly WebMatchService _service;
    static readonly PowerTuning MintTuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static ProjectStandingTests()
    {
        UnlockTuningPolicy.Configure(new UnlockTuning(
            P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));
        ActionFamilyMapPolicy.Configure(new Dictionary<string, IReadOnlyList<string>>());
    }

    public ProjectStandingTests()
    {
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        PowerTuningHub.Configure(PowerTuningLoader.Parse(Read("power-scale.v2.json")));
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(LatestAptitudesText(tuningDir)));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(Read("fusion.v2.json")));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(Read("progression.v3.json")));
        FusionRpg.Core.Battle.BattleTuningHub.Configure(
            FusionRpg.Core.Battle.BattleTuningLoader.Parse(Read("battle.v5.json")));
        FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
            FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(Read("battle-resources.v1.json")));
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(
            FusionRpg.Core.Actions.ActionTimingTuningLoader.Parse(Read("action-timing.v1.json")));
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(Read("stats.v1.json")));

        _store = RpgStore.InMemory();
        _store.Init();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        _service = new WebMatchService(_store, hub);
    }

    static string LatestAptitudesText(string tuningDir)
    {
        var file = Directory.GetFiles(tuningDir, "aptitudes.v*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .First();
        return File.ReadAllText(file);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    public void Dispose() => _store.Dispose();

    (long PlayerId, string InstanceId) SummonOneSpecimen(string correlationSuffix, ulong rngSeed)
    {
        var playerId = _store.CreatePlayer("standing-test-" + correlationSuffix).Id;
        _store.AwardSouls(playerId, 10_000, SoulEarnPolicy.Reasons.Seed, "test-bankroll");
        var (ok, reason, outcome) = _store.ExecuteSummon(
            playerId, SummonBannerCatalog.StandardRift, 1, "c-standing-" + correlationSuffix, rngSeed, focusElementId: null);
        Assert.True(ok, reason);
        return (playerId, Assert.Single(outcome!.Specimens).Profile.InstanceId);
    }

    [Fact]
    public void Aptitude_grant_on_a_combat_channel_raises_Standing()
    {
        var (playerId, instanceId) = SummonOneSpecimen("aptitude", rngSeed: 1);
        var bare = UniqueActorHubCompose.ProjectSheet(_store, _store.GetUniqueActor(instanceId)!);

        // A funded Commander allocation reaches Standing as a residual synthetic (aptitude has no
        // real equip/tree AtomRow of its own) -- this is the whole HF-standing gap T9 closes.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(playerId), allocation);
        var geared = UniqueActorHubCompose.ProjectSheet(_store, _store.GetUniqueActor(instanceId)!);

        // Might funds combat.power.omni/accuracy/crit -- Offense (and likely Control) must move;
        // nothing about a pure Might grant should raise Utility/Economy.
        Assert.True(geared.Standing.Offense > bare.Standing.Offense,
            $"expected Offense to rise: bare={bare.Standing.Offense} geared={geared.Standing.Offense}");
        Assert.Equal(bare.Standing.Utility, geared.Standing.Utility);
        Assert.Equal(bare.Standing.Economy, geared.Standing.Economy);
    }

    [Fact]
    public void Level_alone_theta_progression_does_not_raise_Standing()
    {
        // No gear, no aptitude -- if Standing ever priced progression.power (Theta) or any other
        // level-scaled base, a real (nonzero-level) specimen would already show it here. It must not:
        // Standing prices only what CombatPowerMembership includes, and progression.* is excluded.
        var (_, instanceId) = SummonOneSpecimen("theta", rngSeed: 2);
        var actor = _store.GetUniqueActor(instanceId)!;
        Assert.True(actor.Level >= 1, "a real summon must produce a real, nonzero level");

        var sheet = UniqueActorHubCompose.ProjectSheet(_store, actor);

        Assert.Equal(0.0, sheet.Standing.Offense);
        Assert.Equal(0.0, sheet.Standing.Survivability);
        Assert.Equal(0.0, sheet.Standing.Control);
        Assert.Equal(0.0, sheet.Standing.Utility);
        Assert.Equal(0.0, sheet.Standing.Economy);
    }

    [Fact]
    public void An_aptitude_edge_targeting_a_cooldown_channel_raises_Standing()
    {
        // skill.cooldown.* is combat-support membership (Q5), not element-typed -- if Standing only
        // priced AllCombatChannelIds this would stay flat even though the aptitude grant is real.
        var tuning = AptitudeTuningHub.Tuning;
        var cooldownEdge = tuning.Edges.FirstOrDefault(e =>
            e.Channel.StartsWith(DerivedStatChannels.SkillCooldownPrefix, StringComparison.Ordinal)
            || e.Channel.StartsWith(DerivedStatChannels.SkillEffectivenessPrefix, StringComparison.Ordinal));
        Assert.True(cooldownEdge is not null,
            "shipped aptitudes.v*.json must fund at least one skill.cooldown/effectiveness edge for this test to mean anything");

        var (playerId, instanceId) = SummonOneSpecimen("cooldown", rngSeed: 4);
        var bare = UniqueActorHubCompose.ProjectSheet(_store, _store.GetUniqueActor(instanceId)!);

        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, cooldownEdge!.Source, 100);
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(playerId), allocation);
        var geared = UniqueActorHubCompose.ProjectSheet(_store, _store.GetUniqueActor(instanceId)!);

        var moved = geared.Standing.Offense != bare.Standing.Offense
            || geared.Standing.Survivability != bare.Standing.Survivability
            || geared.Standing.Control != bare.Standing.Control;
        Assert.True(moved, "a funded cooldown/effectiveness aptitude edge must move some Standing axis");
    }

    void UpsertRolledContainer(string containerId, string atomFamily, string channel, long amount)
    {
        var atomId = AtomRow.DeriveId(atomFamily, "", 1);
        var upsertAtom = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.derived",
            FamilyId = atomFamily, Variant = "", Tier = 1, Name = atomFamily,
            ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"flat\",\"amount\":{amount}}}",
        });
        Assert.True(upsertAtom.IsOk, upsertAtom.ToString());
        var upsertContainer = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, atomId) },
        });
        Assert.True(upsertContainer.IsOk, upsertContainer.ToString());
    }

    string EquipRolled(string specimenId, long playerId, string containerId, ulong rngSeed)
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, MintTuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        var itemInstanceId = _store.SaveInstance(inst!);
        _store.SaveItem(new RpgItemRow { InstanceId = itemInstanceId, PlayerId = playerId.ToString(), AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveAssignment(specimenId, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, itemInstanceId);
        return itemInstanceId;
    }

    [Fact]
    public void Equipped_gear_is_priced_exactly_once_in_Standing()
    {
        // The residual synthetics T9 adds skip any contribution whose SourceId is equip:-prefixed,
        // specifically to prevent this: without that skip, an equipped atom would count once as its
        // own real AtomRow (below) AND again as a "residual" Hub contribution, doubling its price.
        const long amount = 40;
        UpsertRolledContainer("item.standing-double-count", "atom.standing-double-count", DerivedStatChannels.CombatPowerFire, amount);

        var (playerId, specimenId) = SummonOneSpecimen("double-count", rngSeed: 5);
        EquipRolled(specimenId, playerId, "item.standing-double-count", rngSeed: 5);
        var (ok, reason, _, _) = _service.BuildSquad(playerId, new[] { specimenId });
        Assert.True(ok, reason);

        var bare = UniqueActorHubCompose.ProjectSheet(_store, _store.GetUniqueActor(specimenId)!);
        // Independently priced: exactly the one equip atom, nothing folded twice.
        var expected = ActorPowerCache.Compose(new[]
        {
            new AtomRow
            {
                AtomId = "standing-double-count-expected", KindId = "stat.derived",
                FamilyId = "standing-double-count-expected", Variant = "", Tier = 1, Name = "expected",
                ParamsJson = $"{{\"channel\":\"{DerivedStatChannels.CombatPowerFire}\",\"op\":\"flat\",\"amount\":{amount}}}",
            },
        });

        Assert.Equal(expected.Offense, bare.Standing.Offense);
    }
}
