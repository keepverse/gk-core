using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>
/// Golden matrix generated from ElementRoster (exhaustive by construction — defeats the
/// fail-open default) plus the seed-equality contract vs ElementRingMatrix comparing UNIT
/// relations, not K-scaled shares (shield-system-spec.md §2.4/§7).
/// </summary>
public class ShieldElementMatrixTests
{
    // Independent statement of the seed rules: ring fire → ice → earth → air → fire,
    // light ⇄ dark mutual counter, light/dark neutral vs the ring, same → 0.
    static readonly ElementTypeId[] Ring =
        { ElementTypeId.Fire, ElementTypeId.Ice, ElementTypeId.Earth, ElementTypeId.Air };

    static int ExpectedUnit(ElementTypeId a, ElementTypeId d)
    {
        if (a == d) return 0;
        var aLd = a is ElementTypeId.Light or ElementTypeId.Dark;
        var dLd = d is ElementTypeId.Light or ElementTypeId.Dark;
        if (aLd && dLd) return 1;             // mutual counter (a != d here)
        if (aLd || dLd) return 0;             // light/dark neutral vs ring
        var ai = Array.IndexOf(Ring, a);
        var di = Array.IndexOf(Ring, d);
        if (di == (ai + 1) % Ring.Length) return 1;   // next in cycle → strong
        if (ai == (di + 1) % Ring.Length) return -1;  // previous in cycle → weak
        return 0;
    }

    public static IEnumerable<object[]> AllPairs() =>
        from a in ElementRoster.Concrete
        from d in ElementRoster.Concrete
        select new object[] { a, d };

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Full_roster_matrix_matches_seed_rules(ElementTypeId attacker, ElementTypeId shieldElement)
    {
        Assert.Equal(ExpectedUnit(attacker, shieldElement),
            ShieldElementMatrix.RelationUnit(attacker, shieldElement));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Seed_equals_ring_matrix_relations(ElementTypeId attacker, ElementTypeId shieldElement)
    {
        // Documents the intentional v1 starting point: shield relations == combat relations.
        // Compares unit relations — NOT RelationShare, which bakes MatchupShareK in.
        var ring = ElementRingMatrix.GetRelation(attacker, shieldElement);
        var expected = ring switch
        {
            ElementMatchupRelation.Strong => 1,
            ElementMatchupRelation.Weak => -1,
            _ => 0
        };
        Assert.Equal(expected, ShieldElementMatrix.RelationUnit(attacker, shieldElement));
    }

    [Fact]
    public void Policy_constants_match_owner_decision_8_and_9()
    {
        Assert.Equal(250, ShieldPolicy.MatchupShareKPm);
        Assert.Equal(100, ShieldPolicy.ChipFloorKPm);
        Assert.Equal(3000, ShieldPolicy.PenCapKPm);
        Assert.Equal(3, ShieldPolicy.MaxShieldsPerActor);
        Assert.True(ShieldPolicy.PriorityInnate < ShieldPolicy.PrioritySkill
                    && ShieldPolicy.PrioritySkill < ShieldPolicy.PriorityAura,
            "Drain order is outer-to-core: aura before skill before innate (lower drains first).");
    }
}
