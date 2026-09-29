using FusionRpg.Core.Delve.Report;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>The result the stage's own tracker reads ("3 / 5 rooms") and `CloseDelve` pays on
/// (spec-delve-quests.md §3-§4).</summary>
public sealed record QuestVerdict(string QuestId, bool Done, int Have, int Need);

/// <summary>
/// D4.11 (spec-delve-quests.md §3) — `Evaluate` is pure and total: "recomputes from scratch every
/// call, holds no counter... same (quest, report) ⇒ same verdict" (spec, verbatim). `need` arrives as
/// a plain parameter, not a `QuestRow` field — D4.10's own `Draw` returns the catalog row, and the
/// spec's own worked pseudocode reads `q.Need` off a DIFFERENT, offer-level type
/// (`OfferedQuest{QuestId,Need}`, this module's "Interface exposed to dependents" table) that
/// `quests_json` actually persists; carrying it here as its own parameter avoids inventing that type
/// before whichever store-wiring task (D4.14) actually needs it.
///
/// <para><b>Predicate evaluation is deliberately NOT wired here.</b> The spec's own pseudocode calls
/// `q.Predicate.Evaluate(r.ExtractionFacts)` directly on the raw tree, but `PredicateNode` has no such
/// instance method — evaluating a compiled tree needs `ICompiledPredicate.Evaluate(ref FactReader)`
/// (confirmed by reading `PredicateCompiler.cs` directly), and building a `FactReader` from a
/// `DelveReport` is its own real design question this task's own acceptance line does not ask —
/// D4.9's `QuestCatalog.PredicateFor` already compiles the tree once at load; <paramref
/// name="predicateHolds"/> is a plain caller-supplied result (default: always true, `null`'s own
/// "no predicate" reading) so a future caller can wire the real evaluation in without this file
/// needing to know `FactReader`'s shape at all.</para>
/// </summary>
public static class QuestProgress
{
    public static QuestVerdict Evaluate(QuestRow quest, int need, DelveReport report, string hungerExhaustedStatusId, Func<bool>? predicateHolds = null)
    {
        if (quest is null) throw new ArgumentNullException(nameof(quest));
        if (report is null) throw new ArgumentNullException(nameof(report));

        var (have, done) = quest.TemplateId switch
        {
            "explore-rooms" => Count(report.Rooms.Count(x => x.Visited && !x.IsSecret), need),
            "cleanse-fights" => Count(report.Rooms.Count(x => x.Cleared && x.Kind == quest.TargetRef), need),
            "gather-curio-kind" => Count(report.Events.Count(e => e.Kind == quest.TargetRef && e.Choice != "leave" && e.Outcome != "nothing"), need),
            "kill-boss" => Flag(report.Kills.Any(k => k.Role == "boss")),
            "extract-with-item-kind" => Flag(report.Haul.Any(h => h.Role == quest.TargetRef)),
            "bring-creature-home-alive" => Flag(report.Members.All(m => !m.Downed)),
            "finish-under-hunger" => Flag(report.Members.All(m => !m.Statuses.Contains(hungerExhaustedStatusId, StringComparer.Ordinal))),
            "survive-no-downed" => Flag(report.Members.All(m => !m.DownedOnce)),
            "spend-no-provision" => Flag(!report.Decisions.Any(d => d.Kind == "pack.drop" && d.By == "use")),
            _ => throw new ArgumentException($"template '{quest.TemplateId}' has no evaluator", nameof(quest)),
        };

        var ok = done && (predicateHolds?.Invoke() ?? true);
        return new QuestVerdict(quest.QuestId, Done: ok, Have: have, Need: need);
    }

    static (int Have, bool Done) Count(int have, int need) => (have, have >= need);
    static (int Have, bool Done) Flag(bool done) => (done ? 1 : 0, done);
}
