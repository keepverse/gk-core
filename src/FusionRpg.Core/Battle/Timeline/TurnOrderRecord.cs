using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Battle.Timeline;

// `battle-tempo` `forecast-rail` (FR2/FR3): lives under Core/Actions/ (the same
// KernelPurityScan tick-path exemption BasicAttack.cs and TimelineDispatch.cs already use), not
// Battle/Timeline/ despite its namespace — this is a once-per-battle rendering utility over a
// resolved trace, not tick-path kernel code, and Battle/Timeline/ has no per-file exemption model
// for ordinary LINQ. Moved 2026-09-05 after TimelinePurityGuardTests caught the misplacement
// (`.Concat(` on line 32 below, flagged as a tick-path violation) — the file's own logic is
// unchanged; only its physical location moved, so every caller (`WebMatchService.cs`,
// `TurnOrderRecordTests.cs`) needs no changes at all.

/// <summary>One entry in a rendered turn-order record — a NAME, never engine vocabulary
/// (spec-forecast-rail.md §2.4: "never actorKey, never tick numbers, never TurnState").</summary>
public readonly record struct TurnOrderEntry(int Round, string DisplayName);

/// <summary>
/// `battle-tempo` `forecast-rail` FR2/FR3 (spec-forecast-rail.md §2.0, D3): renders the ACTING ORDER
/// a resolved battle's `BattleTrace.Turns` already recorded — a record, not a forecast (§2.0's own
/// distinction). `Turns` itself is a raw debug log (`"{round} {actorKey} {from}->{to}"`) and must
/// never reach a player surface directly; this is the one, sanctioned projection that turns it into
/// names.
/// </summary>
public static class TurnOrderRecord
{
    /// <summary>
    /// Extracts the acting order from a resolved trace: one entry per `Ready -> Committed`
    /// transition (that IS the turn order — §2.1's own finding), in the order they occurred,
    /// resolved from `actorKey` to a display name via <paramref name="setup"/>. An actor whose
    /// species is unknown to <see cref="CreatureSpeciesCatalog"/> (a synthetic/golden fixture, never
    /// real content) falls back to its raw species id rather than throwing — a rendering nicety for
    /// test data, never a real production path, since real content's species ids are always known.
    /// </summary>
    public static IReadOnlyList<TurnOrderEntry> FromTrace(BattleTrace trace, BattleSetup setup)
    {
        if (trace is null) throw new ArgumentNullException(nameof(trace));
        if (setup is null) throw new ArgumentNullException(nameof(setup));

        // Two loops rather than `Squad.Concat(Wave)`: this file lives in the timeline kernel, where
        // KernelPurityScan bans LINQ on the tick path because it allocates an enumerator and a
        // delegate per call. The file previously sat under Actions/ while declaring the Timeline
        // namespace, so the purity scan — which globs Battle/Timeline/*.cs — never saw it. Moving it
        // to the folder its namespace always claimed is what surfaced this (solid-remediation T0.6).
        var nameByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var actor in setup.Squad)
            nameByKey[actor.Key] = NameOf(actor);
        foreach (var actor in setup.Wave)
            nameByKey[actor.Key] = NameOf(actor);

        static string NameOf(BattleActorSetup actor) =>
            CreatureSpeciesCatalog.IsKnown(actor.SpeciesId)
                ? CreatureSpeciesCatalog.Get(actor.SpeciesId).Name
                : actor.SpeciesId; // fallback for synthetic/golden fixtures only -- never real content

        var entries = new List<TurnOrderEntry>();
        foreach (var line in trace.Turns)
        {
            // "{round} {actorKey} {from}->{to}" -- only Ready->Committed transitions are the turn
            // order (spec §2.1); every other transition (Committed->Resolving, ->Recovering, ...) is
            // engine bookkeeping this projection deliberately never surfaces.
            var parts = line.Split(' ', 3);
            if (parts.Length != 3 || !int.TryParse(parts[0], out var round)) continue;
            if (!parts[2].EndsWith("->Committed", StringComparison.Ordinal)) continue;
            if (!parts[2].StartsWith("Ready->", StringComparison.Ordinal)) continue;

            var actorKey = parts[1];
            // Every actor that transitions Ready->Committed is, by construction, one of setup's own
            // Squad/Wave entries (the FSM has no other source of actors) -- so nameByKey should never
            // miss. The actorKey fallback below is defensive only, for a battle-tempo invariant this
            // projection cannot itself guarantee; if it is ever exercised, THAT is the real bug to
            // find, not a silently acceptable rendering path (it still leaks engine vocabulary, §2.4).
            var displayName = nameByKey.TryGetValue(actorKey, out var n) ? n : actorKey;
            entries.Add(new TurnOrderEntry(round, displayName));
        }

        return entries;
    }
}
