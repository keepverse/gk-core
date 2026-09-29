using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Activation;
using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Items.Thresholds;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// item/spec-set-requirement-reconciliation.md §"Full-set activation" — the set-tier half of module
/// 24's filter: atomic activation, atomic lapse, and a count that never moves.
/// </summary>
public class SetTierActivationTests
{
    static ThresholdGrant Grant(long count, params string[] wanted) => new(count, wanted);

    static SetRequirementEnvelope Envelope() => new(
        "set.test",
        ResolverRevision: 1,
        CatalogRevision: 3,
        TuningRevision: 1,
        IdentityRequirement: new SetIdentityRequirement("set.test", SetClass.General, null, null, false, SetHybridEligibility.Allowed),
        Members: new[]
        {
            new SetMemberProfile(ItemRole.Mantle,
                new RequirementProfile(null, new BuildTrialClause("Might", 5, null), null, RequirementProfileKind.Fixed)),
        },
        FavoredAptitudeId: "Might",
        FixedCeiling: 5,
        RatioCeilingMilli: null,
        UpkeepResourceId: null,
        UpkeepCostTotal: 0,
        UpkeepReserve: 0,
        UpkeepPeriodTicks: 0);

    [Fact]
    public void A_set_with_no_frozen_envelope_is_not_gated_at_all()
    {
        var activation = SetTierActivationPolicy.For(
            "set.test", envelope: null, SetTrialResult.Ready, Grant(3, "tier.a", "tier.b"));

        Assert.True(activation.Active);
        Assert.False(activation.Suppressed);
        Assert.Equal(new[] { "tier.a", "tier.b" }, activation.ActiveContainerIds);
        Assert.Empty(activation.SuppressedContainerIds);
        Assert.Equal(EquipmentActivationReasonKind.None, activation.Reason.Kind);
    }

    [Fact]
    public void A_passing_set_trial_activates_every_wanted_tier_together()
    {
        var activation = SetTierActivationPolicy.For(
            "set.test", Envelope(), SetTrialResult.Ready, Grant(4, "tier.2", "tier.3", "tier.4"));

        Assert.True(activation.Active);
        Assert.Equal(new[] { "tier.2", "tier.3", "tier.4" }, activation.ActiveContainerIds);
        Assert.Empty(activation.SuppressedContainerIds);
    }

    [Fact]
    public void A_dormant_set_holds_back_every_wanted_tier_together_and_keeps_its_count()
    {
        var grant = Grant(4, "tier.2", "tier.3", "tier.4");
        var activation = SetTierActivationPolicy.For(
            "set.test", Envelope(), SetTrialResult.UnmetMember, grant);

        Assert.False(activation.Active);
        Assert.True(activation.Suppressed);
        Assert.Empty(activation.ActiveContainerIds);
        Assert.Equal(new[] { "tier.2", "tier.3", "tier.4" }, activation.SuppressedContainerIds);
        Assert.Equal(grant.WantedContainerIds, activation.SuppressedContainerIds);   // same list, not a rewrite
        Assert.Equal(grant.Count, activation.Count);                                 // "3 / 4" is untouched
        Assert.Equal(EquipmentActivationReasonKind.SetTrialUnmet, activation.Reason.Kind);
        Assert.Equal(SetTrialOutcome.UnmetMember, activation.Trial.Outcome);
    }

    [Fact]
    public void An_unmet_envelope_clause_is_carried_so_the_projection_can_name_it()
    {
        var trial = SetTrialResult.UnmetEnvelope(TrialUnmetClause.FixedAptitude);
        var activation = SetTierActivationPolicy.For("set.test", Envelope(), trial, Grant(2, "tier.1"));

        Assert.True(activation.Suppressed);
        Assert.Equal(TrialUnmetClause.FixedAptitude, activation.Trial.Unmet);
        Assert.Equal(EquipmentActivationReasonKind.SetTrialUnmet, activation.Reason.Kind);
    }

    [Fact]
    public void A_set_lapses_and_recovers_atomically_with_the_count_never_moving()
    {
        var grant = Grant(4, "tier.2", "tier.3");
        var envelope = Envelope();

        var active = SetTierActivationPolicy.For("set.test", envelope, SetTrialResult.Ready, grant);
        var lapsed = SetTierActivationPolicy.For("set.test", envelope, SetTrialResult.UnmetMember, grant);
        var recovered = SetTierActivationPolicy.For("set.test", envelope, SetTrialResult.Ready, grant);

        Assert.True(active.Active);
        Assert.True(lapsed.Suppressed);
        Assert.Equal(active.ActiveContainerIds, recovered.ActiveContainerIds);
        Assert.Equal(active.Count, lapsed.Count);
        Assert.Equal(lapsed.Count, recovered.Count);
        Assert.Equal(4L, recovered.Count);
    }
}
