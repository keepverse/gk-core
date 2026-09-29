using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Battle;

/// <summary>D2.14 — <see cref="DelveCarryOut"/> and the setup mapping (spec-delve-battle-profile.md
/// §5): a pure overlay of a previous room's carry onto a fresh next-room setup.</summary>
public class DelveCarryTests
{
    [Fact]
    public void Apply_overlays_hp_statuses_and_shield_without_touching_anything_else()
    {
        var freshSetup = new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", SpeciesId = "warden", TypeId = 7,
            Level = 5, MaxHp = 1000, Atk = 50, Defense = 20, PartyIndex = 0,
        };
        var carriedStatuses = new[] { new BattleStatusSpec("regen", MagnitudePerPulse: 10, DurationMs: 3000) };
        var carriedShield = new BattleInnateShield(BaseHp: 200, Priority: FusionRpg.Core.Combat.Shield.ShieldPolicy.PriorityInnate);
        var carryOut = new DelveCarryOut(carriedStatuses, carriedShield, Retreated: false);

        var next = DelveCarryIn.Apply(freshSetup, previousHpRemaining: 350, carryOut);

        Assert.Equal(350, next.CurrentHp);
        Assert.Same(carriedStatuses, next.InitialStatuses);
        Assert.Same(carriedShield, next.InnateShield);
        // Everything else is untouched -- this is an overlay, not a rebuild.
        Assert.Equal(freshSetup.Key, next.Key);
        Assert.Equal(freshSetup.MaxHp, next.MaxHp);
        Assert.Equal(freshSetup.PartyIndex, next.PartyIndex);
    }

    [Fact]
    public void A_null_shield_carries_forward_as_null_no_shield_is_invented()
    {
        var freshSetup = new BattleActorSetup { Key = "squad:0", Side = "squad", MaxHp = 500 };
        var carryOut = new DelveCarryOut(Array.Empty<BattleStatusSpec>(), Shield: null, Retreated: false);

        var next = DelveCarryIn.Apply(freshSetup, previousHpRemaining: 500, carryOut);

        Assert.Null(next.InnateShield);
        Assert.Empty(next.InitialStatuses);
    }

    [Fact]
    public void BattleActorResult_CarryOut_is_absent_from_json_for_every_existing_caller()
    {
        // The golden-argument proof, directly: a result with no CarryOut set serialises with the
        // key ENTIRELY ABSENT, not present-and-null -- WhenWritingDefault's own contract.
        var result = new BattleActorResult("squad:0", "squad", "warden", 7, 500, 0, 0, true, false, 1000);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("CarryOut", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BattleActorResult_CarryOut_serialises_when_actually_set()
    {
        var carryOut = new DelveCarryOut(Array.Empty<BattleStatusSpec>(), Shield: null, Retreated: false);
        var result = new BattleActorResult("squad:0", "squad", "warden", 7, 500, 0, 0, true, false, 1000) { CarryOut = carryOut };
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("CarryOut", json, StringComparison.OrdinalIgnoreCase);
    }
}
