namespace FusionRpg.Core.Match;

/// <summary>
/// Match aggregate root facets. BoardProjection folds spawn/die (W1-B); UniqueBindings W5.
/// </summary>
public sealed class MatchState
{
    public MatchPhase Phase { get; set; } = MatchPhase.Idle;
    public string? MatchKey { get; set; }
    public long Revision { get; set; }
    public BoardProjection Board { get; set; } = new();
    public CapPolicyConfig Caps { get; set; } = CapPolicyConfig.Defaults();
    public MatchDebugSessionFacet Debug { get; set; } = new();
    public MatchEffectSessionFacet Effect { get; set; } = new();
    public MatchUniqueBindingsFacet UniqueBindings { get; set; } = new();

    /// <summary>
    /// buff-debuff-scope T6: normalized ptrs currently mind-controlled, per the real `zombie.hypno`
    /// event. Nothing tracked this on the Core side before — `MatchRuntime.cs`'s own dispatch had only
    /// a placeholder comment for it (spec-membership-events.md's own audit correction).
    /// </summary>
    public HashSet<string> MindControl { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Placeholder until W2/W3 debug sync.</summary>
public sealed class MatchDebugSessionFacet
{
    public bool Active { get; set; }
    public string ScenarioId { get; set; } = "";
}

/// <summary>Placeholder until W2 ClearAll wiring.</summary>
public sealed class MatchEffectSessionFacet
{
    /// <summary>True while match expects an active Effect bag (InMatch / Paused).</summary>
    public bool SessionActive { get; set; }
}

/// <summary>Cold immutable view (Core-local until W3 observe DTO).</summary>
public sealed class MatchSnapshot
{
    public int ContractVersion { get; init; } = MatchRuntime.ContractVersion;
    public MatchPhase Phase { get; init; }
    public string? MatchKey { get; init; }
    public long Revision { get; init; }
    public int PlantCount { get; init; }
    public int ZombieCount { get; init; }
    public int BulletCount { get; init; }
    public BoardEntity[] Entities { get; init; } = Array.Empty<BoardEntity>();
    public bool DebugActive { get; init; }
    public string ScenarioId { get; init; } = "";
    public bool EffectSessionActive { get; init; }
    public CapPolicyConfig Caps { get; init; } = CapPolicyConfig.Defaults();

    /// <summary>Cold observe only — never allocate on hot Emit.</summary>
    public UniqueBinding[] Bindings { get; init; } = Array.Empty<UniqueBinding>();
}
