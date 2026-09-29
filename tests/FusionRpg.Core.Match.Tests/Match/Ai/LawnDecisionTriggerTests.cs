using System;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.7, spec-lawn-cast-trigger.md §1–§6) — the pure
/// trigger: the swing counter, the timer, the post-cast lock, the carry and the seeded offset. Spec
/// test rows 1, 2, 3, 4, 4a, 5, 6 and the trigger half of 9 and 10.
///
/// <para>The seeded offset is not worked around: every test asks the SAME public derivation
/// (<c>SeededRng.DeriveStream(matchSeed, stream + ":" + key)</c>) for a key with the offset it needs,
/// and then asserts against the numbers it derived. That pins the derivation instead of hiding it.</para>
/// </summary>
public class LawnDecisionTriggerTests
{
    const int N = 7;
    const int T = 50;
    const int L = 10;
    const ulong Seed = 0x5EED_1234UL;
    const string Stream = "lawn.ai.offset";

    static LawnDecisionTrigger Trigger() => new(N, T, L, Seed, Stream);

    /// <summary>The trigger's own offset derivation, transcribed here so a test can predict it — the
    /// point is that the prediction and the production value must agree.</summary>
    static (int Offset, long TimerDelay) Seeded(string actorKey)
    {
        var roll = SeededRng.DeriveStream(Seed, Stream + ":" + actorKey);
        var offset = (int)((long)roll.NextPerMille() * N / 1000);
        var delay = (int)((long)roll.NextPerMille() * T / 1000);
        return (offset, delay);
    }

    /// <summary>A deterministic search for a ptr with the offset a test needs. Uniform over 0..N-1, so
    /// this finds one in a handful of tries; the search is itself deterministic, not random.</summary>
    static (string Key, int Offset, long TimerDelay) KeyWith(int wantedOffset, bool timerNotDueAtZero = true)
    {
        for (var i = 0; i < 10_000; i++)
        {
            var key = "ptr." + i;
            var (offset, delay) = Seeded(key);
            if (offset != wantedOffset) continue;
            if (timerNotDueAtZero && delay == 0) continue;
            return (key, offset, delay);
        }

        throw new InvalidOperationException("no ptr with the wanted seeded offset was found");
    }

    static void Swing(LawnDecisionTrigger trigger, string key, int times)
    {
        for (var i = 0; i < times; i++) Assert.True(trigger.RecordSwing(key, isFirstOfSwing: true, castOrigin: false));
    }

    // ---- row 1: the swing trigger ----------------------------------------------------------

    [Fact]
    public void Exactly_N_first_of_swing_records_produce_exactly_one_edge_and_N_minus_one_produce_none()
    {
        var (key, _, _) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);

        Swing(trigger, key, N - 1);
        Assert.False(trigger.IsDue(key, 0));

        Assert.True(trigger.RecordSwing(key, isFirstOfSwing: true, castOrigin: false));
        Assert.True(trigger.IsDue(key, 0));
    }

    [Fact]
    public void The_edge_arrives_exactly_N_minus_the_seeded_offset_swings_later()
    {
        // A key with a NON-zero offset, so the offset rule is exercised rather than assumed away.
        var (key, offset, _) = KeyWith(3);
        var trigger = Trigger();
        trigger.Register(key, 0);
        Assert.Equal(3, offset);

        Swing(trigger, key, N - offset - 1);
        Assert.False(trigger.IsDue(key, 0));

        Swing(trigger, key, 1);
        Assert.True(trigger.IsDue(key, 0));
    }

    // ---- row 2: what must not feed the counter ---------------------------------------------

    [Fact]
    public void A_non_first_record_a_cast_origin_record_and_an_unknown_ptr_each_increment_nothing()
    {
        var (key, _, _) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);

        Assert.False(trigger.RecordSwing(key, isFirstOfSwing: false, castOrigin: false));
        // The discriminator (module 18). Counting a cast-origin record lets a cast manufacture its own
        // next trigger — the feedback loop this refusal keeps impossible.
        Assert.False(trigger.RecordSwing(key, isFirstOfSwing: true, castOrigin: true));
        Assert.False(trigger.RecordSwing("ptr.not-registered", isFirstOfSwing: true, castOrigin: false));

        Assert.True(trigger.TryState(key, out var state));
        Assert.Equal(0, state.Swings);
        Assert.False(trigger.IsDue(key, 0));
    }

    [Fact]
    public void An_unknown_ptr_is_never_due()
    {
        var trigger = Trigger();
        Assert.False(trigger.IsDue("ptr.nobody", 0));
        Assert.False(trigger.TryState("ptr.nobody", out _));
    }

    // ---- row 3: hold at N ------------------------------------------------------------------

    [Fact]
    public void A_refused_decision_at_N_leaves_the_actor_due_with_no_further_swings()
    {
        var (key, _, _) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);
        Swing(trigger, key, N);
        Assert.True(trigger.IsDue(key, 0));

        trigger.OnRefusedDecision(key);

        Assert.True(trigger.IsDue(key, 0));
        Assert.True(trigger.IsDue(key, 1));
        Assert.True(trigger.TryState(key, out var state));
        Assert.True(state.Pending);
    }

    // ---- row 4: the carry's UPPER bound ----------------------------------------------------

    [Fact]
    public void Three_N_swings_accumulated_during_a_lock_produce_one_cast_not_three()
    {
        var (key, _, _) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);
        Swing(trigger, key, 3 * N);
        trigger.OnCommittedCast(key, 0);

        // The carry is capped at ONE cast's worth of swings, not at 3N - N. Without the cap a
        // creature blocked for twenty swings would fire five casts in five frames — the hoarding
        // failure inverted. The expected value is the FIXTURE's own cadence N, deliberately not
        // `N * CarryCasts`: an expectation written against the const moves with the mutation it is
        // supposed to catch (this is why raising CarryCasts to 2 must fail here).
        Assert.True(trigger.TryState(key, out var afterCast));
        Assert.Equal(N, afterCast.Swings);
        Assert.Equal(0 + L, afterCast.LockUntilTick);

        // Swings keep accumulating through the lock (spec §4) ...
        Swing(trigger, key, 3 * N);
        Assert.True(trigger.TryState(key, out var duringLock));
        Assert.Equal(N + 3 * N, duringLock.Swings);

        // ... but no edge is produced inside it, and exactly one edge exists the moment it lifts.
        Assert.False(trigger.IsDue(key, L));
        Assert.True(trigger.IsDue(key, L + 1));

        trigger.OnCommittedCast(key, L + 1);
        Assert.True(trigger.TryState(key, out var secondCast));
        Assert.Equal(N, secondCast.Swings);
    }

    // ---- row 4a: the carry's LOWER bound (the order-driven case) ---------------------------

    [Fact]
    public void A_cast_committed_below_N_leaves_the_counter_at_zero_and_the_next_edge_exactly_N_swings_later()
    {
        var (key, _, _) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);
        Swing(trigger, key, 2);

        // An order (module 20) fires as soon as the gates pass, whatever the swing count.
        trigger.OnCommittedCast(key, 0);

        Assert.True(trigger.TryState(key, out var afterCast));
        Assert.Equal(0, afterCast.Swings); // never negative -- Math.Min would leave -5 here

        // Past the lock, before the timer edge: the next natural edge is exactly N swings away.
        Swing(trigger, key, N - 1);
        Assert.False(trigger.IsDue(key, L + 1));

        Swing(trigger, key, 1);
        Assert.True(trigger.IsDue(key, L + 1));
    }

    // ---- row 5: the lock, and what it does NOT freeze --------------------------------------

    [Fact]
    public void No_edge_inside_the_lock_an_edge_at_L_plus_one_and_the_counter_does_advance_through_it()
    {
        var (key, _, _) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);
        Swing(trigger, key, N);
        trigger.OnCommittedCast(key, 100);

        Swing(trigger, key, N); // during the lock
        Assert.True(trigger.TryState(key, out var state));
        Assert.Equal(N, state.Swings); // the counter DID advance: L is not a hidden multiplier on N

        for (var tick = 101; tick <= 100 + L; tick++) Assert.False(trigger.IsDue(key, tick));
        Assert.True(trigger.IsDue(key, 100 + L + 1));
    }

    // ---- row 6: the seeded offset ----------------------------------------------------------

    [Fact]
    public void The_seeded_offset_is_reproducible_and_matches_the_documented_derivation()
    {
        var (key, offset, delay) = KeyWith(0);
        var first = Trigger();
        var second = Trigger();
        first.Register(key, 0);
        second.Register(key, 0);

        Assert.True(first.TryState(key, out var a));
        Assert.True(second.TryState(key, out var b));
        Assert.Equal(offset, a.Offset);
        Assert.Equal(offset, b.Offset);
        Assert.Equal(delay, a.NextTimerTick);
        Assert.Equal(a.NextTimerTick, b.NextTimerTick);
    }

    [Fact]
    public void Two_different_ptrs_get_different_offsets()
    {
        var (keyA, offsetA, _) = KeyWith(0);
        var (keyB, offsetB, _) = KeyWith(1);

        Assert.NotEqual(keyA, keyB);
        Assert.NotEqual(offsetA, offsetB);

        var trigger = Trigger();
        trigger.Register(keyA, 0);
        trigger.Register(keyB, 0);
        Assert.True(trigger.TryState(keyA, out var a));
        Assert.True(trigger.TryState(keyB, out var b));
        Assert.Equal(offsetA, a.Offset);
        Assert.Equal(offsetB, b.Offset);
    }

    // ---- row 9 (trigger half): death and ptr reuse -----------------------------------------

    [Fact]
    public void Death_drops_the_state_and_a_reused_ptr_starts_at_zero_swings_and_no_lock()
    {
        var (key, _, delay) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);
        Swing(trigger, key, N);
        Assert.True(trigger.IsDue(key, 0));

        Assert.True(trigger.Remove(key));
        Assert.False(trigger.TryState(key, out _));
        Assert.False(trigger.IsDue(key, 0));

        // IL2CPP hands the address out again: a fresh registration, not the dead actor's state.
        trigger.Register(key, 500);
        Assert.True(trigger.TryState(key, out var reused));
        Assert.Equal(0, reused.Swings);
        Assert.Equal(-1, reused.LockUntilTick);
        Assert.False(trigger.IsDue(key, 500));
        Assert.Equal(500 + delay, reused.NextTimerTick);
    }

    [Fact]
    public void Register_is_idempotent_so_a_reconnect_push_does_not_reset_a_live_actor()
    {
        var (key, _, _) = KeyWith(0);
        var trigger = Trigger();
        trigger.Register(key, 0);
        Swing(trigger, key, N);

        trigger.Register(key, 900);

        Assert.True(trigger.TryState(key, out var state));
        Assert.Equal(N, state.Swings);
        Assert.Equal(1, trigger.Count);
    }

    // ---- row 10 (trigger half): the kill switch --------------------------------------------

    [Fact]
    public void Clear_drops_every_actor_and_the_edge_with_it()
    {
        var (keyA, _, _) = KeyWith(0);
        var (keyB, _, _) = KeyWith(1);
        var trigger = Trigger();
        trigger.Register(keyA, 0);
        trigger.Register(keyB, 0);
        Swing(trigger, keyA, N);

        trigger.Clear();

        Assert.Equal(0, trigger.Count);
        Assert.False(trigger.IsDue(keyA, 0));
        Assert.False(trigger.IsDue(keyB, 0));
    }

    // ---- hygiene ---------------------------------------------------------------------------

    [Fact]
    public void The_carry_is_capped_at_exactly_one_cast()
    {
        // A closed, code-owned structural constant: it is the SHAPE of the carry rule, not a
        // magnitude. 0 is TFT's pre-Set-12 reset-to-zero, 1 is Set 12, and >=2 is the burst this
        // module exists to prevent (tunables-ssot.md §1).
        Assert.Equal(1, LawnDecisionTrigger.CarryCasts);
    }

    [Fact]
    public void Negative_cadence_values_are_rejected_and_an_empty_stream_name_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnDecisionTrigger(-1, T, L, Seed, Stream));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnDecisionTrigger(N, -1, L, Seed, Stream));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnDecisionTrigger(N, T, -1, Seed, Stream));
        Assert.Throws<ArgumentException>(() => new LawnDecisionTrigger(N, T, L, Seed, ""));
    }

    [Fact]
    public void An_empty_ptr_is_rejected_at_registration()
    {
        var trigger = Trigger();
        Assert.Throws<ArgumentException>(() => trigger.Register("", 0));
    }
}
