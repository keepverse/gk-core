namespace FusionRpg.Core.Battle.Timeline;

/// <summary>What happened when a reaction tried to enter the lane.</summary>
public enum ReactionOutcome
{
    /// <summary>Slot acquired, depth incremented. Caller must call <see cref="ReactionLane.Exit"/>.</summary>
    Entered,
    /// <summary><c>WReact</c> is 0 — this profile has no reaction lane at all.</summary>
    NoLane,
    /// <summary>The nested-resolution stack is already at <see cref="ReactionLane.DepthLimit"/>.</summary>
    DepthExceeded,
    /// <summary><c>WReact</c> is exhausted by other reactions in flight.</summary>
    NoSlot
}

/// <summary>
/// T2d — the reaction lane (spec-turn-fsm.md "The published pending resolve, and the reaction
/// lane"). A defender mid-<see cref="TurnState.Recovering"/> — or in any live state — can still
/// react to a triggering resolution: block, parry, counter. The reaction never moves the reactor's
/// own <see cref="ActorTurnMachine"/>; it resolves <i>inside</i> the triggering resolution, so
/// <c>Resolving</c> stays atomic with respect to the clock while ceasing to be atomic with respect
/// to this stack. This class owns only the mechanism — the slot pool and the nesting bookkeeping —
/// never what a reaction does, matching the rest of the kernel's boundary.
///
/// <para><b><c>WReact</c> is a separate pool from <see cref="ActionSlots"/>'s <c>W</c></b> — a
/// defender blocking must not contend with the attacker's own turn for the same width, or blocking
/// degrades to "whoever happens to be idle." Composed from <see cref="ActionSlots"/> rather than
/// reimplementing its deterministic `(readyTick, seq)` contention ordering.</para>
///
/// <para><b>Depth is structural, not a balance tunable</b> — tunables-ssot.md §1 lists "recursion
/// depth" under Structural explicitly, alongside buffer sizes and hash depth: it exists to bound a
/// nested-resolution stack from recursing without limit (a counter-riposte triggering a
/// counter-riposte...), not to express how the game feels. Exceeding it drops the reaction and
/// emits telemetry — it never recurses, following the same shape
/// <c>CombatDamageDispatcher</c>'s <c>ChainDepth</c>/<c>ProcDepthLimit</c> check already uses for
/// proc-triggering-proc chains.</para>
///
/// <para><b><c>WReact = 0</c> is byte-identical to no lane at all.</b> The constructor builds no
/// backing <see cref="ActionSlots"/> when <c>wReact</c> is zero, so <see cref="TryEnter"/> always
/// refuses with <see cref="ReactionOutcome.NoLane"/> — a profile that never sets <c>WReact</c> (every
/// profile shipped today) behaves exactly as if this type did not exist.</para>
/// </summary>
public sealed class ReactionLane
{
    /// <summary>
    /// Bounds the nested-resolution stack. Structural (tunables-ssot.md §1 — "recursion depth" is
    /// named there as a Structural example), not a balance dial: it exists so a reaction chain
    /// cannot recurse without limit, not to tune how battles feel. `3` covers the deepest shape this
    /// game has named so far — a hit, a block, and a riposte to the block — with one level of
    /// headroom before a chain is dropped rather than tuned.
    /// </summary>
    public const int DepthLimit = 3;

    readonly ActionSlots? _slots;

    public ReactionLane(int wReact, WScope scope = WScope.Global)
    {
        if (wReact < 0) throw new ArgumentOutOfRangeException(nameof(wReact), "WReact cannot be negative.");
        WReact = wReact;
        _slots = wReact > 0 ? new ActionSlots(wReact, scope) : null;
    }

    public int WReact { get; }

    /// <summary>Current nesting depth. Zero when no reaction is in flight anywhere in the battle.</summary>
    public int Depth { get; private set; }

    /// <summary>
    /// Attempts to enter one nested reaction level for <paramref name="actorKey"/>. On
    /// <see cref="ReactionOutcome.Entered"/> the caller MUST call <see cref="Exit"/> exactly once —
    /// including on the reaction's own failure path — the same release discipline
    /// <see cref="ActionRunner"/> already applies to its own slot.
    /// </summary>
    public ReactionOutcome TryEnter(string actorKey, string sideId, BattleTrace? trace = null)
    {
        if (string.IsNullOrWhiteSpace(actorKey)) throw new ArgumentException("actorKey is required", nameof(actorKey));

        if (_slots is null)
        {
            trace?.Reaction(actorKey, "no-lane");
            return ReactionOutcome.NoLane;
        }

        // Checked before acquiring: a slot taken and then refused on depth would leak exactly like
        // a throw between TryAcquire and the transition it guards elsewhere in this module.
        if (Depth >= DepthLimit)
        {
            trace?.Reaction(actorKey, "depth-exceeded");
            return ReactionOutcome.DepthExceeded;
        }

        if (!_slots.TryAcquire(actorKey, sideId))
        {
            trace?.Reaction(actorKey, "no-slot");
            return ReactionOutcome.NoSlot;
        }

        Depth++;
        trace?.Reaction(actorKey, "entered");
        return ReactionOutcome.Entered;
    }

    /// <summary>
    /// Releases the slot and pops one nesting level. Idempotent per acquire — a caller that never
    /// received <see cref="ReactionOutcome.Entered"/> must not call this, and calling it twice for
    /// the same entry only unwinds once because <see cref="ActionSlots.Release"/> itself is
    /// idempotent and reports whether it actually released anything.
    /// </summary>
    public void Exit(string actorKey)
    {
        if (_slots is null) return;
        if (!_slots.Release(actorKey)) return;
        if (Depth > 0) Depth--;
    }
}
