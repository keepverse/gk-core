namespace FusionRpg.Core.Actions.Aura;

/// <summary>
/// aura-skill T13 (`spec-aura-action-shape.md` §5.1): the typed, visible outcome of enabling an aura —
/// never a silent no-op. GG-55 requires never disabling without saying why, and *"enabling Might
/// switched off Fortitude" is the same class of information* as a typed usability refusal
/// (`UsabilityReason.CannotAfford`, `OnCooldown`) and must reach the player through the same channel.
/// </summary>
public sealed record AuraEnableResult(
    bool Enabled,
    string? EvictedAuraId,
    UsabilityReason? Refusal,
    string? RefusalDetail)
{
    public static readonly AuraEnableResult EnabledClean = new(true, null, null, null);

    public static AuraEnableResult EnabledWithEviction(string evictedAuraId) =>
        new(true, evictedAuraId, null, null);

    public static AuraEnableResult Refuse(UsabilityReason reason, string? detail = null) =>
        new(false, null, reason, detail);
}
