using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// The domain-importer's own input shape (spec-dungeon-seed-contract.md's domain content, not yet a
/// real, loadable type anywhere in `src/` — `domain-catalog`, Phase 4, hasn't built one). A plain
/// input parameter, the same "read model owned elsewhere" shape <see cref="EncounterAnchor"/> already
/// is; this module reads it, it does not parse domain content from disk.
/// </summary>
public sealed record EncounterDomain(string DomainId, ElementTypeId? Climate, IReadOnlyList<EncounterAnchor> Encounters);

/// <summary>How many real, banded corpus anchors fall at each of `creature-threat.v1.json`'s ten
/// rungs — "exactly what `threat-audit` changes" (spec §8).</summary>
public sealed record RungHistogramRow(int Rung, int Count);

public sealed record PreflightResult(
    IReadOnlyList<string> RefusedDomains, IReadOnlyDictionary<string, string> RefusalReasons,
    IReadOnlyList<RungHistogramRow> RungHistogram);

/// <summary>
/// `encounter-generator` D2.7 (spec-encounter-generator.md §8 "Preflight") — model-free: for every
/// domain, every encounter its room palette reaches, every slot, count candidates ignoring element,
/// then under the domain's own climate. Any zero refuses the WHOLE domain for shipping, with the row
/// named — "content did not choose" and "content chose wrong" stay the same DISCIPLINE this whole
/// program already applies elsewhere (`BattleModeProfileCatalog.Resolve`'s own doc), just at the
/// domain-authoring boundary instead of a runtime one.
///
/// <para><b>"Under climate" reads the climate element alone, never a spread-mode roll.</b> Mono,
/// dual and rainbow all include the climate element itself, so checking candidates against
/// <c>{climate}</c> alone is model-free (no RNG, matching this method's own "model-free" acceptance)
/// and a genuine LOWER bound: if a slot cannot even fill mono, it certainly cannot fill dual or
/// rainbow either, so refusing on the lower bound never falsely passes a domain that would fail at
/// runtime under a narrower spread draw. A null climate reads as all six, matching
/// <see cref="Encounter.Build"/>'s own identical override.</para>
/// </summary>
public static class EncounterPreflight
{
    static readonly IReadOnlySet<ElementTypeId> AllElements = Enum.GetValues<ElementTypeId>().ToHashSet();

    public static PreflightResult Run(IReadOnlyList<ConcreteAnchor> corpus, IReadOnlyList<EncounterDomain> domains, EncounterTuning tuning)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (domains is null) throw new ArgumentNullException(nameof(domains));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var refusedDomains = new List<string>();
        var refusalReasons = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var domain in domains)
        {
            if (refusalReasons.ContainsKey(domain.DomainId)) continue; // one row per domain, first cause wins
            var underClimateSpread = domain.Climate is { } c ? new HashSet<ElementTypeId> { c } : AllElements;

            foreach (var anchor in domain.Encounters)
            {
                foreach (var slot in anchor.Slots)
                {
                    var ignoringElement = CountCandidates(corpus, slot, anchor.ThreatWindow, AllElements);
                    var underClimate = CountCandidates(corpus, slot, anchor.ThreatWindow, underClimateSpread);

                    if (ignoringElement == 0 || underClimate == 0)
                    {
                        // The message names the slot's FULL tuple. `targetPreference` was the one
                        // member it used to omit, while `SlotFilter.Candidates` does filter on it —
                        // which made a refusal read as "no `Bastion`+`Melee` anchors exist" when the
                        // real demand was `Bastion`+`Melee`+`Swarm` (F15, party-dungeon-todo.md: a
                        // misdiagnosis that cost two sessions, 2026-09-07 and 2026-09-23).
                        var preferenceText = slot.TargetPreference is { } preference
                            ? $", targetPreference={preference}"
                            : "";
                        refusedDomains.Add(domain.DomainId);
                        refusalReasons[domain.DomainId] =
                            $"slot[posture={slot.Posture}, reach={slot.Reach?.ToString() ?? "any"}{preferenceText}] has " +
                            $"{ignoringElement} candidate(s) ignoring element, {underClimate} under climate " +
                            $"{(domain.Climate?.ToString() ?? "none")} — window [{anchor.ThreatWindow.FloorRung},{anchor.ThreatWindow.CeilRung}]";
                        goto nextDomain;
                    }
                }
            }
            nextDomain: ;
        }

        return new PreflightResult(refusedDomains, refusalReasons, RungHistogram(corpus));
    }

    /// <summary>A refusal (null threatBand anywhere in the corpus, or a genuinely unfillable tuple)
    /// counts as zero candidates for preflight purposes — both are real reasons a domain cannot ship,
    /// and preflight's own job is to say so, not to propagate the exception past its own boundary.</summary>
    static int CountCandidates(IReadOnlyList<ConcreteAnchor> corpus, EncounterSlot slot, ThreatWindow window, IReadOnlySet<ElementTypeId> spread)
    {
        try { return SlotFilter.Candidates(corpus, slot, window, spread).Count; }
        catch (EncounterRefusal) { return 0; }
    }

    static IReadOnlyList<RungHistogramRow> RungHistogram(IReadOnlyList<ConcreteAnchor> corpus)
    {
        var counts = new Dictionary<int, int>();
        foreach (var a in corpus)
            if (a.ThreatRung is { } rung)
                counts[rung] = counts.GetValueOrDefault(rung) + 1;
        return Enumerable.Range(1, 10).Select(r => new RungHistogramRow(r, counts.GetValueOrDefault(r))).ToList();
    }
}
