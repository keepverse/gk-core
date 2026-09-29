using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The contract's machine. Everything the format promises is refused here, loudly, before a run
/// touches a server: an op that is not in the closed vocabulary, a declared route that is not the
/// route that op calls, a read that is not a read-back, an assertion over a reading that was never
/// declared (or is declared later), a digest exclusion with no reason, a step with no <c>why</c>.
///
/// <para>It returns a list of messages rather than throwing on the first one, because a scenario
/// author wants all of them at once. A caller that needs to hard-stop calls
/// <see cref="ValidateOrThrow"/>.</para>
/// </summary>
public static class ScenarioValidator
{
    public static void ValidateOrThrow(ScenarioDocument doc)
    {
        var errors = Validate(doc);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"scenario '{doc.Id}' is invalid ({errors.Count}):\n  " + string.Join("\n  ", errors));
    }

    public static IReadOnlyList<string> Validate(ScenarioDocument doc)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(doc.Id)) errors.Add("id: required");
        if (string.IsNullOrWhiteSpace(doc.Description))
            errors.Add("description: required — a scenario says what it is for");
        if (doc.Seed == 0)
            errors.Add("seed: required and non-zero — a seed nobody chose is refused (SquadHarness idiom)");
        if (doc.Clock is null)
            errors.Add("clock: required — the verdict must be able to print what the run believed the clock was");
        else
        {
            if (!ScenarioVocabulary.ClockModes.Contains(doc.Clock.Mode, StringComparer.Ordinal))
                errors.Add($"clock.mode: '{doc.Clock.Mode}' is not one of {string.Join("|", ScenarioVocabulary.ClockModes)}");
            if (string.IsNullOrWhiteSpace(doc.Clock.Note))
                errors.Add("clock.note: required — an undeclared clock makes every timestamp reading meaningless");

            // RS-F16 candidate (1), landed as increment 5a: `offset` is honest now — the in-process host
            // factory and ProcessHost both apply the declared offset at boot through the ONE seam. `explicit`
            // (an absolute instant) stays refused: the seam's product shape is a signed offset, and a route
            // that sets a clock is deliberately not specified (spec §2, owner ruling D3 (b)).
            if (doc.Clock.Mode == "offset")
            {
                if (doc.Clock.OffsetSeconds is null)
                    errors.Add("clock.offsetSeconds: required when clock.mode is 'offset' — the declaration is the input, so it must say how much");
            }
            else if (doc.Clock.Mode != "ambient")
            {
                errors.Add($"clock.mode '{doc.Clock.Mode}': not accepted — 'offset' is the only non-ambient mode " +
                           "the seam has a product shape for (a signed offset applied at boot); an absolute clock is " +
                           "not specified and a route that sets one is refused (spec §2, owner ruling D3 (b))");
            }

            if (doc.Clock.Mode != "offset" && doc.Clock.OffsetSeconds is not null)
                errors.Add("clock.offsetSeconds: only 'offset' takes an offset — an ambient run that declares one would make the declaration lie");
        }

        // Registered readings and captures are declared-before-use, so an assertion cannot be
        // written against a reading that never ran.
        var readings = new HashSet<string>(StringComparer.Ordinal);
        var captures = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < doc.Steps.Count; i++)
        {
            var step = doc.Steps[i];
            var where = $"steps[{i}] ({step.Op})";
            if (string.IsNullOrWhiteSpace(step.Op)) { errors.Add($"{where}: op required"); continue; }
            if (string.IsNullOrWhiteSpace(step.Why))
                errors.Add($"{where}: why required — a step that cannot say why it exists is not a step");

            switch (ScenarioVocabulary.Kind(step.Op))
            {
                case ScenarioVocabulary.Call:
                    ValidateCall(step, where, errors);
                    ValidateCaptures(step, where, captures, errors);
                    break;
                case ScenarioVocabulary.Read:
                    ValidateRead(step, where, readings, errors);
                    ValidateCaptures(step, where, captures, errors);
                    break;
                case ScenarioVocabulary.Expect:
                    ValidateExpect(step, where, readings, captures, errors);
                    break;
                case ScenarioVocabulary.Digest:
                    ValidateDigest(step, where, readings, errors);
                    break;
                case ScenarioVocabulary.Clock:
                    // RS3 increment 5b: `clock.set` declares an ABSOLUTE offset from the machine clock. The
                    // value is required — a movement with no amount is not a declaration — and the runner
                    // refuses the run when the host cannot be told (there is no route: spec §2).
                    if (step.OffsetSeconds is null)
                        errors.Add($"{where}: clock.set needs offsetSeconds — the absolute offset from the machine clock the host must believe");
                    if (step.Route is not null)
                        errors.Add($"{where}: clock.set names no route — a route that sets the clock is deliberately not specified (rpg-simulator-spec-clock-seam.md §2)");
                    break;
                default:
                    errors.Add($"{where}: '{step.Op}' has no surface (expected sim.* | test.* | api.* | read.* | expect.* | clock.set | digest)");
                    break;
            }
        }

        if (readings.Count == 0)
            errors.Add("steps: no read.* step — a verdict with nothing read back is not a verdict");

        return errors;
    }

    static void ValidateCall(ScenarioStep step, string where, List<string> errors)
    {
        var op = ScenarioVocabulary.CallOp(step.Op);
        if (op is null)
        {
            errors.Add($"{where}: '{step.Op}' is not in the closed call vocabulary " +
                       $"({string.Join(", ", ScenarioVocabulary.Calls.Select(c => c.Name))})");
            return;
        }

        if (string.IsNullOrWhiteSpace(step.Route))
        {
            errors.Add($"{where}: route required — '{op.Name}' calls '{op.Method} {op.RouteTemplate}'");
            return;
        }

        if (step.Route != $"{op.Method} {op.RouteTemplate}")
            errors.Add($"{where}: declared route '{step.Route}' is not the route '{op.Name}' calls " +
                       $"('{op.Method} {op.RouteTemplate}')");

        if (ScenarioVocabulary.RouteSurface(step.Route) != op.Surface)
            errors.Add($"{where}: declared route's surface is not the op's surface '{op.Surface}'");

        foreach (var (placeholder, capture) in op.PathFromContext)
            if (string.IsNullOrWhiteSpace(placeholder) || string.IsNullOrWhiteSpace(capture))
                errors.Add($"{where}: vocabulary defect — empty path placeholder/capture name");

        foreach (var (field, source) in op.Query.Concat(op.Body))
            if (!IsValidSource(source))
                errors.Add($"{where}: vocabulary defect — '{field}' has source '{source}' " +
                           "(expected ctx:<capture>, arg:<arg> or derived:correlationId)");

        if (step.Args is not null)
            foreach (var key in step.Args.Keys)
                if (string.IsNullOrWhiteSpace(key)) errors.Add($"{where}: args has an empty key");

        // A declared arg the op never reads is a scenario author's mistake, and a silent one: the
        // request would simply not carry it. Refused here so the mistake is a validation error.
        var usedArgs = op.Query.Concat(op.Body)
            .Where(p => p.Source.StartsWith(ScenarioSources.ArgPrefix, StringComparison.Ordinal))
            .Select(p => p.Source[ScenarioSources.ArgPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in step.Args?.Keys ?? Enumerable.Empty<string>())
            if (!usedArgs.Contains(key))
                errors.Add($"{where}: arg '{key}' is not read by '{op.Name}' " +
                           $"(it reads: {(usedArgs.Count == 0 ? "none" : string.Join(", ", usedArgs))})");
    }

    static bool IsValidSource(string source) =>
        source.StartsWith(ScenarioSources.ContextPrefix, StringComparison.Ordinal)
        || source.StartsWith(ScenarioSources.ArgPrefix, StringComparison.Ordinal)
        || source == ScenarioSources.CorrelationId;

    static void ValidateRead(ScenarioStep step, string where, HashSet<string> readings, List<string> errors)
    {
        var name = step.Op.Length > "read.".Length ? step.Op["read.".Length..] : "";
        if (string.IsNullOrWhiteSpace(name)) errors.Add($"{where}: read.<name> requires a name");
        else if (!readings.Add(step.Op))
            errors.Add($"{where}: reading '{step.Op}' is declared twice");

        if (string.IsNullOrWhiteSpace(step.Route))
            errors.Add($"{where}: route required — a read-back names the route or hub message it came from");
        else if (!ScenarioVocabulary.IsFeFacingRead(step.Route))
            errors.Add($"{where}: '{step.Route}' is not an FE-facing read — a verdict reads the same query " +
                       "path the web FE uses (a GET under /api/ outside /api/test and /api/sim), or a hub message " +
                       "(live-probe-standard.md §3)");
    }

    static void ValidateExpect(ScenarioStep step, string where, HashSet<string> readings,
        HashSet<string> captures, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(step.Reading))
            errors.Add($"{where}: reading required");
        else if (!readings.Contains(step.Reading))
            errors.Add($"{where}: reading '{step.Reading}' was never declared before this step");

        if (string.IsNullOrWhiteSpace(step.Check) || !ScenarioVocabulary.Checks.Contains(step.Check, StringComparer.Ordinal))
            errors.Add($"{where}: check '{step.Check}' is not one of {string.Join("|", ScenarioVocabulary.Checks)}");

        if (string.IsNullOrWhiteSpace(step.Path) || !step.Path.StartsWith('$'))
            errors.Add($"{where}: path required and must start with '$'");

        var needsValue = ScenarioVocabulary.ValueChecks.Contains(step.Check, StringComparer.Ordinal);
        var needsOther = ScenarioVocabulary.OtherChecks.Contains(step.Check, StringComparer.Ordinal)
                         || step.Check == "equals";
        if (needsValue && step.Value is null && string.IsNullOrWhiteSpace(step.Other))
            errors.Add($"{where}: check '{step.Check}' needs a value (a literal) or other " +
                       "(reading#/pointer | {capturedName})");
        if (needsValue && step.Value is not null && step.Other is not null)
            errors.Add($"{where}: check '{step.Check}' takes value or other, never both");
        if (ScenarioVocabulary.OtherChecks.Contains(step.Check, StringComparer.Ordinal)
            && string.IsNullOrWhiteSpace(step.Other))
            errors.Add($"{where}: check '{step.Check}' needs other (reading#/pointer or {{capturedName}}) — " +
                       "a comparison against a constant is 'equals' with a value");
        if (step.Value is not null && ScenarioVocabulary.OtherChecks.Contains(step.Check, StringComparer.Ordinal))
            errors.Add($"{where}: check '{step.Check}' compares to other, not value");
        if (!string.IsNullOrWhiteSpace(step.Other)) ValidateOtherRef(step.Other!, where, readings, captures, errors);
        if (step.Value is not null && step.Value.Value.ValueKind == JsonValueKind.Undefined)
            errors.Add($"{where}: value is undefined");
    }

    static void ValidateDigest(ScenarioStep step, string where, HashSet<string> readings, List<string> errors)
    {
        if (step.Include is null || step.Include.Count == 0)
            errors.Add($"{where}: include required — a digest with nothing in it proves nothing");
        else
            foreach (var reference in step.Include)
            {
                var reading = readingRef(reference);
                if (reading is null)
                    errors.Add($"{where}: include '{reference}' must be <reading>#</$pointer>");
                else if (!readings.Contains(reading))
                    errors.Add($"{where}: include '{reference}' names reading '{reading}', which was never declared before this step");
            }

        // The exclusion list is load-bearing (BattleGoldenTests.cs:163-172): a field may not be
        // blanked out of the hash without a written reason, because a digest that moves for a reason
        // that is not a determinism break is worse than no digest.
        foreach (var exclusion in step.Exclude ?? new List<ScenarioExclusion>())
        {
            if (string.IsNullOrWhiteSpace(exclusion.Field))
                errors.Add($"{where}: exclude has an empty field");
            if (string.IsNullOrWhiteSpace(exclusion.Reason))
                errors.Add($"{where}: exclude '{exclusion.Field}' has no reason — the exclusion list is load-bearing");
        }
    }

    static void ValidateCaptures(ScenarioStep step, string where, HashSet<string> captures, List<string> errors)
    {
        foreach (var capture in step.Capture ?? new List<ScenarioCapture>())
        {
            if (string.IsNullOrWhiteSpace(capture.Name)) { errors.Add($"{where}: capture has an empty name"); continue; }
            if (!captures.Add(capture.Name))
                errors.Add($"{where}: capture '{capture.Name}' is declared twice — a later step could not tell which value it holds");
            if (string.IsNullOrWhiteSpace(capture.Path) || !capture.Path.StartsWith('$'))
                errors.Add($"{where}: capture '{capture.Name}' needs a path starting with '$'");
        }
    }

    static void ValidateOtherRef(string other, string where, HashSet<string> readings,
        HashSet<string> captures, List<string> errors)
    {
        if (other.StartsWith('{') && other.EndsWith('}'))
        {
            var name = other[1..^1];
            if (!captures.Contains(name))
                errors.Add($"{where}: other '{other}' names a value that was never captured");
            return;
        }

        var reading = readingRef(other);
        if (reading is null)
            errors.Add($"{where}: other '{other}' must be <reading>#</$pointer> or {{capturedName}}");
        else if (!readings.Contains(reading))
            errors.Add($"{where}: other '{other}' names reading '{reading}', which was never declared before this step");
    }

    static string? readingRef(string reference)
    {
        var hash = reference.IndexOf('#');
        return hash <= 0 ? null : reference[..hash];
    }
}
