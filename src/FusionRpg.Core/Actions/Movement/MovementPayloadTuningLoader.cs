using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Movement;

public sealed class MovementPayloadRejection : Exception
{
    public MovementPayloadRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser + cross-checker for <c>data/tuning/movement-payload.v{n}.json</c> (tunables-ssot.md
/// §7.2 — no file I/O here; the caller reads the file). Every rejection names the row or list at
/// fault; a bad table never loads partially (spec-movement-payload.md §4, "a bad table never loads
/// partially" is this module's own restating of every other tuning loader's rule).
///
/// <para>Two families of check run here, both "fail loudly, never silently" (AC5):</para>
/// <list type="bullet">
/// <item>Pure JSON-schema checks — no numeric value anywhere in the three lists (AC1), every entry
/// carries a non-empty <c>id</c>/<c>description</c> and no other key (AC3), every description carries
/// a negative clause (AC2, same word-bounded <c>not</c>/<c>never</c> rule
/// <c>tools/seedsmith/.../validate_heal/schema_audit.py:27</c> already uses for the Python side of
/// this program), <c>payloadKinds</c> is exactly <c>{buff, status, tempo, none}</c> and always
/// includes <c>none</c> (AC3).</item>
/// <item>Cross-checks against the two vocabularies this module references and never extends (§3):
/// every channel id must resolve in the live <see cref="DerivedStatRegistry"/> (AC5), every status id
/// must resolve in the live <see cref="StatusCatalog"/> AND must not carry
/// <see cref="StatusPayloadKind.UnityCc"/> (AC5, AC5b — the predicate is the rule, not a hand-copied
/// list of 13 ids, so a future non-UnityCc executor for one of the 8 refused ids needs no spec edit
/// here, per §2's own "what would overturn it").</item>
/// </list>
/// </summary>
public static class MovementPayloadTuningLoader
{
    // Word-bounded so it never fires on a substring like "notice" or "innovation" — the exact rule
    // gk-forge/tools/seedsmith/seedsmith/adapters/actions/validate_heal/schema_audit.py:27 already uses for
    // this program's Python side (spec-validate-heal.md §2 Stage 0), reused here rather than a second
    // ad-hoc definition of "negative clause."
    static readonly Regex NegativeClause = new(@"\b(not|never)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The closed payloadKinds set (§2) — buff|status|tempo|none, never a fifth. `none` exists so a
    // planner can state "no payload" explicitly (AC3); the validator then refuses it for a movement
    // action (ActionValidator.ValidateMovementPayload, via MovementPayloadPolicy.HasStandalonePayload).
    static readonly string[] ClosedPayloadKinds = { "buff", "status", "tempo", "none" };

    public static MovementPayloadTuning Parse(string json, DerivedStatRegistry channelRegistry, StatusCatalog statusCatalog)
    {
        ArgumentNullException.ThrowIfNull(channelRegistry);
        ArgumentNullException.ThrowIfNull(statusCatalog);

        if (string.IsNullOrWhiteSpace(json))
            throw new MovementPayloadRejection("movement payload tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new MovementPayloadRejection($"movement payload tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            var channelsEl = RequireArray(root, "channels");
            var statusesEl = RequireArray(root, "statuses");
            var payloadKindsEl = RequireArray(root, "payloadKinds");

            // AC1: no numeric value of any kind anywhere in the three lists (schemaVersion/version/
            // _meta stay outside this scan — those are structural document plumbing every tuning file
            // in the repo carries, never a magnitude a balance pass would tune).
            RejectAnyNumber(channelsEl, "channels");
            RejectAnyNumber(statusesEl, "statuses");
            RejectAnyNumber(payloadKindsEl, "payloadKinds");

            var channels = ParseEntries(channelsEl, "channels");
            var statuses = ParseEntries(statusesEl, "statuses");
            var payloadKinds = ParseEntries(payloadKindsEl, "payloadKinds");

            foreach (var c in channels)
                if (!channelRegistry.IsKnown(c.Id))
                    throw new MovementPayloadRejection(
                        $"movement payload tuning: channels names unknown derived channel '{c.Id}' — " +
                        "not registered in DerivedStatRegistry, and this module never invents one (§3)");

            foreach (var s in statuses)
            {
                if (!statusCatalog.TryGet(s.Id, out var def))
                    throw new MovementPayloadRejection(
                        $"movement payload tuning: statuses names unknown status '{s.Id}' — not " +
                        "registered in StatusCatalogBootstrap, and this module never invents one (§3)");

                if (def.PayloadKinds.Contains(StatusPayloadKind.UnityCc))
                    throw new MovementPayloadRejection(
                        $"movement payload tuning: status '{s.Id}' carries StatusPayloadKind.UnityCc — " +
                        "a UnityCc status is delivered by the Unity CC executor, which needs the lawn, " +
                        "so admitting it would let a movement action pass HasStandalonePayload while " +
                        "being inert with the game closed (spec-movement-payload.md §2)");
            }

            var payloadKindIds = new List<string>(payloadKinds.Count);
            foreach (var p in payloadKinds)
            {
                if (Array.IndexOf(ClosedPayloadKinds, p.Id) < 0)
                    throw new MovementPayloadRejection(
                        $"movement payload tuning: payloadKinds names '{p.Id}', not one of the closed " +
                        $"set [{string.Join(", ", ClosedPayloadKinds)}]");
                payloadKindIds.Add(p.Id);
            }
            if (!payloadKindIds.Contains("none", StringComparer.Ordinal))
                throw new MovementPayloadRejection(
                    "movement payload tuning: payloadKinds must include 'none' so a planner can state " +
                    "\"no payload\" explicitly rather than by omission (§2)");

            return new MovementPayloadTuning(channels, statuses, payloadKinds);
        }
    }

    static JsonElement RequireArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new MovementPayloadRejection($"movement payload tuning: missing or non-array '{key}'");
        return el;
    }

    static List<MovementPayloadEntry> ParseEntries(JsonElement arrayEl, string listName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MovementPayloadEntry>();
        var i = 0;
        foreach (var el in arrayEl.EnumerateArray())
        {
            var path = $"{listName}[{i}]";
            if (el.ValueKind != JsonValueKind.Object)
                throw new MovementPayloadRejection($"movement payload tuning: '{path}' must be an object");

            string? id = null;
            string? description = null;
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.NameEquals("id"))
                {
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw new MovementPayloadRejection($"movement payload tuning: '{path}.id' must be a string");
                    id = prop.Value.GetString();
                }
                else if (prop.NameEquals("description"))
                {
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw new MovementPayloadRejection($"movement payload tuning: '{path}.description' must be a string");
                    description = prop.Value.GetString();
                }
                else
                {
                    throw new MovementPayloadRejection(
                        $"movement payload tuning: '{path}' carries unknown key '{prop.Name}' — every " +
                        "field is required and only 'id'/'description' exist (AC3)");
                }
            }

            if (string.IsNullOrWhiteSpace(id))
                throw new MovementPayloadRejection($"movement payload tuning: '{path}' is missing 'id'");
            if (string.IsNullOrWhiteSpace(description))
                throw new MovementPayloadRejection($"movement payload tuning: '{path}' is missing 'description'");
            if (!NegativeClause.IsMatch(description))
                throw new MovementPayloadRejection(
                    $"movement payload tuning: '{path}' description '{description}' carries no negative " +
                    "clause (a 'not'/'never' sentence naming what the entry is NOT — AC2)");
            if (!seen.Add(id))
                throw new MovementPayloadRejection($"movement payload tuning: '{listName}' names '{id}' more than once");

            result.Add(new MovementPayloadEntry(id, description));
            i++;
        }

        if (result.Count == 0)
            throw new MovementPayloadRejection(
                $"movement payload tuning: '{listName}' is empty — a closed list with zero members is not a valid vocabulary");

        return result;
    }

    // AC1: recurses into every object/array under a list, refusing the first JSON number found —
    // independent of key name, so a numeric value cannot slip past under an unexpected key the way a
    // pure "known keys only" check alone might miss if the schema itself were ever loosened later.
    static void RejectAnyNumber(JsonElement el, string path)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                throw new MovementPayloadRejection(
                    $"movement payload tuning: '{path}' carries a numeric value ({el.GetRawText()}) — " +
                    "this file is ids and prose only, no magnitude of any kind (the atom roll owns those, §3)");
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    RejectAnyNumber(prop.Value, $"{path}.{prop.Name}");
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    RejectAnyNumber(item, $"{path}[{i}]");
                    i++;
                }
                break;
        }
    }
}
