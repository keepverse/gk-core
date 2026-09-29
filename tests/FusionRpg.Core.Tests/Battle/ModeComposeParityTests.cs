using System.Linq;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// D4 (`battle-mode-parity`, solid-remediation T3.3) — the same specimen composes the same way
/// whichever mode it is standing in.
///
/// <para><b>What was wrong.</b> `DistrictAssaultResolver.BuildAnimateSetups` built every legion member
/// with flat, level-derived stats and no `HubInputs` at all, while `WebMatchService.BuildSquad` built
/// the same specimen from its real allocation. Its own comment named the gap and the reason honestly —
/// the resolver is Core-only and cannot reach `RpgStore` without crossing the DAL boundary — but
/// stopped at naming it. The missing piece was the inversion: Core declares the need, the Data layer
/// injects the store-backed lookup at its own call site.</para>
///
/// <para>These assert the <b>seam's contract</b>, not any magnitude. The numbers are tuning-owned and a
/// balance pass must not turn this file red; what must hold is that a provider's inputs reach the
/// setup, and that absent one nothing changes.</para>
/// </summary>
[Trait("VerificationId", "core.battle-mode-parity")]
public class ModeComposeParityTests
{
    const string Specimen = "unique:parity-probe";

    /// <summary>Any real species id — the catalog is content and its membership is a reading.</summary>
    static string AnySpecies => FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All[0].SpeciesId;

    /// <summary>Any real aptitude id — the catalog is a closed vocabulary the code owns, and which
    /// member this test picks is not part of what it asserts.</summary>
    static string AnyAptitude => AptitudeCatalog.All[0].Id;

    static WorldEntity Legion(string? instanceId) => new()
    {
        EntityId = "legion:1",
        Kind = WorldEntityKind.Legion,
        Members = new[]
        {
            new WorldEntityMember
            {
                InstanceId = instanceId,
                SpeciesId = AnySpecies,
                Level = 12,
                Hp = 500,
            }
        }
    };

    static BattleHubInputs InputsWithAllocation() => new()
    {
        Aptitude = AptitudeAllocation.Single(AllocationScope.UniqueCreature, AnyAptitude, 7)
    };

    /// <summary>
    /// Loads the real shipped aptitude config rather than depending on some earlier test in the run
    /// having configured the hub — the exact accident `DominanceGuardTests` records having made once,
    /// where it "only passed by test-ordering accident". Highest `aptitudes.v*.json`, never a pinned
    /// literal, so a publish does not break a test that only wants whatever ships.
    /// </summary>
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

    /// <summary>
    /// The falsifier for the seam itself: with a provider wired, a member carrying an `InstanceId`
    /// reaches its setup with those inputs. Before T3.3 there was no way to supply them at all.
    /// </summary>
    [Fact]
    public void A_wired_provider_reaches_the_legion_members_setup()
    {
        var resolver = new DistrictAssaultResolver { HubInputsFor = _ => InputsWithAllocation() };

        var setups = resolver.BuildAnimateSetups(Legion(Specimen), "squad", new List<string>());

        var member = Assert.Single(setups);
        Assert.Equal(Specimen, member.SpecimenId);
        Assert.NotNull(member.HubInputs);
        Assert.NotNull(member.HubInputs!.Aptitude);
    }

    /// <summary>
    /// A member with no `InstanceId` is a non-player force or a guard — its own field doc says so — and
    /// has nothing to look up. It must compose exactly as it did before the seam existed, which is what
    /// keeps every existing siege golden still.
    /// </summary>
    [Fact]
    public void A_member_with_no_instance_id_is_left_exactly_as_it_was()
    {
        var resolver = new DistrictAssaultResolver { HubInputsFor = _ => InputsWithAllocation() };

        var setups = resolver.BuildAnimateSetups(Legion(instanceId: null), "squad", new List<string>());

        var member = Assert.Single(setups);
        Assert.Null(member.HubInputs);
    }

    /// <summary>
    /// And with no provider at all — the `Instance` singleton every pre-T3.3 caller used — nothing
    /// changes either. The seam is inert by default, so it cannot have moved a siege that has not opted
    /// in.
    /// </summary>
    [Fact]
    public void The_store_free_resolver_still_composes_from_level_alone()
    {
        var setups = DistrictAssaultResolver.Instance.BuildAnimateSetups(Legion(Specimen), "squad", new List<string>());

        var member = Assert.Single(setups);
        Assert.Null(member.HubInputs);
    }

    /// <summary>
    /// The parity claim itself, at the level this test can reach without a store: the same allocation
    /// composes to the same derived snapshot whether it arrived through a siege setup or a web-match
    /// setup. `BattleHubCompose` is the one composer both go through, so this asserts that routing a
    /// specimen's inputs through either path lands on identical numbers.
    /// </summary>
    [Fact]
    public void The_same_inputs_compose_identically_whichever_mode_built_the_setup()
    {
        ConfigureShippedAptitudeTuning();
        var inputs = InputsWithAllocation();

        var siege = SetupFor("siege:0", inputs);
        var web = SetupFor("web:0", inputs);

        var siegeDerived = BattleHubCompose.Compose(siege);
        var webDerived = BattleHubCompose.Compose(web);

        foreach (var channel in new[]
                 {
                     DerivedStatChannels.CombatPowerOmni,
                     DerivedStatChannels.CombatDefenseOmni,
                     DerivedStatChannels.CombatAccuracyOmni,
                 })
        {
            Assert.Equal(siegeDerived.Get(channel), webDerived.Get(channel));
        }
    }

    static BattleActorSetup SetupFor(string key, BattleHubInputs inputs) => new()
    {
        Key = key,
        Side = "squad",
        SpeciesId = AnySpecies,
        TypeId = 1,
        Level = 12,
        MaxHp = BattleRuleset.BaseHp(12),
        Atk = BattleRuleset.BaseAtk(12),
        Defense = BattleRuleset.BaseDefense(12),
        HubInputs = inputs,
    };
}
