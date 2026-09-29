using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// <see cref="CombatPtr.Normalize"/> grew an allocation-free fast path on 2026-09-16 (it runs per hit
/// and, via the world HUD walk, per entity per frame). These pin the CONTRACT that makes the fast path
/// safe: it must return exactly what the general path would, for every shape the general path handles.
/// </summary>
public class CombatPtrTests
{
    [Theory]
    [InlineData("1a2b3c", "1a2b3c")]
    [InlineData("1A2B3C", "1a2b3c")]
    [InlineData("0x1A2B3C", "1a2b3c")]
    [InlineData("0X1a2b3c", "1a2b3c")]
    [InlineData("entity:1A2B3C", "1a2b3c")]
    [InlineData("entity:0x1A2B3C", "1a2b3c")]
    [InlineData("ENTITY:1a2b3c", "1a2b3c")]
    [InlineData("  1A2B3C  ", "1a2b3c")]
    [InlineData("  entity:0x1A2B3C ", "1a2b3c")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_strips_prefixes_trims_and_lowercases(string? input, string expected) =>
        Assert.Equal(expected, CombatPtr.Normalize(input));

    [Fact]
    public void An_already_canonical_ptr_is_returned_as_the_same_instance()
    {
        // ⚡ The whole point of the fast path: no allocation when there is nothing to change. Reference
        // equality is the only way to assert that from a test — value equality passes either way.
        var canonical = string.Concat("1a2b", "3c4d");   // not interned by the compiler
        Assert.Same(canonical, CombatPtr.Normalize(canonical));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        foreach (var raw in new[] { "entity:0xABCD", "  0xff  ", "ABC", "abc" })
        {
            var once = CombatPtr.Normalize(raw);
            Assert.Equal(once, CombatPtr.Normalize(once));
        }
    }

    [Fact]
    public void A_non_hex_ptr_still_normalizes_rather_than_taking_the_fast_path()
    {
        // The fast path's test is "already lower-case hex". Anything else — including a ptr with a
        // non-hex character, which callers should never produce but which the general path has always
        // accepted — must still go through trim/strip/lower.
        Assert.Equal("zz9", CombatPtr.Normalize("entity:0xZZ9"));
        Assert.Equal("zz9", CombatPtr.Normalize(" ZZ9 "));
    }

    [Theory]
    [InlineData("entity:0xAB", "ab")]
    [InlineData("AB", "ab")]
    [InlineData("ab", " ab ")]
    public void EqualsPtr_compares_normalized(string a, string b) =>
        Assert.True(CombatPtr.EqualsPtr(a, b));

    [Fact]
    public void EqualsPtr_separates_different_ptrs() =>
        Assert.False(CombatPtr.EqualsPtr("entity:0xAB", "ac"));
}
