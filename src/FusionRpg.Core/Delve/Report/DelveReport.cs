namespace FusionRpg.Core.Delve.Report;

/// <summary>One `rpg_delve_rooms` row's own fields a quest reads (spec-delve-quests.md §3's own
/// table). Deliberately a NEW, Core-owned type rather than `RpgStore`'s own `DelveRoomRow` (a
/// Data-layer type Core must never depend on) — the overlapping fields are projected into this shape
/// by whoever assembles a <see cref="DelveReport"/>, not shared by reference.</summary>
public sealed record DelveReportRoom(int Row, int Col, string Kind, bool Visited, bool Cleared, bool IsSecret, string? EventId);

/// <summary>One battle's own kill/survival result, joined to its setup's role (spec §3: "`SpeciesId`,
/// `Survived`, `Retreated`... joined to its `EncounterHalf` setup for the role").</summary>
public sealed record DelveReportKill(string RoomId, string SpeciesId, string Role);

/// <summary>One event-deck hook row (spec §3, `event-deck`'s own `events[]` extension). `Kind` is
/// missing from §3's own summary table (`roomId, eventId, outcomeOrdinal, choice`) but is read
/// directly by the spec's own worked `Evaluate` pseudocode (`gather-curio-kind`'s `e.Kind ==
/// q.TargetRef`) — the event anchor's real `Kind` vocabulary (`curio·encounter-event·shrine·trap·
/// bargain·story`, `event-deck`'s own registry) is a genuinely separate fact from `EventId`, so this
/// record follows the executable pseudocode over the summary table's omission.</summary>
public sealed record DelveReportEvent(string RoomId, string EventId, string Kind, string Outcome, string Choice);

/// <summary>One `decisions_json` entry — deliberately minimal: `Kind`/`By` are the only two fields
/// any of the nine templates' own `Evaluate` arms read (`spend-no-provision`'s `pack.drop`/`use`
/// check); the full decision shape (`route`, `talk`, `object.*`, seq, partyIndex) belongs to whichever
/// module actually needs those fields, not invented here on spec.</summary>
public sealed record DelveReportDecision(string Kind, string By);

/// <summary>One party member's own attrition-relevant state at report time (spec §3, `parties_json`).</summary>
public sealed record DelveReportMember(int PartyIndex, string InstanceId, bool Downed, bool DownedOnce, IReadOnlyList<string> Statuses);

/// <summary>One haul row a pack carried (spec §3, `parties_json` packs).</summary>
public sealed record DelveReportHaul(int PartyIndex, string RefId, string Role);

/// <summary>
/// D4.11 (spec-delve-quests.md §3) — the read model this module owns, assembled by the host
/// ("the host" — whichever caller reads `rpg_delve_rooms`/battle traces/`parties_json`, D4.14's own
/// unbuilt store wiring) from rows every OTHER module writes; this type touches no store itself. Six
/// slices, one per §3 table row — `ExtractionFacts`/predicate evaluation is deliberately NOT part of
/// this record (see `QuestProgress.Evaluate`'s own doc comment for why).
/// </summary>
public sealed record DelveReport(
    IReadOnlyList<DelveReportRoom> Rooms,
    IReadOnlyList<DelveReportKill> Kills,
    IReadOnlyList<DelveReportEvent> Events,
    IReadOnlyList<DelveReportDecision> Decisions,
    IReadOnlyList<DelveReportMember> Members,
    IReadOnlyList<DelveReportHaul> Haul);
