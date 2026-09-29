namespace FusionRpg.Core.PassiveTree.State;

/// <summary>One tree's `(n_i, s_i)` — self-bought node count and self-spent soul levels
/// (spec-tree-state.md §2.4, D8/D39). Both `long`, both `&gt;= 0` by construction (a count and a sum
/// of non-negative soul levels can never go negative).</summary>
public readonly record struct TreeSelfSpent(long NodeCount, long SoulLevels);

/// <summary>
/// The `selfSpent` projection `tree-resolve`'s `H`/`F` read (spec-tree-state.md §2.4;
/// spec-tree-resolve.md §5.2 states the same four rules so neither module can drift, task C7).
///
/// <para><b>D39 fixes what the quantity is: the FINAL ALLOCATION the actor holds, self-spent only</b>
/// — never points paid, never the purchase order. `RpgStore.LoadTreeState`/`LoadTreeStateBatch`
/// already return exactly that: a set of owned node ids and a soul level each, with no purchase
/// history retained anywhere (§1.1's "row presence means owned"). The projection here is therefore a
/// pure COUNT over rows, order-free for the same reason the store's own read is: the row set is a
/// property of the build, not of the route to it.</para>
///
/// <para><b>Rule 4 (item-granted / aptitude-threshold / creature-aspect exclusion) has nothing to
/// exclude today</b> — stated exactly, not glossed over (spec-tree-state.md §2.4's own note): no
/// source other than the player's own spend can add a tree node right now
/// (`SkillPointsPerThetaMilli` has zero production consumers, and aptitude-threshold/creature-aspect
/// skill-point grants exist in D2's list and nowhere in `src/`). So `RpgStore`'s owned-node dictionary
/// IS already the self-spent set by construction, and this module's whole obligation under D39's
/// parking is to say so in one place rather than let `H` silently assume it forever. The day a
/// granted-unlock source ships, THAT module owns adding the provenance flag this projection would
/// then need to read — not this one, and not by guessing at a column now that would always read `1`
/// (§1.1: a column that is always the same value is not information).</para>
/// </summary>
public static class TreeNodeSet
{
    /// <summary>Projects one actor's owned-node dictionary (exactly `RpgStore.LoadTreeState`'s return
    /// shape: node id → soul level, row presence means owned) into per-tree `(n_i, s_i)`. A tree with
    /// zero self-bought nodes is ABSENT from the result — never present at a zero `TreeSelfSpent`
    /// (rule 3) — because "nothing chosen" must never read as "chose evenly"
    /// (`AptitudeAllocation.cs`'s stated reason, restated here for the same shape of bug).</summary>
    public static IReadOnlyDictionary<string, TreeSelfSpent> SelfSpent(
        IReadOnlyDictionary<string, long> ownedNodeIdToSoulLevel)
    {
        if (ownedNodeIdToSoulLevel is null) throw new ArgumentNullException(nameof(ownedNodeIdToSoulLevel));

        var result = new Dictionary<string, TreeSelfSpent>(StringComparer.Ordinal);
        foreach (var (nodeId, soulLevel) in ownedNodeIdToSoulLevel)
        {
            if (soulLevel < 0)
                throw new ArgumentException($"soul level must be >= 0, got {soulLevel} for '{nodeId}'",
                    nameof(ownedNodeIdToSoulLevel));

            var treeId = TreeIdOf(nodeId);
            var prior = result.TryGetValue(treeId, out var p) ? p : default;
            // Rule 2: a node counts once, at 1 -- never weighted by what it cost. Weighting by price
            // would reintroduce the order dependence D39 closed.
            checked
            {
                result[treeId] = new TreeSelfSpent(prior.NodeCount + 1, prior.SoulLevels + soulLevel);
            }
        }
        return result;
    }

    /// <summary>Extracts `treeId` from `skill.&lt;treeId&gt;-&lt;branch&gt;-t&lt;tier&gt;-&lt;nodeKey&gt;`
    /// (R3) — `treeId` is a lowercase-alphanumeric slug with NO hyphen
    /// (`tools/seedsmith/.../ids.py`'s `_SLUG_RE`), so it is exactly the run between the `"skill."`
    /// prefix and the first `-` that follows it. Never re-derived from a catalog join (B5's "no
    /// `tree_id` column, and no per-tree read" design) — the id already carries it.</summary>
    static string TreeIdOf(string nodeId)
    {
        const string prefix = "skill.";
        if (!nodeId.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{nodeId}' does not start with '{prefix}' (R3 grammar)", nameof(nodeId));

        var afterPrefix = nodeId.AsSpan(prefix.Length);
        var dash = afterPrefix.IndexOf('-');
        if (dash <= 0)
            throw new ArgumentException($"'{nodeId}' has no '-' separating treeId from branch (R3 grammar)", nameof(nodeId));

        return afterPrefix[..dash].ToString();
    }
}
