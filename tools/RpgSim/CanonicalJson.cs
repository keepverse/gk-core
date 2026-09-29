using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The one writing of a JSON value this program hashes and prints. Object members are emitted in
/// ordinal key order, arrays keep their order, numbers keep their raw text, and nothing is
/// indented — so two runs that produced the same reading produce the same bytes, and a diff is a
/// diff in the reading rather than in a formatter.
///
/// <para>The idiom is <c>gk-core/tools/SquadHarness/DeterminismHash.cs:19-25</c>'s: the hash is taken over
/// canonical JSON, never over the DTO's own serialization, because a serializer's member order and
/// number formatting are a library's business and must not move a digest.</para>
///
/// <para>This type writes values; it does not decide which values enter a digest. That decision —
/// the exclusion list, with a reason per field — belongs to RS2.2's verdict contract
/// (<c>readback-verdict.md</c>).</para>
/// </summary>
public static class CanonicalJson
{
    /// <summary>Relaxed escaping, deliberately: this writing goes to a file and to a console, never
    /// into HTML, and the alternative (the default encoder) writes a URL's `&amp;` as `\u0026` and a
    /// blanked field's marker as `\u003Cexcluded\u003E` — which makes a moved-pointer report unreadable
    /// for no gain. Determinism is unaffected: one encoder, used everywhere this type writes.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static string Of(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb);
        return sb.ToString();
    }

    static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name, Options));
                    sb.Append(':');
                    Write(p.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var f = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!f) sb.Append(',');
                    f = false;
                    Write(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(e.GetString(), Options));
                break;
            case JsonValueKind.Number:
                sb.Append(NormalizeNumber(e.GetRawText()));
                break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            default: sb.Append("null"); break;
        }
    }

    /// <summary>Numbers keep their value but not their spelling: `1.0` and `1` are the same reading,
    /// and `1e3` is the same as `1000`. Re-spelled through <see cref="decimal"/> rather than
    /// <see cref="double"/> so an integer magnitude above 2^53 keeps every digit it was written
    /// with (the range rule, AGENTS.md "Numeric types"). A value decimal cannot hold falls back to
    /// its raw text — never to a rounded double.</summary>
    static string NormalizeNumber(string raw) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            // "G29" strips the trailing zeros a decimal keeps for scale, so `1.0` and `1` write the
            // same bytes; it stays in Decimal so an integer magnitude above 2^53 keeps every digit
            // it was written with (the range rule). A value decimal cannot hold falls back to its raw
            // text, never to a rounded double.
            ? d.ToString("G29", CultureInfo.InvariantCulture)
            : raw;
}
