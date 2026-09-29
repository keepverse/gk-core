using System.Text.Json;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.8, spec-lawn-cast-trigger.md §287-306): the lawn
/// section of `data/tuning/combat-ai.v{n}.json`, parsed. Four BALANCE keys — the cadence `N`, the timer
/// `T`, the post-cast lock `L` and the seeded-offset stream name — and **no** structural value: the four
/// structural numbers (`CarryCasts`, `DecisionsPerFrame`, `CastTokens`, `CastTokenTimeoutTicks`) are code
/// `const`s in <c>LawnDecisionTrigger</c>/<c>LawnDecisionBudget</c>/<c>LawnCastTokenPool</c>, because a
/// schema field for a number that lives in code is the dead-config shape the spec's own plan correction 2
/// forbids.
///
/// <para>Pure, like every other tuning loader (`tunables-ssot.md` §7.2): Core reads no file; the hosts do.
/// An ABSENT lawn section is refused rather than defaulted — the caller is a host that named the file, and
/// silently running on invented numbers is how a cadence nobody chose ships.</para>
/// </summary>
public sealed record CombatAiLawnTuning(
    int SwingsPerDecision,
    int TicksPerDecision,
    int PostCastLockTicks,
    string OffsetStream)
{
    public static CombatAiLawnTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CombatAiTuningRejection("combat-ai lawn tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CombatAiTuningRejection($"combat-ai lawn tuning: not valid JSON -- {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("lawn", out var lawn) || lawn.ValueKind != JsonValueKind.Object)
                throw new CombatAiTuningRejection(
                    "combat-ai lawn tuning: no 'lawn' section -- the revision is older than the lawn keys");

            if (!lawn.TryGetProperty("trigger", out var trigger) || trigger.ValueKind != JsonValueKind.Object)
                throw new CombatAiTuningRejection("combat-ai lawn tuning: missing or non-object 'lawn.trigger'");

            var swings = Int(trigger, "swingsPerDecision");
            var ticks = Int(trigger, "ticksPerDecision");
            var lockTicks = Int(trigger, "postCastLockTicks");
            var stream = Str(trigger, "offsetStream");

            // Non-negative and non-empty, the same posture `LawnDecisionTrigger`'s own ctor takes -- refused
            // HERE so a host fails at boot rather than at the first decision.
            if (swings < 0) throw new CombatAiTuningRejection($"combat-ai lawn tuning: lawn.trigger.swingsPerDecision must be >= 0; got {swings}");
            if (ticks < 0) throw new CombatAiTuningRejection($"combat-ai lawn tuning: lawn.trigger.ticksPerDecision must be >= 0; got {ticks}");
            if (lockTicks < 0) throw new CombatAiTuningRejection($"combat-ai lawn tuning: lawn.trigger.postCastLockTicks must be >= 0; got {lockTicks}");

            return new CombatAiLawnTuning(swings, ticks, lockTicks, stream);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new CombatAiTuningRejection($"combat-ai lawn tuning: missing or non-integer 'lawn.trigger.{key}'");
        return v;
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String || el.GetString() is not { } s || s.Length == 0)
            throw new CombatAiTuningRejection($"combat-ai lawn tuning: missing or empty string 'lawn.trigger.{key}'");
        return s;
    }
}
