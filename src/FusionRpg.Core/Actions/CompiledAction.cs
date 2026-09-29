using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>One cost row after the rung's curve-scale has already been applied to its bounds
/// (spec-action-catalog.md §2: "ValueSpec → curve-scaled bounds").</summary>
public sealed record CompiledActionCost(string ResourceId, ValueSpec Amount, ActionCostTiming When);

/// <summary>
/// T30 (spec-action-catalog.md §2): the runtime form the battle engine actually uses — everything an
/// <see cref="ActionRow"/> carries as raw JSON or an uncompiled spec, replaced by its compiled shape.
/// <b>Nothing here is re-derived per resolve</b>: this record is built once at load
/// (<see cref="ActionCompiler.Compile"/>) and cached (<see cref="ActionCatalog"/>) — "the battle path
/// meets no JSON, no dictionary, and no string comparison" (§ Success criteria).
/// </summary>
/// <param name="Category">A-M1 (spec-movement-payload.md §2, "⛔ CORRECTED 2026-09-03"): threaded
/// straight from <see cref="ActionRow.Category"/> — trailing, defaulted, purely additive, matching
/// <see cref="ActionCostRow.AllowLethal"/>'s own precedent for widening this record without moving its
/// existing 16 positional call sites. <c>null</c> for an action the corpus has not categorized, same
/// meaning as on the row.</param>
/// <param name="StockDemands">The <c>holdsStock</c> rows this action's condition requires the actor to
/// hold, lifted out of the predicate tree at compile time — see <see cref="Cost.StockDemand"/> for why
/// the compiled predicate cannot answer this itself (it interns the <c>stockId</c> away to a slot
/// index). <c>null</c>, the default and the case for every action authoring no <c>holdsStock</c> leaf,
/// means the same as empty. Trailing and defaulted, matching <paramref name="Category"/>'s own
/// precedent for widening this record without moving its existing positional call sites.</param>
/// <summary>ST2 (spec-holder-rung-pricing.md contract 4): the authored rung window this action was
/// composed for, copied from <see cref="ActionRow.RungBand"/> by <see cref="ActionCompiler"/>.
/// <c>null</c> for an action authored before A-E1 — unbounded, byte-identical to today.</summary>
public sealed record CompiledAction(
    string ActionId,
    ActionKind Kind,
    int Rung,
    IReadOnlyList<ActionTag> Tags,
    bool Enabled,
    long Revision,
    bool Grantable,
    bool DefaultAttackEligible,
    string ContainerId,
    ActionEnvelope Envelope,
    CompiledTargetSpec Targeting,
    int MinRange,
    int MaxRange,
    string? RangeChannel,
    bool RequiresLineOfSight,
    ICompiledPredicate Condition,
    IReadOnlyList<CompiledActionCost> Costs,
    IReadOnlyList<ActionScopeRow> Scopes,
    RungBand? RungBand = null,
    ActionCategory? Category = null,
    ProjectilePenalties ProjectilePenalties = ProjectilePenalties.All,
    IReadOnlyList<Cost.StockDemand>? StockDemands = null);
