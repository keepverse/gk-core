using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.23 — "AI-empire specimens never touch a human-only table": the codex is the
/// human's own almanac of the save, and contracts are the summoner's binding slots, loyalty and
/// tribute. An AI empire's own mint (Zomboss's, today's one production caller of `MintForEmpire` with
/// a non-human empire) touches neither, and its deploy is never gated by the contract system —
/// fixing D6 (every Zomboss mint used to auto-bind on one shared 12-slot row).
/// </summary>
[Trait("VerificationId", "data.ai-empire-specimen")]
public class AiEmpireSpecimenTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AiEmpireSpecimenTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s =>
            s.DeployMode != FusionRpg.Core.Creatures.CreatureDeployMode.HypnoAlly);

    /// <summary>save-identity SE4.28 ("Injector ownership"): the read `UniqueActorService.DeployAsync`
    /// stamps onto the spawn command — a Zomboss specimen resolves (Zomboss, Ai), a human one
    /// (Dave, Human), and an unseeded/empty empire_id resolves null rather than a guess.</summary>
    [Fact]
    public void SpecimenOwnerEmpire_resolves_the_specimens_own_empire_and_controller()
    {
        var zombossOwner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        var zombossSpecimen = _store.MintForEmpire(zombossOwner, CatalogSpecies.SpeciesId, seed: 30);
        var (humanSpecimen, _) = _store.MintCreature(1, new()
        {
            SpeciesId = CatalogSpecies.SpeciesId,
            Side = CatalogSpecies.Side,
            GameTypeId = CatalogSpecies.GameTypeId,
            Rarity = CatalogSpecies.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = CatalogSpecies.ElementPrimary.ToElementId(),
            Origin = "summon",
        });

        var zombossResolved = _store.SpecimenOwnerEmpire(zombossSpecimen.Actor.InstanceId);
        var humanResolved = _store.SpecimenOwnerEmpire(humanSpecimen.Actor.InstanceId);

        Assert.Equal(EmpireId.Zomboss, zombossResolved!.Value.Empire);
        Assert.Equal(EmpireController.Ai, zombossResolved.Value.Controller);
        Assert.Equal(EmpireId.Dave, humanResolved!.Value.Empire);
        Assert.Equal(EmpireController.Human, humanResolved.Value.Controller);
        Assert.Null(_store.SpecimenOwnerEmpire("no-such-instance"));
    }

    [Fact]
    public void An_AI_empire_mint_writes_no_codex_contract_or_contract_state_row()
    {
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        var specimen = _store.MintForEmpire(owner, CatalogSpecies.SpeciesId, seed: 1);

        Assert.DoesNotContain(_store.ListCreatureCodex(1).Entries, e => e.SpeciesId == CatalogSpecies.SpeciesId);
        Assert.Null(_store.GetContract(specimen.Actor.InstanceId));
        Assert.Null(_store.GetContractState(1));
    }

    [Fact]
    public void A_human_mint_still_writes_codex_and_a_bound_contract_exactly_as_before()
    {
        var (specimen, _) = _store.MintCreature(1, new()
        {
            SpeciesId = CatalogSpecies.SpeciesId,
            Side = CatalogSpecies.Side,
            GameTypeId = CatalogSpecies.GameTypeId,
            Rarity = CatalogSpecies.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = CatalogSpecies.ElementPrimary.ToElementId(),
            Origin = "summon",
        });

        Assert.Contains(_store.ListCreatureCodex(1).Entries, e => e.SpeciesId == CatalogSpecies.SpeciesId);
        var contract = _store.GetContract(specimen.Actor.InstanceId);
        Assert.NotNull(contract);
        Assert.True(contract!.Bound);
    }

    [Fact]
    public void An_AI_empire_specimen_deploys_with_no_contract_row_the_gate_never_checks_it()
    {
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        var specimen = _store.MintForEmpire(owner, CatalogSpecies.SpeciesId, seed: 2);
        Assert.Null(_store.GetContract(specimen.Actor.InstanceId)); // unbound, by SE4.23's own first test

        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(
            specimen.Actor.InstanceId, Guid.NewGuid().ToString("N"), matchKey: "ai-empire-deploy");

        Assert.True(ok, reason);
    }

    [Fact]
    public void D6_a_13th_Zomboss_deploy_in_one_save_still_deploys()
    {
        var owner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);

        // 13 > ContractPolicy.Capacity(0) (12, gk-core/data/tuning/contracts.v1.json). Before SE4.22/SE4.23 every
        // one of these auto-bound on ONE shared global row, so the 13th arrived unbound and refused
        // `contract.unbound` (D6). None of these touch contracts at all any more.
        FusionRpg.Contracts.CreatureSpecimenDto? thirteenth = null;
        for (var i = 1; i <= 13; i++)
            thirteenth = _store.MintForEmpire(owner, CatalogSpecies.SpeciesId, seed: (ulong)(100 + i));

        var (ok, reason, _, _) = _store.TryBeginUniqueDeploy(
            thirteenth!.Actor.InstanceId, Guid.NewGuid().ToString("N"), matchKey: "d6-regression");

        Assert.True(ok, reason);
        Assert.Null(_store.GetContractState(1)); // the human's contract system was never touched
    }
}
