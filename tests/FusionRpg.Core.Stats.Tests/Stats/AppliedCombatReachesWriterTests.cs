using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// The completeness half of the single-writer invariant.
///
/// <para><c>guard-single-writer.py</c> already enforces containment — only <c>EntityStatWriter</c>
/// may touch Unity combat fields. Nothing enforced the other direction: that everything
/// <c>ActorHub.Resolve</c> computes actually REACHES that writer. It did not. <c>EntityApply</c>
/// decided whether to write by enumerating the contributors it knew about, so a contributor missing
/// from the list composed correctly and was dropped silently — no error, no telemetry, no failing
/// test. Commander aptitudes were exactly that: a real 222-point <c>Might</c> allocation resolved to
/// <c>appliedAtk = 31010</c> on a live lawn and wrote nothing (owner-observed 2026-08-30).</para>
///
/// <para>These tests pin the replacement rule — <c>EntityFinal.DiffersFrom(baseline)</c> — and, most
/// importantly, <see cref="A_brand_new_derived_producer_reaches_the_writer_input_without_any_gate_edit"/>
/// proves a producer nobody has written yet still reaches the writer input. That is the property that
/// makes this bug class non-recurring rather than fixed-once.</para>
/// </summary>
public class AppliedCombatReachesWriterTests
{
    static EntityBaseline Baseline() => new()
    {
        Hp = 300, MaxHp = 300, Atk = 20,
        Arm1 = 0, Arm1Max = 0, Arm2 = 0, Arm2Max = 0,
        AttackInterval = 1.5, ProduceInterval = 0, ZombieSpeed = 0
    };

    static EntityFinal FinalMatching(EntityBaseline y0) => new()
    {
        Hp = y0.Hp, MaxHp = y0.MaxHp, Atk = y0.Atk,
        Arm1 = y0.Arm1, Arm1Max = y0.Arm1Max, Arm2 = y0.Arm2, Arm2Max = y0.Arm2Max,
        AttackInterval = y0.AttackInterval, ProduceInterval = y0.ProduceInterval,
        ZombieSpeed = y0.ZombieSpeed
    };

    [Fact]
    public void An_untouched_entity_does_not_differ_so_spawn_writes_nothing()
    {
        var y0 = Baseline();
        Assert.False(FinalMatching(y0).DiffersFrom(y0));
    }

    [Theory]
    // One per field the writer owns: a change to ANY of them must be seen, or that field is a
    // silent-drop hole exactly like progression.bonus.atk was.
    [InlineData("maxHp")]
    [InlineData("atk")]
    [InlineData("hp")]
    [InlineData("arm1")]
    [InlineData("arm1Max")]
    [InlineData("arm2")]
    [InlineData("arm2Max")]
    [InlineData("attackInterval")]
    [InlineData("produceInterval")]
    [InlineData("zombieSpeed")]
    [InlineData("defensePercent")]
    [InlineData("defenseFlat")]
    public void Any_single_composed_field_moving_is_enough_to_trigger_a_write(string field)
    {
        var y0 = Baseline();
        var b = FinalMatching(y0);
        EntityFinal moved = field switch
        {
            "maxHp" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp + 1, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "atk" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk + 1, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "hp" => new EntityFinal { Hp = b.Hp + 1, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "arm1" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1 + 1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "arm1Max" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max + 1, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "arm2" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2 + 1, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "arm2Max" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max + 1, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "attackInterval" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval + 0.5, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed },
            "produceInterval" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval + 0.5, ZombieSpeed = b.ZombieSpeed },
            "zombieSpeed" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed + 0.5 },
            "defensePercent" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed, DefensePercent = 2f },
            "defenseFlat" => new EntityFinal { Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk, Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max, AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval, ZombieSpeed = b.ZombieSpeed, DefenseFlat = 5 },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unmapped field")
        };
        Assert.True(moved.DiffersFrom(y0), $"a change to '{field}' must reach the writer");
    }

    /// <summary>The live case that was silently dropped: `progression.bonus.maxHp` from a commander
    /// aptitude, with NO cheat scale, NO absolute, NO PvzStats row and NO effect-session mod — the
    /// exact combination every one of the old contributor flags read as "nothing to do".</summary>
    [Fact]
    public void A_progression_bonus_with_no_other_contributor_still_reaches_the_writer()
    {
        var y0 = Baseline();
        var withBonus = new EntityFinal
        {
            Hp = y0.Hp, MaxHp = y0.MaxHp + 37188, Atk = y0.Atk,
            Arm1 = y0.Arm1, Arm1Max = y0.Arm1Max, Arm2 = y0.Arm2, Arm2Max = y0.Arm2Max,
            AttackInterval = y0.AttackInterval, ProduceInterval = y0.ProduceInterval,
            ZombieSpeed = y0.ZombieSpeed
        };
        Assert.True(withBonus.DiffersFrom(y0));
    }

    /// <summary>
    /// The anti-recurrence property, and the reason this file exists.
    ///
    /// <para>A subsystem written today, registered through the ordinary <c>ActorHub.Register</c> seam,
    /// contributes to a `progression.bonus.*` channel. Nothing in <c>EntityApply</c> knows it exists.
    /// Its contribution must still show up in <c>AppliedCombat</c> and still register as "differs from
    /// baseline". If someone reintroduces a contributor-enumerating write gate, this test fails —
    /// which is precisely what did not happen the first time.</para>
    /// </summary>
    [Fact]
    public void A_brand_new_derived_producer_reaches_the_writer_input_without_any_gate_edit()
    {
        // Fully qualified: the test tree has its own `FusionRpg.Core.Tests.ActorHub` namespace.
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new UnknownFutureProducer());

        var y0 = Baseline();
        var ctx = new StatContext { Side = StatSide.Plant, EntityKey = "future", Baseline = y0 };

        var resolved = hub.Resolve(ctx);

        Assert.NotEqual(resolved.RuntimePrimary.MaxHp, resolved.AppliedCombat.MaxHp);
        Assert.True(resolved.AppliedCombat.DiffersFrom(y0),
            "a producer EntityApply has never heard of must still reach the writer");
    }

    /// <summary>Stands in for every producer not yet written — auras, atoms, items, injuries.</summary>
    sealed class UnknownFutureProducer : IActorStatSubsystem
    {
        public string SubsystemId => "test.unknown-future-producer";
        public int Order => 100;

        public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods) =>
            mods.Add(new DerivedModifier(
                DerivedStatChannels.ProgressionBonusMaxHp,
                DerivedModifierOp.Flat,
                4242,
                SourceId: SubsystemId));
    }
}
