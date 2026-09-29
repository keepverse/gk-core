using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The closed assertion vocabulary, executed. Every check is a comparison, a membership test or a
/// shape test — nothing here computes a number, parses a date, or invents a value. That is what lets a
/// scenario be read as a claim about the server rather than as a program.
///
/// <para>Two checks carry the weight of the whole program:</para>
/// <list type="bullet">
/// <item><c>equalSet</c> / <c>allIn</c> / <c>allEqual</c> take their other side from a reading or a
/// capture, never a constant. "The squad the expedition fought with is the roster this run built" is
/// therefore a measurement (read-back vs read-back), not an expectation about a number a scenario
/// wrote down.</item>
/// <item><c>memberOf</c> is how a scenario pins a CLOSED ENUM — a run result, an XP kind — instead of
/// pinning a count or an outcome-dependent row set. A population is a reading; a vocabulary is a
/// contract.</item>
/// </list>
/// </summary>
public static class ScenarioExpectations
{
    /// <summary>Runs one assertion. Returns null when it holds, otherwise the failure, written so a
    /// reader can see both sides without opening the reading. <paramref name="context"/> is the run's
    /// captures, for the one check whose operand is a literal template (<c>allStartWith</c>).</summary>
    public static string? Evaluate(ScenarioStep step, JsonElement? left, JsonElement? other,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        var check = step.Check ?? "";
        var shownLeft = Describe(left);
        var shownOther = other is null ? Describe(step.Value) : Describe(other);

        switch (check)
        {
            case "equals":
                return JsonEquals(left, other ?? step.Value)
                    ? null
                    : $"{step.Path} is {shownLeft}, expected {shownOther}";

            case "notEmpty":
                return IsNonEmpty(left)
                    ? null
                    : $"{step.Path} is empty ({shownLeft})";

            case "allNonEmpty":
                return EveryElement(left, e => e.ValueKind == JsonValueKind.String
                                               && !string.IsNullOrWhiteSpace(e.GetString()))
                    ? null
                    : $"{step.Path} has a blank element ({shownLeft})";

            case "atLeast":
            {
                if (!TryNumber(step.Value, out var bound))
                    return $"{step.Check}: value must be a number";
                return EveryNumber(left, n => n >= bound)
                    ? null
                    : $"{step.Path} is {shownLeft}, expected at least {bound}";
            }

            case "nonZero":
                return EveryNumber(left, n => n != 0)
                    ? null
                    : $"{step.Path} is {shownLeft}, expected every element non-zero";

            case "memberOf":
            {
                if (step.Value is null || step.Value.Value.ValueKind != JsonValueKind.Array)
                    return $"{step.Check}: value must be an array (the closed set)";
                var set = step.Value.Value.EnumerateArray().ToList();
                return EveryElement(left, e => set.Any(s => JsonEquals(e, s)))
                    ? null
                    : $"{step.Path} is {shownLeft}, expected every element in {Describe(step.Value)}";
            }

            case "contains":
            {
                if (left is null) return $"{step.Path} is absent";
                var l = left.Value;
                if (l.ValueKind == JsonValueKind.Array)
                    return l.EnumerateArray().Any(e => JsonEquals(e, step.Value))
                        ? null : $"{step.Path} does not contain {Describe(step.Value)}";
                if (l.ValueKind == JsonValueKind.String)
                    return l.GetString()!.Contains(step.Value?.GetString() ?? "", StringComparison.Ordinal)
                        ? null : $"{step.Path} does not contain {Describe(step.Value)}";
                return $"{step.Path} is {shownLeft}, which cannot contain anything";
            }

            case "allStartWith":
            {
                var prefix = Interpolate(step.Value?.GetString() ?? "", context ?? Empty, out var interpolationError);
                if (interpolationError is not null) return interpolationError;
                return EveryElement(left, e => e.ValueKind == JsonValueKind.String
                                               && e.GetString()!.StartsWith(prefix, StringComparison.Ordinal))
                    ? null
                    : $"{step.Path} is {shownLeft}, expected every element to start with '{prefix}'";
            }

            case "equalSet":
            {
                var l = Elements(left);
                var r = Elements(other);
                if (l is null) return $"{step.Path} is {shownLeft}, which is not an array";
                if (r is null) return $"other side ({shownOther}) is not an array";
                var unmatched = l.Where(e => !r.Any(o => JsonEquals(e, o))).ToList();
                var extra = r.Where(e => !l.Any(o => JsonEquals(e, o))).ToList();
                if (unmatched.Count == 0 && extra.Count == 0) return null;
                return $"{step.Path} is not the same set as {shownOther}: " +
                       $"missing {DescribeList(unmatched)}, unexpected {DescribeList(extra)}";
            }

            case "allIn":
            {
                var l = Elements(left);
                var r = Elements(other);
                if (l is null) return $"{step.Path} is {shownLeft}, which is not an array";
                if (r is null) return $"other side ({shownOther}) is not an array";
                var outside = l.Where(e => !r.Any(o => JsonEquals(e, o))).ToList();
                return outside.Count == 0
                    ? null
                    : $"{step.Path} has {outside.Count} element(s) outside {shownOther}: {DescribeList(outside)}";
            }

            case "allEqual":
            {
                var l = Elements(left);
                if (l is null) return $"{step.Path} is {shownLeft}, which is not an array";
                if (other is null) return "other side is absent";
                var differs = l.Where(e => !JsonEquals(e, other)).ToList();
                return differs.Count == 0
                    ? null
                    : $"{step.Path} has {differs.Count} element(s) != {shownOther}: {DescribeList(differs)}";
            }

            default:
                return $"check '{check}' is not in the closed set ({string.Join("|", ScenarioVocabulary.Checks)})";
        }
    }

    /// <summary>
    /// A literal template: `{capturedName}` is replaced by the captured scalar. The only interpolation
    /// in the format, and it is a substitution, never an expression — used for a route placeholder and
    /// for `allStartWith`'s prefix (`exp-{expeditionId}-`). A name that was never captured is an error,
    /// never an empty string: a route with a hole in it must fail loudly.
    /// </summary>
    public static string Interpolate(string template, IReadOnlyDictionary<string, string?> context, out string? error)
    {
        error = null;
        var result = template;
        while (true)
        {
            var open = result.IndexOf('{');
            if (open < 0) return result;
            var close = result.IndexOf('}', open);
            if (close < 0) { error = $"unclosed '{{' in '{template}'"; return template; }
            var name = result[(open + 1)..close];
            if (string.IsNullOrWhiteSpace(name)) { error = $"empty placeholder in '{template}'"; return template; }
            if (context is null || !context.TryGetValue(name, out var value) || value is null)
            {
                error = $"'{name}' was never captured (in '{template}')";
                return template;
            }
            result = result[..open] + value + result[(close + 1)..];
        }
    }

    // ---- helpers ----------------------------------------------------------------------------- //

    static bool IsNonEmpty(JsonElement? e) => e is not null && e.Value.ValueKind switch
    {
        JsonValueKind.Array => e.Value.GetArrayLength() > 0,
        JsonValueKind.Object => e.Value.EnumerateObject().Any(),
        JsonValueKind.String => !string.IsNullOrWhiteSpace(e.Value.GetString()),
        _ => false
    };

    static List<JsonElement>? Elements(JsonElement? e) =>
        e is not null && e.Value.ValueKind == JsonValueKind.Array
            ? e.Value.EnumerateArray().Select(x => x.Clone()).ToList()
            : null;

    /// <summary>Applies a predicate to a scalar, or to every element of an array (an empty array
    /// passes vacuously — a scenario that needs a floor writes `notEmpty` beside it, which is exactly
    /// what the shipped scenario does).</summary>
    static bool EveryElement(JsonElement? e, Func<JsonElement, bool> predicate)
    {
        if (e is null) return false;
        var v = e.Value;
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().All(predicate);
        return predicate(v);
    }

    static bool EveryNumber(JsonElement? e, Func<decimal, bool> predicate) =>
        EveryElement(e, x => x.ValueKind == JsonValueKind.Number && predicate(x.GetDecimal()));

    static bool TryNumber(JsonElement? e, out decimal value)
    {
        value = 0;
        if (e is null || e.Value.ValueKind != JsonValueKind.Number) return false;
        value = e.Value.GetDecimal();
        return true;
    }

    static bool JsonEquals(JsonElement? a, JsonElement? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Value.ValueKind == JsonValueKind.Number && b.Value.ValueKind == JsonValueKind.Number)
            return a.Value.GetDecimal() == b.Value.GetDecimal();
        return string.Equals(CanonicalJson.Of(a.Value), CanonicalJson.Of(b.Value), StringComparison.Ordinal);
    }

    static string Describe(JsonElement? e) => e is null ? "<absent>" : CanonicalJson.Of(e.Value);

    static string DescribeList(IEnumerable<JsonElement> items) => items.Any()
        ? string.Join(", ", items.Select(x => Describe(x)))
        : "nothing";

    static readonly IReadOnlyDictionary<string, string?> Empty =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}
