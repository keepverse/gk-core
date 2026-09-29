namespace FusionRpg.Core.PassiveTree.State;

/// <summary>
/// §2.1's five-row table (spec-tree-state.md, task C8) — what counts toward the rising unlock
/// price. STRUCTURAL, not tunable (`tunables-ssot.md` §1's test: "if changing it breaks whether the
/// system works rather than how the game feels, it is structural"). Each row is a `const` with the
/// comment citing its reason, so the table lives beside the code it governs rather than only in the
/// spec.
///
/// <para>These are documentation/reference constants, not a runtime switch: `spend(actor) =
/// cumulative(count of VALID owned nodes)` (§2) already reads exactly this row set by construction —
/// `RpgStore.LoadTreeState`'s row-presence-means-owned semantics (B5) already excludes what these
/// rows say to exclude (there is no "invalid" or "soul level" row in the owned-node dictionary to
/// begin with; soul level is a separate value per row, never counted as a node). This class exists so
/// a future caller computing "count of valid owned nodes" has the five reasons written down next to
/// the table it must reproduce, not just a spec citation.</para>
/// </summary>
public static class OwnershipRules
{
    /// <summary>Yes — the baseline case.</summary>
    public const bool SelfEarnedSkillPointsCount = true;

    /// <summary>Yes, while the granting item stays equipped — D11: the item paid for the node it is
    /// pricing. The alternative needs per-node provenance, which destroys D11's stated advantage
    /// ("points flow through the tree's own rules… no special case to define, enforce or test").</summary>
    public const bool ItemGrantedPointsCountWhileEquipped = true;

    /// <summary>No — a node left invalid by unequipping (D11's red state) grants nothing and costs
    /// nothing to hold. Otherwise unequipping would leave a player paying more for nodes that give
    /// them nothing: a pure penalty with no compensation, the exact trap D8's "self-spent only"
    /// amendment closed.</summary>
    public const bool InvalidUnequippedNodesCount = false;

    /// <summary>No — a different track (D3). Points unlock new bonuses, souls scale bonus power. Souls
    /// are unlimited by design (PS-8), so counting soul levels toward the unlock price would convert
    /// D25's soft economic bound into a genuine wall in a currency with no ceiling.</summary>
    public const bool SoulLevelsCount = false;

    /// <summary>Yes — nodes on the actor's OTHER trees count toward the SAME price ladder. That is the
    /// whole point: a per-tree count would make the first node of a 40th tree cost 5 again. At
    /// `Θ=100` the wallet affords ~31-41 nodes while the corpus is 39 trees × tier-1 width — a
    /// per-tree count would hand a spread build several times the breadth for the same budget.</summary>
    public const bool NodesOnOtherTreesCount = true;
}
