using FusionRpg.Contracts;
using FusionRpg.Core.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

/// <summary>Budgeted drain — spec test groups 1 (ordering), 4 (budget), 5 (re-entrancy), 6 (session).</summary>
public class EventDrainTests
{
    /// <summary>Fake clock: each Process call advances time by a configurable cost.</summary>
    sealed class Harness
    {
        public readonly List<EffectEventDto> Seen = new();
        public long Now;
        public long CostPerProcess = 10;
        public Action<EffectEventDto>? OnProcess;
        public EventDrain Drain;

        public Harness()
        {
            Drain = new EventDrain(dto =>
            {
                Seen.Add(dto);
                Now += CostPerProcess;
                OnProcess?.Invoke(dto);
            }, () => Now);
        }

        public GameEventRec Rec(
            GameEventKind kind = GameEventKind.CombatHit,
            long target = 0xB,
            byte chainDepth = 0,
            long amount = -10,
            int pairId = 0,
            long swingPtr = 0)
            => new(kind, frame: 1, seq: Drain.NextSeq(), actorPtr: new IntPtr(0xA),
                targetPtr: new IntPtr(target), typeId: 1, targetTypeId: 2,
                side: GameEventSide.Zombie, amount: amount, hitCount: 1,
                chainDepth: chainDepth, sourceGrantIdx: -1,
                matchKeyIdx: Drain.InternMatchKey("m1"), pairId: pairId,
                swingPtr: new IntPtr(swingPtr));
    }

    [Fact]
    public void Processes_all_within_budget()
    {
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 1));
        h.Drain.Record(h.Rec(target: 2));
        var stats = h.Drain.Drain(budgetTicks: 1000);
        Assert.Equal(2, stats.Processed);
        Assert.Equal(0, stats.Carried);
        Assert.Equal(0, h.Drain.PendingCount);
    }

    [Fact]
    public void Budget_exhaustion_carries_remainder_in_order()
    {
        var h = new Harness { CostPerProcess = 50 };
        for (var i = 1; i <= 5; i++)
            h.Drain.Record(h.Rec(target: i));

        // Budget of 100 → ~2 records (first always processes; check runs after each).
        var stats = h.Drain.Drain(budgetTicks: 100);
        Assert.True(stats.Processed is >= 1 and < 5);
        Assert.Equal(5 - stats.Processed, h.Drain.PendingCount);

        var already = stats.Processed;
        var stats2 = h.Drain.Drain(budgetTicks: 100_000);
        Assert.Equal(5 - already, stats2.Processed);

        // Order across the two drains is the original FIFO by target ptr.
        for (var i = 0; i < 5; i++)
            Assert.Equal(new IntPtr(i + 1).ToString("X"), h.Seen[i].TargetPtr);
    }

    [Fact]
    public void Zero_budget_still_processes_one_record()
    {
        var h = new Harness { CostPerProcess = 50 };
        h.Drain.Record(h.Rec());
        h.Drain.Record(h.Rec(target: 0xC));
        var stats = h.Drain.Drain(budgetTicks: 0);
        Assert.Equal(1, stats.Processed); // forward progress is guaranteed
        Assert.Equal(1, stats.Carried);
    }

    [Fact]
    public void Dto_mapping_dealt_has_attacker_side_and_ptr_hex()
    {
        var h = new Harness();
        h.Drain.Record(h.Rec()); // target side = zombie → attacker side = plant
        h.Drain.Drain(1000);
        var dto = Assert.Single(h.Seen);
        Assert.Equal(EffectTriggers.OnDamageDealt, dto.Trigger);
        Assert.Equal("plant", dto.Side);
        Assert.Equal("A", dto.ActorPtr);
        Assert.Equal("B", dto.TargetPtr);
        Assert.Equal("m1", dto.MatchKey);
        Assert.Equal(1, dto.HitCount);
    }

    [Fact]
    public void Dto_swing_id_is_the_bullet_ptr_for_a_projectile_hit()
    {
        // lawn-hit-attribution (T6): a projectile hit's swing id is the bullet's own ptr, carried
        // independently of ActorPtr (now the firing creature) — "the record gains a swing-id field"
        // and "the DTO carries attacker and swing id independently" (spec-lawn-hit-attribution.md).
        var h = new Harness();
        h.Drain.Record(h.Rec(swingPtr: 0xC0FFEE));
        h.Drain.Drain(1000);
        var dto = Assert.Single(h.Seen);
        Assert.Equal("A", dto.ActorPtr);
        Assert.Equal("C0FFEE", dto.SwingId);
        Assert.NotEqual(dto.ActorPtr, dto.SwingId);
    }

    [Fact]
    public void Dto_swing_id_falls_back_to_actor_and_frame_for_melee()
    {
        // Melee has no bullet-shaped identity — the swing id derives from (ActorPtr, Frame), the
        // same identity _meleePairsByTarget/_meleePairsFrame already scope melee bookkeeping by.
        var h = new Harness();
        h.Drain.Record(h.Rec()); // no swingPtr — melee-shaped
        h.Drain.Drain(1000);
        var dto = Assert.Single(h.Seen);
        Assert.Equal("A:1", dto.SwingId);
    }

    [Fact]
    public void Two_records_sharing_one_swing_id_are_recognisable_as_one_swing()
    {
        // The dedupe key `lawn-hit-entry` (T9) will use: a piercing bullet hitting two different
        // victims records TWO GameEventRecs (one per target) that share ONE swing id.
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 0xB, swingPtr: 0xBEEF));
        h.Drain.Record(h.Rec(target: 0xC, swingPtr: 0xBEEF));
        h.Drain.Drain(10_000);
        Assert.Equal(2, h.Seen.Count);
        Assert.Equal(h.Seen[0].SwingId, h.Seen[1].SwingId);
        Assert.Equal("BEEF", h.Seen[0].SwingId);
        Assert.NotEqual(h.Seen[0].TargetPtr, h.Seen[1].TargetPtr);
    }

    [Fact]
    public void Records_at_depth_limit_are_refused()
    {
        var h = new Harness();
        h.Drain.ChainDepthLimit = 3;
        Assert.True(h.Drain.Record(h.Rec(chainDepth: 2)));
        Assert.False(h.Drain.Record(h.Rec(chainDepth: 3)));
        Assert.Equal(1, h.Drain.DroppedByDepth);
    }

    [Fact]
    public void Depth_limit_clamps_to_hard_range()
    {
        var h = new Harness();
        h.Drain.ChainDepthLimit = 0;   // below floor
        Assert.Equal(1, h.Drain.ChainDepthLimit);
        h.Drain.ChainDepthLimit = 99;  // above ceiling — infinite chains must be impossible
        Assert.Equal(8, h.Drain.ChainDepthLimit);
    }

    [Fact]
    public void Reentrant_records_inherit_depth_and_process_same_pass()
    {
        var h = new Harness();
        var spawned = false;
        h.OnProcess = _ =>
        {
            if (spawned) return;
            spawned = true;
            // Simulates a Unity hook firing during action execution: stamps RecordDepth.
            h.Drain.Record(new GameEventRec(
                GameEventKind.ChainSynthetic, 1, h.Drain.NextSeq(),
                new IntPtr(0xA), new IntPtr(0xC), 1, 2, GameEventSide.Zombie,
                -5, 1, h.Drain.RecordDepth, -1, -1, 0));
        };

        h.Drain.Record(h.Rec());
        var stats = h.Drain.Drain(10_000);
        Assert.Equal(2, stats.Processed);          // same-pass generation
        Assert.Equal(2, stats.Generations);
        Assert.Equal(1, h.Seen[1].ChainDepth);     // inherited parent+1
    }

    [Fact]
    public void Generation_cap_carries_runaway_chains_to_next_frame()
    {
        var h = new Harness();
        h.Drain.GenerationCap = 2;
        h.OnProcess = _ =>
        {
            var depth = h.Drain.RecordDepth;
            h.Drain.Record(new GameEventRec(
                GameEventKind.ChainSynthetic, 1, h.Drain.NextSeq(),
                new IntPtr(0xA), new IntPtr(0xC), 1, 2, GameEventSide.Zombie,
                -5, 1, depth, -1, -1, 0));
        };

        h.Drain.Record(h.Rec());
        var stats = h.Drain.Drain(100_000);
        Assert.True(stats.Generations <= 3);
        Assert.True(h.Drain.PendingCount > 0);     // chain continues next frame, not unbounded now
    }

    [Fact]
    public void FlushForPtr_processes_only_that_ptr_and_keeps_order()
    {
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xC));
        h.Drain.Record(h.Rec(target: 0xB, amount: -3));

        h.Drain.FlushForPtr(new IntPtr(0xB));
        Assert.Equal(2, h.Seen.Count);
        Assert.All(h.Seen, dto => Assert.Equal("B", dto.TargetPtr));
        Assert.Equal(1, h.Drain.PendingCount);

        h.Drain.Drain(10_000);
        Assert.Equal("C", h.Seen[2].TargetPtr);
    }

    /// <summary>lawn-combat-wire L-N15 (mutant "records kept by a ptr flush re-enter the ring in reverse
    /// order" survived): the records a flush leaves behind must drain in their original order. The test
    /// above keeps only one, so a reversal could not show; a dealt/taken pair drained out of order escapes
    /// the coalescer's pair suppression.</summary>
    [Fact]
    public void FlushForPtr_leaves_every_other_ptrs_records_in_their_original_order()
    {
        var h = new Harness();
        h.Drain.SessionMode = true;
        h.Drain.Record(h.Rec(target: 0xC));
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xD));
        h.Drain.Record(h.Rec(target: 0xE));
        h.Drain.Record(h.Rec(target: 0xB, amount: -3));
        h.Drain.Record(h.Rec(target: 0xF));

        h.Drain.FlushForPtr(new IntPtr(0xB));
        h.Seen.Clear();
        h.Drain.Drain(long.MaxValue);

        Assert.Equal(new[] { "C", "D", "E", "F" }, h.Seen.Select(d => d.TargetPtr).ToArray());
    }

    [Fact]
    public void FlushAllAndReset_drains_everything_and_clears_interning()
    {
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xC));
        h.Drain.FlushAllAndReset();
        Assert.Equal(2, h.Seen.Count);
        Assert.Equal(0, h.Drain.PendingCount);
        // Interner reset: new intern starts from index 0 again.
        Assert.Equal(0, h.Drain.InternMatchKey("m2"));
    }

    [Fact]
    public void Session_mode_ignores_budget_and_does_not_coalesce()
    {
        var h = new Harness { CostPerProcess = 1000 };
        h.Drain.SessionMode = true;
        h.Drain.Record(h.Rec(amount: -10));
        h.Drain.Record(h.Rec(amount: -15)); // same key — would merge outside sessions

        var stats = h.Drain.Drain(budgetTicks: 1);
        Assert.Equal(2, stats.Processed);   // event-for-event fidelity
        Assert.Equal(0, stats.Carried);
    }

    [Fact]
    public void Normal_mode_coalesces_same_key_before_processing()
    {
        var h = new Harness();
        h.Drain.Record(h.Rec(amount: -10));
        h.Drain.Record(h.Rec(amount: -15));
        var stats = h.Drain.Drain(10_000);
        Assert.Equal(1, stats.Processed);
        var dto = Assert.Single(h.Seen);
        Assert.Equal(2, dto.HitCount);
        Assert.Equal(-25, dto.Damage);
    }

    [Fact]
    public void Carry_is_never_recoalesced_with_new_records()
    {
        // v5 saturation fix: backlog must not re-merge every frame — a carried record's
        // HitCount stays fixed; new same-key records coalesce only among themselves.
        var h = new Harness { CostPerProcess = 50 };
        h.Drain.Record(h.Rec(target: 1));          // X — processed frame 1
        h.Drain.Record(h.Rec(target: 0xB));        // Y
        h.Drain.Record(h.Rec(target: 0xB));        // Y — merges with previous (HitCount 2)
        var s1 = h.Drain.Drain(budgetTicks: 1);
        Assert.Equal(1, s1.Processed);
        Assert.Equal(1, s1.Carried);               // merged Y(2) carried

        h.Drain.Record(h.Rec(target: 0xB));        // new Y
        h.Drain.Record(h.Rec(target: 0xB));        // new Y — merges into new-window Y(2)
        h.Drain.Drain(100_000);

        // Two distinct Y records processed (2+2), NOT one mega-record of 4.
        var yRecords = h.Seen.Where(d => d.TargetPtr == "B").ToList();
        Assert.Equal(2, yRecords.Count);
        Assert.All(yRecords, d => Assert.Equal(2, d.HitCount));
    }

    [Fact]
    public void Carry_survives_multiple_starved_frames_without_growth_work()
    {
        // Saturated regime: repeated zero-budget drains advance the cursor one record per
        // frame; pending shrinks monotonically, order preserved.
        var h = new Harness { CostPerProcess = 50 };
        for (var i = 1; i <= 4; i++)
            h.Drain.Record(h.Rec(target: i));

        for (var frame = 0; frame < 4; frame++)
            h.Drain.Drain(budgetTicks: 0);

        Assert.Equal(4, h.Seen.Count);
        Assert.Equal(new[] { "1", "2", "3", "4" }, h.Seen.Select(d => d.TargetPtr).ToArray());
        Assert.Equal(0, h.Drain.PendingCount);
    }

    [Fact]
    public void FlushForPtr_reaches_records_in_the_carry()
    {
        // v5: pending records live in carry + ring; the death barrier must scan both.
        var h = new Harness { CostPerProcess = 50 };
        h.Drain.Record(h.Rec(target: 1));
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xC));
        h.Drain.Drain(budgetTicks: 1);             // processes 1; B,C now in carry
        Assert.True(h.Drain.PendingCountFor(new IntPtr(0xB)) > 0);

        h.Drain.FlushForPtr(new IntPtr(0xB));
        Assert.Contains(h.Seen, d => d.TargetPtr == "B");
        Assert.Equal(0, h.Drain.PendingCountFor(new IntPtr(0xB)));
        Assert.True(h.Drain.PendingCountFor(new IntPtr(0xC)) > 0); // untouched, still pending
    }

    [Fact]
    public void Latency_frames_measured_when_frame_passed()
    {
        // v4 item 4: a record from frame 10 processed at frame 14 → latency 4.
        var h = new Harness();
        h.Drain.Record(h.Rec()); // Rec stamps frame: 1
        var stats = h.Drain.Drain(100_000, currentFrame: 5);
        Assert.Equal(4, stats.MaxLatencyFrames);
        Assert.True(stats.MaxRecordTicks > 0); // v4 item 2: costliest record visible
    }

    [Fact]
    public void Death_flush_budget_sheds_over_budget_with_counter()
    {
        // v4 item 1: per-death flush work is bounded; over-budget records for the dying
        // entity are shed and counted, others stay pending untouched.
        var h = new Harness { CostPerProcess = 100 };
        for (var i = 0; i < 5; i++)
            h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xC));

        var spent = h.Drain.FlushForPtr(new IntPtr(0xB), budgetTicks: 150);
        Assert.True(spent >= 150);
        Assert.True(h.Seen.Count is >= 1 and < 5);            // some processed within budget
        Assert.True(h.Drain.DroppedByDeathBudget >= 1);        // rest shed, counted
        Assert.Equal(1, h.Drain.PendingCount);                 // 0xC untouched
        Assert.Equal(0, h.Drain.PendingCountFor(new IntPtr(0xB)));
    }

    /// <summary>lawn-combat-wire L-N33: shedding a dying ATTACKER's record costs a living victim its delta; shedding a record
    /// that targets the dying ptr costs nothing. The two are counted apart.</summary>
    [Fact]
    public void Death_flush_shed_counts_records_with_a_living_target_separately()
    {
        var target = new Harness { CostPerProcess = 100 };
        for (var i = 0; i < 5; i++)
            target.Drain.Record(target.Rec(target: 0xB));
        target.Drain.FlushForPtr(new IntPtr(0xB), budgetTicks: 150);
        Assert.True(target.Drain.DroppedByDeathBudget >= 1);
        Assert.Equal(0, target.Drain.DroppedByDeathBudgetLivingTarget);

        var attacker = new Harness { CostPerProcess = 100 };
        for (var i = 0; i < 5; i++)
            attacker.Drain.Record(attacker.Rec(target: 0xB)); // actor 0xA dies with hits on living 0xB queued
        attacker.Drain.FlushForPtr(new IntPtr(0xA), budgetTicks: 150);
        Assert.True(attacker.Drain.DroppedByDeathBudget >= 1);
        Assert.Equal(attacker.Drain.DroppedByDeathBudget, attacker.Drain.DroppedByDeathBudgetLivingTarget);
    }

    [Fact]
    public void Death_flush_unlimited_budget_processes_all()
    {
        var h = new Harness { CostPerProcess = 100 };
        for (var i = 0; i < 5; i++)
            h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.FlushForPtr(new IntPtr(0xB)); // default -1 = unlimited (legacy semantics)
        Assert.Equal(5, h.Seen.Count);
        Assert.Equal(0, h.Drain.DroppedByDeathBudget);
    }

    [Fact]
    public void Ptr_index_tracks_pending_through_record_drain_and_flush()
    {
        // v3 A3: derived index must match the ring after arbitrary operation sequences.
        var h = new Harness { CostPerProcess = 50 };
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xC));
        Assert.Equal(2, h.Drain.PendingCountFor(new IntPtr(0xB)));
        Assert.Equal(3, h.Drain.PendingCountFor(new IntPtr(0xA))); // actor on all three

        // Partial drain (budget forces carry): index must reflect only what remains pending.
        var stats = h.Drain.Drain(budgetTicks: 1);
        Assert.True(stats.Carried > 0);
        var remainingForB = h.Drain.PendingCountFor(new IntPtr(0xB));
        var remainingForC = h.Drain.PendingCountFor(new IntPtr(0xC));
        Assert.Equal(h.Drain.PendingCount, remainingForB + remainingForC); // each rec: 1 target key
        Assert.Equal(h.Drain.PendingCount, h.Drain.PendingCountFor(new IntPtr(0xA)));

        h.Drain.FlushForPtr(new IntPtr(0xB));
        Assert.Equal(0, h.Drain.PendingCountFor(new IntPtr(0xB)));

        h.Drain.Drain(100_000);
        Assert.Equal(0, h.Drain.PendingCountFor(new IntPtr(0xA)));
        Assert.Equal(0, h.Drain.PendingCount);
    }

    [Fact]
    public void FlushForPtr_with_nothing_pending_is_a_noop()
    {
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 0xB));
        // Unknown ptr: O(1) early-out — nothing processed, nothing reordered.
        h.Drain.FlushForPtr(new IntPtr(0xDEAD));
        Assert.Empty(h.Seen);
        Assert.Equal(1, h.Drain.PendingCount);
    }

    [Fact]
    public void Reentrant_flush_during_drain_is_safe_no_duplicates_no_loss()
    {
        // A drained record can kill an entity → death hook → FlushForPtr mid-drain.
        // The nested flush must no-op; every record processes exactly once.
        var h = new Harness();
        h.OnProcess = _ =>
        {
            h.Drain.FlushForPtr(new IntPtr(0xC));   // nested — must not corrupt the pass
            h.Drain.FlushAllAndReset();              // nested — must not clear mid-drain state
        };
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xC));
        h.Drain.Drain(100_000);

        // C1: the nested FlushAllAndReset means the match ended mid-drain — the window
        // remainder belongs to the ended match and is discarded, never resurrected.
        Assert.Single(h.Seen);
        Assert.Equal(0, h.Drain.PendingCount);
    }

    [Fact]
    public void Nested_reset_discards_carry_and_resets_interning()
    {
        // C1 (review, repro-proven): match end fired by a processed record must not leak the
        // ended match's carried records or interner entries into the next match.
        var h = new Harness { CostPerProcess = 50 };
        var fired = false;
        h.OnProcess = _ =>
        {
            if (fired) return;
            fired = true;
            h.Drain.FlushAllAndReset(); // board.end mid-drain
        };
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.Record(h.Rec(target: 0xC));
        h.Drain.Record(h.Rec(target: 0xD));

        h.Drain.Drain(budgetTicks: 1); // budget would carry — reset must override
        Assert.Single(h.Seen);
        Assert.Equal(0, h.Drain.PendingCount);
        Assert.Equal(0, h.Drain.PendingCountFor(new IntPtr(0xA)));
        // Interner was reset — next intern starts from index 0.
        Assert.Equal(0, h.Drain.InternMatchKey("next-match"));
    }

    [Fact]
    public void Budget_carry_preserves_fifo_against_reentrant_records()
    {
        // I1 (review): carried window remainder is OLDER than records recorded during
        // processing — the carry must drain first next frame.
        var h = new Harness { CostPerProcess = 50 };
        var recorded = false;
        h.OnProcess = _ =>
        {
            if (recorded) return;
            recorded = true;
            h.Drain.Record(h.Rec(target: 0xE)); // re-entrant, newest
        };
        h.Drain.Record(h.Rec(target: 1));
        h.Drain.Record(h.Rec(target: 2));
        h.Drain.Record(h.Rec(target: 3));

        var s1 = h.Drain.Drain(budgetTicks: 1); // processes target 1, carries 2,3; ring holds E
        Assert.True(s1.Carried >= 2);
        h.Drain.Drain(100_000);

        var order = h.Seen.Select(d => d.TargetPtr).ToList();
        Assert.Equal(new[] { "1", "2", "3", "E" }, order);
    }

    [Fact]
    public void Session_mode_still_suppresses_paired_taken()
    {
        // I4 (review): pair suppression is causal, not a coalescing optimization — v1
        // suppressed the taken of one physical hit too.
        var h = new Harness();
        h.Drain.SessionMode = true;
        h.Drain.Record(new GameEventRec(GameEventKind.CombatHit, 1, h.Drain.NextSeq(),
            new IntPtr(0xA), new IntPtr(0xB), 1, 2, Core.Events.GameEventSide.Plant,
            -10, 1, 0, -1, -1, pairId: 5));
        h.Drain.Record(new GameEventRec(GameEventKind.PlantDamage, 1, h.Drain.NextSeq(),
            new IntPtr(0xA), new IntPtr(0xB), 1, 2, Core.Events.GameEventSide.Plant,
            -10, 1, 0, -1, -1, pairId: 5));

        h.Drain.Drain(0);
        var dto = Assert.Single(h.Seen);
        Assert.Equal(EffectTriggers.OnDamageDealt, dto.Trigger);
    }

    [Fact]
    public void Reentrant_flush_during_flush_forces_the_nested_ptr_through_before_returning()
    {
        // T9c (lawn-hit-entry): a nested FlushForPtr used to silently no-op, leaving its ptr's
        // records pending until the NEXT natural drain — which meant a caller that took "flushed"
        // as permission to withdraw grants was lied to (the records would then drain AFTER the
        // withdraw). It must now be forced through before the OUTER FlushForPtr call returns, and
        // must tell its own caller (via a negative return) that it was nested so grant-withdraw
        // can be deferred rather than assumed safe.
        var h = new Harness();
        var nested = false;
        h.OnProcess = _ =>
        {
            if (nested) return;
            nested = true;
            h.Drain.Record(h.Rec(target: 0xD));                    // new record mid-flush
            var spent = h.Drain.FlushForPtr(new IntPtr(0xD));       // nested
            Assert.True(spent < 0);                                // T9c sentinel: caller must defer forget
        };
        h.Drain.Record(h.Rec(target: 0xB));
        h.Drain.FlushForPtr(new IntPtr(0xB));

        // The nested ptr's record was forced through by the time the OUTER call returned — never
        // left dangling for a later drain, and never lost.
        Assert.Equal(2, h.Seen.Count);
        Assert.Contains(h.Seen, d => d.TargetPtr == "D");
        Assert.Equal(0, h.Drain.PendingCount);
    }

    [Fact]
    public void Expensive_kind_defers_after_first_but_cheap_ones_flow()
    {
        var h = new Harness();
        // Teach the EMA that StatusHook is expensive: one pricey pass first.
        h.CostPerProcess = 900;
        h.Drain.Record(h.Rec(GameEventKind.StatusHook, target: 1));
        h.Drain.Drain(budgetTicks: 1000);
        h.Seen.Clear();
        h.CostPerProcess = 10;

        h.Drain.Record(h.Rec(GameEventKind.StatusHook, target: 2));
        h.Drain.Record(h.Rec(GameEventKind.StatusHook, target: 3));
        h.Drain.Record(h.Rec(GameEventKind.CombatHit, target: 4));
        var stats = h.Drain.Drain(budgetTicks: 1000);

        Assert.Equal(1, stats.ExpensiveDeferred);         // second StatusHook deferred
        Assert.Contains(h.Seen, d => d.TargetPtr == "4"); // cheap record behind it still ran
        Assert.True(h.Drain.PendingCount >= 1);
    }

    // --- lawn-hit-entry (T9a, D8): one swing id -> exactly one action trigger, N damage
    // applications. IsFirstOfSwing is the mechanism a future consumer (e.g. a stamina charge)
    // gates a per-swing action on; the elemental rider (Damage/TargetPtr per record) must never
    // be gated by it. ---

    [Fact]
    public void One_swing_five_victims_exactly_one_is_first_of_swing()
    {
        var h = new Harness();
        for (var i = 1; i <= 5; i++)
            h.Drain.Record(h.Rec(target: i, swingPtr: 0xBEEF));
        h.Drain.Drain(100_000);

        Assert.Equal(5, h.Seen.Count);                                  // N damage applications
        Assert.Equal(1, h.Seen.Count(d => d.IsFirstOfSwing));            // exactly one trigger
        Assert.Equal(4, h.Seen.Count(d => !d.IsFirstOfSwing));
        Assert.All(h.Seen, d => Assert.Equal("BEEF", d.SwingId));        // all one swing
        Assert.Equal(new[] { "1", "2", "3", "4", "5" },
            h.Seen.Select(d => d.TargetPtr).OrderBy(x => x).ToArray());  // each victim still hit
    }

    [Fact]
    public void One_melee_swing_two_victims_exactly_one_is_first_of_swing()
    {
        // Melee swing identity is (ActorPtr, Frame), no SwingPtr — same dedupe must apply there.
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 1));
        h.Drain.Record(h.Rec(target: 2));
        h.Drain.Drain(100_000);

        Assert.Equal(2, h.Seen.Count);
        Assert.Equal(1, h.Seen.Count(d => d.IsFirstOfSwing));
    }

    [Fact]
    public void Two_different_swings_each_get_their_own_trigger()
    {
        // Falsifier: the dedupe must not over-suppress across genuinely different swings.
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 1, swingPtr: 0xAAAA));
        h.Drain.Record(h.Rec(target: 2, swingPtr: 0xBBBB));
        h.Drain.Drain(100_000);

        Assert.Equal(2, h.Seen.Count(d => d.IsFirstOfSwing));
    }

    [Fact]
    public void Swing_split_across_a_budget_carry_still_triggers_exactly_once()
    {
        // The persistent-counter design's whole point: a swing whose victims get split across two
        // Drain() calls (budget exhaustion mid-window) must not re-trigger for the carried half.
        var h = new Harness { CostPerProcess = 60 };
        h.Drain.Record(h.Rec(target: 1, swingPtr: 0xC0FFEE));
        h.Drain.Record(h.Rec(target: 2, swingPtr: 0xC0FFEE));
        h.Drain.Record(h.Rec(target: 3, swingPtr: 0xC0FFEE));

        var s1 = h.Drain.Drain(budgetTicks: 100); // processes some, carries the rest
        Assert.True(s1.Processed < 3);
        h.Drain.Drain(100_000);                    // next "frame" drains the carried remainder

        Assert.Equal(3, h.Seen.Count);
        Assert.Equal(1, h.Seen.Count(d => d.IsFirstOfSwing)); // still exactly one, not one per frame
    }

    [Fact]
    public void A_swing_pointer_reused_after_full_drain_starts_a_fresh_trigger()
    {
        // Ptr-reuse safety: once a swing's records fully drain, its counter entry is removed — a
        // LATER, unrelated swing that happens to reuse the same bullet pointer must still fire.
        var h = new Harness();
        h.Drain.Record(h.Rec(target: 1, swingPtr: 0xF00D));
        h.Drain.Drain(100_000);
        Assert.True(h.Seen.Single().IsFirstOfSwing);

        h.Seen.Clear();
        h.Drain.Record(h.Rec(target: 2, swingPtr: 0xF00D)); // same ptr, brand-new swing
        h.Drain.Drain(100_000);
        Assert.True(h.Seen.Single().IsFirstOfSwing); // not permanently "already triggered"
    }

    [Fact]
    public void Non_swing_kinds_always_read_as_first()
    {
        // OnSpawn/taken-side triggers never had a dedupe concept and must keep firing every time.
        var h = new Harness();
        h.Drain.Record(new GameEventRec(GameEventKind.PlantDamage, 1, h.Drain.NextSeq(),
            new IntPtr(0xA), new IntPtr(0xB), 1, 2, GameEventSide.Plant, -10, 1, 0, -1, -1, 0));
        h.Drain.Record(new GameEventRec(GameEventKind.PlantDamage, 1, h.Drain.NextSeq(),
            new IntPtr(0xA), new IntPtr(0xC), 1, 2, GameEventSide.Plant, -10, 1, 0, -1, -1, 0));
        h.Drain.Drain(100_000);

        Assert.Equal(2, h.Seen.Count);
        Assert.All(h.Seen, d => Assert.True(d.IsFirstOfSwing));
    }

    // --- lawn-hit-entry (T9b, D9): an effect-bearing hit is never dropped under budget/ring
    // exhaustion — carried and coalesced. Every record that reaches EventDrain.Record already
    // passed a live-grant gate at the caller, so a ring-capacity overflow can no longer mean
    // "lost"; it must divert to the carry tier instead. ---

    [Fact]
    public void Ring_overflow_diverts_to_carry_instead_of_dropping()
    {
        var h = new Harness();
        var capacity = GameEventRing.DefaultCapacity;
        // One over capacity — the ring itself can hold `capacity`, so this one record must
        // overflow it under the OLD "drop" contract.
        for (var i = 0; i < capacity + 1; i++)
            Assert.True(h.Drain.Record(h.Rec(target: i, amount: -1))); // Record() itself never refuses this

        Assert.Equal(capacity + 1, h.Drain.PendingCount); // nothing lost before drain even runs

        long totalDamage = 0;
        h.Drain.Drain(long.MaxValue); // unlimited budget — everything should come through
        foreach (var dto in h.Seen)
            totalDamage += dto.Damage ?? 0;

        Assert.Equal(capacity + 1, h.Seen.Count);   // every single record delivered — D9
        Assert.Equal(-(capacity + 1), totalDamage);  // total damage preserved exactly
        Assert.Equal(0, h.Drain.PendingCount);
    }
}
