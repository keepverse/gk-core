using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>
/// ssot-charms.md §3.4's three AP classes. <b>The split is a runtime rule, not flavour</b> — it decides
/// whether a charm has a rolled half at all.
/// </summary>
public enum CharmClass
{
    /// <summary>1 AP, rolled, 0-1 pool rolls, no carry limit of its own.</summary>
    Minor = 0,

    /// <summary>2-3 AP, rolled, 1-2 pool rolls, no carry limit of its own.</summary>
    Standard,

    /// <summary>5 AP, <b>never rolled</b>, <c>unique_carry</c>, and an authored negative atom.</summary>
    Signet,
}

/// <summary>One <c>charm_def</c> row as `gk-data/packs/fusion/data/seed/items/charms/*.json` ships it.</summary>
/// <param name="UniqueCarry">
/// The copy cap is <b>1</b> for a signet, not §3.3's default of 2 — one gate, two limits, and the
/// tighter one is per <c>container_id</c>.
/// </param>
/// <param name="HasNegativeAtom">
/// A signet carries an authored negative atom (§6.1). It binds with the rest of the container and never
/// as a separable row: a drawback that can be dropped is not a drawback.
/// </param>
public sealed record CharmDef(
    string ContainerId,
    string DisplayName,
    string Axis,
    CharmClass Class,
    int ApCost,
    bool UniqueCarry,
    int PrefixRolls,
    int SuffixRolls,
    bool HasNegativeAtom)
{
    /// <summary>A signet's rolled half does not exist, so module 15's enhance/reroll must REFUSE on one
    /// rather than silently no-op.</summary>
    public bool HasRolledHalf => PrefixRolls + SuffixRolls > 0;
}

public sealed class CharmCorpusRejection : Exception
{
    public AtomRejection Rejection { get; }

    public CharmCorpusRejection(string ruleId, string detail) : base($"{ruleId}: {detail}")
    {
        ThresholdEvaluator.EnsureRegistered();
        Rejection = AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// `gk-data/packs/fusion/data/seed/items/charms/*.json`, parsed. Pure — no file I/O.
///
/// <para><b><c>charm_class</c> is a column, authored, never derived from <c>ap_cost</c>.</b> The two are
/// perfectly correlated in today's corpus (1 → minor, 2-3 → standard, 5 → signet), and deriving one from
/// the other would make a future 2-AP signet unrepresentable. <c>ap_cost</c> is never rolled either
/// (§3.3): "if it were, the whole game becomes rerolling for a 1-AP copy of a 5-AP charm." It is a base
/// type property, and the AP gate reads it, never an instance value.</para>
///
/// <para><b>⏸ The pouch, the AP gate and the five charm tables are NOT here.</b> D40 split them into
/// module 22 (`charm-carry`): five tables, a gate, five reason codes and a run-lifecycle hook is larger
/// than the evaluator it would have ridden inside. This file carries only what the evaluator and the
/// class rules need.</para>
/// </summary>
public static class CharmCorpus
{
    public static IReadOnlyList<CharmDef> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CharmCorpusRejection("threshold.charm-corpus-malformed", "empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new CharmCorpusRejection("threshold.charm-corpus-malformed", $"not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new CharmCorpusRejection("threshold.charm-corpus-malformed", "no 'entries' array");

            var result = new List<CharmDef>();
            foreach (var e in entries.EnumerateArray()) result.Add(ReadEntry(e));
            return result;
        }
    }

    static CharmDef ReadEntry(JsonElement e)
    {
        var id = Str(e, "id");
        var axis = Str(e, "axis");

        var classId = Str(e, "charmClass");
        var cls = classId switch
        {
            "minor" => CharmClass.Minor,
            "standard" => CharmClass.Standard,
            "signet" => CharmClass.Signet,
            _ => throw new CharmCorpusRejection("threshold.charm-class-unknown",
                $"charm '{id}' declares class '{classId}'; ssot-charms §3.4 has exactly three"),
        };

        var apCost = Int(e, "apCost");
        var prefixRolls = Int(e, "prefixRolls");
        var suffixRolls = Int(e, "suffixRolls");

        var uniqueCarry = e.TryGetProperty("uniqueCarry", out var ucEl)
                          && ucEl.ValueKind == JsonValueKind.True;

        var hasNegative = false;
        if (e.TryGetProperty("fixedAtoms", out var atomsEl) && atomsEl.ValueKind == JsonValueKind.Array)
            foreach (var a in atomsEl.EnumerateArray())
                if (a.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("sign", out var s) && s.ValueKind == JsonValueKind.String
                    && string.Equals(s.GetString(), "negative", StringComparison.Ordinal))
                    hasNegative = true;

        var def = new CharmDef(
            id,
            e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : id,
            axis, cls, apCost, uniqueCarry, prefixRolls, suffixRolls, hasNegative);

        ValidateClassRules(def);
        return def;
    }

    /// <summary>
    /// The three class rules that are runtime invariants rather than authoring taste. Each refuses by its
    /// own rule id, so a content author reads which one they broke.
    /// </summary>
    public static void ValidateClassRules(CharmDef def)
    {
        if (def.ApCost < 1)
            throw new CharmCorpusRejection("threshold.charm-ap-cost-invalid",
                $"charm '{def.ContainerId}' has apCost {def.ApCost}; a free charm is not a budget decision");

        if (def.Class != CharmClass.Signet) return;

        if (def.HasRolledHalf)
            throw new CharmCorpusRejection("threshold.charm-signet-has-rolled-half",
                $"signet '{def.ContainerId}' declares {def.PrefixRolls}+{def.SuffixRolls} pool rolls; " +
                "a signet is fixed-unique (§3.4, pool_rolls = 0) and module 15 must have nothing to act on");

        if (!def.UniqueCarry)
            throw new CharmCorpusRejection("threshold.charm-signet-not-unique-carry",
                $"signet '{def.ContainerId}' is not uniqueCarry; the signet copy cap is 1, not §3.3's default 2");

        if (!def.HasNegativeAtom)
            throw new CharmCorpusRejection("threshold.charm-signet-has-no-drawback",
                $"signet '{def.ContainerId}' carries no negative atom; §6.1 — a signet is a build, not a " +
                "stat stick, and a drawback that can be dropped is not a drawback");
    }

    static readonly Regex IntRe = new("^-?[0-9]+$", RegexOptions.Compiled);

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el)) throw new CharmCorpusRejection(
            "threshold.charm-corpus-malformed", $"missing '{key}'");
        if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
        if (el.ValueKind == JsonValueKind.String && IntRe.IsMatch(el.GetString() ?? ""))
            return int.Parse(el.GetString()!);
        throw new CharmCorpusRejection("threshold.charm-corpus-malformed", $"'{key}' is not numeric");
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new CharmCorpusRejection("threshold.charm-corpus-malformed", $"missing or non-string '{key}'");
        return el.GetString()!;
    }
}
