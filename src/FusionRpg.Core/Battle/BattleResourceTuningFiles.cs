namespace FusionRpg.Core.Battle;

/// <summary>
/// The <c>battle-resources</c> revision every production reader loads. Bumped in the SAME commit as
/// the publish that makes the new revision current — the filename and the readers move together, so a
/// published file with no reader and a reader pointed at the wrong file are both impossible. That is
/// the whole reason this constant exists, and it is the shape
/// <see cref="FusionRpg.Core.Actions.Ai.CombatAiTuningFiles"/> and
/// <see cref="FusionRpg.Core.Items.Sockets.SocketTuningFiles"/> already ship: <c>CAI-F1</c> measured
/// both hosts naming <c>combat-ai.v1.json</c> <b>literally</b>, which made an H7 publish impossible for
/// any combat-ai lane, because the publish would either have had no effect or would have shipped
/// unread. The same class of defect is what
/// <c>TuningVersionAgreementGuardTests</c> records for <c>element-catalog</c>: a publish that moves one
/// reader and not the other reaches some paths and not others.
///
/// <para>Without this constant every reader of this domain had to name its own revision literal, and
/// <c>battle-resources.v2.json</c> was named in four of them. Publishing <c>v3</c> therefore could not
/// have reached the game, or the gate that guards it, without a second change nobody would have
/// remembered to make.</para>
/// </summary>
public static class BattleResourceTuningFiles
{
    /// <summary>
    /// <c>battle-resources.v3.json</c> — the sustainable-fire band retune of 2026-10-05.
    /// <c>regenPerSecondShareMilli.stamina</c> 50 → 321 so the flat per-swing cost is sustainable across
    /// the whole reachable band rather than from the power pin up; <c>poolShareMilli</c> is unchanged
    /// for every resource, so pool size, burst depth and the other four resources' deliberate zero regen
    /// rows are exactly as <c>v2</c> shipped them. The file's own <c>_meta.regenDerivation</c> carries the
    /// arithmetic. <c>v1</c> (no regen block at all) and <c>v2</c> stay on disk for revert, and the
    /// readers that mean an OLD revision as history — the <c>V1StillParsesWithNoRegenBlock</c> case —
    /// keep naming it literally on purpose.
    /// </summary>
    public const string Current = "battle-resources.v3.json";
}