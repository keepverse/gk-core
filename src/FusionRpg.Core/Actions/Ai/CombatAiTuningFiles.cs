namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `CAI-F1` (2026-09-23, lane `cai2`): the combat-ai tuning revision every production reader
/// loads. Bumped in the SAME commit as the publish that makes the new revision current — the filename and
/// the readers move together, so a published file with no reader and a reader pointed at the wrong file
/// are both impossible. That is the whole reason this constant exists: `CAI-F1` measured both hosts naming
/// `combat-ai.v1.json` **literally**, which made an H7 publish impossible for any combat-ai lane (the
/// publish would either have no effect or ship unread).
///
/// <para>This is the pattern `SocketTuningFiles.Current` already ships for exactly this reason, copied
/// rather than re-invented.</para>
/// </summary>
public static class CombatAiTuningFiles
{
    /// <summary>The revision the Server and the Injector both read. `combat-ai.v2.json` carries the lawn
    /// section (CAI4.7's four balance keys); its profiles and router are identical to `v1`'s, so the
    /// publish was a pure addition and no golden can move.</summary>
    public const string Current = "combat-ai.v2.json";
}
