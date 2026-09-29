using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Derived;

/// <summary>aura-skill T1 (audit D1/D2): <c>OverlayAdd</c> accumulates, <c>Overlay</c> still
/// replaces, and re-asserting the same fixed contribution never compounds.</summary>
public class ActorDerivedSnapshotOverlayTests
{
    const string Channel = "combat.power.fire";

    [Fact]
    public void OverlayAdd_accumulates_two_independent_contributions()
    {
        var snap = ActorDerivedSnapshot.Empty
            .OverlayAdd(new[] { new KeyValuePair<string, double>(Channel, 10.0) })
            .OverlayAdd(new[] { new KeyValuePair<string, double>(Channel, 5.0) });

        Assert.Equal(15.0, snap.Get(Channel));
    }

    [Fact]
    public void OverlayAdd_onto_a_nonzero_base_adds_to_the_existing_value()
    {
        var baseline = ActorDerivedSnapshot.FromValues(
            new[] { new KeyValuePair<string, double>(Channel, 7.0) });

        var snap = baseline.OverlayAdd(new[] { new KeyValuePair<string, double>(Channel, 3.0) });

        Assert.Equal(10.0, snap.Get(Channel));
    }

    [Fact]
    public void OverlayAdd_never_erases_a_second_channel_from_a_first_overlay()
    {
        // D1 regression guard: two producers contributing to DIFFERENT channels must not stomp
        // each other — this is what "Overlay is replace, not add" broke for a shared channel, and
        // OverlayAdd must not reintroduce any variant of that loss.
        var snap = ActorDerivedSnapshot.Empty
            .OverlayAdd(new[] { new KeyValuePair<string, double>("combat.power.omni", 20.0) })
            .OverlayAdd(new[] { new KeyValuePair<string, double>("combat.defense.omni", 8.0) });

        Assert.Equal(20.0, snap.Get("combat.power.omni"));
        Assert.Equal(8.0, snap.Get("combat.defense.omni"));
    }

    [Fact]
    public void Idempotence_applying_the_same_fixed_contribution_twice_from_the_same_base_matches_once()
    {
        // D2: a contribution is a function of (source, coefficients) only, never of the channel's
        // current value — so recomputing it from the SAME base twice must yield the SAME result,
        // not a doubled one. (Doubling would occur only if a caller read-and-re-added the existing
        // value before calling OverlayAdd, which OverlayAdd's own contract forbids.)
        var baseline = ActorDerivedSnapshot.FromValues(
            new[] { new KeyValuePair<string, double>(Channel, 100.0) });

        var once = baseline.OverlayAdd(new[] { new KeyValuePair<string, double>(Channel, 15.0) });
        var again = baseline.OverlayAdd(new[] { new KeyValuePair<string, double>(Channel, 15.0) });

        Assert.Equal(once.Get(Channel), again.Get(Channel));
        Assert.Equal(115.0, once.Get(Channel));
    }

    [Fact]
    public void Overlay_still_replaces_for_genuine_replacement_use()
    {
        // Regression: Overlay's replace semantics are pinned elsewhere for ActorDerivedProfiles
        // (ActorDerivedProfilesTests.Overlay_channels_replace_profile) — confirm the method itself
        // is untouched by this task.
        var snap = ActorDerivedSnapshot.FromValues(
                new[] { new KeyValuePair<string, double>(Channel, 40.0) })
            .Overlay(new[] { new KeyValuePair<string, double>(Channel, 5.0) });

        Assert.Equal(5.0, snap.Get(Channel));
    }
}
