namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `replay-identity` (module 8, CAI2.1, spec-replay-identity.md §2): the host-side seam that
/// can hand back an ALREADY-PUBLISHED profile set. Core reads no file; the Server's own implementation
/// loads every `data/tuning/combat-ai.v*.json` in the tuning directory and answers from that map
/// (`tunables-ssot.md` §7.2 — the same Core/SDK split `spec-mode-profile.md` uses for
/// `mode-profiles.v1.json`).
///
/// <para><b><see cref="ForVersion"/> returning null is a refusal, never a fallback.</b> "The host cannot
/// supply that version" must surface as `profile.unavailable:v{n}` at the replay call site — silently
/// resolving the pinned match on <see cref="Current"/> is the exact defect this module exists to close
/// (a published rebalance would rewrite a battle that already happened). Null carries that meaning; it
/// is not "use the newest".</para>
/// </summary>
public interface ICombatAiProfileSource
{
    /// <summary>The set a FRESH match resolves under, and whose stamp it records.</summary>
    CombatAiTuning Current { get; }

    /// <summary>The set a PINNED match resolves under, addressed by the tuning version its stamp
    /// recorded. Null means the host cannot supply that version — a refusal.</summary>
    CombatAiTuning? ForVersion(int tuningVersion);
}
