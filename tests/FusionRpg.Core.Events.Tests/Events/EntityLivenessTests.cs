using FusionRpg.Contracts;
using FusionRpg.Core.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Events;

/// <summary>
/// lawn-hit-entry T9 / lawn-combat-wire L-N16: "a dead/dying target absorbs no delta and triggers no
/// second <c>Die()</c>", for a record queued BEFORE death that drains after it. The consumer below
/// mirrors the injector's apply path: <c>InjectorEffectActionSink.ExecApplyResourceDelta</c> asks
/// <see cref="EntityLiveness.AdmitsDelta"/>, then <c>EntityStatWriter.AddZombieHp</c> applies the delta
/// and calls <c>ForceKill</c> (→ <c>Die()</c> → the die hook's <c>MarkDead</c>) at HP ≤ 0.
/// </summary>
public class EntityLivenessTests
{
    sealed class Lawn
    {
        public readonly EntityLiveness Liveness = new();
        public readonly Dictionary<long, long> Hp = new();
        public readonly Dictionary<long, int> Deaths = new();
        public int DeltasApplied;
        public EventDrain Drain;

        public Lawn()
        {
            Drain = new EventDrain(dto =>
            {
                if (!Liveness.AdmitsDelta(dto.TargetPtr)) return;
                Assert.True(EntityLiveness.TryParsePtr(dto.TargetPtr, out var ptr));
                var key = (long)ptr;
                DeltasApplied++;
                Hp[key] -= Math.Abs(dto.Damage ?? 0);
                if (Hp[key] <= 0) Die(key);
            });
        }

        public void Die(long ptr)
        {
            Deaths[ptr] = Deaths.GetValueOrDefault(ptr) + 1;
            Liveness.MarkDead(new IntPtr(ptr));
        }

        public void Hit(long attacker, long target, long amount, int frame = 1) =>
            Drain.Record(new GameEventRec(GameEventKind.CombatHit, frame, Drain.NextSeq(),
                actorPtr: new IntPtr(attacker), targetPtr: new IntPtr(target),
                typeId: 1, targetTypeId: 2, side: GameEventSide.Zombie,
                amount: amount, hitCount: 1, chainDepth: 0, sourceGrantIdx: -1,
                matchKeyIdx: Drain.InternMatchKey("m1"), pairId: 0));
    }

    [Fact]
    public void A_hit_queued_before_death_applies_no_delta_and_runs_no_second_Die()
    {
        var lawn = new Lawn { Drain = { SessionMode = true } }; // uncoalesced: two separate records
        lawn.Hp[0xB] = 30;

        lawn.Hit(attacker: 0xA1, target: 0xB, amount: -30); // lethal
        lawn.Hit(attacker: 0xA2, target: 0xB, amount: -30); // already in flight when the first one kills
        lawn.Drain.Drain(long.MaxValue);

        Assert.Equal(1, lawn.Deaths[0xB]);
        Assert.Equal(1, lawn.DeltasApplied);
        Assert.Equal(0, lawn.Hp[0xB]);
    }

    [Fact]
    public void Death_marked_outside_the_drain_blocks_every_pending_delta_for_that_ptr_only()
    {
        var lawn = new Lawn { Drain = { SessionMode = true } };
        lawn.Hp[0xB] = 100;
        lawn.Hp[0xC] = 100;
        lawn.Hit(0xA1, 0xB, -10);
        lawn.Hit(0xA1, 0xC, -10);

        lawn.Die(0xB); // vanilla kill between record and drain (Zombie.Die prefix → MarkDead)
        lawn.Drain.Drain(long.MaxValue);

        Assert.Equal(100, lawn.Hp[0xB]);
        Assert.Equal(1, lawn.Deaths[0xB]);
        Assert.Equal(90, lawn.Hp[0xC]);
    }

    [Fact]
    public void A_new_entity_spawned_at_a_recycled_ptr_takes_hits_again()
    {
        var lawn = new Lawn { Drain = { SessionMode = true } };
        lawn.Hp[0xB] = 10;
        lawn.Hit(0xA1, 0xB, -10);
        lawn.Drain.Drain(long.MaxValue);
        Assert.True(lawn.Liveness.IsDead(new IntPtr(0xB)));

        lawn.Liveness.MarkSpawned(new IntPtr(0xB)); // Zombie.Start postfix for the new occupant
        lawn.Hp[0xB] = 50;
        lawn.Hit(0xA1, 0xB, -20, frame: 2);
        lawn.Drain.Drain(long.MaxValue);

        Assert.Equal(30, lawn.Hp[0xB]);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("b")]
    [InlineData("0xB")]
    [InlineData("entity:0b")]
    public void Every_ptr_spelling_of_a_dead_target_is_refused(string spelling)
    {
        var liveness = new EntityLiveness();
        liveness.MarkDead(new IntPtr(0xB));
        Assert.False(liveness.AdmitsDelta(spelling));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("0")]
    public void An_unparseable_ptr_is_admitted_because_this_gate_only_refuses_dead_targets(string? spelling)
    {
        var liveness = new EntityLiveness();
        liveness.MarkDead(new IntPtr(0xB));
        Assert.True(liveness.AdmitsDelta(spelling));
    }

    [Fact]
    public void Clear_at_match_end_forgets_every_mark()
    {
        var liveness = new EntityLiveness();
        liveness.MarkDead(new IntPtr(0xB));
        liveness.MarkDead(IntPtr.Zero);
        Assert.Equal(1, liveness.DeadCount);

        liveness.Clear();

        Assert.False(liveness.IsDead(new IntPtr(0xB)));
        Assert.True(liveness.AdmitsDelta("B"));
    }

    /// <summary>lawn-combat-wire L-N27: a hit on an entity that still has health, at a ptr marked dead, is a stale mark —
    /// counted and reported per window; hits on unmarked ptrs are not.</summary>
    [Fact]
    public void Hits_on_living_entities_at_dead_marked_ptrs_are_counted_and_reset_per_window()
    {
        var liveness = new EntityLiveness();
        liveness.MarkDead(new IntPtr(0xA));

        Assert.False(liveness.NoteHitOnLivingEntity(new IntPtr(0xB)));
        Assert.True(liveness.NoteHitOnLivingEntity(new IntPtr(0xA)));
        Assert.True(liveness.NoteHitOnLivingEntity(new IntPtr(0xA)));

        Assert.Equal((2L, new IntPtr(0xA)), liveness.TakeStaleMarkHits());
        Assert.Equal((0L, IntPtr.Zero), liveness.TakeStaleMarkHits());

        liveness.MarkSpawned(new IntPtr(0xA));
        Assert.False(liveness.NoteHitOnLivingEntity(new IntPtr(0xA)));
    }
}
