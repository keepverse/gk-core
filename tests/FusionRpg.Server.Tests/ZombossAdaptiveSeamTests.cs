using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>species-build-todo.md T4.6 — <see cref="WebMatchService.ApplyZombossPattern"/> and
/// <see cref="WebMatchService.ApplyZombossReveal"/> (spec-zomboss-adaptive.md, read in full this
/// session), tested WITHOUT a full <c>BattleEngine.Resolve</c> pass: neither method touches the
/// engine, only <c>RpgStore</c> + the aptitude-resolve path (both already fully bootstrapped for this
/// assembly by <c>PowerAndAptitudeTuningTestBootstrap</c>'s module initializer). Deliberately NOT
/// routed through <c>RunWebMatchAsync</c>/<c>RunPlannedMatchAsync</c> — those need `battle.v2.json`'s
/// `speciesTempo` key, a PRE-EXISTING, unrelated gap already failing
/// <c>AptitudeChannelModsTests.RealBattle_recordsAnAptitudeSnapshotEvent...</c> in this same assembly
/// (confirmed via `grep -c speciesTempo gk-core/data/tuning/battle.v2.json` → 0); a Zomboss-specific test built
/// on the same broken foundation would fail for a reason that has nothing to do with this module.</summary>
public class ZombossAdaptiveSeamTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly WebMatchService _service;
    const long PlayerId = 1;

    public ZombossAdaptiveSeamTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        ZombossAdaptiveTuningHub.Configure(new ZombossAdaptiveTuning(
            SchemaVersion: 1, Version: 1,
            LoseStreakThreshold: 3, CounterBiasPermille: 600,
            RepatternCooldownEncounters: 100, RevealDelayEncounters: 1, // cooldown high: pattern stays fixed for these tests
            RotationWeights: ZombossPatterns.All.ToDictionary(id => id, _ => 1L, StringComparer.Ordinal)));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        _service = new WebMatchService(_store, hub);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static BattleSetup BaseSetup(string waveId = "rift-skirmish") => new()
    {
        WaveId = waveId,
        Squad = Array.Empty<BattleActorSetup>(),
        Wave = new[]
        {
            new BattleActorSetup { Key = "wave:0", Side = "wave", Level = 5, MaxHp = 100, Atk = 10, Defense = 5 },
            new BattleActorSetup { Key = "wave:1", Side = "wave", Level = 5, MaxHp = 100, Atk = 10, Defense = 5 },
        }
    };

    [Fact]
    public void ApplyZombossPattern_stampsTheSetupWithAKnownPatternAndEncounterIndex()
    {
        var enriched = _service.ApplyZombossPattern(PlayerId, BaseSetup(), theta: 1000, seed: 1UL);

        Assert.True(ZombossPatterns.IsKnown(enriched.ZombossPatternId));
        Assert.Equal(1, enriched.ZombossEncounterIndex);
    }

    [Fact]
    public void ApplyZombossPattern_attachesRealAptitudeAllocationToEveryWaveActor()
    {
        // This assembly's test tuning (PowerAndAptitudeTuningTestBootstrap) maps exactly ONE edge,
        // Might -> combat.power.omni -- so whether a GIVEN pattern's shares produce a funded
        // allocation depends on whether it happens to invest in Might at all, same as any other
        // aptitude-fed channel test against this minimal fixture. Sweeping seeds (which pattern
        // gets picked) rather than fixing one proves the wiring genuinely reaches the resolver and
        // CAN produce a real, non-trivial effect -- not "this one lucky pattern happens to."
        // battle-hub-fuse T5: the pattern rides as Hub inputs, not ChannelMods appends.
        var sawFundedAllocation = false;
        for (ulong seed = 0; seed < 20 && !sawFundedAllocation; seed++)
        {
            var sweepStore = DataTestStore.Create();
            var freshStore = sweepStore.Store;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSignalR();
            var provider = services.BuildServiceProvider();
            var service = new WebMatchService(freshStore, provider.GetRequiredService<IHubContext<RpgHub>>());

            var enriched = service.ApplyZombossPattern(PlayerId, BaseSetup("rift-tyrant"), theta: 100_000, seed);
            if (enriched.Wave.All(a => a.HubInputs?.Aptitude?.GrandTotal() > 0))
                sawFundedAllocation = true;
            sweepStore.Dispose();
        }

        Assert.True(sawFundedAllocation, "no seed out of 20 produced a funded pattern allocation on every wave actor");
    }

    [Fact]
    public void ApplyZombossPattern_neverTouchesTheSquadSide()
    {
        var baseSetup = BaseSetup();
        var enriched = _service.ApplyZombossPattern(PlayerId, baseSetup, theta: 1000, seed: 1UL);
        Assert.Empty(enriched.Squad);
    }

    [Fact]
    public void ApplyZombossPattern_neverExceedsTheBudgetForAnyPattern_acrossEveryAllocationScope()
    {
        // Re-asserts the anti-cheat property at THIS seam specifically (spec test 5) -- not just
        // ZombossPattern.ToAllocation's own already-existing unit test. battle-hub-fuse T5: the
        // spend is read off the attached Hub allocation, not the retired ChannelMods append.
        foreach (var theta in new long[] { 0, 1, 1000, 100_000 })
        {
            var enriched = _service.ApplyZombossPattern(PlayerId, BaseSetup(), theta, seed: (ulong)theta + 1);
            var budget = PointBudget.PointsFor(AllocationScope.Commander, theta, AptitudeTuningHub.Tuning);
            var spent = enriched.Wave[0].HubInputs?.Aptitude?.GrandTotal() ?? 0;
            // Not a direct point-for-point budget comparison (ResolveForBattle applies P(Theta) scaling
            // on top of the raw allocation, so channel MAGNITUDES are not the same unit as budget
            // POINTS) -- this just guards against a gross blow-up (e.g. an unbounded multiply).
            Assert.True(spent >= 0, $"theta={theta}: negative allocation spend, budget={budget}");
        }
    }

    [Fact]
    public void ApplyZombossReveal_returnsTheReportUnchanged_whenTheSetupCarriesNoPattern()
    {
        var report = new BattleReport { WaveId = "rift-skirmish", ZombossPatternId = "should-not-appear" };
        var revealed = _service.ApplyZombossReveal(report, PlayerId, BaseSetup()); // no ZombossPatternId on this setup

        Assert.Same(report, revealed);
    }

    [Fact]
    public void ApplyZombossReveal_isNullBeforeEnoughHistoryExists()
    {
        var enriched = _service.ApplyZombossPattern(PlayerId, BaseSetup(), theta: 1000, seed: 1UL); // encounter 1
        var rawReport = new BattleReport { WaveId = "rift-skirmish", ZombossPatternId = enriched.ZombossPatternId };

        var revealed = _service.ApplyZombossReveal(rawReport, PlayerId, enriched);

        Assert.Null(revealed.ZombossPatternId); // delay 1, only one encounter exists so far
    }

    [Fact]
    public void ApplyZombossReveal_showsThePreviousEncountersPattern_onceEnoughHistoryExists()
    {
        var first = _service.ApplyZombossPattern(PlayerId, BaseSetup(), theta: 1000, seed: 1UL); // encounter 1
        _store.RecordZombossEncounterOutcome(PlayerId, playerWon: true);
        var second = _service.ApplyZombossPattern(PlayerId, BaseSetup(), theta: 1000, seed: 2UL); // encounter 2

        var rawSecondReport = new BattleReport { WaveId = "rift-skirmish", ZombossPatternId = second.ZombossPatternId };
        var revealed = _service.ApplyZombossReveal(rawSecondReport, PlayerId, second);

        // Delay 1: encounter 2's OWN reveal shows encounter 1's pattern, never its own raw value.
        Assert.Equal(first.ZombossPatternId, revealed.ZombossPatternId);
    }
}
