using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W33 (spec-ai-commander.md §ValueMap): what ground is worth before anybody's needs are consulted.
///
/// A catalog rather than a switch, for the reason every catalog here exists: the mechanism audit
/// found this codebase growing parallel `switch (id)` blocks that drift apart. A slot kind added
/// without a value must be a startup error, never a silent zero that quietly makes new content
/// worthless to the AI.
/// </summary>
public class SlotValueCatalogTests
{
    [Fact]
    public void Every_slot_kind_the_game_has_is_worth_something_definite()
    {
        foreach (var kind in Enum.GetValues<SlotKind>())
            _ = SlotValueCatalog.Of(kind);   // throws if the table has a hole
    }

    [Fact]
    public void Asking_about_a_kind_that_does_not_exist_throws_rather_than_answering_zero()
    {
        // Unreachable while the table is complete — which is exactly why it needs asking directly.
        // A silent zero here would make a slot kind added later worth nothing to the AI, and nothing
        // anywhere would say so.
        Assert.Throws<KeyNotFoundException>(() => SlotValueCatalog.Of((SlotKind)999));
    }

    [Fact]
    public void A_table_with_a_hole_in_it_is_a_startup_error()
    {
        var missing = new Dictionary<SlotKind, int> { [SlotKind.Seat] = 1000 };

        var error = Assert.Throws<InvalidOperationException>(() => SlotValueCatalog.Validate(missing));
        Assert.Contains("no value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ground_is_worth_nothing_at_worst_and_never_less()
    {
        // A negative here would double-count: a hazard already costs you by occupying a slot that
        // could have been something. Letting it score against you as well would make a sector with
        // one hazard rank below empty ground, which is not true.
        var negative = Enum.GetValues<SlotKind>().ToDictionary(k => k, _ => 0);
        negative[SlotKind.Hazard] = -100;

        Assert.Throws<InvalidOperationException>(() => SlotValueCatalog.Validate(negative));
    }

    [Fact]
    public void A_seat_is_the_most_valuable_thing_a_sector_can_hold()
    {
        // Supply starts at a Seat; an empire without one has no chain at all. Nothing else is close.
        var seat = SlotValueCatalog.Of(SlotKind.Seat);

        foreach (var kind in Enum.GetValues<SlotKind>())
            if (kind != SlotKind.Seat)
                Assert.True(SlotValueCatalog.Of(kind) < seat, $"{kind} should rank below a Seat");
    }

    [Fact]
    public void Something_that_produces_outranks_ground_you_would_have_to_build_on()
    {
        Assert.True(SlotValueCatalog.Of(SlotKind.EssenceDeposit) > SlotValueCatalog.Of(SlotKind.Wildland));
        Assert.True(SlotValueCatalog.Of(SlotKind.MaterialSeam) > SlotValueCatalog.Of(SlotKind.Wildland));
    }

    [Fact]
    public void A_uniform_need_multiplies_nothing_away()
    {
        // The stub has to be *neutral*, not zero: shaped like the real thing so the AI gets smarter
        // the day stockpiles land, with no change to any AI code.
        foreach (var kind in Enum.GetValues<SlotKind>())
            Assert.Equal(UniformNeeds.Neutral, UniformNeeds.Instance.ForSlotKind(kind));

        Assert.Equal(UniformNeeds.Neutral, UniformNeeds.Instance.ForElement(null));
        Assert.Equal(UniformNeeds.Neutral, UniformNeeds.Instance.ForElement(ElementTypeId.Fire));
    }
}
