using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Aura;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Aura;

/// <summary>aura-skill T13: the active set, enable/disable, FIFO eviction — all typed, visible
/// outcomes, never silent state changes (GG-55).</summary>
public class AuraRuntimeTests
{
    static AuraRuntime OneSlot(params string[] equipped) =>
        new(maxActiveAuras: 1, isEquipped: id => Array.IndexOf(equipped, id) >= 0);

    static AuraRuntime TwoSlots(params string[] equipped) =>
        new(maxActiveAuras: 2, isEquipped: id => Array.IndexOf(equipped, id) >= 0);

    [Fact]
    public void Enabling_an_equipped_aura_under_the_cap_is_clean()
    {
        var runtime = OneSlot("might");
        var result = runtime.Enable("might");

        Assert.True(result.Enabled);
        Assert.Null(result.EvictedAuraId);
        Assert.Null(result.Refusal);
        Assert.True(runtime.IsActive("might"));
    }

    [Fact]
    public void Enabling_at_the_cap_evicts_the_oldest_and_names_it()
    {
        var runtime = OneSlot("might", "fortitude");
        runtime.Enable("might");

        var result = runtime.Enable("fortitude");

        Assert.True(result.Enabled);
        Assert.Equal("might", result.EvictedAuraId);
        Assert.False(runtime.IsActive("might"));
        Assert.True(runtime.IsActive("fortitude"));
    }

    [Fact]
    public void Enabling_an_unequipped_aura_is_refused_with_NotEquipped()
    {
        var runtime = OneSlot("might");
        var result = runtime.Enable("fortitude"); // not in the equipped set

        Assert.False(result.Enabled);
        Assert.Equal(UsabilityReason.NotEquipped, result.Refusal);
        Assert.Equal("fortitude", result.RefusalDetail);
        Assert.False(runtime.IsActive("fortitude"));
    }

    [Fact]
    public void Re_enabling_an_already_active_aura_is_a_reported_no_op_not_a_refresh()
    {
        var runtime = TwoSlots("might", "fortitude");
        runtime.Enable("might");
        runtime.Enable("fortitude");

        // "might" is oldest right now. Re-enabling it must NOT reset its age -- if it did, the next
        // eviction test below would evict "fortitude" instead.
        var reEnable = runtime.Enable("might");
        Assert.False(reEnable.Enabled);
        Assert.Equal(UsabilityReason.AlreadyActive, reEnable.Refusal);

        var thirdAura = new AuraRuntime(maxActiveAuras: 2, isEquipped: _ => true);
        thirdAura.Enable("might");
        thirdAura.Enable("fortitude");
        thirdAura.Enable("might"); // no-op, must not refresh age
        var evictionResult = thirdAura.Enable("ferocity");
        Assert.Equal("might", evictionResult.EvictedAuraId); // still oldest, unchanged by the re-enable attempt
    }

    [Fact]
    public void Disable_is_a_safe_no_op_when_the_aura_was_never_active()
    {
        var runtime = OneSlot("might");
        Assert.False(runtime.Disable("might"));
    }

    [Fact]
    public void Disable_removes_an_active_aura()
    {
        var runtime = OneSlot("might");
        runtime.Enable("might");

        Assert.True(runtime.Disable("might"));
        Assert.False(runtime.IsActive("might"));
    }

    [Fact]
    public void Eviction_order_is_activation_order_not_equip_order()
    {
        // Equip order: fortitude, might. Activation order: might first, then fortitude -- eviction
        // must follow ACTIVATION order (might is oldest), not equip order (fortitude would be oldest).
        var runtime = TwoSlots("fortitude", "might");
        runtime.Enable("might");
        runtime.Enable("fortitude");

        var thirdSlot = new AuraRuntime(maxActiveAuras: 2, isEquipped: _ => true);
        thirdSlot.Enable("might");
        thirdSlot.Enable("fortitude");
        var result = thirdSlot.Enable("ferocity");

        Assert.Equal("might", result.EvictedAuraId);
    }

    [Fact]
    public void Does_not_implement_IStanceCheck_the_anti_StanceHeld_regression()
    {
        // spec-aura-action-shape.md §2's own deliberate divergence: "a commander who can do nothing
        // else while their aura runs is not a commander." AuraRuntime must not participate in the
        // gate-0 exclusivity check StanceRuntime uses.
        var runtime = OneSlot("might");
        Assert.False(runtime is IStanceCheck, "AuraRuntime must not implement IStanceCheck");
    }

    [Fact]
    public void MaxActiveAuras_of_zero_or_less_throws_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuraRuntime(0, _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuraRuntime(-1, _ => true));
    }
}
