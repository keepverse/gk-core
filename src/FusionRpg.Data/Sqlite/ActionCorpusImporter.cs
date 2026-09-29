using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Rungs;

namespace FusionRpg.Data;

/// <summary>
/// A29 (action-corpus-import-completion): a same `action_id` appearing in two different source files
/// with a DIFFERENT payload — the proven historical defect shape
/// (`docs/architecture/action-corpus/audit-2026-09-13-distribution.md` §7 finding J, 11 ids across
/// `_rounds/round-1/survivors.json` and `committed-round-2000.json`). `ActionCorpusImporter.Import`'s
/// upsert is idempotent BY ID, which silently last-write-wins across files for this exact shape — this
/// exception is how the caller (`Program.cs`'s multi-file load step) refuses loudly instead.
/// </summary>
public sealed class ActionCorpusCrossFileCollisionException : Exception
{
    public ActionCorpusCrossFileCollisionException(string message) : base(message) { }
}

/// <summary>
/// T59.4 (spec-action-instance-and-grant.md §1): wires T59.3's pure composition to real persistence.
/// Given already-parsed briefs (file I/O is `FusionRpg.Server`'s startup step's job, never this
/// class's — tunables-ssot.md §7.2), composes each and writes it through the three already-built,
/// already-idempotent upserts (`UpsertContainer`/`UpsertAction`/`UpsertCost` each skip the write, and
/// the revision bump, when nothing differs — T30/E14a's own guard, exercised here, not re-implemented).
/// One brief's rejection is reported and skipped, never fatal to the rest of the batch — the same
/// "whole-row rejection, never partial" posture `RpgStore.BuildActionCatalog` already uses one level
/// up.
/// </summary>
public static class ActionCorpusImporter
{
    /// <summary>
    /// A29's defensive check, run by the caller BEFORE <see cref="Import"/> once briefs from every
    /// source file are parsed (`briefsByFile` keyed by filename, one entry per imported file). The same
    /// id with the SAME payload across two files is fine — the idempotent upsert already handles that —
    /// but a DIFFERENT payload for the same id is rejected loudly, naming the id and both files, rather
    /// than letting a flattened, file-provenance-free list silently pick whichever file the caller's
    /// array lists last (`audit-2026-09-13-distribution.md` §7 finding J's exact shape). Pure — no store,
    /// no file I/O, matching this corpus's own "no I/O outside `Program.cs`" convention.
    /// </summary>
    public static void AssertNoCrossFileIdCollisions(IReadOnlyDictionary<string, IReadOnlyList<ActionCorpusBrief>> briefsByFile)
    {
        if (briefsByFile is null) throw new ArgumentNullException(nameof(briefsByFile));

        var seen = new Dictionary<string, (string File, ActionCorpusBrief Brief)>(StringComparer.Ordinal);
        foreach (var (file, briefs) in briefsByFile)
        {
            foreach (var brief in briefs)
            {
                if (seen.TryGetValue(brief.Id, out var prior))
                {
                    if (!SamePayload(prior.Brief, brief))
                        throw new ActionCorpusCrossFileCollisionException(
                            $"action id '{brief.Id}' appears in both '{prior.File}' and '{file}' with different payloads");
                    // Same id, same payload across files — the idempotent upsert already handles this.
                }
                else
                {
                    seen[brief.Id] = (file, brief);
                }
            }
        }
    }

    // Record equality does not deep-compare `AtomFamilies` (an `IReadOnlyList<string>` — two separately
    // parsed instances are never reference-equal), so a manual field-by-field comparison is required.
    static bool SamePayload(ActionCorpusBrief a, ActionCorpusBrief b) =>
        a.Id == b.Id
        && a.Name == b.Name
        && a.Category == b.Category
        && a.Scope == b.Scope
        && a.ScopeKey == b.ScopeKey
        && a.RungFloor == b.RungFloor
        && a.RungCeiling == b.RungCeiling
        && a.AtomFamilies.SequenceEqual(b.AtomFamilies, StringComparer.Ordinal)
        && a.TargetMode == b.TargetMode
        && a.Relation == b.Relation
        && a.DescriptionKey == b.DescriptionKey
        && a.KindHint == b.KindHint;


    public readonly record struct BriefOutcome(string BriefId, bool Imported, string? Rejection, bool Disabled = false);

    public sealed record ImportResult(IReadOnlyList<BriefOutcome> Outcomes)
    {
        public int ImportedCount => Outcomes.Count(o => o.Imported);
        public int RejectedCount => Outcomes.Count(o => !o.Imported);
        public int DisabledCount => Outcomes.Count(o => o.Disabled);
    }

    public static ImportResult Import(
        RpgStore store,
        IReadOnlyList<ActionCorpusBrief> briefs,
        ActionCorpusCostTemplate costTemplate,
        RungTable rungTable)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (briefs is null) throw new ArgumentNullException(nameof(briefs));

        var outcomes = new List<BriefOutcome>(briefs.Count);
        foreach (var brief in briefs)
        {
            ActionCorpusComposeResult composed;
            try
            {
                composed = ActionCorpusComposer.Compose(
                    brief, costTemplate, rungTable,
                    store.ListAtomsByFamily, store.GetAtom);
            }
            catch (Exception ex) when (ex is ActionCorpusComposeRejection or ActionCorpusCostTemplateRejection)
            {
                // ST1: a brief that composed before but refuses now leaves its stale stored action
                // and pre-window container in the catalog. Upsert it disabled (a revision bump,
                // never a delete — grants may reference the id) and report it as its own outcome.
                var stored = store.GetAction(brief.Id);
                if (stored is not null && stored.Enabled)
                {
                    store.UpsertAction(stored with { Enabled = false });
                    outcomes.Add(new BriefOutcome(brief.Id, Imported: false, ex.Message, Disabled: true));
                }
                else
                {
                    outcomes.Add(new BriefOutcome(brief.Id, Imported: false, ex.Message));
                }
                continue;
            }

            var containerCheck = store.UpsertContainer(composed.Container);
            if (!containerCheck.IsOk)
            {
                outcomes.Add(new BriefOutcome(brief.Id, Imported: false, $"container: {containerCheck.Detail}"));
                continue;
            }

            var actionCheck = store.UpsertAction(composed.Row);
            if (!actionCheck.IsOk)
            {
                outcomes.Add(new BriefOutcome(brief.Id, Imported: false, $"action: {actionCheck.Detail}"));
                continue;
            }

            var costFailure = (string?)null;
            foreach (var cost in composed.Costs)
            {
                var costCheck = store.UpsertCost(cost);
                if (!costCheck.IsOk) costFailure = $"cost: {costCheck.Detail}";
            }

            outcomes.Add(costFailure is null
                ? new BriefOutcome(brief.Id, Imported: true, null)
                : new BriefOutcome(brief.Id, Imported: false, costFailure));
        }

        return new ImportResult(outcomes);
    }
}
