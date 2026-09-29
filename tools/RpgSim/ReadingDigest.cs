using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The digest: SHA-256 over the canonical form of the readings a scenario declared digest-bearing,
/// with a **written-down exclusion list** whose every entry carries a reason. The idiom is
/// <c>gk-core/tools/SquadHarness/DeterminismHash.cs:19-25</c> (hash canonical JSON, never a DTO's own
/// serialization); the exclusion recipe is
/// <c>gk-core/tests/FusionRpg.Core.Tests/Battle/BattleGoldenTests.cs:163-172</c>, whose comment is the whole
/// argument: <i>folding a non-determinism field in makes the golden move for a reason that is not a
/// determinism break</i> — and a digest that moves for a non-determinism reason is worse than no
/// digest at all, because it trains the reader to ignore it.
///
/// <para><b>Why the exclusion list is code and not scenario data.</b> The entries are structural facts
/// about the server's payloads (any reading may carry them), so they are written once, here, next to
/// their reasons — and every verdict prints the list it used. A scenario may add its own exclusion
/// through its <c>digest</c> step (<see cref="ScenarioExclusion"/>), and the validator refuses one
/// without a reason, so both scopes obey one rule.</para>
///
/// <para><b>What the digest is not.</b> It is not an assertion. `expect.*` decides whether behaviour
/// is right (a closed enum membership, an attribution, the fabrication line); the digest only decides
/// whether a second run of the same scenario read the same numbers. A run whose assertions all passed
/// can still have a digest that moved — that is a determinism finding, and this type reports
/// <i>which</i> pointers moved rather than only that the two strings differ.</para>
/// </summary>
public static class ReadingDigest
{
    public sealed record Exclusion(string Field, string Reason);

    /// <summary>
    /// The baseline the server's own payloads require. Each entry is a field name blanked wherever it
    /// appears in a digested value, and each carries the reason a reader would otherwise have to
    /// reconstruct.
    ///
    /// <para><b>One reading is deliberately NOT digest-eligible (RS-F7): the XP ledger.</b> Its row
    /// timestamp is a one-letter field, <c>t</c>, and <c>IsExcluded</c> blanks a name EVERYWHERE it
    /// appears — so adding <c>t</c> here could silently blank a value that matters in another payload, and
    /// leaving it out makes the ledger move between runs. A scenario that needs to digest the ledger must
    /// therefore declare the exclusion itself, with its own reason, or not digest it.
    /// <c>readback-verdict.md</c> §3 carries the same line.</para>
    /// </summary>
    public static readonly IReadOnlyList<Exclusion> Baseline = new[]
    {
        new Exclusion("*Utc",
            "Ambient wall clock: the server now reads it through the ONE seam " +
            "(FusionRpg.Core.Time.ServerClock, rpg-simulator RS3), but the shipped corpus declares " +
            "clock.mode: ambient, so two runs an hour apart still legitimately differ and folding one in " +
            "makes the digest move for a reason that is not a determinism break. A scenario that declares " +
            "an offset makes these stable and may then digest them; until one does, they stay excluded by " +
            "name. Suffix-matched because the payloads name the same clock differently per family " +
            "(dispatchedUtc, dueUtc, collectedUtc, createdUtc, serverUtc, lastHeartbeatUtc)."),
        new Exclusion("*At",
            "Same ambient clock, written as a past-tense suffix by the newer routes (startedAt, finishedAt)."),
        new Exclusion("instanceId",
            "Identity minted per run (Guid.NewGuid on summon), not a value the run produced. The squad's " +
            "identities are still ASSERTED (equalSet against the roster read-back); they are simply not " +
            "digested, because no two runs can have the same ones."),
        new Exclusion("correlationId",
            "Caller-supplied idempotency key. The runner derives it from the scenario seed, so it is stable " +
            "in practice — excluded anyway because a digest over an identifier the runner chose tests the " +
            "runner, not the server."),
        new Exclusion("revision",
            "Monotonic per-player counter: its VALUE is a run's write count, which is a legitimate reading " +
            "but a different question from 'did the same sequence produce the same state'. Asserted where a " +
            "scenario cares; never digested."),
        new Exclusion("activityFactId",
            "A per-row id minted in insert order (the XP ledger's own counter). Named by the RS2.4 " +
            "double-run measurement, not guessed: two runs on fresh hosts move it by construction, for the " +
            "same reason `revision` moves. The ROW SET is asserted by `expect.*`; the id is not a state " +
            "reading."),
        new Exclusion("generatedAt",
            "The verdict's own write time, should a host add one; folding it in would make the artifact " +
            "self-invalidating."),
    };

    /// <summary>The digest input: one entry per declared `include` reference. A reference with no
    /// pointer digests the whole reading; one with a pointer digests the selected value. `source` is
    /// carried so a moved pointer can be traced back to the route it was read from.</summary>
    public sealed record DigestEntry(string Reference, string Source, JsonElement? Value);

    public static IReadOnlyList<Exclusion> MergeExclusions(IEnumerable<ScenarioExclusion>? declared) =>
        Baseline.Concat((declared ?? Enumerable.Empty<ScenarioExclusion>())
                .Select(e => new Exclusion(e.Field, e.Reason)))
            .ToList();

    /// <summary>The canonical form the digest is taken over: an object keyed by `<reading>#<pointer>`
    /// (ordinal), each entry `{ source, value }`, with every excluded field blanked. Deterministic
    /// regardless of the order the scenario declared its includes.</summary>
    public static JsonElement CanonicalForm(IEnumerable<DigestEntry> entries, IEnumerable<Exclusion> exclusions)
    {
        var fields = exclusions.Select(e => e.Field).ToArray();
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var entry in entries.OrderBy(e => e.Reference, StringComparer.Ordinal))
            {
                w.WritePropertyName(entry.Reference);
                w.WriteStartObject();
                w.WriteString("source", entry.Source);
                w.WritePropertyName("value");
                Blank(entry.Value, fields, w);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    public static string Hash(JsonElement canonicalForm) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Of(canonicalForm))))
            .ToLowerInvariant();

    public static string Compute(IEnumerable<DigestEntry> entries, IEnumerable<Exclusion> exclusions) =>
        Hash(CanonicalForm(entries, exclusions));

    /// <summary>
    /// The same-run falsifier's report: two canonical forms, the pointers that differ, and the sources
    /// those pointers came from. `Same` is the assertion; <see cref="MovedPointers"/> is what makes a
    /// red falsifier actionable instead of a mystery ("the digest moved" is not evidence).
    /// </summary>
    public static DigestComparison Compare(JsonElement left, JsonElement right)
    {
        var moved = new List<string>();
        Diff(left, right, "$", moved);
        return new DigestComparison(moved.Count == 0, moved);
    }

    /// <summary>
    /// Compares two verdicts of the same scenario: the same-run double-run falsifier (RS2.2) and the
    /// in-process vs real-process comparison (RS2.5) are the same question, so they are the same call.
    /// The exclusion list is the LEFT verdict's own (a verdict prints what it used); when it printed
    /// none, the baseline applies.
    /// </summary>
    public static DigestComparison CompareVerdicts(ScenarioVerdict left, ScenarioVerdict right)
    {
        var exclusions = left.DigestExclusions.Count > 0
            ? left.DigestExclusions.Select(e => new Exclusion(e.Field, e.Reason))
            : Baseline;
        var a = CanonicalForm(Entries(left), exclusions);
        var b = CanonicalForm(Entries(right), exclusions);
        return Compare(a, b);
    }

    static IEnumerable<DigestEntry> Entries(ScenarioVerdict verdict) =>
        verdict.Readings.Select(r => new DigestEntry(r.Name, r.Source, r.Value));

    /// <summary>A value that is identical here and different there: `&lt;excluded&gt;` for a blanked
    /// field, `&lt;absent&gt;` for a missing one. Printed rather than hashed, so a moved pointer names
    /// what it moved from and to.</summary>
    static void Diff(JsonElement left, JsonElement right, string pointer, List<string> moved)
    {
        if (left.ValueKind != right.ValueKind)
        {
            moved.Add($"{pointer}: {Describe(left)} -> {Describe(right)}");
            return;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = left.EnumerateObject().Select(p => p.Name)
                    .Concat(right.EnumerateObject().Select(p => p.Name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal);
                foreach (var name in names)
                {
                    var hasLeft = left.TryGetProperty(name, out var l);
                    var hasRight = right.TryGetProperty(name, out var r);
                    if (hasLeft != hasRight)
                        moved.Add($"{pointer}.{name}: {(hasLeft ? Describe(l) : "<absent>")} -> {(hasRight ? Describe(r) : "<absent>")}");
                    else Diff(l, r, $"{pointer}.{name}", moved);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var l = left.EnumerateArray().ToList();
                var r = right.EnumerateArray().ToList();
                if (l.Count != r.Count)
                {
                    moved.Add($"{pointer}: array length {l.Count} -> {r.Count}");
                    return;
                }
                for (var i = 0; i < l.Count; i++) Diff(l[i], r[i], $"{pointer}[{i}]", moved);
                break;
            }
            default:
                if (!string.Equals(CanonicalJson.Of(left), CanonicalJson.Of(right), StringComparison.Ordinal))
                    moved.Add($"{pointer}: {Describe(left)} -> {Describe(right)}");
                break;
        }
    }

    static string Describe(JsonElement e) => CanonicalJson.Of(e);

    static void Blank(JsonElement? value, string[] fields, Utf8JsonWriter w)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null || value.Value.ValueKind == JsonValueKind.Undefined)
        {
            w.WriteNullValue();
            return;
        }

        var e = value.Value;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    if (IsExcluded(p.Name, fields)) { w.WriteString(p.Name, "<excluded>"); continue; }
                    w.WritePropertyName(p.Name);
                    Blank(p.Value, fields, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) Blank(item, fields, w);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    /// <summary>An entry matches when the field name equals it, or the entry starts with `*` and the
    /// field name ends with the rest (`*Utc`). Suffix matching is how one reason can cover the same
    /// clock under six names without six near-identical lines.</summary>
    static bool IsExcluded(string field, string[] fields)
    {
        foreach (var pattern in fields)
        {
            if (pattern.Length == 0) continue;
            if (pattern[0] == '*')
            {
                if (field.EndsWith(pattern[1..], StringComparison.Ordinal)) return true;
            }
            else if (string.Equals(pattern, field, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

public sealed record DigestComparison(bool Same, IReadOnlyList<string> MovedPointers)
{
    public string Report() => Same
        ? "digest identical across both runs"
        : $"digest MOVED at {MovedPointers.Count} pointer(s):\n  " + string.Join("\n  ", MovedPointers);
}
