using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>Named content rules this module raises, under its own registered namespace — the same
/// "one code with a namespaced payload" shape `EventRules`/`ConsumableRules` already establish.</summary>
public static class QuestRules
{
    public const string Namespace = "quest";

    public const string DuplicateId = "quest.duplicate-id";
    public const string BadTemplate = "quest.bad-template";
    public const string TargetRefRequired = "quest.target-ref-required";
    public const string TargetRefNotAllowed = "quest.target-ref-not-allowed";
    public const string CountBandRequired = "quest.count-band-required";
    public const string CountBandNotAllowed = "quest.count-band-not-allowed";
    public const string BadCountBand = "quest.bad-count-band";
    public const string BadRewardBand = "quest.bad-reward-band";
    public const string BadScope = "quest.bad-scope";

    static QuestRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Forces the static constructor above to have run — same empty-body idiom
    /// `EventRules.EnsureRegistered`/`ConsumableRules.EnsureRegistered` already use.</summary>
    public static void EnsureRegistered() { }

    public static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>The load result — a catalog plus every rejection, never a thrown exception
/// (`EventCatalogLoad`'s own shape): N bad rows report N rejections in one pass, and the catalog
/// holds every GOOD row regardless.</summary>
public readonly record struct QuestCatalogLoad(QuestCatalog Catalog, IReadOnlyList<AtomRejection> Rejections);

/// <summary>
/// D4.9 (spec-delve-quests.md §1) — validates a quest anchor against the registry's nine objective
/// templates, target kinds, `countBand`/`rewardBand`/`scope` vocabularies, and compiles its optional
/// predicate tree. Every vocabulary is a caller-supplied parameter straight from
/// `DungeonRegistries`/`ObjectiveTemplateCatalog` — this file never hardcodes a registry member name,
/// so a re-vote of any vocabulary (`countBand`'s real shipped members are `lone·few·several·many`,
/// not the spec's own stale prose "few·some·most·all" — a documentation drift this loader is immune
/// to by construction, since it reads whatever `countBandMembers` the caller passes) changes nothing
/// here.
/// </summary>
public sealed class QuestCatalog
{
    /// <summary>Spec §1, verbatim: "`none` legal and required on the six count-less templates" — a
    /// FIXED set this module itself adds (`extract-with-item-kind` has a real `TargetKind.ItemKind`
    /// yet is still on this list, so it cannot be derived from `TargetKind` alone), the same
    /// "module's own vocabulary, not read from a registry" posture `EventCatalog.Consequences`
    /// already takes for its own closed set.</summary>
    public static readonly IReadOnlyList<string> CountLessTemplates = new[]
    {
        "kill-boss", "extract-with-item-kind", "bring-creature-home-alive",
        "finish-under-hunger", "survive-no-downed", "spend-no-provision",
    };

    readonly IReadOnlyDictionary<string, QuestRow> _byId;
    readonly IReadOnlyDictionary<string, ICompiledPredicate> _predicates;

    QuestCatalog(IReadOnlyDictionary<string, QuestRow> byId, IReadOnlyDictionary<string, ICompiledPredicate> predicates)
    {
        _byId = byId;
        _predicates = predicates;
    }

    public int Count => _byId.Count;

    public IReadOnlyList<QuestRow> All => _byId.Values.OrderBy(q => q.QuestId, StringComparer.Ordinal).ToList();

    public QuestRow? Resolve(string questId) => _byId.TryGetValue(questId, out var row) ? row : null;

    /// <summary><see cref="PredicateCompiler.Always"/> for a quest whose own tree was `null` (seed
    /// contract's `none` = always eligible) — never a lookup failure, since every row that reached the
    /// catalog compiled successfully by construction.</summary>
    public ICompiledPredicate PredicateFor(string questId) =>
        _predicates.TryGetValue(questId, out var compiled) ? compiled : PredicateCompiler.Always;

    public static QuestCatalogLoad Load(
        IReadOnlyList<QuestRow> rows,
        IReadOnlyList<ObjectiveTemplateDef> objectiveTemplates,
        IReadOnlyList<string> scopes,
        IReadOnlyList<string> rewardBands,
        IReadOnlyList<string> countBandMembers,
        Func<string, int> statusBit,
        Func<string, int>? elementId = null,
        Func<string, int>? stockBit = null)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        if (objectiveTemplates is null) throw new ArgumentNullException(nameof(objectiveTemplates));
        if (scopes is null) throw new ArgumentNullException(nameof(scopes));
        if (rewardBands is null) throw new ArgumentNullException(nameof(rewardBands));
        if (countBandMembers is null) throw new ArgumentNullException(nameof(countBandMembers));
        if (statusBit is null) throw new ArgumentNullException(nameof(statusBit));

        var templatesById = objectiveTemplates.ToDictionary(t => t.ObjectiveTemplateId, StringComparer.Ordinal);
        var fails = new List<AtomRejection>();
        var byId = new Dictionary<string, QuestRow>(StringComparer.Ordinal);
        var predicates = new Dictionary<string, ICompiledPredicate>(StringComparer.Ordinal);

        foreach (var row in rows.OrderBy(r => r.QuestId, StringComparer.Ordinal))
        {
            if (byId.ContainsKey(row.QuestId))
            {
                fails.Add(QuestRules.Fail(QuestRules.DuplicateId, $"'{row.QuestId}' is defined twice"));
                continue;
            }

            if (!templatesById.TryGetValue(row.TemplateId, out var template))
            {
                fails.Add(QuestRules.Fail(QuestRules.BadTemplate, $"{row.QuestId}: '{row.TemplateId}' is not a known objective template"));
                continue;
            }

            var needsTargetRef = template.TargetKind is ObjectiveTargetKind.RoomKind or ObjectiveTargetKind.CurioKind or ObjectiveTargetKind.ItemKind;
            if (needsTargetRef && row.TargetRef is null)
            {
                fails.Add(QuestRules.Fail(QuestRules.TargetRefRequired, $"{row.QuestId}: template '{row.TemplateId}' needs a targetRef"));
                continue;
            }
            if (!needsTargetRef && row.TargetRef is not null)
            {
                fails.Add(QuestRules.Fail(QuestRules.TargetRefNotAllowed, $"{row.QuestId}: template '{row.TemplateId}' takes no targetRef"));
                continue;
            }

            var isCountLess = CountLessTemplates.Contains(row.TemplateId, StringComparer.Ordinal);
            if (isCountLess && row.CountBand is not null)
            {
                fails.Add(QuestRules.Fail(QuestRules.CountBandNotAllowed, $"{row.QuestId}: template '{row.TemplateId}' is count-less, countBand must be absent"));
                continue;
            }
            if (!isCountLess && row.CountBand is null)
            {
                fails.Add(QuestRules.Fail(QuestRules.CountBandRequired, $"{row.QuestId}: template '{row.TemplateId}' requires a countBand"));
                continue;
            }
            if (row.CountBand is not null && !countBandMembers.Contains(row.CountBand, StringComparer.Ordinal))
            {
                fails.Add(QuestRules.Fail(QuestRules.BadCountBand, $"{row.QuestId}: '{row.CountBand}' is not a known countBand member"));
                continue;
            }

            if (!rewardBands.Contains(row.RewardBand, StringComparer.Ordinal))
            {
                fails.Add(QuestRules.Fail(QuestRules.BadRewardBand, $"{row.QuestId}: '{row.RewardBand}' is not a known rewardBand member"));
                continue;
            }

            if (!scopes.Contains(row.Scope, StringComparer.Ordinal))
            {
                fails.Add(QuestRules.Fail(QuestRules.BadScope, $"{row.QuestId}: '{row.Scope}' is not delve/domain/roster"));
                continue;
            }

            var compileRejection = PredicateCompiler.TryCompile(row.Predicate, statusBit, out var compiled, elementId, stockBit);
            if (!compileRejection.IsOk)
            {
                fails.Add(AtomRejection.Fail(compileRejection.Reason, $"{row.QuestId}: {compileRejection.Detail}"));
                continue;
            }

            byId[row.QuestId] = row;
            predicates[row.QuestId] = compiled;
        }

        return new QuestCatalogLoad(new QuestCatalog(byId, predicates), fails);
    }
}
