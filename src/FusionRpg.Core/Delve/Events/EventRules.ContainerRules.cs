namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.9 (spec-event-deck.md §9), the tenth item's SECOND conjunct — the rule ids its
/// check raises, on <see cref="EventRules"/>'s own registered `"event"` namespace, in their own file.
///
/// <para><b>Why a separate file rather than two more consts in `EventCatalog.cs`.</b> That file is cited
/// by line from `npc-story-events` and `narrative-seed` specs (`spec-storylet-contract.md`,
/// `spec-storylet-vocab.md`, `spec-dungeon-generator-repair.md`, `trade-stories-map.md` and others);
/// inserting into it shifts every citation below the insertion point, which is a broken citation in
/// another program's document. `EventRules` is therefore `partial` and a task that adds a rule id lands
/// it here. The vocabulary is still ONE type — `EventRules.NonEventAtomKind` — never a second one.</para>
/// </summary>
public static partial class EventRules
{
    /// <summary>Spec §9's own bullet, verbatim: "no `nerve.*` id or non-event atom kind in any
    /// container." This id is the SECOND conjunct — an atom an event's own outcome actually instantiates
    /// whose <c>KindId</c> is not one of spec §5's five dispatch kinds. Raised by
    /// <see cref="EventDeckPreflight.CheckEventOutcomeAtomKinds"/>, which scopes the check to the
    /// containers an event outcome really resolves to rather than the whole container store (see that
    /// method's own doc comment for why a whole-corpus scope is empirically wrong).</summary>
    public const string NonEventAtomKind = "event.non-event-atom-kind";

    /// <summary>An event outcome's own effect ref that resolves to no real atom at all — an unknown
    /// <c>powerBand</c>, or a family with no atom at the resolved tier. <see cref="EventDeck.Resolve"/>
    /// throws the identical <see cref="EventDeckRefusal"/> at play, so this is a content-integrity
    /// rejection, never a kind violation.</summary>
    public const string EffectAtomUnresolved = "event.effect-atom-unresolved";
}
