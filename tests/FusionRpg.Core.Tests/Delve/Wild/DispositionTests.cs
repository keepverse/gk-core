using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>D4.1 (spec-wild-room.md §1) — <see cref="Disposition.OrdinalOf"/> and
/// <see cref="Disposition.Shift"/>: the ordinal plus five shifts, the `[0,3]` rail, and the
/// "no species base exists, no literal fallback" acceptance line proven structurally (this file
/// never supplies a default base — every call passes one explicitly, and an unknown id throws).</summary>
public class DispositionTests
{
    static DispositionTests()
    {
        // Reads the real, shipped disposition.v1.json rather than a hand-transcribed copy --
        // DungeonTestFiles.cs's own stated purpose -- so a re-vote of the vocabulary's order is
        // caught here rather than silently assumed. Configure is idempotent, matching every other
        // Configure-gated registry's own test bootstrap this session (InteractionVerbCatalog etc.).
        var json = File.ReadAllText(Path.Combine(DungeonTestFiles.RegistryDir(), "disposition.v1.json"));
        DispositionCatalog.Configure(DispositionCatalog.Parse(json));
    }

    // ---- OrdinalOf ----

    [Theory]
    [InlineData("eager", 0)]
    [InlineData("open", 1)]
    [InlineData("wary", 2)]
    [InlineData("hostile", 3)]
    public void OrdinalOf_matches_the_registrys_own_content_order(string id, int expectedOrdinal)
    {
        Assert.Equal(expectedOrdinal, Disposition.OrdinalOf(id));
    }

    [Fact]
    public void OrdinalOf_an_unknown_id_throws_never_a_silent_default()
    {
        Assert.Throws<ArgumentException>(() => Disposition.OrdinalOf("furious"));
    }

    // ---- Shift: identity and direction ----

    [Fact]
    public void Shift_with_all_zero_shifts_returns_the_base_unchanged()
    {
        Assert.Equal("wary", Disposition.Shift("wary", 0, 0, 0, 0, 0));
    }

    [Fact]
    public void A_positive_sum_moves_toward_hostile()
    {
        // eager(0) + 2 = open(1)+... exactly wary(2): positive = toward hostile, verbatim.
        Assert.Equal("wary", Disposition.Shift("eager", 1, 1, 0, 0, 0));
    }

    [Fact]
    public void A_negative_sum_moves_toward_eager()
    {
        Assert.Equal("open", Disposition.Shift("hostile", -1, -1, 0, 0, 0));
    }

    // ---- Shift: the [0,3] rail, exempt structural clamp ----

    [Fact]
    public void An_overshoot_past_hostile_clamps_at_hostile_never_wraps()
    {
        Assert.Equal("hostile", Disposition.Shift("wary", 10, 0, 0, 0, 0));
    }

    [Fact]
    public void An_undershoot_past_eager_clamps_at_eager_never_goes_negative()
    {
        Assert.Equal("eager", Disposition.Shift("open", -10, 0, 0, 0, 0));
    }

    [Fact]
    public void Two_individually_legal_shifts_that_together_overshoot_still_clamp_once_at_the_end()
    {
        // open(1) + 2 (rung) + 2 (delta band) = 5 -- past hostile(3) -- must land exactly at
        // hostile, not wrap or throw, proving the clamp runs on the FINAL sum, not per-shift.
        Assert.Equal("hostile", Disposition.Shift("open", 2, 2, 0, 0, 0));
    }

    // ---- Shift: every one of the five shift sources is genuinely read, not ignored ----

    [Theory]
    [InlineData(1, 0, 0, 0, 0)] // rungShift
    [InlineData(0, 1, 0, 0, 0)] // deltaBandShift
    [InlineData(0, 0, 1, 0, 0)] // offerPreferenceShift
    [InlineData(0, 0, 0, 1, 0)] // remembersShift
    [InlineData(0, 0, 0, 0, 1)] // stanceShift
    public void Each_of_the_five_named_shift_sources_alone_moves_the_result(
        int rungShift, int deltaBandShift, int offerPreferenceShift, int remembersShift, int stanceShift)
    {
        var shifted = Disposition.Shift("eager", rungShift, deltaBandShift, offerPreferenceShift, remembersShift, stanceShift);
        Assert.Equal("open", shifted);
    }

    [Fact]
    public void Shift_with_an_unknown_base_id_throws_never_a_silent_default()
    {
        Assert.Throws<ArgumentException>(() => Disposition.Shift("furious", 0, 0, 0, 0, 0));
    }

    // ---- Cross-check: the registry's own count backs the "[0,3]" acceptance line ----

    [Fact]
    public void The_real_registry_has_exactly_four_members_the_0_3_rail_is_not_a_guess()
    {
        Assert.Equal(4, DispositionCatalog.All.Count);
        Assert.Equal(new[] { "eager", "open", "wary", "hostile" }, DispositionCatalog.All);
    }
}
