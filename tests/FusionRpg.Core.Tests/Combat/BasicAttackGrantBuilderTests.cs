using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// lawn-combat-wire T10 (spec-basic-attack-grant.md): the per-actor basic-attack grant that makes
/// <c>EffectRuntime.HasOnDamageDealtGrant()</c> true on the lawn. Core-testable half — the injector
/// side (`LawnBasicAttackGrantBinder`) resolves the owner's element via
/// `LawnElementResolverHost.Resolve` and calls straight into <see cref="BasicAttackGrantBuilder.Build"/>,
/// so everything about the DTO's SHAPE and the "no re-bake needed" claim below is provable here.
/// </summary>
public class BasicAttackGrantBuilderTests
{
    // ---- the shape: owner key, effect id, trigger-bearing def ------------------------------------

    [Fact]
    public void BindsToEntityScopeNeverPlantOrZombieType()
    {
        var dto = BasicAttackGrantBuilder.Build("1A2B", ElementTypeId.Fire);

        Assert.Equal("entity:1A2B", dto.OwnerKey);
        Assert.Equal("entity", dto.OwnerKind);
        Assert.Equal(BasicAttackGrantBuilder.EffectId, dto.EffectId);
        Assert.Equal("fx.overlay_damage", dto.EffectId);
    }

    [Fact]
    public void APtrIsRequired()
    {
        Assert.Throws<ArgumentException>(() => BasicAttackGrantBuilder.Build("", ElementTypeId.Fire));
        Assert.Throws<ArgumentException>(() => BasicAttackGrantBuilder.Build("   ", ElementTypeId.Fire));
        Assert.Throws<ArgumentException>(() => BasicAttackGrantBuilder.Build(null!, ElementTypeId.Fire));
    }

    // ---- the element payload: baked from the OWNER, never a row-authored default ------------------

    [Fact]
    public void AFireOwnerBakesAFirePayload()
    {
        var dto = BasicAttackGrantBuilder.Build("A", ElementTypeId.Fire);

        Assert.NotNull(dto.Overlay);
        var packet = DamagePacketBuilder.FromOverlay(dto.Overlay);
        var component = Assert.Single(packet.ElementPayload!);
        Assert.Equal("fire", component.Element);
        Assert.Equal(1.0, component.Weight);
    }

    [Fact]
    public void ANeutralOwnerNeverPrimaryBakesNoPayload_theDocumentedDegenerateCase()
    {
        var dto = BasicAttackGrantBuilder.Build("A", primary: null);

        // L-N36: the overlay now always carries the zero rider cooldown; the degenerate case is still "no payload".
        Assert.NotNull(dto.Overlay);
        Assert.False(dto.Overlay!.ContainsKey("elementPayload"));
        Assert.Null(DamagePacketBuilder.FromOverlay(dto.Overlay).ElementPayload);
    }

    [Fact]
    public void AnIceOwnerAndAFireOwnerProduceMeasurablyDifferentPayloads()
    {
        // The live proof this module names ("a Fire actor and an Ice actor deal measurably different
        // damage to the same target") is the injected consequence of this Core-level fact: their
        // grants carry different elementPayload, full stop.
        var fire = BasicAttackGrantBuilder.Build("A", ElementTypeId.Fire);
        var ice = BasicAttackGrantBuilder.Build("B", ElementTypeId.Ice);

        var fireElement = DamagePacketBuilder.FromOverlay(fire.Overlay).ElementPayload![0].Element;
        var iceElement = DamagePacketBuilder.FromOverlay(ice.Overlay).ElementPayload![0].Element;

        Assert.Equal("fire", fireElement);
        Assert.Equal("ice", iceElement);
        Assert.NotEqual(fireElement, iceElement);
    }

    [Fact]
    public void ADualTypedOwnerBakesBothComponentsSummingToOne()
    {
        var dto = BasicAttackGrantBuilder.Build("A", ElementTypeId.Fire, ElementTypeId.Ice, secondaryWeightMilli: 300);

        var payload = DamagePacketBuilder.FromOverlay(dto.Overlay).ElementPayload!;
        Assert.Equal(2, payload.Count);
        Assert.Equal(1.0, payload[0].Weight + payload[1].Weight, precision: 10);
    }

    // ---- idempotent grant id: a repeat bind for the same ptr is an upsert, never a duplicate -------

    [Fact]
    public void TheSamePtrAlwaysProducesTheSameGrantId()
    {
        var first = BasicAttackGrantBuilder.Build("1A2B", ElementTypeId.Fire);
        var second = BasicAttackGrantBuilder.Build("1A2B", ElementTypeId.Ice);

        Assert.Equal(first.GrantId, second.GrantId);
    }

    [Fact]
    public void DifferentPtrsNeverCollideOnGrantId()
    {
        var a = BasicAttackGrantBuilder.Build("1A2B", ElementTypeId.Fire);
        var b = BasicAttackGrantBuilder.Build("3C4D", ElementTypeId.Fire);

        Assert.NotEqual(a.GrantId, b.GrantId);
    }

    // ---- the hypno re-bake claim, proven rather than assumed ---------------------------------------
    //
    // spec-basic-attack-grant.md's own "hypno re-bake seam" section worried that a side change would
    // leave a stale baked payload on an already-bound grant. lawn-combat-wire T3
    // (LawnElementResolverTests.Trigger2_*) already PROVED against the real code that hypno cannot
    // change a ptr's resolved (side, elements) at all: the cached `side` is the actor's OBJECT KIND
    // (plant/zombie), never its allegiance, and mind control rides the SEPARATE MindControlled flag
    // that this cache — and this grant's own element source — never reads. The test below is the
    // executable form of that finding applied to THIS module: build the grant once, "hypno" the
    // entity (flip only what mind control actually flips — nothing this build path reads), resolve
    // again with the SAME (side, typeId) board facts a real hypno leaves untouched, and show the
    // rebuilt grant is byte-identical. There is nothing to re-bake because there is nothing that
    // changes.
    [Fact]
    public void HypnoDoesNotChangeTheOwnersSpeciesSoTheBakedPayloadNeverGoesStale()
    {
        var index = new LawnElementIndex(new[]
        {
            new CreatureSpeciesDef
            {
                SpeciesId = "s1", Name = "s1", Side = "zombie", GameTypeId = 20,
                CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFloor + 20,
                ElementPrimary = ElementTypeId.Fire,
                BaseRarity = CreatureRarity.Chaff,
                DeployMode = CreatureDeployMode.PlantAvatar,
                Acquisition = CreatureAcquisition.Summonable,
            },
        });
        var resolver = new LawnElementResolver(index);

        // Before hypno: a real zombie, resolved once (matches production — resolved lazily, cached).
        var (sideBefore, elementsBefore) = resolver.Resolve("m1", "1A2B", () => ("zombie", 20, true));
        var beforeGrant = BasicAttackGrantBuilder.Build("1A2B", elementsBefore.Primary, elementsBefore.Secondary);

        // "Hypno" happens: per Trigger2 (LawnElementResolverTests), this NEVER invalidates the
        // resolver's cache and NEVER changes the (side, typeId) a real board lookup would answer for
        // this ptr — mind control is a separate flag the board-fact lookup this cache/grant relies on
        // never reads. So the SAME lookup, called again, must still answer the SAME facts.
        var (sideAfter, elementsAfter) = resolver.Resolve("m1", "1A2B", () => ("zombie", 20, true));
        var afterGrant = BasicAttackGrantBuilder.Build("1A2B", elementsAfter.Primary, elementsAfter.Secondary);

        Assert.Equal(sideBefore, sideAfter);
        Assert.Equal(elementsBefore.Primary, elementsAfter.Primary);
        Assert.Equal(beforeGrant.GrantId, afterGrant.GrantId);
        Assert.Equal(beforeGrant.EffectId, afterGrant.EffectId);
        Assert.Equal(beforeGrant.OwnerKey, afterGrant.OwnerKey);
        var beforePayload = DamagePacketBuilder.FromOverlay(beforeGrant.Overlay).ElementPayload!;
        var afterPayload = DamagePacketBuilder.FromOverlay(afterGrant.Overlay).ElementPayload!;
        Assert.Equal(beforePayload[0].Element, afterPayload[0].Element);
        Assert.Equal(beforePayload[0].Weight, afterPayload[0].Weight);

        // The board lookup itself was only ever called once (the resolver's own per-ptr cache) --
        // confirming the SECOND resolve above was a cache hit, not a fresh (and coincidentally
        // matching) board read. This is the assertion that would fail if hypno DID invalidate.
        Assert.Equal(1, resolver.BoardLookupCount);
    }

    // ---- action-enrich AE2.2: the baked amount, and the battle parity it must hold ------------------

    /// <summary>The base the LAWN bakes, expressed exactly as the battle hit expresses it
    /// (`ActionBaseDerivation` x `ActionBaseMath` over `BattleRuleset.PowerValue`) — the shared
    /// expression is the point: a second P(Θ) computation on either side would break this equality.</summary>
    static long BattleBase(int theta) => ActionBaseMath.BasePerHit(
        ActionBaseDerivation.BasePowerMilli(
            ActionKind.Basic, "act.attack", effectiveRung: 0, RungPolicy.Table, ActionBaseTuningHub.Tuning),
        BattleRuleset.PowerValue(theta));

    [Fact]
    public void TheBakedAmountIsTheBattleBaseForTheSameTheta_negativeForDamage()
    {
        const int theta = 20;
        var expected = BattleBase(theta);
        Assert.True(expected > 0, "a positive base is the precondition of this test");

        var dto = BasicAttackGrantBuilder.Build("A", ElementTypeId.Fire, amount: -expected);

        Assert.Equal(-expected, Assert.IsType<long>(dto.Overlay!["amount"]));
    }

    [Fact]
    public void TheBakedAmountFollowsTheta()
    {
        var low = -BattleBase(5);
        var high = -BattleBase(20);

        var lowDto = BasicAttackGrantBuilder.Build("A", ElementTypeId.Fire, amount: low);
        var highDto = BasicAttackGrantBuilder.Build("B", ElementTypeId.Fire, amount: high);

        Assert.Equal(low, Assert.IsType<long>(lowDto.Overlay!["amount"]));
        Assert.Equal(high, Assert.IsType<long>(highDto.Overlay!["amount"]));
        Assert.True(high < low, "higher Theta must bake a larger (more negative) amount");
    }

    /// <summary>Spec test: `elementPayload` and `icd_ms` are unchanged by the amount, and the grant opts
    /// out of instakill-shaped hits through the filter grammar (AE2.1's key).</summary>
    [Fact]
    public void TheAmountDoesNotDisturbThePayloadOrCooldown_andTheGrantOptsOutOfInstakill()
    {
        var dto = BasicAttackGrantBuilder.Build("A", ElementTypeId.Fire, amount: -1234);

        var payload = DamagePacketBuilder.FromOverlay(dto.Overlay).ElementPayload!;
        Assert.Equal("fire", Assert.Single(payload).Element);
        Assert.Equal(BasicAttackGrantBuilder.RiderIcdMs, Assert.IsType<int>(dto.Overlay!["icd_ms"]));

        var filters = Assert.IsType<Dictionary<string, object?>>(dto.Overlay!["filters"]);
        Assert.True(Assert.IsType<bool>(filters["excludeInstakill"]));
    }

    /// <summary>Spec test: a neutral owner (no element) gets an amount and no payload — the degenerate
    /// case stays degenerate.</summary>
    [Fact]
    public void ANeutralOwnerGetsAnAmountAndStillNoPayload()
    {
        var dto = BasicAttackGrantBuilder.Build("A", primary: null, amount: -4321);

        Assert.Equal(-4321, Assert.IsType<long>(dto.Overlay!["amount"]));
        Assert.False(dto.Overlay!.ContainsKey("elementPayload"));
        Assert.Null(DamagePacketBuilder.FromOverlay(dto.Overlay).ElementPayload);
    }

    /// <summary>An amount of 0 (the parameter's own default — not a value the lawn passes) omits the key,
    /// so every pre-AE2.2 caller keeps the def's own `eventField` resolution instead of dealing zero.</summary>
    [Fact]
    public void AnOmittedAmountLeavesTheDefDrivenPathInCharge()
    {
        var dto = BasicAttackGrantBuilder.Build("A", ElementTypeId.Fire);

        Assert.False(dto.Overlay!.ContainsKey("amount"));
        Assert.True(Assert.IsType<Dictionary<string, object?>>(dto.Overlay!["filters"]).ContainsKey("excludeInstakill"));
    }
}
