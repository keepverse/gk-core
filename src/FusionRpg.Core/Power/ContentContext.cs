namespace FusionRpg.Core.Power;

/// <summary>
/// The content-side inputs to Θ_content (ssot-power-scale.md §5): a plain record because content has
/// no <c>StatContext</c> and forcing one would put a fake actor on every wave definition.
///
/// <para><b>RealmsAdvanced correction (spec-power-index.md §2.2, 2026-08-24):</b> the composition
/// formula (§2.1) is <c>Wz·zombossLevel + Wm·dangerBand + Ww·worldTier + Wf·realmsAdvanced</c>, but
/// the module spec's own prose lists <c>ContentContext</c> as only <c>(dangerBand, worldTier,
/// zombossLevel)</c> — three fields, not the four the formula needs. SSOT §5.1 is explicit that
/// <c>realmsAdvanced</c> must appear on the content side (weighted by <c>Wf = Wa</c>) to keep the
/// actor/content gap constant; without it, Θ_content could never even be constructed for the F2/F8
/// divergence tripwire's "500 simulated worlds" test. Per this spec's own header — "where this spec
/// and the SSOT disagree, the SSOT wins" — <see cref="RealmsAdvanced"/> is added here.</para>
/// </summary>
public sealed record ContentContext(int DangerBand, int WorldTier, int ZombossLevel, int RealmsAdvanced);
