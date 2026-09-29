namespace FusionRpg.Core.Actions;

/// <summary>
/// T30 (spec-action-catalog.md §2, §3): the immutable, compiled cache — keyed by <c>action_id</c>,
/// swapped wholesale on revision change, never mutated in place. Building an instance never touches
/// I/O or SQL (<c>guard-dal.ps1</c>); a caller hands it the already-loaded rows.
/// </summary>
public sealed class ActionCatalog
{
    readonly IReadOnlyDictionary<string, CompiledAction> _byId;

    ActionCatalog(IReadOnlyDictionary<string, CompiledAction> byId) => _byId = byId;

    public static ActionCatalog Empty { get; } = new(new Dictionary<string, CompiledAction>(0, StringComparer.Ordinal));

    public int Count => _byId.Count;

    public CompiledAction? Get(string actionId) => _byId.TryGetValue(actionId, out var action) ? action : null;

    public static ActionCatalog Build(IReadOnlyList<CompiledAction> compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var byId = new Dictionary<string, CompiledAction>(compiled.Count, StringComparer.Ordinal);
        foreach (var action in compiled) byId[action.ActionId] = action;
        return new ActionCatalog(byId);
    }
}

/// <summary>
/// Holds the currently-live <see cref="ActionCatalog"/> and swaps it atomically on a revision change
/// (spec §3: "a revision swap is atomic so a battle in flight keeps its catalog"). A new catalog is
/// always built OFF TO THE SIDE from <see cref="ActionCompiler"/> output, then made visible with one
/// reference write — a reader who already captured the old <see cref="ActionCatalog"/> reference
/// (immutable by construction) keeps reading it forever, never a half-swapped state.
/// </summary>
public sealed class ActionCatalogHost
{
    ActionCatalog _current = ActionCatalog.Empty;

    public ActionCatalog Current => Volatile.Read(ref _current);

    public void Swap(ActionCatalog next) => Volatile.Write(ref _current, next ?? throw new ArgumentNullException(nameof(next)));
}
