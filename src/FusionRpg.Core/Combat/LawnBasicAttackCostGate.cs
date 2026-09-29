using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Combat;

/// <summary>
/// `lawn-combat-wire` T12c (spec-basic-attack-cost.md "Where CostLedger.Check is called on the lawn"):
/// the lawn's own seam onto the SAME shared <see cref="CostLedger"/> battle already uses — one
/// authority, no second gate (§2.15 SOLID / `guard-actor-hub.py`). Never re-implements affordability
/// or spending; only decides WHEN to call <see cref="CostLedger.TryPay"/> and remembers the outcome
/// for the rest of one swing.
///
/// <para><b>There is no lawn "declare" step.</b> Battle's basic attack checks affordability at declare
/// time (<c>StubIntentSource.TryDeclare</c>, itself backed by <c>CostLedger.Check</c>) and only pays
/// at commit, assuming success (spec: "a commit-time shortfall here is the same rare TOCTOU race
/// CostLedger.TryPay already accepts without branching on"). The lawn has no equivalent declare step
/// — a vanilla PvZ hit has already happened by the time an <c>EffectEventDto</c> reaches this class —
/// so <see cref="TryPay"/> IS both the check and the charge here: its own pass-1-validate/pass-2-spend
/// contract (never partial) is exactly the affordability decision this gate needs, and there is
/// nothing upstream of it to reuse instead.</para>
///
/// <para><b>Reuses `lawn-hit-entry`'s (T9) own swing dedupe — never a second one.</b>
/// <c>EffectEventDto.IsFirstOfSwing</c> (stamped by <c>EventDrain.ConsumeSwingTriggerAndRelease</c>)
/// already answers "is this the one record of N victims that should trigger the action" for D8. This
/// gate charges <see cref="CostLedger.TryPay"/> exactly once per swing — on the record where
/// <c>isFirstOfSwing</c> is true — and CACHES that swing's paid/refused outcome (keyed by
/// <c>swingId</c>) so every other victim record sharing the same swing applies (or withholds) the
/// elemental rider consistently, without a second <c>TryPay</c> call (which would over-charge) and
/// without re-deciding from a possibly-already-spent pool (which could under- or over-apply the
/// rider mid-swing).</para>
///
/// <para><b>Position relative to the dedupe:</b> AFTER it — this gate reads
/// <c>ev.IsFirstOfSwing</c>/<c>ev.SwingId</c> as already-resolved facts; it never recomputes swing
/// identity itself. The call site (<c>EffectRuntime.OnDrained</c>) sits downstream of
/// <c>EventDrain.Drain</c>'s own <c>ConsumeSwingTriggerAndRelease</c>, so by the time this gate sees a
/// record the dedupe has already run.</para>
///
/// <para><b>Bounded cache, same shape as <c>EffectRuntime.DealtIdentity</c>.</b> A swing whose LAST
/// victim record never arrives (e.g. dropped by an upstream budget class this program does not touch)
/// would otherwise leak one entry forever; capping and clearing at the same threshold that
/// bookkeeping dictionary already uses keeps this gate's own memory bounded without needing visibility
/// into <c>EventDrain</c>'s private per-swing pending count.</para>
/// </summary>
public sealed class LawnBasicAttackCostGate
{
    // Structural, not tunable (tunables-ssot T2): a memory bound on this gate's bookkeeping, the same
    // cap-and-clear threshold EffectRuntime.DealtIdentity uses. Changing it changes how much memory an
    // abandoned swing can hold, never what a swing costs.
    const int MaxTrackedSwings = 2048;

    readonly CostLedger _ledger;
    readonly Dictionary<string, bool> _swingOutcome = new(StringComparer.Ordinal);

    public LawnBasicAttackCostGate(CostLedger ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <summary>
    /// Returns true when this swing may carry the RPG elemental rider (i.e. the actor could afford
    /// `act.attack`'s cost) — false when it may not (D6: the vanilla shot still lands; only the RPG's
    /// own contribution is withheld). Pays exactly once per swing.
    /// </summary>
    /// <param name="actorKey">The FIRING actor's own key (never the bullet's, never the victim's) —
    /// the lawn's basic-attack cost is charged against the attacker, matching every other resource
    /// spend in this repo.</param>
    /// <param name="swingId">`EffectEventDto.SwingId` — the swing this record belongs to.</param>
    /// <param name="isFirstOfSwing">`EffectEventDto.IsFirstOfSwing` — true for exactly one of the N
    /// records sharing `swingId` (D8's own dedupe, T9).</param>
    public bool TryChargeForSwing(string actorKey, string swingId, bool isFirstOfSwing, AtomRng? rng = null)
    {
        if (!isFirstOfSwing)
        {
            // Defensive, same posture as EventDrain.ConsumeSwingTriggerAndRelease's own "never seen
            // this key" fallback: a bookkeeping miss (this swing's first-of-swing record was dropped
            // upstream, or evicted by the cap below before every victim record arrived) must never
            // block a rider that a real, already-declared swing is entitled to — it answers "yes,
            // afford" rather than silently charging a SECOND payment to find out for sure.
            return !_swingOutcome.TryGetValue(swingId, out var cached) || cached;
        }

        var result = _ledger.TryPay(actorKey, LawnBasicAttackCostRow.ActionId, ActionCostTiming.OnCommit, rng);
        var afforded = result.Outcome == CostPayOutcome.Paid;

        if (_swingOutcome.Count >= MaxTrackedSwings) _swingOutcome.Clear();
        _swingOutcome[swingId] = afforded;
        return afforded;
    }
}
