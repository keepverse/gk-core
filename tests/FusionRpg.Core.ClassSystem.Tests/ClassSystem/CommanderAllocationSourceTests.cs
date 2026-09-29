using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>aura-skill T5 (W1): `CommanderAllocationSource` is the first production caller of
/// `AllocationStore`/`RpgStore.LoadAllocation` — `CheatState.cs` built `ActorHub` with no
/// `aptitudeAllocation` before this, so every one of the 486 aptitude-share edges resolved to zero on
/// a live lawn.
///
/// <para><b>The channel under test moved 2026-09-18.</b> It was `progression.bonus.atk`, retired by
/// owner ruling (`retire-atk`, `DerivedStatChannels.Retired`), so the loader no longer yields its
/// edge. The same mechanism -- refresh the cached allocation, resolve through the Hub -- is exercised
/// on `progression.bonus.defense`, a surviving `FlatSum` bridge channel, so this test still proves
/// exactly what it was written to prove.</para></summary>
public class CommanderAllocationSourceTests
{
    static AptitudeTuning MightToDefenseTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "progression.bonus": "magnitude" },
          "edges": [ { "channel": "progression.bonus.defense", "source": "Might", "kMilli": 10000 } ]
        }
        """);

    // "ActorHub" bare would resolve ambiguously against the sibling FusionRpg.Core.Tests.ActorHub
    // namespace (AptitudeSubsystemTests.cs's own established workaround for the same clash).
    static FusionRpg.Core.Stats.Derived.ActorHub Hub(CommanderAllocationSource source) =>
        ActorHubBootstrap.CreateDefault(aptitudeTuning: MightToDefenseTuning(), aptitudeAllocation: source.Resolve);

    static ActorDerivedSnapshot Resolve(FusionRpg.Core.Stats.Derived.ActorHub hub) =>
        hub.ResolveDerived(hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 }));

    [Fact]
    public void Before_any_refresh_resolves_to_empty_allocation_zero_channel_impact()
    {
        var source = new CommanderAllocationSource(() =>
            throw new InvalidOperationException("must never be called before Refresh"));
        var derived = Resolve(Hub(source));

        Assert.Equal(0.0, derived.Get(DerivedStatChannels.ProgressionBonusDefense, 0.0));
    }

    [Fact]
    public void After_refresh_a_non_empty_allocation_produces_a_non_zero_bonus_defense()
    {
        var source = new CommanderAllocationSource(() =>
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100));
        source.Refresh();

        var derived = Resolve(Hub(source));

        Assert.True(derived.Get(DerivedStatChannels.ProgressionBonusDefense, 0.0) > 0.0);
    }

    [Fact]
    public void Resolve_never_calls_the_reader_only_Refresh_does()
    {
        var reads = 0;
        var source = new CommanderAllocationSource(() =>
        {
            reads++;
            return AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        });

        var hub = Hub(source);
        for (var i = 0; i < 50; i++) Resolve(hub); // 50 hot-path resolves, matching a busy frame
        Assert.Equal(0, reads); // the reader (an HTTP call in production) must never fire from here

        source.Refresh();
        Assert.Equal(1, reads); // exactly one read for this one poll tick

        for (var i = 0; i < 50; i++) Resolve(hub);
        Assert.Equal(1, reads); // still just the one read -- hot-path resolves stay reads of the cache

        source.Refresh();
        Assert.Equal(2, reads); // a second poll tick is a second, and only a second, read
    }

    [Fact]
    public void Refresh_replaces_the_cached_allocation_so_a_later_empty_read_zeroes_the_channel_again()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var source = new CommanderAllocationSource(() => allocation);
        source.Refresh();
        var hub = Hub(source);
        Assert.True(Resolve(hub).Get(DerivedStatChannels.ProgressionBonusDefense, 0.0) > 0.0);

        allocation = AptitudeAllocation.Empty; // e.g. a respec landed server-side between polls
        source.Refresh();

        Assert.Equal(0.0, Resolve(hub).Get(DerivedStatChannels.ProgressionBonusDefense, 0.0));
    }
}
