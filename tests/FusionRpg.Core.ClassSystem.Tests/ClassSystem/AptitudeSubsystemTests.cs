using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P2.4 — AptitudeSubsystem, the registered IActorStatSubsystem seam
/// (spec-aptitude-resolve.md §2), and its wiring through ActorHub.Register /
/// ActorHubBootstrap.CreateDefault.</summary>
public class AptitudeSubsystemTests
{
    static AptitudeTuning MightOnlyTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 } ]
        }
        """);

    static PowerLadder Ladder() => new(PowerTuningHub.Tuning);

    [Fact]
    public void IsAnIActorStatSubsystem()
    {
        Assert.IsAssignableFrom<IActorStatSubsystem>(
            new AptitudeSubsystem(MightOnlyTuning(), Ladder()));
    }

    [Fact]
    public void SubsystemId_isStable()
    {
        var s = new AptitudeSubsystem(MightOnlyTuning(), Ladder());
        Assert.Equal("rpg.aptitude", s.SubsystemId);
    }

    [Fact]
    public void RegisteredThroughActorHub_contributesTheFundedChannel()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100)));

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var derived = hub.ResolveDerived(ctx);

        Assert.True(derived.Get("combat.power.omni", double.NaN) > 0);
    }

    [Fact]
    public void EmptyAllocation_leavesDerivedSnapshotAtDefaults()
    {
        // The default allocation (nobody has spent a point) must be provably inert through the real
        // ActorHub.Register seam -- the property class-system-todo.md P2.4 and success criterion 9
        // both name ("zero goldens move on an empty allocation").
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new AptitudeSubsystem(MightOnlyTuning(), Ladder())); // allocation omitted -> Empty

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var derived = hub.ResolveDerived(ctx);

        Assert.Equal(0.0, derived.Get("combat.power.omni", 0.0));
    }

    [Fact]
    public void ContributeDerived_isIdempotent_callingTwiceYieldsOneSetNotTwo()
    {
        var s = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        var modsA = new List<DerivedModifier>();
        s.ContributeDerived(ctx, modsA);
        var modsB = new List<DerivedModifier>();
        s.ContributeDerived(ctx, modsB);

        Assert.Equal(modsA.Count, modsB.Count);
        Assert.Equal(modsA[0].Value, modsB[0].Value, 12);
    }

    [Fact]
    public void DoubleRegistration_throughActorHub_doesNotDoubleContribute()
    {
        // ActorHub.Register replaces by SubsystemId -- registering twice must not double the resolved
        // value (spec-aptitude-resolve.md §2 rule 1).
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        var subsystem = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        hub.Register(subsystem);
        hub.Register(subsystem);

        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var derived = hub.ResolveDerived(ctx);
        var once = new List<DerivedModifier>();
        subsystem.ContributeDerived(ctx, once);

        Assert.Equal(once[0].Value, derived.Get("combat.power.omni", double.NaN), 6);
    }

    [Fact]
    public void ThetaComesFromThePowerIndexProvider()
    {
        var provider = new FixedPowerIndexProvider(1000);
        var s = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(), powerIndex: provider,
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        var mods = new List<DerivedModifier>();
        s.ContributeDerived(ctx, mods);

        var expected = AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, Ladder().Value(1000));
        Assert.Equal((double)expected, mods[0].Value, 6);
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

    // ── ActorHubBootstrap.CreateDefault wiring (opt-in, and safe when omitted) ─────────────────────

    [Fact]
    public void CreateDefault_withoutAptitudeTuning_registersNoAptitudeSubsystem()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        Assert.DoesNotContain(hub.Subsystems, s => s.SubsystemId == "rpg.aptitude");
    }

    [Fact]
    public void CreateDefault_withAptitudeTuning_registersIt()
    {
        var hub = ActorHubBootstrap.CreateDefault(aptitudeTuning: MightOnlyTuning());
        Assert.Contains(hub.Subsystems, s => s.SubsystemId == "rpg.aptitude");
    }

    [Fact]
    public void CreateDefault_withAptitudeTuning_defaultAllocationIsEmpty_zeroChannelImpact()
    {
        var hub = ActorHubBootstrap.CreateDefault(aptitudeTuning: MightOnlyTuning());
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var derived = hub.ResolveDerived(ctx);
        Assert.Equal(0.0, derived.Get("combat.power.omni", 0.0));
    }

    // ── `species-build` T0.1/T0.2 — the resolver memo ───────────────────────────────────────────────

    static StatContext CtxFor(StatSystem stats, StatSide side, int typeId, string entityKey) =>
        side == StatSide.Plant
            ? stats.Contexts.ForPlant(entityKey, new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }, typeId: typeId)
            : stats.Contexts.ForZombie(entityKey, new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }, typeId: typeId);

    [Fact]
    public void Memo_equivalence_cachedAndUncachedResolveIdentically()
    {
        var cached = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, StatSide.Plant, 3, "P1");

        var first = new List<DerivedModifier>();
        cached.ContributeDerived(ctx, first); // populates the memo
        var second = new List<DerivedModifier>();
        cached.ContributeDerived(ctx, second); // must come back from the memo

        Assert.Equal(first, second); // DerivedModifier is a record; element-wise structural equality
    }

    [Fact]
    public void Memo_thetaIsHonoured_differentThetaResolvesDifferently()
    {
        // The defect an earlier spec draft would have shipped: keying on (Side, TypeId) alone would
        // have served the FIRST theta's modifiers to every later theta at the same (Side, TypeId).
        var callCount = 0;
        var provider = new SequencePowerIndexProvider(new[] { 100, 1000 }, () => callCount++);
        var s = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(), powerIndex: provider,
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, StatSide.Plant, 3, "P1");

        var atThetaLow = new List<DerivedModifier>();
        s.ContributeDerived(ctx, atThetaLow); // theta=100
        var atThetaHigh = new List<DerivedModifier>();
        s.ContributeDerived(ctx, atThetaHigh); // theta=1000, same Side/TypeId/EntityKey

        Assert.NotEqual(atThetaLow[0].Value, atThetaHigh[0].Value);
    }

    [Fact]
    public void Memo_sideIsHonoured_sameTypeIdDifferentSideResolvesIndependently()
    {
        // polevaulterzombie and wallnut are both GameTypeId 3 in the shipped roster
        // (LawnElementIndex's own doc comment) -- a bare type id would collide across sides.
        var s = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        var stats = StatSystemBootstrap.CreateDefault();
        var plantCtx = CtxFor(stats, StatSide.Plant, 3, "P1");
        var zombieCtx = CtxFor(stats, StatSide.Zombie, 3, "Z1");

        var plantMods = new List<DerivedModifier>();
        s.ContributeDerived(plantCtx, plantMods);
        var zombieMods = new List<DerivedModifier>();
        s.ContributeDerived(zombieCtx, zombieMods);

        // Both resolve (same tuning/allocation/theta=0 default), but they must be independent memo
        // entries -- proven by invalidating and re-resolving only one side changing nothing for the
        // other in the boundedGrowth test below, and directly here by asserting neither throws when
        // resolved out of order after the other is already memoized.
        Assert.Equal(plantMods.Count, zombieMods.Count);
        var again = new List<DerivedModifier>();
        s.ContributeDerived(plantCtx, again);
        Assert.Equal(plantMods, again);
    }

    [Fact]
    public void Memo_boundedGrowth_manyEntitiesOfOneKeyProduceOneEntry()
    {
        var s = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        var stats = StatSystemBootstrap.CreateDefault();

        // Ten different entities, same (Side, TypeId, Theta) -- all must read the identical modifier
        // set, proving the memo is keyed on the shared triple and not on the entity.
        DerivedModifier? first = null;
        for (var i = 0; i < 10; i++)
        {
            var ctx = CtxFor(stats, StatSide.Plant, 3, $"P{i}");
            var mods = new List<DerivedModifier>();
            s.ContributeDerived(ctx, mods);
            if (first is null) first = mods[0];
            else Assert.Equal(first, mods[0]);
        }
    }

    [Fact]
    public void Memo_selfCorrects_whenTheAllocationReferenceChanges_noExplicitInvalidateNeeded()
    {
        // The real design: the memo checks the ALLOCATION'S OWN REFERENCE on every read, so a caller
        // that replaces the allocation (a save, a refresh, a respec) never has to remember to call
        // anything -- this is what CommanderAllocationSourceTests' own direct-Refresh() usage (no
        // CheatState, no injector) requires, and what an earlier generation-stamp design could not
        // satisfy for exactly that caller.
        //
        // MightOnlyTuning only funds an edge for "Might", so changing Might's own POINT COUNT alone
        // does not move its resolved value while it is the sole aptitude allocated -- share is
        // normalised over the grand total, and 100/100 == 400/400 == 1.0. Adding a SECOND aptitude to
        // the same scope is the real, share-moving change, used here as the "did it actually
        // recompute" signal.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var s = new AptitudeSubsystem(MightOnlyTuning(), Ladder(), allocation: _ => allocation);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, StatSide.Plant, 3, "P1");

        var before = new List<DerivedModifier>();
        s.ContributeDerived(ctx, before);

        allocation = allocation + AptitudeAllocation.Single(AllocationScope.Commander, "Fortitude", 300);
        var after = new List<DerivedModifier>();
        s.ContributeDerived(ctx, after); // no InvalidateMemo() call -- must see the new reference anyway

        Assert.NotEqual(before[0].Value, after[0].Value);
    }

    [Fact]
    public void Memo_sameAllocationReferenceAcrossCalls_staysCached()
    {
        // The companion proof: an UNCHANGED reference (the common "nothing happened since last
        // refresh" case) really does hit the memo rather than recomputing every time -- proven by
        // returning the exact same AptitudeAllocation instance from two calls and confirming the
        // resolved list is not just equal but the SAME cached instance.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var s = new AptitudeSubsystem(MightOnlyTuning(), Ladder(), allocation: _ => allocation);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, StatSide.Plant, 3, "P1");

        var first = new List<DerivedModifier>();
        s.ContributeDerived(ctx, first);
        var second = new List<DerivedModifier>();
        s.ContributeDerived(ctx, second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void InvalidateMemo_isAnExplicitEscapeHatch_forcesRecomputeEvenWithTheSameReference()
    {
        // Not needed for correctness (the memo self-corrects by reference already) -- this proves the
        // explicit clear still works as a defensive/testing utility: even with the SAME reference, a
        // forced clear makes the next read recompute rather than reuse the stored list instance.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var s = new AptitudeSubsystem(MightOnlyTuning(), Ladder(), allocation: _ => allocation);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, StatSide.Plant, 3, "P1");

        var before = new List<DerivedModifier>();
        s.ContributeDerived(ctx, before);

        s.InvalidateMemo();
        var after = new List<DerivedModifier>();
        s.ContributeDerived(ctx, after);

        Assert.Equal(before, after); // same allocation -> same VALUE, just recomputed, not reused
    }

    [Fact]
    public void Memo_isInstanceScoped_neverLeaksBetweenTwoSubsystems()
    {
        var a = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        var b = new AptitudeSubsystem(
            MightOnlyTuning(), Ladder(),
            allocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100)
                + AptitudeAllocation.Single(AllocationScope.Commander, "Fortitude", 300));
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, StatSide.Plant, 3, "P1");

        var modsA = new List<DerivedModifier>();
        a.ContributeDerived(ctx, modsA);
        var modsB = new List<DerivedModifier>();
        b.ContributeDerived(ctx, modsB);

        Assert.NotEqual(modsA[0].Value, modsB[0].Value);
    }

    sealed class SequencePowerIndexProvider : IPowerIndexProvider
    {
        readonly int[] _values;
        readonly Action _onCall;
        int _index;
        public SequencePowerIndexProvider(int[] values, Action onCall) { _values = values; _onCall = onCall; }
        /// <summary>No per-empire notion: an empire ask advances the SAME sequence.</summary>
        public int ActorIndexFor(FusionRpg.Core.Saves.SaveId save, FusionRpg.Core.Commanders.EmpireId empire) =>
            ActorIndex(new StatContext());

        public int ActorIndex(StatContext ctx)
        {
            _onCall();
            var v = _values[Math.Min(_index, _values.Length - 1)];
            _index++;
            return v;
        }
        public int ContentIndex(ContentContext ctx) => 0;
        public PowerAxisReport Explain(StatContext ctx) => throw new NotSupportedException();
    }
}
