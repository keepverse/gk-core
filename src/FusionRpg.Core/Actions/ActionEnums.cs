using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions;

/// <summary>
/// The three action kinds (action-ideal.md §1, decisions 1/2/25; spec-action-model.md §1). Only
/// <see cref="Skill"/> costs loadout capacity — <see cref="Basic"/> and <see cref="Innate"/> are
/// never bound, so there is nothing for a cap to count.
/// </summary>
public enum ActionKind
{
    Basic = 0,
    Innate,
    Skill,
}

/// <summary>
/// The five closed action categories (H.3, spec-action-seeding.md §3) — a creature type's weight vector
/// (T31) is over these five, plus element/aspect bias, per that spec's own rule: "a creature type is a
/// weight vector over the five shipped action-categories… inventing a third vocabulary is the exact
/// defect the atom program exists to stop." Previously only <see cref="DerivedStatChannels"/>' bare
/// string constants (<see cref="DerivedStatChannels.ActionCategoryAttack"/> etc.) — this enum is typed
/// on top of them, never a second vocabulary: <see cref="ActionCategories.Name"/> returns those SAME
/// constants, so <c>skill.cooldown.{category}</c>/<c>skill.effectiveness.{category}</c> never move.
/// </summary>
public enum ActionCategory
{
    Attack = 0,
    Defense,
    Support,
    Movement,
    Status,
}

/// <summary>
/// The nine closed action tags (spec-action-model.md §2). `A7` selects on these and never on
/// internals — adding one is a reviewed change, because the stub AI's preference key reads this set.
///
/// <para><see cref="Construct"/> added base-defense `siege-construction` (owner decision, 2026-09-06):
/// the four structure-acquisition paths (`Built`/`Assembled`/`Summoned`/`Laboured`) are ordinary
/// actions under <see cref="ActionCategory.Support"/>, not a new category or a nested sub-category —
/// `Summoned` already fits the existing <see cref="Summon"/> tag; `Built`/`Assembled`/`Laboured` share
/// this one rather than reusing <see cref="Utility"/>, since "does this place a structure" is a real
/// distinction `A7`'s own selection and any future construction-specific filter would otherwise have
/// no tag to key on.</para>
/// </summary>
public enum ActionTag
{
    Offensive = 0,
    Defensive,
    Heal,
    Buff,
    Debuff,
    Movement,
    Summon,
    Utility,
    Construct,
}

/// <summary>
/// Which of an action's targets one of its atoms hits (spec-action-model.md §4). An atom with no
/// scope row defaults to <see cref="EachTarget"/>.
/// </summary>
public enum ActionEffectScope
{
    Caster = 0,
    PrimaryTarget,
    EachTarget,
    CasterAllies,
}

/// <summary>When an action's cost is paid (spec-action-model.md §3).</summary>
public enum ActionCostTiming
{
    OnCommit = 0,
    PerTick,
}

/// <summary>
/// A-E1 (spec-eligibility-axis.md §3.1): which tier's rule decides who may hold an action — exactly
/// the three the action-corpus program generates. A fourth value would encode a distinction
/// <see cref="ActionKind"/> already carries (A1's closure — see <see cref="EligibilityScopes"/>).
/// </summary>
public enum EligibilityScope
{
    General = 0,
    Family,
    Species,
}

/// <summary>
/// A-E1 (spec-eligibility-axis.md §3.0): whether a generated action sets up or cashes in a
/// conditional-payoff pairing (`EnablerPayoffPairings`). <c>None</c> is a real value, never an
/// omission — most actions pair with nothing, and the field must say so rather than being absent.
/// </summary>
public enum PairingRole
{
    None = 0,
    Enabler,
    Payoff,
}

public static class ActionKinds
{
    public static string Name(ActionKind kind) => kind switch
    {
        ActionKind.Basic => "basic",
        ActionKind.Innate => "innate",
        ActionKind.Skill => "skill",
        _ => "",
    };

    public static bool TryParse(string? text, out ActionKind kind)
    {
        switch (text)
        {
            case "basic": kind = ActionKind.Basic; return true;
            case "innate": kind = ActionKind.Innate; return true;
            case "skill": kind = ActionKind.Skill; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>Name/parse for <see cref="ActionCategory"/>, mapped onto <see cref="DerivedStatChannels"/>'
/// own existing constants rather than a second set of string literals.</summary>
public static class ActionCategories
{
    public static string Name(ActionCategory category) => category switch
    {
        ActionCategory.Attack => DerivedStatChannels.ActionCategoryAttack,
        ActionCategory.Defense => DerivedStatChannels.ActionCategoryDefense,
        ActionCategory.Support => DerivedStatChannels.ActionCategorySupport,
        ActionCategory.Movement => DerivedStatChannels.ActionCategoryMovement,
        ActionCategory.Status => DerivedStatChannels.ActionCategoryStatus,
        _ => "",
    };

    public static bool TryParse(string? text, out ActionCategory category)
    {
        switch (text)
        {
            case DerivedStatChannels.ActionCategoryAttack: category = ActionCategory.Attack; return true;
            case DerivedStatChannels.ActionCategoryDefense: category = ActionCategory.Defense; return true;
            case DerivedStatChannels.ActionCategorySupport: category = ActionCategory.Support; return true;
            case DerivedStatChannels.ActionCategoryMovement: category = ActionCategory.Movement; return true;
            case DerivedStatChannels.ActionCategoryStatus: category = ActionCategory.Status; return true;
            default: category = default; return false;
        }
    }

    /// <summary>All five, in declared order — matches <see cref="DerivedStatChannels.ActionCategories"/>.</summary>
    public static readonly IReadOnlyList<ActionCategory> All = new[]
    {
        ActionCategory.Attack, ActionCategory.Defense, ActionCategory.Support, ActionCategory.Movement, ActionCategory.Status,
    };
}

public static class ActionTags
{
    public static string Name(ActionTag tag) => tag switch
    {
        ActionTag.Offensive => "offensive",
        ActionTag.Defensive => "defensive",
        ActionTag.Heal => "heal",
        ActionTag.Buff => "buff",
        ActionTag.Debuff => "debuff",
        ActionTag.Movement => "movement",
        ActionTag.Summon => "summon",
        ActionTag.Utility => "utility",
        ActionTag.Construct => "construct",
        _ => "",
    };

    public static bool TryParse(string? text, out ActionTag tag)
    {
        switch (text)
        {
            case "offensive": tag = ActionTag.Offensive; return true;
            case "defensive": tag = ActionTag.Defensive; return true;
            case "heal": tag = ActionTag.Heal; return true;
            case "buff": tag = ActionTag.Buff; return true;
            case "debuff": tag = ActionTag.Debuff; return true;
            case "movement": tag = ActionTag.Movement; return true;
            case "summon": tag = ActionTag.Summon; return true;
            case "utility": tag = ActionTag.Utility; return true;
            case "construct": tag = ActionTag.Construct; return true;
            default: tag = default; return false;
        }
    }
}

public static class ActionEffectScopes
{
    public static string Name(ActionEffectScope scope) => scope switch
    {
        ActionEffectScope.Caster => "caster",
        ActionEffectScope.PrimaryTarget => "primaryTarget",
        ActionEffectScope.EachTarget => "eachTarget",
        ActionEffectScope.CasterAllies => "casterAllies",
        _ => "",
    };

    public static bool TryParse(string? text, out ActionEffectScope scope)
    {
        switch (text)
        {
            case "caster": scope = ActionEffectScope.Caster; return true;
            case "primaryTarget": scope = ActionEffectScope.PrimaryTarget; return true;
            case "eachTarget": scope = ActionEffectScope.EachTarget; return true;
            case "casterAllies": scope = ActionEffectScope.CasterAllies; return true;
            default: scope = default; return false;
        }
    }
}

public static class ActionCostTimings
{
    public static string Name(ActionCostTiming timing) => timing switch
    {
        ActionCostTiming.OnCommit => "onCommit",
        ActionCostTiming.PerTick => "perTick",
        _ => "",
    };

    public static bool TryParse(string? text, out ActionCostTiming timing)
    {
        switch (text)
        {
            case "onCommit": timing = ActionCostTiming.OnCommit; return true;
            case "perTick": timing = ActionCostTiming.PerTick; return true;
            default: timing = default; return false;
        }
    }
}

public static class EligibilityScopes
{
    public static string Name(EligibilityScope scope) => scope switch
    {
        EligibilityScope.General => "general",
        EligibilityScope.Family => "family",
        EligibilityScope.Species => "species",
        _ => "",
    };

    public static bool TryParse(string? text, out EligibilityScope scope)
    {
        switch (text)
        {
            case "general": scope = EligibilityScope.General; return true;
            case "family": scope = EligibilityScope.Family; return true;
            case "species": scope = EligibilityScope.Species; return true;
            default: scope = default; return false;
        }
    }
}

public static class PairingRoles
{
    public static string Name(PairingRole role) => role switch
    {
        PairingRole.None => "none",
        PairingRole.Enabler => "enabler",
        PairingRole.Payoff => "payoff",
        _ => "",
    };

    public static bool TryParse(string? text, out PairingRole role)
    {
        switch (text)
        {
            case "none": role = PairingRole.None; return true;
            case "enabler": role = PairingRole.Enabler; return true;
            case "payoff": role = PairingRole.Payoff; return true;
            default: role = default; return false;
        }
    }
}
