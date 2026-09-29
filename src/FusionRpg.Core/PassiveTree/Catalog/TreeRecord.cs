namespace FusionRpg.Core.PassiveTree.Catalog;

/// <summary>Which roster a tree came from. Five, per ruling R7 — species is a category, not a
/// variant. `tree-plan` emits only the first four today; the importer maps its tokens
/// (`aptitude`->`Primary`, `creatureFamily`->`Family`) and refuses any token outside the map, naming
/// it (spec-tree-catalog.md §2.1).</summary>
public enum TreeCategory
{
    Primary,
    Elemental,
    Status,
    Family,
    Species,
}

/// <summary>
/// One per tree (spec-tree-catalog.md §2.1). The catalog stores coefficients, never magnitudes —
/// `TreeRecord` itself carries no number a balance pass would move; `tiers`/`branches` are
/// structural (D29/D10), and `nodesPerTier` is generated data from the plan, not a constant.
///
/// <para><c>gateQuantity</c> is stored regardless of whether a producer exists for it yet (D37):
/// a tree naming <c>element_mastery</c> or <c>status_applied.&lt;id&gt;</c> is WAITING, not
/// orphaned, and the load path never disables a tree for it — see
/// <see cref="PassiveTreeCatalogLoader"/>.</para>
/// </summary>
public sealed record TreeRecord(
    string TreeId,
    TreeCategory Category,
    string GateQuantity,
    string ShapeArchetype,
    int Tiers,
    int Branches,
    IReadOnlyList<int> NodesPerTier,
    int CatalogVersion,
    bool Enabled,
    // seedsmith-content-standard, passive-tree-identity-content (2026-09-08): the tree's own
    // real generated display name/description (adapters/trees/identity), additive and nullable —
    // a tree the identity stage has not reached yet is a real, valid state, never fabricated.
    string? Name = null,
    string? Description = null);
