using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

public class SeededRngTests
{
    [Fact]
    public void Same_seed_produces_identical_sequences()
    {
        var a = new SeededRng(12345);
        var b = new SeededRng(12345);
        for (var i = 0; i < 100; i++)
            Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void Golden_sequence_locks_the_algorithm()
    {
        // Golden values captured at authoring time — any change here is an RngAlgoVersion bump.
        var rng = new SeededRng(42);
        var actual = new[] { rng.NextULong(), rng.NextULong(), rng.NextULong(), rng.NextULong() };
        Assert.Equal(new ulong[]
        {
            1546998764402558742UL,
            6990951692964543102UL,
            12544586762248559009UL,
            17057574109182124193UL
        }, actual);
    }

    [Fact]
    public void Streams_are_independent()
    {
        var damage1 = SeededRng.DeriveStream(999, "damage");
        var loot1 = SeededRng.DeriveStream(999, "loot");
        // Consume extra rolls from damage — loot's sequence must not shift.
        var damage2 = SeededRng.DeriveStream(999, "damage");
        var loot2 = SeededRng.DeriveStream(999, "loot");
        for (var i = 0; i < 10; i++) damage2.NextULong();
        for (var i = 0; i < 20; i++)
            Assert.Equal(loot1.NextULong(), loot2.NextULong());
        Assert.NotEqual(damage1.NextULong(), loot1.NextULong());
    }

    [Fact]
    public void Bounded_rolls_stay_in_range_and_cover_values()
    {
        var rng = new SeededRng(7);
        var seen = new HashSet<int>();
        for (var i = 0; i < 2000; i++)
        {
            var v = rng.NextInt(10);
            Assert.InRange(v, 0, 9);
            seen.Add(v);
        }

        Assert.Equal(10, seen.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
    }

    [Fact]
    public void PerMille_distribution_is_roughly_uniform()
    {
        var rng = SeededRng.DeriveStream(1, "chance");
        var below200 = 0;
        for (var i = 0; i < 10_000; i++)
            if (rng.NextPerMille() < 200)
                below200++;
        Assert.InRange(below200, 1800, 2200); // 20% ± 2pp
    }
}
