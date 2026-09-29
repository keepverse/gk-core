using FusionRpg.Core.Aura;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// W7 (battle-derived-wire T4): the web-match setup producer is the FIRST production writer of
/// `BattleSetup.ActiveAuras`. Before this, the field was assigned only in tests, so
/// `BattleDerivedModifierLedger`'s recompose ran every round against an empty ledger and no commander
/// aura ever reached a real battle.
///
/// <para>The active SET comes from the same process-local `AuraRuntime` the aura endpoints use; the
/// per-channel VALUE is the shipped `AtomPushService.AuraChannelReferenceValue`. Delivery to exactly
/// the matching-side actors is proven independently in Core
/// (`gk-core/tests/FusionRpg.Core.Tests/Battle/AuraDeliveryTests.cs` — `Wave_side_aura_does_not_touch_squad`),
/// so this file proves the producer, not the delivery filter.</para>
/// </summary>
public class WebMatchAuraDeliveryTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly WebMatchService _service;

    public WebMatchAuraDeliveryTests()
    {
        // The runtime cache is a bare static dictionary keyed by playerId; every test's fresh SQLite
        // file restarts its autoincrement, so a later test's "player 1" would inherit an earlier
        // test's still-active aura. This assembly disables test parallelization for exactly this.
        AuraRuntimeEndpoints.ResetForTests();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        _service = new WebMatchService(_store, hub);
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>Equip an aura into the player's real loadout — the runtime's own `isEquipped` gate
    /// refuses an aura the player has not equipped (the same gate the HTTP enable endpoint relies on).</summary>
    void EquipAura(long playerId, string auraId)
    {
        var result = _store.SetLoadout(
            new OwnerScope(OwnerKind.Player, playerId.ToString()),
            new[] { auraId }, _ => true, () => false);
        Assert.True(result.Ok, result.Reason?.ToString());
    }

    [Fact]
    public void A_player_with_no_active_aura_produces_an_empty_list()
    {
        var playerId = _store.CreatePlayer("aura-delivery-none").Id;

        Assert.Empty(_service.ActiveAurasFor(playerId));
    }

    [Fact]
    public void An_active_aura_projects_its_grant_channel_with_the_shipped_reference_value()
    {
        var playerId = _store.CreatePlayer("aura-delivery-might").Id;
        EquipAura(playerId, "Might");
        Assert.True(AuraRuntimeEndpoints.ActiveRuntime(playerId, _store).Enable("Might").Enabled);

        var rows = _service.ActiveAurasFor(playerId);

        var might = AuraContentCatalog.Resolve("Might");
        var row = Assert.Single(rows);
        Assert.Equal("squad", row.CommanderSide);
        Assert.Equal(might.GrantChannels.Single(), row.TargetChannel);
        Assert.Equal("aura:Might", row.SourceId);
        Assert.Equal(
            AuraMagnitude.ReferenceChannelValue(
                0, might.GrantChannels.Count,
                PowerTuningHub.Tuning.Curve.PinValue, AuraTuningHub.Tuning, AptitudeTuningHub.Tuning),
            row.Value);
        Assert.True(row.Value > 0, "an active aura must contribute a positive reference value");
    }

    [Fact]
    public void The_active_set_is_read_fresh_so_the_order_of_enable_and_read_does_not_matter()
    {
        var playerId = _store.CreatePlayer("aura-delivery-order").Id;
        EquipAura(playerId, "Fortitude");

        // Read before enable: empty. Enable, read again: populated. Never a cached snapshot taken at
        // construction -- the same call path answers both orders.
        Assert.Empty(_service.ActiveAurasFor(playerId));
        Assert.True(AuraRuntimeEndpoints.ActiveRuntime(playerId, _store).Enable("Fortitude").Enabled);
        Assert.Single(_service.ActiveAurasFor(playerId));

        Assert.True(AuraRuntimeEndpoints.ActiveRuntime(playerId, _store).Disable("Fortitude"));
        Assert.Empty(_service.ActiveAurasFor(playerId));
    }
}
