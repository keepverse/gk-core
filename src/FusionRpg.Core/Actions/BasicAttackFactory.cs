using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// `lawn-combat-wire` T8 (spec-lawn-action-bridge.md): the ONE public construction site for the
/// hand-built basic-attack row. `act.attack` has no rung, no container and no atoms — the comment at
/// <c>BattleRunState.cs</c> (where this construction lived before this extraction) explains why it is
/// never run through <see cref="ActionCompiler.Compile"/>: "forcing it through the real-content
/// compiler would mean inventing fake rung/container rows for something that fundamentally has
/// neither." <see cref="TargetSpecCompiler.Compile"/> and <see cref="PredicateCompiler.Always"/> are
/// still the REAL compiler pieces, reused rather than re-guessed, for the two fields that have one.
///
/// <para><b>One factory, two callers.</b> <see cref="Battle.BattleEngine"/>'s <c>BattleRunState</c>
/// (the turn-based Battle system) and the injector's own lawn-side construction both call
/// <em>this</em> method rather than each hand-building the row — that is the whole point of the
/// extraction (spec: "Extracting the factory must leave `BattleRunState` calling it — if the injector
/// gets its own copy, the two drift and that is the dual-compose defect this repo has already
/// overturned once"). A source-scan test
/// (<c>BasicAttackFactoryConstructionSiteTests</c>) proves no second construction site exists.</para>
///
/// <para><b>No transport, no I/O.</b> Every value below is either a compile-time literal or a pure
/// function over another compile-time literal (<see cref="Battle.BattleEngine.BasicAttackEnvelope"/>,
/// <see cref="Battle.BattleEngine.BasicAttackTargeting"/>) and the caller-supplied
/// <see cref="ActionTimingTuning"/> — no HTTP, no SignalR, no SQLite, asserted by
/// <c>BasicAttackFactoryConstructionSiteTests</c>.</para>
/// </summary>
public static class BasicAttackFactory
{
    /// <summary>
    /// Builds today's basic-attack <see cref="CompiledAction"/> row, byte-identical in every field to
    /// the pre-extraction inline construction (field-level golden:
    /// <c>BasicAttackFactoryGoldenTests</c>). <paramref name="timing"/> is passed explicitly, not read
    /// from inside — the same "configured once at host startup, read explicitly wherever the timing
    /// derivation runs" idiom <see cref="ActionTimingPolicy"/> already documents for
    /// <c>RpgStore.BuildActionCatalog</c>.
    /// </summary>
    public static CompiledAction Create(ActionTimingTuning timing) => new(
        ActionId: BattleEngine.BasicAttackEnvelope.ActionId,
        Kind: ActionKind.Basic,
        Rung: 0,
        Tags: new[] { ActionTag.Offensive },
        Enabled: true,
        Revision: 0,
        Grantable: false,
        DefaultAttackEligible: true,
        ContainerId: "",
        Envelope: ActionTimingDerivation.DeriveBasicAttack(BattleEngine.BasicAttackEnvelope, timing),
        Targeting: TargetSpecCompiler.Compile(BattleEngine.BasicAttackTargeting),
        MinRange: 0,
        MaxRange: int.MaxValue,
        RangeChannel: null,
        RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());
}
