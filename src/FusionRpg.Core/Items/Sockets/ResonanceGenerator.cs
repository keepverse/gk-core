using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// The 25 resonance recipes, <b>generated from the element roster</b> rather than authored
/// (ssot-sockets.md §4.4 — "generated the way the atom library generates its element families"). Two
/// examples teach the whole set, which is what keeps the resonance half of the catalog off a wiki.
///
/// <para>Counts follow from the roster and the tuning, never from a literal:
/// <c>|Concrete| × |pureThresholds|</c> Pure + <c>|ringOrder|</c> Ring + 1 Eclipse +
/// <c>|diversityThresholds|</c> Diversity = 6×3 + 4 + 1 + 2 = <b>25</b> against the shipped files. A
/// test asserts the 25 <i>and</i> re-derives it from the roster, so adding a seventh element grows
/// the catalog instead of going red.</para>
///
/// <para>⛔ <b>D27 renamed every one of these.</b> The lane's <c>gem.combo-*</c> ids are retired:
/// definitions.md §1 forces a container id's prefix to match its kind, and a combination's kind is
/// <c>combo</c>, not <c>gem</c>. Inserts keep <c>gem.</c>.</para>
///
/// <para>⚠ <b>The generator is where ssot-sockets.md §6.4's authoring rule lives.</b> "A resonance
/// container may not repeat a family its triggering inserts carry" is enforced by construction — a
/// generated recipe names no ingredient families at all, only a shape and a threshold — so it cannot
/// be violated and needs no rejection code. The atoms a resonance <i>grants</i> are content
/// (effect-atom's <c>combo</c> containers, X7), and this module authors none of them.</para>
/// </summary>
public static class ResonanceGenerator
{
    /// <summary>The <c>container_id</c> prefix D27 fixes for every combination row.</summary>
    public const string ComboPrefix = "combo.";

    /// <summary>
    /// Generate the whole resonance catalog, ordered by <see cref="ComboShape"/> then by id ordinal —
    /// the same order the evaluator resolves in, so a reader of the table sees the priority.
    /// </summary>
    public static IReadOnlyList<ComboRecipe> Generate(SocketTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var rows = new List<ComboRecipe>();

        // Pure — |Concrete| × |pureThresholds|.
        foreach (var element in ElementRoster.Concrete)
        foreach (var k in tuning.PureThresholds)
            rows.Add(Recipe($"{ComboPrefix}pure-{Id(element)}-{k}", ComboShape.Pure, Id(element), k));

        // Ring — one row per adjacent pair on the cycle, which wraps.
        for (var i = 0; i < tuning.RingOrder.Count; i++)
        {
            var a = tuning.RingOrder[i];
            var b = tuning.RingOrder[(i + 1) % tuning.RingOrder.Count];
            rows.Add(Recipe($"{ComboPrefix}ring-{Id(a)}-{Id(b)}", ComboShape.Ring, "", Threshold: 1));
        }

        // Eclipse — the one mutual-counter pair.
        rows.Add(Recipe($"{ComboPrefix}eclipse", ComboShape.Eclipse, "", Threshold: 1));

        // Diversity — the only shape omni contributes to.
        foreach (var k in tuning.DiversityThresholds)
            rows.Add(Recipe($"{ComboPrefix}diversity-{k}", ComboShape.Diversity, "", k));

        return rows
            .OrderBy(r => (int)r.Shape)
            .ThenBy(r => r.ComboId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The two elements a Ring row joins, parsed back out of its generated id. Used by the evaluator
    /// so the pair is read from the row rather than re-derived from the tuning at match time.
    /// </summary>
    public static bool TryReadRingPair(ComboRecipe recipe, out ElementTypeId a, out ElementTypeId b)
    {
        a = default;
        b = default;
        if (recipe.Shape != ComboShape.Ring) return false;

        var tail = recipe.ComboId.StartsWith(ComboPrefix + "ring-", StringComparison.Ordinal)
            ? recipe.ComboId.Substring((ComboPrefix + "ring-").Length)
            : "";
        var parts = tail.Split('-');
        return parts.Length == 2
               && ElementRoster.TryParse(parts[0], out a)
               && ElementRoster.TryParse(parts[1], out b);
    }

    static string Id(ElementTypeId element) => element.ToString().ToLowerInvariant();

    // BaseTier tracks the threshold for a generated row: a k=4 Pure is a bigger step than a k=2 one,
    // and tying them means adding a fifth step needs no second table. It is a SHAPE index, not a
    // magnitude — the numbers a combination grants live on its `combo` container's atoms (X7).
    //
    // ⛔ MinSockets is 0 on every GENERATED row, and that is deliberate. A resonance is self-gating:
    // you cannot put three fire inserts in two sockets. Setting min_sockets = k here would ALSO
    // destroy §4.2's whole payoff, because attunement's +1 exists precisely so that "the right item
    // reaches a step the socket count alone could not" — ssot-sockets.md §7.4's worked example is
    // three attuned inserts on a three-socket item firing pure-earth-4. min_sockets belongs to
    // AUTHORED recipes (module 21's Strains and Splices), which require a host of a given size before
    // any insert is placed.
    static ComboRecipe Recipe(string id, ComboShape shape, string element, int Threshold) =>
        new(id, shape, element, Threshold, HostRole: "", HostFrame: "", MinSockets: 0, BaseTier: Threshold,
            Ingredients: Array.Empty<ComboIngredient>());
}
