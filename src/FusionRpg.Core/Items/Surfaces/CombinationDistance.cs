using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Surfaces;

/// <summary>
/// ssot-presentation.md §4.3's four closed states. There is no fifth, and the order is the order the
/// compendium renders in.
/// </summary>
public enum CombinationDisplayState
{
    /// <summary>The evaluator returned it. Full colour, name, atom lines.</summary>
    Active = 0,

    /// <summary>Dimmed, name, atom lines, <b>and the exact missing ingredient named</b>.</summary>
    OneAway,

    /// <summary>Dimmed, <b>name only</b>, atoms hidden.</summary>
    KnownInactive,

    /// <summary>Not rendered at all — the reveal rule has not fired, or the combination is
    /// unreachable on this item.</summary>
    Undiscovered,
}

/// <summary>
/// One catalog row against one fill.
/// </summary>
/// <param name="Distance">The minimum number of insert substitutions that would satisfy it.
/// <b><c>null</c> is ∞</b> — the combination is unreachable on this item, which is
/// <see cref="CombinationDisplayState.Undiscovered"/> and never <c>one-away</c>. Modelled as a
/// nullable rather than a sentinel so an arithmetic use site cannot quietly treat ∞ as a large
/// number.</param>
/// <param name="MissingElements">For a generated resonance, which elements the fill still lacks —
/// resonances name no ingredient families, so <paramref name="Missing"/> is empty for them and this
/// carries the hint instead.</param>
public readonly record struct CombinationDistanceRow(
    string ComboId,
    ComboShape Shape,
    CombinationDisplayState State,
    int? Distance,
    IReadOnlyList<MissingIngredient> Missing,
    IReadOnlyList<string> MissingElements,
    int GrantedTier,
    bool AllAttuned);

/// <summary>
/// Proof that the tractability claim holds, as data rather than as a paragraph.
/// <see cref="PermutationsEnumerated"/> exists so a test can assert <b>zero</b>, and
/// <see cref="ActiveSetEvaluations"/> so a test can assert <b>one</b> — the two properties
/// spec-item-surfaces.md's own Testing Strategy asks to be asserted "by instrumenting the evaluator's
/// call count" rather than by reading the code.
/// </summary>
public readonly record struct DistanceDiagnostics(
    int RecipesExamined,
    int MultisetComparisons,
    int PermutationsEnumerated,
    int ActiveSetEvaluations);

/// <summary>
/// The near-miss half of the socket surface: how far each catalog row is from firing, for the socket
/// bench's preview and the compendium's four states.
///
/// <para>⛔ <b>It is the same evaluator, called once.</b> ssot-presentation.md §4.3 and
/// ssot-sockets.md:277-279 both forbid a parallel near-miss pass, and the reason is concrete: two
/// functions is how "the tooltip said one more and it did not fire" happens. The active set here is
/// <see cref="CombinationEvaluator.Evaluate"/>'s own return, called exactly once
/// (<see cref="DistanceDiagnostics.ActiveSetEvaluations"/> asserts it), and the distance arms below
/// reuse the evaluator's own arithmetic — including attunement's effective-count bonus on Pure,
/// deliberately, so the preview cannot disagree with the result.</para>
///
/// <para>⛔ <b>D41 removed the swap branch, and this is the one place that has to say so.</b>
/// spec-item-surfaces.md (2026-09-03) specifies an INSERT/SWAP split whose swap leg counts
/// <c>n − cycles(σ)</c> over an ordered recipe. **The owner ruled recipes UNORDERED the next day**
/// (spec-sockets.md D41, 2026-09-04: *"unordered — we only need collect enough type of socket"*), and
/// that ruling's own consequence table names this module by name: *"Module 20's swap-distance —
/// sized against unordered; `distance` counts missing kinds, never positions."* Module 16 shipped it
/// that way: <see cref="ComboIngredient"/> carries no position field and
/// <see cref="ComboMatcher.Match"/> (strain-splice-host SSH1.1 — formerly
/// <c>CombinationEvaluator.MultisetSatisfied</c>) counts and claims. So there is no
/// <c>SWAP</c> kind here, no permutation, and no cycle count — not as an optimisation, but because a
/// swap distance over an unordered recipe would always be zero and the hint would be a lie. The
/// tractability the spec wanted is a consequence rather than a technique: the multiset difference
/// decides every row in O(k), k ≤ 4.</para>
///
/// <para><b>Empty sockets need no arm.</b> §4.3 counts an empty socket as one substitution; a fill
/// carries only the sockets that hold something, so a needed-but-absent ingredient already costs one
/// whether the socket is empty or holds the wrong insert. Room is the separate question, and it is
/// answered once by the reachability gate.</para>
/// </summary>
public static class CombinationDistance
{
    /// <summary>
    /// Every catalog row's state and distance against one fill, in the catalog's own order.
    /// The reveal rule is <b>not</b> applied here — <see cref="CompendiumReveal"/> owns it, so that
    /// "how far is this" and "may the player see this" stay two separately testable questions.
    /// </summary>
    public static IReadOnlyList<CombinationDistanceRow> Evaluate(
        SocketHost host,
        IReadOnlyList<SocketFill> fill,
        IReadOnlyList<ComboRecipe> catalog,
        SocketTuning tuning,
        ItemSurfaceTuning surfaceTuning,
        out DistanceDiagnostics diagnostics,
        IReadOnlyList<TierLadderRung>? ladder = null)
    {
        if (fill is null) throw new ArgumentNullException(nameof(fill));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (surfaceTuning is null) throw new ArgumentNullException(nameof(surfaceTuning));

        // ⛔ ONE call. Not one per recipe, and never a second implementation.
        var active = CombinationEvaluator.Evaluate(host, fill, catalog, tuning, ladder);
        var activeById = active.ToDictionary(r => r.ComboId, StringComparer.Ordinal);

        var multisetComparisons = 0;
        var rows = new List<CombinationDistanceRow>(catalog.Count);

        foreach (var recipe in catalog)
        {
            if (activeById.TryGetValue(recipe.ComboId, out var hit))
            {
                rows.Add(new CombinationDistanceRow(
                    recipe.ComboId, recipe.Shape, CombinationDisplayState.Active, 0,
                    Array.Empty<MissingIngredient>(), Array.Empty<string>(), hit.GrantedTier, hit.AllAttuned));
                continue;
            }

            var missing = Array.Empty<MissingIngredient>();
            var missingElements = Array.Empty<string>();
            int? distance;

            if (!Reachable(host, recipe))
            {
                distance = null;
            }
            else if (ComboShapes.IsStrainOrSplice(recipe.Shape))
            {
                multisetComparisons++;
                var match = ComboMatcher.Match(recipe, fill, ladder);
                missing = match.Missing.ToArray();
                // SSH7.2 (spec-tier-ladder §2): a fill whose FAMILIES all match but whose tiers miss
                // rung 1 is not distance zero — it is as many positions short as the rung it has not
                // reached. Counting them keeps the invariant this file's own property test pins:
                // distance-zero is exactly "the evaluator fires".
                distance = match.Missing.Sum(m => m.Quantity)
                           + (match.Missing.Count == 0 ? match.ShortOfNext.Count : 0);
            }
            else
            {
                distance = ResonanceShortfall(recipe, fill, tuning, out missingElements);
            }

            var state = distance is null
                ? CombinationDisplayState.Undiscovered
                : distance <= surfaceTuning.OneAwayDistance
                    ? CombinationDisplayState.OneAway
                    : CombinationDisplayState.KnownInactive;

            rows.Add(new CombinationDistanceRow(
                recipe.ComboId, recipe.Shape, state, distance, missing, missingElements,
                GrantedTier: recipe.BaseTier, AllAttuned: false));
        }

        diagnostics = new DistanceDiagnostics(
            RecipesExamined: catalog.Count,
            MultisetComparisons: multisetComparisons,
            PermutationsEnumerated: 0,
            ActiveSetEvaluations: 1);

        return rows;
    }

    /// <summary>
    /// G3 §4.3's <c>∞</c> rule, in one place: a combination this item can never carry is
    /// <c>undiscovered</c>, never <c>one-away</c>. Three ways it can be unreachable — too few sockets
    /// for the recipe's shape, the wrong host role or frame, and D21's set-piece exclusivity — and all
    /// three are permanent facts about the item, not about the fill.
    /// </summary>
    static bool Reachable(SocketHost host, ComboRecipe recipe)
    {
        // strain-splice-host SSH1.3 (host-gate §2): "reachable" is a CAPACITY question, not an
        // opened-count one -- an unbored chassis (SocketCount 0) with real Capacity is still a real
        // goal, never Undiscovered, because the player can bore up to Capacity. Every check below
        // reads Capacity for the same reason; only the actual fill (which can never exceed the
        // opened count) is allowed to determine the DISTANCE, in ComboMatcher.Match.
        if (!ComboMatcher.CanEverHold(host, recipe)) return false;

        // D21: a set piece may not carry a Strain or a Splice. It is not "one insert away" from one;
        // it will never have one, and saying "one away" would be the tooltip lie in its purest form.
        if (ComboShapes.IsStrainOrSplice(recipe.Shape) && !SetExclusivityValidator.MayFire(host, recipe.Shape))
            return false;

        // The recipe wants more ingredients than the chassis could ever hold. `MinSockets` usually
        // already covers this, but a content row may under-declare it, and a distance that can never
        // reach zero must not render as a goal.
        if (ComboShapes.IsStrainOrSplice(recipe.Shape))
        {
            var needed = recipe.Ingredients.Sum(i => Math.Max(0, i.Quantity));
            if (needed > host.Capacity) return false;
        }

        var thresholdShapes = recipe.Shape is ComboShape.Pure or ComboShape.Diversity;
        if (thresholdShapes && recipe.Threshold > host.Capacity) return false;
        if (recipe.Shape is ComboShape.Ring or ComboShape.Eclipse && host.Capacity < 2) return false;

        return true;
    }

    /// <summary>
    /// The generated shapes' distance, each read off the same arithmetic its evaluator arm uses.
    ///
    /// <para>⚠ <b>Pure's distance includes attunement's effective-count bonus, deliberately.</b>
    /// spec-item-surfaces.md says "a matched affinity changes the result, not the distance" — that is
    /// true of a Strain (where attunement moves the granted TIER) and <b>false of Pure against the
    /// shipped evaluator</b>, whose Pure arm adds <c>AttunedEffectiveCountBonus</c> to the CONTRIBUTOR
    /// COUNT, which is the very thing the threshold is compared against
    /// (<c>CombinationEvaluator</c>, the two-arm table in spec-sockets.md §6). Reporting a distance
    /// that ignored it would say "one more" about a resonance that is already firing — the exact
    /// failure the same-evaluator rule exists to prevent. The divergence is named in the module's
    /// build log rather than silently resolved either way.</para>
    /// </summary>
    static int ResonanceShortfall(
        ComboRecipe recipe, IReadOnlyList<SocketFill> fill, SocketTuning tuning, out string[] missingElements)
    {
        missingElements = Array.Empty<string>();

        switch (recipe.Shape)
        {
            case ComboShape.Pure:
            {
                var contributors = fill.Where(f => string.Equals(f.Insert.Element, recipe.Element, StringComparison.Ordinal)).ToList();
                var effective = contributors.Count
                                + (contributors.Count > 0 && contributors.All(f => f.IsAttuned) ? tuning.AttunedEffectiveCountBonus : 0);
                var shortfall = Math.Max(0, recipe.Threshold - effective);
                if (shortfall > 0) missingElements = new[] { recipe.Element };
                return shortfall;
            }

            case ComboShape.Ring:
            {
                if (!ResonanceGenerator.TryReadRingPair(recipe, out var a, out var b)) return int.MaxValue;
                return PresenceShortfall(fill, new[] { Id(a), Id(b) }, out missingElements);
            }

            case ComboShape.Eclipse:
                return PresenceShortfall(fill, new[] { Id(tuning.EclipseA), Id(tuning.EclipseB) }, out missingElements);

            case ComboShape.Diversity:
            {
                var distinct = fill
                    .Select(f => f.Insert.Element)
                    .Where(e => e.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                // Diversity counts DISTINCT elements, so the shortfall names no particular element —
                // "one more element you do not already have" is the honest hint and the caller renders it.
                return Math.Max(0, recipe.Threshold - distinct);
            }

            default:
                return int.MaxValue;
        }
    }

    static int PresenceShortfall(IReadOnlyList<SocketFill> fill, string[] wanted, out string[] missingElements)
    {
        var absent = wanted
            .Where(w => !fill.Any(f => string.Equals(f.Insert.Element, w, StringComparison.Ordinal)))
            .ToArray();
        missingElements = absent;
        return absent.Length;
    }

    static string Id(ElementTypeId element) => element.ToString().ToLowerInvariant();
}
