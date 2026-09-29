using System.Text.Json;

namespace FusionRpg.Core.Items.Drops;

public sealed class DropVolumeTuningRejection : Exception
{
    public DropVolumeTuningRejection(string message) : base(message) { }
}

/// <summary>Correction 5's two pity counters, keyed on RUNG IDS (`heirloom`, `sunwoven`) — never on
/// an ordinal and never on a positional label like I12's `r4`/`r6`, which named a seven-rung ladder
/// that no longer exists.</summary>
public readonly record struct LootPityTuning(
    long HeirloomHardFloorItems,
    long SunwovenRampStartItems,
    long SunwovenRampStepItems,
    long SunwovenRampWeightMultiplier,
    long SunwovenHardCeilingItems);

/// <summary>
/// `gk-core/data/tuning/item-drop-volume.v1.json`, parsed. Pure — no file I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject."), the same shape
/// <see cref="FusionRpg.Core.Items.ItemRarityTuning"/> already established for module 7.
///
/// <para>Every number a balance pass would touch is here. <see cref="FloorMilli"/> and
/// <see cref="MaxNestingDepth"/> are the two exceptions in spirit — both are structural bounds and
/// both say so in the shipped file's own notes — but they stay in config anyway, because a structural
/// bound that is wrong is still cheaper to fix with a file save.</para>
/// </summary>
public readonly record struct DropVolumeTuning(
    int ThetaPin,
    long VolumeBaseMilli,
    long VolumeSlopeMilli,
    long FloorMilli,
    long DropChanceOnKillMilli,
    bool KillScalesWithTheta,
    LootPityTuning Pity,
    int JitterDownBelowPerMille,
    int JitterFlatBelowPerMille,
    int MaxNestingDepth,
    int LogRetentionHorizonDays)
{
    public static DropVolumeTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DropVolumeTuningRejection("item-drop-volume tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new DropVolumeTuningRejection($"item-drop-volume tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var volume = Obj(root, "volume");
            var kill = Obj(root, "kill");
            var pity = Obj(root, "pity");
            var heirloom = Obj(pity, "heirloom");
            var sunwoven = Obj(pity, "sunwoven");
            var jitter = Obj(root, "itemLevelJitter");
            var nesting = Obj(root, "nesting");
            var log = Obj(root, "log");

            var parsed = new DropVolumeTuning(
                ThetaPin: (int)Long(volume, "thetaPin"),
                VolumeBaseMilli: Long(volume, "baseMilli"),
                VolumeSlopeMilli: Long(volume, "slopeMilli"),
                FloorMilli: Long(volume, "floorMilli"),
                DropChanceOnKillMilli: Long(kill, "dropChanceOnKillMilli"),
                KillScalesWithTheta: Bool(kill, "scalesWithTheta"),
                Pity: new LootPityTuning(
                    Long(heirloom, "hardFloorItems"),
                    Long(sunwoven, "rampStartItems"),
                    Long(sunwoven, "rampStepItems"),
                    Long(sunwoven, "rampWeightMultiplier"),
                    Long(sunwoven, "hardCeilingItems")),
                JitterDownBelowPerMille: (int)Long(jitter, "downBelowPerMille"),
                JitterFlatBelowPerMille: (int)Long(jitter, "flatBelowPerMille"),
                MaxNestingDepth: (int)Long(nesting, "maxDepth"),
                LogRetentionHorizonDays: (int)Long(log, "retentionHorizonDays"));

            Validate(parsed);
            return parsed;
        }
    }

    /// <summary>
    /// Refuses a document that is self-inconsistent. Deliberately does NOT refuse a large slope or a
    /// large base — there is no upper bound on volume anywhere in this module (D26), and a validator
    /// that "sanity capped" the slope would be the cap this program forbids, wearing a different hat.
    /// </summary>
    public static void Validate(DropVolumeTuning t)
    {
        if (t.ThetaPin < 0)
            throw new DropVolumeTuningRejection($"item-drop-volume tuning: thetaPin {t.ThetaPin} is negative");
        if (t.VolumeBaseMilli <= 0)
            throw new DropVolumeTuningRejection($"item-drop-volume tuning: baseMilli {t.VolumeBaseMilli} must be positive");
        if (t.FloorMilli <= 0)
            throw new DropVolumeTuningRejection(
                $"item-drop-volume tuning: floorMilli {t.FloorMilli} must be positive — a drop source " +
                "that resolves to a zero rate is a dead source, not a balanced one");
        if (t.FloorMilli > t.VolumeBaseMilli)
            throw new DropVolumeTuningRejection(
                $"item-drop-volume tuning: floorMilli {t.FloorMilli} is above baseMilli {t.VolumeBaseMilli}, " +
                "so the floor would bind at the pin itself and the slope would be inert");
        if (t.DropChanceOnKillMilli is < 0 or > 1000)
            throw new DropVolumeTuningRejection(
                $"item-drop-volume tuning: dropChanceOnKillMilli {t.DropChanceOnKillMilli} is outside [0, 1000] — " +
                "this one IS a bounded ratio (a probability), which AGENTS.md exempts by name");
        if (t.JitterDownBelowPerMille < 0 || t.JitterFlatBelowPerMille < t.JitterDownBelowPerMille
            || t.JitterFlatBelowPerMille > 1000)
            throw new DropVolumeTuningRejection(
                "item-drop-volume tuning: itemLevelJitter thresholds must satisfy 0 <= down <= flat <= 1000");
        if (t.MaxNestingDepth < 1)
            throw new DropVolumeTuningRejection($"item-drop-volume tuning: nesting.maxDepth {t.MaxNestingDepth} must be >= 1");
        if (t.Pity.HeirloomHardFloorItems < 1 || t.Pity.SunwovenRampStartItems < 1
            || t.Pity.SunwovenHardCeilingItems < t.Pity.SunwovenRampStartItems
            || t.Pity.SunwovenRampStepItems < 1 || t.Pity.SunwovenRampWeightMultiplier < 1)
            throw new DropVolumeTuningRejection("item-drop-volume tuning: pity thresholds are inconsistent");
        if (t.LogRetentionHorizonDays < 1)
            throw new DropVolumeTuningRejection(
                $"item-drop-volume tuning: log.retentionHorizonDays {t.LogRetentionHorizonDays} must be >= 1");
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DropVolumeTuningRejection($"item-drop-volume tuning: missing or non-object '{key}'");
        return el;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new DropVolumeTuningRejection($"item-drop-volume tuning: missing or non-integer '{key}'");
        return v;
    }

    static bool Bool(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el)
            || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
            throw new DropVolumeTuningRejection($"item-drop-volume tuning: missing or non-boolean '{key}'");
        return el.GetBoolean();
    }
}
