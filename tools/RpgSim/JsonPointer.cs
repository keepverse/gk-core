using System.Globalization;
using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The scenario's selection grammar. A scenario picks values out of a response with a pointer, and
/// selection is the <i>only</i> thing it may do to a value — there is no expression, no arithmetic
/// and no function (<c>scenario-format.md</c> §1). This is the whole vocabulary:
///
/// <code>
///   $                     the root
///   .name                 an object member
///   [3]                   an array index
///   [-1]                  an index from the end (used by "the newest ledger row")
///   [1:3]                 a half-open slice
///   [*]                   every element
///   [id==7]               the single array element whose `id` equals 7
///   [id=={expeditionId}]  ... equals a value captured earlier in this run
/// </code>
///
/// <para><b>Result shape is a property of the selector, not of how many things matched.</b>
/// <c>[*]</c> and a slice always produce an array — zero, one or many elements — so a scenario written
/// for "every item" cannot silently become a scalar when the array happens to hold one. An index and a
/// key match produce the element itself. A key match that matches more than one element is a scenario
/// defect (the ambiguity RS1's <c>Single(...)</c> would have thrown on), never a silent first-wins.</para>
///
/// <para>A pointer that selects nothing returns <c>null</c>: "the reading has no such value" is a
/// verdict fact, never a silent zero. Malformed grammar throws — the whole pointer is parsed before
/// anything is selected, so a typo in a later segment is caught even when an earlier segment matched
/// nothing.</para>
/// </summary>
public static class JsonPointer
{
    public static JsonElement? Select(JsonElement root, string pointer,
        IReadOnlyDictionary<string, string>? context = null)
    {
        var segments = Tokenize(pointer, context);
        var current = new List<JsonElement> { root };
        var arrayShaped = false;

        foreach (var segment in segments)
        {
            var next = new List<JsonElement>();
            switch (segment.Kind)
            {
                case SegmentKind.Member:
                    foreach (var e in current)
                        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(segment.Name, out var v))
                            next.Add(v.Clone());
                    break;

                case SegmentKind.Wildcard:
                    arrayShaped = true;
                    foreach (var e in current)
                        if (e.ValueKind == JsonValueKind.Array)
                            next.AddRange(e.EnumerateArray().Select(x => x.Clone()));
                    break;

                case SegmentKind.Slice:
                    arrayShaped = true;
                    foreach (var e in current)
                    {
                        if (e.ValueKind != JsonValueKind.Array) continue;
                        var items = e.EnumerateArray().ToList();
                        var from = segment.From < 0 ? items.Count + segment.From : segment.From;
                        var to = segment.To < 0 ? items.Count + segment.To : segment.To;
                        for (var k = Math.Max(0, from); k < Math.Min(items.Count, to); k++) next.Add(items[k].Clone());
                    }
                    break;

                case SegmentKind.Index:
                    arrayShaped = false;
                    foreach (var e in current)
                    {
                        if (e.ValueKind != JsonValueKind.Array) continue;
                        var items = e.EnumerateArray().ToList();
                        var k = segment.From < 0 ? items.Count + segment.From : segment.From;
                        if (k >= 0 && k < items.Count) next.Add(items[k].Clone());
                    }
                    break;

                case SegmentKind.KeyMatch:
                    arrayShaped = false;
                    foreach (var e in current)
                    {
                        if (e.ValueKind != JsonValueKind.Array) continue;
                        var matches = e.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.Object
                                           && item.TryGetProperty(segment.Name, out var v)
                                           && ValueEquals(v, segment.Match))
                            .ToList();
                        if (matches.Count > 1)
                            throw new FormatException(
                                $"[{segment.Name}=={segment.RawMatch}] matched {matches.Count} elements in '{pointer}' — " +
                                "a key match must be unambiguous");
                        if (matches.Count == 1) next.Add(matches[0].Clone());
                    }
                    break;
            }

            current = next;
            if (current.Count == 0 && !arrayShaped) return null;
        }

        if (arrayShaped) return Pack(current);
        return current.Count == 0 ? null : current[0];
    }

    enum SegmentKind { Member, Index, Slice, Wildcard, KeyMatch }

    sealed class Segment
    {
        public SegmentKind Kind;
        public string Name = "";
        public int From;
        public int To;
        public JsonElement Match;
        public string RawMatch = "";
    }

    /// <summary>Parses the whole pointer before anything is selected. A malformed segment throws here
    /// whether or not an earlier segment matched, and a <c>{capturedName}</c> that was never captured
    /// throws here too — a scenario may not read a value it never captured.</summary>
    static List<Segment> Tokenize(string pointer, IReadOnlyDictionary<string, string>? context)
    {
        if (string.IsNullOrWhiteSpace(pointer) || pointer[0] != '$')
            throw new FormatException($"pointer must start with '$': '{pointer}'");

        var segments = new List<Segment>();
        var i = 1;
        while (i < pointer.Length)
        {
            var c = pointer[i];
            if (c == '.')
            {
                var start = ++i;
                while (i < pointer.Length && pointer[i] != '.' && pointer[i] != '[') i++;
                var name = pointer[start..i];
                if (name.Length == 0) throw new FormatException($"empty member name in '{pointer}'");
                segments.Add(new Segment { Kind = SegmentKind.Member, Name = name });
            }
            else if (c == '[')
            {
                var close = pointer.IndexOf(']', i);
                if (close < 0) throw new FormatException($"unclosed '[' in '{pointer}'");
                var token = pointer[(i + 1)..close];
                i = close + 1;
                segments.Add(TokenizeBracket(token, pointer, context));
            }
            else
            {
                throw new FormatException($"unexpected '{c}' at {i} in '{pointer}'");
            }
        }
        return segments;
    }

    static Segment TokenizeBracket(string token, string pointer,
        IReadOnlyDictionary<string, string>? context)
    {
        if (token == "*") return new Segment { Kind = SegmentKind.Wildcard };

        var eq = token.IndexOf("==", StringComparison.Ordinal);
        if (eq >= 0)
        {
            var field = token[..eq].Trim();
            var raw = token[(eq + 2)..].Trim();
            if (field.Length == 0) throw new FormatException($"empty key field in '{pointer}'");
            if (raw.Length == 0) throw new FormatException($"empty key value in '{pointer}'");
            return new Segment
            {
                Kind = SegmentKind.KeyMatch,
                Name = field,
                RawMatch = raw,
                Match = ResolveToken(raw, context, pointer)
            };
        }

        var colon = token.IndexOf(':');
        if (colon >= 0)
            return new Segment
            {
                Kind = SegmentKind.Slice,
                From = ParseIndex(token[..colon], pointer),
                To = ParseIndex(token[(colon + 1)..], pointer)
            };

        return new Segment { Kind = SegmentKind.Index, From = ParseIndex(token, pointer) };
    }

    static int ParseIndex(string raw, string pointer)
    {
        if (!int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            throw new FormatException($"not an index: '{raw.Trim()}' in '{pointer}'");
        return n;
    }

    /// <summary>A bracket key value: a captured name in braces, or a bare token compared by JSON
    /// kind (number to number, string to string). No coercion between kinds — `"1"` is not `1`.</summary>
    static JsonElement ResolveToken(string raw, IReadOnlyDictionary<string, string>? context, string pointer)
    {
        if (raw.Length > 2 && raw[0] == '{' && raw[^1] == '}')
        {
            var name = raw[1..^1];
            if (context is null || !context.TryGetValue(name, out var captured))
                throw new FormatException($"'{name}' was never captured (in '{pointer}')");
            if (long.TryParse(captured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                return JsonDocument.Parse(num.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();
            return JsonDocument.Parse(JsonSerializer.Serialize(captured)).RootElement.Clone();
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asNumber))
            return JsonDocument.Parse(asNumber.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();
        if (raw is "true" or "false")
            return JsonDocument.Parse(raw).RootElement.Clone();
        return JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement.Clone();
    }

    static bool ValueEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            return a.GetDecimal() == b.GetDecimal();
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
            return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
        if (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)
            return a.ValueKind == b.ValueKind;
        if (a.ValueKind == JsonValueKind.Null) return b.ValueKind == JsonValueKind.Null;
        return false;
    }

    static JsonElement Pack(List<JsonElement> items)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var e in items) e.WriteTo(w);
            w.WriteEndArray();
        }
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    /// <summary>Flattens a selected element to the scalar text a verdict prints and a digest
    /// canonicalizes: a string is itself, a number/bool is its invariant text, an object or array
    /// (including an array-shaped selection) is its canonical JSON. Never a domain-formatted value —
    /// no rounding, no unit, no date (that is the reading's own business).</summary>
    public static string? AsScalarText(JsonElement? e)
    {
        if (e is null) return null;
        var v = e.Value;
        return v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => CanonicalJson.Of(v)
        };
    }
}
