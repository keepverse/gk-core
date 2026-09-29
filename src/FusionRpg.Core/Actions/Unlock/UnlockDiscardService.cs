namespace FusionRpg.Core.Actions.Unlock;

/// <summary>
/// T20 (spec-unlock-ladder.md §3): orchestrates one discard — refuse, THEN spend, THEN mutate, never
/// the other order, so a refusal never has a side effect to undo.
///
/// <para>Soul balance and "is this actor mid-run" are Data-layer / player-scoped facts this module
/// has no way to read itself (Core cannot reference Data) — both are injected exactly like
/// <c>CostLedger</c>'s (T17) pool/derived resolvers. The real wiring (a soul-ledger spend, a
/// `UniqueActor` phase read matching the shipped equip gate's `phase != UniqueActorPhases.Roster`
/// check) is a future Data/Server integration point this service does not build for itself.</para>
/// </summary>
public sealed class UnlockDiscardService
{
    readonly Func<bool> _isMidRun;
    readonly Func<long, bool> _trySpendSoul;

    /// <param name="isMidRun">True when the owning actor is mid-run and discard must be refused
    /// (spec §3: "A15 freezes the action set at run start... discard is refused during a run").</param>
    /// <param name="trySpendSoul">Attempts to spend the given soul amount, returning whether it
    /// succeeded. Must not deduct anything on a false return — the same all-or-nothing contract
    /// <c>ActorResourcePools.TrySpend</c> (T17) already holds callers to.</param>
    public UnlockDiscardService(Func<bool> isMidRun, Func<long, bool> trySpendSoul)
    {
        _isMidRun = isMidRun ?? throw new ArgumentNullException(nameof(isMidRun));
        _trySpendSoul = trySpendSoul ?? throw new ArgumentNullException(nameof(trySpendSoul));
    }

    public DiscardOutcome TryDiscard(UnlockState state, string unlockId, long theta, UnlockTuning tuning)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var held = false;
        foreach (var h in state.Held)
        {
            if (h.UnlockId == unlockId) { held = true; break; }
        }
        if (!held)
            return DiscardOutcome.Refuse(DiscardRefusalReason.NotHeld);

        if (_isMidRun())
            return DiscardOutcome.Refuse(DiscardRefusalReason.MidRun);

        var price = DiscardPolicy.PriceOf(theta, tuning);
        if (!_trySpendSoul(price.SoulAmount))
            return DiscardOutcome.Refuse(DiscardRefusalReason.InsufficientSoul);

        // Guaranteed to succeed: `held` was already proven true above, and nothing between that
        // check and here can remove it (this service owns the whole sequence, single-threaded).
        return state.TryDiscard(unlockId);
    }
}
