using System.Globalization;
using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The runner: reads a scenario, owns the seed, drives **one sequential client**, and writes a
/// verdict. It contains no domain math — every number in the verdict came out of a route, and the only
/// values it synthesizes are the ones the scenario's seed implies (a correlation id).
///
/// <para><b>One client, in order.</b> A scenario is not a load test. The digest is over a total order,
/// so the runner never issues two requests at once and never retries one: a retry would make the
/// second run's ordering differ from the first, which is precisely the property the digest measures
/// (<c>readback-verdict.md</c> §4).</para>
///
/// <para><b>Two refusals before anything runs</b> (owner ruling D1 (b), the property
/// <c>SimService.Guard()</c> already enforces server-side, <c>gk-core/src/FusionRpg.Server/SimService.cs:25-30</c>):</para>
/// <list type="number">
/// <item>the target must report <c>simEnabled: true</c> (<c>gk-core/src/FusionRpg.Contracts/Dtos.cs:48</c>) —
/// otherwise this is a player's install and the run would be writing to it;</item>
/// <item>the target must not report a live injector connected — a scenario is not allowed to run
/// beside a real game session, so it refuses rather than racing one.</item>
/// </list>
/// <para>Both produce a verdict with <c>refused: true</c> and the reason named, never a silent exit: a
/// refusal that leaves no artifact is a refusal nobody can review.</para>
/// </summary>
public sealed class ScenarioRunner
{
    readonly HttpClient _http;
    readonly string _host;
    readonly IClockControl? _clockControl;

    public ScenarioRunner(HttpClient http, string host, IClockControl? clockControl = null)
    {
        _http = http;
        _host = host;
        _clockControl = clockControl;
    }

    /// <summary>Where the runner is pointed, for the refusal message.</summary>
    public string Target => _http.BaseAddress?.ToString() ?? "(no base address)";

    public async Task<ScenarioVerdict> RunAsync(ScenarioDocument doc, CancellationToken ct = default)
    {
        ScenarioValidator.ValidateOrThrow(doc);

        // The run's own STARTING clock. A scenario that declares one must start from it, or a second run
        // against the same host would inherit the first run's mid-run movement (the CLI's --double-run does
        // exactly that). With no control the host cannot be told and the boot declaration is the caller's
        // promise; with one, the declaration is enforced rather than assumed.
        if (_clockControl is not null)
        {
            var bootOffset = doc.Clock?.Mode == "offset" ? doc.Clock.OffsetSeconds ?? 0 : 0;
            await _clockControl.SetOffsetSecondsAsync(bootOffset, ct);
        }

        var verdict = new ScenarioVerdict
        {
            ScenarioId = doc.Id,
            Seed = doc.Seed,
            Clock = ClockDeclaration(doc),
            Host = _host
        };

        var refusal = await GateAsync(ct);
        if (refusal is not null)
        {
            verdict.Refused = true;
            verdict.Ok = false;
            verdict.Failures.Add(refusal);
            return verdict;
        }

        var state = new RunState();
        var digests = doc.Steps.Where(s => ScenarioVocabulary.Kind(s.Op) == ScenarioVocabulary.Digest).ToList();

        // "The host settled" is polled, never assumed (RS2.4): the server's ingest writer, boot catch-up
        // and compaction tick on their own, and a reading taken mid-flight is a reading of a race.
        var settle = await SimSettler.WaitAsync(_http, ct: ct);
        verdict.Settle = new SettleRecord
        {
            Settled = settle.Settled, Polls = settle.Polls, ElapsedMs = settle.ElapsedMs, Reason = settle.Reason
        };
        if (!settle.Settled)
        {
            verdict.Ok = false;
            verdict.Failures.Add($"settle: {settle.Reason}");
            return verdict;
        }

        for (var i = 0; i < doc.Steps.Count; i++)
        {
            var step = doc.Steps[i];
            var outcome = new ScenarioStepOutcome { Index = i, Op = step.Op };

            try
            {
                switch (ScenarioVocabulary.Kind(step.Op))
                {
                    case ScenarioVocabulary.Call:
                        await ExecuteCallAsync(doc, step, i, state, outcome, verdict, ct);
                        break;
                    case ScenarioVocabulary.Read:
                        await ExecuteReadAsync(step, state, outcome, verdict, ct);
                        break;
                    case ScenarioVocabulary.Expect:
                    {
                        outcome.Route = step.Reading ?? "";
                        var failure = ExecuteExpect(step, state, verdict);
                        outcome.Outcome = failure is null ? "ok" : "failed";
                        outcome.Detail = failure;
                        break;
                    }
                    case ScenarioVocabulary.Digest:
                        outcome.Route = "(digest)";
                        outcome.Outcome = "ok";
                        break;
                    case ScenarioVocabulary.Clock:
                    {
                        outcome.Route = "(clock)";
                        if (_clockControl is null)
                            throw new InvalidOperationException(
                                "clock.set: the host cannot be told the clock — no clock control was supplied. " +
                                "A route that sets one is deliberately not specified (spec-clock-seam.md section 2), " +
                                "so the host that boots the target must hand the runner a control (D3 (b): the " +
                                "untestability is the finding, never a silent no-op)");

                        var offsetSeconds = step.OffsetSeconds!.Value;
                        await _clockControl.SetOffsetSecondsAsync(offsetSeconds, ct);
                        outcome.Outcome = "ok";
                        outcome.Detail = $"the host now believes the clock is machine{(offsetSeconds >= 0 ? "+" : "")}{offsetSeconds}s";
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"unknown op kind for '{step.Op}'");
                }
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException
                                      or FormatException or JsonException or TaskCanceledException)
            {
                outcome.Outcome = "failed";
                outcome.Detail = e.Message;
                verdict.Failures.Add($"{step.Op}: {e.Message}");
                verdict.Ok = false;
                verdict.Steps.Add(outcome);

                // A failed call leaves the run without the state later steps address, so the rest are
                // skipped rather than run against absent values. Assertion failures do NOT stop the
                // run: a reader wants every failed claim at once, not the first one.
                if (ScenarioVocabulary.Kind(step.Op) == ScenarioVocabulary.Call)
                {
                    for (var j = i + 1; j < doc.Steps.Count; j++)
                        verdict.Steps.Add(new ScenarioStepOutcome
                        {
                            Index = j, Op = doc.Steps[j].Op, Outcome = "skipped",
                            Detail = $"'{step.Op}' failed"
                        });
                    return verdict;
                }
                continue;
            }

            verdict.Steps.Add(outcome);
        }

        ApplyDigest(doc, digests, state, verdict);

        // A second settle before the digest: a scenario's own writes can enqueue work (and a background
        // pass can land between the last read and the hash), so the end state is polled as well as the
        // start state. The observation is recorded, not silently dropped.
        var finalSettle = await SimSettler.WaitAsync(_http, ct: ct);
        if (!finalSettle.Settled)
        {
            verdict.Ok = false;
            verdict.Failures.Add($"settle(after): {finalSettle.Reason}");
        }

        verdict.Ok = verdict.Failures.Count == 0;
        return verdict;
    }

    string ClockDeclaration(ScenarioDocument doc)
    {
        var declared = doc.Clock?.Mode == "offset"
            ? $"{doc.Clock?.Mode} {doc.Clock?.OffsetSeconds}s — {doc.Clock?.Note}"
            : $"{doc.Clock?.Mode} — {doc.Clock?.Note}";
        // A mid-run movement is part of what the verdict must say: a run that moved the clock and did not
        // report it cannot be compared to one that did not (spec §2, "declared in a verdict").
        var moved = doc.Steps.Where(s => ScenarioVocabulary.Kind(s.Op) == ScenarioVocabulary.Clock)
            .Select(s => s.OffsetSeconds ?? 0)
            .ToList();
        return moved.Count == 0
            ? declared
            : declared + $" [moved mid-run to machine{(moved[^1] >= 0 ? "+" : "")}{moved[^1]}s at {moved.Count} clock.set step(s)]";
    }

    /// <summary>The two refusals. A null return means the target is safe to run against.</summary>
    async Task<string?> GateAsync(CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.GetAsync("health", ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return $"refused: the target {Target} did not answer GET /health ({e.Message}) — " +
                   "a scenario needs a live sim server";
        }

        if (!res.IsSuccessStatusCode)
            return $"refused: GET /health answered {(int)res.StatusCode} on {Target}";

        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = body.RootElement;
        var simEnabled = root.TryGetProperty("simEnabled", out var sim) && sim.ValueKind == JsonValueKind.True;
        if (!simEnabled)
            return $"refused: the target {Target} reports simEnabled:false — that is a player install, " +
                   "not a sim server (src/FusionRpg.Contracts/Dtos.cs:48)";

        var injector = root.TryGetProperty("injectorConnected", out var inj) && inj.ValueKind == JsonValueKind.True;
        if (injector)
            return $"refused: a live injector is connected to {Target} — a scenario does not run beside a " +
                   "real game session (owner ruling D1 (b), the property SimService.Guard() enforces)";

        return null;
    }

    // ---- calls ------------------------------------------------------------------------------- //

    async Task ExecuteCallAsync(ScenarioDocument doc, ScenarioStep step, int index, RunState state,
        ScenarioStepOutcome outcome, ScenarioVerdict verdict, CancellationToken ct)
    {
        var op = ScenarioVocabulary.CallOp(step.Op)
                 ?? throw new InvalidOperationException($"'{step.Op}' is not in the closed call vocabulary");

        var path = ResolvePath(step.Route!, op.PathFromContext, state);
        var query = BuildPairs(op.Query, step, state);
        var body = BuildPairs(op.Body, step, state, doc.Seed, index);

        var requestTarget = path + (query.Count == 0 ? "" : "?" + string.Join("&", query.Select(kv => $"{kv.Key}={kv.Value}")));
        // The verdict says what was CALLED (`POST /api/players`); the request target is only the path,
        // and the two are kept apart on purpose — a display string passed to HttpClient encoded the
        // method into the URL once and answered 405 for a route that exists.
        outcome.Route = $"{op.Method} {requestTarget}";

        using var request = new HttpRequestMessage(new HttpMethod(op.Method), requestTarget);
        if (body.Count > 0)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body.ToDictionary(kv => kv.Key, kv => kv.Value)),
                System.Text.Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(request, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"{requestTarget} -> {(int)res.StatusCode} {Truncate(text)}");

        outcome.Outcome = "ok";

        if (step.Capture is null || step.Capture.Count == 0) return;
        using var response = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
        foreach (var capture in step.Capture)
        {
            var selected = JsonPointer.Select(response.RootElement, capture.Path, state.Scalars());
            state.Captures[capture.Name] = selected;
            verdict.Captures.Add(new ScenarioCaptureRecord
            {
                Name = capture.Name,
                Path = capture.Path,
                Source = outcome.Route,
                Value = selected
            });
        }
    }

    List<KeyValuePair<string, object?>> BuildPairs((string Field, string Source)[] pairs, ScenarioStep step,
        RunState state, long seed = 0, int index = 0)
    {
        var result = new List<KeyValuePair<string, object?>>();
        foreach (var (field, source) in pairs)
        {
            if (source == ScenarioSources.CorrelationId)
            {
                // The only value the runner synthesizes, and it is derived from the scenario's own seed
                // plus the step index — never a random number, so a rerun is the same rerun.
                result.Add(new(field, $"sim-{seed}-{index}"));
                continue;
            }

            if (source.StartsWith(ScenarioSources.ArgPrefix, StringComparison.Ordinal))
            {
                var name = source[ScenarioSources.ArgPrefix.Length..];
                if (step.Args is null || !step.Args.TryGetValue(name, out var value))
                    throw new InvalidOperationException($"'{step.Op}' needs arg '{name}', which the scenario did not declare");
                result.Add(new(field, ToClr(value)));
                continue;
            }

            if (source.StartsWith(ScenarioSources.ContextPrefix, StringComparison.Ordinal))
            {
                var name = source[ScenarioSources.ContextPrefix.Length..];
                if (!state.Captures.TryGetValue(name, out var value) || value is null)
                    throw new InvalidOperationException($"'{step.Op}' needs the captured value '{name}', which no earlier step captured");
                result.Add(new(field, ToClr(value.Value)));
                continue;
            }

            throw new InvalidOperationException($"'{step.Op}': unknown source '{source}'");
        }
        return result;
    }

    static object? ToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => JsonSerializer.Deserialize<object>(e.GetRawText())
    };

    // ---- reads ------------------------------------------------------------------------------- //

    async Task ExecuteReadAsync(ScenarioStep step, RunState state, ScenarioStepOutcome outcome,
        ScenarioVerdict verdict, CancellationToken ct)
    {
        var space = step.Route!.IndexOf(' ');
        var method = space < 0 ? "" : step.Route[..space].Trim().ToUpperInvariant();
        if (method != "GET")
            throw new InvalidOperationException(
                $"{step.Op}: '{step.Route}' is not a GET — hub reads need the SignalR client, which this wave " +
                "does not ship (RS2.5 owns the real transport)");

        var resolved = ResolveTemplate(step.Route[(space + 1)..], state);
        var source = $"GET {resolved}";
        outcome.Route = source;

        using var res = await _http.GetAsync(resolved, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"{source} -> {(int)res.StatusCode} {Truncate(text)}");

        var reading = new ScenarioReading
        {
            Name = step.Op,
            Source = source,
            Method = "GET",
            Value = string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text).RootElement.Clone()
        };
        state.Readings[step.Op] = reading;
        verdict.Readings.Add(reading);
        outcome.Outcome = "ok";

        if (step.Capture is null || step.Capture.Count == 0) return;
        foreach (var capture in step.Capture)
        {
            var selected = JsonPointer.Select(reading.Value ?? default, capture.Path, state.Scalars());
            state.Captures[capture.Name] = selected;
            verdict.Captures.Add(new ScenarioCaptureRecord
            {
                Name = capture.Name, Path = capture.Path, Source = source, Value = selected
            });
        }
    }

    // ---- assertions -------------------------------------------------------------------------- //

    /// <summary>Asserts one declared claim. Returns null when it holds, otherwise the failure — the
    /// caller records it on the step outcome AND in the verdict's failure list, so a reader sees both
    /// where it happened and what the run concluded.</summary>
    string? ExecuteExpect(ScenarioStep step, RunState state, ScenarioVerdict verdict)
    {
        if (!state.Readings.TryGetValue(step.Reading!, out var reading))
            throw new InvalidOperationException($"{step.Op}: reading '{step.Reading}' was never read");

        var left = JsonPointer.Select(reading.Value ?? default, step.Path!, state.Scalars());
        JsonElement? other = null;
        if (!string.IsNullOrWhiteSpace(step.Other))
            other = ResolveOther(step.Other!, state);

        var failure = ScenarioExpectations.Evaluate(step, left, other, state.Scalars());
        if (failure is not null)
        {
            verdict.Failures.Add($"{step.Op}: {failure}");
            verdict.Ok = false;
        }
        return failure;
    }

    /// <summary>`other` is a reading reference (`read.roster#$.items[*].x`) or a captured value
    /// (`{squadIds}`). Never a literal — the validator refuses that, and the reason is the fabrication
    /// line: a read-back compared to a read-back is a measurement.</summary>
    JsonElement? ResolveOther(string other, RunState state)
    {
        if (other.StartsWith('{') && other.EndsWith('}'))
        {
            var name = other[1..^1];
            if (!state.Captures.TryGetValue(name, out var value))
                throw new InvalidOperationException($"other '{other}' names a value that was never captured");
            return value;
        }

        var hash = other.IndexOf('#');
        var readingName = other[..hash];
        var pointer = other[(hash + 1)..];
        if (!state.Readings.TryGetValue(readingName, out var reading))
            throw new InvalidOperationException($"other '{other}' names reading '{readingName}', which was never read");
        return JsonPointer.Select(reading.Value ?? default, pointer, state.Scalars());
    }

    // ---- the digest -------------------------------------------------------------------------- //

    static void ApplyDigest(ScenarioDocument doc, List<ScenarioStep> digestSteps, RunState state,
        ScenarioVerdict verdict)
    {
        if (digestSteps.Count == 0) return;

        // One digest per scenario; a second `digest` step would be two claims about one run, which the
        // validator's declared-before-use rule already makes meaningless.
        var step = digestSteps[0];
        var exclusions = ReadingDigest.MergeExclusions(step.Exclude);
        verdict.DigestExclusions = exclusions
            .Select(e => new ExclusionRecord { Field = e.Field, Reason = e.Reason }).ToList();

        var entries = new List<ReadingDigest.DigestEntry>();
        foreach (var reference in step.Include!)
        {
            var hash = reference.IndexOf('#');
            var readingName = reference[..hash];
            var pointer = reference[(hash + 1)..];
            if (!state.Readings.TryGetValue(readingName, out var reading))
            {
                verdict.Failures.Add($"digest: include '{reference}' names reading '{readingName}', which was never read");
                continue;
            }

            var value = pointer is "" or "$"
                ? reading.Value
                : JsonPointer.Select(reading.Value ?? default, pointer, state.Scalars());
            entries.Add(new ReadingDigest.DigestEntry(reference, reading.Source, value));
        }

        if (entries.Count > 0)
            verdict.Digest = ReadingDigest.Compute(entries, exclusions);
    }

    // ---- route resolution -------------------------------------------------------------------- //

    /// <summary>Resolves a call's path from the declared route template. The method is NOT part of the
    /// result — <see cref="ScenarioOp.Method"/> owns it, so the two can never be written twice.</summary>
    static string ResolvePath(string route, (string Placeholder, string Capture)[] fromContext, RunState state)
    {
        var space = route.IndexOf(' ');
        var path = route[(space + 1)..];
        foreach (var (placeholder, capture) in fromContext)
        {
            if (!state.Captures.TryGetValue(capture, out var value) || value is null)
                throw new InvalidOperationException($"'{route}' needs the captured value '{capture}', which no earlier step captured");
            path = path.Replace("{" + placeholder + "}", Escape(ScalarText(value.Value)), StringComparison.Ordinal);
        }
        return path;
    }

    /// <summary>A read's template is resolved from the captures directly: `{playerId}` names the capture
    /// `playerId`. A read that needs a differently-named value writes the name it captured. Only SCALAR
    /// captures can fill a route slot — a composite one is refused by name rather than serialized into a
    /// URL, and the failure says which capture and which kind it is.</summary>
    static string ResolveTemplate(string template, RunState state)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in state.Captures)
            if (value is not null && IsScalar(value.Value)) map[name] = ScalarText(value.Value);

        var resolved = ScenarioExpectations.Interpolate(template, map, out var error);
        if (error is null) return resolved;

        foreach (var (name, value) in state.Captures)
            if (template.Contains("{" + name + "}", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"'{name}' holds a {value?.ValueKind} capture; a route placeholder needs a scalar");
        throw new InvalidOperationException(error);
    }

    static bool IsScalar(JsonElement e) => e.ValueKind is JsonValueKind.String or JsonValueKind.Number
        or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

    static string ScalarText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new InvalidOperationException(
            $"a route placeholder needs a scalar; this value is a {e.ValueKind} (capture a scalar, or compare the " +
            "array in an expect.* step instead)")
    };

    static string Escape(string s) => Uri.EscapeDataString(s);

    static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    /// <summary>The run's mutable state: captures (sequencing) and readings (evidence). Two maps, on
    /// purpose — a value cannot be both, and the verdict writes them into two different arrays.</summary>
    sealed class RunState
    {
        public readonly Dictionary<string, JsonElement?> Captures = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ScenarioReading> Readings = new(StringComparer.Ordinal);

        /// <summary>The captures as the pointer layer wants them for a bracket key match: a scalar is
        /// its own text, a composite is its raw JSON (which can never equal a scalar, and should not).</summary>
        public IReadOnlyDictionary<string, string?> Scalars()
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, value) in Captures)
                map[name] = value is null || value.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : value.Value.ValueKind == JsonValueKind.String
                        ? value.Value.GetString()
                        : value.Value.GetRawText();
            return map;
        }
    }
}
