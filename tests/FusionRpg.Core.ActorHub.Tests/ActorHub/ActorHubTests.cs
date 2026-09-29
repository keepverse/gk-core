using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

public class ActorHubResolveTests
{
    [Fact]
    public void Resolve_derived_stub_tier_power()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new RpgProgressionSubsystem());

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var result = hub.Resolve(ctx);

        // T3.2: progression.power reads Theta via IPowerIndexProvider now, defaulting to
        // StubPowerIndexProvider (Theta=0) -- the retired curve's "level<=0 -> 1.0" is gone, so the
        // un-hydrated default genuinely is 0, not 1. TierPower = power * realm = 0 * 1.0 = 0.
        Assert.Equal(100, result.RuntimePrimary.Hp);
        Assert.Equal(100, result.AppliedCombat.Hp);
        Assert.Equal(0.0, result.Derived.TierPower);
        Assert.Equal(0.0, result.Derived.Get(DerivedStatChannels.ProgressionPower));
    }

    [Fact]
    public void ResolveDerived_fresh_each_call()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var a = hub.ResolveDerived(ctx);
        var b = hub.ResolveDerived(ctx);
        Assert.Equal(a.TierPower, b.TierPower);
    }

    [Fact]
    public void Applied_combat_matches_primary_without_bonus_level()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = ActorHubBootstrap.CreateDefault(stats);
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 },
            cheatScale: new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f } });

        var result = hub.Resolve(ctx);
        Assert.Equal(result.RuntimePrimary.Hp, result.AppliedCombat.Hp);
        Assert.Equal(result.RuntimePrimary.Atk, result.AppliedCombat.Atk);
    }

    [Fact]
    public void Applied_combat_includes_progression_bonus_flats()
    {
        // class-system-todo.md P3.3 (2026-08-27): progression.bonus.* is allocation-sourced now --
        // RpgProgressionSubsystem's retired level-gated stub used to be what this test exercised
        // directly. AptitudeSubsystem is the new feeder, through the same ActorHub.Register seam.
        var tuning = AptitudeTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
              "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
              "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
              "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
              "familyRead": { "progression.bonus.maxHp": "magnitude", "progression.bonus.defense": "magnitude", "progression.bonus.atk": "magnitude" },
              "edges": [
                { "channel": "progression.bonus.maxHp", "source": "Vigor", "kMilli": 12000 },
                { "channel": "progression.bonus.defense", "source": "Might", "kMilli": 10000 },
                { "channel": "progression.bonus.atk", "source": "Might", "kMilli": 10000 }
              ]
            }
            """);
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 50)
                        + AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50);
        var ladder = new PowerLadder(PowerTuningHub.Tuning);
        const int theta = 100;

        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new AptitudeSubsystem(tuning, ladder, new FixedPowerIndexProvider(theta), _ => allocation));

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var result = hub.Resolve(ctx);

        var pTheta = ladder.Value(theta);
        var expectedBonusMaxHp = (double)AptitudeReadFunctions.Magnitude(12000, 0.5, 1000, pTheta);
        var expectedBonusDefense = (double)AptitudeReadFunctions.Magnitude(10000, 0.5, 1000, pTheta);

        Assert.Equal(100 + expectedBonusMaxHp, result.AppliedCombat.MaxHp);
        Assert.Equal(100 + expectedBonusMaxHp, result.AppliedCombat.Hp);
        // DefenseFlat's primary is 0, unlike Atk's 10 -- the bonus IS the applied value here.
        Assert.Equal(expectedBonusDefense, result.AppliedCombat.DefenseFlat);
        Assert.Equal(100, result.RuntimePrimary.MaxHp);
        // `progression.bonus.atk` retired 2026-09-18 (`retire-atk`): this fixture deliberately keeps an
        // atk edge above to prove the drop, so Atk must equal the primary EXACTLY, not primary + bonus.
        Assert.Equal(result.RuntimePrimary.Atk, result.AppliedCombat.Atk);
    }

    /// <summary>bound-loadout-hub (T14) end-to-end: the EXACT grant shape
    /// <c>UniqueBoundLoadout.GrantBonus</c> produces (ownerKind "entity", overlay
    /// <c>derived.channel</c>/<c>derived.op</c>/<c>derived.amount</c>, no catalog EffectDef) read
    /// through <see cref="GrantedDerivedAtomReader"/> as a <c>boundDerivedAtoms</c> contributor —
    /// proving the durable-grant replacement for the old Writer-absolute path actually reaches
    /// <see cref="ActorResolveResult.AppliedCombat"/>, the same assertion
    /// <see cref="Applied_combat_includes_progression_bonus_flats"/> makes for the aptitude feeder.</summary>
    [Fact]
    public void Applied_combat_includes_a_unique_bound_loadout_grant_shaped_bonus()
    {
        // (Was `progression.bonus.atk` until 2026-09-18, when the owner retired that channel --
        // solid-enforcement `retire-atk` R4. A grant naming it now fails LOUDLY at compose time
        // (UnknownDerivedChannelException), which is the point of unregistering rather than
        // zero-filling. This test proves the grant SHAPE reaches AppliedCombat, a property of the
        // reader, so any surviving bridge channel proves it identically.)
        const string ptr = "1A2B";
        var store = new FakeEntityGrantStore().Add(
            EffectOwnerKeys.Entity(ptr), "progression.bonus.defense", "flat", 15.0, grantId: "unique.bound.defense:" + ptr);
        store.Add(EffectOwnerKeys.Entity(ptr), "progression.bonus.maxHp", "flat", 200.0, grantId: "unique.bound.maxhp:" + ptr);

        var stats = StatSystemBootstrap.CreateDefault();
        var hub = ActorHubBootstrap.CreateDefault(stats,
            boundDerivedAtoms: ctx => GrantedDerivedAtomReader.Read(store, ctx));

        var ctx = stats.Contexts.ForPlant(ptr, new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 40 });
        var result = hub.Resolve(ctx);

        Assert.Equal(300 + 200, result.AppliedCombat.MaxHp);
        Assert.Equal(300 + 200, result.AppliedCombat.Hp);
        Assert.Equal(0 + 15, result.AppliedCombat.DefenseFlat);
        // Never leaks onto an unrelated entity -- the whole point of the entity: {ptr} scope.
        var other = stats.Contexts.ForPlant("OTHER", new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 40 });
        var otherResult = hub.Resolve(other);
        Assert.Equal(300, otherResult.AppliedCombat.MaxHp);
        Assert.Equal(0, otherResult.AppliedCombat.DefenseFlat);
    }

    /// <summary>Minimal entity-scoped store for the test above -- mirrors
    /// <c>GrantedDerivedAtomReaderTests.FakeGrantStore</c>'s shape (kept local rather than shared
    /// across test assemblies for one field).</summary>
    sealed class FakeEntityGrantStore : IEffectGrantStore
    {
        readonly List<EffectGrant> _grants = new();

        public FakeEntityGrantStore Add(string ownerKey, string channel, string op, object amount, string grantId)
        {
            _grants.Add(new EffectGrant
            {
                GrantId = grantId,
                EffectId = "unique.bound.derived",
                OwnerKind = "entity",
                OwnerKey = ownerKey,
                Overlay = new Dictionary<string, object?>
                {
                    [GrantedDerivedAtomReader.ChannelKey] = channel,
                    [GrantedDerivedAtomReader.OpKey] = op,
                    [GrantedDerivedAtomReader.AmountKey] = amount,
                },
            });
            return this;
        }

        public IReadOnlyList<EffectGrant> ForOwner(string? ownerKind, string ownerKey) =>
            _grants.Where(g =>
                string.Equals(g.OwnerKind, ownerKind, StringComparison.Ordinal) &&
                string.Equals(g.OwnerKey, ownerKey, StringComparison.Ordinal)).ToList();

        public EffectGrant? Get(string grantId) => _grants.FirstOrDefault(g => g.GrantId == grantId);
        public IReadOnlyList<EffectGrant> All() => _grants;
        public IReadOnlyList<EffectGrant> Matching(EffectEventDto ev) => _grants;
        public void Upsert(EffectGrant grant) => _grants.Add(grant);
        public bool Withdraw(string grantId) => _grants.RemoveAll(g => g.GrantId == grantId) > 0;
        public void Clear() => _grants.Clear();
    }

    [Fact]
    public void Resolve_preserves_neutral_element_types_by_default()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        var result = hub.Resolve(ctx);

        Assert.True(result.ElementTypes.IsNeutral);
        Assert.Null(result.ElementTypes.Primary);
        Assert.Null(result.ElementTypes.Secondary);
    }

    [Fact]
    public void Resolve_preserves_explicit_element_types()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        var ctx = hub.Stats.Contexts.ForZombie(
            "Z1",
            new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 },
            elementTypes: ActorElementTypes.Create(ElementTypeId.Fire, ElementTypeId.Air));

        var result = hub.Resolve(ctx);

        Assert.Equal(ElementTypeId.Fire, result.ElementTypes.Primary);
        Assert.Equal(ElementTypeId.Air, result.ElementTypes.Secondary);
    }

    [Fact]
    public void Actor_element_types_parse_neutral_when_empty()
    {
        var types = ActorElementTypes.Parse(null, "");
        Assert.True(types.IsNeutral);
    }

    [Fact]
    public void Actor_element_types_reject_duplicate_slots()
    {
        Assert.Throws<ArgumentException>(() => ActorElementTypes.Create(ElementTypeId.Fire, ElementTypeId.Fire));
    }

    [Fact]
    public void Actor_element_types_reject_omni_slot()
    {
        Assert.Throws<ArgumentException>(() => ActorElementTypes.Parse("omni", null));
    }

    [Fact]
    public void Actor_element_types_reject_unknown_slot()
    {
        Assert.Throws<ArgumentException>(() => ActorElementTypes.Parse("storm", null));
    }

    [Fact]
    public void Combat_policies_match_docs()
    {
        Assert.Equal(0.25, ElementMatchupPolicy.MatchupShareK);
        Assert.Equal(100.0, CombatProbabilityPolicy.AccuracyScale);
        Assert.Equal(100.0, CombatProbabilityPolicy.CritRateScale);
        Assert.Equal(100.0, CombatProbabilityPolicy.CritDamageScale);
        Assert.Equal(1.0, CombatProbabilityPolicy.Steepness);
    }

    [Fact]
    public void Parse_single_type_fire_only()
    {
        var types = ActorElementTypes.Parse("fire", null);
        Assert.Equal(ElementTypeId.Fire, types.Primary);
        Assert.Null(types.Secondary);
        Assert.False(types.IsNeutral);
    }

    [Fact]
    public void Parse_rejects_secondary_without_primary()
    {
        Assert.Throws<ArgumentException>(() => ActorElementTypes.Parse(null, "ice"));
    }

    [Fact]
    public void Parse_round_trips_lowercase_element_ids()
    {
        var types = ActorElementTypes.Parse("fire", "ice");
        Assert.Equal("fire", types.Primary!.Value.ToElementId());
        Assert.Equal("ice", types.Secondary!.Value.ToElementId());
    }

    [Fact]
    public void Element_metadata_keys_match_docs()
    {
        Assert.Equal("element.type.primary", ElementMetadataKeys.Primary);
        Assert.Equal("element.type.secondary", ElementMetadataKeys.Secondary);
    }

    [Fact]
    public void StatContext_factory_defaults_neutral_types()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        Assert.True(ctx.ElementTypes.IsNeutral);
    }

    [Fact]
    public void Element_types_do_not_change_applied_combat()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = ActorHubBootstrap.CreateDefault(stats);
        var neutralCtx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 200, MaxHp = 200, Atk = 15 });
        var typedCtx = stats.Contexts.ForPlant(
            "P2",
            new EntityBaseline { Hp = 200, MaxHp = 200, Atk = 15 },
            elementTypes: ActorElementTypes.Create(ElementTypeId.Fire, ElementTypeId.Ice));

        var neutral = hub.Resolve(neutralCtx);
        var typed = hub.Resolve(typedCtx);

        Assert.Equal(neutral.AppliedCombat.Hp, typed.AppliedCombat.Hp);
        Assert.Equal(neutral.AppliedCombat.Atk, typed.AppliedCombat.Atk);
        Assert.Equal(neutral.RuntimePrimary.Hp, typed.RuntimePrimary.Hp);
    }

    [Fact]
    public void SeedResourceBaseline_sets_resource_max_for_all_six_ids()
    {
        var power = new FixedPowerIndexProvider(theta: 50);
        var hub = ActorHubBootstrap.CreateDefault(powerIndex: power, seedResourceBaseline: true);
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 500, MaxHp = 500, Atk = 20 });
        var snap = hub.ResolveDerived(ctx);

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            var max = ResourceChannelReader.Max(snap, id);
            Assert.True(max > 0, $"resource.max.{id} must be Hub-seeded > 0, got {max}");
            if (id == "hp")
                Assert.Equal(500, max);
        }

        Assert.Contains(hub.Subsystems, s => s.SubsystemId == ContributionSourceIds.ResourceBaseline);
    }

    [Fact]
    public void Bare_CreateDefault_does_not_seed_resource_max()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var snap = hub.ResolveDerived(ctx);
        Assert.Equal(0, ResourceChannelReader.Max(snap, "hp"));
        Assert.DoesNotContain(hub.Subsystems, s => s.SubsystemId == ContributionSourceIds.ResourceBaseline);
    }

    sealed class FixedPowerIndexProvider : IPowerIndexProvider
    {
        readonly int _theta;
        public FixedPowerIndexProvider(int theta) => _theta = theta;
        public int ActorIndex(StatContext ctx) => _theta;
        public int ContentIndex(ContentContext ctx) => _theta;
        public PowerAxisReport Explain(StatContext ctx) => throw new NotSupportedException();
        public int ActorIndexFor(FusionRpg.Core.Saves.SaveId save, FusionRpg.Core.Commanders.EmpireId empire) => _theta;
    }
}
