using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
// The `Encounter` class lives IN the `FusionRpg.Core.Delve.Encounter` namespace -- an unqualified
// `Encounter.Build(...)` is ambiguous with the namespace segment itself, the same collision
// `EncounterSeedContentTests.cs` already names and fixes the same way.
using static FusionRpg.Core.Delve.Encounter.Encounter;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>One domain's own coverage sample — spec-encounter-generator.md §8's own
/// `EncounterCoverage.Report(domain, rung, seeds)` citation, run for real (D4.31, party-dungeon-todo.md,
/// 2026-09-07). `RoomRefusals` names every `roomId` whose own `Encounter.Build` call threw
/// `EncounterRefusal` at least once across the sample — "a refusal names any domain that cannot fill a
/// slot" (D4.31's own verify line) — rather than letting one starved slot crash the whole report.</summary>
public sealed record DomainEncounterCoverageReport(
    string DomainId, int DistinctCells, bool MeetsBudget, IReadOnlyList<string> RoomRefusals);
/// <summary>
/// D4.31's own production bridge — `EncounterCoverage.cs`'s own pure counting functions (D2.7, already
/// shipped: <see cref="EncounterCoverage.DistinctCells"/>/<see cref="EncounterCoverage.MeetsBudget"/>)
/// stay UNTOUCHED, matching that file's own "this module owns none of the seeding or looping itself"
/// posture — this class OWNS the seeding/looping/domain-resolution the spec's own citation describes,
/// the same "row's own adapter, not the pure primitive" split D4.17's rows 4/6/7 already established.
///
/// <para>Samples ONLY `fight`/`elite`/`boss` archetypes (spec §8's own "a domain's fight/elite/boss
/// rooms" — `rest`/`merchant`/`curio`/`shrine`/`trap`/`wild`/`cache` never resolve through
/// `Encounter.Build` at all) drawn from the domain's own real `roomPalette`, resolved through each
/// room's own `encounterRef`. A `boss`-formation anchor has its `BossSpeciesRef` patched to the
/// domain's own real `bossSpeciesRef` first — the seed anchor itself never authors one (seed contract
/// §1.6: "not a species — the domain pins it at runtime"), the same patch
/// `EncounterSeedContentTests.cs` already applies for its own goldens.</para>
///
/// <para><b>Sibling-collision reporting (spec §8's own "StS rule") is
/// <see cref="SiblingCollisions"/></b>, the CALLER half of the already-shipped
/// <see cref="EncounterCoverage.SiblingCollisions"/> (D2.7's own pure primitive, which reads the
/// caller's grouping rather than re-deriving it). It was named "not built" here because it needs an
/// actual rolled `DelveGraph` to know which encounters share a graph ROW — D4.17 row 4's own bridge
/// shape, which has since landed (`DomainGraphPreflight.Build` rolls a real graph through
/// `DomainAnchorBuilder.From` + `DelveGraphRoll.Roll`), so the caller half is built here rather than
/// left as a gap. It is deliberately NOT folded into <see cref="Report"/>: `Report` samples each room
/// independently on its own derived seed and never rolls a graph at all, so giving it one would mix
/// two sampling models in one signature. A caller that already has a rolled graph (the preflight
/// bridge's own shape, or a test) calls both.</para>
/// </summary>
public static class DomainEncounterCoverage
{
    public static DomainEncounterCoverageReport Report(
        DomainRow domain,
        IReadOnlyList<string> roomPalette,
        IReadOnlyDictionary<string, RoomPaletteEntry> roomsById,
        IReadOnlyDictionary<string, string?> roomEncounterRefById,
        IReadOnlyDictionary<string, EncounterAnchor> encountersById,
        IReadOnlyList<ConcreteAnchor> corpus,
        int roomTheta,
        RaidModeTuning raid,
        DifficultyRungTuning rung,
        EncounterTuning tuning,
        CreatureThreatTuning threatTuning,
        AptitudeTuning aptitudeTuning,
        PowerTuning powerTuning,
        int sampleSeeds,
        int budgetTarget,
        int budgetToleranceUnder = 0)
    {
        if (domain is null) throw new ArgumentNullException(nameof(domain));
        if (roomPalette is null) throw new ArgumentNullException(nameof(roomPalette));
        if (roomsById is null) throw new ArgumentNullException(nameof(roomsById));
        if (roomEncounterRefById is null) throw new ArgumentNullException(nameof(roomEncounterRefById));
        if (encountersById is null) throw new ArgumentNullException(nameof(encountersById));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (raid is null) throw new ArgumentNullException(nameof(raid));
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));
        if (aptitudeTuning is null) throw new ArgumentNullException(nameof(aptitudeTuning));
        if (powerTuning is null) throw new ArgumentNullException(nameof(powerTuning));

        ElementTypeId? climate = ElementRoster.TryParse(domain.Climate, out var c) ? c : null;
        var cells = new List<EncounterCell>();
        var refusedRooms = new List<string>();

        foreach (var roomId in roomPalette)
        {
            if (!roomsById.TryGetValue(roomId, out var room)) continue;
            if (room.Kind is not ("fight" or "elite" or "boss")) continue;
            if (!roomEncounterRefById.TryGetValue(roomId, out var encounterRef) || encounterRef is null) continue;
            if (!encountersById.TryGetValue(encounterRef, out var anchor)) continue;

            var resolved = anchor.Formation == Formation.Boss ? anchor with { BossSpeciesRef = domain.BossSpeciesRef } : anchor;

            for (var i = 0; i < sampleSeeds; i++)
            {
                var streamName = $"domain-encounter-coverage:{domain.DomainId}:{roomId}:{i}";
                var seed = SeededRng.DeriveStream(0, streamName).NextULong();
                try
                {
                    var half = Build(resolved, roomTheta, climate, raid, rung, seed, corpus, tuning, threatTuning, aptitudeTuning, powerTuning);
                    cells.Add(half.Cell);
                }
                catch (EncounterRefusal)
                {
                    if (!refusedRooms.Contains(roomId, StringComparer.Ordinal)) refusedRooms.Add(roomId);
                }
            }
        }

        var distinct = EncounterCoverage.DistinctCells(cells);
        var meetsBudget = refusedRooms.Count == 0 && EncounterCoverage.MeetsBudget(distinct, budgetTarget, budgetToleranceUnder);
        return new DomainEncounterCoverageReport(domain.DomainId, distinct, meetsBudget, refusedRooms);
    }

    /// <summary>
    /// Spec §8, verbatim: "**StS sibling rule at graph rows:** two same-kind siblings on one row
    /// resolving to the same cell are a finding here and a filed ask on `delve-graph-roll`". Returns
    /// the colliding sibling GROUPS, each keyed `"{row}:{kind}"` — the grouping key is a plain string
    /// so <see cref="EncounterCoverage.SiblingCollisions"/> (D2.7, the pure primitive that owns the
    /// "did two cells come out identical" question) stays the ONE owner of the comparison, and this
    /// method owns only the part that needs a rolled graph: which facts are siblings, and what cell
    /// each one's own room draw produces.
    ///
    /// <para>Each fact's seed is its own room's named stream (`DelveStreams.Room(row, col)` off
    /// <paramref name="delveSeed"/>), which is exactly what spec §9 asks the caller for ("the caller's
    /// own `SeededRng.DeriveStream(dungeonSeed, "room:{r}:{c}")` derivation") — so two siblings
    /// resolve on genuinely independent rolls, and a collision means the anchor is too constrained to
    /// produce more than one shape, not that the two drew the same number.</para>
    ///
    /// <para>A room whose <see cref="Encounter.Build"/> refuses is skipped, never counted as a
    /// collision: <see cref="Report"/>'s own `RoomRefusals` is the named surface for that, and the
    /// spec's own words make a sibling collision a finding, never a refusal.</para>
    /// </summary>
    public static IReadOnlyList<string> SiblingCollisions(
        DelveGraph graph,
        IReadOnlyDictionary<string, string?> roomEncounterRefById,
        IReadOnlyDictionary<string, EncounterAnchor> encountersById,
        IReadOnlyList<ConcreteAnchor> corpus,
        ulong delveSeed,
        int roomTheta,
        ElementTypeId? climate,
        RaidModeTuning raid,
        DifficultyRungTuning rung,
        EncounterTuning tuning,
        CreatureThreatTuning threatTuning,
        AptitudeTuning aptitudeTuning,
        PowerTuning powerTuning,
        string bossSpeciesRef)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        if (roomEncounterRefById is null) throw new ArgumentNullException(nameof(roomEncounterRefById));
        if (encountersById is null) throw new ArgumentNullException(nameof(encountersById));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (raid is null) throw new ArgumentNullException(nameof(raid));
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));
        if (aptitudeTuning is null) throw new ArgumentNullException(nameof(aptitudeTuning));
        if (powerTuning is null) throw new ArgumentNullException(nameof(powerTuning));

        var drawn = new List<(string SiblingGroup, EncounterCell Cell)>();
        foreach (var fact in graph.Facts)
        {
            if (!roomEncounterRefById.TryGetValue(fact.ArchetypeId, out var encounterRef) || encounterRef is null) continue;
            if (!encountersById.TryGetValue(encounterRef, out var anchor)) continue;

            var resolved = anchor.Formation == Formation.Boss ? anchor with { BossSpeciesRef = bossSpeciesRef } : anchor;
            var seed = SeededRng.DeriveStream(delveSeed, DelveStreams.Room(fact.Row, fact.Col)).NextULong();
            try
            {
                var half = Build(resolved, roomTheta, climate, raid, rung, seed, corpus, tuning, threatTuning, aptitudeTuning, powerTuning);
                drawn.Add(($"{fact.Row}:{fact.Kind}", half.Cell));
            }
            catch (EncounterRefusal)
            {
                // Not a collision -- Report's own RoomRefusals names it.
            }
        }

        return EncounterCoverage.SiblingCollisions(drawn);
    }
}
