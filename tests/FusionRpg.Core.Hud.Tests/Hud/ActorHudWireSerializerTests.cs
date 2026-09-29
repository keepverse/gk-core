using FusionRpg.Core.Hud;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudWireSerializerTests
{
    [Fact]
    public void ToDictionary_tier_and_band_strings()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Unique, "specimen", 12, new[] { "unique" }),
            null,
            new[]
            {
                new ActorHudStatusToken("expose", false, MagnitudeBand.Mid),
                new ActorHudStatusToken("freeze", true, MagnitudeBand.High),
            },
            new ActorHudOverflow(0));

        var wire = ActorHudWireSerializer.ToDictionary(snap);
        var identity = Assert.IsType<Dictionary<string, object>>(wire["identity"]);
        Assert.Equal("unique", identity["tier"]);
        Assert.Equal("specimen", identity["role"]);
        Assert.Equal(12, identity["levelBand"]);

        var statuses = Assert.IsAssignableFrom<object[]>(wire["statuses"]);
        Assert.Equal(2, statuses.Length);
        var first = Assert.IsType<Dictionary<string, object>>(statuses[0]);
        Assert.Equal("mid", first["magnitudeBand"]);
        var second = Assert.IsType<Dictionary<string, object>>(statuses[1]);
        Assert.Equal("high", second["magnitudeBand"]);
        Assert.True((bool)second["cc"]!);
    }

    [Fact]
    public void ToDictionary_omits_levelBand_when_null()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
            null,
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0));

        var identity = Assert.IsType<Dictionary<string, object>>(
            ActorHudWireSerializer.ToDictionary(snap)["identity"]);
        Assert.False(identity.ContainsKey("levelBand"));
    }

    [Fact]
    public void ToDictionary_shield_stacks_shape()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
            new ActorHudResources(
                new ActorHudShield(50, 80, new[] { new ActorHudShieldStack("fire", 50, 80) }),
                null,
                null),
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0));

        var resources = Assert.IsType<Dictionary<string, object>>(
            ActorHudWireSerializer.ToDictionary(snap)["resources"]);
        var shield = Assert.IsType<Dictionary<string, object>>(resources["shield"]);
        Assert.Equal(50L, shield["hp"]);
        Assert.Equal(80L, shield["max"]);

        var stacks = Assert.IsAssignableFrom<object[]>(shield["stacks"]);
        var stack = Assert.IsType<Dictionary<string, object>>(stacks[0]);
        Assert.Equal("fire", stack["element"]);
    }

    // E41 (spec-ui-attach-point.md §4): "the wire serializer emits it (ActorHudWireSerializer.cs:48)".
    [Fact]
    public void ToDictionary_emits_meters()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
            new ActorHudResources(null, null, new[] { new ActorHudMeter("hp", 0.75) }),
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0));

        var resources = Assert.IsType<Dictionary<string, object>>(
            ActorHudWireSerializer.ToDictionary(snap)["resources"]);
        var meters = Assert.IsAssignableFrom<object[]>(resources["meters"]);
        var meter = Assert.IsType<Dictionary<string, object>>(meters[0]);
        Assert.Equal("hp", meter["id"]);
        Assert.Equal(0.75, meter["ratio"]);
    }

    [Fact]
    public void ToDictionary_omits_meters_when_empty()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
            new ActorHudResources(
                new ActorHudShield(50, 80, Array.Empty<ActorHudShieldStack>()), null, null),
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0));

        var resources = Assert.IsType<Dictionary<string, object>>(
            ActorHudWireSerializer.ToDictionary(snap)["resources"]);
        Assert.False(resources.ContainsKey("meters"));
    }

    [Fact]
    public void ToDictionary_omits_resources_when_null()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
            null,
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0));

        var wire = ActorHudWireSerializer.ToDictionary(snap);
        Assert.False(wire.ContainsKey("resources"));
    }

    [Fact]
    public void ToDictionary_emits_ordered_primary_and_secondary_elements()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
            null,
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0),
            new ActorHudElements("air", "fire"));

        var elements = Assert.IsType<Dictionary<string, object>>(
            ActorHudWireSerializer.ToDictionary(snap)["elements"]);
        Assert.Equal("air", elements["primary"]);
        Assert.Equal("fire", elements["secondary"]);
    }

    [Fact]
    public void ToDictionary_omits_elements_when_the_actor_is_neutral()
    {
        var snap = new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
            null,
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0));

        Assert.False(ActorHudWireSerializer.ToDictionary(snap).ContainsKey("elements"));
    }
}
