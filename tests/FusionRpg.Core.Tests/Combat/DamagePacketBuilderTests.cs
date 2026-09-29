using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// lawn-hit-entry (T9c, spec-lawn-hit-entry.md "Lifecycle correctness"): "instakill-shaped is
/// DEFINED — prefer the engine's own DamageType ... over a magnitude threshold — and produces no
/// proportional rider." These tests exercise DamagePacketBuilder.FromOverlay's event-linked
/// ("proportional") amount marker directly — the exact mechanism that would otherwise turn a
/// 1,000,000-damage lawnmower event into an equally huge rider.
/// </summary>
public class DamagePacketBuilderTests
{
    static Dictionary<string, object?> EventLinkedOverlay(long multiplierMilli = 1000) => new()
    {
        ["channel"] = "hp",
        ["amount"] = new Dictionary<string, object?>
        {
            ["eventField"] = "damage",
            ["multiplierMilli"] = multiplierMilli
        }
    };

    [Fact]
    public void Ordinary_hit_still_gets_the_proportional_rider()
    {
        // Falsifier for the next test: an ordinary (non-instakill) hit must still scale normally —
        // proves the guard is DamageType-shaped, not an accidental blanket suppression.
        var ev = new EffectEventDto { Damage = 100, InstakillShaped = false };
        var packet = DamagePacketBuilder.FromOverlay(EventLinkedOverlay(), ev);
        Assert.Equal(100, packet.SignedAmount);
    }

    [Fact]
    public void Instakill_shaped_hit_gets_no_proportional_rider()
    {
        // The lawnmower-shaped case: a huge vanilla amount must not produce an equally huge rider.
        var ev = new EffectEventDto { Damage = 1_000_000, InstakillShaped = true };
        var packet = DamagePacketBuilder.FromOverlay(EventLinkedOverlay(), ev);
        Assert.Equal(0, packet.SignedAmount);
    }

    [Fact]
    public void Instakill_shaped_hit_still_honours_a_flat_authored_amount()
    {
        // Only the EVENT-LINKED ("proportional") marker is refused — a flat, non-proportional
        // authored amount is unaffected by instakill-shaping (it was never scaling off the hit).
        var ev = new EffectEventDto { Damage = 1_000_000, InstakillShaped = true };
        var packet = DamagePacketBuilder.FromOverlay(
            new Dictionary<string, object?> { ["channel"] = "hp", ["amount"] = -50L }, ev);
        Assert.Equal(-50, packet.SignedAmount);
    }
}
