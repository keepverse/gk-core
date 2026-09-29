namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `core-scorer` (module 1, CAI1.4, spec-core-scorer.md §6): moved verbatim from
/// `Battle/Siege/SiegeAiIntentSource.cs:316-351` on 2026-09-20 (base-defense `siege-ai` task 17.8,
/// spec-siege-ai.md §5.20 rule 3) — the per-actor "last target, last retarget tick" memory. Widened
/// with two more members so a commitment bonus and a repeat-decay lever have exactly ONE anti-repeat
/// mechanism to live in (audit M9: two mechanisms for one problem is the defect this move exists to
/// close). `TryGetHeld`/`RecordRetarget`/`Forget` are unchanged, including the property that makes
/// `retargetLatencyTicks == 0` (the value `siege.v1.json` ships) behave exactly like the stateless
/// path.
///
/// <para>One instance is meant to be constructed ONCE per battle and reused across every decision on
/// that battle — like `CooldownLedger`, this is battle-scoped state, never a `static`/shared
/// singleton. Memory is rebuilt by replay from tick 0.</para>
///
/// <para>This is the ONLY anti-repeat mechanism in the repo. A commitment bonus or a repeat-decay
/// lever added anywhere else (a second dictionary, a second "last chosen" field) would be exactly the
/// SOLID (S) defect this module exists to close.</para>
/// </summary>
public sealed class RetargetLedger
{
    readonly Dictionary<string, (string TargetKey, long RetargetedAtTick)> _lastByActor = new(StringComparer.Ordinal);
    readonly Dictionary<(string ActorKey, string ActionId), long> _lastActionChosenAtTick = new();

    /// <summary>
    /// True when `actorKey` already has a target that should be HELD rather than rescored this tick —
    /// the latency has not yet elapsed since the last retarget AND `stillValid` confirms the held
    /// target is still a real, live, scoreable enemy. `retargetLatencyTicks == 0` (the shipped
    /// default) always returns false: `nowTick - RetargetedAtTick` is never negative, so "elapsed &lt;
    /// 0 ticks" can never hold — immediate re-evaluation every call, byte-identical to the pre-move
    /// stateless shape.
    /// </summary>
    public bool TryGetHeld(
        string actorKey, long nowTick, long retargetLatencyTicks, Func<string, bool> stillValid,
        out string targetKey)
    {
        if (_lastByActor.TryGetValue(actorKey, out var last) &&
            checked(nowTick - last.RetargetedAtTick) < retargetLatencyTicks &&
            stillValid(last.TargetKey))
        {
            targetKey = last.TargetKey;
            return true;
        }

        targetKey = "";
        return false;
    }

    /// <summary>Records a fresh retarget decision — called only when `ChooseTarget` actually rescored
    /// (never when `TryGetHeld` served a held target), so the latency window restarts from THIS tick.</summary>
    public void RecordRetarget(string actorKey, string targetKey, long nowTick) =>
        _lastByActor[actorKey] = (targetKey, nowTick);

    /// <summary>Clears an actor's own memory — e.g. it just rescored and found no live target at all.</summary>
    public void Forget(string actorKey) => _lastByActor.Remove(actorKey);

    /// <summary>
    /// combat-ai `core-scorer` §6: added to the score of the CURRENTLY-HELD target only — every other
    /// candidate is unaffected. `bonus == 0` (the identity default) never even reads the ledger, so
    /// `Commitment_bonus_zero_is_byte_identical` holds unconditionally. Lewis's warning travels with
    /// the code: a commitment bonus only MOVES the oscillation zone; the real fix is a distinguishing
    /// consideration.
    /// </summary>
    public long CommitmentBonusFor(string actorKey, string candidateKey, long bonus)
    {
        if (bonus == 0) return 0;
        return _lastByActor.TryGetValue(actorKey, out var last) &&
               string.Equals(last.TargetKey, candidateKey, StringComparison.Ordinal)
            ? bonus
            : 0;
    }

    /// <summary>
    /// combat-ai `core-scorer` §6: a per-mille multiplier on a recently-chosen ACTION's rank ordering
    /// — 1000 (no decay) at `halfLifeTicks &lt;= 0` (the identity/off sentinel), so
    /// `Repeat_decay_1000_is_byte_identical` holds and touches no ledger state at all. Otherwise the
    /// multiplier halves for each `halfLifeTicks` elapsed since <see cref="RecordActionChosen"/> last
    /// recorded this `(actorKey, actionId)` pair, recovering to 1000 once ten half-lives have passed —
    /// a floor of 1 rather than 0, since a repeat should be discouraged, never made structurally
    /// impossible (that is a gate's job, not a rank multiplier's). Unwired in this module: no
    /// production caller exists yet, matching `CommitmentBonusFor`'s own identity-until-wired shape.
    /// </summary>
    public int RepeatDecayFor(string actorKey, string actionId, long nowTick, int halfLifeTicks)
    {
        if (halfLifeTicks <= 0) return 1000;
        if (!_lastActionChosenAtTick.TryGetValue((actorKey, actionId), out var lastTick) || nowTick < lastTick)
            return 1000;

        var elapsedHalfLives = checked(nowTick - lastTick) / halfLifeTicks;
        if (elapsedHalfLives >= 10) return 1000;
        var milli = (int)(1000L >> (int)elapsedHalfLives);
        return milli < 1 ? 1 : milli;
    }

    /// <summary>The write half of <see cref="RepeatDecayFor"/>'s memory — called only when
    /// `actionId` is actually committed to, mirroring <see cref="RecordRetarget"/>'s own "only on a
    /// real decision" discipline.</summary>
    public void RecordActionChosen(string actorKey, string actionId, long nowTick) =>
        _lastActionChosenAtTick[(actorKey, actionId)] = nowTick;
}
