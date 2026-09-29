using System;
using System.Linq;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// power-index wave 1 (spec-power-index.md §5). Composition weighting, rejections, the stub/hydrated
/// providers. The report's own shape/equivalence properties are PowerAxisReportTests.cs.
/// </summary>
public class PowerIndexTests
{
    static PowerTuning Tuning(long wf = 25000, long? wm = 5000) =>
        PowerTuning.Build(1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
            wdMilli: 1000, waMilli: 25000, wrMilli: 250, wzMilli: 1000, wmMilli: wm, wwMilli: 5000, wfMilli: wf);

    static StatContext Ctx(long playerId = 1, int typeId = 0) =>
        new() { PlayerId = playerId, Side = StatSide.Plant, TypeId = typeId };

    // ---- stub -----------------------------------------------------------------------------------

    [Fact]
    public void Stub_ReturnsZeroForBothIndices()
    {
        var stub = new StubPowerIndexProvider();
        Assert.Equal(0, stub.ActorIndex(Ctx()));
        Assert.Equal(0, stub.ContentIndex(new ContentContext(0, 0, 0, 0)));
        Assert.Equal(0, stub.Explain(Ctx()).Total);
        Assert.Empty(stub.Explain(Ctx()).Axes);
    }

    // ---- weighted composition ---------------------------------------------------------------------

    [Fact]
    public void SingleAxis_DaveLevelOnly()
    {
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(DaveLevel: 10, RealmsAdvanced: 0, PvzRuns: 0));
        Assert.Equal(10, provider.ActorIndex(Ctx()));
    }

    [Fact]
    public void WeightedSum_ExactAcrossThreeAxes()
    {
        // dave 10, realms 3, runs 40 at 1000/25000/250 -> 10 + 75 + 10 == 95, exactly (spec §5).
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(DaveLevel: 10, RealmsAdvanced: 3, PvzRuns: 40));
        Assert.Equal(95, provider.ActorIndex(Ctx()));
    }

    [Fact]
    public void RoundingHappensOnceAtTheSum_NotPerAxis()
    {
        // Wr=250 with 3 runs -> 750 milli -> rounds to 1, not 0, when it is the only nonzero axis.
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(DaveLevel: 0, RealmsAdvanced: 0, PvzRuns: 3));
        Assert.Equal(1, provider.ActorIndex(Ctx()));
    }

    [Fact]
    public void PvzRuns_Uncapped_NoSaturationAtTenThousand()
    {
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(DaveLevel: 0, RealmsAdvanced: 0, PvzRuns: 10_000));
        Assert.Equal(2500, provider.ActorIndex(Ctx()));

        // Uncapped means it keeps climbing linearly past any threshold a cap would have imposed.
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(DaveLevel: 0, RealmsAdvanced: 0, PvzRuns: 1_000_000));
        Assert.Equal(250_000, provider.ActorIndex(Ctx()));
    }

    [Fact]
    public void NegativeLadderInput_ClampedToZero_NotThrown()
    {
        // A missing progression row is absence, not corruption.
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(DaveLevel: -5, RealmsAdvanced: -1, PvzRuns: -100));
        Assert.Equal(0, provider.ActorIndex(Ctx()));
    }

    [Fact]
    public void ContentIndex_WeightedSum_IncludesRealmsAdvanced()
    {
        var provider = new HydratedPowerIndexProvider(Tuning());
        // zomboss 4 (*1000=4000), dangerBand 2 (*5000=10000), worldTier 1 (*5000=5000), realms 1 (*25000=25000)
        var idx = provider.ContentIndex(new ContentContext(DangerBand: 2, WorldTier: 1, ZombossLevel: 4, RealmsAdvanced: 1));
        Assert.Equal(44, idx); // (4000+10000+5000+25000)/1000
    }

    // ---- PS-6 tripwire -----------------------------------------------------------------------------

    [Fact]
    public void PS6Tripwire_RunShareStaysBelowRealmShare_AtShippedWeights()
    {
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(DaveLevel: 0, RealmsAdvanced: 200, PvzRuns: 10_000));
        var report = provider.Explain(Ctx());

        var realmShare = report.Axes.Single(a => a.AxisId == "realmsAdvanced").SharePermille;
        var runShare = report.Axes.Single(a => a.AxisId == "pvzRuns").SharePermille;
        Assert.True(runShare < realmShare, $"runShare={runShare} must stay below realmShare={realmShare} (PS-6)");
    }

    // ---- F2/F8 divergence tripwire -------------------------------------------------------------------

    [Fact]
    public void F2F8Tripwire_ActorMinusContent_ExactlyConstant_Across500SimulatedWorlds()
    {
        var tuning = Tuning(wf: 25000); // Wf = Wa, the invariant
        var provider = new HydratedPowerIndexProvider(tuning);
        const int daveLevel = 30, pvzRuns = 500, zombossLevel = 20, dangerBand = 3, worldTier = 2;

        int? expectedGap = null;
        for (int world = 0; world <= 500; world++)
        {
            provider.Hydrate(Ctx(), new ActorLadderSnapshot(daveLevel, world, pvzRuns));
            var actor = provider.ActorIndex(Ctx());
            var content = provider.ContentIndex(new ContentContext(dangerBand, worldTier, zombossLevel, world));
            var gap = actor - content;

            expectedGap ??= gap;
            Assert.Equal(expectedGap.Value, gap);
        }
    }

    [Fact]
    public void DivergesWhenWfNotEqualWa_DemonstratingWhyTheInvariantIsRequired()
    {
        // The counter-proof: without Wf=Wa (bypassing ValidateWeights via reflection-free direct
        // composer calls against two independently built tunings), the gap is NOT constant. This is
        // what F2/F8 actually found and why PowerWeightInvalid exists — not exercised through the
        // public provider (which refuses to construct), but through the pure composer functions
        // directly, matching how the original audit finding would have been observed.
        var actorTuning = Tuning(wf: 25000);
        // A "wrong" content-side tuning where Wf=20000 (diverges from Wa=25000) — inspecting the pure
        // math only; never constructed through HydratedPowerIndexProvider, which would reject it.
        var wrongWeights = new PowerWeightsTuning(1000, 25000, 250, 1000, 5000, 5000, 20000);

        long GapAt(int world)
        {
            var actor = PowerIndexComposer.ActorExplain(actorTuning, new ActorLadderSnapshot(30, world, 500)).Total;
            var contentMilli = wrongWeights.WzMilli * 20 + wrongWeights.WmMilli!.Value * 3 + wrongWeights.WwMilli * 2 + wrongWeights.WfMilli * world;
            return actor - contentMilli / 1000;
        }

        Assert.NotEqual(GapAt(0), GapAt(500));
    }

    // ---- rejections --------------------------------------------------------------------------------

    [Fact]
    public void WfNotEqualWa_RejectedAtConstruction()
    {
        var ex = Assert.Throws<PowerWeightInvalid>(() => new HydratedPowerIndexProvider(Tuning(wf: 20000)));
        Assert.Equal(25000, ex.WaMilli);
        Assert.Equal(20000, ex.WfMilli);
    }

    [Fact]
    public void WmNull_ContentIndexThrows_ActorIndexStillWorks()
    {
        var provider = new HydratedPowerIndexProvider(Tuning(wm: null));
        provider.Hydrate(Ctx(), new ActorLadderSnapshot(10, 0, 0));

        Assert.Equal(10, provider.ActorIndex(Ctx())); // unaffected

        var ex = Assert.Throws<PowerWeightMissing>(() => provider.ContentIndex(new ContentContext(1, 1, 1, 0)));
        Assert.Equal("Wm", ex.Weight);
    }

    [Fact]
    public void NullTuning_Rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new HydratedPowerIndexProvider(null!));
    }

    // ---- un-hydrated identity ------------------------------------------------------------------------

    [Fact]
    public void UnhydratedContext_ReturnsZero_MatchingOldProviderBehaviour()
    {
        // InjectorProgressionPowerProvider.GetLevel returns 0 for an unknown key (SSOT §6.4) — the
        // migration must not change this observable behaviour.
        var provider = new HydratedPowerIndexProvider(Tuning());
        Assert.Equal(0, provider.ActorIndex(Ctx(playerId: 999)));
    }

    // ---- hydration scope ------------------------------------------------------------------------------

    [Fact]
    public void PlayerHydration_ReachesEveryActorContextOfThatPlayer_OnBothSides()
    {
        // The injector hydrates one snapshot per player (no side, no type id) and every lawn actor reads it with its
        // own side and type id. Keying on those gave Θ only to plants whose type id is 0 (lawn-tuning-profile, 2026-09-16).
        var provider = new HydratedPowerIndexProvider(Tuning());
        provider.Hydrate(new StatContext { PlayerId = 6 }, new ActorLadderSnapshot(DaveLevel: 6, RealmsAdvanced: 0, PvzRuns: 0));

        Assert.Equal(6, provider.ActorIndex(Ctx(playerId: 6, typeId: 0)));
        Assert.Equal(6, provider.ActorIndex(Ctx(playerId: 6, typeId: 1032)));
        Assert.Equal(6, provider.ActorIndex(new StatContext { PlayerId = 6, Side = StatSide.Zombie, TypeId = 23 }));
        Assert.Equal(0, provider.ActorIndex(Ctx(playerId: 7, typeId: 1032)));
    }
}
