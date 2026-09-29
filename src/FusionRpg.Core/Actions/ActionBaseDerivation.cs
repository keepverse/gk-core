using FusionRpg.Core.Actions.Rungs;

namespace FusionRpg.Core.Actions;

/// <summary>
/// action-enrich `action-base` (spec-action-base.md §Derivation): the ONE function every attack's base
/// resolves through, at hit time — never a stored field on <see cref="CompiledAction"/>, so
/// <see cref="ActionCompiler"/>, <see cref="BasicAttackFactory"/> and <see cref="CompiledAction"/> stay
/// untouched by this module (the spec's own correction to its first draft).
///
/// <para>A <see cref="ActionKind.Skill"/>/<see cref="ActionKind.Innate"/> reads the
/// <see cref="RungRow.QPowerMilli"/> of the row at its holder's effective rung from the LOADED table —
/// never a literal, never a per-action authored base. The basic attack reads its tuning value. A skill
/// whose effective rung has no row throws, naming the action: never a basic-attack fallback.</para>
/// </summary>
public static class ActionBaseDerivation
{
    /// <summary>The move's base, in per-mille of <c>PowerMath.One</c>.</summary>
    public static long BasePowerMilli(ActionKind kind, string actionId, int effectiveRung,
        RungTable rungTable, ActionBaseTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(rungTable);
        ArgumentNullException.ThrowIfNull(tuning);

        return kind switch
        {
            ActionKind.Basic => tuning.BasicAttackBasePowerMilli,
            _ => rungTable.TryGet(effectiveRung, out var row)
                ? row.QPowerMilli
                : throw new InvalidOperationException(
                    $"action '{actionId}': effective rung {effectiveRung} has no rung row"),
        };
    }
}
