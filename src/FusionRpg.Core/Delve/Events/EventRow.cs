using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.1 (spec-event-deck.md §6; spec-dungeon-seed-contract.md §1.4) — one authored effect
/// bundle an outcome grants. `Family`/`PowerBand` only: the `Instantiator` container itself
/// (`container_id` DERIVED) is the importer's own job, not this module's — a def row loaded here has
/// no concrete container behind it yet, the same "shape rules run, container-kind binding does not"
/// posture `ConsumableCatalog.Load` already takes for its own def rows.
/// </summary>
public sealed record EventEffectRef(string Family, string PowerBand);

/// <summary>One of an event's 2-4 outcomes (seed-contract §1.4). <c>Consequence</c> is event-deck's
/// own vocabulary (`none · loot · encounter · scout`), not read from any registry — this module is
/// the one place it is authored and validated.</summary>
public sealed record EventOutcomeRow(
    string Ordinal, string DropBand, string Consequence, IReadOnlyList<EventEffectRef> Effects);

/// <summary>
/// One event anchor (`events/&lt;id&gt;.json`, seed-contract §1.4). <c>Eligibility</c> is the raw,
/// already-parsed tree (<see cref="PredicateNode"/>) — `null` means "always eligible" (the seed
/// contract's own `none` = always eligible), never a sentinel string. `Theme`/`ClimateAffinity` are
/// carried but not validated here: theme is a planner-fixed motif subset this module has no catalog
/// for (matching `creature-seed`'s own still-unaudited motif system), and climate affinity weights a
/// draw without ever gating one (seed contract: "not an eligibility rule").
/// </summary>
public sealed record EventRow(
    string EventId,
    string Kind,
    string? Theme,
    string? ClimateAffinity,
    string RepeatScope,
    PredicateNode? Eligibility,
    IReadOnlyList<EventOutcomeRow> Outcomes,
    string? SupplyOverride,
    string? ChainRef);
