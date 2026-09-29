using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// D16 (combat-math-dedup Task 12). <see cref="DerivedStatChannels.CombatChannelFamilies"/> is the
/// one declaration of the 28 combat channel families; <c>PowerTables</c>' category overrides are a
/// PARTITION of it, not a second list. The assertions below are what makes a new family impossible
/// to add to one side and miss the other: the union must equal the family list, the sides must be
/// disjoint, every role-carrying family must land on its named side, and the 12 role-less families
/// must land on the side domain meaning gives them.
/// </summary>
public class CoefficientTableCategoryOverrideTests
{
    static IReadOnlyList<PowerCategoryOverrideRow> Rows() =>
        PowerTables.Authored().CategoryOverrides;

    static HashSet<string> Side(PowerCategory category) =>
        Rows().Where(r => r.Category == category).Select(r => r.ChannelFamily)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void The_two_sides_partition_every_combat_channel_family()
    {
        var offense = Side(PowerCategory.Offense);
        var survivability = Side(PowerCategory.Survivability);
        var families = DerivedStatChannels.CombatChannelFamilies.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(families, offense.Union(survivability).ToHashSet(StringComparer.Ordinal));
        Assert.Empty(offense.Intersect(survivability));
        Assert.Equal(families.Count, offense.Count + survivability.Count);
    }

    [Fact]
    public void Every_role_carrying_family_lands_on_the_side_its_role_names()
    {
        var byFamily = Rows().ToDictionary(r => r.ChannelFamily, r => r.Category, StringComparer.Ordinal);

        Assert.NotEmpty(DerivedStatChannels.CombatFamilyRole);
        foreach (var (family, role) in DerivedStatChannels.CombatFamilyRole)
        {
            var expected = role == "attacker" ? PowerCategory.Offense : PowerCategory.Survivability;
            Assert.True(byFamily.TryGetValue(family, out var actual), family);
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// The 12 families with no <c>CombatFamilyRole</c> entry, pinned by side. This is a closed
    /// classification a human edits (spec-standing-coeff-tuning.md D4 finding 5), so the literal set
    /// is deliberate: it is the assertion that bites if a non-role family is silently reclassified or
    /// dropped. <c>combat.dodge</c> is the spec's own worked example of a purely defender stat.
    /// </summary>
    [Fact]
    public void The_twelve_role_less_families_land_on_the_side_domain_meaning_gives_them()
    {
        var byFamily = Rows().ToDictionary(r => r.ChannelFamily, r => r.Category, StringComparer.Ordinal);
        var roleLess = DerivedStatChannels.CombatChannelFamilies
            .Where(f => !DerivedStatChannels.CombatFamilyRole.ContainsKey(f))
            .ToHashSet(StringComparer.Ordinal);

        var expectedOffense = new HashSet<string>(StringComparer.Ordinal)
        {
            "combat.power", "combat.crit.rate", "combat.crit.damage", "combat.accuracy",
            DerivedStatChannels.CombatShieldPenPrefix
        };
        var expectedSurvivability = new HashSet<string>(StringComparer.Ordinal)
        {
            "combat.defense", "combat.crit.resist", "combat.crit.resist.damage", "combat.dodge",
            DerivedStatChannels.CombatShieldCapacityPrefix, DerivedStatChannels.CombatShieldToughnessPrefix,
            DerivedStatChannels.CombatShieldRegenPrefix
        };

        Assert.Equal(expectedOffense.Union(expectedSurvivability).ToHashSet(StringComparer.Ordinal), roleLess);
        foreach (var family in expectedOffense)
            Assert.Equal(PowerCategory.Offense, byFamily[family]);
        foreach (var family in expectedSurvivability)
            Assert.Equal(PowerCategory.Survivability, byFamily[family]);
    }
}
