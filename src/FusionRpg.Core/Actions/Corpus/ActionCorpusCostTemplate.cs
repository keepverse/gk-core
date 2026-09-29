using System.Text.Json;

namespace FusionRpg.Core.Actions.Corpus;

/// <summary>One category's default onCommit cost row (spec-action-instance-and-grant.md §2, T59.1 —
/// narrowed during BUILD: the only per-category number `ActionTimingTuning` does not already own).
/// Used by the corpus importer (T59.3) only when a brief itself carries no cost data of its own.</summary>
public readonly record struct ActionCorpusCostTemplateRow(string ResourceId, int BaseAmountAtRung1, ActionCostTiming Timing);

public sealed class ActionCorpusCostTemplateRejection : Exception
{
    public ActionCorpusCostTemplateRejection(string message) : base(message) { }
}

/// <summary>Every `ActionCategory` must carry a row — a missing one is a REJECTION naming which
/// category, never a silent default (tunables-ssot.md's own reject-not-default discipline, the same
/// shape `ActionTimingTuning.CategoryOf` already uses for envelope timing).
///
/// <para><see cref="Kinds"/> is `basic-attack-seed`'s (T7) addition, spec-basic-attack-seed.md
/// §"Cost resolution must become Kind-aware": `Category` alone cannot express that a
/// <see cref="ActionKind.Basic"/> costs `stamina` while an <see cref="ActionKind.Innate"/> attack-
/// category signature move costs `qi` — both can share <c>Category = Attack</c>. Optional (defaults
/// null/empty) purely so every pre-T7 call site that constructs this record with one positional
/// argument keeps compiling unchanged; <see cref="ResolveFor"/> is the one new entry point
/// `ActionCorpusComposer` calls, and it still rejects rather than silently falling back once a kind
/// actually needs a row it doesn't have.</para></summary>
public sealed record ActionCorpusCostTemplate(
    IReadOnlyDictionary<ActionCategory, ActionCorpusCostTemplateRow> Categories,
    IReadOnlyDictionary<ActionKind, ActionCorpusCostTemplateRow>? Kinds = null)
{
    public ActionCorpusCostTemplateRow CategoryOf(ActionCategory category) =>
        Categories.TryGetValue(category, out var row) ? row : throw new ActionCorpusCostTemplateRejection(
            $"action-corpus-cost-templates tuning: no categories entry for '{category}'. Every ActionCategory " +
            "value must carry a cost row in config — there is no built-in default to fall back to.");

    /// <summary>Kind-aware resolution (T7): a non-<see cref="ActionKind.Skill"/> kind MUST resolve
    /// through its own <see cref="Kinds"/> row — a missing one is a REJECTION naming the kind, never a
    /// silent fall-through to <paramref name="category"/>. <see cref="ActionKind.Skill"/> (which is
    /// also what an absent `kindHint` defaults to) always resolves through <see cref="CategoryOf"/>,
    /// byte-for-byte the pre-T7 behaviour — so every brief authored before this field existed resolves
    /// exactly as it did before.</summary>
    public ActionCorpusCostTemplateRow ResolveFor(ActionKind kind, ActionCategory category)
    {
        if (kind == ActionKind.Skill)
            return CategoryOf(category);

        if (Kinds is not null && Kinds.TryGetValue(kind, out var row))
            return row;

        throw new ActionCorpusCostTemplateRejection(
            $"action-corpus-cost-templates tuning: no kinds entry for '{ActionKinds.Name(kind)}'. Every " +
            "non-Skill ActionKind must carry a cost row in config — there is no Category fallback for it.");
    }
}

/// <summary>Pure parser over `gk-core/data/tuning/action-corpus-cost-templates.v1.json` — no file I/O
/// (tunables-ssot.md §7.2: "Core never reads a file. Hosts load and inject."), mirroring
/// `ActionTimingTuningLoader`'s own established shape exactly.</summary>
public static class ActionCorpusCostTemplateLoader
{
    static readonly IReadOnlyDictionary<ActionCategory, string> CategoryKeys = new Dictionary<ActionCategory, string>
    {
        [ActionCategory.Attack] = "attack",
        [ActionCategory.Defense] = "defense",
        [ActionCategory.Support] = "support",
        [ActionCategory.Movement] = "movement",
        [ActionCategory.Status] = "status",
    };

    /// <summary>T7 (basic-attack-seed): the two non-Skill kinds a brief's `kindHint` can name. `Skill`
    /// is deliberately absent — it always resolves through `categories`, never a `kinds` row (see
    /// <see cref="ActionCorpusCostTemplate.ResolveFor"/>).</summary>
    static readonly IReadOnlyDictionary<ActionKind, string> KindKeys = new Dictionary<ActionKind, string>
    {
        [ActionKind.Basic] = "basic",
        [ActionKind.Innate] = "innate",
    };

    public static ActionCorpusCostTemplate Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ActionCorpusCostTemplateRejection("action-corpus-cost-templates tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ActionCorpusCostTemplateRejection($"action-corpus-cost-templates tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("categories", out var categoriesEl) || categoriesEl.ValueKind != JsonValueKind.Object)
                throw new ActionCorpusCostTemplateRejection("action-corpus-cost-templates tuning: missing or non-object 'categories'");

            var categories = new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>();
            foreach (var (category, key) in CategoryKeys)
            {
                if (!categoriesEl.TryGetProperty(key, out var catEl) || catEl.ValueKind != JsonValueKind.Object)
                    throw new ActionCorpusCostTemplateRejection($"action-corpus-cost-templates tuning: missing categories.{key}");
                categories[category] = ParseRow(catEl, $"categories.{key}");
            }

            // T7: `kinds` is required, matching `categories`' own reject-not-default discipline --
            // once a brief can carry a non-Skill `kindHint`, EVERY non-Skill kind must have a row here
            // or `ActionCorpusCostTemplate.ResolveFor` has nothing to resolve it to.
            if (!root.TryGetProperty("kinds", out var kindsEl) || kindsEl.ValueKind != JsonValueKind.Object)
                throw new ActionCorpusCostTemplateRejection("action-corpus-cost-templates tuning: missing or non-object 'kinds'");

            var kinds = new Dictionary<ActionKind, ActionCorpusCostTemplateRow>();
            foreach (var (kind, key) in KindKeys)
            {
                if (!kindsEl.TryGetProperty(key, out var kindEl) || kindEl.ValueKind != JsonValueKind.Object)
                    throw new ActionCorpusCostTemplateRejection($"action-corpus-cost-templates tuning: missing kinds.{key}");
                kinds[kind] = ParseRow(kindEl, $"kinds.{key}");
            }

            return new ActionCorpusCostTemplate(categories, kinds);
        }
    }

    /// <summary>The `{resourceId, baseAmountAtRung1, timing}` row shape, shared by every
    /// `categories.*` and `kinds.*` entry — one parser, one set of rejection messages.</summary>
    static ActionCorpusCostTemplateRow ParseRow(JsonElement obj, string path)
    {
        if (!obj.TryGetProperty("resourceId", out var resourceIdEl) || resourceIdEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(resourceIdEl.GetString()))
            throw new ActionCorpusCostTemplateRejection($"action-corpus-cost-templates tuning: missing or empty {path}.resourceId");

        if (!obj.TryGetProperty("baseAmountAtRung1", out var amountEl) || amountEl.ValueKind != JsonValueKind.Number
            || !amountEl.TryGetInt32(out var amount))
            throw new ActionCorpusCostTemplateRejection($"action-corpus-cost-templates tuning: missing or non-integer {path}.baseAmountAtRung1");
        if (amount <= 0)
            throw new ActionCorpusCostTemplateRejection($"action-corpus-cost-templates tuning: {path}.baseAmountAtRung1 must be > 0; got {amount}");

        if (!obj.TryGetProperty("timing", out var timingEl) || timingEl.ValueKind != JsonValueKind.String
            || !TryParseTiming(timingEl.GetString(), out var timing))
            throw new ActionCorpusCostTemplateRejection(
                $"action-corpus-cost-templates tuning: {path}.timing must be 'onCommit' or 'perTick'");

        return new ActionCorpusCostTemplateRow(resourceIdEl.GetString()!, amount, timing);
    }

    static bool TryParseTiming(string? raw, out ActionCostTiming timing)
    {
        switch (raw)
        {
            case "onCommit": timing = ActionCostTiming.OnCommit; return true;
            case "perTick": timing = ActionCostTiming.PerTick; return true;
            default: timing = default; return false;
        }
    }
}
