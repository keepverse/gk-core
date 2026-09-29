using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Derived;

/// <summary>species-progression module 6 (`species-layer-delivery`) step 6.2 — SpeciesLayerSubsystem,
/// the registered `rpg.species-layer` seam, and its wiring through ActorHubBootstrap.CreateDefault.
/// Mirrors AptitudeSubsystemTests' own shape, adjusted for this subsystem's memo key (the
/// ProjectedLayerRow LIST REFERENCE, never an actor identity) — see the type's own doc comment.</summary>
public class SpeciesLayerSubsystemTests
{
    static PowerLadder Ladder() => new(PowerTuningHub.Tuning);

    static readonly IReadOnlyList<ProjectedLayerRow> OneFixedRow = new[]
    {
        new ProjectedLayerRow("combat.power.omni", DerivedModifierOp.Flat,
            new LayerValue.Fixed(50.0), ContributionSourceIds.SpeciesBase("melon-pult")),
    };

    static IReadOnlyList<ProjectedLayerRow> OneLadderRow(long kMicro) => new[]
    {
        new ProjectedLayerRow("resource.max.hp", DerivedModifierOp.Flat,
            new LayerValue.LadderMicro(kMicro),
            ContributionSourceIds.SpeciesEmpire(EmpireId.Dave, "melon-pult", "Fortitude")),
    };

    static StatContext CtxFor(StatSystem stats, string entityKey) =>
        stats.Contexts.ForPlant(entityKey, new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

    [Fact]
    public void IsAnIActorStatSubsystem() =>
        Assert.IsAssignableFrom<IActorStatSubsystem>(new SpeciesLayerSubsystem(Ladder()));

    [Fact]
    public void SubsystemId_isStable() =>
        Assert.Equal("rpg.species-layer", new SpeciesLayerSubsystem(Ladder()).SubsystemId);

    [Fact]
    public void RegisteredThroughActorHub_contributesTheRowsChannel()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => OneFixedRow));

        var derived = hub.ResolveDerived(CtxFor(stats, "P1"));

        Assert.Equal(50.0, derived.Get("combat.power.omni", 0.0), 6);
    }

    [Fact]
    public void EmptyRows_leavesDerivedSnapshotAtDefaults()
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        hub.Register(new SpeciesLayerSubsystem(Ladder())); // rowsFor omitted -> empty

        var derived = hub.ResolveDerived(CtxFor(stats, "P1"));

        Assert.Equal(0.0, derived.Get("combat.power.omni", 0.0));
    }

    [Fact]
    public void ContributeDerived_isIdempotent_callingTwiceYieldsOneSetNotTwo()
    {
        var s = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => OneFixedRow);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, "P1");

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
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(stats);
        var subsystem = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => OneFixedRow);
        hub.Register(subsystem);
        hub.Register(subsystem);

        var derived = hub.ResolveDerived(CtxFor(stats, "P1"));

        Assert.Equal(50.0, derived.Get("combat.power.omni", 0.0), 6);
    }

    [Fact]
    public void AnEmptySourceId_isSkipped_neverMintsAnUnattributedContribution()
    {
        var rows = new[]
        {
            new ProjectedLayerRow("combat.power.omni", DerivedModifierOp.Flat, new LayerValue.Fixed(50.0), ""),
        };
        var s = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => rows);
        var stats = StatSystemBootstrap.CreateDefault();
        var mods = new List<DerivedModifier>();

        s.ContributeDerived(CtxFor(stats, "P1"), mods);

        Assert.Empty(mods);
    }

    // ── Θ read per resolve, never cached (spec: "Theta is not a trigger") ───────────────────────────

    [Fact]
    public void Theta_is_never_cached_in_species_layers()
    {
        // A rows list is Θ-free by construction (module 3); the memo must therefore re-check Θ on
        // every read even when the rows REFERENCE is unchanged, or a stale Θ-resolved value would
        // silently survive a Θ bump. OneLadderRow(2200) is pinned to ONE instance so the reference
        // stays identical across both calls, isolating Θ alone as the only thing that changed.
        var provider = new SequencePowerIndexProvider(new[] { 10, 1000 }, () => { });
        var rows = OneLadderRow(2200);
        var s = new SpeciesLayerSubsystem(Ladder(), powerIndex: provider, rowsFor: _ => rows);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, "P1");

        var atLow = new List<DerivedModifier>();
        s.ContributeDerived(ctx, atLow); // theta=10
        var atHigh = new List<DerivedModifier>();
        s.ContributeDerived(ctx, atHigh); // theta=1000, SAME rows reference

        Assert.NotEqual(atLow[0].Value, atHigh[0].Value);
    }

    // ── memo keyed on the ROWS REFERENCE, never on actor identity ───────────────────────────────────

    [Fact]
    public void Memo_boundedGrowth_manyActorsSharingOneRowsReference_produceOneEntry()
    {
        var rows = OneFixedRow; // one shared reference
        var s = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => rows);
        var stats = StatSystemBootstrap.CreateDefault();

        DerivedModifier? first = null;
        for (var i = 0; i < 10; i++)
        {
            var mods = new List<DerivedModifier>();
            s.ContributeDerived(CtxFor(stats, $"P{i}"), mods);
            if (first is null) first = mods[0];
            else Assert.Equal(first, mods[0]);
        }
    }

    [Fact]
    public void Memo_selfCorrects_whenTheRowsReferenceChanges_noExplicitInvalidateNeeded()
    {
        var rows = OneLadderRow(2200);
        var s = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => rows);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, "P1");

        var before = new List<DerivedModifier>();
        s.ContributeDerived(ctx, before);

        rows = OneLadderRow(9000); // a NEW reference, different kMicro
        var after = new List<DerivedModifier>();
        s.ContributeDerived(ctx, after); // no InvalidateMemo() call

        Assert.NotEqual(before[0].Value, after[0].Value);
    }

    [Fact]
    public void Memo_sameRowsReferenceAcrossCalls_staysCached()
    {
        var rows = OneFixedRow;
        var s = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => rows);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, "P1");

        var first = new List<DerivedModifier>();
        s.ContributeDerived(ctx, first);
        var second = new List<DerivedModifier>();
        s.ContributeDerived(ctx, second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void InvalidateMemo_isAnExplicitEscapeHatch_forcesRecomputeEvenWithTheSameReference()
    {
        var rows = OneFixedRow;
        var s = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => rows);
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, "P1");

        var before = new List<DerivedModifier>();
        s.ContributeDerived(ctx, before);

        s.InvalidateMemo();
        var after = new List<DerivedModifier>();
        s.ContributeDerived(ctx, after);

        Assert.Equal(before, after); // same rows -> same VALUE, just recomputed, not reused
    }

    [Fact]
    public void Memo_isInstanceScoped_neverLeaksBetweenTwoSubsystems()
    {
        var a = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => OneFixedRow);
        var b = new SpeciesLayerSubsystem(Ladder(), rowsFor: _ => OneLadderRow(2200));
        var stats = StatSystemBootstrap.CreateDefault();
        var ctx = CtxFor(stats, "P1");

        var modsA = new List<DerivedModifier>();
        a.ContributeDerived(ctx, modsA);
        var modsB = new List<DerivedModifier>();
        b.ContributeDerived(ctx, modsB);

        Assert.NotEqual(modsA[0].ChannelId, modsB[0].ChannelId);
    }

    // ── ActorHubBootstrap.CreateDefault wiring (opt-in, and safe when omitted) ─────────────────────

    [Fact]
    public void CreateDefault_withoutSpeciesLayers_registersNoSpeciesLayerSubsystem()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        Assert.DoesNotContain(hub.Subsystems, s => s.SubsystemId == "rpg.species-layer");
    }

    [Fact]
    public void CreateDefault_withSpeciesLayers_registersIt()
    {
        var hub = ActorHubBootstrap.CreateDefault(speciesLayers: _ => OneFixedRow);
        Assert.Contains(hub.Subsystems, s => s.SubsystemId == "rpg.species-layer");
    }

    [Fact]
    public void CreateDefault_withSpeciesLayers_contributesThroughTheRealHub()
    {
        var hub = ActorHubBootstrap.CreateDefault(speciesLayers: _ => OneFixedRow);
        var derived = hub.ResolveDerived(hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }));
        Assert.Equal(50.0, derived.Get("combat.power.omni", 0.0), 6);
    }

    // ── No mode gets its own reader — every registered subsystem reads through this ONE seam ───────

    [Fact]
    public void NoModeGetsItsOwnReader_theSameSubsystemAnswersEveryContext()
    {
        var rows = OneFixedRow;
        var hub = ActorHubBootstrap.CreateDefault(speciesLayers: _ => rows);
        var plant = hub.ResolveDerived(hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }));
        var zombie = hub.ResolveDerived(hub.Stats.Contexts.ForZombie("Z1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }));

        Assert.Equal(50.0, plant.Get("combat.power.omni", 0.0), 6);
        Assert.Equal(50.0, zombie.Get("combat.power.omni", 0.0), 6);
    }

    sealed class SequencePowerIndexProvider : IPowerIndexProvider
    {
        readonly int[] _values;
        readonly Action _onCall;
        int _index;
        public SequencePowerIndexProvider(int[] values, Action onCall) { _values = values; _onCall = onCall; }
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
