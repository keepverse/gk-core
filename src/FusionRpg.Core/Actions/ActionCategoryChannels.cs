using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions;

/// <summary>
/// T59.1 (spec-action-instance-and-grant.md §2, narrowed during BUILD): which
/// `skill.cooldown.{category}` / `skill.effectiveness.{category}` channel each `ActionCategory` reads
/// (`ActionEnvelope.CooldownChannel`/`EffectivenessChannel`). Neither `ActionCompiler.Compile` nor
/// `ActionTimingDerivation.Derive` ever sets either field — confirmed by reading both — so every action
/// compiled through the real production path (`RpgStore.BuildActionCatalog`) carries both `null` today,
/// unless the row's own authored `Envelope` already set them. A structural naming mapping, not a
/// balance number (a balance pass tunes what a channel's modifiers are worth, never which channel name
/// an action reads) — an exhaustive switch, not a tuning-file row: a new `ActionCategory` member fails
/// to compile here rather than silently mapping to no channel.
/// </summary>
public static class ActionCategoryChannels
{
    public static string CooldownChannelFor(ActionCategory category) => category switch
    {
        ActionCategory.Attack => DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategoryAttack),
        ActionCategory.Defense => DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategoryDefense),
        ActionCategory.Support => DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategorySupport),
        ActionCategory.Movement => DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategoryMovement),
        ActionCategory.Status => DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategoryStatus),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "every ActionCategory member must map to a cooldown channel"),
    };

    public static string EffectivenessChannelFor(ActionCategory category) => category switch
    {
        ActionCategory.Attack => DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryAttack),
        ActionCategory.Defense => DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryDefense),
        ActionCategory.Support => DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport),
        ActionCategory.Movement => DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryMovement),
        ActionCategory.Status => DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryStatus),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "every ActionCategory member must map to an effectiveness channel"),
    };
}
