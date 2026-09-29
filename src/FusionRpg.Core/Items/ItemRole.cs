using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>
/// The fifteen equip roles, plus the reserved sixteenth (`Standard`) D14 declares and never
/// generates into. **Closed** — the registry's own `appendOnlyRule` forbids adding, removing or
/// renumbering a role in place; a change here is a `core.v1.json` `registryVersion` bump
/// (item-ideal.md, `slot-roles`, D1/D14).
/// </summary>
public enum ItemRole
{
    ArmamentPrimary,
    CoreGuard,
    WardArray,
    ArmamentSecondary,
    JewelMajor,
    Manipulator,
    Mantle,
    HeadGuard,
    Girdle,
    Sense,
    Footing,
    Infusion,
    Retinue,
    JewelMinorA,
    JewelMinorB,

    /// <summary>D14: the commander's own slot. Priced from a separate 100‰ commander budget, never
    /// drawn from the body's 1000‰. Declared; the generator emits nothing into it.</summary>
    Standard,
}

public static class ItemRoles
{
    /// <summary>The registry's own kebab-case `roleId` — never hand-transcribed elsewhere.</summary>
    public static string Id(ItemRole role) => role switch
    {
        ItemRole.ArmamentPrimary => "armament-primary",
        ItemRole.CoreGuard => "core-guard",
        ItemRole.WardArray => "ward-array",
        ItemRole.ArmamentSecondary => "armament-secondary",
        ItemRole.JewelMajor => "jewel-major",
        ItemRole.Manipulator => "manipulator",
        ItemRole.Mantle => "mantle",
        ItemRole.HeadGuard => "head-guard",
        ItemRole.Girdle => "girdle",
        ItemRole.Sense => "sense",
        ItemRole.Footing => "footing",
        ItemRole.Infusion => "infusion",
        ItemRole.Retinue => "retinue",
        ItemRole.JewelMinorA => "jewel-minor-a",
        ItemRole.JewelMinorB => "jewel-minor-b",
        ItemRole.Standard => "standard",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool TryParse(string? id, out ItemRole role)
    {
        foreach (ItemRole r in Enum.GetValues(typeof(ItemRole)))
            if (string.Equals(Id(r), id, StringComparison.Ordinal)) { role = r; return true; }
        role = default;
        return false;
    }
}

/// <summary>One role row, exactly as `core.v1.json`'s `roles.list[]` (or `commanderOnly[]`) declares
/// it — a mechanical read, never a hand-copy, so the two can never silently drift.</summary>
public sealed record ItemRoleDef(
    ItemRole Role, string HumanoidName, string PlantName, bool HybridEligible, int BudgetWeightMilli);

public sealed class ItemRoleRegistryRejection : Exception
{
    public ItemRoleRegistryRejection(string message) : base(message) { }
}

/// <summary>
/// The parsed registry (item-ideal.md, `slot-roles`). Pure parser, no file I/O — matching every
/// other `*TuningLoader` in this codebase (e.g. <see cref="FusionRpg.Core.Match.MatchTuningLoader"/>):
/// the caller supplies the JSON text, this throws on anything malformed rather than defaulting.
///
/// <para><b>There is no second roles registry.</b> The fifteen roles and their
/// <c>budgetWeightMilli</c> ship in `core.v1.json`'s own `roles.list[]` — a `roles.v1.json` would be
/// the two-sources-of-truth defect this program refuses everywhere else.</para>
/// </summary>
public static class ItemRoleRegistry
{
    public static IReadOnlyList<ItemRoleDef> Parse(string coreRegistryJson)
    {
        if (string.IsNullOrWhiteSpace(coreRegistryJson))
            throw new ItemRoleRegistryRejection("core registry: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(coreRegistryJson); }
        catch (JsonException ex) { throw new ItemRoleRegistryRejection($"core registry: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("roles", out var rolesEl))
                throw new ItemRoleRegistryRejection("core registry: no 'roles' section");

            var result = new List<ItemRoleDef>();
            if (rolesEl.TryGetProperty("list", out var listEl))
                foreach (var row in listEl.EnumerateArray())
                    result.Add(ReadRow(row));

            if (rolesEl.TryGetProperty("commanderOnly", out var commanderEl))
                foreach (var row in commanderEl.EnumerateArray())
                    result.Add(ReadRow(row, defaultWeight: 0, defaultHybridEligible: false));

            if (rolesEl.TryGetProperty("budgetWeightMilliTotal", out var totalEl) &&
                totalEl.ValueKind == JsonValueKind.Number)
            {
                var declaredTotal = totalEl.GetInt32();
                var summed = result.Where(r => r.Role != ItemRole.Standard).Sum(r => r.BudgetWeightMilli);
                if (summed != declaredTotal)
                    throw new ItemRoleRegistryRejection(
                        $"core registry: roles.list sums to {summed}‰ but budgetWeightMilliTotal declares {declaredTotal}‰");
            }

            return result;
        }
    }

    /// <summary>D3/D30: the twelve roles a hybrid body may host, at exactly 800‰.</summary>
    public static IReadOnlyList<ItemRoleDef> HybridCore(IReadOnlyList<ItemRoleDef> all) =>
        all.Where(r => r.HybridEligible).ToList();

    static ItemRoleDef ReadRow(JsonElement row, int? defaultWeight = null, bool? defaultHybridEligible = null)
    {
        var roleId = Str(row, "roleId");
        if (!ItemRoles.TryParse(roleId, out var role))
            throw new ItemRoleRegistryRejection($"core registry: unknown roleId '{roleId}'");

        var humanoid = Str(row, "humanoidName");
        var plant = Str(row, "plantName");

        bool hybridEligible;
        if (row.TryGetProperty("hybridEligible", out var heEl) && heEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            hybridEligible = heEl.GetBoolean();
        else if (defaultHybridEligible is { } dhe)
            hybridEligible = dhe;
        else
            throw new ItemRoleRegistryRejection($"core registry: '{roleId}' has no hybridEligible");

        int weight;
        if (row.TryGetProperty("budgetWeightMilli", out var wEl) && wEl.ValueKind == JsonValueKind.Number)
            weight = wEl.GetInt32();
        else if (defaultWeight is { } dw)
            weight = dw;
        else
            throw new ItemRoleRegistryRejection($"core registry: '{roleId}' has no budgetWeightMilli");

        return new ItemRoleDef(role, humanoid, plant, hybridEligible, weight);
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ItemRoleRegistryRejection($"core registry: missing or non-string '{key}'");
        return el.GetString()!;
    }
}
