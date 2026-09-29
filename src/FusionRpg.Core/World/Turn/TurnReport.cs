namespace FusionRpg.Core.World.Turn;

public static class TurnReportKinds
{
    public const string CommandAccepted = "command.accepted";
    public const string CommandDropped = "command.dropped";
    public const string Calendar = "calendar";
    public const string Event = "event";
    public const string Battle = "battle";
}

/// <summary>
/// One line of what happened, in the order it happened.
///
/// <paramref name="SectorId"/> is what makes the line **projectable**: a turn report reaches a
/// player, and a report that narrates the far side of the map is a second door into the fog that
/// the state projection spends its whole existence closing. `Detail` is free text — filtering a
/// sector name out of prose works until somebody writes a different sentence — so anything that
/// happened *somewhere* names where, structurally.
///
/// Null means "nowhere in particular": a calendar tick, or a command refused before it named ground.
/// Those are shown to everyone, because they reveal nothing about the map.
///
/// <paramref name="Audience"/> is the other half of who may see a line (world-stage W12, fog defect
/// A): a faction-scoped line that names no ground — a handicap notice, a legion topping up its
/// supply — still needs to reach only the faction it is about, not "nowhere in particular" read as
/// "everyone". Null on every line built before this field existed, which reads as "no restriction",
/// exactly the behaviour those rows already had.
/// </summary>
public readonly record struct TurnReportEntry(
    string Phase, string Kind, string Subject, string Detail, string? SectorId = null, string? Audience = null);

/// <summary>
/// What a turn did — the presentation feed, the "while you were away" screen, and the record a
/// replay reproduces. The report IS the log: nothing in the engine writes anywhere else.
/// </summary>
public sealed class TurnReport
{
    readonly List<TurnReportEntry> _entries = new();
    readonly List<string> _phases = new();

    public IReadOnlyList<TurnReportEntry> Entries => _entries;

    /// <summary>Phases in the order they ran — the locked order, made observable.</summary>
    public IReadOnlyList<string> Phases => _phases;

    public IEnumerable<TurnReportEntry> Accepted =>
        _entries.Where(e => e.Kind == TurnReportKinds.CommandAccepted);

    public IEnumerable<TurnReportEntry> Dropped =>
        _entries.Where(e => e.Kind == TurnReportKinds.CommandDropped);

    /// <summary>
    /// Rebuilds a report from its own stored phase list and entries — the store's read path for the
    /// hot tail, once both are actually persisted. Trusts <paramref name="phases"/> outright rather
    /// than re-deriving it from <paramref name="entries"/>, because a phase that ran with zero
    /// entries (`Growth`'s own named no-op, most turns) has no entry to re-derive it *from* — see
    /// <see cref="FromEntries"/>'s own doc comment for why that reconstruction is lossy.
    /// </summary>
    public static TurnReport FromStored(IReadOnlyList<string> phases, IEnumerable<TurnReportEntry> entries)
    {
        var report = new TurnReport();
        report._phases.AddRange(phases);
        report._entries.AddRange(entries);
        return report;
    }

    /// <summary>
    /// Rebuilds a report from stored entries alone — the legacy fallback for a row committed before
    /// `phases_json` existed. **Lossy by construction**: a phase that ran with zero entries (`Growth`
    /// most turns) leaves no entry behind to reconstruct it from, so it silently vanishes from
    /// <see cref="Phases"/> rather than surviving as an empty section — exactly the "Nothing to
    /// report this phase" case `PlaybackRail`'s own GG-17 discipline exists to render, not hide.
    /// Prefer <see cref="FromStored"/>; this exists only so an old row still returns *something*
    /// rather than refusing outright.
    /// </summary>
    public static TurnReport FromEntries(IEnumerable<TurnReportEntry> entries)
    {
        var report = new TurnReport();
        foreach (var entry in entries)
        {
            report._entries.Add(entry);
            if (report._phases.LastOrDefault() != entry.Phase)
                report._phases.Add(entry.Phase);
        }

        return report;
    }

    internal void BeginPhase(string phase) => _phases.Add(phase);

    internal void Add(string phase, string kind, string subject, string detail, string? sectorId = null, string? audience = null) =>
        _entries.Add(new TurnReportEntry(phase, kind, subject, detail, sectorId, audience));

    /// <summary>
    /// The one door Data-side post-Step passes write through (empire-inventory-surfaces
    /// `cargo-commands` §Design 5 — the commit-transaction pass runs after `TurnEngine.Step`
    /// returns, so it cannot reach the internal <see cref="Add"/> the engine's own resolvers use,
    /// and `TurnEngine.cs` stays untouched per Locked anchors). Same entry shape, same closed
    /// kind vocabulary (`TurnReportKinds.Event` / `TurnReportKinds.CommandDropped`); the phase is
    /// the caller's to name honestly (the cargo pass uses `TurnEngine.Phases.Snapshot` — settlement
    /// after every phase ran, the phase claims already settle in).
    /// </summary>
    public void AddPostStep(string phase, string kind, string subject, string detail, string? sectorId = null, string? audience = null) =>
        Add(phase, kind, subject, detail, sectorId, audience);
}
