using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>D3.12 (spec-dungeon-loot.md §2, "Soul earn — two reads, no new curve"). `TuningAt`/`Pin`
/// mirror `SoulEarnPolicyTests.cs`'s own exact-value fixture pattern (`contentScale(20) == 1.000` for
/// any `B`) rather than the real shipped tuning, so every assertion below is byte-exact, not approximate.</summary>
public class DelveSoulLedgerTests
{
    static PowerTuning TuningAt(long bMilli) => PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    const int Pin = 20;
    static readonly PowerTuning Tuning = TuningAt(400);

    static BattleActorSetup Setup(int level) => new() { Key = "e", Side = "wave", SpeciesId = "x", TypeId = 1, Level = level };

    static BattleActorResult Result(bool survived, bool retreated) =>
        new(Key: "e", Side: "wave", SpeciesId: "x", TypeId: 1, HpRemaining: 0, DamageDealt: 0,
            Kills: 0, Survived: survived, Retreated: retreated, XpMilli: 1000);

    // ---- RoomKillEarn ----

    [Fact]
    public void RoomKillEarn_null_arguments_throw()
    {
        var enemies = new[] { (Setup(Pin), Result(false, false)) };
        Assert.Throws<ArgumentNullException>(() => DelveSoulLedger.RoomKillEarn(null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => DelveSoulLedger.RoomKillEarn(enemies, null!));
    }

    [Fact]
    public void An_empty_room_earns_nothing()
    {
        Assert.Equal(0, DelveSoulLedger.RoomKillEarn(Array.Empty<(BattleActorSetup, BattleActorResult)>(), Tuning));
    }

    [Fact]
    public void One_dead_non_retreated_enemy_earns_exactly_KillEarn()
    {
        var enemies = new[] { (Setup(Pin), Result(survived: false, retreated: false)) };
        var expected = SoulEarnPolicy.KillEarn(Pin, Tuning);
        Assert.Equal(expected, DelveSoulLedger.RoomKillEarn(enemies, Tuning));
    }

    [Fact]
    public void A_surviving_enemy_earns_nothing()
    {
        var enemies = new[] { (Setup(Pin), Result(survived: true, retreated: false)) };
        Assert.Equal(0, DelveSoulLedger.RoomKillEarn(enemies, Tuning));
    }

    [Fact]
    public void A_retreated_captured_enemy_earns_nothing_even_though_it_died()
    {
        // "one captured enemy pays nothing" -- Retreated true, Survived false: dead by the ledger's
        // own bookkeeping, but withdrawn, not killed for souls.
        var enemies = new[] { (Setup(Pin), Result(survived: false, retreated: true)) };
        Assert.Equal(0, DelveSoulLedger.RoomKillEarn(enemies, Tuning));
    }

    [Fact]
    public void Mixed_enemies_sum_only_the_dead_and_non_retreated_ones()
    {
        var enemies = new[]
        {
            (Setup(Pin), Result(survived: false, retreated: false)), // counts
            (Setup(Pin), Result(survived: true, retreated: false)),  // survived -- excluded
            (Setup(Pin), Result(survived: false, retreated: true)),  // captured -- excluded
            (Setup(Pin), Result(survived: false, retreated: false)), // counts
        };
        var expected = 2 * SoulEarnPolicy.KillEarn(Pin, Tuning);
        Assert.Equal(expected, DelveSoulLedger.RoomKillEarn(enemies, Tuning));
    }

    [Fact]
    public void Each_enemys_own_level_is_read_independently()
    {
        var enemies = new[]
        {
            (Setup(Pin), Result(false, false)),
            (Setup(Pin + 50), Result(false, false)),
        };
        var expected = SoulEarnPolicy.KillEarn(Pin, Tuning) + SoulEarnPolicy.KillEarn(Pin + 50, Tuning);
        Assert.Equal(expected, DelveSoulLedger.RoomKillEarn(enemies, Tuning));
    }

    // ---- AtExtraction ----

    [Fact]
    public void AtExtraction_null_tuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DelveSoulLedger.AtExtraction(0, Pin, true, null!));
    }

    [Fact]
    public void A_win_pays_the_victory_term_exactly()
    {
        var earn = DelveSoulLedger.AtExtraction(soulsUnbanked: 500, thetaRun: Pin, won: true, Tuning);
        Assert.Equal(500, earn.Kills);
        Assert.Equal(SoulEarnPolicy.MatchEndEarn(true, Pin, Tuning), earn.Victory);
        Assert.Equal(Pin, earn.ThetaRun);
    }

    [Fact]
    public void A_wipe_forfeits_the_victory_term_entirely_never_partially()
    {
        var earn = DelveSoulLedger.AtExtraction(soulsUnbanked: 500, thetaRun: Pin, won: false, Tuning);
        Assert.Equal(500, earn.Kills); // kills already banked room by room -- a wipe never claws these back
        Assert.Equal(0, earn.Victory);
    }

    [Fact]
    public void Zero_souls_unbanked_still_reports_a_real_victory_term_on_a_win()
    {
        // Kills and victory are independent terms -- a delve with no kills at all can still win.
        var earn = DelveSoulLedger.AtExtraction(soulsUnbanked: 0, thetaRun: Pin, won: true, Tuning);
        Assert.Equal(0, earn.Kills);
        Assert.True(earn.Victory > 0);
    }
}
