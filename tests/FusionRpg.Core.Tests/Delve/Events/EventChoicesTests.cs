using FusionRpg.Core.Delve.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.8 (spec-event-deck.md §6, "Choices, v1"): the fixed verb set, presentation, eligibility
/// and autopilot.</summary>
public class EventChoicesTests
{
    static EventOutcomeRow Outcome() => new("good", "staple", "none", Array.Empty<EventEffectRef>());

    static EventRow Row(string kind, string? supplyOverride) => new(
        EventId: "e1", Kind: kind, Theme: null, ClimateAffinity: null, RepeatScope: "per-delve",
        Eligibility: null, Outcomes: new[] { Outcome(), Outcome() }, SupplyOverride: supplyOverride, ChainRef: null);

    // ---- Presented ----

    [Fact]
    public void Presented_null_row_throws()
    {
        Assert.Throws<ArgumentNullException>(() => EventChoices.Presented(null!));
    }

    [Fact]
    public void A_plain_curio_with_no_override_presents_only_interact()
    {
        var verbs = EventChoices.Presented(Row("curio", supplyOverride: null));
        Assert.Equal(new[] { EventChoices.Interact }, verbs);
    }

    [Fact]
    public void A_curio_with_an_override_presents_use_then_interact()
    {
        var verbs = EventChoices.Presented(Row("curio", supplyOverride: "herbs"));
        Assert.Equal(new[] { EventChoices.Use, EventChoices.Interact }, verbs);
    }

    [Fact]
    public void A_story_with_no_override_presents_interact_then_leave()
    {
        var verbs = EventChoices.Presented(Row("story", supplyOverride: null));
        Assert.Equal(new[] { EventChoices.Interact, EventChoices.Leave }, verbs);
    }

    [Fact]
    public void A_story_with_an_override_presents_all_three_in_fixed_order()
    {
        var verbs = EventChoices.Presented(Row("story", supplyOverride: "key"));
        Assert.Equal(new[] { EventChoices.Use, EventChoices.Interact, EventChoices.Leave }, verbs);
    }

    // ---- IsEligible ----

    [Fact]
    public void Use_is_eligible_only_when_the_party_holds_the_override_tag()
    {
        Assert.True(EventChoices.IsEligible(EventChoices.Use, holdsOverrideTag: true));
        Assert.False(EventChoices.IsEligible(EventChoices.Use, holdsOverrideTag: false));
    }

    [Fact]
    public void Interact_and_leave_are_always_eligible_regardless_of_the_tag()
    {
        Assert.True(EventChoices.IsEligible(EventChoices.Interact, holdsOverrideTag: false));
        Assert.True(EventChoices.IsEligible(EventChoices.Leave, holdsOverrideTag: false));
    }

    [Fact]
    public void An_unrecognized_verb_throws()
    {
        Assert.Throws<ArgumentException>(() => EventChoices.IsEligible("gamble", holdsOverrideTag: true));
    }

    // ---- Autopilot ----

    [Fact]
    public void Autopilot_picks_interact_when_no_override_exists_at_all()
    {
        Assert.Equal(EventChoices.Interact, EventChoices.Autopilot(Row("curio", null), holdsOverrideTag: false));
    }

    [Fact]
    public void Autopilot_picks_use_when_the_tag_is_held()
    {
        Assert.Equal(EventChoices.Use, EventChoices.Autopilot(Row("curio", "herbs"), holdsOverrideTag: true));
    }

    [Fact]
    public void Autopilot_falls_through_to_interact_when_the_tag_is_not_held()
    {
        Assert.Equal(EventChoices.Interact, EventChoices.Autopilot(Row("curio", "herbs"), holdsOverrideTag: false));
    }

    [Fact]
    public void Autopilot_never_picks_leave_interact_always_wins_first()
    {
        // A real, verified property, not an assumption: interact is always presented and always
        // eligible, and always precedes leave in the fixed order -- so autopilot can never reach
        // leave at all, regardless of story/override state. Leave is reachable only by a STEERED
        // (player) choice, never autopilot's own.
        Assert.Equal(EventChoices.Interact, EventChoices.Autopilot(Row("story", null), holdsOverrideTag: false));
        // Even WITH an override presented, not holding the tag still lands on interact, never leave.
        Assert.Equal(EventChoices.Interact, EventChoices.Autopilot(Row("story", "key"), holdsOverrideTag: false));
    }
}
