using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

public class ArmouryGuardsTests
{
    [Fact]
    public void An_assigned_item_is_never_salvageable()
    {
        var preview = SalvageGuards.Preview(new[]
        {
            new SalvageCandidate("a", Assigned: true, Locked: false, InAnyLoadout: false, BestInRole: false),
        });

        Assert.Empty(preview.Eligible);
        Assert.Equal(SalvageExclusionReason.Assigned, Assert.Single(preview.Excluded).Reason);
    }

    [Fact]
    public void A_locked_item_is_never_salvageable_through_any_path()
    {
        var preview = SalvageGuards.Preview(new[]
        {
            new SalvageCandidate("a", Assigned: false, Locked: true, InAnyLoadout: false, BestInRole: false),
        });

        Assert.Empty(preview.Eligible);
        Assert.Equal(SalvageExclusionReason.Locked, Assert.Single(preview.Excluded).Reason);
    }

    [Fact]
    public void Loadout_membership_implies_lock()
    {
        var preview = SalvageGuards.Preview(new[]
        {
            new SalvageCandidate("a", Assigned: false, Locked: false, InAnyLoadout: true, BestInRole: false),
        });

        Assert.Empty(preview.Eligible);
        Assert.Equal(SalvageExclusionReason.LoadoutMember, Assert.Single(preview.Excluded).Reason);
    }

    [Fact]
    public void Best_in_role_items_are_excluded_from_bulk_by_default_and_named()
    {
        var preview = SalvageGuards.Preview(new[]
        {
            new SalvageCandidate("a", Assigned: false, Locked: false, InAnyLoadout: false, BestInRole: true),
        });

        Assert.Empty(preview.Eligible);
        var excluded = Assert.Single(preview.Excluded);
        Assert.Equal("a", excluded.InstanceId);
        Assert.Equal(SalvageExclusionReason.BestInRole, excluded.Reason);
    }

    [Fact]
    public void Best_in_role_can_be_explicitly_included()
    {
        var preview = SalvageGuards.Preview(new[]
        {
            new SalvageCandidate("a", Assigned: false, Locked: false, InAnyLoadout: false, BestInRole: true),
        }, includeBestInRole: true);

        Assert.Equal(new[] { "a" }, preview.Eligible);
        Assert.Empty(preview.Excluded);
    }

    [Fact]
    public void An_ordinary_item_is_eligible()
    {
        var preview = SalvageGuards.Preview(new[]
        {
            new SalvageCandidate("a", Assigned: false, Locked: false, InAnyLoadout: false, BestInRole: false),
        });

        Assert.Equal(new[] { "a" }, preview.Eligible);
        Assert.Empty(preview.Excluded);
    }

    [Fact]
    public void Each_guard_excludes_independently_and_the_report_names_every_excluded_item()
    {
        var preview = SalvageGuards.Preview(new[]
        {
            new SalvageCandidate("assigned", Assigned: true, Locked: false, InAnyLoadout: false, BestInRole: false),
            new SalvageCandidate("locked", Assigned: false, Locked: true, InAnyLoadout: false, BestInRole: false),
            new SalvageCandidate("loadout", Assigned: false, Locked: false, InAnyLoadout: true, BestInRole: false),
            new SalvageCandidate("best", Assigned: false, Locked: false, InAnyLoadout: false, BestInRole: true),
            new SalvageCandidate("fine", Assigned: false, Locked: false, InAnyLoadout: false, BestInRole: false),
        });

        Assert.Equal(new[] { "fine" }, preview.Eligible);
        Assert.Equal(4, preview.Excluded.Count);
        Assert.Contains(preview.Excluded, e => e.InstanceId == "assigned" && e.Reason == SalvageExclusionReason.Assigned);
        Assert.Contains(preview.Excluded, e => e.InstanceId == "locked" && e.Reason == SalvageExclusionReason.Locked);
        Assert.Contains(preview.Excluded, e => e.InstanceId == "loadout" && e.Reason == SalvageExclusionReason.LoadoutMember);
        Assert.Contains(preview.Excluded, e => e.InstanceId == "best" && e.Reason == SalvageExclusionReason.BestInRole);
    }
}
