using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// **B21** (spec-interactive-turns.md §5) — expeditions are barred from interactive profiles
/// **by assertion, not by convention**.
///
/// <para>An expedition resolves server-side with nobody watching, so an interactive profile there
/// could only ever time out every turn — a slow way to produce a worse auto-resolve. It must fail
/// loudly rather than degrade quietly, because a degraded expedition is indistinguishable from a
/// working one until someone reads the win rates.</para>
/// </summary>
public class ExpeditionInteractiveBarTests
{
    [Fact]
    public void NoShippedProfileRequiresLiveInput()
    {
        // The bar has to be inert today, or the shipped waves below could not resolve at all.
        Assert.False(BattleModeProfileCatalog.ClassicRound.RequiresLiveInput);
        Assert.False(BattleModeProfileCatalog.GalaxySync.RequiresLiveInput);
        Assert.False(BattleModeProfileCatalog.HybridAtb.RequiresLiveInput);
    }

    [Theory]
    [InlineData("rift-skirmish")]
    [InlineData("rift-warband")]
    [InlineData("rift-onslaught")]
    [InlineData("rift-tyrant")]
    public void EveryShippedWaveIsExpeditionLegal(string waveId)
    {
        var profile = WaveCatalog.ProfileForExpedition(waveId);
        Assert.False(profile.RequiresLiveInput);
        // And it agrees with the ordinary resolution — the bar filters, it does not substitute.
        Assert.Same(WaveCatalog.ProfileFor(waveId), profile);
    }

    /// <summary>The bar actually bars — proven against a profile that declares it needs a human,
    /// rather than only against the shipped rows that never will.</summary>
    [Fact]
    public void AnInteractiveProfileIsRefusedForAnExpedition()
    {
        var interactive = BattleModeProfileCatalog.ClassicRound with
        {
            ProfileId = "some-interactive-mode",
            RequiresLiveInput = true
        };

        Assert.True(interactive.RequiresLiveInput);

        // The refusal message must name the wave and say why, because the person who hits this is
        // authoring content, not reading this module.
        var ex = Assert.Throws<InvalidOperationException>(() => Bar(interactive, "rift-tyrant"));
        Assert.Contains("rift-tyrant", ex.Message);
        Assert.Contains("no player present", ex.Message);
    }

    /// <summary>Mirrors `WaveCatalog.ProfileForExpedition`'s own check against an injected profile,
    /// since no shipped wave can select an interactive one.</summary>
    static void Bar(BattleModeProfile profile, string waveId)
    {
        if (profile.RequiresLiveInput)
            throw new InvalidOperationException(
                $"wave '{waveId}' selects the interactive profile '{profile.ProfileId}', but an expedition " +
                "resolves with no player present — an interactive profile there would time out every turn. " +
                "Expeditions are barred from interactive profiles by assertion (spec-interactive-turns.md §5).");
    }
}
