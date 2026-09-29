using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P1.2 (AptitudeAllocation + AllocationScope) and P1.3 (DominantPosture).</summary>
public class AptitudeAllocationTests
{
    [Fact]
    public void EmptyAllocationHasAllZeroShares_neverOneTwelfth()
    {
        foreach (var apt in AptitudeCatalog.All)
        {
            Assert.Equal(0, AptitudeAllocation.Empty.Total(apt.Id));
            Assert.Equal(0.0, AptitudeAllocation.Empty.Share(apt.Id));
        }
        Assert.Equal(0, AptitudeAllocation.Empty.GrandTotal());
    }

    [Fact]
    public void AdditionIsCommutative()
    {
        var a = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 30);
        var b = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 70);

        var ab = a + b;
        var ba = b + a;

        foreach (var apt in AptitudeCatalog.All)
            Assert.Equal(ab.Total(apt.Id), ba.Total(apt.Id));
        Assert.Equal(ab.GrandTotal(), ba.GrandTotal());
    }

    [Fact]
    public void AdditionIsAssociative()
    {
        var a = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 10);
        var b = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 20);
        var c = AptitudeAllocation.Single(AllocationScope.Aspect, "Might", 30);

        var left = (a + b) + c;
        var right = a + (b + c);
        Assert.Equal(60, left.Total("Might"));
        Assert.Equal(60, right.Total("Might"));
    }

    [Fact]
    public void SharesSumToOneWhenNonEmpty()
    {
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 25)
                  + AptitudeAllocation.Single(AllocationScope.Aspect, "Vigor", 25)
                  + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Focus", 50);

        var sum = alloc.Shares().Values.Sum();
        Assert.Equal(1.0, sum, 12);
    }

    [Fact]
    public void ScopesSumBeforeShare()
    {
        // Two DIFFERENT scopes both fund Might, nothing else funded anywhere -- share must read 100%
        // off the SUM across scopes, not off either scope considered alone.
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50)
                  + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 50);

        Assert.Equal(100, alloc.Total("Might"));
        Assert.Equal(1.0, alloc.Share("Might"));
        Assert.Equal(0, alloc.Total("Vigor"));
    }

    // ── ShareWithinScope (SP1.6, species-layer-delivery step 6.1's own prerequisite) ────────────

    [Fact]
    public void ShareWithinScope_dividesByThatScopesOwnTotal_neverTheGrandTotal()
    {
        // Two DIFFERENT scopes both fund Might, evenly -- ScopesSumBeforeShare (above) proves Share
        // reads 100% off the sum. ShareWithinScope must read the OPPOSITE: each scope sees only its
        // own 50, against its own scope-local total of 50 (nothing else funded in either scope) -- 1.0
        // in EACH scope, not 0.5.
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50)
                  + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 50);

        Assert.Equal(1.0, alloc.ShareWithinScope(AllocationScope.Commander, "Might"));
        Assert.Equal(1.0, alloc.ShareWithinScope(AllocationScope.UniqueCreature, "Might"));
        Assert.Equal(0.0, alloc.ShareWithinScope(AllocationScope.CreatureType, "Might"));
    }

    [Fact]
    public void ShareWithinScope_splitsCorrectlyWhenOneScopeFundsTwoAptitudes()
    {
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 30)
                  + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 70)
                  + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 1000); // a different scope must not leak in

        Assert.Equal(0.3, alloc.ShareWithinScope(AllocationScope.Commander, "Might"), 12);
        Assert.Equal(0.7, alloc.ShareWithinScope(AllocationScope.Commander, "Vigor"), 12);
    }

    [Fact]
    public void ShareWithinScope_emptyScopeReadsZero_neverOneTwelfth()
    {
        var alloc = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 100);

        foreach (var apt in AptitudeCatalog.All)
            Assert.Equal(0.0, alloc.ShareWithinScope(AllocationScope.Commander, apt.Id));
    }

    [Fact]
    public void ShareWithinScope_equalsShare_forASingleScopeAllocation()
    {
        // "For a single-scope allocation it equals Share" (SP1.6's own acceptance line): when every
        // point sits in one scope, that scope's own total IS the grand total, so the two reads agree.
        var alloc = AptitudeAllocation.Single(AllocationScope.Aspect, "Might", 40)
                  + AptitudeAllocation.Single(AllocationScope.Aspect, "Vigor", 60);

        foreach (var apt in AptitudeCatalog.All)
            Assert.Equal(alloc.Share(apt.Id), alloc.ShareWithinScope(AllocationScope.Aspect, apt.Id), 12);
    }

    [Fact]
    public void ShareWithinScope_neverChangesShareOrTotalForScope()
    {
        // "No existing reader changes" (SP1.6's own acceptance line) -- a pure addition beside the
        // existing reads, not a rewrite of either.
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50)
                  + AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 50);

        Assert.Equal(1.0, alloc.Share("Might"));
        Assert.Equal(50, alloc.TotalForScope(AllocationScope.Commander));
        Assert.Equal(50, alloc.TotalForScope(AllocationScope.UniqueCreature));
    }

    [Fact]
    public void ExactAtOneBillion()
    {
        // "exact at Theta = 10^9" -- long arithmetic stays exact far past this; the share ratio is a
        // bounded [0,1] double by design (docs/architecture/numeric-types.md: bounded ratios are exempt from the long-only rule).
        const long huge = 1_000_000_000L;
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", huge)
                  + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", huge);

        Assert.Equal(huge, alloc.Total("Might"));
        Assert.Equal(huge, alloc.Total("Vigor"));
        Assert.Equal(2 * huge, alloc.GrandTotal());
        Assert.Equal(0.5, alloc.Share("Might"), 12);
    }

    [Fact]
    public void OverflowThrowsNeverClamps()
    {
        // Same (scope, aptitude) key on both sides -- the merge path actually adds, rather than just
        // inserting a second independent key.
        var a = AptitudeAllocation.Single(AllocationScope.Commander, "Might", long.MaxValue);
        var b = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 1);
        Assert.Throws<OverflowException>(() => a + b);
    }

    [Fact]
    public void NegativePointsReject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AptitudeAllocation.Single(AllocationScope.Commander, "Might", -1));
    }

    [Fact]
    public void UnknownAptitudeIdRejects()
    {
        Assert.Throws<ArgumentException>(() => AptitudeAllocation.Single(AllocationScope.Commander, "NotAnAptitude", 10));
    }

    [Fact]
    public void ZeroPointsSingleIsEmpty()
    {
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 0);
        Assert.Equal(0, alloc.GrandTotal());
    }

    // ── DominantPosture (P1.3) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void DominantPosture_picksTheClearLeader()
    {
        // Three Force aptitudes funded, nothing else -- Force must lead.
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 40)
                  + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 40)
                  + AptitudeAllocation.Single(AllocationScope.Commander, "Onslaught", 20);
        Assert.Equal(Posture.Force, DominantPosture.Of(alloc));
    }

    [Fact]
    public void DominantPosture_tieReturnsNull()
    {
        var alloc = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50)     // Force
                  + AptitudeAllocation.Single(AllocationScope.Commander, "Agility", 50);   // Finesse
        Assert.Null(DominantPosture.Of(alloc));
    }

    [Fact]
    public void DominantPosture_emptyReturnsNull()
    {
        Assert.Null(DominantPosture.Of(AptitudeAllocation.Empty));
    }

    [Fact]
    public void DominantPosture_isNeverAField()
    {
        // Structural: DominantPosture exposes only a static read, no instance state, no setter --
        // there is nothing an allocation type could hold that would make this a stored field instead
        // of a derived value.
        var t = typeof(DominantPosture);
        Assert.True(t.IsAbstract && t.IsSealed); // static class
        Assert.Empty(t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }
}

/// <summary>species-progression SP3.8 (species-layer-delivery step 6.1's own prerequisite) — the ONE
/// AllocationScope&lt;-&gt;text vocabulary, moved here from RpgStore.ScopeToText/.ScopeFromText.
/// AllocationStoreTests.cs's own ScopeToText/ScopeFromText tests keep proving RpgStore's now-thin
/// delegate stays wired; these prove the SSOT itself.</summary>
public class AllocationScopeTextTests
{
    [Fact]
    public void ToText_andBack_roundTripsForAllFourScopes()
    {
        foreach (var scope in new[]
                 { AllocationScope.Commander, AllocationScope.CreatureType, AllocationScope.Aspect, AllocationScope.UniqueCreature })
            Assert.Equal(scope, AllocationScopeText.FromText(AllocationScopeText.ToText(scope)));
    }

    [Fact]
    public void FromText_unknownScope_rejectsNamingIt()
    {
        var ex = Assert.Throws<ArgumentException>(() => AllocationScopeText.FromText("guildmaster"));
        Assert.Contains("guildmaster", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToText_unknownScope_rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AllocationScopeText.ToText((AllocationScope)999));
    }
}
