using FusionRpg.Core.Hud;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudComposerTests
{
    static ActorHudStatusToken Tok(string id, bool cc = false, MagnitudeBand band = MagnitudeBand.Low) =>
        new(id, cc, band);

    [Fact]
    public void Compose_cc_priority_and_overflow()
    {
        var input = new ActorHudComposer.ActorHudComposeInput(
            IsUniquePlant: false,
            BindingPhase: null,
            LevelBand: null,
            ShieldStacks: null,
            ShieldHp: 0,
            ShieldMax: 0,
            StatusTokens: new[]
            {
                Tok("expose"),
                Tok("freeze", cc: true),
                Tok("command"),
                Tok("spark"),
            },
            StatusStripMax: 2,
            HpSliverEnabled: false);

        var snap = ActorHudComposer.Compose(input);

        Assert.Equal(2, snap.Statuses.Count);
        Assert.Equal("freeze", snap.Statuses[0].Id);
        Assert.Equal(2, snap.Overflow.StatusCount);
    }

    [Fact]
    public void Compose_omits_levelBand_when_null()
    {
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, null, null, 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false));

        Assert.Null(snap.Identity.LevelBand);
        var wire = ActorHudWireSerializer.ToDictionary(snap);
        var identity = Assert.IsType<Dictionary<string, object>>(wire["identity"]);
        Assert.False(identity.ContainsKey("levelBand"));
    }

    [Fact]
    public void Compose_never_emits_boss_tier()
    {
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, UniqueBindingPhase.Bound, 42,
            Array.Empty<ActorHudShieldStack>(), 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false));

        Assert.NotEqual(ActorHudTier.Boss, snap.Identity.Tier);
        Assert.Equal("specimen", snap.Identity.Role);
    }

    [Fact]
    public void Compose_unique_plant_flag_and_tier()
    {
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            true, null, 5,
            null, 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false));

        Assert.Equal(ActorHudTier.Unique, snap.Identity.Tier);
        Assert.Contains("unique", snap.Identity.Flags);
    }

    [Fact]
    public void Compose_shield_gate_wired_emits_zero_totals()
    {
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, null,
            Array.Empty<ActorHudShieldStack>(), 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false));

        Assert.NotNull(snap.Resources);
        Assert.NotNull(snap.Resources!.Shield);
        Assert.Equal(0, snap.Resources.Shield!.Hp);
    }

    // E41 (spec-ui-attach-point.md §4): "op:meter fills ActorHudResources.Meters for that ptr" — the
    // first-ever producer for a field that has been declared and serialized (ActorHudSnapshot.cs,
    // ActorHudWireSerializer.cs) with nothing filling it. Meters-only: no shield, no HP sliver — the
    // branch that did not exist before this module (Compose used to hardcode null in every branch).
    [Fact]
    public void Compose_fills_meters_with_no_shield_and_no_hp_sliver()
    {
        var meters = new[] { new ActorHudMeter("hp", 0.75) };
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, null, null, 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false,
            Meters: meters));

        Assert.NotNull(snap.Resources);
        Assert.Null(snap.Resources!.Shield);
        Assert.Null(snap.Resources.HpSliver);
        Assert.NotNull(snap.Resources.Meters);
        Assert.Equal("hp", snap.Resources.Meters![0].Id);
        Assert.Equal(0.75, snap.Resources.Meters[0].Ratio);
    }

    [Fact]
    public void Compose_carries_meters_alongside_an_existing_shield_block()
    {
        var meters = new[] { new ActorHudMeter("qi", 0.5) };
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, null,
            Array.Empty<ActorHudShieldStack>(), 10, 20,
            Array.Empty<ActorHudStatusToken>(), 3, false,
            Meters: meters));

        Assert.NotNull(snap.Resources!.Shield);
        Assert.NotNull(snap.Resources.Meters);
        Assert.Equal("qi", snap.Resources.Meters![0].Id);
    }

    [Fact]
    public void Compose_no_meters_keeps_the_field_null_as_before()
    {
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, null, null, 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false));

        Assert.Null(snap.Resources);
    }

    [Fact]
    public void Compose_carries_primary_and_optional_secondary_species_elements()
    {
        var elements = new ActorHudElements("air", "fire");
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, null, null, 0, 0,
            Array.Empty<ActorHudStatusToken>(), 3, false,
            Elements: elements));

        Assert.Equal("air", snap.Elements!.Primary);
        Assert.Equal("fire", snap.Elements.Secondary);
    }

    [Fact]
    public void Build_status_priority_uses_layout_prioritize()
    {
        var snap = ActorHudComposer.Compose(new ActorHudComposer.ActorHudComposeInput(
            false, null, null, null, 0, 0,
            new[]
            {
                Tok("expose"),
                Tok("freeze", cc: true),
                Tok("command"),
            },
            StatusStripMax: 2,
            HpSliverEnabled: false));

        Assert.Equal("freeze", snap.Statuses[0].Id);
        Assert.Equal(1, snap.Overflow.StatusCount);
    }
}
