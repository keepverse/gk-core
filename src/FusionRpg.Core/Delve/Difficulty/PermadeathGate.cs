using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Difficulty;

/// <summary>
/// Which rungs are permadeath (spec-difficulty-ladder.md §4, decision 1). A domain seed may RAISE
/// the gate above the tuning default (`domain.permadeathFromRung`) — never lower it; absence means
/// the difficulty default, a fallback to another tunable, never a built-in number. What permadeath
/// DOES (`downedOnce`, Retired at extraction, the wipe rule) is `delve-attrition`'s — this module
/// only answers "does this rung qualify".
/// </summary>
public static class PermadeathGate
{
    public static bool Applies(DungeonTuning dungeon, DomainThetaInputs domain, string rungId)
    {
        var effectiveGate = domain.PermadeathFromRungOverride ?? dungeon.Domain.PermadeathFromRung;
        return RungTable.OrdinalOf(rungId) >= RungTable.OrdinalOf(effectiveGate);
    }

    /// <summary>
    /// `solid-remediation` T4.9 — the delve's own <see cref="FusionRpg.Core.Battle.Attrition.IPermadeathLadder"/>.
    /// The engine asks "does permanent death apply"; this answers with the delve's rung/domain inputs
    /// already resolved, so the engine never learns `difficulty-ladder`'s vocabulary.
    /// </summary>
    public sealed class Ladder : FusionRpg.Core.Battle.Attrition.IPermadeathLadder
    {
        readonly DungeonTuning _dungeon;
        readonly DomainThetaInputs _domain;
        readonly string _rungId;

        public Ladder(DungeonTuning dungeon, DomainThetaInputs domain, string rungId)
        {
            _dungeon = dungeon ?? throw new ArgumentNullException(nameof(dungeon));
            _domain = domain;
            _rungId = rungId ?? throw new ArgumentNullException(nameof(rungId));
        }

        public bool PermadeathApplies() => Applies(_dungeon, _domain, _rungId);
    }

    /// <summary>A domain override must never sit BELOW the tuning default — a domain only makes
    /// itself harder (§1.1's own field note on `permadeathFromRung`).</summary>
    public static void ValidateOverride(DungeonTuning dungeon, string? overrideRungId)
    {
        if (overrideRungId is null) return;
        if (RungTable.OrdinalOf(overrideRungId) < RungTable.OrdinalOf(dungeon.Domain.PermadeathFromRung))
            throw new InvalidOperationException(
                $"domain.permadeathFromRung override '{overrideRungId}' sits below the tuning default " +
                $"'{dungeon.Domain.PermadeathFromRung}' — a domain may only raise the gate.");
    }
}
