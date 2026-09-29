using System.Linq;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>aura-skill T11: per-source provenance retained alongside `DerivedComposer.Compose` — GG-49
/// ("why did my attack drop") is unanswerable without this today, since `Compose` folds a modifier
/// list into one number per channel and the per-source breakdown is gone the moment it returns.</summary>
public class DerivedContributionBagTests
{
    [Fact]
    public void Two_sources_on_one_channel_stay_two_entries_never_merged()
    {
        var mods = new[]
        {
            new DerivedModifier("combat.power.fire", DerivedModifierOp.Flat, 30.0, SourceId: "aura:ember"),
            new DerivedModifier("combat.power.fire", DerivedModifierOp.Flat, 17.0, SourceId: "commander:allocation"),
        };

        var bag = DerivedContributionBag.From(mods);
        var contributions = bag.ContributionsFor("combat.power.fire");

        Assert.Equal(2, contributions.Count);
        Assert.Contains(contributions, c => c.SourceId == "aura:ember" && c.Value == 30.0);
        Assert.Contains(contributions, c => c.SourceId == "commander:allocation" && c.Value == 17.0);
    }

    [Fact]
    public void ContributionsFor_answers_GG_49_naming_every_source_and_its_value()
    {
        // "Why did my attack drop?" -- a negative contribution from a debuff, positive from a buff,
        // both individually visible rather than pre-summed away.
        var mods = new[]
        {
            new DerivedModifier("combat.power.omni", DerivedModifierOp.Flat, 50.0, SourceId: "gear:sword"),
            new DerivedModifier("combat.power.omni", DerivedModifierOp.Flat, -80.0, SourceId: "status:weaken"),
        };

        var bag = DerivedContributionBag.From(mods);
        var contributions = bag.ContributionsFor("combat.power.omni");

        Assert.Equal(2, contributions.Count);
        Assert.Equal(-80.0, contributions.Single(c => c.SourceId == "status:weaken").Value);
        Assert.Equal(50.0, contributions.Single(c => c.SourceId == "gear:sword").Value);
    }

    [Fact]
    public void An_untouched_channel_returns_an_empty_list_never_null_never_throws()
    {
        var bag = DerivedContributionBag.From(Array.Empty<DerivedModifier>());
        var contributions = bag.ContributionsFor("combat.power.omni");

        Assert.NotNull(contributions);
        Assert.Empty(contributions);
    }

    [Fact]
    public void Different_channels_stay_fully_independent()
    {
        var mods = new[]
        {
            new DerivedModifier("combat.power.fire", DerivedModifierOp.Flat, 10.0, SourceId: "a"),
            new DerivedModifier("combat.defense.omni", DerivedModifierOp.Flat, 5.0, SourceId: "b"),
        };

        var bag = DerivedContributionBag.From(mods);

        Assert.Single(bag.ContributionsFor("combat.power.fire"));
        Assert.Single(bag.ContributionsFor("combat.defense.omni"));
        Assert.Empty(bag.ContributionsFor("combat.power.ice"));
    }

    [Fact]
    public void Records_the_op_too_not_just_source_and_value()
    {
        var mods = new[]
        {
            new DerivedModifier("progression.power", DerivedModifierOp.Replace, 3.0, SourceId: "override"),
        };

        var bag = DerivedContributionBag.From(mods);
        var contribution = Assert.Single(bag.ContributionsFor("progression.power"));

        Assert.Equal(DerivedModifierOp.Replace, contribution.Op);
    }

    [Fact]
    public void A_contribution_a_channels_compose_kind_never_reads_still_shows_up_here_D6_transparency()
    {
        // This bag answers "what tried to contribute," not "what the fold used" -- it does not
        // re-implement DerivedComposer.ComposeChannel's per-kind op filtering (that stays the fold's
        // own job, and AtomRowValidator/T2 is what rejects this shape at bind time for authored
        // content). An "increased" op on a channel that would actually compose as FlatSum (reads only
        // Flat) is still visible here, honestly, as a recorded attempt.
        var mods = new[]
        {
            new DerivedModifier("combat.power.fire", DerivedModifierOp.Increased, 999.0, SourceId: "mismatched-op"),
        };

        var bag = DerivedContributionBag.From(mods);
        var contribution = Assert.Single(bag.ContributionsFor("combat.power.fire"));

        Assert.Equal("mismatched-op", contribution.SourceId);
        Assert.Equal(DerivedModifierOp.Increased, contribution.Op);
    }

    [Fact]
    public void Channels_lists_every_channel_that_has_at_least_one_contribution()
    {
        var mods = new[]
        {
            new DerivedModifier("combat.power.fire", DerivedModifierOp.Flat, 1.0, SourceId: "a"),
            new DerivedModifier("combat.defense.omni", DerivedModifierOp.Flat, 1.0, SourceId: "b"),
        };

        var bag = DerivedContributionBag.From(mods);

        Assert.Equal(2, bag.Channels.Count);
        Assert.Contains("combat.power.fire", bag.Channels);
        Assert.Contains("combat.defense.omni", bag.Channels);
    }
}
