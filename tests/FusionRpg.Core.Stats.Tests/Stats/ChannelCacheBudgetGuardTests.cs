using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// E25's standing guard, beside E13's <c>AtomBenchGuardTests</c>: <c>AllCombatChannelIds</c> must stay
/// cached. Not a strict CI gate on absolute nanoseconds (compose is not E13's hot path) — an
/// allocation trip-wire, which is what actually regresses if the cache is ever accidentally removed
/// (back to 84 fresh string allocations per call, and a fresh 84-entry <c>List</c> to hold them).
/// </summary>
public class ChannelCacheBudgetGuardTests
{
    readonly ITestOutputHelper _out;

    public ChannelCacheBudgetGuardTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Repeated_reads_with_no_roster_change_allocate_close_to_nothing()
    {
        using var scope = ElementTable.UseScoped(ElementTable.Shipped());

        // Warm: first call in this scope always allocates (cache miss for this table reference).
        _ = DerivedStatChannels.AllCombatChannelIds;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        var sink = 0;
        for (var i = 0; i < 10_000; i++)
            sink += DerivedStatChannels.AllCombatChannelIds.Count;

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var perCall = allocated / 10_000.0;

        _out.WriteLine($"allocated over 10^4 cached reads: {allocated} bytes ({perCall:F2} B/call, sink {sink})");

        // A single rebuild (84 strings + a List<string> + a HashSet<string>) is on the order of
        // several KB; if the cache were removed this would be ~84 allocations * 10^4 calls instead
        // of ~0. 64 bytes/call is generous headroom over "should be ~0" without being a false alarm
        // on GC/JIT noise from ITestOutputHelper or the loop itself.
        Assert.True(perCall < 64,
            $"{perCall:F2} bytes/call suggests the cache is not holding — expected close to 0");
    }

    [Fact]
    public void IsCombatChannel_also_allocates_close_to_nothing_once_warm()
    {
        using var scope = ElementTable.UseScoped(ElementTable.Shipped());
        var warm = DerivedStatChannels.AllCombatChannelIds; // warm

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        var hits = 0;
        for (var i = 0; i < 10_000; i++)
            if (DerivedStatChannels.IsCombatChannel("combat.power.omni")) hits++;

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var perCall = allocated / 10_000.0;

        _out.WriteLine($"allocated over 10^4 IsCombatChannel calls: {allocated} bytes ({perCall:F2} B/call, hits {hits})");
        Assert.Equal(10_000, hits);
        Assert.True(perCall < 16, $"{perCall:F2} bytes/call suggests a per-call allocation crept back in");
    }
}
