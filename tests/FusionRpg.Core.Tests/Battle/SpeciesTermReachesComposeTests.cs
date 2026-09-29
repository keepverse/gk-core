using System.Linq;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// `solid-remediation` T4.4 (S2, S7) — the species term reaches the battle compose, through
/// `ActorHub`, carrying a GG-49 grammar id.
///
/// <para><b>S2</b> was "battle receives no species allocation at all": the world-turn provider built
/// `Aptitude = commander + specimen` and had no species term. <b>S7</b> was "a plant type at level 4
/// composes exactly as one at level 1" — the same defect seen from the other end, because the species
/// allocation is where a species level shows up. Both close with one term, which is why they share a
/// task.</para>
///
/// <para><b>What this module does NOT decide</b> (the audit says so explicitly): what a species level
/// grants. That mapping is `species-progression`'s, and it already exists —
/// `SpeciesAllocation.Baseline(shares, level, tuning)`. This proves the term arrives and that a
/// different allocation composes differently, never what the magnitudes should be.</para>
/// </summary>
[Trait("VerificationId", "core.species-term-compose")]
public class SpeciesTermReachesComposeTests
{
    // Loads the real shipped aptitude config rather than depending on an earlier test in the run
    // having configured the hub — the test-ordering accident DominanceGuardTests records making once.
    static SpeciesTermReachesComposeTests() => ConfigureShippedAptitudeTuning();

    static void ConfigureShippedAptitudeTuning()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var tuningDir = Path.Combine(dir.FullName, "data", "tuning");
            if (Directory.Exists(tuningDir))
            {
                var newest = Directory.GetFiles(tuningDir, "aptitudes.v*.json")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (newest is not null)
                {
                    AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(newest)));
                    return;
                }
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no data/tuning/aptitudes.v*.json above the test output");
    }

    static BattleActorSetup Actor(BattleHubInputs? inputs) => new()
    {
        Key = "squad:0",
        Side = "squad",
        SpeciesId = "fumeshroom",
        TypeId = 10_001,
        Level = 6,
        MaxHp = BattleRuleset.BaseHp(6),
        Atk = BattleRuleset.BaseAtk(6),
        Defense = BattleRuleset.BaseDefense(6),
        HubInputs = inputs,
    };

    static AptitudeAllocation Species(long points) =>
        AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", points);

    [Fact]
    public void A_species_allocation_reaching_the_compose_changes_what_it_composes()
    {
        // S2/S7's acceptance in its observable form. Two composes that differ ONLY by the species
        // term: if the term never reached the compose, these would be identical — which is exactly the
        // state the register describes.
        var without = BattleHubCompose.Compose(Actor(new BattleHubInputs { Aptitude = AptitudeAllocation.Empty }));
        var with = BattleHubCompose.Compose(Actor(new BattleHubInputs { Aptitude = Species(40) }));

        Assert.NotEqual(
            without.Get(DerivedStatChannels.CombatPowerOmni, 0),
            with.Get(DerivedStatChannels.CombatPowerOmni, 0));
    }

    [Fact]
    public void A_different_share_distribution_composes_differently()
    {
        // What the species term can and cannot move, established by reading AptitudeResolver rather
        // than assuming: it reads `allocation.Share(edge.Source)` — a SHARE, not a point count — and
        // the magnitude comes from `pTheta = ladder.Value(theta)`. So the allocation steers WHERE the
        // power goes; theta decides HOW MUCH there is.
        var allMight = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 40);
        var splitAcrossTwo =
            AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 20)
            + AptitudeAllocation.Single(AllocationScope.CreatureType, "Agility", 20);

        var focused = BattleHubCompose.Compose(Actor(new BattleHubInputs { Aptitude = allMight }));
        var spread = BattleHubCompose.Compose(Actor(new BattleHubInputs { Aptitude = splitAcrossTwo }));

        Assert.True(
            focused.Get(DerivedStatChannels.CombatPowerOmni, 0)
            > spread.Get(DerivedStatChannels.CombatPowerOmni, 0));
    }

    [Fact]
    public void Point_count_alone_does_not_move_the_compose_which_is_why_S7_is_not_closed_here()
    {
        // The negative result this task actually produced, pinned so nobody re-derives it. Ten points
        // and two hundred points in the SAME single share are both share = 1.0, so they compose
        // identically. A species LEVEL enters `SpeciesAllocation.Baseline(shares, level, tuning)` as a
        // point count, and battle's theta is the MEMBER's level (`setup.ThetaActor ?? setup.Level`),
        // never the species level.
        //
        // Therefore S7 — "a plant type at level 4 composes exactly as one at level 1" — is NOT closed
        // by routing this term. Closing it means giving a species level a path into theta or a channel,
        // which is deciding what a species level grants, and the audit assigns that to
        // `species-progression`, explicitly not to this module.
        var ten = BattleHubCompose.Compose(Actor(new BattleHubInputs
        {
            Aptitude = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 10),
        }));
        var twoHundred = BattleHubCompose.Compose(Actor(new BattleHubInputs
        {
            Aptitude = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 200),
        }));

        Assert.Equal(
            ten.Get(DerivedStatChannels.CombatPowerOmni, 0),
            twoHundred.Get(DerivedStatChannels.CombatPowerOmni, 0));
    }

    [Fact]
    public void The_species_term_arrives_through_ActorHub_carrying_a_GG49_grammar_id()
    {
        // The acceptance's own wording: "through ActorHub with a GG-49 grammar id". Not a private fold
        // beside the Hub — the term is an AptitudeAllocation resolved by the registered
        // AptitudeSubsystem, and AptitudeResolver stamps every modifier it emits with
        // ContributionSourceIds.Aptitude(share).
        var mods = new List<DerivedModifier>();
        new AptitudeSubsystem(
                AptitudeTuningHub.Tuning,
                new PowerLadder(PowerTuningHub.Tuning),
                allocation: _ => Species(40))
            .ContributeDerived(
                new StatContextFactory().ForBattle(
                    "squad:0",
                    new EntityBaseline { MaxHp = 100, Atk = 10 },
                    side: StatSide.Plant,
                    typeId: 10_001),
                mods);

        Assert.NotEmpty(mods);

        // Every contribution is attributable — a blank source id is the thing GG-49 exists to prevent,
        // because an unattributable number cannot be explained on the sheet or traced in an audit.
        Assert.All(mods, m => Assert.False(string.IsNullOrWhiteSpace(m.SourceId)));

        // And attributable specifically to the aptitude grammar, not some other source's prefix.
        Assert.Contains(mods, m => m.SourceId!.StartsWith("aptitude.", StringComparison.Ordinal));
    }
}
