using System.Reflection;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `salvage-craft` (item module 14) §1 — the closed 27-id cost vocabulary every other sink spends
/// in, drawn from FIVE classes (species-gear-chain T33 filed a SIXTH, `Trophy`, and T40 a SEVENTH,
/// `Assurance` — neither mints an id counted here: Trophy's ids are a population injected by a host
/// registry, Assurance's are a closed three-id set `materialgen` authors display content for), and
/// the matrix that makes it enforceable rather than advisory.
/// </summary>
public class MaterialVocabularyTests
{
    [Fact]
    public void The_vocabulary_is_twenty_seven_closed_ids_in_five_of_seven_classes()
    {
        var all = MaterialCatalog.All;
        // pin: closed-vocabulary MaterialCatalog.All — a new material id is a reviewed change
        Assert.Equal(27, all.Count);
        // Self-referential, not a second literal: proves the roster carries no duplicate id.
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());

        // The counts the spec's §1 table publishes, each measured off the shipped roster it derives
        // from rather than transcribed: 10 rungs, 2 frames x 4 grades, 6 concrete elements, 3 verbs.
        Assert.Equal(CreatureRarityLadder.RungCount, all.Count(i => i.StartsWith("shard.", StringComparison.Ordinal)));
        Assert.Equal(8, all.Count(i => i.StartsWith("substrate.", StringComparison.Ordinal)));
        Assert.Equal(ElementRoster.Concrete.Count, all.Count(i => i.StartsWith("essence.", StringComparison.Ordinal)));
        Assert.Equal(3, all.Count(i => i.StartsWith("catalyst.", StringComparison.Ordinal)));

        // 27 and not 28: souls carry no material id, they are a ledger balance. The class exists;
        // the id does not.
        Assert.Contains(MaterialClass.Souls, Enum.GetValues<MaterialClass>());
        Assert.DoesNotContain(all, i => i.StartsWith("souls", StringComparison.Ordinal));

        // species-gear-chain T33/T40: Trophy (6th) and Assurance (7th) mint nothing here — Trophy's
        // ids are a population (a deterministic planner's output, injected by the host), Assurance's
        // are a closed three-id set of their own. Still 27, still five classes represented in it.
        Assert.Equal(7, Enum.GetValues<MaterialClass>().Length);
        Assert.DoesNotContain(all, i => i.StartsWith("trophy.", StringComparison.Ordinal));
        Assert.DoesNotContain(all, i => i.StartsWith("assurance.", StringComparison.Ordinal));
    }

    [Fact]
    public void Ten_shard_ids_exist_and_four_legacy_ids_resolve_but_are_never_minted()
    {
        // The platform state spec-salvage-craft.md verified I9's "four bands ship" claim FALSE
        // against, pinned here so a regression is loud.
        foreach (var rarity in CreatureRarityLadder.All)
            Assert.True(MaterialCatalog.IsIssuable(MaterialCatalog.ShardId(rarity)), rarity.ToId());

        foreach (var legacy in new[] { "shard.common", "shard.rare", "shard.epic", "shard.legendary" })
        {
            Assert.True(MaterialCatalog.IsKnown(legacy), $"{legacy} must still RESOLVE");
            Assert.False(MaterialCatalog.IsIssuable(legacy), $"{legacy} must never be MINTED");
            Assert.True(MaterialCatalog.IsLegacyShardId(legacy));
            Assert.DoesNotContain(legacy, MaterialCatalog.All);
        }

        // And the sixteen shipped ids are REUSED from CreatureMaterialCatalog, not re-minted here.
        foreach (var id in CreatureMaterialCatalog.All)
            Assert.True(MaterialCatalog.IsIssuable(id), id);
    }

    [Theory]
    [InlineData("essence.fire.pvz")]
    [InlineData("shard.heirloom.web")]
    [InlineData("catalyst.forge.lawn")]
    public void A_source_tagged_material_id_is_refused(string id)
    {
        // Boundaries, "Never": the injector enriches, it never gates (SC8). A PvZ-exclusive material
        // id would make the lawn a required source for a web operation.
        Assert.False(MaterialCatalog.IsKnown(id));
        Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.ClassOf(id));
    }

    [Fact]
    public void Spend_order_is_souls_shard_substrate_essence_catalyst_trophy_assurance()
    {
        // Fixed class order matters: a partial failure always fails at the same point, so two logs
        // of one refusal are byte-comparable. The enum's member order IS the spend order. Trophy
        // (T33) and Assurance (T40) are appended in filing order -- the least disruptive placement,
        // and neither displaces the five ClassRank already fixed.
        Assert.Equal(
            new[] { MaterialClass.Souls, MaterialClass.Shard, MaterialClass.Substrate, MaterialClass.Essence,
                   MaterialClass.Catalyst, MaterialClass.Trophy, MaterialClass.Assurance },
            Enum.GetValues<MaterialClass>().OrderBy(MaterialCatalog.ClassRank).ToArray());
    }

    [Fact]
    public void Cost_class_forbidden_rejects_a_forge_spending_temper()
    {
        // The spec's own named example. `forge` DOES spend a catalyst, so this is not "no catalyst
        // allowed" — it is the RIGHT class carrying the WRONG catalyst, which is why the matrix
        // raises a second, distinct rule for it.
        var refusal = CostClassMatrix.Check(CraftOperation.Forge, "catalyst.temper");
        Assert.NotNull(refusal);
        Assert.Equal(CostClassMatrix.CatalystMismatchRule, refusal!.Value.Rule);
        Assert.Contains("catalyst.forge", refusal.Value.Detail);

        // And a class the operation may not spend at all is the OTHER rule.
        var forbidden = CostClassMatrix.Check(CraftOperation.Forge, "shard.heirloom");
        Assert.NotNull(forbidden);
        Assert.Equal(CostClassMatrix.CostClassForbiddenRule, forbidden!.Value.Rule);

        Assert.Null(CostClassMatrix.Check(CraftOperation.Forge, "catalyst.forge"));
        Assert.Null(CostClassMatrix.Check(CraftOperation.Forge, "substrate.plant.sound"));
    }

    [Fact]
    public void Imbue_rides_forge_and_the_two_catalyst_free_operations_ride_none()
    {
        // §"The three catalysts": make / improve / re-randomise.
        Assert.Equal("catalyst.forge", CostClassMatrix.CatalystFor(CraftOperation.Forge));
        Assert.Equal("catalyst.forge", CostClassMatrix.CatalystFor(CraftOperation.ForgeGem));
        Assert.Equal("catalyst.forge", CostClassMatrix.CatalystFor(CraftOperation.Bore));
        Assert.Equal("catalyst.forge", CostClassMatrix.CatalystFor(CraftOperation.Imbue));
        Assert.Equal("catalyst.temper", CostClassMatrix.CatalystFor(CraftOperation.Temper));
        Assert.Equal("catalyst.temper", CostClassMatrix.CatalystFor(CraftOperation.Elevate));
        Assert.Equal("catalyst.flux", CostClassMatrix.CatalystFor(CraftOperation.RerollOne));
        Assert.Equal("catalyst.flux", CostClassMatrix.CatalystFor(CraftOperation.RerollAll));

        // Neither brings anything into existence, so neither burns a catalyst — and socketing an
        // insert you already own must never be a material decision (I9 §8.4).
        Assert.Null(CostClassMatrix.CatalystFor(CraftOperation.Upcycle));
        Assert.Null(CostClassMatrix.CatalystFor(CraftOperation.Socket));
        Assert.False(CostClassMatrix.Allows(CraftOperation.Socket, MaterialClass.Catalyst));
        Assert.False(CostClassMatrix.Allows(CraftOperation.Socket, MaterialClass.Substrate));
    }

    [Fact]
    public void The_operation_vocabulary_is_twelve_verbs_and_upgrade_is_the_twelfth()
    {
        // I9 §6.1's enum is SEVEN. The shipped vocabulary is eleven: `forge-gem` and the reroll split
        // I9 §7.4 already implies, D24's `imbue` (which has no row anywhere in I9), and
        // species-gear-chain T23's `repair` (the durability slice, module 7).
        // The count is fully implied by the element-wise equality below -- never pinned separately
        // (population-pin SE3.3, 2026-09-19).
        Assert.Equal(
            new[] { "forge", "upcycle", "forge-gem", "bore", "imbue", "socket", "elevate", "temper", "reroll-one", "reroll-all", "repair", "upgrade" },
            CraftOperations.AllIds);

        Assert.True(CraftOperations.TryParse("imbue", out var imbue));
        Assert.Equal(CraftOperation.Imbue, imbue);

        // ⛔ `reroll` is deliberately NOT parseable — the reroll-one / reroll-all split was module
        // 15's to make, and inventing it here would have minted a second op_kind vocabulary the
        // spec's Boundaries forbid outright. ⭐ Module 15 made it 2026-09-05 and re-authored the
        // seven corpus rows onto the split verbs, so the bare verb is now used by nothing at all —
        // this assertion pins that it never comes back.
        Assert.False(CraftOperations.TryParse("reroll", out _));
        // `socket-imbue` is an OP_KIND (MutationOpKind.SocketImbue, minted by module 15), never a
        // CraftOperation: the priced verb for D24's operation is `imbue`, asserted above. Two
        // vocabularies, one name each — this pins that they stay separate.
        Assert.False(CraftOperations.TryParse("socket-imbue", out _));
    }

    [Fact]
    public void Substrate_ids_round_trip_through_frame_and_grade_and_refuse_a_fifth_grade()
    {
        foreach (var frame in MaterialCatalog.SubstrateFrames)
        {
            for (var g = 1; g <= MaterialCatalog.SubstrateGrades.Count; g++)
            {
                var id = MaterialCatalog.SubstrateId(frame, g);
                Assert.True(MaterialCatalog.IsIssuable(id));
                Assert.Equal(g, MaterialCatalog.GradeOf(id));
                Assert.Equal(frame, MaterialCatalog.FrameOf(id));
                Assert.Equal(MaterialClass.Substrate, MaterialCatalog.ClassOf(id));
            }
        }

        // Throws rather than clamping: a silent clamp to prime would hand out the exact material
        // the grade lock exists to protect.
        Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.SubstrateId("humanoid", 5));
        Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.SubstrateId("humanoid", 0));
        Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.SubstrateId("mineral", 1));
    }

    [Fact]
    public void Every_id_in_the_vocabulary_classifies_and_nothing_outside_it_does()
    {
        foreach (var id in MaterialCatalog.All)
            Assert.True(Enum.IsDefined(MaterialCatalog.ClassOf(id)));

        foreach (var bad in new[] { "", "souls", "substrate.humanoid", "essence.omni", "catalyst.reforge", "shard.mythic" })
            Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.ClassOf(bad));
    }

    [Fact]
    public void No_cost_input_reads_a_player_property()
    {
        // D26, MECHANICALLY. The context types simply have nowhere to put a player stat, and the
        // resolver takes no other argument that could carry one. This is a shape assertion, not a
        // grep: a field added later is caught by name here.
        // species-gear-chain T32: SpeciesRungIndex is the CRAFTED PIECE's own species rung — a
        // property of the target, exactly like TargetRungIndex/TargetItemLevel/TargetFrame beside
        // it, never of the player. Added here deliberately, not by drift.
        // species-gear-chain T34b: BoundSpeciesId/BoundSpeciesFamilies/TrophyStock are likewise all
        // properties of the TARGET (the crafted piece's own species binding and a snapshot of what
        // the caller already resolved for it) — none reads the player, so D26 still holds.
        Assert.Equal(
            new[] {
                "TargetRungIndex", "TargetTier", "TargetItemLevel", "TargetFrame", "EnhanceLevel",
                "SpeciesRungIndex", "BoundSpeciesId", "BoundSpeciesFamilies", "TrophyStock",
            },
            typeof(RecipeContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToArray());

        Assert.Equal(
            new[] { "RungIndex", "ItemLevel", "Frame", "AffixCount", "ElementalAffixCounts", "EnhanceLevel" },
            typeof(SalvageInput).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToArray());

        // Resolve(recipeId, context) — no third argument, so nothing can be smuggled past the type.
        var resolve = typeof(MaterialRecipeCatalog).GetMethod(nameof(MaterialRecipeCatalog.Resolve))!;
        Assert.Equal(new[] { typeof(string), typeof(RecipeContext) },
            resolve.GetParameters().Select(p => p.ParameterType).ToArray());

        // And the closed variable vocabulary has no spelling for a player term at all.
        foreach (var v in Enum.GetNames<CostVariable>())
            Assert.DoesNotContain(v.ToLowerInvariant(), new[] { "theta", "playerlevel", "powerindex", "daily", "session" });
    }
}

/// <summary>
/// species-gear-chain T33 — `CostClassMatrix.Allows`'s Trophy arm, dedicated so
/// `--filter "FullyQualifiedName~CostClass"` (the task's own verification line) finds something.
/// </summary>
public class CostClassMatrixTests
{
    [Fact]
    public void Trophy_is_allowed_only_on_elevate_and_temper()
    {
        Assert.True(CostClassMatrix.Allows(CraftOperation.Elevate, MaterialClass.Trophy));
        Assert.True(CostClassMatrix.Allows(CraftOperation.Temper, MaterialClass.Trophy));
    }

    [Theory]
    [InlineData(CraftOperation.Forge)]
    [InlineData(CraftOperation.Upcycle)]
    [InlineData(CraftOperation.ForgeGem)]
    [InlineData(CraftOperation.Bore)]
    [InlineData(CraftOperation.Imbue)]
    [InlineData(CraftOperation.Socket)]
    [InlineData(CraftOperation.RerollOne)]
    [InlineData(CraftOperation.RerollAll)]
    [InlineData(CraftOperation.Repair)]
    public void Trophy_is_forbidden_on_every_other_of_the_eleven_verbs(CraftOperation op)
    {
        // A permissive arm here would reopen the "every recipe is a travel itinerary" failure
        // ssot-materials-crafting.md §3.4 already refused. Only 2 of 11 verbs may spend a trophy.
        Assert.False(CostClassMatrix.Allows(op, MaterialClass.Trophy));
    }

    // species-gear-chain T34b: `MaterialCatalog.ClassOf` now recognizes both a trophy SCOPE TOKEN
    // (author-time, ClassOf's own structural check) and a CONCRETE id the injected registry holds —
    // `Check`-level tests against both shapes now live in `MaterialTrophyClassResolutionTests` below, next to
    // the rest of the registry/scope-token machinery they exercise.

    [Fact]
    public void All_eleven_operations_are_accounted_for_in_the_narrow_arm()
    {
        // Closed-list guard: if a twelfth CraftOperation is ever added, the foreach below fails
        // loudly on the unhandled member (CostClassMatrix.Allows throws) rather than silently
        // defaulting its Trophy eligibility -- the count itself is never pinned separately
        // (population-pin, mega-merge conflict resolution 2026-09-20; CraftOperations.All.Count is
        // pinned once, MaterialVocabularyTests.cs's own earlier test).
        foreach (var op in CraftOperations.All)
            _ = CostClassMatrix.Allows(op, MaterialClass.Trophy); // throws on an unhandled member
    }

    // species-gear-chain T40: `CostClassMatrix.Allows`'s Assurance arm — narrower than Trophy's on
    // purpose (Temper only; T43/T44 extend it to the decaying verbs and Repair).

    [Theory]
    [InlineData(CraftOperation.Temper)]
    [InlineData(CraftOperation.Elevate)]
    [InlineData(CraftOperation.RerollOne)]
    [InlineData(CraftOperation.RerollAll)]
    [InlineData(CraftOperation.Bore)]
    [InlineData(CraftOperation.Socket)]
    [InlineData(CraftOperation.Imbue)]
    [InlineData(CraftOperation.Repair)]
    public void Assurance_is_allowed_on_temper_and_the_verbs_T24_made_decaying(CraftOperation op)
    {
        // T40 (Temper) + T43 (Elevate/RerollOne/RerollAll/Bore/Socket/Imbue -- `spec-craft-risk-ladder.md`
        // §2's own "promotion, temper, reroll and socket work", which share ItemWorkbench.CraftWearFor)
        // + T44 (Repair, its own D1/D2 verb). Allows says the VERB is eligible; which of the three ids
        // a given verb actually reads (assure only where a success die rolls; protect never on repair,
        // R10) is ItemWorkbench.TryCoalesceAssuranceLines's own, finer job — not this matrix's.
        Assert.True(CostClassMatrix.Allows(op, MaterialClass.Assurance));
    }

    [Theory]
    [InlineData(CraftOperation.Forge)]
    [InlineData(CraftOperation.Upcycle)]
    [InlineData(CraftOperation.ForgeGem)]
    public void Assurance_is_forbidden_on_every_other_of_the_eleven_verbs(CraftOperation op)
    {
        // The three verbs left outside T40+T43+T44's own eight: the pure-mint verbs, which touch no
        // existing instance at all (Forge/Upcycle/ForgeGem). A refusal rides the existing rule and no
        // new error code.
        Assert.False(CostClassMatrix.Allows(op, MaterialClass.Assurance));
        var refusal = CostClassMatrix.Check(op, MaterialCatalog.AssuranceId("assure"));
        Assert.NotNull(refusal);
        Assert.Equal(CostClassMatrix.CostClassForbiddenRule, refusal!.Value.Rule);
    }

    [Fact]
    public void All_eleven_operations_are_accounted_for_in_the_assurance_arm()
    {
        // Self-referential, not a second literal: the vocabulary's size is pinned once at its owner.
        // What this test proves is the loop -- every verb has an Assurance answer, and an unhandled
        // member throws.
        Assert.Equal(CraftOperations.AllIds.Count, CraftOperations.All.Count);
        foreach (var op in CraftOperations.All)
            _ = CostClassMatrix.Allows(op, MaterialClass.Assurance); // throws on an unhandled member
    }
}

/// <summary>
/// species-gear-chain T40 (`craft-assurance` b) — `MaterialClass.Assurance`, the closed three-id
/// class: `ClassOf`/`IsIssuable`/`IsAssuranceId` all agree, the vocabulary is exactly three, and it
/// never grows or shrinks by drift (a fourth is the next ask-first boundary, same posture
/// `CatalystVerbs` already holds for its own closed three).
/// </summary>
public class MaterialAssuranceClassTests
{
    [Fact]
    public void The_assurance_vocabulary_is_exactly_three_closed_ids()
    {
        Assert.Equal(3, MaterialCatalog.AssuranceVerbs.Count);
        Assert.Equal(new[] { "assure", "protect", "repair" }, MaterialCatalog.AssuranceVerbs);
    }

    [Theory]
    [InlineData("assure")]
    [InlineData("protect")]
    [InlineData("repair")]
    public void Each_assurance_id_is_issuable_and_classifies(string verb)
    {
        var id = MaterialCatalog.AssuranceId(verb);
        Assert.Equal($"assurance.{verb}", id);
        Assert.True(MaterialCatalog.IsAssuranceId(id));
        Assert.True(MaterialCatalog.IsIssuable(id));
        Assert.True(MaterialCatalog.IsKnown(id));
        Assert.Equal(MaterialClass.Assurance, MaterialCatalog.ClassOf(id));
        // Never counted in the closed 27 -- a real closed set of its own, not a 28th/29th/30th id
        // in the original five-class vocabulary.
        Assert.DoesNotContain(id, MaterialCatalog.All);
    }

    [Theory]
    [InlineData("assurance.ward")]
    [InlineData("assurance.assure.pvz")]
    [InlineData("assurance")]
    public void An_unknown_or_malformed_assurance_id_is_refused(string materialId)
    {
        Assert.False(MaterialCatalog.IsAssuranceId(materialId));
        Assert.False(MaterialCatalog.IsIssuable(materialId));
        Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.ClassOf(materialId));
    }
}

/// <summary>
/// species-gear-chain T34b (`species-materials` c) — `MaterialCatalog`'s trophy surface: the
/// structural SCOPE TOKEN grammar (author-time, `trophy.species.N` / `trophy.family.N`, never
/// registry-checked) and the host-injected CONCRETE id registry (resolve-time,
/// `trophy.{scope}.{scopeKey}.{slot}`). Real ids are read against the bootstrap's own injected
/// registry (`ContractTuningTestBootstrap`, the REAL committed `trophy-registry.json` — 892 species
/// x2 + 227 families x8) — never a synthetic override, so no test here mutates `MaterialCatalog`'s
/// process-wide static registry mid-run (this assembly's own hard-edge discipline for shared static
/// state: configure once, at the bootstrap, never reconfigure inside a test).
/// </summary>
public class MaterialTrophyClassResolutionTests
{
    // A real species, perSpecies = 2 — confirmed present in the committed registry (species-gear-
    // chain T34's own real run: 892 species x 2 ids).
    const string RealSpeciesTrophy1 = "trophy.species.abyssswordstar.1";
    const string RealSpeciesTrophy2 = "trophy.species.abyssswordstar.2";
    const string RealFamilyTrophy1 = "trophy.family.flora.1";

    [Fact]
    public void ClassOf_resolves_a_real_injected_concrete_id_to_trophy()
    {
        Assert.Equal(MaterialClass.Trophy, MaterialCatalog.ClassOf(RealSpeciesTrophy1));
        Assert.Equal(MaterialClass.Trophy, MaterialCatalog.ClassOf(RealFamilyTrophy1));
        Assert.True(MaterialCatalog.IsTrophyId(RealSpeciesTrophy1));
        Assert.True(MaterialCatalog.IsIssuable(RealSpeciesTrophy1));
        Assert.True(MaterialCatalog.IsKnown(RealSpeciesTrophy1));
    }

    [Theory]
    [InlineData("trophy.species.1")]
    [InlineData("trophy.family.8")]
    public void ClassOf_resolves_a_scope_token_to_trophy_without_any_registry_lookup(string token)
    {
        // A scope token is never a member of the injected registry (it names no scopeKey at all) —
        // ClassOf must still resolve it, structurally, because this is the AUTHORED shape a recipe
        // corpus row carries before a piece's species is known.
        Assert.False(MaterialCatalog.IsTrophyId(token));
        Assert.Equal(MaterialClass.Trophy, MaterialCatalog.ClassOf(token));
    }

    [Fact]
    public void ClassOf_throws_for_a_concrete_id_the_registry_does_not_hold()
    {
        // Slot 99 -- perSpecies is 2, so this id was never minted.
        Assert.Throws<MaterialVocabularyRejection>(
            () => MaterialCatalog.ClassOf("trophy.species.abyssswordstar.99"));
        Assert.Throws<MaterialVocabularyRejection>(
            () => MaterialCatalog.ClassOf("trophy.species.not-a-real-species.1"));
    }

    [Theory]
    [InlineData("trophy.general.1")]     // R9: no general scope word
    [InlineData("trophy.species.0")]     // slot must be >= 1
    [InlineData("trophy.species.-1")]
    [InlineData("trophy.species.abyssswordstar")]  // 3 dots' worth of shape, non-numeric segment
    [InlineData("trophy.")]
    public void ClassOf_throws_for_a_malformed_trophy_ish_id(string materialId)
    {
        Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.ClassOf(materialId));
    }

    [Theory]
    [InlineData("trophy.species.1", "species", 1)]
    [InlineData("trophy.family.8", "family", 8)]
    public void TryParseTrophyScopeToken_accepts_the_two_legal_shapes(string token, string wantScope, int wantSlot)
    {
        Assert.True(MaterialCatalog.TryParseTrophyScopeToken(token, out var scope, out var slot));
        Assert.Equal(wantScope, scope);
        Assert.Equal(wantSlot, slot);
    }

    [Theory]
    [InlineData("trophy.general.1")]
    [InlineData("trophy.species.0")]
    [InlineData("trophy.species.-1")]
    [InlineData("trophy.species.abyssswordstar")]
    [InlineData("trophy.species.abyssswordstar.1")]  // a CONCRETE id, not a scope token — 4 parts
    [InlineData("shard.chaff")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseTrophyScopeToken_refuses_everything_else(string? materialId)
    {
        Assert.False(MaterialCatalog.TryParseTrophyScopeToken(materialId, out _, out _));
    }

    [Fact]
    public void ComposeTrophyId_is_the_scope_tokens_own_inverse()
    {
        Assert.Equal(RealSpeciesTrophy1, MaterialCatalog.ComposeTrophyId("species", "abyssswordstar", 1));
        Assert.Equal(RealFamilyTrophy1, MaterialCatalog.ComposeTrophyId("family", "flora", 1));
    }

    [Fact]
    public void ParseTrophyRegistryIds_reads_the_entries_array()
    {
        var ids = MaterialCatalog.ParseTrophyRegistryIds(
            """{"_meta": {"schemaVersion": 1}, "entries": [{"materialId": "trophy.species.foo.1", "scope": "species", "scopeKey": "foo", "slot": 1}, {"materialId": "trophy.family.bar.3", "scope": "family", "scopeKey": "bar", "slot": 3}]}""");
        Assert.Equal(new HashSet<string> { "trophy.species.foo.1", "trophy.family.bar.3" }, ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"entries": "not-an-array"}""")]
    [InlineData("""{"entries": [{"scope": "species"}]}""")]  // missing materialId
    [InlineData("""{"entries": [{"materialId": "a"}, {"materialId": "a"}]}""")]  // duplicate
    public void ParseTrophyRegistryIds_refuses_malformed_input(string json)
    {
        Assert.Throws<MaterialVocabularyRejection>(() => MaterialCatalog.ParseTrophyRegistryIds(json));
    }
}
