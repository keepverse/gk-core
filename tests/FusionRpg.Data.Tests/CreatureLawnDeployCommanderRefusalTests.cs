using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>creature-lawn-deploy T1.1: the active Patron must never also get free lawn combat value on
/// top of its aura (RpgStore.Fusion.cs:354 already refuses it as a fusion sacrifice for the identical
/// reason) — and, since commander-roster EP3.6, the specimen holding THIS RUN's commander seat is refused
/// by name with `commander.cannot-deploy`, before the phase check, so the reason states the rule rather
/// than a phase. A role-holder that is not leading this run deploys like any unique.</summary>
public class CreatureLawnDeployCommanderRefusalTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CreatureLawnDeployCommanderRefusalTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // DeployMode != HypnoAlly: this file tests Patron refusal (T1.1), a concern unrelated to
    // DeployMode (T1.4) — excluding HypnoAlly keeps it robust to which species happens to sort first,
    // rather than incidentally also exercising T1.4's own deploy.hypno-ally-not-implemented refusal.
    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Side == "zombie" && s.Acquisition != CreatureAcquisition.CaptureOnly
            && s.DeployMode != CreatureDeployMode.HypnoAlly);

    string Mint()
    {
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = Species.SpeciesId,
            Side = Species.Side,
            GameTypeId = Species.GameTypeId,
            Rarity = Species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = Species.ElementPrimary.ToElementId(),
            ElementSecondary = Species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { Species.TraitPool[0] },
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    [Fact]
    public void The_active_patron_refuses_deploy_with_a_named_reason()
    {
        var patron = Mint();
        var set = _store.SetPatron(1, patron, "patron-corr-1");
        Assert.True(set.Ok, set.Reason);

        var deploy = _store.TryBeginUniqueDeploy(patron, "deploy-corr-1");

        Assert.False(deploy.Ok);
        Assert.Equal("patron.cannot-deploy", deploy.Reason);
        Assert.False(deploy.Queued);
    }

    [Fact]
    public void A_creature_that_is_not_the_active_patron_deploys_normally()
    {
        var patron = Mint();
        _store.SetPatron(1, patron, "patron-corr-2");
        var other = Mint();

        var deploy = _store.TryBeginUniqueDeploy(other, "deploy-corr-2");

        Assert.True(deploy.Ok, deploy.Reason);
        Assert.True(deploy.Queued);
    }

    [Fact]
    public void Switching_the_patron_away_lets_the_old_patron_deploy_again()
    {
        var oldPatron = Mint();
        _store.AwardSouls(1, 500, "seed", "patron-switch-bank");
        _store.SetPatron(1, oldPatron, "patron-corr-3");
        var newPatron = Mint();
        var switched = _store.SetPatron(1, newPatron, "patron-corr-4");
        Assert.True(switched.Ok, switched.Reason);

        var deploy = _store.TryBeginUniqueDeploy(oldPatron, "deploy-corr-3");

        Assert.True(deploy.Ok, deploy.Reason);
        Assert.True(deploy.Queued);
    }

    [Fact]
    public void A_seated_commander_is_refused_by_name_and_a_role_holder_that_is_not_leading_deploys()
    {
        var previous = FusionRpg.Core.Commanders.CommanderDirectoryHub.Current;
        var authored = FusionRpg.Core.Commanders.DataCommanderDirectory.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "commanders", "_registry", "default-commanders.v1.json")));
        FusionRpg.Core.Commanders.CommanderDirectoryHub.Configure(
            authored.WithSource(new FusionRpg.Core.Commanders.UniqueCommanderSource(authored, _store)));
        try
        {
            var empire = new FusionRpg.Core.Saves.EmpireRef(
                new FusionRpg.Core.Saves.SaveId(1), _store.HumanEmpireOf(1));

            var leading = Mint();
            Assert.True(_store.GrantCommanderRole(empire, leading).Ok);
            var seated = _store.SeatLeadingCommander(
                FusionRpg.Core.Commanders.UniqueCommanderSource.UniquePrefix + leading, "m-canary");
            Assert.True(seated.Seated, seated.Reason);

            var refused = _store.TryBeginUniqueDeploy(leading, "deploy-corr-canary");
            Assert.False(refused.Ok);
            Assert.Equal("commander.cannot-deploy", refused.Reason);

            var idle = Mint();
            Assert.True(_store.GrantCommanderRole(empire, idle).Ok);
            var deploy = _store.TryBeginUniqueDeploy(idle, "deploy-corr-idle");
            Assert.True(deploy.Ok, deploy.Reason);
        }
        finally
        {
            FusionRpg.Core.Commanders.CommanderDirectoryHub.Configure(previous);
        }
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
