using System.Linq;
using FusionRpg.Core.Battle.Attrition;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// `solid-remediation` T4.10 — the lawn's permadeath ladder, keyed to damage taken.
///
/// <para><b>These tests assert the DIRECTION, never the constants.</b> The task is explicit: *"the
/// curve's direction is the design, its constants are not"*, and the tuning file marks every number
/// unmeasured. So nothing here pins a chance value — a balance pass must be able to move all four
/// numbers without turning this red. What must hold is monotonicity, the floors, and permanent death
/// sitting behind injury.</para>
/// </summary>
[Trait("VerificationId", "core.lawn-attrition-ladder")]
public class LawnPermadeathLadderTests
{
    static LawnAttritionTuning Shipped()
    {
        var path = FindTuning("lawn-attrition.v2.json");
        return LawnAttritionTuningLoader.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void More_damage_taken_is_never_less_chance()
    {
        // Monotonic across the whole domain, including past a full bar (an actor can be overkilled).
        var t = Shipped();
        var previous = -1;

        for (var taken = 0; taken <= 1500; taken += 25)
        {
            var chance = LawnPermadeathLadder.ChanceMilli(taken, t.PermadeathFloorMilli, t.PermadeathChanceAtFullMilli);
            Assert.True(chance >= previous,
                $"chance fell from {previous} to {chance} at {taken}‰ taken — the curve must never reward damage");
            previous = chance;
        }
    }

    [Fact]
    public void A_scratch_risks_nothing_and_a_full_bar_risks_the_tuned_maximum()
    {
        var t = Shipped();

        Assert.Equal(0, LawnPermadeathLadder.ChanceMilli(0, t.PermadeathFloorMilli, t.PermadeathChanceAtFullMilli));
        Assert.Equal(0, LawnPermadeathLadder.ChanceMilli(t.PermadeathFloorMilli, t.PermadeathFloorMilli, t.PermadeathChanceAtFullMilli));

        Assert.Equal(
            t.PermadeathChanceAtFullMilli,
            LawnPermadeathLadder.ChanceMilli(1000, t.PermadeathFloorMilli, t.PermadeathChanceAtFullMilli));
    }

    [Fact]
    public void Overkill_is_clamped_rather_than_extrapolated_past_the_tuned_endpoint()
    {
        // The curve was designed to 1000‰. Extrapolating past it would invent balance nobody authored.
        var t = Shipped();
        var atFull = LawnPermadeathLadder.ChanceMilli(1000, t.PermadeathFloorMilli, t.PermadeathChanceAtFullMilli);

        Assert.Equal(atFull, LawnPermadeathLadder.ChanceMilli(2500, t.PermadeathFloorMilli, t.PermadeathChanceAtFullMilli));
    }

    [Fact]
    public void A_member_risks_a_limb_before_it_risks_a_life()
    {
        // The designed ordering: injury's floor is lower, so there is a band where a member can be
        // hurt but not killed, and none where the reverse is true.
        var t = Shipped();
        Assert.True(t.PermadeathFloorMilli >= t.InjuryFloorMilli);

        // Sample the middle of the band between the two floors, not one per-mille above the lower one:
        // the curve is integer per-mille, so a single tick over a floor still truncates to zero. That
        // is a property of the shape, not a defect — but it makes the boundary the wrong place to ask
        // "is a member hurt here?".
        Assert.True(t.PermadeathFloorMilli > t.InjuryFloorMilli + 1,
            "the hurt-but-not-killed band is empty — this test has nothing to sample");

        var inTheBand = t.InjuryFloorMilli + (t.PermadeathFloorMilli - t.InjuryFloorMilli) / 2;
        var ladder = new LawnPermadeathLadder(t, inTheBand, rolledMilli: 0);

        Assert.True(ladder.InjuryChanceMilli() > 0);
        Assert.Equal(0, LawnPermadeathLadder.ChanceMilli(inTheBand, t.PermadeathFloorMilli, t.PermadeathChanceAtFullMilli));
    }

    [Fact]
    public void The_roll_decides_and_the_ladder_owns_no_randomness()
    {
        // Deterministic by construction: the caller draws, this compares. A replay with the same seed
        // settles identically, which is what keeps a permanent death reproducible rather than a
        // report of one.
        var t = Shipped();

        var certain = new LawnPermadeathLadder(t, damageTakenMilli: 1000, rolledMilli: 0);
        var impossible = new LawnPermadeathLadder(t, damageTakenMilli: 1000, rolledMilli: 999);

        Assert.True(certain.PermadeathApplies() || t.PermadeathChanceAtFullMilli == 0);
        Assert.False(impossible.PermadeathApplies());
    }

    [Fact]
    public void It_refuses_a_roll_outside_per_mille_and_negative_damage()
    {
        var t = Shipped();

        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnPermadeathLadder(t, 500, rolledMilli: 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnPermadeathLadder(t, 500, rolledMilli: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnPermadeathLadder(t, -1, rolledMilli: 0));
    }

    [Fact]
    public void The_loader_refuses_a_file_that_inverts_the_designed_direction()
    {
        // The constants are free to move; the direction is not. A file where a life is risked before a
        // limb is a content error, not a balance choice.
        var inverted = """
            {"schemaVersion":1,"version":1,
             "injuryFloorMilli":700,"injuryChanceAtFullMilli":400,
             "permadeathFloorMilli":250,"permadeathChanceAtFullMilli":150}
            """;

        var ex = Assert.Throws<LawnAttritionTuningRejection>(() => LawnAttritionTuningLoader.Parse(inverted));
        Assert.Contains("before it risks a life", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_file_parses_and_says_its_numbers_are_unmeasured()
    {
        // The task asks for working values "with an _meta note saying they are unmeasured". A future
        // reader must be able to tell these apart from measured ones, so the note is part of the
        // deliverable rather than a comment.
        var path = FindTuning("lawn-attrition.v2.json");
        var json = File.ReadAllText(path);

        Assert.NotNull(LawnAttritionTuningLoader.Parse(json));
        Assert.Contains("UNMEASURED", json, StringComparison.Ordinal);
    }

    static string FindTuning(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"no data/tuning/{fileName} above the test output");
    }
}
