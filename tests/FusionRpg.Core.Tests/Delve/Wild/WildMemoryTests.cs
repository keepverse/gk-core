using FusionRpg.Core.Delve.Wild;
using Xunit;
using Row = FusionRpg.Core.Delve.Wild.WildMemory.WildTalkLogRow;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>
/// D4.4 (spec-wild-room.md §8, "Remembers") — `WildMemory.For`.
///
/// <para><b>Correction to this task's own Verify line</b> ("a `remembers` round-trip across two
/// delves"): §8 is explicit and verbatim — "a pure read over THIS delve's `decisions_json`… No
/// table: the log is the memory and ends with the delve." There is no cross-delve persistence to
/// round-trip; the corrected, real behavior is the opposite — a fresh delve's own (empty) log never
/// carries a prior delve's opinion forward, proven below by <see cref="Memory_never_crosses_a_delve_boundary_a_fresh_delves_empty_log_has_no_opinion"/>.
/// </para>
/// </summary>
public class WildMemoryTests
{
    const string SpeciesA = "species.a";
    const string SpeciesB = "species.b";

    [Fact]
    public void No_row_for_the_species_returns_zero_no_opinion()
    {
        var decisions = new[] { new Row("wild", SpeciesB, WildMemory.ResolutionJoins) };
        Assert.Equal(0, WildMemory.For(decisions, SpeciesA));
    }

    [Fact]
    public void An_empty_log_returns_zero_no_opinion()
    {
        Assert.Equal(0, WildMemory.For(Array.Empty<Row>(), SpeciesA));
    }

    [Theory]
    [InlineData(WildMemory.ResolutionJoins, -1)]
    [InlineData(WildMemory.ResolutionFight, 1)]
    [InlineData(WildMemory.ResolutionAttacks, 1)]
    [InlineData(WildMemory.ResolutionLeave, 0)]
    [InlineData(WildMemory.ResolutionFlees, 0)]
    [InlineData(WildMemory.ResolutionTakesLeaves, 0)]
    public void Each_of_the_six_resolutions_maps_to_its_own_named_shift(string resolution, int expectedShift)
    {
        var decisions = new[] { new Row("wild", SpeciesA, resolution) };
        Assert.Equal(expectedShift, WildMemory.For(decisions, SpeciesA));
    }

    [Fact]
    public void The_most_recent_row_naming_the_species_decides_not_the_first()
    {
        var decisions = new[]
        {
            new Row("wild", SpeciesA, WildMemory.ResolutionJoins),  // -1, oldest
            new Row("wild", SpeciesA, WildMemory.ResolutionFight),  // +1, most recent
        };
        Assert.Equal(1, WildMemory.For(decisions, SpeciesA));
    }

    [Fact]
    public void Rows_for_a_different_species_are_ignored()
    {
        var decisions = new[]
        {
            new Row("wild", SpeciesA, WildMemory.ResolutionJoins),
            new Row("wild", SpeciesB, WildMemory.ResolutionFight),
        };
        Assert.Equal(-1, WildMemory.For(decisions, SpeciesA));
    }

    [Fact]
    public void Rows_from_a_different_surface_are_ignored_even_if_they_name_the_species()
    {
        // §8: "talk rows with surface: wild" -- an event-deck row naming the same species must not count.
        var decisions = new[] { new Row("event", SpeciesA, WildMemory.ResolutionJoins) };
        Assert.Equal(0, WildMemory.For(decisions, SpeciesA));
    }

    [Fact]
    public void Exactly_one_bands_worth_of_shift_regardless_of_how_many_prior_rows_agree()
    {
        // "Exactly one band, whatever the count" -- three straight joins do not stack to -3.
        var decisions = new[]
        {
            new Row("wild", SpeciesA, WildMemory.ResolutionJoins),
            new Row("wild", SpeciesA, WildMemory.ResolutionJoins),
            new Row("wild", SpeciesA, WildMemory.ResolutionJoins),
        };
        Assert.Equal(-1, WildMemory.For(decisions, SpeciesA));
    }

    [Fact]
    public void An_unknown_resolution_throws_never_a_silent_zero()
    {
        var decisions = new[] { new Row("wild", SpeciesA, "befriends") };
        Assert.Throws<ArgumentException>(() => WildMemory.For(decisions, SpeciesA));
    }

    [Fact]
    public void A_null_decisions_list_throws()
    {
        Assert.Throws<ArgumentNullException>(() => WildMemory.For(null!, SpeciesA));
    }

    [Fact]
    public void Memory_never_crosses_a_delve_boundary_a_fresh_delves_empty_log_has_no_opinion()
    {
        // The corrected behavior (see the class doc comment): a prior delve's own strong opinion
        // (three fights -- a betrayal, +1) has nothing to carry forward through, because there is no
        // table anywhere -- only the CALLER'S OWN rows are ever read, and a fresh delve calls this
        // with a fresh (here: empty) list. Simulating "delve 1 ends, delve 2 begins" as two SEPARATE
        // calls, the second with its own empty log, is the whole of what "no cross-delve persistence"
        // means for a pure function with no static state.
        var delveOneLog = new[] { new Row("wild", SpeciesA, WildMemory.ResolutionFight) };
        Assert.Equal(1, WildMemory.For(delveOneLog, SpeciesA)); // delve 1's own opinion, formed correctly

        var delveTwoLog = Array.Empty<Row>(); // delve 2's own decisions_json -- starts empty, always
        Assert.Equal(0, WildMemory.For(delveTwoLog, SpeciesA)); // no opinion carried over
    }
}
