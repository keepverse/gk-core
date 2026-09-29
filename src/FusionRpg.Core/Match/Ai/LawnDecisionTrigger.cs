using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Match.Ai;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.7) — one actor's trigger state. Five fields, all
/// value types, so a warmed dictionary never allocates to hold one.
///
/// <para><see cref="Swings"/> is clamped at <c>2N</c> by the carry rule (see
/// <see cref="LawnDecisionTrigger"/>), <see cref="Offset"/> is <c>0..N-1</c>, and the two ticks are
/// lawn ticks (<c>KernelDriveHost.NowTicks / 100</c>, the same 100 ms grid the lawn cost ledger
/// already uses).</para>
/// </summary>
public struct LawnActorTriggerState
{
    /// <summary>First-of-swing records since the last committed cast. Seeded with
    /// <see cref="Offset"/> at registration and clamped at <c>2N</c> on a cast.</summary>
    public int Swings;

    /// <summary>The lawn tick this actor's timer edge is due on.</summary>
    public long NextTimerTick;

    /// <summary>No edge is produced at or before this tick. Set on a committed cast; <c>-1</c> until
    /// then, so a registration at tick 0 is not read as locked.</summary>
    public long LockUntilTick;

    /// <summary>The carry: this actor was due and is still owed a decision. A <c>bool</c>, never a
    /// count — at most one held cast.</summary>
    public bool Pending;

    /// <summary>The seeded per-actor desync offset, applied once at registration.</summary>
    public int Offset;
}

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.7, spec-lawn-cast-trigger.md §1–§5) — the swing
/// counter, the timer, the post-cast lock, the carry and the seeded offset, for every lawn actor.
///
/// <para><b>Pure.</b> It reads no clock: <c>nowTick</c> is passed in by the host, which reads
/// <c>KernelDriveHost.NowTicks / 100</c> — the exact expression the lawn cost ledger and cooldown
/// ledger are already built on. A trigger on a different time base would recreate D15 ("one board, two
/// notions of when") one field along, so this class deliberately holds no clock of its own and no
/// <c>DateTime</c> appears anywhere on its path.</para>
///
/// <para><b>Two triggers, OR'd.</b> The actor is due on its <c>N</c>-th first-of-swing record, or when
/// <c>T</c> lawn ticks have passed since its last committed cast, whichever comes first — and only on
/// an edge, never per frame and never per zombie.</para>
///
/// <para><b>Hold at N, carry capped at one cast.</b> A refused decision (nothing usable, no token, or
/// the frame budget spent) leaves the actor <see cref="LawnActorTriggerState.Pending"/> — due again on
/// the next frame with no further swings. A committed cast clamps the counter to
/// <c>[0, N × CarryCasts]</c>: the upper bound stops a blocked actor bursting five casts in five
/// frames, and the lower bound is load-bearing because a cast can commit BELOW <c>N</c> when an order
/// drives it (module 20) — <c>Math.Min(Swings - N, N)</c> would go negative and stall the actor's next
/// natural cast by up to <c>2N</c> swings, which reads in play as "my creature stopped casting after I
/// ordered it".</para>
///
/// <para><b>What must not feed the counter.</b> Only a record whose <c>IsFirstOfSwing</c> is true
/// counts; a cast-origin record never does (a cast that manufactures its own next trigger is a
/// feedback loop), and neither does a record for a ptr this trigger does not know — which is also how
/// a reused IL2CPP address starts clean, because the death path calls <see cref="Remove"/>.</para>
/// </summary>
public sealed class LawnDecisionTrigger
{
    /// <summary>Structural, NOT a balance number (tunables-ssot.md §1): it is the SHAPE of the carry
    /// rule, not a magnitude. 0 is TFT's pre-Set-12 reset-to-zero, 1 is Set 12, and ≥2 is the burst
    /// this module exists to prevent — so it is a behaviour switch a design decision owns, never a
    /// number a balance pass may flip. It is also the upper bound test 4 asserts, and a number a
    /// contract test pins is a declaration, not a reading (validation-ssot.md).</summary>
    public const int CarryCasts = 1;

    readonly int _swingsPerDecision;
    readonly int _ticksPerDecision;
    readonly int _postCastLockTicks;
    readonly ulong _matchSeed;
    readonly string _offsetStream;
    readonly Dictionary<string, LawnActorTriggerState> _states = new(StringComparer.Ordinal);

    /// <param name="swingsPerDecision">The tuning key <c>lawn.trigger.swingsPerDecision</c> (seed 7).
    /// A balance number, so it arrives from <c>data/tuning/combat-ai*.json</c> and never as a literal
    /// here.</param>
    /// <param name="ticksPerDecision">The tuning key <c>lawn.trigger.ticksPerDecision</c> (seed 50).</param>
    /// <param name="postCastLockTicks">The tuning key <c>lawn.trigger.postCastLockTicks</c> (seed 10).</param>
    /// <param name="matchSeed">The match's own seed — the offset is a function of
    /// <c>(matchSeed, actorKey)</c> and nothing else, so it is reproducible without being stored.</param>
    /// <param name="offsetStream">The tuning key <c>lawn.trigger.offsetStream</c>, pinned as data
    /// because moving it would re-roll every actor's offset.</param>
    public LawnDecisionTrigger(
        int swingsPerDecision,
        int ticksPerDecision,
        int postCastLockTicks,
        ulong matchSeed,
        string offsetStream)
    {
        if (swingsPerDecision < 0) throw new ArgumentOutOfRangeException(nameof(swingsPerDecision));
        if (ticksPerDecision < 0) throw new ArgumentOutOfRangeException(nameof(ticksPerDecision));
        if (postCastLockTicks < 0) throw new ArgumentOutOfRangeException(nameof(postCastLockTicks));
        if (string.IsNullOrEmpty(offsetStream)) throw new ArgumentException("offsetStream must not be empty", nameof(offsetStream));

        _swingsPerDecision = swingsPerDecision;
        _ticksPerDecision = ticksPerDecision;
        _postCastLockTicks = postCastLockTicks;
        _matchSeed = matchSeed;
        _offsetStream = offsetStream;
    }

    /// <summary>Actors currently holding trigger state.</summary>
    public int Count => _states.Count;

    /// <summary>
    /// Registers a ptr that has just entered the board. Idempotent: a ptr already registered keeps its
    /// state, because a Cold re-push at reconnect is not a new actor. A reused IL2CPP address reaches
    /// this fresh only because <see cref="Remove"/> ran on the death path first.
    /// </summary>
    public void Register(string actorKey, long nowTick)
    {
        if (string.IsNullOrEmpty(actorKey)) throw new ArgumentException("actorKey must not be empty", nameof(actorKey));
        if (_states.ContainsKey(actorKey)) return;

        // The seeded offset is what stops every creature casting on the same frame (the sync-spike
        // failure the ideal names). `NextPerMille() <= 1000` and both counts are small structural
        // bounds, so the widening to long cannot overflow and the divide is last (docs/architecture/numeric-types.md rules 2/5).
        var roll = SeededRng.DeriveStream(_matchSeed, _offsetStream + ":" + actorKey);
        var offset = (int)((long)roll.NextPerMille() * _swingsPerDecision / 1000);
        var timerDelay = (int)((long)roll.NextPerMille() * _ticksPerDecision / 1000);

        _states[actorKey] = new LawnActorTriggerState
        {
            Swings = offset,
            NextTimerTick = nowTick + timerDelay,
            // -1, not 0: a registration at tick 0 must not read as "locked until tick 0". The lock is
            // only ever set by a committed cast.
            LockUntilTick = -1,
            Pending = false,
            Offset = offset,
        };
    }

    /// <summary>
    /// One drained record. Increments only for a first-of-swing, non-cast-origin record belonging to a
    /// known ptr; returns whether it counted. Never throws, never allocates.
    /// </summary>
    public bool RecordSwing(string actorKey, bool isFirstOfSwing, bool castOrigin)
    {
        if (!isFirstOfSwing) return false;
        // A cast paid at commit (module 18). Counting it would let a cast manufacture its own next
        // trigger — the feedback loop the discriminator exists to keep impossible.
        if (castOrigin) return false;
        if (!_states.TryGetValue(actorKey, out var state)) return false;

        state.Swings = checked(state.Swings + 1);
        _states[actorKey] = state;
        return true;
    }

    /// <summary>Whether this actor is due for a decision at <paramref name="nowTick"/>. False for a ptr
    /// this trigger does not know.</summary>
    public bool IsDue(string actorKey, long nowTick)
    {
        if (!_states.TryGetValue(actorKey, out var state)) return false;

        // No edge inside the lock. The counter keeps accumulating through it (spec §4): freezing it
        // would make L a hidden second multiplier on the cadence, so N would silently stop meaning
        // "every N basic attacks".
        if (nowTick <= state.LockUntilTick) return false;

        if (state.Pending) return true;
        if (state.Swings >= _swingsPerDecision) return true;
        return nowTick >= state.NextTimerTick;
    }

    /// <summary>A cast that committed. Applies the carry clamp, clears the hold, starts the lock and
    /// restarts the timer from <paramref name="nowTick"/>.</summary>
    public void OnCommittedCast(string actorKey, long nowTick)
    {
        if (!_states.TryGetValue(actorKey, out var state)) return;

        state.Swings = Math.Clamp(state.Swings - _swingsPerDecision, 0, _swingsPerDecision * CarryCasts);
        state.Pending = false;
        state.LockUntilTick = nowTick + _postCastLockTicks;
        state.NextTimerTick = nowTick + _ticksPerDecision;
        _states[actorKey] = state;
    }

    /// <summary>A decision that was due but refused — nothing usable, no token, or the frame budget
    /// spent. The actor holds: still due on the next frame, with no further swings needed.</summary>
    public void OnRefusedDecision(string actorKey)
    {
        if (!_states.TryGetValue(actorKey, out var state)) return;
        state.Pending = true;
        _states[actorKey] = state;
    }

    /// <summary>The death path (Hot rule 4). A reused ptr address starts at zero swings and no lock
    /// because this ran before IL2CPP handed the address out again.</summary>
    public bool Remove(string actorKey) => _states.Remove(actorKey);

    /// <summary>The board edge, and the kill switch turning off mid-match: drop every actor's state.</summary>
    public void Clear() => _states.Clear();

    /// <summary>A copy of one actor's state, for a host that records it (the decision record's
    /// <c>Trigger</c> field) or a test that asserts it. A copy, never a live handle.</summary>
    public bool TryState(string actorKey, out LawnActorTriggerState state) => _states.TryGetValue(actorKey, out state);
}
