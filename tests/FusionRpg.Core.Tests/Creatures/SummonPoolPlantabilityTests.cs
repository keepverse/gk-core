using System.Linq;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// lawn `LW1.2` (<c>lawn-playable/spec-summon-pool-integrity.md</c>): <b>a species the build refuses can
/// never be served by the summon roller.</b>
///
/// <para><b>What was measured.</b> live-probe Task 22: 3 of 5 real pulls (100 souls each) rolled a
/// species this build cannot plant — <c>typeId 261/264/265</c>, all three <c>speciesKind:
/// "excluded"</c> in the corpus. The engine logged <c>不存在该类型的植物</c>, the spawn threw, and the
/// player's souls were gone. The corpus carries 11 such rows, and <c>CreatureAdmission</c> — whose own
/// doc calls itself the one declaring site and refuses a re-derivation in a fourth file — was read by
/// the wave roster, the wild map and the delve, but <b>not</b> by the summon roller, which filtered on
/// the raw <c>Summonable</c> flag.</para>
///
/// <para><b>These tests are the falsifier for that, over the WHOLE real roster.</b> They scope the
/// committed corpus (<c>gk-data/packs/fusion/data/generated/creatures/**</c>, read the way every host reads it) and assert
/// the join closes: every member of every rarity band the roller can draw from is admitted. A count is
/// never asserted — only the join and its non-vacuity.</para>
/// </summary>
[Trait("VerificationId", "core.summon-pool-plantability")]
public class SummonPoolPlantabilityTests
{
    /// <summary>Every committed generated species, through the same reader and mapper the hosts use.</summary>
    static IReadOnlyList<CreatureSpeciesDef> RealRoster()
    {
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "generated", "creatures");
        var roster = new List<CreatureSpeciesDef>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json")
                     .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            roster.Add(ConcreteSpeciesMapper.ToCreatureSpeciesDef(
                ConcreteSpeciesSeedReader.Parse(File.ReadAllText(file))));
        }

        return roster;
    }

    static IDisposable ScopeRealRoster(out IReadOnlyList<CreatureSpeciesDef> roster)
    {
        roster = RealRoster();
        return CreatureSpeciesCatalog.UseScoped(roster);
    }

    [Fact]
    public void No_species_the_build_refuses_can_be_served_by_any_rarity_band()
    {
        using var _ = ScopeRealRoster(out var roster);

        // Non-vacuity, stated as a canary: this corpus is what makes the join below meaningful. If a
        // future regeneration leaves nothing out of play, this guard has nothing to close and SHOULD go
        // red — the correct response is to re-point or delete it, never to widen it.
        Assert.NotEmpty(roster);
        Assert.Contains(roster, CreatureAdmission.IsExcluded);

        foreach (var rarity in Enum.GetValues<CreatureRarity>())
        {
            IReadOnlyList<CreatureSpeciesDef> pool;
            try
            {
                pool = SummonRoller.PoolFor(rarity);
            }
            catch (InvalidOperationException)
            {
                continue; // no band at or below this rung holds an admitted species -- nothing to serve
            }

            foreach (var served in pool)
            {
                Assert.True(CreatureAdmission.ForWave(served),
                    $"rarity band {rarity} would serve '{served.SpeciesId}' (gameTypeId {served.GameTypeId}, "
                    + $"speciesKind '{served.SpeciesKind}'), which CreatureAdmission refuses — the exact "
                    + "defect live-probe Task 22 measured on 3 of 5 real pulls");
            }
        }
    }

    [Fact]
    public void The_three_ids_live_probe_Task_22_measured_are_refused_and_never_served()
    {
        using var _ = ScopeRealRoster(out var roster);

        // The observed case, pinned by id so a regression names the finding it repeats. A canary by
        // design: if the generator ever legitimately un-excludes one of these rows, this goes red and
        // the pin should be re-read against the generator's own reason — never widened silently.
        foreach (var typeId in new[] { 261, 264, 265 })
        {
            var row = roster.Single(s => s.GameTypeId == typeId && string.Equals(s.Side, "plant", StringComparison.Ordinal));
            Assert.True(CreatureAdmission.IsExcluded(row), $"{row.SpeciesId} is no longer marked out of play");
            Assert.False(CreatureAdmission.ForWave(row));

            foreach (var rarity in Enum.GetValues<CreatureRarity>())
            {
                try
                {
                    Assert.DoesNotContain(SummonRoller.PoolFor(rarity), s => s.SpeciesId == row.SpeciesId);
                }
                catch (InvalidOperationException)
                {
                    // No admitted species at or below this rung -- an empty pool cannot serve it either.
                }
            }
        }
    }

    [Fact]
    public void An_excluded_species_carrying_the_summonable_flag_is_still_refused()
    {
        // The predicate itself, on a fixture — no corpus, no population: the flag alone must never be
        // enough, which is precisely what the roller used to assume.
        var excluded = new CreatureSpeciesDef
        {
            SpeciesId = "fixture-excluded",
            Side = "plant",
            GameTypeId = 261,
            CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFor("plant", 261),
            BaseRarity = CreatureRarity.Chaff,
            Acquisition = CreatureAcquisition.Summonable,
            SpeciesKind = CreatureAdmission.ExcludedKind,
        };

        Assert.True(excluded.Acquisition.HasFlag(CreatureAcquisition.Summonable));
        Assert.False(CreatureAdmission.ForWave(excluded));
        Assert.False(CreatureAdmission.ForWildMap(excluded));
        Assert.False(CreatureAdmission.ForDelve(excluded));
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
