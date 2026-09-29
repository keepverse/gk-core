using System.Text.Json;

namespace FusionRpg.Core.Actions.Seeding;

public sealed class EnablerPayoffPairingRejection : Exception
{
    public EnablerPayoffPairingRejection(string message) : base(message) { }
}

/// <summary>
/// T32 (spec-action-seeding.md §5): authored data — <c>gk-data/packs/fusion/data/seed/actions/pairings.json</c> — mapping
/// a conditional-payoff atom family to the enabler atom family (or families — any ONE suffices) that
/// must ride in the same pool for the payoff to ever fire against something the pool itself can
/// create. "The pair is the unit, not the action."
///
/// <para>Membership as a KEY here is what makes a family a "payoff" at all — a family absent from
/// this table is untracked and never flagged, exactly like an atom with no <c>group</c> row is simply
/// ungrouped rather than an error. Pairing is authored, never inferred by parsing a predicate tree.</para>
/// </summary>
public sealed class EnablerPayoffPairings
{
    readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _enablersByPayoff;

    EnablerPayoffPairings(IReadOnlyDictionary<string, IReadOnlyList<string>> enablersByPayoff) => _enablersByPayoff = enablersByPayoff;

    public bool IsPayoff(string atomFamily) => _enablersByPayoff.ContainsKey(atomFamily);

    /// <summary>Empty only for an unauthored family — a real payoff always authors at least one
    /// enabler (enforced at parse time below).</summary>
    public IReadOnlyList<string> EnablersOf(string payoffFamily) =>
        _enablersByPayoff.TryGetValue(payoffFamily, out var enablers) ? enablers : Array.Empty<string>();

    public static EnablerPayoffPairings Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new EnablerPayoffPairingRejection("enabler/payoff pairings: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new EnablerPayoffPairingRejection($"enabler/payoff pairings: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new EnablerPayoffPairingRejection("enabler/payoff pairings: root must be an object of payoff -> [enablers]");

            var table = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    throw new EnablerPayoffPairingRejection($"enabler/payoff pairings: '{prop.Name}' must be an array of enabler family ids");

                var enablers = new List<string>();
                foreach (var el in prop.Value.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String)
                        throw new EnablerPayoffPairingRejection($"enabler/payoff pairings: '{prop.Name}' has a non-string enabler entry");
                    enablers.Add(el.GetString()!);
                }

                if (enablers.Count == 0)
                    throw new EnablerPayoffPairingRejection(
                        $"enabler/payoff pairings: '{prop.Name}' authors zero enablers — a payoff with no possible " +
                        "enabler is the exact unreal combination §5 forbids pricing a discount for");

                table[prop.Name] = enablers;
            }

            return new EnablerPayoffPairings(table);
        }
    }
}
