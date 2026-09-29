using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// spec-sockets.md §8 — <b>127 rows, one pure function</b>:
/// <c>evaluate(fill, affinities, hostContainer) -> Combination[]</c>. No RNG, no ambient state, no
/// writes, no clock. Same fill, same catalog, same answer, forever.
///
/// <para><b>Ordered</b>: Strains/Splices first, then Pure (highest k per element), then Ring, Eclipse,
/// Diversity — an order carried by <see cref="ComboShape"/>'s own declaration order rather than by
/// the sequence of statements below.</para>
///
/// <para><b>Per CIRCUIT (circuit-topology §3).</b> The fill is grouped by
/// <c>socketIndex / <see cref="SocketLimits.SocketCircuitSize"/></c> and the SAME ordered pass runs
/// once per circuit: at most one Strain/Splice identity per circuit, then Pure, Ring, Eclipse and
/// Diversity over that circuit's own inserts. There is no cross-circuit recipe and no cross-circuit
/// resonance — a six-socket mantle is one complete circuit plus a two-socket remainder that can carry
/// resonance only, and an eight-socket host can carry two independent combinations. A host with
/// <see cref="SocketLimits.SocketCircuitSize"/> or fewer sockets is ONE circuit, so behaviour under
/// v1 is unchanged.</para>
///
/// <para>✅ <b>D41 — a recipe is an unordered multiset match.</b> Ingredients are counted, never
/// positioned; the same inserts in any arrangement WITHIN a circuit resolve to the same combination.
/// ⛔ A matcher that read <c>bind_ordinal</c> would be a bug: that column is for stable display order
/// only.</para>
///
/// <para><b>Reusing module 12's shape, not its machine.</b> <c>ThresholdEvaluator</c>'s own doc
/// comment already says module 16 reuses the shape at per-item scope and that merging them "would
/// make the scope a parameter of a thing whose whole identity is its scope". This function counts a
/// multiset and looks up the shapes that match — the same idea, owned per item.</para>
/// </summary>
public static class CombinationEvaluator
{
    /// <summary>
    /// Every combination the fill satisfies, in resolution order (circuit ascending, then the shape
    /// order above within each circuit).
    /// </summary>
    public static IReadOnlyList<CombinationResult> Evaluate(
        SocketHost host, IReadOnlyList<SocketFill> fill, IReadOnlyList<ComboRecipe> catalog, SocketTuning tuning,
        IReadOnlyList<TierLadderRung>? ladder = null)
    {
        if (fill is null) throw new ArgumentNullException(nameof(fill));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var results = new List<CombinationResult>();
        var eligible = catalog.Where(r => ComboMatcher.HostAdmits(host, r)).ToList();

        // A circuit with no filled socket is not evaluated at all — an empty remainder has nothing to
        // match. Circuits are walked in ascending index order so the result list is deterministic and
        // independent of the fill's own ordering.
        foreach (var circuit in fill
                     .GroupBy(f => f.SocketIndex / SocketLimits.SocketCircuitSize)
                     .OrderBy(g => g.Key))
        {
            var circuitFill = circuit.OrderBy(f => f.SocketIndex).ToList();
            EvaluateCircuit(host, circuitFill, circuit.Key, eligible, tuning, ladder, results);
        }

        return results;
    }

    /// <summary>
    /// The ONE ordered pass, over one circuit. Extracted rather than duplicated: the per-circuit rule
    /// is a grouping around the same evaluation, never a second evaluator.
    /// </summary>
    static void EvaluateCircuit(
        SocketHost host, IReadOnlyList<SocketFill> fill, int circuit,
        IReadOnlyList<ComboRecipe> eligible, SocketTuning tuning,
        IReadOnlyList<TierLadderRung>? ladder, List<CombinationResult> results)
    {
        // ── Strain / Splice ───────────────────────────────────────────────────────────────────
        // At most one per CIRCUIT: it is the circuit's identity, and two identities is one too many.
        // Ties break on the lowest container_id ordinal — content-derived, so deterministic and
        // independent of catalog order. D21: never on a set piece (SetExclusivityValidator).
        var identity = eligible
            .Where(r => ComboShapes.IsStrainOrSplice(r.Shape))
            .Where(r => SetExclusivityValidator.MayFire(host, r.Shape))
            .Where(r => ComboMatcher.Fits(r, fill, ladder))
            .OrderBy(r => r.ComboId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (identity is not null)
        {
            // SSH7.2 (spec-tier-ladder §2): the SAME match that decided it fires also decides which
            // RUNG it reached, and the rung's own grant delta lands between the shape's base tier and
            // attunement's bonus. With no ladder passed the rung is 1 and its delta is 0, which is
            // exactly the flat floor every pre-ladder caller has always had.
            var match = ComboMatcher.Match(identity, fill, ladder);
            var ingredients = match.Used;
            var attuned = AllAttuned(ingredients);
            results.Add(new CombinationResult(
                identity.ComboId, identity.Shape,
                EffectiveCount: ingredients.Count,
                // A Strain has no count to raise, so attunement's +1 lands on the granted TIER
                // instead (spec-sockets.md §6's two-arm table). Unbounded above by design.
                GrantedTier: identity.BaseTier + ComboMatcher.GrantDelta(ladder, match.Rung)
                             + (attuned ? tuning.AttunedTierBonus : 0),
                AllAttuned: attuned,
                Circuit: circuit));
        }

        // ── Pure ──────────────────────────────────────────────────────────────────────────────
        // Only the highest k per element fires: three fire inserts fire pure-fire-3, never
        // pure-fire-2 as well, or the ladder stacks with itself.
        foreach (var element in ElementRoster.Concrete)
        {
            var id = element.ToString().ToLowerInvariant();
            var contributors = fill.Where(f => string.Equals(f.Insert.Element, id, StringComparison.Ordinal)).ToList();
            if (contributors.Count == 0) continue;

            var attuned = AllAttuned(contributors);
            var effective = contributors.Count + (attuned ? tuning.AttunedEffectiveCountBonus : 0);

            var best = eligible
                .Where(r => r.Shape == ComboShape.Pure
                            && string.Equals(r.Element, id, StringComparison.Ordinal)
                            && r.Threshold <= effective)
                .OrderByDescending(r => r.Threshold)
                .ThenBy(r => r.ComboId, StringComparer.Ordinal)
                .FirstOrDefault();

            if (best is not null)
                results.Add(new CombinationResult(
                    best.ComboId, best.Shape, effective, best.BaseTier, attuned, Circuit: circuit));
        }

        // ── Ring ──────────────────────────────────────────────────────────────────────────────
        // A presence test over two adjacent elements: no count to raise, so no attunement arm.
        foreach (var recipe in eligible.Where(r => r.Shape == ComboShape.Ring))
        {
            if (!ResonanceGenerator.TryReadRingPair(recipe, out var a, out var b)) continue;
            if (!HasElement(fill, a) || !HasElement(fill, b)) continue;
            results.Add(new CombinationResult(
                recipe.ComboId, recipe.Shape, 2, recipe.BaseTier, AllAttuned: false, Circuit: circuit));
        }

        // ── Eclipse ───────────────────────────────────────────────────────────────────────────
        foreach (var recipe in eligible.Where(r => r.Shape == ComboShape.Eclipse))
        {
            if (!HasElement(fill, tuning.EclipseA) || !HasElement(fill, tuning.EclipseB)) continue;
            results.Add(new CombinationResult(
                recipe.ComboId, recipe.Shape, 2, recipe.BaseTier, AllAttuned: false, Circuit: circuit));
        }

        // ── Diversity ─────────────────────────────────────────────────────────────────────────
        // The only shape `omni` contributes to — the deliberate no-combo option, raw additive power
        // for a player who does not want a puzzle. An ELEMENT-FREE insert (a vitality gem) counts
        // toward nothing at all, here included: "" is an absent element, not a seventh one.
        var distinct = fill
            .Select(f => f.Insert.Element)
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var diversity = eligible
            .Where(r => r.Shape == ComboShape.Diversity && r.Threshold <= distinct)
            .OrderByDescending(r => r.Threshold)
            .ThenBy(r => r.ComboId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (diversity is not null)
            results.Add(new CombinationResult(
                diversity.ComboId, diversity.Shape, distinct, diversity.BaseTier,
                AllAttuned: false, Circuit: circuit));
    }

    /// <summary>
    /// The write-free preview form module 20 renders: the same function over a HYPOTHETICAL fill.
    /// It is deliberately the same code path — a second "preview" implementation is how a preview
    /// starts lying about what socketing will actually do.
    /// </summary>
    public static IReadOnlyList<CombinationResult> Preview(
        SocketHost host, IReadOnlyList<SocketFill> hypotheticalFill,
        IReadOnlyList<ComboRecipe> catalog, SocketTuning tuning) =>
        Evaluate(host, hypotheticalFill, catalog, tuning);

    /// <summary>
    /// What the fill would produce with one more insert in one more socket — module 20's
    /// "one insert away" hint, which ssot-sockets.md §8.2 promotes from a nicety to a requirement.
    /// </summary>
    public static IReadOnlyList<CombinationResult> PreviewWithOneMore(
        SocketHost host, IReadOnlyList<SocketFill> fill, SocketFill candidate,
        IReadOnlyList<ComboRecipe> catalog, SocketTuning tuning) =>
        Evaluate(host, fill.Concat(new[] { candidate }).ToList(), catalog, tuning);

    static bool HasElement(IReadOnlyList<SocketFill> fill, ElementTypeId element)
    {
        var id = element.ToString().ToLowerInvariant();
        return fill.Any(f => string.Equals(f.Insert.Element, id, StringComparison.Ordinal));
    }

    static bool AllAttuned(IReadOnlyList<SocketFill> contributors) =>
        contributors.Count > 0 && contributors.All(f => f.IsAttuned);
}
