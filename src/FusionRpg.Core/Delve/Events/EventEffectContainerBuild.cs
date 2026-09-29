using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Uniques;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.3/D3.5 (spec-event-deck.md §5, "Effects" paragraph) — the small, previously-missing
/// half of "the outcome's container goes through `Instantiator.TryInstantiate`": turns an outcome's
/// authored <see cref="EventEffectRef"/> list (`Family`+`PowerBand` only — D3.1's own shipped shape,
/// `EventRow.cs:12`) into a real <see cref="ContainerRow"/> `TryInstantiate` can actually run on.
///
/// <para><b>Structurally identical to <see cref="UniqueContainerBuild.From"/>'s own fixed-atom
/// resolution</b> (re-diagnosed 2026-09-07, D3.3's own todo entry): `family` × `powerBand` → tier
/// (<see cref="UniqueBudget.TierOfPowerBand"/>) → atom id (<see cref="AtomRow.DeriveId"/>), the same
/// two real, already-shipped halves that build composes elsewhere. Every <see cref="EventEffectRef"/>
/// becomes one <see cref="ContainerAtomRow"/> in the FIXED core, in authoring order — an event outcome
/// never carries a weighted pool (spec §5 names no such thing for events; `PrefixRolls`/`SuffixRolls`
/// stay 0/0), unlike a unique's own variance slot.</para>
///
/// <para><see cref="ContainerKind.Item"/> is a deliberate, confirmed-safe reuse, not a placeholder: a
/// direct search confirms <c>Instantiator.TryInstantiate</c> never branches on <c>ContainerRow.Kind</c>
/// anywhere (grep, zero hits), so no NEW, reviewed <see cref="ContainerKind"/> member is needed for this
/// container to instantiate correctly — the same finding D3.3's own todo entry already made.</para>
/// </summary>
public static class EventEffectContainerBuild
{
    /// <summary>
    /// The one owner of the container id an event outcome instantiates under — extracted 2026-09-22 so
    /// <see cref="EventDeck.Resolve"/> and <see cref="EventDeckPreflight.CheckEventOutcomeAtomKinds"/> share
    /// one derivation instead of each holding a copy (the N13 "same name under a second owner" defect this
    /// program has already paid for once, `EventDraw.RootStream`).
    ///
    /// <para><b>Not the raw `eventId:ordinal` the spec's own pseudocode implies</b> (a correction named in
    /// <see cref="EventDeck.Resolve"/>'s doc comment before this extraction):
    /// <c>ContainerValidator</c>'s own id grammar is <c>^(item|trait|...)\.[a-z0-9-]+$</c> — no second dot,
    /// no colon — while a real shipped event id already carries dots of its own
    /// (<c>event.bargain-creature.allpeater-001</c>). Every '.' folds to '-' and the container-kind's own
    /// required <c>item.</c> prefix is applied, or every real call would be refused by the grammar check.</para>
    /// </summary>
    public static string ContainerIdFor(string eventId, string outcomeOrdinal)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required", nameof(eventId));
        if (string.IsNullOrWhiteSpace(outcomeOrdinal)) throw new ArgumentException("outcomeOrdinal required", nameof(outcomeOrdinal));
        return $"item.{eventId.Replace('.', '-')}-{outcomeOrdinal}";
    }

    /// <param name="containerId">Caller-supplied — <see cref="ContainerIdFor"/> is the one owner of the
    /// convention every real caller uses (`EventDeck.Resolve`, and `EventDeckPreflight`'s own
    /// `CheckEventOutcomeAtomKinds`); a pure builder should not invent one itself.</param>
    /// <param name="effects">The outcome's own authored effect refs, in authoring order — becomes the
    /// container's fixed core, one <see cref="ContainerAtomRow"/> per ref, `Seq` 0-based.</param>
    /// <param name="lookupAtom">The real atom catalog, read-only — this module holds no copy of it
    /// (the "read model owned elsewhere" idiom <see cref="UniqueContainerBuild"/> already uses).</param>
    public static ContainerRow From(
        string containerId,
        IReadOnlyList<EventEffectRef> effects,
        Func<string, AtomRow?> lookupAtom)
    {
        if (string.IsNullOrWhiteSpace(containerId)) throw new ArgumentException("containerId required", nameof(containerId));
        if (effects is null) throw new ArgumentNullException(nameof(effects));
        if (effects.Count == 0) throw new ArgumentException("an outcome's effects list must not be empty", nameof(effects));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));

        var atoms = new List<ContainerAtomRow>(effects.Count);
        var seq = 0;
        foreach (var effect in effects)
        {
            if (effect is null) throw new ArgumentException("an outcome's effects list must not contain a null entry", nameof(effects));

            int tier;
            try { tier = UniqueBudget.TierOfPowerBand(effect.PowerBand); }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new EventDeckRefusal(
                    $"container '{containerId}': effect family '{effect.Family}' names powerBand " +
                    $"'{effect.PowerBand}', which is not one of bands.v1.json's five ({ex.Message})");
            }

            var atomId = AtomRow.DeriveId(effect.Family, "", tier);
            if (lookupAtom(atomId) is null)
                throw new EventDeckRefusal(
                    $"container '{containerId}': effect family '{effect.Family}' band '{effect.PowerBand}' " +
                    $"resolves to atom '{atomId}', which is not in the atom catalog");

            atoms.Add(new ContainerAtomRow(seq++, atomId));
        }

        return new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Atoms = atoms,
            Pool = Array.Empty<ContainerPoolRow>(),
            PrefixRolls = 0,
            SuffixRolls = 0,
        };
    }
}
