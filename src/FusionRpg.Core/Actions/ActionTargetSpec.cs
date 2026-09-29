// buff-debuff-scope T1: ActionRelation/ActionRelations now alias the shared Contracts type so
// Core/Scope/WhoSelector.cs can reference the same Ally/Enemy/Any vocabulary without either
// namespace depending on the other. Same technique as TargetModeNames.cs's own AtomRng alias.
global using ActionRelation = FusionRpg.Contracts.RelationKind;
global using ActionRelations = FusionRpg.Contracts.RelationKinds;

namespace FusionRpg.Core.Actions;

/// <summary>
/// The six modes an action may target with (spec-targeting.md §1). `EventTarget`, `Actor`, and
/// `Selected` from the shipped <c>TargetSpec</c> are deliberately not exposed — they are
/// capture-path and debug modes, not action authoring.
/// </summary>
public enum ActionTargetMode
{
    /// <summary>Resolves to the caster's own ptr directly and never enters the resolver's pool.</summary>
    Self = 0,
    Single,
    Multi,

    /// <summary>
    /// One or more targets drawn uniformly via the seeded `target` RNG stream (T8). Named
    /// <c>RolledTarget</c> rather than the spec prose's "Random" — this directory's purity scan bans
    /// the literal token <c>Random</c> even as a bare identifier, since it cannot tell an enum member
    /// from a reference to <c>System.Random</c>.
    /// </summary>
    RolledTarget,

    All,

    /// <summary>Needs cells to enumerate — rejected at bind time while no board exists (spec-targeting.md §4).</summary>
    Area,
}

// ActionRelation (Self/Ally/Enemy/Any, resolved against the caster rather than an absolute side —
// spec-targeting.md §2) is now FusionRpg.Contracts.RelationKind, aliased at the top of this file.
// Compiled to at most two concrete `TargetSpec`s — one per caster side — so one authored action
// serves both factions without duplicate rows. Behaviour unchanged; see RelationKind.cs's own doc
// comment for why this moved.

/// <summary>`Area`-only shapes, matching the shipped resolver's `AreaShapes`.</summary>
public enum ActionAreaShape
{
    Row = 0,
    Column,
    Square,
    Rectangle,
}

/// <summary>
/// Where an `Area` anchors, distinct from the shipped resolver's `AnchorOrigin`
/// (`Corner`/`Center`), which is rectangle geometry rather than a source (spec-targeting.md §1).
/// </summary>
public enum ActionAnchorSource
{
    Caster = 0,
    PrimaryTarget,
    ChosenCell,
}

/// <summary>
/// The order candidates resolve in — a visible data value rather than two code paths that silently
/// disagree (spec-targeting.md §2a). New content defaults to `OrdinalPtr`; the basic attack is
/// authored `SourceOrder` so `A5` stays byte-identical to the engine's existing `SelectTarget`.
/// </summary>
public enum ActionTargetOrdering
{
    OrdinalPtr = 0,
    SourceOrder,
}

/// <summary>
/// The typed authoring contract compiled to the shipped `TargetResolver` (spec-targeting.md §1). No
/// strings compared at runtime, no dictionaries, no `object?` — the atom program's rule that content
/// must not reintroduce what atoms removed.
/// </summary>
public sealed record ActionTargetSpec
{
    public ActionTargetMode Mode { get; init; } = ActionTargetMode.Single;
    public ActionRelation Relation { get; init; } = ActionRelation.Enemy;

    /// <summary>For `Multi` / `RolledTarget`.</summary>
    public int? Count { get; init; }

    /// <summary>`Area` only.</summary>
    public ActionAreaShape? Shape { get; init; }
    public int? Size { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    public ActionAnchorSource AnchorSource { get; init; } = ActionAnchorSource.Caster;

    public ActionTargetFilters Filters { get; init; } = new();

    /// <summary>Capped by `CombatPolicy.ResolveMaxTargets`, as today.</summary>
    public int? MaxTargets { get; init; }

    public ActionTargetOrdering Ordering { get; init; } = ActionTargetOrdering.OrdinalPtr;
}

public static class ActionTargetModes
{
    public static string Name(ActionTargetMode mode) => mode switch
    {
        ActionTargetMode.Self => "self",
        ActionTargetMode.Single => "single",
        ActionTargetMode.Multi => "multi",
        ActionTargetMode.RolledTarget => "rolledTarget",
        ActionTargetMode.All => "all",
        ActionTargetMode.Area => "area",
        _ => "",
    };

    public static bool TryParse(string? text, out ActionTargetMode mode)
    {
        switch (text)
        {
            case "self": mode = ActionTargetMode.Self; return true;
            case "single": mode = ActionTargetMode.Single; return true;
            case "multi": mode = ActionTargetMode.Multi; return true;
            case "rolledTarget": mode = ActionTargetMode.RolledTarget; return true;
            case "all": mode = ActionTargetMode.All; return true;
            case "area": mode = ActionTargetMode.Area; return true;
            default: mode = default; return false;
        }
    }
}

// ActionRelations (Name/TryParse for the relation enum) is now FusionRpg.Contracts.RelationKinds,
// aliased at the top of this file. Same four string mappings, unchanged.

public static class ActionAreaShapes
{
    /// <summary>The wire name is the enum member name lowercased, derived rather than re-listed:
    /// <see cref="ActionAreaShape"/> owns the closed four-shape vocabulary, and
    /// <see cref="FusionRpg.Contracts.AreaShapes"/>'s consts are its PascalCase spelling (proved by
    /// <c>ActionTargetingTests.ActionAreaShape_names_are_the_AreaShapes_const_values</c>). A fifth shape
    /// added to the enum cannot be missed here.</summary>
    public static string Name(ActionAreaShape shape) =>
        Enum.GetName(shape)?.ToLowerInvariant() ?? "";

    public static bool TryParse(string? text, out ActionAreaShape shape)
    {
        shape = default;
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var name in Enum.GetNames<ActionAreaShape>())
        {
            if (!string.Equals(text, name.ToLowerInvariant(), StringComparison.Ordinal)) continue;
            return Enum.TryParse(name, out shape);
        }

        return false;
    }
}

public static class ActionAnchorSources
{
    public static string Name(ActionAnchorSource source) => source switch
    {
        ActionAnchorSource.Caster => "caster",
        ActionAnchorSource.PrimaryTarget => "primaryTarget",
        ActionAnchorSource.ChosenCell => "chosenCell",
        _ => "",
    };

    public static bool TryParse(string? text, out ActionAnchorSource source)
    {
        switch (text)
        {
            case "caster": source = ActionAnchorSource.Caster; return true;
            case "primaryTarget": source = ActionAnchorSource.PrimaryTarget; return true;
            case "chosenCell": source = ActionAnchorSource.ChosenCell; return true;
            default: source = default; return false;
        }
    }
}

public static class ActionTargetOrderings
{
    public static string Name(ActionTargetOrdering ordering) => ordering switch
    {
        ActionTargetOrdering.OrdinalPtr => "ordinalPtr",
        ActionTargetOrdering.SourceOrder => "sourceOrder",
        _ => "",
    };

    public static bool TryParse(string? text, out ActionTargetOrdering ordering)
    {
        switch (text)
        {
            case "ordinalPtr": ordering = ActionTargetOrdering.OrdinalPtr; return true;
            case "sourceOrder": ordering = ActionTargetOrdering.SourceOrder; return true;
            default: ordering = default; return false;
        }
    }
}
