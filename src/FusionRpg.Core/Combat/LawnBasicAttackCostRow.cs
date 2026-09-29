using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Combat;

/// <summary>
/// `lawn-combat-wire` T12 (spec-basic-attack-cost.md, wire 1): the lawn's own `stamina` cost row for
/// `act.attack` — never SQLite-delivered (the injector has no `FusionRpg.Data` reference and no Server
/// round trip on this path) and never a hardcoded C# number (that would reintroduce the exact
/// balance-in-code defect `tunables-ssot.md` bans). Instead this reads the SAME shipped
/// `action-corpus-cost-templates.v{n}.json` `ActionCorpusComposer.Compose` already builds
/// <see cref="ActionCostRow"/> rows from (`ActionCorpusComposer.cs:165-168`), applying the kind-aware
/// `Basic -> stamina` rule (T7/D5) — one construction site, mirrored here rather than duplicated,
/// because the arithmetic is a pure function of the parsed template row.
/// </summary>
public static class LawnBasicAttackCostRow
{
    /// <summary>The hand-built lawn basic-attack action id (`BasicAttackFactory.cs`, T8) —
    /// `BattleEngine.BasicAttackEnvelope.ActionId` is the same string; duplicated here as a `const`
    /// only because this file must not reference `FusionRpg.Core.Battle` (the dependency direction
    /// runs the other way — Battle already depends on Actions/Combat, not vice versa).</summary>
    public const string ActionId = "act.attack";

    /// <summary>
    /// Builds the ONE-entry cost table <see cref="CostLedger"/> reads for the lawn: `act.attack`
    /// costs whatever <see cref="ActionCorpusCostTemplate.ResolveFor"/> resolves for
    /// <see cref="ActionKind.Basic"/>/<see cref="ActionCategory.Attack"/> — `stamina`,
    /// `baseAmountAtRung1` from the real shipped tuning, at whatever timing that tuning authors
    /// (`onCommit` today; <see cref="CostLedger.Check"/>/<see cref="CostLedger.TryPay"/> only ever
    /// look at <see cref="ActionCostTiming.OnCommit"/> rows for this action — see the lawn charge
    /// site — so a future retune to `perTick` would need its OWN charge-site change, not a silent
    /// behavior change here).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> Build(
        ActionCorpusCostTemplate template)
    {
        var row = template.ResolveFor(ActionKind.Basic, ActionCategory.Attack);
        return new Dictionary<string, IReadOnlyList<ActionCostRow>>(StringComparer.Ordinal)
        {
            [ActionId] = new[]
            {
                new ActionCostRow(ActionId, row.ResourceId, ValueSpec.Of(row.BaseAmountAtRung1), row.Timing)
            }
        };
    }
}
