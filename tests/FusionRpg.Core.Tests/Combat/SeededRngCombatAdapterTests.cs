using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>U2 — deterministic ICombatRng bridge for replayable hosts.</summary>
public class SeededRngCombatAdapterTests
{
    [Fact]
    public void Same_seed_and_stream_produce_identical_sequences()
    {
        var a = new SeededRngCombatAdapter(SeededRng.DeriveStream(42, "crit"));
        var b = new SeededRngCombatAdapter(SeededRng.DeriveStream(42, "crit"));
        for (var i = 0; i < 256; i++)
            Assert.Equal(a.Next(1_000_000), b.Next(1_000_000));
    }

    [Fact]
    public void Adapter_is_a_pure_passthrough_over_SeededRng_NextInt()
    {
        var raw = SeededRng.DeriveStream(7, "crit");
        var adapted = new SeededRngCombatAdapter(SeededRng.DeriveStream(7, "crit"));
        for (var i = 0; i < 64; i++)
            Assert.Equal(raw.NextInt(1_000_000), adapted.Next(1_000_000));
    }

    [Fact]
    public void Distinct_streams_from_one_seed_are_independent()
    {
        var crit = new SeededRngCombatAdapter(SeededRng.DeriveStream(42, "crit"));
        var other = new SeededRngCombatAdapter(SeededRng.DeriveStream(42, "riders"));
        var same = 0;
        for (var i = 0; i < 64; i++)
            if (crit.Next(1_000_000) == other.Next(1_000_000))
                same++;
        Assert.True(same < 4, "streams should not track each other");
    }

    [Fact]
    public void Nonpositive_max_returns_zero_matching_the_contract()
    {
        var adapter = new SeededRngCombatAdapter(SeededRng.DeriveStream(1, "crit"));
        Assert.Equal(0, adapter.Next(0));
        Assert.Equal(0, adapter.Next(-5));
    }

    [Fact]
    public void RollSuccess_consumes_exactly_one_draw_for_open_probabilities()
    {
        var reference = SeededRng.DeriveStream(9, "crit");
        var adapted = new SeededRngCombatAdapter(SeededRng.DeriveStream(9, "crit"));
        CombatProbability.RollSuccess(adapted, 0.5);
        // After one roll, the adapted stream sits one draw ahead of a fresh clone.
        reference.NextInt(1_000_000);
        Assert.Equal(reference.NextInt(1_000_000), adapted.Next(1_000_000));
    }
}
