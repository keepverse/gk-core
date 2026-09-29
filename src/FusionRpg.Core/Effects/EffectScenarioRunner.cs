using System.Text.Json;
using System.Text.Json.Serialization;
using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Plugins;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Effects;

public sealed class EffectScenarioDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("seed")] public int Seed { get; set; } = 42;
    [JsonPropertyName("matchKey")] public string MatchKey { get; set; } = "sim-match";
    [JsonPropertyName("plugins")] public List<string>? Plugins { get; set; }
    [JsonPropertyName("board")] public List<BoardEntitySnapDto>? Board { get; set; }
    [JsonPropertyName("steps")] public List<EffectScenarioStepDto> Steps { get; set; } = new();
}

public sealed class BoardEntitySnapDto
{
    [JsonPropertyName("ptr")] public string Ptr { get; set; } = "";
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("typeId")] public int TypeId { get; set; }
    [JsonPropertyName("col")] public int Col { get; set; }
    [JsonPropertyName("row")] public int Row { get; set; }
    [JsonPropertyName("mindControlled")] public bool MindControlled { get; set; }
    [JsonPropertyName("derivedProfile")] public string? DerivedProfile { get; set; }
    [JsonPropertyName("derived")] public Dictionary<string, double>? Derived { get; set; }
}

public sealed class EffectScenarioStepDto
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";

    [JsonPropertyName("grant")] public EffectGrantDto? Grant { get; set; }
    [JsonPropertyName("grantId")] public string? GrantId { get; set; }
    [JsonPropertyName("ms")] public int? Ms { get; set; }

    [JsonPropertyName("actorPtr")] public string? ActorPtr { get; set; }
    [JsonPropertyName("targetPtr")] public string? TargetPtr { get; set; }
    [JsonPropertyName("attackerSide")] public string? AttackerSide { get; set; }
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("ptr")] public string? Ptr { get; set; }
    [JsonPropertyName("typeId")] public int? TypeId { get; set; }
    [JsonPropertyName("targetTypeId")] public int? TargetTypeId { get; set; }
    [JsonPropertyName("damage")] public long? Damage { get; set; }
    [JsonPropertyName("killerPtr")] public string? KillerPtr { get; set; }

    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("payload")] public Dictionary<string, JsonElement>? Payload { get; set; }
    [JsonPropertyName("event")] public EffectEventDto? Event { get; set; }

    [JsonPropertyName("expect")] public IntentPlanDto? Expect { get; set; }
    [JsonPropertyName("golden")] public string? Golden { get; set; }
    [JsonPropertyName("contains")] public string? Contains { get; set; }
    [JsonPropertyName("hostPtr")] public string? HostPtr { get; set; }
    [JsonPropertyName("statusId")] public string? StatusId { get; set; }
    [JsonPropertyName("present")] public bool? Present { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class EffectScenarioStepResult
{
    public int Index { get; init; }
    public string Op { get; init; } = "";
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public IntentPlanDto? Plan { get; init; }
}

public sealed class EffectScenarioRunResult
{
    public string Id { get; init; } = "";
    public bool Ok { get; init; }
    public List<EffectScenarioStepResult> Steps { get; init; } = new();
    public string? Error => Steps.FirstOrDefault(s => !s.Ok)?.Error;
}

/// <summary>Declarative offline Effect scenarios — Plan asserts only, never lawn.</summary>
public static class EffectScenarioRunner
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <param name="catalog">
    /// Which defs to run the scenario against. Null means the seeded ones. E11 runs every fixture
    /// twice — once seeded, once against the compiled atom catalog — and diffs the plans, which is
    /// the module's whole acceptance and was unreachable while this was hardcoded.
    /// </param>
    public static EffectScenarioRunResult RunFile(
        string path, string? goldenRoot = null, IEnumerable<EffectDef>? catalog = null)
    {
        var dto = JsonSerializer.Deserialize<EffectScenarioDto>(File.ReadAllText(path), JsonOpts)
                  ?? throw new InvalidOperationException("null scenario: " + path);
        goldenRoot ??= Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".."));
        return Run(dto, goldenRoot, catalog);
    }

    public static EffectScenarioRunResult Run(
        EffectScenarioDto scenario, string? goldenRoot = null, IEnumerable<EffectDef>? catalog = null)
    {
        var host = new SimEffectHost(scenario.Seed, scenario.MatchKey, catalog);
        if (scenario.Plugins is { Count: > 0 })
            SecondaryPluginRegistry.RegisterById(host.Plugins, scenario.Plugins);
        if (scenario.Board is { Count: > 0 })
        {
            host.SetBoard(scenario.Board.Select(b => new BoardEntitySnap
            {
                Ptr = b.Ptr,
                Side = b.Side,
                TypeId = b.TypeId,
                Col = b.Col,
                Row = b.Row,
                MindControlled = b.MindControlled
            }));
            foreach (var b in scenario.Board)
            {
                if (string.IsNullOrWhiteSpace(b.Ptr)) continue;
                if (string.IsNullOrWhiteSpace(b.DerivedProfile) && (b.Derived == null || b.Derived.Count == 0))
                    continue;
                host.PinDerived(b.Ptr, ActorDerivedProfiles.Resolve(b.DerivedProfile, b.Derived));
            }
        }
        var results = new List<EffectScenarioStepResult>();
        IntentPlanDto? lastPlan = null;
        var ok = true;

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            try
            {
                var r = Execute(host, step, i, goldenRoot, scenario.MatchKey, ref lastPlan);
                results.Add(r);
                if (!r.Ok)
                {
                    ok = false;
                    break;
                }
            }
            catch (Exception ex)
            {
                results.Add(new EffectScenarioStepResult
                {
                    Index = i,
                    Op = step.Op,
                    Ok = false,
                    Error = ex.Message,
                    Plan = lastPlan
                });
                ok = false;
                break;
            }
        }

        return new EffectScenarioRunResult { Id = scenario.Id, Ok = ok, Steps = results };
    }

    static EffectScenarioStepResult Execute(
        SimEffectHost host,
        EffectScenarioStepDto step,
        int index,
        string? goldenRoot,
        string scenarioMatchKey,
        ref IntentPlanDto? lastPlan)
    {
        var op = (step.Op ?? "").Trim();
        var key = op.ToLowerInvariant();
        IntentPlanDto? plan = null;

        switch (key)
        {
            case "clear":
                host.ClearAll();
                lastPlan = null;
                break;

            case "matchstart":
                host.BeginMatch(scenarioMatchKey);
                lastPlan = null;
                break;

            case "matchend":
                host.EndMatch();
                lastPlan = null;
                break;

            case "grant":
                if (step.Grant == null) throw new InvalidOperationException("grant requires grant dto");
                if (step.Grant.Overlay != null)
                    step.Grant.Overlay = JsonOverlay.FromObject(step.Grant.Overlay);
                host.Grant(step.Grant);
                // Passive OnGranted may leave items on sink — capture as plan if sink has actions
                if (host.Sink.Items.Count > 0)
                {
                    lastPlan = new IntentPlanDto
                    {
                        ContractVersion = FoundationContractVersion.Current,
                        Trigger = EffectTriggers.OnGranted,
                        Actions = host.Sink.Items.ToList(),
                        Skipped = host.Bag.LastSkipped.ToList()
                    };
                    plan = lastPlan;
                }
                break;

            case "withdraw":
                if (string.IsNullOrWhiteSpace(step.GrantId))
                    throw new InvalidOperationException("withdraw requires grantId");
                host.Withdraw(step.GrantId);
                if (host.Sink.Items.Count > 0)
                {
                    lastPlan = new IntentPlanDto
                    {
                        ContractVersion = FoundationContractVersion.Current,
                        Trigger = EffectTriggers.OnRemoved,
                        Actions = host.Sink.Items.ToList(),
                        Skipped = host.Bag.LastSkipped.ToList()
                    };
                    plan = lastPlan;
                }
                break;

            case "advancems":
            case "advance":
            {
                var before = host.Sink.Items.Count;
                host.AdvanceMs(step.Ms ?? 0);
                var added = host.Sink.Items.Skip(before).ToList();
                lastPlan = new IntentPlanDto
                {
                    ContractVersion = FoundationContractVersion.Current,
                    Trigger = EffectTriggers.OnTimer,
                    Actions = added,
                    Skipped = host.Bag.LastSkipped.ToList()
                };
                plan = lastPlan;
                break;
            }

            case "hit":
                plan = host.HitDealt(
                    actorPtr: step.ActorPtr ?? "0xA",
                    targetPtr: step.TargetPtr ?? "0xB",
                    attackerSide: step.AttackerSide ?? "plant",
                    typeId: step.TypeId ?? 0,
                    targetTypeId: step.TargetTypeId ?? 0,
                    damage: step.Damage ?? 20);
                lastPlan = plan;
                break;

            case "die":
                plan = host.Die(
                    side: step.Side ?? "zombie",
                    ptr: step.Ptr ?? "0xZ",
                    typeId: step.TypeId ?? 0,
                    killerPtr: step.KillerPtr ?? "0xK");
                lastPlan = plan;
                break;

            case "spawn":
                plan = host.Spawn(
                    side: step.Side ?? "plant",
                    ptr: step.Ptr ?? "0xP",
                    typeId: step.TypeId ?? 0);
                lastPlan = plan;
                break;

            case "event":
                if (step.Event == null) throw new InvalidOperationException("event requires event dto");
                plan = host.OnEvent(step.Event);
                lastPlan = plan;
                break;

            case "fire":
            case "firefromcapture":
                if (string.IsNullOrWhiteSpace(step.Kind))
                    throw new InvalidOperationException("fire requires kind");
                plan = host.FireFromCapture(step.Kind, ToObjectDict(step.Payload));
                lastPlan = plan;
                break;

            case "expectplan":
            {
                var actual = lastPlan ?? throw new InvalidOperationException("expectPlan with no prior plan");
                ComparePlans(ResolveExpected(step, goldenRoot), actual);
                return Pass(index, op, actual);
            }

            case "expectempty":
            {
                var actual = lastPlan ?? throw new InvalidOperationException("expectEmpty with no prior plan");
                if (actual.Actions.Count != 0)
                    throw new InvalidOperationException("expected empty actions, got " + actual.Actions.Count);
                return Pass(index, op, actual);
            }

            case "expectskippedcontains":
            {
                var actual = lastPlan ?? throw new InvalidOperationException("expectSkippedContains with no prior plan");
                var needle = step.Contains ?? "";
                if (!actual.Skipped.Any(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "skipped missing '" + needle + "': " + string.Join(",", actual.Skipped));
                return Pass(index, op, actual);
            }

            case "expectstatus":
            {
                var status = host.Bag.Status ?? throw new InvalidOperationException("expectStatus: no StatusRuntime");
                var hostPtr = step.HostPtr ?? throw new InvalidOperationException("expectStatus requires hostPtr");
                var statusId = step.StatusId ?? throw new InvalidOperationException("expectStatus requires statusId");
                var present = step.Present ?? true;
                var found = status.ForHost(hostPtr).Any(i =>
                    string.Equals(i.StatusId, statusId, StringComparison.OrdinalIgnoreCase));
                if (found != present)
                    throw new InvalidOperationException(
                        "expectStatus " + statusId + " on " + hostPtr + " present=" + found + " expected=" + present);
                return Pass(index, op, lastPlan);
            }

            case "expectresisted":
            {
                var status = host.Bag.Status ?? throw new InvalidOperationException("expectResisted: no StatusRuntime");
                var hostPtr = step.HostPtr;
                var statusId = step.StatusId;
                var reason = step.Reason ?? "";
                var hit = status.ResistedEvents.Any(ev =>
                    (string.IsNullOrWhiteSpace(hostPtr) || CombatPtr.EqualsPtr(ev.HostPtr, hostPtr)) &&
                    (string.IsNullOrWhiteSpace(statusId) ||
                     string.Equals(ev.StatusId, statusId, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(reason) ||
                     string.Equals(ev.Reason.ToString(), reason, StringComparison.OrdinalIgnoreCase)));
                if (!hit)
                    throw new InvalidOperationException(
                        "expectResisted no match host=" + hostPtr + " statusId=" + statusId + " reason=" + reason);
                return Pass(index, op, lastPlan);
            }

            default:
                throw new InvalidOperationException("unknown op: " + op);
        }

        if (step.Expect != null || !string.IsNullOrWhiteSpace(step.Golden))
        {
            var actual = plan ?? lastPlan ?? throw new InvalidOperationException("golden/expect with no plan");
            ComparePlans(ResolveExpected(step, goldenRoot), actual);
        }

        return Pass(index, op, plan);
    }

    static EffectScenarioStepResult Pass(int index, string op, IntentPlanDto? plan) => new()
    {
        Index = index,
        Op = op,
        Ok = true,
        Plan = plan
    };

    static IntentPlanDto ResolveExpected(EffectScenarioStepDto step, string? goldenRoot)
    {
        if (step.Expect != null) return NormalizePlan(step.Expect);
        if (string.IsNullOrWhiteSpace(step.Golden))
            throw new InvalidOperationException("expectPlan needs expect or golden");

        var root = goldenRoot ?? ".";
        var candidates = new[]
        {
            Path.IsPathRooted(step.Golden) ? step.Golden : null,
            Path.Combine(root, step.Golden),
            Path.Combine(root, "effects", step.Golden),
            Path.Combine(root, "..", step.Golden)
        };
        foreach (var c in candidates)
        {
            if (c == null) continue;
            var full = Path.GetFullPath(c);
            if (File.Exists(full))
            {
                var dto = JsonSerializer.Deserialize<IntentPlanDto>(File.ReadAllText(full), JsonOpts)
                          ?? throw new InvalidOperationException("null golden");
                return NormalizePlan(dto);
            }
        }

        throw new FileNotFoundException("golden not found: " + step.Golden);
    }

    static IntentPlanDto NormalizePlan(IntentPlanDto p)
    {
        foreach (var a in p.Actions)
            a.Params = JsonOverlay.FromObject(a.Params);
        return p;
    }

    static void ComparePlans(IntentPlanDto expected, IntentPlanDto actual)
    {
        if (expected.ContractVersion != 0 && expected.ContractVersion != actual.ContractVersion)
            throw new InvalidOperationException(
                $"contractVersion {actual.ContractVersion} != {expected.ContractVersion}");
        if (!string.IsNullOrEmpty(expected.Trigger) &&
            !string.Equals(expected.Trigger, actual.Trigger, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"trigger {actual.Trigger} != {expected.Trigger}");
        if (expected.Actions.Count != actual.Actions.Count)
            throw new InvalidOperationException(
                $"actions count {actual.Actions.Count} != {expected.Actions.Count}");

        for (var i = 0; i < expected.Actions.Count; i++)
        {
            var e = expected.Actions[i];
            var a = actual.Actions[i];
            if (!string.Equals(e.Action, a.Action, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"action[{i}] {a.Action} != {e.Action}");
            if (!string.IsNullOrEmpty(e.EffectId) &&
                !string.Equals(e.EffectId, a.EffectId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"effectId[{i}] {a.EffectId} != {e.EffectId}");
            if (!string.IsNullOrEmpty(e.GrantId) &&
                !string.Equals(e.GrantId, a.GrantId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"grantId[{i}] {a.GrantId} != {e.GrantId}");

            var expectParams = JsonOverlay.FromObject(e.Params);
            var actualParams = JsonOverlay.FromObject(a.Params);
            foreach (var kv in expectParams)
            {
                actualParams.TryGetValue(kv.Key, out var gotObj);
                if (!ParamEquals(kv.Value, gotObj))
                    throw new InvalidOperationException(
                        $"params[{i}].{kv.Key} '{FormatParam(gotObj)}' != '{FormatParam(kv.Value)}'");
            }
        }
    }

    static bool ParamEquals(object? want, object? got)
    {
        if (want == null && got == null) return true;
        if (want == null || got == null) return false;
        if (want is bool wb && got is bool gb) return wb == gb;
        if (TryNumber(want, out var wn) && TryNumber(got, out var gn))
            return Math.Abs(wn - gn) < 1e-9;
        var wantS = Convert.ToString(want, System.Globalization.CultureInfo.InvariantCulture);
        var gotS = Convert.ToString(got, System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(wantS, gotS, StringComparison.OrdinalIgnoreCase);
    }

    static bool TryNumber(object v, out double n)
    {
        switch (v)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                n = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out n):
                return true;
            default:
                n = 0;
                return false;
        }
    }

    static string FormatParam(object? v) =>
        Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    static Dictionary<string, object> ToObjectDict(Dictionary<string, JsonElement>? payload)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (payload == null) return d;
        foreach (var kv in payload)
        {
            object? v = kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString(),
                JsonValueKind.Number => kv.Value.TryGetInt64(out var l) ? l : kv.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => kv.Value.GetRawText()
            };
            if (v != null) d[kv.Key] = v;
        }
        return d;
    }
}
