using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

public class IlvlTierLadderTests
{
    [Fact]
    public void The_ilvl_tier_ladder_is_1_1_8_18_32()
    {
        Assert.Equal(new[] { 1, 1, 8, 18, 32 }, IlvlTierLadder.MinIlvlByTier);
        // I8's rejected table must not reappear.
        Assert.DoesNotContain(12, IlvlTierLadder.MinIlvlByTier);
        Assert.DoesNotContain(25, IlvlTierLadder.MinIlvlByTier);
        Assert.DoesNotContain(40, IlvlTierLadder.MinIlvlByTier);
        Assert.DoesNotContain(60, IlvlTierLadder.MinIlvlByTier);
    }

    [Theory]
    [InlineData(1, 2)]  // t1 and t2 share the same minimum ilvl (1) -- both are reachable immediately
    [InlineData(7, 2)]
    [InlineData(8, 3)]
    [InlineData(17, 3)]
    [InlineData(18, 4)]
    [InlineData(31, 4)]
    [InlineData(32, 5)]
    [InlineData(500, 5)]
    public void MaxTierAt_matches_the_ladder(int ilvl, int expectedTier) =>
        Assert.Equal(expectedTier, IlvlTierLadder.MaxTierAt(ilvl));

    [Fact]
    public void T1_never_falls_out_of_the_window_at_high_ilvl()
    {
        // The collapsing envelope, not I8's rejected sliding window: at ilvl 500 a rung whose band
        // starts at t1 still offers t1, because env.minTier = min(band.MinTier, env.maxTier).
        var (minTier, maxTier) = IlvlTierLadder.Envelope(bandMinTier: 1, bandMaxTier: 5, ilvl: 500);
        Assert.Equal(1, minTier);
        Assert.Equal(5, maxTier);
    }

    [Fact]
    public void The_envelope_collapses_toward_the_ceiling_at_low_ilvl()
    {
        var (minTier, maxTier) = IlvlTierLadder.Envelope(bandMinTier: 1, bandMaxTier: 5, ilvl: 1);
        Assert.Equal(1, minTier);
        Assert.Equal(2, maxTier); // t1 and t2 share the same minimum ilvl
    }

    [Fact]
    public void The_envelope_never_exceeds_the_rungs_own_band_ceiling()
    {
        var (_, maxTier) = IlvlTierLadder.Envelope(bandMinTier: 3, bandMaxTier: 4, ilvl: 500);
        Assert.Equal(4, maxTier); // capped by the band, not by the ladder's top
    }

    [Fact]
    public void A_narrowed_envelope_narrows_the_count_and_records_it()
    {
        var result = EnvelopeNarrowing.Apply(requestedRolls: 3, drawableGroupsInEnvelope: 2);
        Assert.Equal(2, result.RollCount);
        Assert.True(result.Narrowed);
    }

    [Fact]
    public void An_unnarrowed_envelope_is_not_flagged()
    {
        var result = EnvelopeNarrowing.Apply(requestedRolls: 2, drawableGroupsInEnvelope: 5);
        Assert.Equal(2, result.RollCount);
        Assert.False(result.Narrowed);
    }
}

public class AffixFiltersTests
{
    [Fact]
    public void Stat_derived_families_are_legal_on_battle_and_lawn()
    {
        Assert.True(AffixFilters.RuntimeAllows("stat.derived", RuntimeId.Battle));
        Assert.True(AffixFilters.RuntimeAllows("stat.derived", RuntimeId.Lawn));
    }

    [Fact]
    public void A_stat_derived_affix_is_now_allowed_for_a_sim_target_via_the_partial_fold()
    {
        // Moved with the registry cell (mechanism-wiring E5, 2026-09-06). Was `Assert.False` when the
        // Sim cell was `RuntimeState.None` (the half of the D6 quarantine that had not lifted yet).
        // `AffixFilters.RuntimeAllows` is "anything but None" (AffixFilters.cs), and the Sim cell moved
        // to `RuntimeState.Partial` once ActorDerivedLookup's contribution fold gave it a real (if
        // partial -- Flat/Increased only, see EffectOfflineKitTests.
        // The_four_derived_ops_decide_Full_versus_Partial) consumer, then to `RuntimeState.Full`
        // (sim-hub-parity, T15, 2026-09-13) once the fold became op-aware. So this is now legal
        // content, not a refusal -- an affix author may name `stat.derived` for a Sim target and every
        // op composes correctly, not just Flat/Increased.
        Assert.True(AffixFilters.RuntimeAllows("stat.derived", RuntimeId.Sim));
    }

    [Fact]
    public void Warding_and_resilience_are_flagged_match_scope_only()
    {
        Assert.True(AffixFilters.IsMatchScopeOnly("atom.warding"));
        Assert.True(AffixFilters.IsMatchScopeOnly("atom.resilience"));
        Assert.False(AffixFilters.IsMatchScopeOnly("atom.vitality"));
    }

    [Fact]
    public void Side_both_always_passes()
    {
        Assert.True(AffixFilters.SideAllows("both", "zombie"));
        Assert.True(AffixFilters.SideAllows("both", "plant"));
    }

    [Fact]
    public void A_zombie_only_family_refuses_a_plant_side_actor()
    {
        Assert.False(AffixFilters.SideAllows("zombie", "plant"));
        Assert.True(AffixFilters.SideAllows("zombie", "zombie"));
    }

    [Fact]
    public void Frame_allows_checks_the_familys_own_frame_list()
    {
        Assert.True(AffixFilters.FrameAllows(new[] { "plant" }, "plant"));
        Assert.False(AffixFilters.FrameAllows(new[] { "plant" }, "humanoid"));
    }
}
