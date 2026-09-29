using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// The trace is the instrument the byte-identity gate depends on. If it silently recorded
/// nothing, or recorded in an unstable order, the parity ladder would go green while proving
/// nothing — so the instrument gets its own tests rather than being trusted because it is
/// "just test infrastructure".
/// </summary>
public class BattleTraceTests
{
    sealed class FixedRng : ICombatRng
    {
        readonly int[] _values;
        int _i;
        public FixedRng(params int[] values) => _values = values;
        public int Next(int exclusiveMax) => _values[_i++ % _values.Length];
    }

    [Fact]
    public void Draws_are_recorded_per_stream_in_order()
    {
        var t = new BattleTrace();
        t.Draw("initiative", 700);
        t.Draw("essence", 42);
        t.Draw("initiative", 100);

        Assert.Equal(new[] { 700, 100 }, t.Draws("initiative"));
        Assert.Equal(new[] { 42 }, t.Draws("essence"));
    }

    [Fact]
    public void An_unseen_stream_reads_as_empty_rather_than_throwing()
    {
        Assert.Empty(new BattleTrace().Draws("never-used"));
    }

    [Fact]
    public void The_combat_wrapper_records_and_passes_the_value_through_unchanged()
    {
        // It must be a pure observer: if wrapping altered a roll, tracing would change outcomes
        // and every captured fixture would be a lie.
        var t = new BattleTrace();
        var wrapped = t.WrapCombat("crit", new FixedRng(123, 456));

        Assert.Equal(123, wrapped.Next(1000));
        Assert.Equal(456, wrapped.Next(1000));
        Assert.Equal(new[] { 123, 456 }, t.Draws("crit"));
    }

    [Fact]
    public void Phases_and_state_reach_the_digest_in_call_order()
    {
        var t = new BattleTrace();
        t.Phase(1, "status");
        t.State(1, "squad:0", hp: 90, shieldAbsorbed: 10);
        t.Phase(1, "initiative");
        t.Draw("initiative", 5);

        Assert.Equal(new[] { "1:status", "1:initiative" }, t.Phases);
        Assert.Equal(
            "phase 1 status\nstate 1 squad:0 hp=90 abs=10\nphase 1 initiative\ndraw initiative 5",
            t.Digest);
    }

    [Fact]
    public void An_empty_trace_has_an_empty_digest()
    {
        // Guards the failure mode where a trace records nothing and the comparison passes
        // trivially — the fixture tests assert non-emptiness for exactly this reason.
        Assert.Equal(string.Empty, new BattleTrace().Digest);
        Assert.Empty(new BattleTrace().Phases);
    }

    // ---- base-defense siege-ai R6: AiDecision ------------------------------------------------

    [Fact]
    public void AiDecisions_are_recorded_in_call_order()
    {
        var t = new BattleTrace();
        t.AiDecision(0, "wave:0", "#1=target-a(total=100)");
        t.AiDecision(1, "wave:1", "#1=target-b(total=50)");

        Assert.Equal(
            new[] { "0 wave:0 #1=target-a(total=100)", "1 wave:1 #1=target-b(total=50)" },
            t.AiDecisions);
    }

    [Fact]
    public void An_unrecorded_trace_has_no_ai_decisions()
    {
        Assert.Empty(new BattleTrace().AiDecisions);
    }

    [Fact]
    public void AiDecisions_are_kept_out_of_the_digest()
    {
        // Same reason Target/Turn are separate: an observability addition must not move the
        // fixture the byte-identity parity ladder compares.
        var t = new BattleTrace();
        t.Phase(1, "ai");
        t.AiDecision(1, "wave:0", "#1=target-a(total=100)");

        Assert.Equal("phase 1 ai", t.Digest);
        Assert.Single(t.AiDecisions);
    }
}
