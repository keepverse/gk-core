namespace FusionRpg.Core.World.Growth;

/// <summary>
/// A sector-wide project (spec-sector-development.md §3, world-map W52) — raises the whole sector
/// (development, defense, capacity), never one slot's output, which is <see cref="WorldSlot.StructureId"/>'s
/// own job. Mirrors <see cref="StructureCatalog"/>'s exact shape (dictionary-backed, eager
/// <c>Validate()</c>, <c>IsKnown</c>/<c>Get</c> — `StructureCatalog.cs:48-140` is the template), and
/// deliberately its own catalog rather than a scope field bolted onto <c>StructureCatalog</c> — the
/// owner's own decision (2026-09-04, "two catalogs"): <c>RequiredSlotKind</c>/<c>YieldMultiplierMilli</c>
/// are meaningless on a sector-wide project, and a shared catalog with half its columns null for
/// every row of one kind is the shape that later grows a <c>switch</c>.
/// </summary>
public sealed record ProjectDef
{
    public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>
    /// Upfront cost, spent from the developing sector's own <see cref="WorldSector.LoamStock"/> —
    /// a project has no entity to spend a founding legion's <c>CarriedLoam</c> the way `build` does
    /// (`develop` carries no `EntityId`, spec-sector-development.md's command table: it belongs to
    /// the sector, not a legion), so the sector's own stock is the only pool a sector-wide order can
    /// draw from.
    /// </summary>
    public long CostMilli { get; init; }

    /// <summary>
    /// How many `Growth` passes this project takes before it completes — mirrors
    /// <see cref="WorldSlot.ConstructionTurnsRemaining"/>'s shape one level up, counted down in
    /// `Growth`, never `Production` (spec-sector-development.md §3's stated split: a structure counts
    /// down where it already does, `LoamPhases.DecrementConstruction`; a sector-wide project counts
    /// down here instead, so a completed project's effects are never visible to this same turn's
    /// `Production` pass, only the next one's).
    /// </summary>
    public int ProjectTurns { get; init; }

    /// <summary>
    /// What completing this project adds to the sector's <see cref="WorldSector.DevelopmentLevel"/>,
    /// once (world-map W53) — the "one line that makes the number mean something."
    /// </summary>
    public int DevelopmentBonus { get; init; }
}

/// <summary>What can be started on a sector you hold (spec-sector-development.md §3). Deliberately
/// content-light this wave — one placeholder row proves the mechanism (the catalog shape, the
/// `develop` command, the `Growth`-phase countdown and completion) the identical precedent
/// `StructureCatalog.cs:44-46` already states for its own placeholder row, before a later balance
/// pass gives this a real roster and real tuning.</summary>
public static class ProjectCatalog
{
    static IReadOnlyList<ProjectDef>? _all;
    static Dictionary<string, ProjectDef>? _byId;

    public static IReadOnlyList<ProjectDef> All => _all ??= Validate(Seed);

    public static bool IsKnown(string? projectId) =>
        projectId != null && ByIdMap().ContainsKey(projectId);

    public static ProjectDef Get(string projectId) =>
        ByIdMap().TryGetValue(projectId, out var def)
            ? def
            : throw new ArgumentException($"Unknown project id '{projectId}'.");

    /// <summary>
    /// <c>CostMilli</c>/<c>ProjectTurns</c>/<c>DevelopmentBonus</c> are provisional placeholders, not
    /// yet tuning-backed — matching `loam-source-placeholder`'s own <c>CostMilli = 0</c> precedent
    /// one file over. Every literal here (100, 1, 1) is in <c>audit-magic-numbers.py</c>'s own exempt
    /// set, so none of them are flagged as an un-tuned balance number by that audit; a later balance
    /// pass that adds a real roster is expected to route real rows through a named tuning accessor
    /// the same way `StructureCatalog`'s own well/waystation/granary rows already do.
    /// </summary>
    static readonly IReadOnlyList<ProjectDef> Seed = new ProjectDef[]
    {
        new()
        {
            ProjectId = "raise-development-placeholder",
            Name = "Raise Development (placeholder)",
            CostMilli = 100,
            ProjectTurns = 1,
            DevelopmentBonus = 1
        }
    };

    static Dictionary<string, ProjectDef> ByIdMap()
    {
        if (_byId == null)
        {
            _ = All;
            _byId = All.ToDictionary(p => p.ProjectId, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>Catalog discipline — a bad project row is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<ProjectDef> Validate(IReadOnlyList<ProjectDef> projects)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            WorldIds.RequireKebab(p.ProjectId, "Project id");
            if (!seenIds.Add(p.ProjectId))
                throw new InvalidOperationException($"Duplicate project id '{p.ProjectId}'.");
            if (string.IsNullOrWhiteSpace(p.Name))
                throw new InvalidOperationException($"Project '{p.ProjectId}' has no display name.");
            if (p.CostMilli < 0)
                throw new InvalidOperationException($"Project '{p.ProjectId}' has negative cost.");
            if (p.ProjectTurns < 0)
                throw new InvalidOperationException($"Project '{p.ProjectId}' has negative turns.");
            if (p.DevelopmentBonus < 0)
                throw new InvalidOperationException($"Project '{p.ProjectId}' has a negative development bonus.");
        }

        return projects;
    }
}
