namespace FusionRpg.Core.Actions;

/// <summary>
/// action-plan.md §1.2: "`P0.2`–`P0.5` belong to two other programs; this program supplies the
/// requirement and the tests." `P0.3` and `P0.5` already carry this pattern
/// (<see cref="Rungs.RungMonotonicity.PredicatePricingLanded"/>, <c>DurationResolverTests</c>' fixture
/// resolver) — this file is the same pattern for the two prerequisites that had nothing recording
/// them: <b>false today, for both</b>, verified by search rather than assumed absent.
///
/// <para>A landed flag is deliberately a `const bool`, not a runtime check: flipping it is a reviewed
/// code change made by whoever lands the real feature, at the same time as making the tests below
/// stop merely observing absence and start requiring the real behavior.</para>
/// </summary>
public static class CrossProgramLandedFlags
{
    /// <summary>`P0.2` (linkage, effect-atom program): a magnitude source that reads
    /// <c>EffectEventDto.Damage</c> (GAS's <c>SetByCaller</c> shape). Landed 2026-08-28 (design
    /// decision recorded in spec-value-spec-and-curve.md, "Event-linked magnitudes") under the same
    /// explicit cross-program authorization as `P0.3`–`P0.5` — <c>ValueSpec.EventField</c>/
    /// <c>MultiplierMilli</c>, the `{"eventField":"damage","multiplierMilli":...}` JSON grammar,
    /// `AtomCompiler.ResolvedParams`' marker bake (an event-linked spec cannot resolve to a literal at
    /// compile time — no event exists yet), and `DamagePacketBuilder.FromOverlay`'s fire-time
    /// resolution from the real firing event, proven end to end through a lifesteal-chain test against
    /// the actual <c>EffectBag</c> runtime. Scoped to GAS's `SetByCaller` shape only ("Ask 1, the small
    /// one" per the owner's own two-ask split) and to the <c>resource.delta</c> kind — GAS's
    /// `AttributeBased` shape ("10% of the target's max HP") is Ask 2, explicitly NOT built.</summary>
    public const bool LinkageLanded = true;

    /// <summary>`P0.4` (effect-atom program): the <c>holdsStock</c> predicate leaf plus a readonly
    /// <c>FactReader</c> stock probe. Landed 2026-08-28 (approved 2026-08-27,
    /// spec-predicate-tree.md) under explicit owner authorization to build across the program
    /// boundary — <see cref="LeafId.HoldsStock"/>, the four-slot interned stock probe on
    /// <c>EntityFacts</c>/<c>FactReader</c>, both compiled forms (the typed-graph reference and the
    /// shipped flat encoding), and the JSON grammar all real and fuzz-proven equivalent. T10's own
    /// mode-matrix wiring (<c>ActionBindMode</c>, <c>ActionCompiler</c> refusing a consumable action
    /// in lawn mode) is built too — see action-todo.md's T10 entry for full evidence. The underlying
    /// INVENTORY SYSTEM (`rpg_item_stock`) remains unbuilt, by design: the leaf reads from
    /// caller-supplied quantities, exactly like `IAffordabilityCheck` stands in for the cost ledger
    /// elsewhere in this program.</summary>
    public const bool HoldsStockLanded = true;

    /// <summary>`P0.5` (battle-timeline program): <c>turn.speed</c>/<c>turn.haste</c> registered in
    /// <c>DerivedStatRegistry</c> with a reader (<c>TurnReadiness.cs</c>'s pure readiness function),
    /// and T29's real, `turn.speed`-backed <c>BattleDurationResolver</c> built on it. Landed
    /// 2026-08-28 under explicit owner authorization to build across the program boundary — the
    /// FULL B9 slice (scheduling a live <c>Readiness</c> timeline event, wiring
    /// <c>Charging → Ready</c> in <c>ActionRunner</c>) is NOT included: that is a kernel-FSM change
    /// with its own "zero production code rewired" acceptance bar
    /// (battle-timeline-todo.md Checkpoint A) this session did not touch. This flag tracks P0.5's own
    /// narrow definition ("turn.speed registered with a reader, and readiness computed") — true —
    /// not B9's full scope.</summary>
    public const bool TurnSpeedLanded = true;

    /// <summary>A3 (party-dungeon `supplies-and-objects`, spec-supplies-and-objects.md §3): an
    /// item-shaped cost row on actions, the seam a `useContext: battle` supply needs (its
    /// <c>GrantsActionId</c> names a corpus action whose cost IS the item, "external and gating for
    /// battle use only" — rest and curio uses never wait on this, their own cost is the pack decrement
    /// `loot-pack` already owns). Verified false by reading the row itself, not assumed:
    /// <see cref="ActionCostRow"/> is `(ActionId, ResourceId, AmountSpec, When, AllowLethal)` —
    /// <c>ResourceId</c> is one of the six resource-hub ids only (`ActionRow.cs:122-123`), with no way
    /// to name an item id as a cost. <b>False today</b> — flip it, and the tests that gate on it,
    /// together, the day a real item-cost row lands.</summary>
    public const bool ItemCostRowLanded = false;
}
