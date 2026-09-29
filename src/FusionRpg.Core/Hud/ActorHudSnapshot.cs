namespace FusionRpg.Core.Hud;

/// <summary>Presentation DTO — view of Hot snapshot data at observe time, not a new SSOT store.</summary>
public sealed record ActorHudSnapshot(
    ActorHudIdentity Identity,
    ActorHudResources? Resources,
    IReadOnlyList<ActorHudStatusToken> Statuses,
    ActorHudOverflow Overflow,
    ActorHudElements? Elements = null);

public sealed record ActorHudIdentity(
    ActorHudTier Tier,
    string Role,
    int? LevelBand,
    IReadOnlyList<string> Flags);

public sealed record ActorHudResources(
    ActorHudShield? Shield,
    ActorHudHpSliver? HpSliver,
    IReadOnlyList<ActorHudMeter>? Meters);

public sealed record ActorHudShield(
    long Hp,
    long Max,
    IReadOnlyList<ActorHudShieldStack> Stacks);

public sealed record ActorHudShieldStack(
    string Element,
    long Hp,
    long Max);

public sealed record ActorHudHpSliver(double Ratio);

public sealed record ActorHudMeter(string Id, double Ratio);

public sealed record ActorHudStatusToken(
    string Id,
    bool Cc,
    MagnitudeBand MagnitudeBand);

public sealed record ActorHudOverflow(int StatusCount);

/// <summary>
/// Concrete species typing for a compact actor-HUD read. Values are catalog ids, never a second
/// gameplay element model: the lawn resolver already validates the primary/secondary slots.
/// </summary>
public sealed record ActorHudElements(string Primary, string? Secondary);
