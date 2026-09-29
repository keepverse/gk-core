using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The scenario envelope and its steps. One ordered list, one sequential client: a scenario may
/// <i>sequence</i> real routes and <i>read</i> state back through them; it may not compute
/// (<c>scenario-format.md</c> §1). This type is the format's only definition — the runner, the
/// validator and the corpus all read it, so a step cannot mean one thing in the file and another in
/// the runner.
///
/// <para>Deliberately a NEW type rather than an extension of <c>EffectScenarioStepDto</c>
/// (<c>gk-core/src/FusionRpg.Core/Effects/EffectScenarioRunner.cs:34-60</c>): see
/// <c>scenario-format.md</c> §6 for the expressibility measurement that decided it. The envelope
/// <i>shape</i> (id + seed + an op-dispatch step list) is shared with that runner; the step type is
/// not.</para>
/// </summary>
public sealed class ScenarioDocument
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("program")] public string Program { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>
    /// The run's seed. Required and non-zero: a seed nobody chose is refused
    /// (<c>gk-core/tools/SquadHarness/Program.cs:35-39</c>). Every value the scenario synthesizes derives from
    /// it — a correlation id, an amount, a squad pick — so a rerun is the same rerun.
    /// </summary>
    [JsonPropertyName("seed")] public long Seed { get; set; }

    /// <summary>What the run believes the server's clock is. Printed in the verdict, never sent
    /// anywhere (the seam that would send it is RS3, gated — <c>scenario-format.md</c> §4).</summary>
    [JsonPropertyName("clock")] public ScenarioClock? Clock { get; set; }

    /// <summary>The rules the scenario obeys. Prose, but load-bearing prose: the honesty guard (RS4)
    /// and a reviewer both read it against the steps.</summary>
    [JsonPropertyName("rules")] public List<string> Rules { get; set; } = new();

    /// <summary>Names a bypass, a sanctioned seed surface or a known reachability gap. A scenario
    /// that uses a fixture surface has to say so here (<c>scenario-format.md</c> §5.3).</summary>
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();

    [JsonPropertyName("steps")] public List<ScenarioStep> Steps { get; set; } = new();
}

public sealed class ScenarioClock
{
    /// <summary>
    /// `ambient` | `offset` | `explicit`. `ambient` means the machine clock; `offset` means the host was
    /// booted with <c>offsetSeconds</c> added to the machine clock through the ONE seam
    /// (<c>FusionRpg.Core.Time.ServerClock</c>, rpg-simulator RS3) — the in-process factory and
    /// <c>ProcessHost</c> both honour it, the latter via <c>FUSIONRPG_CLOCK_OFFSET</c>. `explicit` (an
    /// absolute instant) is refused by name: the seam's product shape is a signed offset, never a route or
    /// an absolute clock (spec §2).
    /// </summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "ambient";

    /// <summary>Required. `ambient` must explain what that means for this run's verdicts.</summary>
    [JsonPropertyName("note")] public string Note { get; set; } = "";

    /// <summary>
    /// Required when <see cref="Mode"/> is `offset`: signed seconds added to the machine clock. The host
    /// applies it AT BOOT (5a) and may re-apply it mid-run (5b); a scenario never computes it, it declares
    /// it. Refused for any other mode — a stray offset on an `ambient` run would make the declaration lie.
    /// </summary>
    [JsonPropertyName("offsetSeconds")] public long? OffsetSeconds { get; set; }
}

/// <summary>
/// One step. Exactly one of five shapes, chosen by the op's prefix
/// (<see cref="ScenarioVocabulary"/>): a <c>sim.*</c>/<c>test.*</c>/<c>api.*</c> call, a
/// <c>read.*</c> read-back, an <c>expect.*</c> assertion, a <c>clock.set</c> declaration, or
/// <c>digest</c>. Every shape carries a <c>why</c>; a step with no reason is refused.
/// </summary>
public sealed class ScenarioStep
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";

    /// <summary>For a call: the route, which must equal the vocabulary's route for the op (the
    /// declared-route property RS1 shipped — drift is a validation failure, not a stale comment).
    /// For a read: the FE-facing GET template or hub message the read-back comes from.</summary>
    [JsonPropertyName("route")] public string? Route { get; set; }

    /// <summary>Call arguments. Serialized into the request body, or into the query string when the
    /// op's vocabulary entry says so. Values are literals or <c>{"$seed": n}</c>-free plain JSON: a
    /// scenario may not compute one.</summary>
    [JsonPropertyName("args")] public Dictionary<string, JsonElement>? Args { get; set; }

    /// <summary>Sequencing state taken out of this step's response by JSON pointer, so a later step
    /// can address it. NOT evidence: the verdict records a capture's <c>source</c>, and an
    /// <c>expect.*</c> compares read-backs to read-backs.</summary>
    [JsonPropertyName("capture")] public List<ScenarioCapture>? Capture { get; set; }

    /// <summary>`expect.*`: the registered reading name this assertion is about.</summary>
    [JsonPropertyName("reading")] public string? Reading { get; set; }

    /// <summary>`expect.*`: the JSON pointer into that reading.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }

    /// <summary>`expect.*`: one of <see cref="ScenarioVocabulary.Checks"/>.</summary>
    [JsonPropertyName("check")] public string? Check { get; set; }

    /// <summary>`expect.*`: the literal to compare against, or for `allStartWith` a literal template
    /// that may name a captured value (<c>{expeditionId}</c>).</summary>
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }

    /// <summary>`expect.*`: the other side of a read-back-to-read-back comparison. Either
    /// <c>reading#/pointer</c> or <c>{capturedName}</c>. A literal is never accepted here — a
    /// comparison against a constant is <c>value</c>.</summary>
    [JsonPropertyName("other")] public string? Other { get; set; }

    /// <summary>`digest`: the <c>reading#/pointer</c> references that enter the digest.</summary>
    [JsonPropertyName("include")] public List<string>? Include { get; set; }

    /// <summary>`digest`: field names blanked inside the included readings before hashing, each with
    /// a written reason (<c>readback-verdict.md</c> §3).</summary>
    [JsonPropertyName("exclude")] public List<ScenarioExclusion>? Exclude { get; set; }

    /// <summary>
    /// <c>clock.set</c>: the offset in seconds, from the machine clock, that the HOST must believe from this
    /// point on (RS3 increment 5b, owner ruling on RS-F16 candidate 2). Absolute, not a delta, so the runner
    /// performs no arithmetic over declarations — it hands the value to the host's clock control. A scenario
    /// that declares one is REFUSED by name when the host cannot be told (no control injected, or the CLI's
    /// <c>--base-url</c> lane), because a silently unapplied clock movement would make every later stamp and
    /// every later due-date comparison a lie.
    /// </summary>
    [JsonPropertyName("offsetSeconds")] public long? OffsetSeconds { get; set; }

    [JsonPropertyName("why")] public string? Why { get; set; }
}

public sealed class ScenarioCapture
{
    /// <summary>The context name a later step (or a route placeholder) resolves against.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>A JSON pointer over this step's response body. Selection only — index, slice,
    /// wildcard, or a key match whose value may itself be a captured name.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public sealed class ScenarioExclusion
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";

    /// <summary>Required and non-empty. The digest may not exclude a field without saying why — the
    /// `BattleGoldenTests.cs:163-172` recipe, which this copies.</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

/// <summary>Reads a scenario file with the same tolerances the effect-scenario runner uses
/// (<c>gk-core/src/FusionRpg.Core/Effects/EffectScenarioRunner.cs:71-76</c>): case-insensitive names,
/// comments and trailing commas allowed. An authored artifact should forgive a trailing comma; it
/// should not forgive a missing route.</summary>
public static class ScenarioFile
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ScenarioDocument Parse(string json) =>
        JsonSerializer.Deserialize<ScenarioDocument>(json, JsonOptions)
        ?? throw new InvalidOperationException("null scenario document");

    public static ScenarioDocument Read(string path) => Parse(File.ReadAllText(path));
}
