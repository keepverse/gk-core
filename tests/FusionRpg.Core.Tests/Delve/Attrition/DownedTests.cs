using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Delve.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Attrition;

/// <summary>D2.21 (spec-delve-attrition.md §6) — the timeline FSM's `Downed` transition, wired into
/// `BattleEngine.Resolve`'s death-cleanup site behind `BattleModeProfile.DownedOnDeplete`. The todo's
/// own verify line, literally: a Downed test under the delve profile, and a no-Downed test under every
/// shipped profile.</summary>
public class DownedTests
{
    // A single, deliberately fragile party member (MaxHp 1) against a normal wave attacker --
    // any landed hit ends it, so the outcome is deterministic across a real fixed seed without
    // hand-computing the combat formula.
    static BattleSetup Setup() => new()
    {
        Squad = new List<BattleActorSetup>
        {
            new() { Key = "squad:0", Side = "squad", Level = 10, MaxHp = 1, Atk = 10, Defense = 0, AttackIntervalMs = 1000, PartyIndex = 0 },
        },
        Wave = new List<BattleActorSetup>
        {
            new() { Key = "wave:0", Side = "wave", Level = 10, MaxHp = 5000, Atk = 300, Defense = 0, AttackIntervalMs = 1000 },
        },
    };

    [Fact]
    public void Under_the_delve_profile_a_partyIndex_actor_at_zero_hp_goes_downed_not_dead()
    {
        var report = DelveBattle.Run(Setup(), seed: 12345);

        var member = Assert.Single(report.Actors, a => a.Key == "squad:0");
        Assert.True(member.HpRemaining <= 0, "the fixture must actually deplete the party member's hp for this test to prove anything");
        Assert.False(member.Survived); // Alive is still Hp > 0 -- Downed is a DIFFERENT axis, not a Survived override
        Assert.True(member.WentDowned); // the acceptance line's own "downedOnce is recorded" signal
    }


    [Fact]
    public void Under_every_shipped_profile_the_same_fixture_never_reports_wentDowned()
    {
        // Every shipped profile has DownedOnDeplete false (CheckpointG2Tests already pins this),
        // so the branch this task adds is unreachable for all of them -- proven here by actually
        // running the identical fixture through each and confirming WentDowned never flips on.
        AssertNeverDowned(BattleEngine.Resolve(Setup(), seed: 12345)); // classic-round, the implicit default
        AssertNeverDowned(BattleEngine.Resolve(Setup(), seed: 12345, profile: BattleModeProfileCatalog.ClassicRound));
        AssertNeverDowned(BattleEngine.Resolve(Setup(), seed: 12345, profile: BattleModeProfileCatalog.GalaxySync));
        AssertNeverDowned(BattleEngine.Resolve(Setup(), seed: 12345, profile: BattleModeProfileCatalog.HybridAtb));
        AssertNeverDowned(BattleEngine.Resolve(Setup(), seed: 12345, profile: BattleModeProfileCatalog.Siege));

        static void AssertNeverDowned(BattleReport report)
        {
            var member = Assert.Single(report.Actors, a => a.Key == "squad:0");
            Assert.True(member.HpRemaining <= 0, "the fixture must still deplete hp under every profile for this to prove anything");
            Assert.False(member.WentDowned);
        }
    }

    [Fact]
    public void A_wave_actor_never_goes_downed_even_under_the_delve_profile()
    {
        // DownedOnDeplete alone is not enough -- PartyIndex is required too (spec §6: "whose setup
        // carries PartyIndex"). A wave actor never carries one, so it keeps today's exact death path
        // even in a delve battle.
        var setup = Setup() with
        {
            Squad = new List<BattleActorSetup>
            {
                new() { Key = "squad:0", Side = "squad", Level = 10, MaxHp = 5000, Atk = 300, Defense = 0, AttackIntervalMs = 1000, PartyIndex = 0 },
            },
            Wave = new List<BattleActorSetup>
            {
                new() { Key = "wave:0", Side = "wave", Level = 10, MaxHp = 1, Atk = 10, Defense = 0, AttackIntervalMs = 1000 },
            },
        };

        var report = DelveBattle.Run(setup, seed: 12345);
        var wave = Assert.Single(report.Actors, a => a.Key == "wave:0");
        Assert.True(wave.HpRemaining <= 0, "the fixture must actually deplete the wave actor's hp for this test to prove anything");
        Assert.False(wave.WentDowned);
    }
}
