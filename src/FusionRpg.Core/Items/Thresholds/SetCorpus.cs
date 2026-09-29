using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>One <c>item_set_member</c> row: a specific base type, in a specific role, on one frame's ladder.</summary>
public readonly record struct SetMemberDef(string ContainerId, ItemRole Role, ItemFrame Frame);

/// <summary>One <c>item_set_tier</c> row: at this many DISTINCT member roles, this container is held.</summary>
public readonly record struct SetTierDef(int PiecesRequired, string ContainerId, bool IsCapability);

/// <summary>One authored set, as `gk-data/packs/fusion/data/seed/items/sets/*.json` ships it.</summary>
/// <param name="FlavourKey">
/// The set's own lore key, and <c>null</c> for the 24 of 30 shipped sets that authored none. Owner
/// decision 2026-09-06 (spec-item-lore.md §2.4): a set gets the card's Flavour block too, so this is
/// read rather than dropped. A key, never a literal — the sentence lives in N2's string catalog.
/// </param>
/// <param name="FlavourText">
/// The authored sentence beside the key, as the seed's <c>flavor</c> field ships it (6 of 30 sets).
/// Carried for the same reason <c>UniqueSeed.FlavourText</c> is: the catalog is generated from it, and
/// a key with no sentence beside it is a content gap somebody should be able to see.
/// </param>
/// <param name="SpeciesId">
/// species-gear-chain T27/T28 (spec-set-species-binding.md): the theme's own species, lower-case,
/// the runtime catalog's spelling — <c>null</c> for a <c>build.*</c>/<c>theme.*</c> themed set,
/// which has no species (explicit absence, never a guessed default). Identity only: no evaluator
/// reads it and no bonus magnitude derives from it (Success criterion 5) — a downstream consumer
/// (species-cost-shaping) reads it as a fact about the piece, never through this record's own logic.
/// </param>
public sealed record SetDef(
    string SetId,
    string DisplayName,
    IReadOnlyList<SetMemberDef> Members,
    IReadOnlyList<SetTierDef> Tiers,
    string? FlavourKey = null,
    string? FlavourText = null,
    string? SpeciesId = null)
{
    /// <summary>How many tiers this set can ever reach — its distinct member roles, not its member rows.</summary>
    public int DistinctRoleCount => Members.Select(m => m.Role).Distinct().Count();
}

/// <summary>
/// species-gear-chain T34d-wire — one container's species binding, resolved across every set that
/// names it. <see cref="SpeciesId"/> is <c>null</c> when the container belongs to no species-themed
/// set (or to none at all); <see cref="Ambiguous"/> is true when two or more sets naming this
/// container disagree about their own <see cref="SetDef.SpeciesId"/> — refused by the caller, never
/// guessed.
/// </summary>
public readonly record struct ContainerSpeciesLookup(string? SpeciesId, bool Ambiguous)
{
    public static readonly ContainerSpeciesLookup None = new(null, false);
}

public sealed class SetCorpusRejection : Exception
{
    public AtomRejection Rejection { get; }

    public SetCorpusRejection(string ruleId, string detail) : base($"{ruleId}: {detail}")
    {
        ThresholdEvaluator.EnsureRegistered();
        Rejection = AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// `gk-data/packs/fusion/data/seed/items/sets/*.json`, parsed into the three-table shape ssot-sets.md §4.2 declares. Pure —
/// the caller supplies the JSON text; Core never opens a file.
///
/// <para>The tier container ids are <b>derived</b> from <c>set_id</c> and <c>pieces</c>
/// (<see cref="ThresholdContainerIds.SetTier"/>), never authored: an authored copy is a second source
/// of truth for the same fact, and the zero pad is exactly the sort of detail an author gets wrong once
/// and nobody notices until a ten-piece set resolves its tiers backwards.</para>
/// </summary>
public static class SetCorpus
{
    public static IReadOnlyList<SetDef> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SetCorpusRejection("threshold.set-corpus-malformed", "empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new SetCorpusRejection("threshold.set-corpus-malformed", $"not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new SetCorpusRejection("threshold.set-corpus-malformed", "no 'entries' array");

            var result = new List<SetDef>();
            foreach (var e in entries.EnumerateArray()) result.Add(ReadEntry(e));
            return result;
        }
    }

    static SetDef ReadEntry(JsonElement e)
    {
        var id = Str(e, "id");

        // The corpus ships ids already in the container namespace (`set.frostbitten-vanguard-001`).
        // The set_id is the body; the tier ids then append `-{pieces:D2}` to it.
        const string prefix = "set.";
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
            throw new SetCorpusRejection("threshold.set-id-ungrammatical",
                $"set id '{id}' does not start with '{prefix}'");
        var setId = id[prefix.Length..];

        var displayName = e.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()!
            : setId;

        var members = new List<SetMemberDef>();
        if (e.TryGetProperty("members", out var membersEl) && membersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in membersEl.EnumerateArray())
            {
                var roleId = Str(m, "role");
                if (!ItemRoles.TryParse(roleId, out var role))
                    throw new SetCorpusRejection("threshold.set-member-unknown-role",
                        $"set '{setId}' names role '{roleId}', which is not in the core registry");

                var frameId = Str(m, "frame");
                var frame = frameId switch
                {
                    "humanoid" => ItemFrame.Humanoid,
                    "plant" => ItemFrame.Plant,
                    _ => throw new SetCorpusRejection("threshold.set-member-unknown-frame",
                        $"set '{setId}' names frame '{frameId}'; a member is drawn from one of the two " +
                        "pure ladders — 'hybrid' is a body, not a base-type ladder"),
                };

                members.Add(new SetMemberDef(Str(m, "baseType"), role, frame));
            }
        }

        if (members.Count == 0)
            throw new SetCorpusRejection("threshold.set-has-no-members",
                $"set '{setId}' declares no members, so no threshold can ever count and every bonus on " +
                "it is unreachable");

        var seenPairs = new HashSet<(ItemRole, ItemFrame)>();
        foreach (var m in members)
        {
            if (!seenPairs.Add((m.Role, m.Frame)))
                throw new SetCorpusRejection("threshold.set-member-duplicate-role-frame",
                    $"set '{setId}' declares (role {ItemRoles.Id(m.Role)}, frame {m.Frame}) twice — " +
                    "ssot-sets.md §4.2's UNIQUE (set_id, role, frame)");
        }

        var tiers = new List<SetTierDef>();
        if (e.TryGetProperty("thresholds", out var thEl) && thEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in thEl.EnumerateArray())
            {
                if (!t.TryGetProperty("pieces", out var pEl) || pEl.ValueKind != JsonValueKind.Number)
                    throw new SetCorpusRejection("threshold.set-tier-malformed",
                        $"set '{setId}' has a threshold with no numeric 'pieces'");
                var pieces = pEl.GetInt32();
                var isCapability = t.TryGetProperty("capability", out _);
                tiers.Add(new SetTierDef(pieces, ThresholdContainerIds.SetTier(setId, pieces), isCapability));
            }
        }

        if (tiers.Count == 0)
            throw new SetCorpusRejection("threshold.set-has-no-tiers",
                $"set '{setId}' declares no thresholds — a set with no bonus is not a set");

        var seenPieces = new HashSet<int>();
        foreach (var t in tiers)
            if (!seenPieces.Add(t.PiecesRequired))
                throw new SetCorpusRejection("threshold.set-tier-duplicate",
                    $"set '{setId}' declares two thresholds at {t.PiecesRequired} pieces — " +
                    "PRIMARY KEY (set_id, pieces_required)");

        var def = new SetDef(setId, displayName, members, tiers,
            OptStr(e, "flavorKey"), OptStr(e, "flavor"), OptStr(e, "speciesId"));

        // I5's completability rule, and the one check the whole seed tool was originally written for:
        // a threshold above the set's distinct ROLE count can never be reached, because counting is
        // per role and a role holds one item.
        var reachable = def.DistinctRoleCount;
        foreach (var t in tiers)
        {
            if (t.PiecesRequired < 1)
                throw new SetCorpusRejection("threshold.set-tier-malformed",
                    $"set '{setId}' has a threshold at {t.PiecesRequired} pieces");
            if (t.PiecesRequired > reachable)
                throw new SetCorpusRejection("threshold.set-tier-unreachable",
                    $"set '{setId}' has a {t.PiecesRequired}-piece threshold but only {reachable} distinct " +
                    "member role(s); counting is per role, so that tier can never fire");
        }

        return def;
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new SetCorpusRejection("threshold.set-corpus-malformed", $"missing or non-string '{key}'");
        return el.GetString()!;
    }

    /// <summary>An optional authored string. Absent and empty both read as <c>null</c> — "no flavour
    /// was authored" and "an empty one was" are the same fact, and only one of them should reach a
    /// render decision.</summary>
    static string? OptStr(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            && el.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// species-gear-chain T34d-wire — the container → species join `ItemWorkbench.RecipeContextFor`
    /// needs, the SAME shape <see cref="ThresholdEvaluator"/>'s own set-bonus join proves for a whole
    /// loadout (<c>SetEvaluator.Hits</c>'s <c>(ContainerId, Role) → set ids</c>), narrowed to ONE
    /// container's own <see cref="SetDef.SpeciesId"/> rather than a bonus count. A container naming no
    /// species-themed set (or none at all) resolves to <see cref="ContainerSpeciesLookup.None"/> — the
    /// legal, common, species-less case; two sets disagreeing about the SAME container's species is
    /// refused (never guessed), matching this whole program's own "refuse rather than pick either"
    /// posture for every other ambiguity it has found.
    /// </summary>
    public static ContainerSpeciesLookup SpeciesIdFor(string containerId, IReadOnlyList<SetDef> sets)
    {
        var speciesIds = sets
            .Where(s => s.Members.Any(m => string.Equals(m.ContainerId, containerId, StringComparison.Ordinal)))
            .Select(s => s.SpeciesId)
            .Where(id => id is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return speciesIds.Count switch
        {
            0 => ContainerSpeciesLookup.None,
            1 => new ContainerSpeciesLookup(speciesIds[0], Ambiguous: false),
            _ => new ContainerSpeciesLookup(null, Ambiguous: true),
        };
    }
}
