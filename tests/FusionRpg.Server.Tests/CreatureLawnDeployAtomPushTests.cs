using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// creature-lawn-deploy T1.2's own named minor gap, closed: the reconcile tests
/// (`FusionRpg.Data.Tests/CreatureLawnDeployTests.cs`) only ever asserted the `effect_binding` ROWS a
/// deploy produces — never the actual <see cref="AtomPushService.Build(OwnerScope, BindContext, ulong,
/// string?, long?, int?, int?)"/> output a live match would receive for that owner. This is the wire-
/// level half, mirroring <see cref="MultiOwnerPushTests"/>'s own "prove the compiled push is correct
/// BEFORE reaching the lawn" pattern for the creature-trait case specifically.
/// </summary>
public class CreatureLawnDeployAtomPushTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly AtomPushService _push;

    public CreatureLawnDeployAtomPushTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _push = new AtomPushService(_store);
        ImportRealTraitSeed();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void ImportRealTraitSeed()
    {
        var root = RepoRoot();
        var files = new[]
        {
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "atoms", "trait-critical-hunter.json"),
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "containers", "trait-critical-hunter.json"),
        }.Select(f => (f, File.ReadAllText(f))).ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var outcome = _store.ImportContent(collected.Content);
        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
    }

    static string RepoRoot() => ContentRoot.Path;

    // DeployMode != HypnoAlly: this file's subject is the atom-push wire payload for a creature-trait
    // binding, unrelated to T1.4's DeployMode — a HypnoAlly pick would refuse the deploy this test needs
    // to succeed before it can even reach the reconcile step under test.
    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Side == "zombie" && s.Acquisition != CreatureAcquisition.CaptureOnly
            && s.DeployMode != CreatureDeployMode.HypnoAlly);

    string MintWithTraits(IReadOnlyList<string> traitIds)
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
            TraitIds = traitIds.ToList(),
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    static BindContext Lawn() => new(RuntimeId.Lawn);

    // A compiled grant's EffectId is the atom's own EffectiveIcdKey (AtomCompiler.cs:77,174,205), NOT
    // AtomRow.DeriveId(family, variant, tier) — trait-critical-hunter.json's real seed content
    // explicitly authors "icdKey": "trait.critical-hunter.crit", so that string is what the compiled
    // grant carries. MultiOwnerPushTests.cs's own hand-authored atoms never set an icdKey, which is why
    // THEIR grants fall back to DeriveId — a different atom shape, not a contradiction.
    const string CriticalHunterEffectId = "trait.critical-hunter.crit";

    [Fact]
    public void A_creature_specimens_reconciled_trait_binding_reaches_the_real_atom_push_payload()
    {
        // P1.5-L (2026-09-07): AtomPushService.Build now rewrites a UniqueActor-scoped grant's durable
        // instance:{id} owner key to the specimen's own live entity:{ptr} (using RpgStore's own
        // durably-tracked LastPtr) — the injector's hot path refuses instance: outright, confirmed live
        // against a real running server + game. A specimen that only reached Deploying (never acked
        // with a real ptr) now correctly contributes no grant; acking it here is what proves the trait
        // binding reaches the wire in its real, acceptable shape.
        var id = MintWithTraits(new[] { "critical-hunter" });
        var deploy = _store.TryBeginUniqueDeploy(id, "deploy-push-1");
        Assert.True(deploy.Ok, deploy.Reason);
        var ack = _store.TryAckUniqueSpawn("deploy-push-1", "0xCRIT1");
        Assert.True(ack.Ok, ack.Reason);

        var payload = _push.Build(new OwnerScope(OwnerKind.UniqueActor, id), Lawn(), matchSeed: 7);

        var grant = Assert.Single(payload.Grants, g => g.EffectId == CriticalHunterEffectId);
        Assert.Equal(EffectOwnerKeys.Entity("CRIT1"), grant.OwnerKey);
        Assert.False(FusionRpg.Core.Stats.StatApplyScope.IsInstanceOwnerKey(grant.OwnerKey));
    }

    [Fact]
    public void A_specimen_with_no_traits_produces_no_grant_for_that_owner()
    {
        var id = MintWithTraits(Array.Empty<string>());
        var deploy = _store.TryBeginUniqueDeploy(id, "deploy-push-2");
        Assert.True(deploy.Ok, deploy.Reason);

        var payload = _push.Build(new OwnerScope(OwnerKind.UniqueActor, id), Lawn(), matchSeed: 7);

        Assert.DoesNotContain(payload.Grants, g => g.EffectId == CriticalHunterEffectId);
    }
}
