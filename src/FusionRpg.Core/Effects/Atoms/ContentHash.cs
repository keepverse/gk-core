using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The content hash (spec-content-hash.md, definitions §8). Makes a changed number <b>visible</b>:
/// once effect content lives in rows, a golden that moves is either a bug or a balance edit, and
/// without a stamp nobody can tell which.
///
/// <code>
/// rowDigest   = SHA256(canonical(row))
/// tableDigest = SHA256(concat(sort(rowDigests)))      // sorted, never folded
/// contentHash = SHA256(concat(tableDigest per covered table, in registry order))
/// </code>
///
/// <para><b>XOR-fold is banned.</b> XOR cancels duplicates, so a non-idempotent import that doubled
/// every row would leave the hash unchanged — and the importer's own "import twice, hash unchanged"
/// test would pass while the database doubled. The cheaper option is the broken one.</para>
///
/// <para><b>Columns are length-prefixed</b>, not separator-joined. A bare <c>0x1f</c> separator is not
/// injective: <c>(name = "a\x1fb", note = "c")</c> and <c>(name = "a", note = "b\x1fc")</c> serialise
/// identically, and both <c>name</c> and <c>power_note</c> are free text.</para>
///
/// <para>Pure and side-effect free. It reads no database — the data layer hands it rows.</para>
/// </summary>
public static class ContentHash
{
    /// <summary>Nesting past this is treated as opaque text rather than risking the stack.</summary>
    const int MaxJsonDepth = 64;

    /// <summary>The digest of a covered table with no rows — recognisable, not a stable-looking accident.</summary>
    public static byte[] EmptyDigest() => SHA256.HashData(Array.Empty<byte>());

    // ---- canonical form -------------------------------------------------------------------------

    /// <summary>
    /// One column value: <c>{byteLen}:{bytes}</c> for a value, and the sentinel <c>N:</c> for NULL.
    ///
    /// <para><b>NULL is a marker, not a payload.</b> definitions §8 encoded it as a literal
    /// <c>0x00</c> byte and argued the length prefix kept it distinct from a string containing one.
    /// It does not: a column holding exactly <c>"\0"</c> is also one byte of <c>0x00</c> under
    /// prefix <c>1:</c>, so the two forge the same digest. <c>N</c> is not a digit, so no length can
    /// ever produce this prefix and no value can impersonate a NULL.</para>
    /// </summary>
    public static void AppendCanonicalValue(ContentHashColumn column, object? value, Stream to)
    {
        if (value is null or DBNull)
        {
            var nul = Encoding.ASCII.GetBytes("N:");
            to.Write(nul, 0, nul.Length);
            return;
        }

        var text = value as string ?? Stringify(value);
        if (column.IsJson) text = CanonicalJson(text);
        var payload = Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC));

        var prefix = Encoding.ASCII.GetBytes(payload.Length.ToString(CultureInfo.InvariantCulture) + ":");
        to.Write(prefix, 0, prefix.Length);
        to.Write(payload, 0, payload.Length);
    }

    /// <summary>SHA-256 over the canonical form of one row, columns in declared order.</summary>
    public static byte[] RowDigest(IReadOnlyList<ContentHashColumn> columns, IReadOnlyList<object?> values)
    {
        if (columns.Count != values.Count)
            throw new ArgumentException(
                $"row has {values.Count} values for {columns.Count} declared columns", nameof(values));

        using var buffer = new MemoryStream(256);
        for (var i = 0; i < columns.Count; i++)
            AppendCanonicalValue(columns[i], values[i], buffer);

        buffer.Position = 0;
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>
    /// Sort the row digests, concatenate, hash. Sorting is what makes insertion order irrelevant;
    /// concatenating rather than folding is what makes a duplicated row visible.
    /// </summary>
    public static byte[] TableDigest(IEnumerable<byte[]> rowDigests)
    {
        var ordered = rowDigests.ToList();
        if (ordered.Count == 0) return EmptyDigest();

        ordered.Sort(CompareBytes);
        using var buffer = new MemoryStream(ordered.Count * 32);
        foreach (var d in ordered) buffer.Write(d, 0, d.Length);
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>Combine per-table digests in registry order. The caller supplies the order.</summary>
    public static byte[] Combine(IEnumerable<byte[]> tableDigestsInRegistryOrder)
    {
        using var buffer = new MemoryStream();
        foreach (var d in tableDigestsInRegistryOrder) buffer.Write(d, 0, d.Length);
        return SHA256.HashData(buffer.ToArray());
    }

    public static string Hex(byte[] digest) => Convert.ToHexString(digest).ToLowerInvariant();

    static int CompareBytes(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    static string Stringify(object value) => value switch
    {
        bool b => b ? "1" : "0",
        byte[] raw => Convert.ToHexString(raw).ToLowerInvariant(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // ---- canonical JSON -------------------------------------------------------------------------

    /// <summary>
    /// Keys sorted ordinal, no whitespace, integral numbers emitted as integers, strings NFC.
    /// Unparseable text is returned unchanged — the row validators already refuse malformed JSON, and
    /// a hash that throws on a database edited by hand is worse than one that hashes the bytes.
    /// </summary>
    public static string CanonicalJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var sb = new StringBuilder(text.Length);
            WriteCanonical(doc.RootElement, sb, 0);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return text;
        }
    }

    static void WriteCanonical(JsonElement el, StringBuilder sb, int depth)
    {
        if (depth > MaxJsonDepth)
        {
            WriteJsonString(el.GetRawText(), sb);
            return;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var firstProp = true;
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstProp) sb.Append(',');
                    firstProp = false;
                    WriteJsonString(p.Name, sb);
                    sb.Append(':');
                    WriteCanonical(p.Value, sb, depth + 1);
                }
                sb.Append('}');
                break;

            // Array order is content, not formatting: curve points are ordered and a reorder is a
            // different curve. Sorting here would hide a real edit.
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    WriteCanonical(item, sb, depth + 1);
                }
                sb.Append(']');
                break;

            case JsonValueKind.String:
                WriteJsonString(el.GetString() ?? string.Empty, sb);
                break;

            case JsonValueKind.Number:
                sb.Append(CanonicalNumber(el.GetRawText()));
                break;

            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            default: sb.Append("null"); break;
        }
    }

    /// <summary>
    /// <c>100.0</c> and <c>100</c> are the same magnitude and must hash the same; <c>1.50</c> and
    /// <c>1.5</c> likewise. Anything outside <see cref="decimal"/> keeps its literal text — better a
    /// stable oddity than a lossy round-trip.
    /// </summary>
    static string CanonicalNumber(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i.ToString(CultureInfo.InvariantCulture);

        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            var truncated = decimal.Truncate(d);
            return d == truncated
                ? truncated.ToString("0", CultureInfo.InvariantCulture)
                : d.ToString("0.#############################", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    static void WriteJsonString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var ch in s.Normalize(NormalizationForm.FormC))
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
