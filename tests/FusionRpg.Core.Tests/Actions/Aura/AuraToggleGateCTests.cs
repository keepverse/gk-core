using FusionRpg.Core.Actions.Aura;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Aura;

/// <summary>aura-skill T13, Gate C: "disabling returns the channel to its prior value." Composes
/// `AuraRuntime` (T13, which aura ids are active) with `BattleDerivedModifierLedger` (T4's recompose
/// seam, already proven idempotent in isolation) — enabling delivers the T10 value, disabling
/// withdraws exactly that contribution, never more or less.</summary>
public class AuraToggleGateCTests
{
    const string Actor = "squad:0";
    const string Channel = "combat.power.omni";

    [Fact]
    public void Disabling_an_aura_returns_the_channel_to_its_prior_value()
    {
        var baseline = ActorDerivedSnapshot.FromValues(
            new[] { new KeyValuePair<string, double>(Channel, 100.0) });
        var live = ActorDerivedSnapshot.FromValues(baseline.Channels);
        var ledger = new BattleDerivedModifierLedger();
        var runtime = new AuraRuntime(maxActiveAuras: 1, isEquipped: _ => true);

        var enableResult = runtime.Enable("aura:ember");
        Assert.True(enableResult.Enabled);
        ledger.Add(Actor, Channel, "aura:ember", 500);
        ledger.Recompose(Actor, baseline, live);
        Assert.Equal(600.0, live.Get(Channel)); // prior 100 + the aura's 500

        var disabled = runtime.Disable("aura:ember");
        Assert.True(disabled);
        ledger.RemoveBySource(Actor, "aura:ember");
        ledger.Recompose(Actor, baseline, live);

        Assert.Equal(100.0, live.Get(Channel)); // back to prior, exactly
    }

    [Fact]
    public void Eviction_also_returns_the_evicted_auras_channel_to_prior_via_the_same_mechanism()
    {
        // Gate C's own guarantee must hold for the AUTOMATIC eviction path too, not just an explicit
        // Disable call -- AuraEnableResult.EvictedAuraId is exactly what a caller withdraws by.
        var baseline = ActorDerivedSnapshot.FromValues(
            new[] { new KeyValuePair<string, double>(Channel, 100.0) });
        var live = ActorDerivedSnapshot.FromValues(baseline.Channels);
        var ledger = new BattleDerivedModifierLedger();
        var runtime = new AuraRuntime(maxActiveAuras: 1, isEquipped: _ => true);

        runtime.Enable("aura:ember");
        ledger.Add(Actor, Channel, "aura:ember", 500);
        ledger.Recompose(Actor, baseline, live);
        Assert.Equal(600.0, live.Get(Channel));

        var result = runtime.Enable("aura:frost"); // evicts ember (maxActiveAuras=1)
        Assert.Equal("aura:ember", result.EvictedAuraId);
        ledger.RemoveBySource(Actor, result.EvictedAuraId!); // the caller's own withdrawal, driven by the typed outcome
        ledger.Add(Actor, Channel, "aura:frost", 200);
        ledger.Recompose(Actor, baseline, live);

        Assert.Equal(300.0, live.Get(Channel)); // prior 100 + frost's 200, ember's 500 fully gone
    }

    [Fact]
    public void Two_toggles_in_a_row_never_drift_from_the_true_prior_value()
    {
        // D2 (idempotence): enable/disable/enable/disable must not accumulate rounding or leftover
        // contributions across cycles.
        var baseline = ActorDerivedSnapshot.FromValues(
            new[] { new KeyValuePair<string, double>(Channel, 100.0) });
        var live = ActorDerivedSnapshot.FromValues(baseline.Channels);
        var ledger = new BattleDerivedModifierLedger();
        var runtime = new AuraRuntime(maxActiveAuras: 1, isEquipped: _ => true);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            runtime.Enable("aura:ember");
            ledger.Add(Actor, Channel, "aura:ember", 500);
            ledger.Recompose(Actor, baseline, live);
            Assert.Equal(600.0, live.Get(Channel));

            runtime.Disable("aura:ember");
            ledger.RemoveBySource(Actor, "aura:ember");
            ledger.Recompose(Actor, baseline, live);
            Assert.Equal(100.0, live.Get(Channel));
        }
    }
}
