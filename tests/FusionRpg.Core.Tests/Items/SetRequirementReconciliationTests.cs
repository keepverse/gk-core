using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// item/spec-set-requirement-reconciliation.md Testing strategy, module 25's Core half (P7.5): the
/// frozen envelope, the one-direction reconciliation, the static identity contract, and the pure
/// full-set witness. Every row recomputed from live inputs; no population count anywhere.
/// </summary>
public class SetRequirementReconciliationTests
{
    const long MidTheta = 50_000;   // inside the shipped tuning's own "mid" power band

    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    static RequirementProfileTuning Shipped() => RequirementProfileTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "equipment-requirements.v1.json")));

    /// <summary>A test-owned tuning with a REAL upkeep budget, a single drawable value per band, and
    /// one upkeep resource that is actually an actor pool — so the budget rule and the accept path are
    /// both testable without republishing the shipped file (whose budget is 0 today).</summary>
    static RequirementProfileTuning Budgeted(int budget = 100, int cost = 15) =>
        RequirementProfileTuningLoader.Parse($$"""
        {
          "schemaVersion": 1, "version": 1, "resolverRevision": 1, "tuningRevision": 1,
          "powerBands": [{ "id": "mid", "minP": 0, "maxP": 1000000 }],
          "focusBands": ["offense", "utility"],
          "profileMatrix": [
            { "rarity": "chaff", "powerBand": "mid", "focus": "offense", "weights": { "none": 1 } }
          ],
          "levelThresholds": { "mid": [{ "value": 10, "weight": 1 }] },
          "fixedThresholds": { "mid": [{ "value": 30, "weight": 1 }] },
          "ratioThresholds": { "mid": [{ "value": 250, "weight": 1 }] },
          "upkeepEligible": { "mid": true },
          "upkeepResources": [{ "id": "stamina", "weight": 1 }],
          "reserveBands": { "mid": [{ "value": 20, "weight": 1 }] },
          "costBands": { "mid": [{ "value": {{cost}}, "weight": 1 }] },
          "periodBands": { "mid": [{ "value": 100, "weight": 1 }] },
          "setEnvelopeBudget": {{budget}}
        }
        """);

    static readonly string[] Pool = { "Might", "Focus" };

    static SetRequirementPlan GeneralPlan(params RequirementProfileKind[] kinds)
    {
        var roles = new[] { ItemRole.ArmamentPrimary, ItemRole.CoreGuard, ItemRole.WardArray, ItemRole.Mantle, ItemRole.Girdle };
        var members = new List<SetMemberPlan>();
        for (var i = 0; i < kinds.Length; i++) members.Add(new SetMemberPlan(roles[i], kinds[i]));
        return new SetRequirementPlan("set.test", SetClass.General, members);
    }

    static SetRequirementEnvelope Resolve(
        SetRequirementPlan plan, ulong seed = 11, RequirementProfileTuning? tuning = null,
        long pTheta = MidTheta, IReadOnlyList<string>? pool = null) =>
        SetRequirementReconciler.Resolve(plan, seed, catalogRevision: 3, tuningRevision: 1, pTheta,
            pool ?? Pool, tuning ?? Shipped());

    // ── replay and order independence ───────────────────────────────────────────────

    [Fact]
    public void The_same_input_resolves_a_byte_identical_envelope()
    {
        var plan = GeneralPlan(RequirementProfileKind.Fixed, RequirementProfileKind.Ratio,
            RequirementProfileKind.Level, RequirementProfileKind.None, RequirementProfileKind.Sustained);
        var first = Resolve(plan, tuning: Budgeted());
        var second = Resolve(plan, tuning: Budgeted());

        Assert.Equal(first.SetId, second.SetId);
        Assert.Equal(first.ResolverRevision, second.ResolverRevision);
        Assert.Equal(first.CatalogRevision, second.CatalogRevision);
        Assert.Equal(first.TuningRevision, second.TuningRevision);
        Assert.Equal(first.IdentityRequirement, second.IdentityRequirement);
        Assert.Equal(first.FavoredAptitudeId, second.FavoredAptitudeId);
        Assert.Equal(first.FixedCeiling, second.FixedCeiling);
        Assert.Equal(first.RatioCeilingMilli, second.RatioCeilingMilli);
        Assert.Equal(first.UpkeepResourceId, second.UpkeepResourceId);
        Assert.Equal(first.UpkeepCostTotal, second.UpkeepCostTotal);
        Assert.Equal(first.UpkeepReserve, second.UpkeepReserve);
        Assert.Equal(first.UpkeepPeriodTicks, second.UpkeepPeriodTicks);
        Assert.Equal(first.MemberUpkeepTotal, second.MemberUpkeepTotal);
        Assert.Equal(first.Members, second.Members);   // element-wise: the frozen profiles themselves
    }

    [Fact]
    public void Member_and_pool_ordering_cannot_alter_the_envelope()
    {
        var forward = GeneralPlan(RequirementProfileKind.Fixed, RequirementProfileKind.Ratio,
            RequirementProfileKind.Level, RequirementProfileKind.None, RequirementProfileKind.Sustained);
        var flipped = forward with { Members = forward.Members.Reverse().ToList() };

        var a = Resolve(forward, tuning: Budgeted(), pool: new[] { "Focus", "Might", "Vigor" });
        var b = Resolve(flipped, tuning: Budgeted(), pool: new[] { "Vigor", "Might", "Focus" });

        Assert.Equal(a.FavoredAptitudeId, b.FavoredAptitudeId);
        Assert.Equal(a.Roles, b.Roles);
        Assert.Equal(a.Members, b.Members);
    }

    // ── one build / one resource direction ──────────────────────────────────────────

    [Fact]
    public void Every_non_empty_member_clause_uses_the_envelopes_one_aptitude_resource_and_period()
    {
        var envelope = Resolve(GeneralPlan(
            RequirementProfileKind.Fixed, RequirementProfileKind.Ratio, RequirementProfileKind.Jackpot,
            RequirementProfileKind.Sustained, RequirementProfileKind.Fixed), tuning: Budgeted());

        SetRequirementReconciler.RequireOneDirection(envelope);

        Assert.NotNull(envelope.FavoredAptitudeId);
        Assert.Equal("stamina", envelope.UpkeepResourceId);
        foreach (var member in envelope.Members)
        {
            if (member.Profile.BuildTrial is { } build) Assert.Equal(envelope.FavoredAptitudeId, build.AptitudeId);
            if (member.Profile.Upkeep is { } upkeep)
            {
                Assert.Equal(envelope.UpkeepResourceId, upkeep.ResourceId);
                Assert.Equal(envelope.UpkeepPeriodTicks, upkeep.PeriodTicks);
            }
        }
    }

    [Fact]
    public void Member_upkeep_stays_within_the_envelopes_tuning_owned_total()
    {
        // Five sustained members at 15 each = 75, inside a 100 ceiling.
        var within = Resolve(GeneralPlan(
            RequirementProfileKind.Sustained, RequirementProfileKind.Sustained, RequirementProfileKind.Sustained,
            RequirementProfileKind.Sustained, RequirementProfileKind.Sustained), tuning: Budgeted(budget: 100));
        Assert.Equal(75L, within.MemberUpkeepTotal);

        var rejection = Assert.Throws<SetRequirementUnsatisfiable>(() => Resolve(GeneralPlan(
            RequirementProfileKind.Sustained, RequirementProfileKind.Sustained, RequirementProfileKind.Sustained,
            RequirementProfileKind.Sustained, RequirementProfileKind.Sustained), tuning: Budgeted(budget: 70)));
        Assert.Equal("upkeep-budget-exceeded", rejection.Rule);
    }

    [Fact]
    public void The_shipped_tunings_zero_budget_refuses_every_sustained_member_by_name()
    {
        // NOT a pinned defect: this is the reconciler reading the shipped `setEnvelopeBudget` (0) and
        // saying so, which is the whole reason module 25 reconciles instead of assuming. The tuning
        // row is filed as ITEM-activation-1's sibling work.
        var shipped = Shipped();
        Assert.Equal(0L, shipped.SetEnvelopeBudget);

        var rejection = Assert.Throws<SetRequirementUnsatisfiable>(() =>
            Resolve(GeneralPlan(RequirementProfileKind.Sustained, RequirementProfileKind.None,
                RequirementProfileKind.None, RequirementProfileKind.None, RequirementProfileKind.None)));
        Assert.Equal("upkeep-budget-exceeded", rejection.Rule);
    }

    // ── static identity ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_static_identity_requirement_is_validated_per_class_and_refuses_typed_reasons()
    {
        Assert.Equal("general-carries-a-selector", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None) with { RequiredFamilyId = "f" })).Rule);
        Assert.Equal("family-selector-shape", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None) with
            { Class = SetClass.Family, RequiredSpeciesId = "s" })).Rule);
        Assert.Equal("unique-species-allows-hybrid", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None) with
            {
                Class = SetClass.UniqueSpecies, RequiredSpeciesId = "s", RequiresUniqueCreature = true,
                HybridEligibility = SetHybridEligibility.Allowed,
            })).Rule);
        Assert.Equal("duplicate-member-role", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            SetIdentityRequirement.Validate(new SetRequirementPlan("set.dup", SetClass.General, new[]
            {
                new SetMemberPlan(ItemRole.Mantle, RequirementProfileKind.None),
                new SetMemberPlan(ItemRole.Mantle, RequirementProfileKind.None),
            }))).Rule);

        var family = SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None) with
        { Class = SetClass.Family, RequiredFamilyId = "sol" });
        Assert.Equal(SetIdentityRefusal.None, family.Admits(new SetIdentityFacts("sol", null, false, true)));
        Assert.Equal(SetIdentityRefusal.FamilyMismatch, family.Admits(new SetIdentityFacts("luna", null, false, false)));

        var unique = SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None) with
        {
            Class = SetClass.UniqueSpecies, RequiredSpeciesId = "rose", RequiresUniqueCreature = true,
            HybridEligibility = SetHybridEligibility.Forbidden,
        });
        Assert.Equal(SetIdentityRefusal.None, unique.Admits(new SetIdentityFacts(null, "rose", true, false)));
        Assert.Equal(SetIdentityRefusal.UniqueCreatureRequired, unique.Admits(new SetIdentityFacts(null, "rose", false, false)));
        Assert.Equal(SetIdentityRefusal.SpeciesMismatch, unique.Admits(new SetIdentityFacts(null, "tulip", true, false)));
        // A hybrid is refused for BOTH unique templates — the fifteen-role and the ten-role one see the
        // same contract, because the requirement is a set fact, not a role-count fact.
        Assert.Equal(SetIdentityRefusal.HybridSetForbidden, unique.Admits(new SetIdentityFacts(null, "rose", true, true)));
    }

    [Fact]
    public void A_restrictive_member_shared_by_two_set_identities_blocks_before_any_member_seed()
    {
        var family = SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None) with
        { Class = SetClass.Family, RequiredFamilyId = "sol" });
        var other = SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None) with
        { Class = SetClass.Family, RequiredFamilyId = "luna" });
        var general = SetIdentityRequirement.Validate(GeneralPlan(RequirementProfileKind.None));

        var conflict = Assert.Throws<SetRequirementUnsatisfiable>(() => SetIdentityMembership.RequireNoConflict(
            "base.rose-mantle", new[] { ("set.a", family), ("set.b", other) }));
        Assert.Equal("set-identity-membership-conflict", conflict.Rule);

        // The same restriction twice, or a general set beside either, is not a conflict.
        SetIdentityMembership.RequireNoConflict("base.ok", new[] { ("set.a", family), ("set.c", family) });
        SetIdentityMembership.RequireNoConflict("base.ok", new[] { ("set.a", family), ("set.d", general) });
    }

    // ── ceilings, floors, named rejections ──────────────────────────────────────────

    [Fact]
    public void Floors_are_drawn_inside_the_bands_own_ceilings_and_a_mismatch_is_refused()
    {
        var envelope = Resolve(GeneralPlan(
            RequirementProfileKind.Fixed, RequirementProfileKind.Ratio, RequirementProfileKind.None,
            RequirementProfileKind.None, RequirementProfileKind.None));

        long maxFixed = 0;
        long maxRatio = 0;
        foreach (var row in Shipped().FixedThresholds["mid"]) if (row.Value > maxFixed) maxFixed = row.Value;
        foreach (var row in Shipped().RatioThresholds["mid"]) if (row.Value > maxRatio) maxRatio = row.Value;

        Assert.Equal(maxFixed, envelope.FixedCeiling);
        Assert.Equal(maxRatio, envelope.RatioCeilingMilli);
        foreach (var member in envelope.Members)
        {
            if (member.Profile.BuildTrial is { MinimumPoints: { } points }) Assert.InRange(points, 1, maxFixed);
            if (member.Profile.BuildTrial is { MinimumShareMilli: { } share }) Assert.InRange(share, 1, maxRatio);
        }
    }

    [Fact]
    public void Impossible_inputs_are_named_rejections_never_a_repaired_envelope()
    {
        Assert.Equal("power-negative", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            Resolve(GeneralPlan(RequirementProfileKind.None, RequirementProfileKind.None,
                RequirementProfileKind.None, RequirementProfileKind.None, RequirementProfileKind.None),
                pTheta: -1)).Rule);
        Assert.Equal("power-out-of-band", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            Resolve(GeneralPlan(RequirementProfileKind.None, RequirementProfileKind.None,
                RequirementProfileKind.None, RequirementProfileKind.None, RequirementProfileKind.None),
                tuning: Budgeted(), pTheta: 2_000_000)).Rule);
        Assert.Equal("revision-mismatch", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            SetRequirementReconciler.Resolve(GeneralPlan(RequirementProfileKind.None, RequirementProfileKind.None,
                RequirementProfileKind.None, RequirementProfileKind.None, RequirementProfileKind.None),
                11, 3, tuningRevision: 99, MidTheta, Pool, Shipped())).Rule);
        Assert.Equal("pool-missing", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            Resolve(GeneralPlan(RequirementProfileKind.Fixed, RequirementProfileKind.None,
                RequirementProfileKind.None, RequirementProfileKind.None, RequirementProfileKind.None),
                pool: Array.Empty<string>())).Rule);
        Assert.Equal("upkeep-ineligible", Assert.Throws<SetRequirementUnsatisfiable>(() =>
            Resolve(GeneralPlan(RequirementProfileKind.Sustained, RequirementProfileKind.None,
                RequirementProfileKind.None, RequirementProfileKind.None, RequirementProfileKind.None),
                pTheta: 500)).Rule);
    }

    // ── the pure witness and the full-set activation ────────────────────────────────

    [Fact]
    public void An_envelopes_own_witness_satisfies_every_member_and_activates_the_whole_set()
    {
        var envelope = Resolve(GeneralPlan(
            RequirementProfileKind.Fixed, RequirementProfileKind.Ratio, RequirementProfileKind.Level,
            RequirementProfileKind.None, RequirementProfileKind.Sustained), tuning: Budgeted());

        var witness = SetTrialEvaluator.TryWitness(envelope);
        Assert.NotNull(witness);
        Assert.True(SetTrialEvaluator.WitnessSatisfies(envelope, witness!.Value));

        // And the full-set evaluation itself, with every counted member active.
        var ready = SetTrialEvaluator.Evaluate(envelope, allCountedMembersActive: true,
            witness.Value.Level, witness.Value.Allocation);
        Assert.Equal(SetTrialOutcome.Ready, ready.Outcome);
        // The witness puts the WHOLE allocation on the one favored aptitude, which is what makes a
        // 1000‰ share — and therefore any legal per-mille floor — pass by construction.
        var aptitude = envelope.FavoredAptitudeId!;
        Assert.Equal(witness.Value.Allocation.Total(aptitude), witness.Value.Allocation.GrandTotal());
    }

    [Fact]
    public void An_unmet_member_and_an_unmet_envelope_read_as_two_different_verdicts()
    {
        var envelope = Resolve(GeneralPlan(
            RequirementProfileKind.Fixed, RequirementProfileKind.None, RequirementProfileKind.None,
            RequirementProfileKind.None, RequirementProfileKind.None));
        var witness = SetTrialEvaluator.TryWitness(envelope)!.Value;

        Assert.Equal(SetTrialOutcome.UnmetMember,
            SetTrialEvaluator.Evaluate(envelope, allCountedMembersActive: false, witness.Level, witness.Allocation).Outcome);

        var starved = SetTrialEvaluator.Evaluate(envelope, allCountedMembersActive: true,
            witness.Level, AptitudeAllocation.Empty);
        Assert.Equal(SetTrialOutcome.UnmetEnvelope, starved.Outcome);
        Assert.Equal(TrialUnmetClause.FixedAptitude, starved.Unmet);
    }

    [Fact]
    public void A_build_clause_with_no_favored_aptitude_is_unwitnessable_rather_than_fabricated()
    {
        var envelope = Resolve(GeneralPlan(
            RequirementProfileKind.Fixed, RequirementProfileKind.None, RequirementProfileKind.None,
            RequirementProfileKind.None, RequirementProfileKind.None)) with { FavoredAptitudeId = null };

        Assert.Null(SetTrialEvaluator.TryWitness(envelope));
    }
}
