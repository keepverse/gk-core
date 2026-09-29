using System.Text.Json;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle;

/// <summary>
/// `battle-tempo` `battle-resources` (spec-battle-resources.md §2.2) — each actor resource pool's
/// max as a per-mille share of <see cref="BattleRuleset.BaseHp"/>. A projection of the shipped
/// ladder, never a second curve: the only numbers this module introduces are the shares themselves.
///
/// <para><c>hp</c> is deliberately absent — it mirrors <c>BattleActorSetup.MaxHp</c> directly (spec
/// §2.6), because two disagreeing HP maxima is a worse outcome than an incomplete row.</para>
/// </summary>
public sealed record BattleResourceTuning(
    int SchemaVersion, int Version,
    IReadOnlyDictionary<string, int> PoolShareMilli,
    IReadOnlyDictionary<string, int> RegenPerSecondShareMilli)
{
    /// <summary>Refuses rather than defaults: a resource the closed set ships but config forgot is a
    /// missing balance row, not a request for a built-in fallback (the same stance
    /// <see cref="BattleTuning.ProfileOf"/> already takes).</summary>
    public int ShareOf(string resourceId) =>
        PoolShareMilli.TryGetValue(resourceId, out var s) ? s : throw new BattleResourceTuningRejection(
            $"battle-resources tuning: no poolShareMilli entry for resource '{resourceId}'. Every " +
            "resource in DerivedStatChannels.ResourceIds except 'hp' must carry a share — there is no " +
            "built-in default to fall back to.");

    /// <summary>
    /// `lawn-combat-wire` T11 (spec-lawn-combat-calibration.md) — per-mille share of the resource's
    /// OWN pool max, regenerated per second. A projection of <see cref="ShareOf"/>'s own pool, never a
    /// flat absolute rate, so it stays proportionate at another theta the same way the pool max already
    /// does. `regenPerSecondShareMilli.v2` authors `stamina=50` (5%/s) and `0` for the other four —
    /// explicit zeros, not missing rows (poise's scarcity argument stands unchanged; hunger/spirit/qi
    /// have no cost mechanism spending them yet). Same refuse-not-default stance as <see cref="ShareOf"/>.
    /// </summary>
    public int RegenShareOf(string resourceId) =>
        RegenPerSecondShareMilli.TryGetValue(resourceId, out var s) ? s : throw new BattleResourceTuningRejection(
            $"battle-resources tuning: no regenPerSecondShareMilli entry for resource '{resourceId}'. " +
            "Every resource in DerivedStatChannels.ResourceIds except 'hp' must carry one (0 is a valid, " +
            "explicit share) — there is no built-in default to fall back to.");
}

public sealed class BattleResourceTuningRejection : Exception
{
    public BattleResourceTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class BattleResourceTuningLoader
{
    public static BattleResourceTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new BattleResourceTuningRejection("battle-resources tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new BattleResourceTuningRejection($"battle-resources tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("poolShareMilli", out var sharesEl) || sharesEl.ValueKind != JsonValueKind.Object)
                throw new BattleResourceTuningRejection("battle-resources tuning: missing or non-object 'poolShareMilli'");

            var shares = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prop in sharesEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var v))
                    throw new BattleResourceTuningRejection(
                        $"battle-resources tuning: poolShareMilli.{prop.Name} is not an integer");
                if (v < 0)
                    throw new BattleResourceTuningRejection(
                        $"battle-resources tuning: poolShareMilli.{prop.Name} must be >= 0 (it is a share of a pool); got {v}");
                shares[prop.Name] = v;
            }

            // Every id in the closed set except `hp` must be authored. A silently-missing share would
            // make exactly one pool max 0 -- which is the bug this whole module exists to end, and it
            // would reappear as "the counter declines sometimes" rather than as a load error.
            foreach (var id in DerivedStatChannels.ResourceIds)
            {
                if (id == "hp") continue;
                if (!shares.ContainsKey(id))
                    throw new BattleResourceTuningRejection(
                        $"battle-resources tuning: poolShareMilli has no entry for '{id}'. Every resource " +
                        "in the closed set except 'hp' must carry one — a missing share silently produces " +
                        "an empty pool, which is the defect this module removes.");
            }

            if (shares.ContainsKey("hp"))
                throw new BattleResourceTuningRejection(
                    "battle-resources tuning: poolShareMilli must NOT carry 'hp' — hp's max mirrors " +
                    "BattleActorSetup.MaxHp directly (spec-battle-resources.md §2.6), and a share here " +
                    "would create a second, disagreeing HP maximum.");

            // T11 (lawn-combat-wire, spec-lawn-combat-calibration.md): `regenPerSecondShareMilli` is
            // OPTIONAL at this parse boundary — v1 (and any hand-built fixture in these tests) never
            // carried it, and resource-subtick's own scope boundary is "battle stays byte-identical
            // while the regen rows remain absent" (spec-resource-subtick.md), so absence must still
            // parse, defaulting every id to a share of 0 (today's structural zero). When the block IS
            // present, it is held to the SAME closed-coverage rule as poolShareMilli: every non-hp id,
            // no hp — a half-authored block would silently regen some pools and not others.
            var regenShares = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("regenPerSecondShareMilli", out var regenEl))
            {
                if (regenEl.ValueKind != JsonValueKind.Object)
                    throw new BattleResourceTuningRejection("battle-resources tuning: 'regenPerSecondShareMilli' must be an object");

                foreach (var prop in regenEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var v))
                        throw new BattleResourceTuningRejection(
                            $"battle-resources tuning: regenPerSecondShareMilli.{prop.Name} is not an integer");
                    if (v < 0)
                        throw new BattleResourceTuningRejection(
                            $"battle-resources tuning: regenPerSecondShareMilli.{prop.Name} must be >= 0 (it is a share of a pool); got {v}");
                    regenShares[prop.Name] = v;
                }

                if (regenShares.ContainsKey("hp"))
                    throw new BattleResourceTuningRejection(
                        "battle-resources tuning: regenPerSecondShareMilli must NOT carry 'hp' — hp has " +
                        "no regen row, the same reason poolShareMilli excludes it.");

                foreach (var id in DerivedStatChannels.ResourceIds)
                {
                    if (id == "hp") continue;
                    if (!regenShares.ContainsKey(id))
                        throw new BattleResourceTuningRejection(
                            $"battle-resources tuning: regenPerSecondShareMilli has no entry for '{id}'. " +
                            "A present block must cover every non-'hp' resource explicitly (0 is a valid " +
                            "share) — a half-authored block would silently regen some pools and not others.");
                }
            }
            else
            {
                foreach (var id in DerivedStatChannels.ResourceIds)
                {
                    if (id == "hp") continue;
                    regenShares[id] = 0;
                }
            }

            return new BattleResourceTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                PoolShareMilli: shares,
                RegenPerSecondShareMilli: regenShares);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new BattleResourceTuningRejection($"battle-resources tuning: missing or non-integer '{key}'");
        return v;
    }
}
