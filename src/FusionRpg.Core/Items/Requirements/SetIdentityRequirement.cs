using FusionRpg.Core.Items.Thresholds;

namespace FusionRpg.Core.Items.Requirements;

/// <summary>
/// item/spec-set-requirement-reconciliation.md §"Static set identity requirement" — the closed set
/// classes. Three values, matching the spec's table exactly; a fourth is a reviewed change to the
/// spec, the identity check, and the Seedsmith metric together.
/// </summary>
public enum SetClass
{
    /// <summary>No identity selector. The ordinary role/frame/faction/level gate is the whole rule.</summary>
    General = 0,

    /// <summary>One declared <c>requiredFamilyId</c>; a hybrid is allowed.</summary>
    Family = 1,

    /// <summary>One declared <c>requiredSpeciesId</c> plus <c>requiresUniqueCreature</c>; a hybrid is
    /// forbidden before the member can be assigned.</summary>
    UniqueSpecies = 2,
}

/// <summary>Whether a hybrid wearer may carry this set's members. A closed vocabulary, matching the
/// spec's <c>hybridEligibility</c>.</summary>
public enum SetHybridEligibility
{
    Allowed = 0,
    Forbidden = 1,
}

/// <summary>The typed I11 refusals the spec names. <see cref="None"/> is the admitted case — never a
/// <c>null</c> and never a boolean, so a caller cannot read "refused" as "no data".</summary>
public enum SetIdentityRefusal
{
    None = 0,
    FamilyMismatch = 1,
    SpeciesMismatch = 2,
    UniqueCreatureRequired = 3,
    HybridSetForbidden = 4,
}

/// <summary>One member's intended profile kind, as the deterministic Set Topology Plan declares it.
/// The plan supplies WHICH kind; the envelope supplies the one aptitude, the one resource and the
/// ceilings the floor is drawn under — which is why no rarity-keyed kind table is read here (the
/// spec's own resolver signature carries no rarity).</summary>
public readonly record struct SetMemberPlan(ItemRole Role, RequirementProfileKind Kind);

/// <summary>
/// The Core-side shape of the spec's "deterministic Set Topology Plan" input: the set class and its
/// declared selectors, plus one intended kind per member role. It is an INPUT contract — this module
/// never produces it, and a plan that contradicts itself is refused by
/// <see cref="SetIdentityRequirement.Validate"/> rather than repaired.
/// </summary>
public sealed record SetRequirementPlan(
    string SetId,
    SetClass Class,
    IReadOnlyList<SetMemberPlan> Members,
    string? RequiredFamilyId = null,
    string? RequiredSpeciesId = null,
    bool RequiresUniqueCreature = false,
    SetHybridEligibility HybridEligibility = SetHybridEligibility.Allowed);

/// <summary>Who the wearer actually is, for the static identity check. Read from the catalog's own
/// facts, never from a display name, an id prefix, or a faction string.</summary>
public readonly record struct SetIdentityFacts(
    string? FamilyId,
    string? SpeciesId,
    bool IsUniqueCreature,
    bool IsHybrid);

/// <summary>A named refusal — a class/selector mismatch or an unknown selector blocks the set plan
/// before any member seed is written, per the spec's §"Static set identity requirement".</summary>
public sealed class SetRequirementUnsatisfiable : Exception
{
    public SetRequirementUnsatisfiable(string rule, string detail)
        : base($"set requirement unsatisfiable — {rule}: {detail}") => Rule = rule;

    public string Rule { get; }
}

/// <summary>
/// The frozen static restriction, resolved ONCE before member items are minted. It is a hard
/// assignment/projection rule (I11's), never a trial, never an upkeep clause, and never a set-bonus
/// activation condition — the spec draws that line by name, and an unmet family/species rule is never
/// represented as <c>set_trial_unmet</c>.
/// </summary>
public readonly record struct SetIdentityRequirement(
    string SetId,
    SetClass Class,
    string? RequiredFamilyId,
    string? RequiredSpeciesId,
    bool RequiresUniqueCreature,
    SetHybridEligibility HybridEligibility)
{
    /// <summary>
    /// Rule 1 of the spec's §Validation, as a pure function of the plan: <c>general</c> carries no
    /// selector, <c>family</c> exactly one known family, <c>unique-species</c> exactly one known
    /// unique-creature species plus <c>hybridEligibility = forbidden</c>. A class/selector mismatch, a
    /// missing selector, or a selector borrowed from the other class throws — never a fallback filter.
    /// </summary>
    public static SetIdentityRequirement Validate(SetRequirementPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.SetId))
            throw new SetRequirementUnsatisfiable("set-id-missing", "the plan names no set");
        if (plan.Members.Count == 0)
            throw new SetRequirementUnsatisfiable("members-missing", $"set '{plan.SetId}' declares no member role");

        var hasFamily = !string.IsNullOrWhiteSpace(plan.RequiredFamilyId);
        var hasSpecies = !string.IsNullOrWhiteSpace(plan.RequiredSpeciesId);

        switch (plan.Class)
        {
            case SetClass.General:
                if (hasFamily || hasSpecies || plan.RequiresUniqueCreature)
                    throw new SetRequirementUnsatisfiable("general-carries-a-selector",
                        $"set '{plan.SetId}' is general but declares a family/species selector or requires a unique creature");
                if (plan.HybridEligibility != SetHybridEligibility.Allowed)
                    throw new SetRequirementUnsatisfiable("general-forbids-hybrid",
                        $"set '{plan.SetId}' is general, whose contract is hybridEligibility = allowed");
                break;

            case SetClass.Family:
                if (!hasFamily || hasSpecies)
                    throw new SetRequirementUnsatisfiable("family-selector-shape",
                        $"set '{plan.SetId}' is family and must declare exactly one requiredFamilyId");
                if (plan.RequiresUniqueCreature)
                    throw new SetRequirementUnsatisfiable("family-requires-unique",
                        $"set '{plan.SetId}' is family and cannot require a unique creature");
                break;

            case SetClass.UniqueSpecies:
                if (!hasSpecies || hasFamily)
                    throw new SetRequirementUnsatisfiable("unique-species-selector-shape",
                        $"set '{plan.SetId}' is unique-species and must declare exactly one requiredSpeciesId");
                if (!plan.RequiresUniqueCreature)
                    throw new SetRequirementUnsatisfiable("unique-species-not-unique",
                        $"set '{plan.SetId}' is unique-species and must set requiresUniqueCreature");
                if (plan.HybridEligibility != SetHybridEligibility.Forbidden)
                    throw new SetRequirementUnsatisfiable("unique-species-allows-hybrid",
                        $"set '{plan.SetId}' is unique-species, whose contract forbids a hybrid");
                break;

            default:
                throw new SetRequirementUnsatisfiable("class-unreachable", $"unhandled set class {plan.Class}");
        }

        var seenRoles = new HashSet<ItemRole>();
        foreach (var member in plan.Members)
            if (!seenRoles.Add(member.Role))
                throw new SetRequirementUnsatisfiable("duplicate-member-role",
                    $"set '{plan.SetId}' declares role '{ItemRoles.Id(member.Role)}' twice — a set counts DISTINCT roles, so a duplicate cannot raise its own ceiling");

        return new SetIdentityRequirement(
            plan.SetId, plan.Class, plan.RequiredFamilyId, plan.RequiredSpeciesId,
            plan.RequiresUniqueCreature, plan.HybridEligibility);
    }

    /// <summary>The static admission decision. Order is the spec's table order: the unique-creature
    /// requirement, then the species, then the family, then the hybrid refusal — so a hybrid of the
    /// wrong species reads the same refusal twice over, deterministically.</summary>
    public SetIdentityRefusal Admits(in SetIdentityFacts facts)
    {
        if (Class == SetClass.UniqueSpecies)
        {
            if (!facts.IsUniqueCreature) return SetIdentityRefusal.UniqueCreatureRequired;
            if (HybridEligibility == SetHybridEligibility.Forbidden && facts.IsHybrid)
                return SetIdentityRefusal.HybridSetForbidden;
            if (!string.Equals(facts.SpeciesId, RequiredSpeciesId, StringComparison.Ordinal))
                return SetIdentityRefusal.SpeciesMismatch;
            return SetIdentityRefusal.None;
        }

        if (Class == SetClass.Family)
        {
            if (!string.Equals(facts.FamilyId, RequiredFamilyId, StringComparison.Ordinal))
                return SetIdentityRefusal.FamilyMismatch;
            return SetIdentityRefusal.None;
        }

        return SetIdentityRefusal.None;
    }
}
