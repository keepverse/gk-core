namespace FusionRpg.Core.Vfx;

/// <summary>
/// Static visual signature for one custom status — used by identity audits and collision tests.
/// Compares motion grammar, tint, marker shape, and RGB channels separately.
/// </summary>
public sealed record StatusVfxSignature(
    string StatusId,
    (byte R, byte G, byte B) ApplyRgb,
    string ApplyBurstKey,
    VfxAuraStyle? AuraStyle,
    (byte R, byte G, byte B)? AuraRgb,
    float TintStrength,
    (byte R, byte G, byte B)? TintRgb,
    VfxMarkerShape? MarkerShape,
    (byte R, byte G, byte B)? MarkerRgb)
{
    /// <summary>Motion + marker + tint strength — RGB excluded.</summary>
    public string StructuralKey =>
        $"{AuraStyle?.ToString() ?? "none"}|tint={TintStrength:F2}|marker={MarkerShape?.ToString() ?? "none"}";

    /// <summary>Aura motion family only — used to find color-reskin clusters.</summary>
    public string MotionGrammarKey =>
        AuraStyle?.ToString() ?? "none";

    /// <summary>Motion + marker shape — react-to badges break orbit/pulse clusters.</summary>
    public string GrammarKey =>
        $"{AuraStyle?.ToString() ?? "none"}|marker={MarkerShape?.ToString() ?? "none"}";

    /// <summary>Full key including all RGB tuples.</summary>
    public string FullKey =>
        StructuralKey +
        $"|apply={ApplyRgb.R},{ApplyRgb.G},{ApplyRgb.B}" +
        $"|aura={Fmt(AuraRgb)}|tintRgb={Fmt(TintRgb)}|markerRgb={Fmt(MarkerRgb)}";

    static string Fmt((byte R, byte G, byte B)? rgb) =>
        rgb is { } c ? $"{c.R},{c.G},{c.B}" : "none";
}

/// <summary>Pure helpers for status VFX identity audits (vfx-v3 distinguishability).</summary>
public static class StatusVfxIdentity
{
    static readonly string[] CustomStatusIds =
    {
        "wither", "blight", "rot", "spark", "spore", "pact_mark", "leech",
        "expose", "shatter", "bond", "rally", "command", "charm_pulse"
    };

    public static IReadOnlyList<string> CustomIds => CustomStatusIds;

    /// <summary>Default apply burst template — engine-wrapped statuses use this.</summary>
    public const string DefaultApplyBurstKey = "Radial|count=14|life=0.45|scale=1.00";

    /// <summary>Apply burst template key — shape, count, life, size (batch overrides included).</summary>
    public static string FormatApplyBurstKey(VfxPrimitiveSpec burst) =>
        $"{burst.Shape}|count={burst.Count}|life={burst.LifeSeconds:F2}|scale={burst.SizeScale:F2}";

    static readonly Lazy<IReadOnlyDictionary<string, VfxPrimitiveSpec>> ApplyBurstByStatus =
        new(BuildApplyBurstMap);

    static IReadOnlyDictionary<string, VfxPrimitiveSpec> BuildApplyBurstMap()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());
        var map = new Dictionary<string, VfxPrimitiveSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in CustomStatusIds)
        {
            if (!catalog.TryGet(StatusVfxCues.CueId(id), out var recipe)) continue;
            var burst = recipe.Primitives.FirstOrDefault(p => p.Kind == VfxPrimitiveKind.Burst);
            if (burst != null) map[id] = burst;
        }

        return map;
    }

    public static StatusVfxSignature Signature(string statusId)
    {
        var apply = VfxSeedCatalog.StatusFx.FirstOrDefault(
            x => string.Equals(x.Id, statusId, StringComparison.OrdinalIgnoreCase));
        if (apply.Id == null)
            throw new ArgumentException("Unknown status id: " + statusId, nameof(statusId));

        if (!ApplyBurstByStatus.Value.TryGetValue(statusId, out var burst))
            throw new InvalidOperationException("No apply burst in catalog for status: " + statusId);

        var sustain = VfxSeedCatalog.StatusSustainFx.FirstOrDefault(
            s => string.Equals(s.Id, statusId, StringComparison.OrdinalIgnoreCase));

        (byte R, byte G, byte B)? auraRgb = sustain?.Aura.HasValue == true
            ? (sustain.AR, sustain.AG, sustain.AB)
            : null;
        (byte R, byte G, byte B)? tintRgb = sustain is { Tint: > 0f }
            ? (sustain.TR, sustain.TG, sustain.TB)
            : null;
        (byte R, byte G, byte B)? markerRgb = sustain?.Marker.HasValue == true
            ? (sustain.MR, sustain.MG, sustain.MB)
            : null;

        return new StatusVfxSignature(
            statusId,
            (apply.R, apply.G, apply.B),
            FormatApplyBurstKey(burst),
            sustain?.Aura,
            auraRgb,
            sustain?.Tint ?? 0f,
            tintRgb,
            sustain?.Marker,
            markerRgb);
    }

    public static IReadOnlyList<StatusVfxSignature> AllCustomSignatures() =>
        CustomStatusIds.Select(Signature).ToList();

    public static int RgbDistance((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    public sealed record CollisionPair(
        string A,
        string B,
        string Kind,
        string Detail);

    /// <summary>
    /// Returns exact duplicates, structural color-only pairs, motion-grammar clusters, and near apply colors.
    /// </summary>
    public static IReadOnlyList<CollisionPair> FindCollisions()
    {
        var sigs = AllCustomSignatures();
        var pairs = new List<CollisionPair>();

        for (var i = 0; i < sigs.Count; i++)
        {
            for (var j = i + 1; j < sigs.Count; j++)
            {
                var a = sigs[i];
                var b = sigs[j];

                if (a.FullKey == b.FullKey)
                {
                    pairs.Add(new CollisionPair(a.StatusId, b.StatusId, "exact-duplicate", a.FullKey));
                    continue;
                }

                if (a.StructuralKey == b.StructuralKey)
                {
                    pairs.Add(new CollisionPair(
                        a.StatusId, b.StatusId, "structural-color-only",
                        $"structural={a.StructuralKey}; applyΔ={RgbDistance(a.ApplyRgb, b.ApplyRgb)}"));
                }

                if (a.MotionGrammarKey == b.MotionGrammarKey &&
                    !string.Equals(a.MotionGrammarKey, "none", StringComparison.Ordinal))
                {
                    pairs.Add(new CollisionPair(
                        a.StatusId, b.StatusId, "same-motion-grammar",
                        $"grammar={a.MotionGrammarKey}; markerA={a.MarkerShape?.ToString() ?? "none"} markerB={b.MarkerShape?.ToString() ?? "none"}; applyΔ={RgbDistance(a.ApplyRgb, b.ApplyRgb)}"));
                }

                var applyDist = RgbDistance(a.ApplyRgb, b.ApplyRgb);
                if (applyDist <= VfxTuningHub.Tuning.Identity.SimilarApplyRgbDistanceThreshold
                    && a.StructuralKey != b.StructuralKey
                    && string.Equals(a.ApplyBurstKey, b.ApplyBurstKey, StringComparison.Ordinal))
                {
                    pairs.Add(new CollisionPair(
                        a.StatusId, b.StatusId, "similar-apply-color",
                        $"applyΔ={applyDist}; burst={a.ApplyBurstKey}; applyA=#{a.ApplyRgb.R:X2}{a.ApplyRgb.G:X2}{a.ApplyRgb.B:X2} applyB=#{b.ApplyRgb.R:X2}{b.ApplyRgb.G:X2}{b.ApplyRgb.B:X2}"));
                }

                if (a.GrammarKey == b.GrammarKey &&
                    a.MarkerShape.HasValue &&
                    a.AuraStyle == b.AuraStyle)
                {
                    pairs.Add(new CollisionPair(
                        a.StatusId, b.StatusId, "same-aura-and-marker-shape",
                        $"grammar={a.GrammarKey}"));
                }
            }
        }

        return pairs;
    }

    public static IReadOnlyList<CollisionPair> MotionGrammarPairs() =>
        FindCollisions()
            .Where(p => p.Kind == "same-motion-grammar")
            .ToList();

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ClusterByAuraStyle()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sig in AllCustomSignatures())
        {
            var key = sig.AuraStyle?.ToString() ?? "none";
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<string>();
                map[key] = list;
            }

            list.Add(sig.StatusId);
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }
}
