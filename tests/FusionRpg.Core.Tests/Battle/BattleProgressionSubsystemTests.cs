using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// W17 (battle-derived-wire T15, `battle-wire-remainder` BWR1.6): battle consumes the ActorHub
/// `AppliedCombat` merge, so a specimen's aptitude points in an aptitude with an edge into
/// `progression.bonus.*` now reach the battle. `Vigor`/`Fortitude` edge into
/// `progression.bonus.maxHp`; `Fortitude`/`Bulwark` edge into `progression.bonus.defense`.
///
/// <para>Before this, `BattleHubCompose` called `hub.ResolveDerived(ctx)` and the merge never ran, so
/// those points composed a channel battle never read. Goldens do NOT move: every golden fixture is
/// bare (no `HubInputs.Aptitude`), so the bonus is 0 there.</para>
/// </summary>
public class BattleProgressionSubsystemTests
{
    /// <summary>Loads the real shipped aptitude config rather than depending on another test having
    /// configured the hub first (the test-ordering accident `ModeComposeParityTests` documents).</summary>
    static BattleProgressionSubsystemTests() => ConfigureShippedAptitudeTuning();

    static void ConfigureShippedAptitudeTuning()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var tuningDir = Path.Combine(dir.FullName, "data", "tuning");
            if (Directory.Exists(tuningDir))
            {
                var newest = Directory.GetFiles(tuningDir, "aptitudes.v*.json")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (newest is not null)
                {
                    AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(newest)));
                    return;
                }
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no data/tuning/aptitudes.v*.json above the test output");
    }

    static BattleActorSetup Actor(string key, string side, int level = 5,
        AptitudeAllocation? aptitude = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
        HubInputs = aptitude is null ? null : new BattleHubInputs { Aptitude = aptitude },
    };

    static BattleSetup Setup(AptitudeAllocation? aptitude) => new()
    {
        WaveId = "progression",
        Squad = new[] { Actor("squad:0", "squad", aptitude: aptitude) },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    /// <summary>`Fortitude` carries both edges: `progression.bonus.maxHp` (k=8000) and
    /// `progression.bonus.defense` (k=10000) — read from `gk-core/data/tuning/aptitudes.v10.json`.</summary>
    static readonly AptitudeAllocation Fortitude =
        AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Fortitude", 7);

    readonly ITestOutputHelper _out;

    public BattleProgressionSubsystemTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Progression_power_composes_from_battles_own_theta_across_levels()
    {
        // W5/W17's own reading: `progression.power` is the term ResistanceEvaluator's status contest
        // reads, and battle composes it from its own Theta through FixedPowerIndexProvider — one
        // ladder, never a second curve. Printed so the audit delta report carries a reading, not a
        // claim; the assertion is structural (non-decreasing), never a pinned magnitude.
        var parts = new List<string>();
        var previous = -1.0;
        foreach (var level in new[] { 1, 5, 10, 20 })
        {
            var value = BattleHubCompose.Compose(Actor("squad:0", "squad", level))
                .Get(DerivedStatChannels.ProgressionPower);
            parts.Add($"L{level}={value}");
            Assert.True(value >= previous,
                $"progression.power must not fall as Theta rises: L{level} gave {value}, previous {previous}");
            previous = value;
        }
        _out.WriteLine("reading: progression.power " + string.Join(" ", parts));
    }

    [Fact]
    public void No_allocation_leaves_the_pool_and_defense_at_their_setup_bases()
    {
        var maxHp = BattleEngine.MaxHpForTest(Setup(null), 1, "squad:0");
        var defense = BattleEngine.ComposedDefenseForTest(Setup(null), 1, "squad:0");
        _out.WriteLine($"reading: no-allocation maxHp={maxHp} defense={defense} (base {BattleRuleset.BaseHp(5)}/{BattleRuleset.BaseDefense(5)})");

        Assert.Equal(BattleRuleset.BaseHp(5), maxHp);
        Assert.Equal(BattleRuleset.BaseDefense(5), defense);
    }

    [Fact]
    public void A_progression_bonus_maxHp_allocation_raises_the_battle_pool()
    {
        var with = BattleEngine.MaxHpForTest(Setup(Fortitude), 1, "squad:0");
        _out.WriteLine($"reading: Fortitude(7) maxHp={with} base={BattleRuleset.BaseHp(5)} delta={with - BattleRuleset.BaseHp(5)}");

        Assert.True(with > BattleRuleset.BaseHp(5),
            $"expected the Fortitude maxHp bonus to raise the pool above {BattleRuleset.BaseHp(5)}, got {with}");
    }

    [Fact]
    public void A_progression_bonus_defense_allocation_reaches_the_composed_defense()
    {
        var with = BattleEngine.ComposedDefenseForTest(Setup(Fortitude), 1, "squad:0");
        _out.WriteLine($"reading: Fortitude(7) defense={with} base={BattleRuleset.BaseDefense(5)} delta={with - BattleRuleset.BaseDefense(5)}");

        Assert.True(with > BattleRuleset.BaseDefense(5),
            $"expected the Fortitude defense bonus to raise the composed defense above {BattleRuleset.BaseDefense(5)}, got {with}");
    }
}
