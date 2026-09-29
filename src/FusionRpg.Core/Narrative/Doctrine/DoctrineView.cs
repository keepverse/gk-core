using FusionRpg.Core.Narrative.Vocabulary;

namespace FusionRpg.Core.Narrative.Doctrine;

/// <summary>
/// What a consumer of an active doctrine may read (spec-counter-doctrine.md §3, "Consumption is
/// world-map's"): the element biases and the order weights, and NOTHING else. There is deliberately no
/// magnitude member — no hp, attack, level, damage, defense, rarity or count — because R13's rule is that
/// the antagonist's answer changes **what** his faction fields, never **how strong** it is. A reflection
/// test asserts the member set, so a field that could smuggle strength cannot appear quietly.
///
/// <para><b>The biases re-weight only within the candidate set the undoctrined draw would use at the same
/// threat rung and rarity</b> (spec §3's own audit: an element bias that crossed rungs would field stronger
/// units, not merely different ones) — a constraint on the consumer, carried in the filed ask, not
/// something this view can enforce.</para>
///
/// <para><b>`For(world, factionId)` is not here yet, and that is a named dependency, not an omission:</b>
/// the active doctrine has to be stored on the faction for a pure read of hashed state to find it
/// (`WorldFaction` carries no doctrine member today, `WorldState.cs:70-93`), and adopting one is the study
/// bar's own row. Until then the engine-side and test-side read is <see cref="Of"/> over a doctrine row,
/// which is exactly the shape the policy ask consumes once the field exists.</para>
/// </summary>
public sealed record DoctrineView(
    string DoctrineId,
    IReadOnlyDictionary<string, int> SpeciesElementBiasMilli,
    IReadOnlyDictionary<string, int> OrderWeightMilli)
{
    /// <summary>The view of one doctrine row.</summary>
    public static DoctrineView Of(DoctrineDef doctrine)
    {
        ArgumentNullException.ThrowIfNull(doctrine);
        return new DoctrineView(doctrine.Id, doctrine.SpeciesElementBiasMilli, doctrine.OrderWeightMilli);
    }

    /// <summary>The bias for one element, or the neutral 1000 when the doctrine does not name it (a
    /// doctrine is a bias, never a full table).</summary>
    public int ElementBiasFor(string elementId) =>
        SpeciesElementBiasMilli.TryGetValue(elementId, out var bias) ? bias : 1000;

    /// <summary>The weight for one order kind, or the neutral 1000 when the doctrine does not name it.</summary>
    public int OrderWeightFor(string orderKind) =>
        OrderWeightMilli.TryGetValue(orderKind, out var weight) ? weight : 1000;
}
