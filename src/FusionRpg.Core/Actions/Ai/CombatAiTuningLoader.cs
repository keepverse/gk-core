using System.Text.Json;
using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md §5): pure parser, no file I/O
/// (tunables-ssot.md §7.2 — "Core still does no `File.Read`; hosts inject"). Copies the shipped
/// convention exactly (`Battle/Board/SiegeTuning.cs`'s `Obj`/`Int`/`Str`/`Long` helper shape): every
/// range check and structural requirement runs ONCE here, at parse, never at the hot decision path.
/// </summary>
public static class CombatAiTuningLoader
{
    public static CombatAiTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CombatAiTuningRejection("combat-ai tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CombatAiTuningRejection($"combat-ai tuning: not valid JSON -- {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion");
            var version = Int(root, "version");

            var profilesObj = Obj(root, "profiles");
            var profiles = new Dictionary<string, CombatAiProfile>(StringComparer.Ordinal);
            foreach (var prop in profilesObj.EnumerateObject())
                profiles[prop.Name] = ParseProfile(prop.Name, prop.Value);

            // The root row is required to exist (§3/§4 rule 4's twin) -- a lookup can never fail at
            // decision time on the hot path.
            if (!profiles.ContainsKey("*/default"))
                throw new CombatAiTuningRejection("combat-ai tuning: missing required root profile '*/default'");

            // Required for the same reason: intent-router (module 4) reads it unconditionally.
            var routerObj = Obj(root, "router");
            var router = new AiRouterBlock(
                OrderTimeoutTicks: Long(routerObj, "orderTimeoutTicks"),
                ReactionsPerRoundExpected: Int(routerObj, "reactionsPerRoundExpected"));

            return new CombatAiTuning(schemaVersion, version, profiles, router);
        }
    }

    static CombatAiProfile ParseProfile(string profileId, JsonElement el)
    {
        var parts = profileId.Split('/');
        if (parts.Length != 2)
            throw new CombatAiTuningRejection(
                $"combat-ai tuning: profile key '{profileId}' must be '<place>/<role>' or '*/<role>'");

        // "*" is a wildcard on the PLACE axis only -- the role segment ("default"/"striker"/...) is
        // always a real AiRole member; AiRole.Default is not itself a wildcard. AiPlace has no
        // wildcard member, so a "*"-keyed profile's Place field is a documented placeholder
        // (AiPlace.Battle) -- ProfileId/the dictionary key is the source of truth for resolution
        // (CombatAiProfilePolicy.For), never this field.
        var place = parts[0] == "*" ? AiPlace.Battle : AiVocabularyParse.Enum<AiPlace>($"profiles.{profileId}", parts[0]);
        var role = AiVocabularyParse.Enum<AiRole>($"profiles.{profileId}", parts[1]);

        var tierOverrideStr = OptStr(el, "tierOverride");
        var tierOverride = tierOverrideStr is null
            ? (AiTier?)null
            : AiVocabularyParse.Enum<AiTier>($"profiles.{profileId}.tierOverride", tierOverrideStr);

        var tierByActorClass = ParseTierByActorClass(profileId, Obj(el, "tierByActorClass"));
        var rows = ParseRows(profileId, Arr(el, "rows"));
        var scoring = ParseScoring(profileId, Obj(el, "scoring"));
        var selection = ParseSelection(profileId, Obj(el, "selection"));
        var reserves = ParseReserves(Arr(el, "reserves"));
        var guards = ParseGuards(Obj(el, "guards"));
        var antiRepeat = ParseAntiRepeat(Obj(el, "antiRepeat"));
        var trigger = TryObj(el, "trigger", out var triggerEl) ? ParseTrigger(profileId, triggerEl) : null;
        var personality = ParsePersonality(profileId, Obj(el, "personality"));

        return new CombatAiProfile(
            ProfileId: profileId, Place: place, Role: role, TierOverride: tierOverride,
            TierByActorClass: tierByActorClass, Rows: rows, Scoring: scoring, Selection: selection,
            Reserves: reserves, Guards: guards, AntiRepeat: antiRepeat, Trigger: trigger,
            Personality: personality);
    }

    static IReadOnlyDictionary<AiActorClass, AiTier> ParseTierByActorClass(string profileId, JsonElement el)
    {
        var map = new Dictionary<AiActorClass, AiTier>();
        foreach (var prop in el.EnumerateObject())
        {
            var actorClass = AiVocabularyParse.Enum<AiActorClass>($"profiles.{profileId}.tierByActorClass", prop.Name);
            if (prop.Value.ValueKind != JsonValueKind.String || prop.Value.GetString() is not { } tierStr)
                throw new CombatAiTuningRejection($"combat-ai tuning: profiles.{profileId}.tierByActorClass.{prop.Name} must be a string");
            map[actorClass] = AiVocabularyParse.Enum<AiTier>($"profiles.{profileId}.tierByActorClass.{prop.Name}", tierStr);
        }

        // Total over the enum -- a map missing a class is rejected at parse, never at decision time.
        foreach (var actorClass in (AiActorClass[])Enum.GetValues(typeof(AiActorClass)))
            if (!map.ContainsKey(actorClass))
                throw new CombatAiTuningRejection(
                    $"combat-ai tuning: profiles.{profileId}.tierByActorClass is missing '{actorClass.ToString().ToLowerInvariant()}'");

        return map;
    }

    static IReadOnlyList<AiProfileRow> ParseRows(string profileId, JsonElement arr)
    {
        var rows = new List<AiProfileRow>(arr.GetArrayLength());
        var i = 0;
        foreach (var rowEl in arr.EnumerateArray())
        {
            var path = $"profiles.{profileId}.rows[{i}]";
            var selector = AiVocabularyParse.Enum<TargetSelector>(path, Str(rowEl, "selector"));
            var condition = AiVocabularyParse.Enum<AiRowCondition>(path, Str(rowEl, "condition"));
            var conditionArgMilli = OptInt(rowEl, "conditionArgMilli") ?? 0;
            var conditionArgId = OptStr(rowEl, "conditionArgId") ?? "";
            var census = AiVocabularyParse.Enum<AiCensusCondition>(path, Str(rowEl, "census"));
            var censusArg = OptInt(rowEl, "censusArg") ?? 0;
            var actions = TryObj(rowEl, "actions", out var actionsEl)
                ? ParseActionFilter(actionsEl)
                : new AiActionFilter(null, null, null, null);

            rows.Add(new AiProfileRow(selector, condition, conditionArgMilli, conditionArgId, census, censusArg, actions));
            i++;
        }

        // Rule 4: the last row must be unconditional, or a decision can fall off the end of the walk.
        if (rows.Count == 0 || rows[^1].Condition != AiRowCondition.Always || rows[^1].Census != AiCensusCondition.None)
            throw new CombatAiTuningRejection(
                $"combat-ai tuning: profiles.{profileId}'s last row must be Condition=Always, Census=None " +
                "(a profile can never fall off the end of the rank walk)");

        return rows;
    }

    static AiActionFilter ParseActionFilter(JsonElement el)
    {
        IReadOnlyList<ActionTag>? tags = null;
        if (TryArr(el, "tags", out var tagsEl))
        {
            var list = new List<ActionTag>(tagsEl.GetArrayLength());
            foreach (var t in tagsEl.EnumerateArray())
                list.Add(AiVocabularyParse.Enum<ActionTag>("actions.tags", t.GetString() ?? ""));
            tags = list;
        }

        IReadOnlyList<string>? families = null;
        if (TryArr(el, "families", out var familiesEl))
        {
            var list = new List<string>(familiesEl.GetArrayLength());
            foreach (var f in familiesEl.EnumerateArray())
                list.Add(f.GetString() ?? "");
            families = list;
        }

        // JSON keys are `rungAtLeast`/`rungAtMost` -- see AiActionFilter's own doc comment
        // (RungSemanticsTests reserves a different rung-floor spelling for a different defect class).
        return new AiActionFilter(tags, families, OptInt(el, "rungAtLeast"), OptInt(el, "rungAtMost"));
    }

    static AiScoringBlock ParseScoring(string profileId, JsonElement el)
    {
        var maxCandidatesScored = Int(el, "maxCandidatesScored");
        if (maxCandidatesScored <= 0)
            throw new CombatAiTuningRejection(
                $"combat-ai tuning: profiles.{profileId}.scoring.maxCandidatesScored must be > 0; got {maxCandidatesScored}");
        var aggressionRange = Int(el, "aggressionRange");
        if (aggressionRange <= 0)
            throw new CombatAiTuningRejection(
                $"combat-ai tuning: profiles.{profileId}.scoring.aggressionRange must be > 0; got {aggressionRange}");

        // CAI-find-2 (2026-09-23): a NEGATIVE weight is malformed input, not a balance choice. Four
        // of the seven terms (hit-chance, objective, cannot-counter, round) have no downstream clamp,
        // so a negative value INVERTS its own term rather than being rejected; the other three are
        // clamped by `AiPersonalityApply.ClampNonNegative`, which would hide the authoring mistake
        // instead of reporting it. This restores the refusal the ten migrated keys lost in CAI1.8
        // (the old `SiegeTuning` reader carried it). ZERO stays legal: "this term does not score" is
        // a real authoring choice, and the spec's own clamp is about the RESULT, not the input.
        var weightHitChance = NonNegativeWeight(profileId, el, "weightHitChance");
        var weightObjective = NonNegativeWeight(profileId, el, "weightObjective");
        var weightKill = NonNegativeWeight(profileId, el, "weightKill");
        var weightLowHp = NonNegativeWeight(profileId, el, "weightLowHp");
        var weightCannotCounter = NonNegativeWeight(profileId, el, "weightCannotCounter");
        var weightRound = NonNegativeWeight(profileId, el, "weightRound");
        var weightRisk = NonNegativeWeight(profileId, el, "weightRisk");

        return new AiScoringBlock(
            WeightHitChance: weightHitChance, WeightObjective: weightObjective,
            WeightKill: weightKill, WeightLowHp: weightLowHp,
            WeightCannotCounter: weightCannotCounter, WeightRound: weightRound,
            WeightRisk: weightRisk, AggressionRange: aggressionRange,
            MaxCandidatesScored: maxCandidatesScored);
    }

    /// <summary>A negative authored weight is refused at parse, naming the profile and the key — the
    /// same whole-row-rejection posture as the two structural checks above, so a malformed file fails
    /// at load rather than inverting a score term at decision time (CAI-find-2).</summary>
    static int NonNegativeWeight(string profileId, JsonElement el, string key)
    {
        var value = Int(el, key);
        if (value < 0)
            throw new CombatAiTuningRejection(
                $"combat-ai tuning: profiles.{profileId}.scoring.{key} must be >= 0; got {value}");
        return value;
    }

    static AiSelectionBlock ParseSelection(string profileId, JsonElement el)
    {
        var mode = AiVocabularyParse.Enum<SelectionMode>($"profiles.{profileId}.selection.mode", Str(el, "mode"));
        var keepPctMilli = Int(el, "keepPctMilli");
        if (keepPctMilli is < 0 or > 1000)
            throw new CombatAiTuningRejection(
                $"combat-ai tuning: profiles.{profileId}.selection.keepPctMilli must be 0..1000; got {keepPctMilli}");
        return new AiSelectionBlock(mode, keepPctMilli, Str(el, "rngStreamName"));
    }

    static IReadOnlyList<AiReserveFloor> ParseReserves(JsonElement arr)
    {
        var list = new List<AiReserveFloor>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
            list.Add(new AiReserveFloor(Str(el, "resourceId"), Int(el, "floorMilliOfMax")));
        return list;
    }

    static AiWasteGuards ParseGuards(JsonElement el) => new(
        MinTargetsForArea: Int(el, "minTargetsForArea"),
        KillMarginMilli: Int(el, "killMarginMilli"),
        FightEndingLiveCount: Int(el, "fightEndingLiveCount"));

    static AiAntiRepeat ParseAntiRepeat(JsonElement el) => new(
        RetargetLatencyTicks: Long(el, "retargetLatencyTicks"),
        CommitmentBonus: Long(el, "commitmentBonus"),
        RepeatDecayHalfLifeTicks: Int(el, "repeatDecayHalfLifeTicks"));

    static AiTriggerBlock ParseTrigger(string profileId, JsonElement el) => new(
        SwingsN: Int(el, "swingsN"), TicksT: Long(el, "ticksT"), PostCastLockL: Long(el, "postCastLockL"));

    static AiPersonalityBounds ParsePersonality(string profileId, JsonElement el)
    {
        var boundsObj = Obj(el, "bounds");
        var map = new Dictionary<PersonalityAxis, int>();
        foreach (var prop in boundsObj.EnumerateObject())
        {
            var axis = AiVocabularyParse.Enum<PersonalityAxis>($"profiles.{profileId}.personality.bounds", prop.Name);
            map[axis] = prop.Value.GetInt32();
        }
        return new AiPersonalityBounds(map);
    }

    // ---- JSON element helpers, matching Battle/Board/SiegeTuning.cs's own shape --------------------

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new CombatAiTuningRejection($"combat-ai tuning: missing or non-object '{key}'");
        return el;
    }

    static bool TryObj(JsonElement parent, string key, out JsonElement value)
    {
        if (parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Object)
        {
            value = el;
            return true;
        }
        value = default;
        return false;
    }

    static JsonElement Arr(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new CombatAiTuningRejection($"combat-ai tuning: missing or non-array '{key}'");
        return el;
    }

    static bool TryArr(JsonElement parent, string key, out JsonElement value)
    {
        if (parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            value = el;
            return true;
        }
        value = default;
        return false;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new CombatAiTuningRejection($"combat-ai tuning: missing or non-integer '{key}'");
        return v;
    }

    static int? OptInt(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v
            : null;

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new CombatAiTuningRejection($"combat-ai tuning: missing or non-integer '{key}'");
        return v;
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String || el.GetString() is not { } s)
            throw new CombatAiTuningRejection($"combat-ai tuning: missing or non-string '{key}'");
        return s;
    }

    static string? OptStr(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
