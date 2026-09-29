using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// D4.17 row 6's own production bridge (party-dungeon-todo.md, 2026-09-07) — wires the real
/// `EncounterPreflight.Run` (D2.7, already shipped) into <see cref="DomainPreflightInputs.CheckEncounters"/>.
///
/// <para><b>Does NOT need row 4's sampled graphs</b>, despite spec-domain-catalog.md §2's own "rows 4-8
/// share the sampled graphs" prose — <see cref="EncounterPreflight.Run"/>'s real, already-shipped
/// signature takes "every encounter its room palette reaches" (its own doc comment) as a flat list per
/// domain, not a per-sampled-graph one. A real shipped room anchor names exactly one `encounterRef`
/// (`"none"` for a room with no combat), so the domain's reachable encounter set is a static property
/// of its `roomPalette`, resolvable without ever rolling a graph. Confirmed by reading the real function
/// body directly rather than trusting the spec's own simplified pseudocode citation — the same
/// "read the real signature, not the spec's" discipline row 4/5's own investigations already applied.</para>
///
/// <para><b>Surfaced a real, separate, previously-unknown finding along the way:</b> `ConcreteAnchor.From`
/// (the species-corpus join `EncounterPreflight.Run`'s own `corpus` parameter needs) had ZERO production
/// callers anywhere — the only place the real species corpus was ever assembled into `ConcreteAnchor`
/// rows was a test-only fixture. <see cref="EncounterCorpusBuilder"/> is the production equivalent,
/// built alongside this bridge.</para>
///
/// <para><b>`corpus` MUST already be pre-filtered to `ThreatBand is not null` by the caller.</b>
/// `SlotFilter.Candidates`'s own documented contract refuses EAGERLY the moment it meets ANY corpus
/// anchor with a null `ThreatBand`, in ordinal `SpeciesId` order, regardless of whether that anchor
/// would have matched the slot — this bridge does not re-filter internally (that would silently hide
/// the caller's own mistake), matching `EncounterSeedContentTests.ClassifiedCorpus`'s own established
/// "the caller pre-filters, this method will not do it silently" discipline. Passing the full,
/// unfiltered <see cref="EncounterCorpusBuilder.Build"/> output here would refuse every domain on
/// every slot (0 candidates, always) regardless of real content quality — the SAME shape as row 4's
/// own wild-room discovery, not a new kind of mistake.</para>
/// </summary>
public static class DomainEncounterPreflight
{
    public static Func<DomainRow, IReadOnlyList<DomainRefusal>> Build(
        IReadOnlyDictionary<string, IReadOnlyList<string>> roomPaletteByDomainId,
        IReadOnlyDictionary<string, string?> roomEncounterRefById,
        IReadOnlyDictionary<string, EncounterAnchor> encountersById,
        IReadOnlyList<ConcreteAnchor> corpus,
        EncounterTuning tuning)
    {
        if (roomPaletteByDomainId is null) throw new ArgumentNullException(nameof(roomPaletteByDomainId));
        if (roomEncounterRefById is null) throw new ArgumentNullException(nameof(roomEncounterRefById));
        if (encountersById is null) throw new ArgumentNullException(nameof(encountersById));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        return domain =>
        {
            // Row 3 (run before this one) already refuses an unreal palette cell; an absent lookup
            // here is unreachable in a real chain -- "nothing to check yet", not a second refusal.
            if (!roomPaletteByDomainId.TryGetValue(domain.DomainId, out var palette)) return Array.Empty<DomainRefusal>();

            var encounterIds = new List<string>();
            foreach (var roomId in palette)
            {
                if (!roomEncounterRefById.TryGetValue(roomId, out var encounterRef) || encounterRef is null) continue;
                if (!encounterIds.Contains(encounterRef, StringComparer.Ordinal)) encounterIds.Add(encounterRef);
            }

            var encounters = new List<EncounterAnchor>(encounterIds.Count);
            foreach (var id in encounterIds)
            {
                if (!encountersById.TryGetValue(id, out var anchor))
                    return new[] { new DomainRefusal(domain.DomainId, "domain.encounter:ref-missing", $"encounterRef '{id}' is not a real encounter") };
                encounters.Add(anchor);
            }

            ElementTypeId? climate = ElementRoster.TryParse(domain.Climate, out var c) ? c : null;
            var encounterDomain = new EncounterDomain(domain.DomainId, climate, encounters);

            var result = EncounterPreflight.Run(corpus, new[] { encounterDomain }, tuning);
            return result.RefusalReasons.TryGetValue(domain.DomainId, out var reason)
                ? new[] { new DomainRefusal(domain.DomainId, "domain.encounter:slot", reason) }
                : Array.Empty<DomainRefusal>();
        };
    }
}
