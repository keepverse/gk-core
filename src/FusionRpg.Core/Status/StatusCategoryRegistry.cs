namespace FusionRpg.Core.Status;

/// <summary>Normative StatusId → L2b category — status-ssot.md §9.5.</summary>
public static class StatusCategoryRegistry
{
    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wither"] = StatusL2bCategory.Dot,
        ["poison"] = StatusL2bCategory.Dot,
        ["leech"] = StatusL2bCategory.Dot,
        ["bond"] = StatusL2bCategory.Dot,
        ["rally"] = StatusL2bCategory.Dot,
        ["expose"] = StatusL2bCategory.Dot,
        ["command"] = StatusL2bCategory.Dot,
        ["shatter"] = StatusL2bCategory.Dot,
        ["butter"] = StatusL2bCategory.Cc,
        ["freeze"] = StatusL2bCategory.Cc,
        ["cold"] = StatusL2bCategory.Cc,
        ["hypno"] = StatusL2bCategory.Cc,
        ["ember"] = StatusL2bCategory.Cc,
        ["jala"] = StatusL2bCategory.Cc,
        ["kelp"] = StatusL2bCategory.Cc,
        ["charm_pulse"] = StatusL2bCategory.Cc,
        ["blight"] = StatusL2bCategory.Contagion,
        ["rot"] = StatusL2bCategory.Contagion,
        ["spark"] = StatusL2bCategory.Contagion,
        ["pact_mark"] = StatusL2bCategory.Contagion,
        ["spore"] = StatusL2bCategory.Contagion,

        // 9.5 Nerve (P3, delve-attrition D2.19) -- 21 -> 24, same Dot category expose/shatter/command
        // already use despite none of the four being literal damage over time.
        ["nerve.unsettled"] = StatusL2bCategory.Dot,
        ["nerve.shaken"] = StatusL2bCategory.Dot,
        ["nerve.afflicted"] = StatusL2bCategory.Dot,
    };

    public static IReadOnlyCollection<string> AllStatusIds => Map.Keys.ToList();

    /// <summary>
    /// Adds a new statusId → L2b category entry for a consumer outside the 21 locked ids
    /// (status-ssot.md §9) — e.g. the action program's exhaustion debuffs (spec-action-costs.md §7).
    /// Extensibility is deliberately code, not a runtime loader (status-ssot.md §3): this is that
    /// code path made callable instead of only reachable by hand-editing <see cref="Map"/> above.
    /// </summary>
    public static void Register(string statusId, string category)
    {
        if (string.IsNullOrWhiteSpace(statusId))
            throw new ArgumentException("statusId required", nameof(statusId));
        if (category != StatusL2bCategory.Dot && category != StatusL2bCategory.Cc && category != StatusL2bCategory.Contagion)
            throw new ArgumentException($"unknown L2b category '{category}'", nameof(category));
        Map[statusId] = category;
    }

    public static bool TryGetCategory(string statusId, out string category) =>
        Map.TryGetValue(statusId, out category!);

    public static string GetRequiredCategory(string statusId)
    {
        if (!TryGetCategory(statusId, out var category))
            throw new ArgumentException($"Unknown status id for L2b category: {statusId}", nameof(statusId));
        return category;
    }

    public static string PowerChannel(string statusId) =>
        $"status.power.{GetRequiredCategory(statusId)}";

    public static string ResistChannel(string statusId) =>
        $"status.resist.{GetRequiredCategory(statusId)}";
}
