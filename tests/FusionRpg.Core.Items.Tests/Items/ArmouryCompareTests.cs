using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

public class ArmouryCompareTests
{
    static AtomRow Atom(string family, string channel, string op = "flat") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1), KindId = "stat.modify", FamilyId = family,
        Variant = "", Tier = 1, Name = family,
        ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"{op}\",\"amount\":{{\"min\":10,\"max\":20}}}}",
    };

    static CompareAtom Rolled(AtomRow atom, int amount) =>
        new(atom, $"{{\"channel\":\"{ReadChannel(atom)}\",\"op\":\"flat\",\"amount\":{amount}}}");

    static string ReadChannel(AtomRow atom)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(atom.ParamsJson);
        return doc.RootElement.GetProperty("channel").GetString()!;
    }

    [Fact]
    public void Comparison_invents_no_scalar()
    {
        // SC9: the result type itself has no single "score" field -- there is nothing to invent.
        var props = typeof(CompareResult).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("Score", props);
        Assert.DoesNotContain("Rating", props);
        Assert.DoesNotContain("Power", props);
    }

    [Fact]
    public void A_strict_improvement_on_every_shared_channel_is_strictly_better()
    {
        var atk = Atom("atom.attack", "atk");
        var incumbent = new[] { Rolled(atk, 10) };
        var candidate = new[] { Rolled(atk, 20) };

        var result = ArmouryCompare.Compare(incumbent, candidate);

        Assert.Equal(DominanceVerdict.StrictlyBetter, result.Dominance);
        var delta = Assert.Single(result.Deltas);
        Assert.Equal(10, delta.Incumbent);
        Assert.Equal(20, delta.Candidate);
        Assert.Equal(10, delta.Delta);
    }

    [Fact]
    public void A_worse_value_on_the_only_shared_channel_is_strictly_worse()
    {
        var atk = Atom("atom.attack", "atk");
        var incumbent = new[] { Rolled(atk, 20) };
        var candidate = new[] { Rolled(atk, 10) };

        var result = ArmouryCompare.Compare(incumbent, candidate);

        Assert.Equal(DominanceVerdict.StrictlyWorse, result.Dominance);
    }

    [Fact]
    public void Mixed_direction_across_shared_channels_is_a_sidegrade()
    {
        var atk = Atom("atom.attack", "atk");
        var def = Atom("atom.defense", "def");
        var incumbent = new[] { Rolled(atk, 10), Rolled(def, 20) };
        var candidate = new[] { Rolled(atk, 20), Rolled(def, 10) };

        var result = ArmouryCompare.Compare(incumbent, candidate);

        Assert.Equal(DominanceVerdict.Sidegrade, result.Dominance);
        Assert.Equal(2, result.Deltas.Count);
    }

    [Fact]
    public void Disjoint_channel_sets_are_incomparable_not_a_sidegrade()
    {
        var atk = Atom("atom.attack", "atk");
        var def = Atom("atom.defense", "def");
        var incumbent = new[] { Rolled(atk, 10) };
        var candidate = new[] { Rolled(def, 10) };

        var result = ArmouryCompare.Compare(incumbent, candidate);

        Assert.Equal(DominanceVerdict.Incomparable, result.Dominance);
    }

    [Fact]
    public void Roll_quality_is_the_rolled_positions_percentile_between_min_and_max()
    {
        var atk = Atom("atom.attack", "atk"); // authored [10,20]
        var candidate = new[] { Rolled(atk, 15) }; // exact midpoint

        var result = ArmouryCompare.Compare(Array.Empty<CompareAtom>(), candidate);

        var quality = Assert.Single(result.RollQualities);
        Assert.Equal(500, quality.Milli);
        Assert.Equal(500, result.MeanRollQualityMilli);
    }

    [Fact]
    public void A_fixed_value_atom_reports_full_roll_quality()
    {
        var fixedAtom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.fixed", "", 1), KindId = "stat.modify",
            FamilyId = "atom.fixed", Variant = "", Tier = 1, Name = "Fixed",
            ParamsJson = """{"channel":"atk","op":"flat","amount":5}""",
        };
        var candidate = new[] { new CompareAtom(fixedAtom, """{"channel":"atk","op":"flat","amount":5}""") };

        var result = ArmouryCompare.Compare(Array.Empty<CompareAtom>(), candidate);

        Assert.Equal(1000, Assert.Single(result.RollQualities).Milli);
    }
}
