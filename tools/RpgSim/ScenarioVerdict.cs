using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// What a run produced, and the only thing a human is meant to read. One shape for both hosts
/// (owner ruling **E2 (a)**: one scenario file, two hosts — the <see cref="Host"/> field is what
/// makes a pasted verdict self-describing), plus the readings each assertion and the digest used.
///
/// <para>Two rules are structural here, not conventions:</para>
/// <list type="number">
/// <item><b>Every reading carries its source.</b> <see cref="ScenarioReading.Source"/> is
/// non-optional on the type: a value with no route behind it cannot be written into a verdict at all
/// (<c>readback-verdict.md</c> §2, <c>docs/contributing/live-probe-standard.md</c> §3).</item>
/// <item><b>A reading is never a response body.</b> The runner registers readings from GET read-backs
/// only; a call's response body becomes a <see cref="ScenarioCaptureRecord"/> (sequencing state, with
/// the route it came from named) and never a digit of evidence.</item>
/// </list>
/// </summary>
public sealed class ScenarioVerdict
{
    [JsonPropertyName("scenarioId")] public string ScenarioId { get; set; } = "";
    [JsonPropertyName("seed")] public long Seed { get; set; }

    /// <summary>The clock declaration, printed verbatim from the scenario plus what it means:
    /// `ambient` today, because no server seam accepts a clock (RS3, gated).</summary>
    [JsonPropertyName("clock")] public string Clock { get; set; } = "";

    /// <summary>`inproc` or `process:<url>` — which host ran, so the same file's two verdicts are
    /// distinguishable without a second format.</summary>
    [JsonPropertyName("host")] public string Host { get; set; } = "";

    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>The run never started: the target is not a sim server, or a live injector is connected.
    /// A refusal is an artifact with a named reason, not a silent exit — a refusal nobody can review is
    /// indistinguishable from a crash.</summary>
    [JsonPropertyName("refused")] public bool Refused { get; set; }

    [JsonPropertyName("steps")] public List<ScenarioStepOutcome> Steps { get; set; } = new();
    [JsonPropertyName("captures")] public List<ScenarioCaptureRecord> Captures { get; set; } = new();
    [JsonPropertyName("readings")] public List<ScenarioReading> Readings { get; set; } = new();

    /// <summary>Assertion failures and run failures, named. The digest never reports a failure: a
    /// digest that moved is a determinism question, an absent assertion is a behaviour question, and
    /// this program keeps them apart.</summary>
    [JsonPropertyName("failures")] public List<string> Failures { get; set; } = new();

    /// <summary>SHA-256 over the canonical form of the declared digest readings, or null when the
    /// scenario declared no `digest` step.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }

    /// <summary>The exclusion list the digest used, reasons included — written into the verdict so a
    /// reader does not have to open the contract to know what was blanked.</summary>
    [JsonPropertyName("digestExclusions")] public List<ExclusionRecord> DigestExclusions { get; set; } = new();

    /// <summary>How the host was judged settled before the readings were taken (RS2.4). Printed so a
    /// verdict says whether it measured a stable server or a race.</summary>
    [JsonPropertyName("settle")] public SettleRecord? Settle { get; set; }

    static readonly JsonSerializerOptions Out = new()
    {
        WriteIndented = true,
        // Same relaxed escaping as CanonicalJson: the verdict is read by a human and by
        // ReadingDigest.Compare, never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToJson() => JsonSerializer.Serialize(this, Out);

    /// <summary>
    /// Read a stored verdict back — the golden artifact's own shape (<c>readback-verdict.md</c> §5), and the
    /// same reader for any verdict a caller kept. Relaxed like the writer: a stored artifact is data a human
    /// reads, not a wire contract.
    /// </summary>
    public static ScenarioVerdict FromJson(string json) =>
        JsonSerializer.Deserialize<ScenarioVerdict>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("null verdict document");

    /// <summary>
    /// The artifact's own rules, checked before it is written. A verdict that fails these is not a
    /// verdict: a reading with no route behind it, a digest with no exclusion list printed beside it,
    /// a failing run with no named failure.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ScenarioId)) errors.Add("scenarioId: required");
        if (string.IsNullOrWhiteSpace(Host)) errors.Add("host: required — a verdict says which host ran it");
        if (string.IsNullOrWhiteSpace(Clock)) errors.Add("clock: required — an undeclared clock makes timestamp readings meaningless");

        for (var i = 0; i < Readings.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Readings[i].Name)) errors.Add($"readings[{i}]: name required");
            if (string.IsNullOrWhiteSpace(Readings[i].Source))
                errors.Add($"readings[{i}] ({Readings[i].Name}): source required — a reading is the same query path the web FE uses, and the verdict must say which (live-probe-standard.md §3)");
        }

        if (Digest is not null && DigestExclusions.Count == 0)
            errors.Add("digestExclusions: a digest must print the exclusion list it used (BattleGoldenTests.cs:163-172)");

        if (!Refused && Settle is null)
            errors.Add("settle: required on a run that happened — \"the host settled\" is defined by polling, never assumed");
        if (!Refused && Settle is { Settled: false })
            errors.Add($"settle: the host did not settle ({Settle.Reason}) — a verdict read from an unsettled " +
                       "host describes a race, not the scenario");

        if (!Ok && Failures.Count == 0)
            errors.Add("failures: a run reported not-ok must name what failed");

        return errors;
    }
}

public sealed class ScenarioStepOutcome
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("op")] public string Op { get; set; } = "";

    /// <summary>The route actually requested (`POST /api/players`), after placeholders resolved but
    /// with the resolved values in place — the verdict says what was called, not what was declared.</summary>
    [JsonPropertyName("route")] public string Route { get; set; } = "";

    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";  // ok | failed | skipped
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

/// <summary>Sequencing state. <see cref="Source"/> is the step's route, so "the squad came from the
/// roster read" is checkable from the verdict alone.</summary>
public sealed class ScenarioCaptureRecord
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
}

/// <summary>One read-back. <see cref="Source"/> is required by the contract; <see cref="Method"/> is
/// the declared method (always GET, or a hub message), kept so a reader can see that a POST body never
/// became evidence.</summary>
public sealed class ScenarioReading
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
}

public sealed class SettleRecord
{
    [JsonPropertyName("settled")] public bool Settled { get; set; }
    [JsonPropertyName("polls")] public int Polls { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class ExclusionRecord
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}
