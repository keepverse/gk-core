using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// aura-skill-todo.md Phase 5 / <b>TC3</b> — <see cref="EntityWriteGate"/>, the decision that says
/// whether a resolve reaches <c>EntityStatWriter</c>.
///
/// <para><b>Why this file exists.</b> This is the highest-value bug this repo found in 2026-08 — the
/// gate enumerated <i>contributors</i> instead of comparing <i>values</i>, so commander aptitude
/// bonuses composed correctly and then wrote nothing. It was found by the owner playing the game and
/// noticing a plant still had 300 HP, not by the suite. The rule lived inline in
/// <c>EntityApply.RunPlant</c>/<c>RunZombie</c> (duplicated), inside an assembly no CI test can build.
/// TC3 pulled it into Core so the regression is guarded by something that actually runs.</para>
///
/// <para><see cref="EntityFinal.DiffersFrom"/>'s own field-by-field coverage lives in
/// <c>AppliedCombatReachesWriterTests</c> and is not duplicated here; this file covers the gate
/// <i>around</i> it — the forced-source override and the composition of the two.</para>
/// </summary>
public class EntityWriteGateTests
{
    static EntityBaseline Baseline() => new()
    {
        Hp = 300, MaxHp = 300, Atk = 20,
        Arm1 = 0, Arm1Max = 0, Arm2 = 0, Arm2Max = 0,
        AttackInterval = 1.5, ProduceInterval = 24.0, ZombieSpeed = 1.0,
    };

    static EntityFinal Matching(EntityBaseline y0) => new()
    {
        Hp = y0.Hp, MaxHp = y0.MaxHp, Atk = y0.Atk,
        Arm1 = y0.Arm1, Arm1Max = y0.Arm1Max, Arm2 = y0.Arm2, Arm2Max = y0.Arm2Max,
        AttackInterval = y0.AttackInterval, ProduceInterval = y0.ProduceInterval,
        ZombieSpeed = y0.ZombieSpeed,
    };

    // ── the forced sources ───────────────────────────────────────────────────────────────────────

    /// <summary>TC3's acceptance box: <b>a forced source writes even when nothing differs.</b> Both
    /// run after a bag clear, when the value has legitimately returned to baseline — correct as a value
    /// answer, wrong as an action. Without the override, clearing a cheat would visibly do nothing.</summary>
    [Theory]
    [InlineData("pushScales")]
    [InlineData("reapply")]
    [InlineData("reapplyLiving")]        // the real InjectorLoop tag — substring match, not equality
    [InlineData("tabB:pushScales")]      // prefixed caller tags must still force
    [InlineData("spawn+reapply")]
    public void A_forced_source_writes_even_when_nothing_differs(string source)
    {
        var y0 = Baseline();
        var unchanged = Matching(y0);

        Assert.False(unchanged.DiffersFrom(y0), "fixture is wrong: this final must NOT differ, or the test proves nothing");
        Assert.True(EntityWriteGate.IsForcedSource(source));
        Assert.True(EntityWriteGate.ShouldWrite(unchanged, y0, source));
    }

    /// <summary>The complement — an ordinary source with nothing to say writes nothing. This is the
    /// "empty bag = no write on spawn" half; losing it would make every spawn poke the writer.</summary>
    [Theory]
    [InlineData("spawn")]
    [InlineData("absolute")]
    [InlineData("tabB:apply")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_source_with_no_value_change_does_not_write(string? source)
    {
        var y0 = Baseline();
        Assert.False(EntityWriteGate.IsForcedSource(source));
        Assert.False(EntityWriteGate.ShouldWrite(Matching(y0), y0, source));
    }

    /// <summary>Case sensitivity is deliberate and pinned: the tags are produced by this codebase, and
    /// an <c>OrdinalIgnoreCase</c> match would make an unrelated caller tag containing "Reapply"
    /// silently force a write. Documented so a future "helpful" relaxation is a decision, not a slip.</summary>
    [Theory]
    [InlineData("PushScales")]
    [InlineData("REAPPLY")]
    public void Forced_source_matching_is_ordinal_and_case_sensitive(string source)
    {
        Assert.False(EntityWriteGate.IsForcedSource(source));
    }

    // ── the value question ───────────────────────────────────────────────────────────────────────

    /// <summary><b>The original defect, as a regression test.</b> A single derived-only contribution —
    /// no cheat scale, no absolute, no PvzStats row, no effect-session mod — must reach the writer.
    /// Every one of the old contributor flags read this exact combination as "nothing to do".</summary>
    [Fact]
    public void A_value_change_from_any_source_writes_on_an_unforced_source()
    {
        var y0 = Baseline();
        var withBonus = Matching(y0);
        withBonus = new EntityFinal
        {
            Hp = withBonus.Hp, MaxHp = withBonus.MaxHp + 37_188, Atk = withBonus.Atk,
            Arm1 = withBonus.Arm1, Arm1Max = withBonus.Arm1Max,
            Arm2 = withBonus.Arm2, Arm2Max = withBonus.Arm2Max,
            AttackInterval = withBonus.AttackInterval, ProduceInterval = withBonus.ProduceInterval,
            ZombieSpeed = withBonus.ZombieSpeed,
        };

        Assert.True(EntityWriteGate.ShouldWrite(withBonus, y0, "spawn"),
            "a composed value change must write regardless of WHICH producer made it — never re-add a contributor check here");
    }

    /// <summary>The vanilla identity view. <see cref="EntityBaseline"/> carries no defense fields, so
    /// "unchanged" means exactly <c>×1, +0</c>; anything else is a real change even though there is no
    /// baseline field to compare against.</summary>
    [Theory]
    [InlineData(2f, 0, true)]
    [InlineData(1f, 5, true)]
    [InlineData(1f, 0, false)]
    public void The_vanilla_defense_identity_view_is_pinned(float defensePercent, int defenseFlat, bool expectWrite)
    {
        var y0 = Baseline();
        var b = Matching(y0);
        var final = new EntityFinal
        {
            Hp = b.Hp, MaxHp = b.MaxHp, Atk = b.Atk,
            Arm1 = b.Arm1, Arm1Max = b.Arm1Max, Arm2 = b.Arm2, Arm2Max = b.Arm2Max,
            AttackInterval = b.AttackInterval, ProduceInterval = b.ProduceInterval,
            ZombieSpeed = b.ZombieSpeed,
            DefensePercent = defensePercent, DefenseFlat = defenseFlat,
        };

        Assert.Equal(expectWrite, EntityWriteGate.ShouldWrite(final, y0, "spawn"));
    }

    // ── contract ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Null_arguments_reject_rather_than_silently_not_writing()
    {
        var y0 = Baseline();
        Assert.Throws<ArgumentNullException>(() => EntityWriteGate.ShouldWrite(null!, y0, "spawn"));
        Assert.Throws<ArgumentNullException>(() => EntityWriteGate.ShouldWrite(Matching(y0), null!, "spawn"));
    }
}
