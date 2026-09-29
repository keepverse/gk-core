using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L26 / spec-loam-legions.md's "found by a harness, not guessed here" resolution — the same
/// L9-style discipline `EconomyHarnessTests` gave `LoamPolicy`'s sector constants, applied to
/// <see cref="LoamPolicy.CarryPerBearer"/> and <see cref="LoamPolicy.BurnPerMember"/>: the ideal
/// argues a 4-8 turn leash as a target, not a rule, so this tunes against representative legion
/// compositions rather than picking the constants and hoping.
/// </summary>
public class LegionSupplyEconomyTests
{
    readonly ITestOutputHelper _out;
    public LegionSupplyEconomyTests(ITestOutputHelper output) => _out = output;

    static WorldEntity Legion(int fighters, int bearers) => new()
    {
        EntityId = "legion", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave", AtSectorId = "home",
        Members = Enumerable.Range(0, fighters)
            .Select(_ => new WorldEntityMember { SpeciesId = "grunt", Role = WorldEntityMemberRole.Fighter })
            .Concat(Enumerable.Range(0, bearers)
                .Select(_ => new WorldEntityMember { SpeciesId = "grunt", Role = WorldEntityMemberRole.Bearer }))
            .ToList()
    };

    /// <summary>Representative party shapes, not exhaustive — enough to bound the constants against the target.</summary>
    public static IEnumerable<object[]> RepresentativeCompositions()
    {
        yield return new object[] { "small, one bearer", 3, 1 };
        yield return new object[] { "medium, two bearers", 4, 2 };
        yield return new object[] { "large, two bearers", 6, 2 };
    }

    [Theory]
    [MemberData(nameof(RepresentativeCompositions))]
    public void Representative_compositions_leash_inside_the_4_to_8_turn_target(string label, int fighters, int bearers)
    {
        var legion = Legion(fighters, bearers);
        var leash = LegionSupply.LeashTurns(legion);

        _out.WriteLine($"{label}: {fighters} fighters + {bearers} bearers -> leash {leash}");
        Assert.NotNull(leash);
        Assert.InRange(leash!.Value, 4, 8);
    }

    [Fact]
    public void Leash_is_exact_and_reproducible_for_known_inputs()
    {
        var legion = Legion(fighters: 2, bearers: 1);

        Assert.Equal(LoamPolicy.CarryPerBearer, LegionSupply.Capacity(legion));
        Assert.Equal(3 * LoamPolicy.BurnPerMember, LegionSupply.Burn(legion));
        Assert.Equal(LegionSupply.LeashTurns(legion), LegionSupply.LeashTurns(legion));

        var expectedLeash = (int)(LoamPolicy.CarryPerBearer / (3 * LoamPolicy.BurnPerMember));
        Assert.Equal(expectedLeash, LegionSupply.LeashTurns(legion));
    }

    [Fact]
    public void A_bearer_heavy_legion_reaches_farther_than_a_bearer_light_one_of_equal_size()
    {
        var bearerLight = Legion(fighters: 5, bearers: 1);
        var bearerHeavy = Legion(fighters: 3, bearers: 3);

        Assert.Equal(6, bearerLight.Members.Count);
        Assert.Equal(6, bearerHeavy.Members.Count);

        var lightLeash = LegionSupply.LeashTurns(bearerLight)!.Value;
        var heavyLeash = LegionSupply.LeashTurns(bearerHeavy)!.Value;

        Assert.True(heavyLeash > lightLeash,
            $"bearer-heavy leash ({heavyLeash}) should exceed bearer-light leash ({lightLeash}) at equal headcount");
    }

    [Fact]
    public void Equal_bearer_count_different_total_size_shares_capacity_but_not_burn()
    {
        // The exact degeneracy the spec exists to avoid: if capacity scaled with headcount too,
        // these two legions (same bearer count, different total size) would leash identically.
        var lean = Legion(fighters: 4, bearers: 2);   // 6 total
        var bloated = Legion(fighters: 8, bearers: 2); // 10 total

        Assert.Equal(LegionSupply.Capacity(lean), LegionSupply.Capacity(bloated));
        Assert.NotEqual(LegionSupply.Burn(lean), LegionSupply.Burn(bloated));
        Assert.NotEqual(LegionSupply.LeashTurns(lean), LegionSupply.LeashTurns(bloated));
    }

    [Fact]
    public void An_all_fighter_legion_has_zero_capacity_and_no_reserve_beyond_supply()
    {
        var legion = Legion(fighters: 4, bearers: 0);

        Assert.Equal(0, LegionSupply.Capacity(legion));
        Assert.Equal(0, LegionSupply.LeashTurns(legion));
    }

    [Fact]
    public void An_empty_legion_has_no_leash_to_speak_of()
    {
        var legion = Legion(fighters: 0, bearers: 0);

        Assert.Null(LegionSupply.LeashTurns(legion));
    }

    // ---- The march-loam gate's soft half (spec-loam-ai.md): TurnsUntilExhausted -----------------

    [Fact]
    public void TurnsUntilExhausted_reads_what_is_actually_carried_not_the_full_tank_ceiling()
    {
        var legion = Legion(fighters: 2, bearers: 1) with { CarriedLoam = LegionSupply.Burn(Legion(2, 1)) * 3 };

        Assert.Equal(3, LegionSupply.TurnsUntilExhausted(legion));
        Assert.NotEqual(LegionSupply.LeashTurns(legion), LegionSupply.TurnsUntilExhausted(legion));
    }

    [Fact]
    public void A_legion_already_carrying_less_than_one_turns_burn_shows_zero_turns_left()
    {
        var legion = Legion(fighters: 2, bearers: 1) with { CarriedLoam = 1 };
        Assert.Equal(0, LegionSupply.TurnsUntilExhausted(legion));
    }

    [Fact]
    public void An_empty_legion_has_no_runway_figure_to_speak_of()
    {
        var legion = Legion(fighters: 0, bearers: 0) with { CarriedLoam = 500 };
        Assert.Null(LegionSupply.TurnsUntilExhausted(legion));
    }
}
