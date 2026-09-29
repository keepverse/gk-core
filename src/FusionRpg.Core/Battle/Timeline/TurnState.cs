namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// Per-actor turn states. Identical in every mode — a profile changes how much simulated time is
/// spent in each, never which exist or how they connect.
///
/// Note what is <b>absent</b>: there is no <c>Incapacitated</c> state. CC is a <i>derived read</i>
/// from StatusRuntime at the seat check, never stored here — its lifetime is mutated through
/// stacking, family-mutex displacement, grant clearing, entity withdrawal, and expiry pruning,
/// none of which this machine can observe. A cached remainder would go stale on all five, and the
/// expiry boundary (<c>prune on ExpiresAt &lt; now</c> vs <c>lock on ExpiresAt &gt;= now</c>) means a
/// remainder counting to zero would resume a round early.
/// </summary>
public enum TurnState
{
    Charging,
    Ready,
    Committed,
    Resolving,
    Recovering,
    /// <summary>HP ≤ 0 but still present, targetable, and revivable. Death is a decision, not an edge.</summary>
    Downed,
    Dead,
    Withdrawn
}

/// <summary>
/// The legal transition table, declared once so a test can read it.
///
/// <c>Downed</c> is why this exists. Without it, HP ≤ 0 transitions straight to a terminal
/// <c>Dead</c>, and an immortal's revive then attempts <c>Dead → Charging</c> — which is not legal,
/// which throws. That is a crash at the most expensive checkpoint in the program, so the state and
/// its table entry land before the engine is touched.
/// </summary>
public static class TurnTransitions
{
    static readonly (TurnState From, TurnState To)[] Legal =
    {
        // the action cycle
        (TurnState.Charging, TurnState.Ready),
        (TurnState.Ready, TurnState.Committed),
        (TurnState.Committed, TurnState.Resolving),
        (TurnState.Resolving, TurnState.Recovering),
        (TurnState.Recovering, TurnState.Charging),

        // interrupt: a committed action broken before it resolves
        (TurnState.Committed, TurnState.Charging),

        // no legal intent / passed turn — back to charging without taking a slot
        (TurnState.Ready, TurnState.Charging),

        // HP <= 0 from any live state
        (TurnState.Charging, TurnState.Downed),
        (TurnState.Ready, TurnState.Downed),
        (TurnState.Committed, TurnState.Downed),
        (TurnState.Resolving, TurnState.Downed),
        (TurnState.Recovering, TurnState.Downed),

        // death resolution is veto-capable: revive, or actually die
        (TurnState.Downed, TurnState.Charging),
        (TurnState.Downed, TurnState.Dead),

        // Retreat is checked after every dispatch, so it fires from any live state — INCLUDING
        // Resolving. The engine runs its retreat check inside the dispatch loop, so a coward
        // crossing its HP threshold on the very hit it is delivering withdraws mid-resolution.
        // An earlier table omitted Resolving while the comment claimed "any live state"; since
        // illegal transitions throw, that omission was the same latent crash Downed was added to
        // prevent, and the meta-test mirrored the omission rather than catching it.
        (TurnState.Charging, TurnState.Withdrawn),
        (TurnState.Ready, TurnState.Withdrawn),
        (TurnState.Committed, TurnState.Withdrawn),
        (TurnState.Resolving, TurnState.Withdrawn),
        (TurnState.Recovering, TurnState.Withdrawn),
        (TurnState.Downed, TurnState.Withdrawn)
    };

    /// <summary>
    /// A genuinely read-only view. An <c>IReadOnlyList&lt;T&gt;</c> over a bare <c>T[]</c> casts straight
    /// back, and because <see cref="Legal"/> is <c>static readonly</c>, one such cast would rewrite
    /// the legal-transition table for every actor, every battle, and every later test in the
    /// process — resurrecting the dead or outlawing the action cycle.
    /// </summary>
    static readonly IReadOnlyList<(TurnState From, TurnState To)> ReadOnlyLegal = Array.AsReadOnly(Legal);

    public static IReadOnlyList<(TurnState From, TurnState To)> All => ReadOnlyLegal;

    public static bool IsLegal(TurnState from, TurnState to)
    {
        foreach (var t in Legal)
            if (t.From == from && t.To == to)
                return true;
        return false;
    }

    public static bool IsTerminal(TurnState s) => s is TurnState.Dead or TurnState.Withdrawn;
}
