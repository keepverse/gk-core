using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// E25 (completeness-audit.md finding B3): <c>AllCombatChannelIds</c> rebuilt 84 interpolated strings
/// on every read (84 was the family count at the time; 196 today, reconcile pass F6 2026-08-25 — the
/// caching this test proves is what keeps that growth free), and <c>StatusStatPayload.IsKnownChannel</c>
/// did an O(n) scan of a freshly allocated list per channel parsed. Both are now cached by reference to
/// <see cref="ElementTable.Current"/>,
/// which is always a *new* immutable instance on a swap — never mutated in place — so a reference
/// check is exactly as fresh as a rebuild.
/// </summary>
public class ChannelCacheTests
{
    [Fact]
    public void Repeated_reads_with_no_roster_change_return_the_same_list_instance()
    {
        using var _ = ElementTable.UseScoped(ElementTable.Shipped());

        var first = DerivedStatChannels.AllCombatChannelIds;
        var second = DerivedStatChannels.AllCombatChannelIds;

        Assert.Same(first, second);
    }

    [Fact]
    public void A_roster_swap_invalidates_the_cache_and_the_output_changes()
    {
        var shipped = ElementTable.Shipped();
        var withVoid = shipped.Elements.Append(new ElementRow("void", "Void", shipped.Elements.Count, true)).ToArray();
        var sevenElement = new ElementTable(withVoid, shipped.CombatRows, shipped.ShieldRows);

        using (ElementTable.UseScoped(shipped))
        {
            var six = DerivedStatChannels.AllCombatChannelIds;
            Assert.DoesNotContain(six, id => id.EndsWith(".void", StringComparison.Ordinal));

            using (ElementTable.UseScoped(sevenElement))
            {
                var seven = DerivedStatChannels.AllCombatChannelIds;
                Assert.Contains(seven, id => id.EndsWith(".void", StringComparison.Ordinal));
                Assert.NotSame(six, seven);
            }

            // Restored scope reads the six-element cache again — proven by VALUE, not by same-
            // instance, since the outer scope's cache slot may have been evicted by the inner swap.
            var sixAgain = DerivedStatChannels.AllCombatChannelIds;
            Assert.DoesNotContain(sixAgain, id => id.EndsWith(".void", StringComparison.Ordinal));
            Assert.Equal(six, sixAgain);
        }
    }

    [Fact]
    public void The_cached_output_is_byte_identical_to_an_uncached_rebuild()
    {
        using var _ = ElementTable.UseScoped(ElementTable.Shipped());

        var cached = DerivedStatChannels.AllCombatChannelIds;
        var rebuilt = DerivedStatChannels.BuildAllCombatChannelIds(
            ElementTable.Current.Elements.Where(e => e.Enabled).Select(e => e.ElementId));

        Assert.Equal(rebuilt, cached);
    }

    [Fact]
    public void IsCombatChannel_agrees_with_AllCombatChannelIds_for_every_generated_id()
    {
        using var _ = ElementTable.UseScoped(ElementTable.Shipped());

        foreach (var id in DerivedStatChannels.AllCombatChannelIds)
            Assert.True(DerivedStatChannels.IsCombatChannel(id), id);

        Assert.False(DerivedStatChannels.IsCombatChannel("not.a.real.channel"));
    }

    [Fact]
    public void IsCombatChannel_also_invalidates_on_a_roster_swap()
    {
        var shipped = ElementTable.Shipped();
        var withVoid = shipped.Elements.Append(new ElementRow("void", "Void", shipped.Elements.Count, true)).ToArray();
        var sevenElement = new ElementTable(withVoid, shipped.CombatRows, shipped.ShieldRows);

        using (ElementTable.UseScoped(shipped))
            Assert.False(DerivedStatChannels.IsCombatChannel("combat.power.void"));

        using (ElementTable.UseScoped(sevenElement))
            Assert.True(DerivedStatChannels.IsCombatChannel("combat.power.void"));
    }
}
