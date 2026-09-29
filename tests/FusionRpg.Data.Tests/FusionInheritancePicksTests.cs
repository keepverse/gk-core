using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// WAVE F2.4 (creature-standalone, 2026-09-07, `creature-mechanism-gaps-ideal.md` §3.4): `ExecuteFusion`
/// accepts, validates, and prices player-selected inheritance picks — the closing seam over F2.1
/// (`InstanceProducer.Compose` forced picks), F2.2 (`GetSpecimenMaterialisedRoll`), and F2.3
/// (`FusionCostTable.InheritPick`).
///
/// <para>Recipe fixture deliberately selects one whose OWN INPUTS are already at or above
/// <c>CreatureRecipeCatalog.OutputEligibilityFloor</c> (Cultivated) — unlike the sibling
/// <c>FusionStoreTests.Recipe</c> (a Cultivated-output recipe, whose own inputs sit one rung BELOW
/// that floor per <c>InputPoolBelow</c>). <c>InheritCostByRarity</c> only covers Cultivated-and-above,
/// so a Cultivated-output recipe's inputs are never pick-eligible; this fixture picks a recipe one or
/// more rungs higher, whose inputs genuinely are.</para>
/// </summary>
public class FusionInheritancePicksTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public FusionInheritancePicksTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning MaterialiseTuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);
    const int PinTheta = 20;

    static readonly CreatureRecipeDef Recipe = CreatureRecipeCatalog.All
        .First(r =>
            CreatureRarityLadder.AtLeast(CreatureSpeciesCatalog.Get(r.InputSpeciesIdA).BaseRarity, CreatureRecipeCatalog.OutputEligibilityFloor)
            && CreatureRarityLadder.AtLeast(CreatureSpeciesCatalog.Get(r.InputSpeciesIdB).BaseRarity, CreatureRecipeCatalog.OutputEligibilityFloor));
    static readonly CreatureSpeciesDef Output = CreatureSpeciesCatalog.Get(Recipe.OutputSpeciesId);

    string Mint(string speciesId)
    {
        var species = CreatureSpeciesCatalog.Get(speciesId);
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { species.TraitPool[0] },
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    /// <summary>A real POOLED species-passive container with roll budget — a forced pick needs a
    /// nonzero `SuffixRolls`/`PrefixRolls` budget to land in (F2.1's own "exceeds-roll-budget"
    /// refusal), unlike `SpecimenMaterialisedRollTests`'s fixed-atom-only fixture.</summary>
    void SeedPooledSpecies(string speciesId, out string atomId, int amount)
    {
        var familyId = "atom." + speciesId.Replace(".", "-") + "-passive";
        atomId = familyId + ".t1";
        var atomRes = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = familyId, Tier = 1,
            Name = atomId, ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
        });
        Assert.True(atomRes.IsOk, atomRes.ToString());
        var affixId = "affix." + speciesId + ".passive";
        var affixRes = _store.UpsertAffix(
            new AffixRow(affixId, AffixClass.Prefix, new[] { new AffixRefRow(1, atomId) }),
            _store.GetAtom);
        Assert.True(affixRes.IsOk, affixRes.ToString());
        var containerRes = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = $"species-passive.{speciesId}",
            Kind = ContainerKind.SpeciesPassive,
            PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow(affixId, 100) },
        });
        Assert.True(containerRes.IsOk, containerRes.ToString());
    }

    void Bankroll(long souls)
    {
        var cost = FusionCostTable.Recipe(Output.BaseRarity);
        _store.AwardSouls(1, souls, "seed", "inherit-bank-" + Guid.NewGuid().ToString("N"));
        _store.AddCreatureMaterials(1, new[]
        {
            ("shard." + cost.ShardRarity.ToId(), (long)cost.ShardCount * 5),
            ("essence." + Output.ElementPrimary.ToElementId(), (long)cost.EssenceCount * 5),
        });
    }

    static readonly FusionRpg.Core.Saves.EmpireRef Owner =
        new(new FusionRpg.Core.Saves.SaveId(1), EmpireId.Dave);

    static bool HasLedgerRowFor(RpgStore store, string speciesId) =>
        store.ListSpeciesMods(Owner).Any(m => string.Equals(m.SpeciesId, speciesId, StringComparison.Ordinal));

    /// <summary>Seeds species A's own pooled passive content, mints both sacrifices, and reads A's pick
    /// source — the paying empire's LEDGER instance when it has one, else the delayed preview. There is no
    /// eager roster roll any more (SP0.3), so nothing is materialised first.</summary>
    (string a, string b, string atomId, string pick) SetUpValidSacrifices()
    {
        SeedPooledSpecies(Recipe.InputSpeciesIdA, out _, 7);
        var a = Mint(Recipe.InputSpeciesIdA);
        var b = Mint(Recipe.InputSpeciesIdB);
        var atomId = _store.PickSourceAtoms(1, Recipe.InputSpeciesIdA)
            .Select(at => at.AtomId).First();
        var pick = _store.GetCreatureProfile(a)!.TraitIds[0];
        return (a, b, atomId, pick);
    }

    [Fact]
    public void A_valid_affordable_pick_set_succeeds_and_the_output_instance_carries_it_verbatim()
    {
        var (a, b, atomId, pick) = SetUpValidSacrifices();
        // Seeded AFTER the sacrifices were read — this player has never fused the output species, so a
        // first-time forced pick is legal (the "already-materialised" gate is open).
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        Bankroll(5000);

        var (ok, reason, outcome) = _store.ExecuteFusion(1, "inherit-1", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick,
            new[] { new FusionPick(a, atomId) }), seed: 7);

        Assert.True(ok, reason);
        var mintedInstanceId = outcome!.Minted!.Profile.InstanceId;
        Assert.Equal(Output.SpeciesId, outcome.Minted.Profile.SpeciesId);

        // SP0.3: the forced pick materialised into ONE LEDGER row for the paying empire (append-only,
        // first ever roll), whose instance carries the picked atom verbatim. The row's correlation id is
        // the fused output's own instance id, which is what makes a replay write nothing.
        var mod = _store.ListSpeciesMods(Owner).Single(m => m.SpeciesId == Output.SpeciesId);
        Assert.Equal(FusionRpg.Core.Creatures.Layers.SpeciesModMechanism.FusionPick, mod.Mechanism);
        Assert.Equal(mintedInstanceId, mod.CorrelationId);
        Assert.Equal(EmpireId.Dave, mod.Empire);

        var speciesInstance = _store.GetInstance(mod.InstanceId)!;
        Assert.Contains(speciesInstance.Atoms, at => at.AtomId == atomId);
        // Plus the correctly-rolled remainder: PrefixRolls=1 total, 1 forced -> 0 remaining rolled,
        // so the instance carries EXACTLY the forced pick, nothing else.
        Assert.Single(speciesInstance.Atoms);
        // And the pick source now resolves through the ledger: the same atoms the preview offered.
        Assert.Equal(mod.InstanceId, _store.ListSpeciesMods(Owner).Single().InstanceId);
        Assert.Contains(_store.PickSourceAtoms(1, Output.SpeciesId), at => at.AtomId == atomId);
    }

    [Fact]
    public void A_pick_set_over_slotsByRaritys_cap_is_refused_before_spending_anything()
    {
        var (a, b, _, pick) = SetUpValidSacrifices();
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        Bankroll(5000);
        var balanceBefore = _store.GetSoulBalance(1).Balance;
        var slotCap = FusionRoller.SlotsFor(Output.BaseRarity);

        var overCap = Enumerable.Range(0, slotCap + 1)
            .Select(_ => new FusionPick(a, "atom.does-not-need-to-resolve"))
            .ToArray();
        var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-overcap", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick, overCap), seed: 7);

        Assert.False(ok);
        Assert.Equal("picks.exceeds-slots", reason);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.False(HasLedgerRowFor(_store, Output.SpeciesId));
    }

    [Fact]
    public void An_unaffordable_pick_set_is_refused_before_spending_anything()
    {
        var (a, b, atomId, pick) = SetUpValidSacrifices();
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        // Enough for the base recipe cost alone, nowhere near enough once the pick's own inherit
        // cost is added on top.
        var baseCost = FusionCostTable.Recipe(Output.BaseRarity);
        _store.AwardSouls(1, baseCost.Souls, "seed", "inherit-tight");
        _store.AddCreatureMaterials(1, new[]
        {
            ("shard." + baseCost.ShardRarity.ToId(), (long)baseCost.ShardCount * 5),
            ("essence." + Output.ElementPrimary.ToElementId(), (long)baseCost.EssenceCount * 5),
        });
        var balanceBefore = _store.GetSoulBalance(1).Balance;

        var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-poor", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick,
            new[] { new FusionPick(a, atomId) }), seed: 7);

        Assert.False(ok);
        Assert.Equal("souls.insufficient", reason);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.False(HasLedgerRowFor(_store, Output.SpeciesId));
        // Sacrifices survive — a refused fusion consumes nothing (ExecuteFusion's own contract).
        Assert.Equal(FusionRpg.Contracts.UniqueActorPhases.Roster, _store.GetUniqueActor(a)!.Phase);
    }

    [Fact]
    public void A_pick_naming_an_atom_its_specimen_never_actually_rolled_is_rejected_by_name()
    {
        var (a, b, _, pick) = SetUpValidSacrifices();
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        Bankroll(5000);
        var balanceBefore = _store.GetSoulBalance(1).Balance;

        var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-bad-atom", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick,
            new[] { new FusionPick(a, "atom.never-rolled-by-anything") }), seed: 7);

        Assert.False(ok);
        Assert.Equal("picks.atom-not-rolled", reason);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance);
        Assert.False(HasLedgerRowFor(_store, Output.SpeciesId));
    }

    [Fact]
    public void A_pick_naming_an_instance_that_is_not_one_of_the_two_sacrifices_is_rejected_by_name()
    {
        var (a, b, atomId, pick) = SetUpValidSacrifices();
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        Bankroll(5000);

        var stranger = Mint(Recipe.InputSpeciesIdA);
        var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-stranger", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick,
            new[] { new FusionPick(stranger, atomId) }), seed: 7);

        Assert.False(ok);
        Assert.Equal("picks.source-not-a-sacrifice", reason);
    }

    [Fact]
    public void A_pick_set_is_refused_when_the_empire_already_holds_a_1b_row_for_the_output_species()
    {
        var (a, b, atomId, pick) = SetUpValidSacrifices();
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        // The paying empire already holds a 1b roll for the output species (an earlier fusion) — picks can
        // no longer be honored without silently re-rolling every other specimen's instance out from under
        // it. SP0.3: the gate now reads the LEDGER, not player_species.
        Assert.True(_store.AppendSpeciesMod(Owner, Output.SpeciesId,
            FusionRpg.Core.Creatures.Layers.SpeciesModMechanism.FusionPick,
            correlationId: "earlier-fusion", instanceId: "earlier-fusion", catalogRevision: 0));
        Bankroll(5000);

        var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-already", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick,
            new[] { new FusionPick(a, atomId) }), seed: 7);

        Assert.False(ok);
        Assert.Equal("picks.already-materialised", reason);
    }

    [Fact]
    public void A_source_species_with_no_container_still_reaches_source_not_materialised()
    {
        // Only species A gets a pooled container, so sacrifice B has no source at all — the one case the
        // retired code still reaches (spec behaviour 4: the code stays in the vocabulary).
        SeedPooledSpecies(Recipe.InputSpeciesIdA, out _, 7);
        var a = Mint(Recipe.InputSpeciesIdA);
        var b = Mint(Recipe.InputSpeciesIdB);
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        Bankroll(5000);
        Assert.Empty(_store.PickSourceAtoms(1, Recipe.InputSpeciesIdB));

        var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-nosource", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, _store.GetCreatureProfile(a)!.TraitIds[0],
            new[] { new FusionPick(b, "atom.whatever") }), seed: 7);

        Assert.False(ok);
        Assert.Equal("picks.source-not-materialised", reason);
    }

    [Fact]
    public void Every_atom_the_preview_offers_is_one_the_transaction_accepts()
    {
        // The preview endpoint and the transaction call the SAME function (store.PickSourceAtoms), so the
        // atoms offered are exactly the atoms accepted. This is the parity, made executable.
        SeedPooledSpecies(Recipe.InputSpeciesIdA, out _, 7);
        var a = Mint(Recipe.InputSpeciesIdA);
        var b = Mint(Recipe.InputSpeciesIdB);
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        Bankroll(5000);

        var offered = _store.PickSourceAtoms(1, Recipe.InputSpeciesIdA);
        Assert.NotEmpty(offered);

        var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-parity", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, _store.GetCreatureProfile(a)!.TraitIds[0],
            offered.Select(at => new FusionPick(a, at.AtomId)).ToArray()), seed: 7);

        Assert.True(ok, reason);
        var mod = _store.ListSpeciesMods(Owner).Single(m => m.SpeciesId == Output.SpeciesId);
        var accepted = _store.GetInstance(mod.InstanceId)!.Atoms.Select(at => at.AtomId).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(accepted, offered.Select(at => at.AtomId).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Zero_picks_reproduces_todays_exact_recipe_behavior()
    {
        var (a, b, _, pick) = SetUpValidSacrifices();
        Bankroll(5000);

        var (ok, reason, outcome) = _store.ExecuteFusion(1, "inherit-none", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick), seed: 7);

        Assert.True(ok, reason);
        Assert.Equal(Output.SpeciesId, outcome!.Minted!.Profile.SpeciesId);
        // No forced picks -> no ledger row at all: "no row means no 1b" (spec behaviour 1).
        Assert.False(HasLedgerRowFor(_store, Output.SpeciesId));
    }

    /// <summary>F2.3's own long-deferred acceptance box (`creature-standalone-todo.md` F2.3, cleared
    /// 2026-09-20 by `backlog-clean-up` BCU4.4): each pick's own inherit cost must come from ITS OWN
    /// source rarity — never the fusion output's rarity.
    ///
    /// <para><b>Corrected while writing this test</b>: the box's original wording ("two
    /// differently-rarity'd sacrifices") describes a scenario <see cref="CreatureRecipeCatalog"/>
    /// cannot produce. <c>BuildDeterministicOnly</c>'s <c>InputPoolBelow</c> always draws BOTH of a
    /// recipe's inputs from the SAME nearest-populated rung below the output — a documented,
    /// deliberate invariant (`CreatureRecipeCatalog.cs` around <c>TryFindPair</c>, pinned by
    /// <c>CreatureRecipeCatalogTests.Inputs_are_distinct_band_below_and_never_capture_only</c>). No
    /// recipe anywhere has two inputs at different rarities from EACH OTHER. What genuinely differs,
    /// always, is the shared source rarity versus the OUTPUT's rarity — the source pool is by
    /// construction one or more rungs below the output. That is the real, always-true, always
    /// testable divergence the box's own follow-on sentence ("not the output's [rarity]") actually
    /// names, and it is what this test proves: if <c>ExecuteFusion</c> ever priced a pick by
    /// <c>output.BaseRarity</c> instead of the pick's own source rarity, this test's exact-souls
    /// assertion below would fail, because the two rarities' <c>InheritPick</c> values differ.</para>
    /// </summary>
    [Fact]
    public void A_pick_is_priced_by_its_own_source_rarity_never_the_fusion_outputs_rarity()
    {
        var sourceRarity = CreatureSpeciesCatalog.Get(Recipe.InputSpeciesIdA).BaseRarity;
        // The invariant this whole test leans on: the source pool sits strictly below the output —
        // never equal — so InheritPick(source) and InheritPick(output) are two different lookups by
        // construction, not two names for the same rung.
        Assert.NotEqual(sourceRarity, Output.BaseRarity);
        Assert.NotEqual(FusionCostTable.InheritPick(sourceRarity), FusionCostTable.InheritPick(Output.BaseRarity));

        var (a, b, atomId, pick) = SetUpValidSacrifices();
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        var baseCost = FusionCostTable.Recipe(Output.BaseRarity);
        var expectedSpend = baseCost.Souls + FusionCostTable.InheritPick(sourceRarity);
        // Fund generously — this is the fusion's FIRST time (fresh player, fresh species/recipe), so
        // a discovery bonus also lands; the decisive assertion below reads it back from the outcome
        // itself rather than re-deriving `SoulEarnPolicy`'s own formula, which is a different
        // mechanism's contract, not this test's subject.
        _store.AwardSouls(1, expectedSpend, "seed", "source-rarity-bank-" + Guid.NewGuid().ToString("N"));
        _store.AddCreatureMaterials(1, new[]
        {
            ("shard." + baseCost.ShardRarity.ToId(), (long)baseCost.ShardCount * 5),
            ("essence." + Output.ElementPrimary.ToElementId(), (long)baseCost.EssenceCount * 5),
        });
        var balanceBefore = _store.GetSoulBalance(1).Balance;

        var (ok, reason, outcome) = _store.ExecuteFusion(1, "inherit-source-rarity", new FusionRequest(
            FusionModes.Recipe, null, new[] { a, b }, pick,
            new[] { new FusionPick(a, atomId) }), seed: 7);

        Assert.True(ok, reason);
        Assert.Equal(Output.SpeciesId, outcome!.Minted!.Profile.SpeciesId);
        // The decisive assertion: souls spent equal EXACTLY base-recipe-cost plus the pick's own
        // SOURCE-rarity cost (a first-time discovery bonus, reported by the outcome itself, is the
        // only other soul movement) — pricing the pick by the output's rarity instead would spend a
        // different, provably-mismatched total (asserted above), so this could not pass by
        // coincidence.
        Assert.Equal(balanceBefore - expectedSpend + outcome.DiscoverySouls, _store.GetSoulBalance(1).Balance);
    }

    // ---- rank floor (spec-species-rank.md §6, creature-seed Task 8) ------------------------------------

    [Fact]
    public void A_pick_from_a_source_below_the_rank_floor_is_refused_by_name()
    {
        var (a, b, atomId, pick) = SetUpValidSacrifices();
        SeedPooledSpecies(Output.SpeciesId, out _, 3);
        Bankroll(5000);

        // Every gate's shipped floor is the bottom rung; raise THIS one so the new gate actually
        // decides. The source species' own rank (whatever the compiled roster carries) is what the
        // enforcing site reads, so a floor above bottom refuses it — and the refusal lands BEFORE any
        // cost is spent, exactly like the rarity floor beside it.
        var rankIds = CreatureRarityLadder.All.Select(r => r.ToId()).ToList();
        CreatureRankFloors.Configure(new CreatureRankTuning(
            1, rankIds, Array.Empty<CreatureRankCell>(),
            CreatureRankFloors.DeclaredGates.ToDictionary(g => g, _ => CreatureRank.Heirloom.ToId(), StringComparer.Ordinal)));
        try
        {
            var balanceBefore = _store.GetSoulBalance(1).Balance;
            var (ok, reason, _) = _store.ExecuteFusion(1, "inherit-rank-floor", new FusionRequest(
                FusionModes.Recipe, null, new[] { a, b }, pick,
                new[] { new FusionPick(a, atomId) }), seed: 7);

            Assert.False(ok);
            Assert.Equal("picks.source-below-rank-floor", reason);
            Assert.Equal(balanceBefore, _store.GetSoulBalance(1).Balance); // nothing was spent
        }
        finally
        {
            CreatureRankFloors.ResetToUnconfigured();
        }
    }
}
