using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Surfaces;

/// <summary>
/// What the player has ever held, which is the only thing the reveal rule reads. Two ledgers rather
/// than one because the catalog has two kinds of row: an authored Strain/Splice names insert
/// FAMILIES, a generated resonance names ELEMENTS and no family at all
/// (<see cref="ResonanceGenerator"/> emits no ingredients by construction).
///
/// <para><b>"Held", not "holds".</b> ssot-sockets.md:246 — <i>"revealed once the player has held every
/// ingredient at least once"</i>. A ledger that decayed when the player spent a gem would un-teach a
/// recipe, which is worse than never having taught it.</para>
/// </summary>
public readonly record struct HeldLedger(IReadOnlySet<string> InsertFamilies, IReadOnlySet<string> Elements)
{
    public static HeldLedger Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    public static HeldLedger From(IEnumerable<InsertDef> everHeld)
    {
        if (everHeld is null) throw new ArgumentNullException(nameof(everHeld));
        var families = new HashSet<string>(StringComparer.Ordinal);
        var elements = new HashSet<string>(StringComparer.Ordinal);
        foreach (var insert in everHeld)
        {
            if (insert.FamilyId.Length > 0) families.Add(insert.FamilyId);
            if (insert.Element.Length > 0) elements.Add(insert.Element);
        }
        return new HeldLedger(families, elements);
    }
}

/// <summary>
/// The compendium's reveal rule and its display cap — ssot-sockets.md §4.4/§8.2's own mitigation,
/// which D20 promoted from a nicety to a requirement when the catalog went from ~45 to <b>127</b>.
///
/// <para>The argument, in one line: <i>"The list is content the game gives you, not knowledge you
/// import."</i> §8.2's named failure is D2's runeword list, which was in practice an out-of-game
/// resource. A reveal rule is what keeps the recipe a goal the game states.</para>
///
/// <para>⛔ <b>Never render 127.</b> <see cref="Render"/> returns active, then one-away, then
/// known-inactive by name only, and nothing else — ssot-presentation.md §4.3's four closed states,
/// which is GG-26 progressive disclosure applied to a catalog that cannot fit. The
/// <c>knownInactiveRowCap</c> bounds only the name-only tail; an active or one-away row is never
/// dropped, so the cap cannot hide a combination the player is about to earn.</para>
/// </summary>
public static class CompendiumReveal
{
    /// <summary>
    /// Has the player held every ingredient of this recipe at least once?
    ///
    /// <para>A generated resonance names no ingredient families, so its reveal condition is read off
    /// its shape instead: Pure wants its element, Ring and Eclipse want both of theirs, and Diversity
    /// wants <c>threshold</c> distinct elements — which is the same sentence ("every ingredient") for
    /// a shape whose ingredients are "any k different elements". Deriving it rather than authoring a
    /// second reveal table is what keeps a seventh element from needing a content edit here.</para>
    /// </summary>
    public static bool IsRevealed(ComboRecipe recipe, HeldLedger held, SocketTuning tuning)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (ComboShapes.IsStrainOrSplice(recipe.Shape))
            return recipe.Ingredients.Count > 0
                   && recipe.Ingredients.All(i => held.InsertFamilies.Contains(i.FamilyId));

        return recipe.Shape switch
        {
            ComboShape.Pure => held.Elements.Contains(recipe.Element),
            ComboShape.Ring => ResonanceGenerator.TryReadRingPair(recipe, out var a, out var b)
                               && held.Elements.Contains(Id(a)) && held.Elements.Contains(Id(b)),
            ComboShape.Eclipse => held.Elements.Contains(Id(tuning.EclipseA)) && held.Elements.Contains(Id(tuning.EclipseB)),
            ComboShape.Diversity => held.Elements.Count >= recipe.Threshold,
            _ => false,
        };
    }

    /// <summary>
    /// The compendium's render list: the four states applied, the unrevealed demoted, the
    /// undiscovered dropped, and the name-only tail capped.
    ///
    /// <para>Ordered <b>active → one-away → known-inactive</b>, and within a band by combo id ordinal
    /// so the list is stable across renders. An unstable order in a 127-row catalog is how a player
    /// loses the row they were reading.</para>
    /// </summary>
    public static IReadOnlyList<CombinationDistanceRow> Render(
        IReadOnlyList<CombinationDistanceRow> rows,
        IReadOnlyList<ComboRecipe> catalog,
        HeldLedger held,
        SocketTuning tuning,
        ItemSurfaceTuning surfaceTuning)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (surfaceTuning is null) throw new ArgumentNullException(nameof(surfaceTuning));

        var byId = catalog.ToDictionary(r => r.ComboId, StringComparer.Ordinal);

        var active = new List<CombinationDistanceRow>();
        var oneAway = new List<CombinationDistanceRow>();
        var knownInactive = new List<CombinationDistanceRow>();

        foreach (var row in rows)
        {
            if (row.State == CombinationDisplayState.Undiscovered) continue;

            if (row.State == CombinationDisplayState.Active)
            {
                active.Add(row);
                continue;
            }

            // An unreachable row never gets here (it is already Undiscovered); an unrevealed one is
            // demoted rather than dimmed, because "there is a thing here you have not met" is exactly
            // the wiki-dependency §8.2 names.
            if (!byId.TryGetValue(row.ComboId, out var recipe) || !IsRevealed(recipe, held, tuning)) continue;

            if (row.State == CombinationDisplayState.OneAway) oneAway.Add(row);
            else knownInactive.Add(row);
        }

        static IEnumerable<CombinationDistanceRow> Stable(IEnumerable<CombinationDistanceRow> band) =>
            band.OrderBy(r => r.Distance ?? int.MaxValue).ThenBy(r => r.ComboId, StringComparer.Ordinal);

        var result = new List<CombinationDistanceRow>();
        result.AddRange(Stable(active));
        result.AddRange(Stable(oneAway));
        result.AddRange(Stable(knownInactive).Take(surfaceTuning.KnownInactiveRowCap));
        return result;
    }

    static string Id(ElementTypeId element) => element.ToString().ToLowerInvariant();
}
