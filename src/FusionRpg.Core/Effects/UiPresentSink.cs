namespace FusionRpg.Core.Effects;

/// <summary>
/// E41 (spec-ui-attach-point.md §2b): the two present shapes <c>IDamageFxSink</c> cannot express —
/// <c>op:banner</c> (match-scoped, no target ptr) and <c>op:meter</c> (a per-actor HUD meter).
/// <c>op:number</c> deliberately does NOT go through this interface — it reuses <c>IDamageFxSink</c>
/// directly, the same merge/throttle-tested floater path <c>DamageFxDto.MergedCount</c> already gives
/// every other damage present (the 2026-08 perf audit's own discipline).
///
/// <para>Kept separate from <see cref="IEffectActionSink"/> on purpose: a <c>Ui</c>-attached kind's
/// executor never runs through the stat/resource/status/shield/board sink, by construction —
/// <c>EffectBag.FireGrant</c> handles <c>PresentUi</c> bag-side (the same shape
/// <see cref="EffectActions.GrantShield"/>/<see cref="EffectActions.ApplyResourceDelta"/> already use)
/// and it never becomes an <see cref="FusionRpg.Contracts.EffectActionPlanItem"/> that reaches
/// <c>InjectorEffectActionSink</c>. A second sink type is what makes the read-only invariant
/// structural rather than a convention nobody enforces.</para>
/// </summary>
public interface IUiPresentSink
{
    /// <summary>
    /// <c>op:meter</c> — an <c>ActorHudMeter</c> on the target's own snapshot. <paramref name="ratio"/>
    /// is already 0..1 (divided by 1000 from the atom's per-mille magnitude, exactly once, in
    /// <c>EffectBag.ExecPresentUi</c> — never re-divided downstream).
    /// </summary>
    void SetMeter(string targetPtr, string meterId, double ratio);

    /// <summary>
    /// <c>op:banner</c> — a match-scoped present, no target ptr. <paramref name="bannerId"/> is a
    /// catalog key, already refused at load if unknown (<c>AtomKindRegistry</c>'s own Vocabulary
    /// check) — never free text.
    /// </summary>
    void ShowBanner(string bannerId, int? durationMs);
}

public sealed class NoopUiPresentSink : IUiPresentSink
{
    public static readonly NoopUiPresentSink Instance = new();
    public void SetMeter(string targetPtr, string meterId, double ratio) { }
    public void ShowBanner(string bannerId, int? durationMs) { }
}

/// <summary>Test double — the "fake HUD cache" spec-ui-attach-point.md §4 asks for.</summary>
public sealed class RecordingUiPresentSink : IUiPresentSink
{
    public sealed record MeterCall(string TargetPtr, string MeterId, double Ratio);
    public sealed record BannerCall(string BannerId, int? DurationMs);

    public List<MeterCall> Meters { get; } = new();
    public List<BannerCall> Banners { get; } = new();

    public void SetMeter(string targetPtr, string meterId, double ratio) =>
        Meters.Add(new MeterCall(targetPtr, meterId, ratio));

    public void ShowBanner(string bannerId, int? durationMs) =>
        Banners.Add(new BannerCall(bannerId, durationMs));

    public void Clear()
    {
        Meters.Clear();
        Banners.Clear();
    }
}
