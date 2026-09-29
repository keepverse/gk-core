using FusionRpg.Core.Settings;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// The centralized user-settings module (`solid-remediation`, 2026-09-17, owner: *"Make new user
/// setting module if we dont have centralize user settings"*).
///
/// <para>Covers the three properties that make it a module rather than another one-off: it is scoped
/// per player, its key vocabulary is closed and enforced, and "never chosen" is distinguishable from
/// "chosen to equal the default".</para>
/// </summary>
[Trait("VerificationId", "data.user-settings")]
public class UserSettingsStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public UserSettingsStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary><b>The closed vocabulary.</b> `validation-ssot.md`: a registry the code owns and a human
    /// edits is pinned, and its count IS the contract — unlike a population such as species, whose size
    /// is a reading. Adding a setting is a reviewed change and must land here deliberately.</summary>
    [Fact]
    public void The_setting_registry_is_a_closed_vocabulary()
    {
        Assert.Collection(UserSettingKeys.All,
            hud => Assert.Equal(UserSettingKeys.WorldHud, hud.Key),
            vfx => Assert.Equal(UserSettingKeys.VisualEffects, vfx.Key));

        Assert.True(UserSettingKeys.IsKnown(UserSettingKeys.WorldHud));
        Assert.True(UserSettingKeys.IsKnown(UserSettingKeys.VisualEffects));
        Assert.False(UserSettingKeys.IsKnown("lawn.notARealSetting"));
        Assert.False(UserSettingKeys.IsKnown(""));
        Assert.False(UserSettingKeys.IsKnown(null));

        // Keys are unique: two rows sharing one key would make the persisted primary key ambiguous.
        Assert.Equal(UserSettingKeys.All.Count, UserSettingKeys.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());

        // Every declared default parses as its declared kind. A default that does not parse would read
        // back as `false` forever and look exactly like a deliberate choice.
        foreach (var def in UserSettingKeys.All)
            Assert.True(UserSettingKeys.TryParseBool(def.Key, def.DefaultJson, out _));
    }

    /// <summary>The world HUD ships OFF — the measured ruling, asserted rather than left in a comment.</summary>
    [Fact]
    public void The_world_hud_defaults_off()
    {
        Assert.False(UserSettingKeys.DefaultBool(UserSettingKeys.WorldHud));
        Assert.False(_store.GetUserSettingBool(_store.GetCurrentPlayerId(), UserSettingKeys.WorldHud));
    }

    [Fact]
    public void Visual_effects_default_on()
    {
        Assert.True(UserSettingKeys.DefaultBool(UserSettingKeys.VisualEffects));
        Assert.True(_store.GetUserSettingBool(_store.GetCurrentPlayerId(), UserSettingKeys.VisualEffects));
    }

    [Fact]
    public void A_set_value_is_read_back_and_can_be_flipped_again()
    {
        var playerId = _store.GetCurrentPlayerId();

        _store.SetUserSettingBool(playerId, UserSettingKeys.WorldHud, true);
        Assert.True(_store.GetUserSettingBool(playerId, UserSettingKeys.WorldHud));

        _store.SetUserSettingBool(playerId, UserSettingKeys.WorldHud, false);
        Assert.False(_store.GetUserSettingBool(playerId, UserSettingKeys.WorldHud));
    }

    /// <summary>Per player, which is the whole reason this is not the existing singleton
    /// <c>settings</c> table.</summary>
    [Fact]
    public void Settings_are_scoped_to_one_player()
    {
        var a = _store.GetCurrentPlayerId();
        var b = _store.CreatePlayer("Other").Id;
        Assert.NotEqual(a, b);

        _store.SetUserSettingBool(a, UserSettingKeys.WorldHud, true);

        Assert.True(_store.GetUserSettingBool(a, UserSettingKeys.WorldHud));
        Assert.False(_store.GetUserSettingBool(b, UserSettingKeys.WorldHud));
    }

    /// <summary>"Never chosen" and "chosen to be the default" are different states, and the store keeps
    /// them different. If defaults were written into the table on read, a later change to a default
    /// would silently fail to reach everyone who had never touched the setting.</summary>
    [Fact]
    public void An_unset_setting_is_absent_rather_than_stored_as_its_default()
    {
        var playerId = _store.GetCurrentPlayerId();
        Assert.Empty(_store.ListUserSettings(playerId));

        // Reading does not create a row.
        _ = _store.GetUserSettingBool(playerId, UserSettingKeys.WorldHud);
        Assert.Empty(_store.ListUserSettings(playerId));

        // Choosing the SAME value as the default still records an explicit choice.
        _store.SetUserSettingBool(playerId, UserSettingKeys.WorldHud, false);
        var stored = Assert.Single(_store.ListUserSettings(playerId));
        Assert.Equal(UserSettingKeys.WorldHud, stored.Key);
        Assert.Equal("false", stored.Value);
    }

    /// <summary>An unknown key is refused at the store, not quietly stored. A settings table that
    /// accepts anything stops being a registry and becomes a junk drawer.</summary>
    [Fact]
    public void An_unknown_key_is_refused_on_both_read_and_write()
    {
        var playerId = _store.GetCurrentPlayerId();
        Assert.Throws<ArgumentException>(() => _store.SetUserSettingBool(playerId, "lawn.notARealSetting", true));
        Assert.Throws<ArgumentException>(() => _store.GetUserSettingBool(playerId, "lawn.notARealSetting"));
        Assert.Empty(_store.ListUserSettings(playerId));
    }

    /// <summary>A value that does not fit the declared kind reads back as the default rather than being
    /// coerced — and the parse helper says so, so a caller can tell the difference.</summary>
    [Fact]
    public void A_malformed_value_does_not_parse_as_a_boolean()
    {
        Assert.False(UserSettingKeys.TryParseBool(UserSettingKeys.WorldHud, "yes", out _));
        Assert.False(UserSettingKeys.TryParseBool(UserSettingKeys.WorldHud, "1", out _));
        Assert.False(UserSettingKeys.TryParseBool(UserSettingKeys.WorldHud, null, out _));
        Assert.True(UserSettingKeys.TryParseBool(UserSettingKeys.WorldHud, "TRUE", out var loud));
        Assert.True(loud);
    }
}
