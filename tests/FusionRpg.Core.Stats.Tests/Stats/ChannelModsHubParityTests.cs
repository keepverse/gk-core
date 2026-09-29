using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Items.Consumables;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// channelmods-hub T2 parity — UniqueCreature aptitude / Zomboss pattern / draught / expedition
/// injury / boss kit moved out of private <c>BattleChannelMod</c> arithmetic into Hub contributions
/// sharing one Core read path. Species <c>AptitudeChannelMods</c> has zero production callers
/// (proven-unused, DEBT-tagged) and is pinned by its own tests, not here.
/// </summary>
public class ChannelModsHubParityTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    // Same direct-file discipline as BossBuildTests: AptitudeTuningHub is NOT read (per-test-class
    // race); the tuning file is parsed inline. PowerTuningHub IS whole-assembly configured.
    static readonly AptitudeTuning AptitudeTuning =
        AptitudeTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitudes.v2.json")));
    static readonly PowerTuning PowerTuning = PowerTuningHub.Tuning;
    static DerivedStatRegistry Registry() => DerivedStatRegistry.CreateDefault();

    // ExpeditionTuningHub stays on the assembly bootstrap's ambient values (same working set
    // as the shipped file): reconfiguring shared hubs from files here once flapped unrelated
    // tests by swapping their tuning mid-run. Only the divisor is read below — identical either way.

    static Dictionary<string, long> SumBattle(IEnumerable<BattleChannelMod> mods) =>
        mods.GroupBy(m => m.ChannelId)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Amount), StringComparer.Ordinal);

    // Hub Resolve keeps Contest reads fractional; the battle twin narrows half-away-from-zero.
    // The ONLY permitted difference per channel is that narrowing.
    static Dictionary<string, long> SumHub(IEnumerable<DerivedModifier> mods) =>
        mods.GroupBy(m => m.ChannelId)
            .ToDictionary(g => g.Key,
                g => g.Sum(m => checked((long)Math.Round(m.Value, MidpointRounding.AwayFromZero))),
                StringComparer.Ordinal);

    static AptitudeAllocation FundedAllocation()
    {
        var ids = AptitudeCatalog.All.Take(3).Select(r => r.Id).ToArray();
        var alloc = AptitudeAllocation.Empty;
        foreach (var id in ids)
            alloc += AptitudeAllocation.Single(AllocationScope.Commander, id, 100);
        alloc += AptitudeAllocation.Single(AllocationScope.UniqueCreature, ids[0], 50);
        return alloc;
    }

    static AptitudeAllocation PatternAllocation(string patternId, long theta)
    {
        var commander = new ZombossCommanderAllocation(patternId);
        commander.Refresh(AllocationScope.Commander, theta, AptitudeTuning);
        return commander.Resolve(null!);
    }

    [Fact]
    public void AptitudeResolve_matches_retired_battle_twin_per_channel()
    {
        // battle-hub-fuse T6: ResolveForBattle is deleted; this literal was captured from its
        // output pre-delete (the passing parity run proved Hub == battle ± narrowing), so Hub
        // drift still fails loudly here. Capture run: see evidence map T2/T6.
        const int theta = 100;
        var allocation = FundedAllocation();
        var ladder = new PowerLadder(PowerTuning);

        var hub = SumHub(AptitudeResolver.Resolve(allocation, AptitudeTuning, ladder, theta, Registry()));

        Assert.Equal(CapturedAptitudeHub, hub);
    }

    // Captured battle-hub-fuse T6 (immediately after ResolveForBattle's deletion) — Hub's own output
    // for FundedAllocation() at theta=100. RE-BLESSED species-progression step 6.1 (R2/R16/R21,
    // 2026-09-19): FundedAllocation funds a Commander share AND a UniqueCreature share on the SAME
    // aptitude id (ids[0]) -- exactly the two-non-empty-scope shape step 6.1's own resolver rewrite
    // moves. Before 6.1, AptitudeResolver.Resolve took ONE merged share per aptitude
    // (Total(id)/GrandTotal, ids[0] = 150/350 = 0.4286); since 6.1 each scope resolves ALONE, its share
    // taken over that scope's own total (AptitudeAllocation.ShareWithinScope) -- Commander's own three
    // ids each read share=100/300=0.333, and UniqueCreature's ids[0] separately reads share=50/50=1.0,
    // concatenated into two modifiers on that aptitude's channels instead of one merged one. This is
    // the documented consequence, not a regression: an actor with two or more non-empty scopes is
    // never byte-identical after 6.1 (spec-species-layer-delivery.md's own re-bless classification).
    // The single-scope-only ChannelModsHubParityTests captures below (KitResolve/ZombossPatternResolve,
    // both Commander-only via ZombossCommanderAllocation) stay byte-identical, matching that same rule.
    static readonly Dictionary<string, long> CapturedAptitudeHub = new(StringComparer.Ordinal)
    {
        ["combat.absorption.omni"] = 6, ["combat.accuracy.omni"] = 187, ["combat.crit.damage.omni"] = 187,
        ["combat.crit.rate.omni"] = 187, ["combat.crit.resist.damage.omni"] = 23, ["combat.crit.resist.omni"] = 23,
        ["combat.defense.omni"] = 1403, ["combat.power.omni"] = 13725, ["combat.reduction.omni"] = 1,
        ["combat.shield.capacity.omni"] = 85714, ["combat.shield.regen.omni"] = 525, ["combat.shield.toughness.omni"] = 3117,
        ["progression.bonus.arm2"] = 12468, ["progression.bonus.defense"] = 15584, ["progression.bonus.maxHp"] = 31169,
        ["resource.max.hp"] = 121623, ["resource.max.hunger"] = 90431, ["resource.max.qi"] = 56133,
        ["resource.max.spirit"] = 28065, ["resource.max.stamina"] = 165279, ["resource.regen.hp"] = 759,
        ["resource.regen.hunger"] = 1049, ["resource.regen.qi"] = 1404, ["resource.regen.spirit"] = 702,
        ["resource.regen.stamina"] = 3013, ["skill.effectiveness.attack"] = 107, ["status.duration.cc"] = 79,
        ["status.duration.contagion"] = 79, ["status.duration.dot"] = 79, ["status.duration.omni"] = 41,
        ["status.durationReduction.cc"] = 79, ["status.durationReduction.contagion"] = 187, ["status.durationReduction.dot"] = 113,
        ["status.durationReduction.omni"] = 57, ["status.intensity.cc"] = 159, ["status.intensity.contagion"] = 79,
        ["status.intensity.dot"] = 186, ["status.intensity.omni"] = 63, ["status.intensityReduction.cc"] = 79,
        ["status.intensityReduction.contagion"] = 180, ["status.intensityReduction.dot"] = 153, ["status.intensityReduction.omni"] = 63,
        ["status.power.cc"] = 60, ["status.power.contagion"] = 60, ["status.power.dot"] = 100,
        ["status.power.omni"] = 41, ["status.resist.cc"] = 60, ["status.resist.contagion"] = 113,
        ["status.resist.dot"] = 123, ["status.resist.omni"] = 53,
    };

    [Fact]
    public void KitResolve_matches_retired_ResolveKit_per_channel()
    {
        // Captured pre-delete like above: BossBuild.ResolveKit("force-pure", 127) output.
        const int thetaBoss = 127;
        var allocation = PatternAllocation("force-pure", thetaBoss);
        var ladder = new PowerLadder(PowerTuning);

        var hub = SumHub(AptitudeResolver.Resolve(allocation, AptitudeTuning, ladder, thetaBoss, Registry()));

        Assert.Equal(CapturedKitHub, hub);
    }

    // Captured this session (battle-hub-fuse T6) — Hub's own output for the "force-pure" pattern
    // allocation at thetaBoss=127, pinned as the literal.
    static readonly Dictionary<string, long> CapturedKitHub = new(StringComparer.Ordinal)
    {
        ["combat.power.omni"] = 6566, ["combat.accuracy.omni"] = 76, ["combat.crit.rate.omni"] = 55,
        ["combat.crit.damage.omni"] = 55, ["combat.penetration.omni"] = 5, ["combat.amplification.omni"] = 1,
        ["combat.parry.break.omni"] = 14, ["combat.parry.shred.omni"] = 364, ["combat.block.break.omni"] = 14,
        ["combat.block.shred.omni"] = 364, ["combat.reflect.rate.omni"] = 7, ["combat.reflect.damage.omni"] = 54,
        ["progression.bonus.maxHp"] = 11894, ["progression.bonus.arm2"] = 7930,
        ["skill.effectiveness.attack"] = 32, ["resource.max.hp"] = 78463, ["resource.regen.hp"] = 1032,
        ["resource.max.stamina"] = 102939, ["resource.regen.stamina"] = 1837, ["resource.max.qi"] = 39648,
        ["resource.regen.qi"] = 992, ["resource.max.hunger"] = 92214, ["resource.regen.hunger"] = 1633,
        ["resource.max.spirit"] = 19824, ["resource.regen.spirit"] = 495, ["status.power.omni"] = 22,
        ["status.power.dot"] = 52, ["status.power.cc"] = 31, ["status.power.contagion"] = 67,
        ["status.resist.omni"] = 23, ["status.resist.dot"] = 40, ["status.resist.cc"] = 31,
        ["status.resist.contagion"] = 64, ["status.duration.omni"] = 38, ["status.duration.dot"] = 100,
        ["status.duration.cc"] = 40, ["status.duration.contagion"] = 100, ["status.durationReduction.omni"] = 27,
        ["status.durationReduction.dot"] = 55, ["status.durationReduction.cc"] = 40, ["status.durationReduction.contagion"] = 73,
        ["status.intensity.omni"] = 31, ["status.intensity.dot"] = 71, ["status.intensity.cc"] = 64,
        ["status.intensity.contagion"] = 105, ["status.intensityReduction.omni"] = 21, ["status.intensityReduction.dot"] = 40,
        ["status.intensityReduction.cc"] = 40, ["status.intensityReduction.contagion"] = 55, ["combat.absorption.omni"] = 1,
        ["combat.reduction.omni"] = 3, ["combat.crit.resist.omni"] = 11, ["combat.crit.resist.damage.omni"] = 11,
        ["combat.shield.toughness.omni"] = 1982, ["combat.shield.capacity.omni"] = 54516, ["combat.shield.regen.omni"] = 334,
    };

    [Fact]
    public void ZombossPatternResolve_matches_retired_Concat_per_channel()
    {
        // Captured pre-delete like above: the ResolveForBattle output ApplyZombossPattern concated.
        const int level = 42;
        var allocation = PatternAllocation("bastion-pure", level);
        var ladder = new PowerLadder(PowerTuning);

        var hub = SumHub(AptitudeResolver.Resolve(allocation, AptitudeTuning, ladder, level, Registry()));

        Assert.Equal(CapturedZombossHub, hub);
    }

    // Captured this session (battle-hub-fuse T6) — Hub's own output for the "bastion-pure" pattern
    // allocation at level=42, pinned as the literal.
    static readonly Dictionary<string, long> CapturedZombossHub = new(StringComparer.Ordinal)
    {
        ["combat.power.omni"] = 983, ["combat.accuracy.omni"] = 75, ["combat.crit.rate.omni"] = 121,
        ["combat.crit.damage.omni"] = 121, ["combat.parry.rate.omni"] = 21, ["combat.parry.strength.omni"] = 121,
        ["combat.block.rate.omni"] = 21, ["combat.block.strength.omni"] = 121, ["progression.bonus.maxHp"] = 2062,
        ["progression.bonus.defense"] = 4197, ["progression.bonus.arm1"] = 2159,
        ["skill.cooldown.defense"] = 27, ["skill.effectiveness.attack"] = 38, ["skill.effectiveness.defense"] = 21,
        ["skill.effectiveness.support"] = 25, ["skill.effectiveness.status"] = 25, ["combat.heal.power"] = 2212,
        ["resource.max.hp"] = 17190, ["resource.regen.hp"] = 76, ["resource.max.stamina"] = 13808,
        ["resource.regen.stamina"] = 325, ["resource.max.qi"] = 13272, ["resource.regen.qi"] = 299,
        ["resource.max.hunger"] = 12415, ["resource.regen.hunger"] = 222, ["resource.max.spirit"] = 7939,
        ["resource.regen.spirit"] = 114, ["status.power.omni"] = 23, ["status.power.dot"] = 78,
        ["status.power.cc"] = 57, ["status.power.contagion"] = 37, ["status.resist.omni"] = 23,
        ["status.resist.dot"] = 52, ["status.resist.cc"] = 36, ["status.resist.contagion"] = 37,
        ["status.duration.omni"] = 25, ["status.duration.dot"] = 72, ["status.duration.cc"] = 90,
        ["status.duration.contagion"] = 54, ["status.durationReduction.omni"] = 21, ["status.durationReduction.dot"] = 40,
        ["status.durationReduction.cc"] = 40, ["status.durationReduction.contagion"] = 57, ["status.intensity.omni"] = 31,
        ["status.intensity.dot"] = 121, ["status.intensity.cc"] = 80, ["status.intensity.contagion"] = 40,
        ["status.intensityReduction.omni"] = 38, ["status.intensityReduction.dot"] = 95, ["status.intensityReduction.cc"] = 76,
        ["status.intensityReduction.contagion"] = 74, ["combat.defense.omni"] = 305, ["combat.absorption.omni"] = 6,
        ["combat.reduction.omni"] = 0, ["combat.shield.toughness.omni"] = 162, ["combat.shield.capacity.omni"] = 2429,
    };

    [Fact]
    public void DraughtTwin_maps_manifest_one_to_one_with_attributed_source_ids()
    {
        // battle-hub-fuse T6: the BattleChannelMod Apply is deleted; the twin's 1:1 mapping is
        // pinned literally (per-member fan-out lives in DraughtSubsystem, pinned separately).
        var manifest = new[]
        {
            new DraughtMod("draught:ember-flask", DerivedStatChannels.CombatPowerOmni, 25),
            new DraughtMod("draught:stone-tonic", DerivedStatChannels.CombatDefenseOmni, 10),
        };

        var twin = DraughtProjection.ToDerivedModifiers(manifest);

        Assert.Equal(2, twin.Count);
        Assert.Equal(DerivedStatChannels.CombatPowerOmni, twin[0].ChannelId);
        Assert.Equal(25L, checked((long)twin[0].Value));
        Assert.Equal("grant:draught:draught:ember-flask", twin[0].SourceId);
        Assert.Equal(DerivedStatChannels.CombatDefenseOmni, twin[1].ChannelId);
        Assert.Equal(10L, checked((long)twin[1].Value));
        Assert.Equal("grant:draught:draught:stone-tonic", twin[1].SourceId);
    }

    [Fact]
    public void InjuryTwin_matches_historical_expression_per_channel()
    {
        // ApplyInjuries is private — pin the twin against the historical expression copied from its
        // body (T1-test-1 style): count × -max(1, atk/divisor) on combat.power.omni.
        const string key = "squad:0";
        const int count = 2;
        const long atk = 400;
        var divisor = ExpeditionTuningHub.Tuning.EventRoll.InjuryPowerDivisor;

        var expected = count * -Math.Max(1, atk / divisor);
        var twin = ExpeditionResolver.InjuryDerivedModifiers(key, count, atk);

        Assert.Equal(expected, twin.Sum(m => checked((long)m.Value)));
        Assert.All(twin, m => Assert.Equal(DerivedStatChannels.CombatPowerOmni, m.ChannelId));
        Assert.All(twin, m => Assert.Equal($"grant:injury:{key}", m.SourceId));
    }

    [Fact]
    public void DraughtSubsystem_contributes_through_hub_with_attributed_source_ids()
    {
        var manifest = new[]
        {
            new DraughtMod("draught:ember-flask", DerivedStatChannels.CombatPowerOmni, 25),
        };
        var hub = ActorHubBootstrap.CreateDefault(draughts: _ => manifest);
        var (snapshot, bag) = hub.ResolveDerivedWithContributions(
            new StatContextFactory().ForPlant("squad:0", new EntityBaseline { Atk = 400 }));

        Assert.Equal(25.0, snapshot.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
        var sources = bag.ContributionsFor(DerivedStatChannels.CombatPowerOmni).Select(c => c.SourceId).ToList();
        Assert.Contains("grant:draught:draught:ember-flask", sources);
    }

    [Fact]
    public void InjurySubsystem_contributes_through_hub_with_attributed_source_ids()
    {
        const long atk = 400;
        var divisor = ExpeditionTuningHub.Tuning.EventRoll.InjuryPowerDivisor;
        var injuries = new Dictionary<string, int>(StringComparer.Ordinal) { ["squad:0"] = 2 };

        var hub = ActorHubBootstrap.CreateDefault(expeditionInjuries: _ => injuries);
        var (snapshot, bag) = hub.ResolveDerivedWithContributions(
            new StatContextFactory().ForPlant("squad:0", new EntityBaseline { Atk = atk }));

        Assert.Equal(2 * -Math.Max(1, atk / divisor), snapshot.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
        var sources = bag.ContributionsFor(DerivedStatChannels.CombatPowerOmni).Select(c => c.SourceId).ToList();
        Assert.Contains("grant:injury:squad:0", sources);
    }
}
