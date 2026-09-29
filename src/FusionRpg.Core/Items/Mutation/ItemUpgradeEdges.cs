using System.Text.Json;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// Rule 1's production predicate (spec-item-upgrade-tree.md § 2a, resolved 2026-09-21): an affix is
/// legal on the successor when the affix family's own <c>roles</c> allow-list contains the successor's
/// own role — the *same* filter the drop path applies when it rolls an item's affixes, so the upgrade
/// agrees with the corpus by construction rather than by a second vocabulary.
///
/// <para>This is the injected seam the spec promises: if <c>affix_pool_tag</c> is ever emitted, the
/// caller passes <see cref="PoolTagLegality"/> instead and nothing else changes.</para>
/// </summary>
public sealed class RoleAllowListLegality : IAffixLegality
{
    readonly Func<string, IReadOnlySet<string>> _rolesOfFamily;

    /// <param name="rolesOfFamily">Each affix family id to the roles that may roll it. An unknown
    /// family is a refusal, never a pass: a rule that cannot read its own evidence must say so
    /// rather than let the affix through.</param>
    public RoleAllowListLegality(Func<string, IReadOnlySet<string>> rolesOfFamily) =>
        _rolesOfFamily = rolesOfFamily ?? throw new ArgumentNullException(nameof(rolesOfFamily));

    public string Rule => "affix-family-roles";

    public bool Allows(ItemUpgradeAffix affix, ItemUpgradeNode successor)
    {
        if (string.IsNullOrEmpty(successor.Role)) return false;
        var roles = _rolesOfFamily(affix.FamilyId);
        return roles is not null && roles.Contains(successor.Role);
    }
}

/// <summary>
/// species-gear-chain T38 (`item-upgrade-tree` b) — the authored edge table, in the shape the hub
/// holds. ⚠ <b>The C# registry parser that used to sit here was DELETED 2026-09-21:</b> once the runtime
/// was pointed at the emitted corpus (below), nothing in <c>src/</c> read
/// <c>_registry/successor-edges.v1.json</c> any more — the Python generator reads it and its closure gate
/// validates it, and a second C# parser with no production caller is how the duplicate-source defect this
/// file's own next comment describes would come back.
/// </summary>
public sealed record ItemUpgradeEdgeTable(
    int SchemaVersion, int Version, IReadOnlyDictionary<string, string> Edges);

/// <summary>
/// The CORPUS side of the same table — the reader the RUNTIME uses.
///
/// <para>⛔ Found 2026-09-21: the generator emits `successorOf` onto base-type rows and NOTHING in
/// <c>src/</c> read it (only refusal-message strings mentioned the word), while the executor read the
/// authoring registry instead — the same fact in two places, and an emitted field with no reader. The
/// registry stays what it is: the AUTHORING input the generator consumes and the closure gate validates.
/// The runtime reads what shipped with the content, which is also what the host already has on disk
/// (the base-type corpus beside `socketMax`/`class`/`implicit`).</para>
///
/// <para>A malformed document or entry is skipped rather than thrown, matching
/// <c>BaseTypeSocketMaxCorpus</c>'s own posture for this exact file family: a row whose edge a reader
/// cannot parse yields no edge, and the verb then refuses <c>upgrade.no-successor</c> — a visible, safe
/// failure rather than a guess.</para>
/// </summary>
public static class ItemUpgradeEdgeCorpusReader
{
    public static ItemUpgradeEdgeTable Parse(IEnumerable<string> baseTypeDocuments)
    {
        ArgumentNullException.ThrowIfNull(baseTypeDocuments);

        var edges = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var json in baseTypeDocuments)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (!entry.TryGetProperty("successorOf", out var target)) continue;
                    if (target.ValueKind != JsonValueKind.String) continue;

                    var sourceId = id.GetString();
                    var targetId = target.GetString();
                    if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId)) continue;
                    if (string.Equals(sourceId, targetId, StringComparison.Ordinal)) continue;

                    edges[sourceId!] = targetId!;
                }
            }
        }

        return new ItemUpgradeEdgeTable(1, 1, edges);
    }
}

/// <summary>Single configuration point for the executor's edge lookup. Like every hub here, it throws
/// when nothing configured it — there is no built-in edge to fall back to, and inventing one is the
/// class-ladder derivation the owner ruling forbids.</summary>
public static class ItemUpgradeEdgeHub
{
    static ItemUpgradeEdgeTable? _table;

    public static void Configure(ItemUpgradeEdgeTable table) =>
        _table = table ?? throw new ArgumentNullException(nameof(table));

    public static ItemUpgradeEdgeTable Tuning => _table ?? throw new InvalidOperationException(
        "ItemUpgradeEdgeHub.Configure(...) has not run. The executor reads " +
        "data/seed/items/_registry/successor-edges.v1.json — there is no built-in successor to fall back to.");

    /// <summary>The authored edge for a base type, or null when nobody authored one. Null is the
    /// ordinary state for every base type today and is what `upgrade.no-successor` reports.</summary>
    public static string? EdgeFor(string baseTypeId) =>
        Tuning.Edges.TryGetValue(baseTypeId, out var target) ? target : null;
}
