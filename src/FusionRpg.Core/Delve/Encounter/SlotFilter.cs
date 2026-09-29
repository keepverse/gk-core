using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>The anchor's own `reach` ordinal (spec-encounter-generator.md §4) — melee/short/long are
/// the only three populated today (605/168/68 of 841 real anchors); siege is a real, legal, currently
/// empty member (§8's own named refusal case), never removed for being unpopulated.</summary>
public enum EncounterReach { Melee, Short, Long, Siege }

/// <summary>The anchor's own `targetPreference` ordinal — the default-pick rule `RankOrder` (D2.3)
/// reads; six real values confirmed on all 841/841 anchors (2026-09-06 corpus scan).</summary>
public enum TargetPreference { Frontline, Backline, Swarm, Elite, Structure, Indiscriminate }

/// <summary>One corpus row: an anchor's ordinals joined to its expanded species' numeric pass-through
/// fields, keyed by <see cref="SpeciesId"/> (spec-encounter-generator.md §1 Inputs — "the species
/// corpus as `ConcreteSpecies` rows... joined to their anchor ordinals"). The join is real, not spec
/// drift to paper over: neither <see cref="ConcreteSpecies"/> nor the live, post-catalog-runtime-flip
/// <see cref="CreatureSpeciesDef"/> carries <c>ThreatBand</c>, <c>AptitudePrimary</c>, <c>Reach</c> or
/// <c>TargetPreference</c> (confirmed by reading both 2026-09-06) — every one of those is consumed
/// into a numeric/derived field and discarded during species generation, so a filter over ordinals
/// has nowhere else to read them from.</summary>
public sealed record ConcreteAnchor
{
    public string SpeciesId { get; init; } = "";

    /// <summary>Null exactly when the anchor has not been classified yet (`threat-audit`'s own gap,
    /// §8's named refusal case) — never defaulted to a rung here.</summary>
    public string? ThreatBand { get; init; }

    /// <summary>Null iff <see cref="ThreatBand"/> is null — resolved once, at join time, from
    /// `creature-threat.v1.json`'s own rung table, never re-derived per filter call.</summary>
    public int? ThreatRung { get; init; }

    public string AptitudePrimary { get; init; } = "";
    public EncounterReach Reach { get; init; }
    public TargetPreference TargetPreference { get; init; }
    public ElementTypeId ElementPrimary { get; init; }
    public ElementTypeId? ElementSecondary { get; init; }
    public IReadOnlyList<string> TraitPool { get; init; } = Array.Empty<string>();
    public long AttackIntervalMs { get; init; }

    /// <summary>The anchor's own captured PvZ type id — <see cref="CreatureTypeId"/> is computed from
    /// this, never stored twice (<see cref="ConcreteSpecies"/>'s own established discipline).</summary>
    public int GameTypeId { get; init; }

    /// <summary>The anchor's side — carried so <see cref="CreatureTypeId"/> can apply the plant
    /// offset (species-gear-chain T19); without it this filter recomputed the 102-way collision.</summary>
    public string Side { get; init; } = "";

    public int CreatureTypeId => CreatureSpeciesCatalog.CreatureTypeIdFor(Side, GameTypeId);

    /// <summary>The species' rank (spec-species-rank.md §6's Delve row) — carried from the expanded
    /// <see cref="ConcreteSpecies.Rank"/>, NOT re-parsed from the anchor's own string, so there is one
    /// resolution path for the value the runtime rows already carry. Null means the pipeline skipped
    /// it (an unresolved threatBand/rarity) — this join never defaults it to a rung, and the filter
    /// logic here is unchanged by rank: a later gate (if any) reads it through
    /// <see cref="CreatureRankFloors.Passes"/>.</summary>
    public CreatureRank? Rank { get; init; }

    /// <summary>Joins one already-expanded species back to its own source anchor. Throws
    /// <see cref="InvalidOperationException"/> (never <see cref="EncounterRefusal"/> — that type
    /// names a SLOT's filter tuple, and there is no slot in scope during a corpus join) for a
    /// mismatched pairing or an unrecognised ordinal string — the same "not a known X" convention
    /// <see cref="SpeciesExpander"/> already uses for every other anchor enum field.</summary>
    public static ConcreteAnchor From(AnchorRow anchor, ConcreteSpecies species, CreatureThreatTuning threatTuning)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        if (species is null) throw new ArgumentNullException(nameof(species));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));
        if (!string.Equals(anchor.SpeciesId, species.SpeciesId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"anchor '{anchor.SpeciesId}' does not match species '{species.SpeciesId}' — not the same species");

        return new ConcreteAnchor
        {
            SpeciesId = anchor.SpeciesId,
            ThreatBand = anchor.ThreatBand,
            ThreatRung = anchor.ThreatBand is null ? null : RungFor(anchor.SpeciesId, anchor.ThreatBand, threatTuning),
            AptitudePrimary = anchor.AptitudePrimary,
            Reach = ParseReach(anchor.SpeciesId, anchor.Reach),
            TargetPreference = ParseTargetPreference(anchor.SpeciesId, anchor.TargetPreference),
            ElementPrimary = species.ElementPrimary,
            ElementSecondary = species.ElementSecondary,
            TraitPool = species.TraitPool,
            AttackIntervalMs = species.AttackIntervalMs,
            GameTypeId = species.GameTypeId,
            Side = species.Side,
            // Read off the expanded species (one resolution path for the enum), never re-parsed from
            // the anchor's own string — and NEVER defaulted: null travels through this join as null.
            Rank = species.Rank,
        };
    }

    static int RungFor(string speciesId, string threatBand, CreatureThreatTuning tuning)
    {
        foreach (var t in tuning.Thresholds)
            if (string.Equals(t.Id, threatBand, StringComparison.Ordinal)) return t.Rung;
        throw new InvalidOperationException(
            $"'{speciesId}': threatBand '{threatBand}' has no rung in creature-threat.v1.json");
    }

    static EncounterReach ParseReach(string speciesId, string reach) =>
        Enum.TryParse<EncounterReach>(reach, ignoreCase: true, out var r) ? r
            : throw new InvalidOperationException($"'{speciesId}': reach '{reach}' is not a known EncounterReach");

    static TargetPreference ParseTargetPreference(string speciesId, string targetPreference) =>
        Enum.TryParse<TargetPreference>(targetPreference, ignoreCase: true, out var t) ? t
            : throw new InvalidOperationException(
                $"'{speciesId}': targetPreference '{targetPreference}' is not a known TargetPreference");
}

/// <summary>One encounter slot — a filter tuple over anchor ordinals, never a new noun (ideal §11.4).
/// <see cref="Reach"/>/<see cref="TargetPreference"/> null means "any" (the seed contract's own
/// `none` admission, spec-dungeon-seed-contract.md §1.6). <see cref="CountBand"/> stays a raw band
/// name here (`lone`/`few`/`several`/`many`) — resolving it into `{min,max}` against
/// `encounter.v1.json` is `SlotFill`'s job (D2.2), not this filter's.</summary>
public sealed record EncounterSlot(
    Posture Posture, EncounterReach? Reach, TargetPreference? TargetPreference, string CountBand);

/// <summary>The anchor's threat rung must fall in `[FloorRung, CeilRung]` — `creature-threat.v1.json`'s
/// rungs 1-10 (spec-encounter-generator.md §2 step 2).</summary>
public sealed record ThreatWindow(int FloorRung, int CeilRung)
{
    public bool Contains(int rung) => rung >= FloorRung && rung <= CeilRung;
}

/// <summary>
/// `encounter-generator` D2.1 (spec-encounter-generator.md §2 step 2) — `Candidates` is a pure filter,
/// never a draw (no `SeededRng` reference anywhere in this file): count and weighted pick are
/// `SlotFill`'s job (D2.2).
/// </summary>
public static class SlotFilter
{
    /// <summary>
    /// Filters <paramref name="corpus"/> down to the anchors this slot can legally draw from.
    ///
    /// <para><b>A null <see cref="ConcreteAnchor.ThreatBand"/> anywhere in the corpus refuses
    /// immediately</b> — never skipped, never defaulted to the rung-4 fallback. This is deliberately
    /// eager: as long as `threat-audit` (the external dependency, ships classifications for the 657 of
    /// 841 anchors still missing one) has not finished, ANY corpus that still includes an unclassified
    /// anchor cannot be filtered at all, by design (§8's own "red now by design" acceptance for the
    /// pre-audit preflight fixture). A caller with a partially-classified corpus must pre-filter to the
    /// classified subset itself; this method will not do it silently.</para>
    /// </summary>
    public static IReadOnlyList<ConcreteAnchor> Candidates(
        IReadOnlyList<ConcreteAnchor> corpus, EncounterSlot slot, ThreatWindow window, IReadOnlySet<ElementTypeId> spreadSet)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (slot is null) throw new ArgumentNullException(nameof(slot));
        if (window is null) throw new ArgumentNullException(nameof(window));
        if (spreadSet is null) throw new ArgumentNullException(nameof(spreadSet));

        var list = new List<ConcreteAnchor>();
        foreach (var a in corpus) // caller's own order is trusted — ordinal SpeciesId order, never a dictionary
        {
            if (a.ThreatBand is null)
                throw new EncounterRefusal(slot, $"'{a.SpeciesId}' has no threatBand — never the rung-4 default");
            if (!window.Contains(a.ThreatRung!.Value)) continue;
            if (PostureOf(a.AptitudePrimary) != slot.Posture) continue; // AptitudeCatalog, never roster.json's own untrusted _derived echo
            if (slot.Reach is { } wantReach && a.Reach != wantReach) continue;
            if (slot.TargetPreference is { } wantTp && a.TargetPreference != wantTp) continue;
            if (!spreadSet.Contains(a.ElementPrimary)) continue;
            // HypnoAlly species (CreatureDeployMode) are candidates like any other — no special case here.
            list.Add(a);
        }

        if (list.Count == 0) throw new EncounterRefusal(slot, "unfillable — no anchor satisfies the tuple");
        return list;
    }

    /// <summary>Posture for an `aptitudePrimary` id, read from <see cref="AptitudeCatalog"/> — never
    /// `roster.json`'s own `posture` field, which is a `_derived` echo of this same code and is never
    /// trusted (spec-encounter-generator.md §2 step 2). Case-insensitive: anchors write `Bastion`-style
    /// casing that already matches <see cref="AptitudeCatalog"/>'s own ids exactly, but the match stays
    /// defensive rather than assuming a corpus-wide casing convention.</summary>
    public static Posture PostureOf(string aptitudePrimary)
    {
        foreach (var row in AptitudeCatalog.All)
            if (string.Equals(row.Id, aptitudePrimary, StringComparison.OrdinalIgnoreCase))
                return row.Posture;
        throw new InvalidOperationException($"'{aptitudePrimary}' is not a known aptitude id");
    }
}
