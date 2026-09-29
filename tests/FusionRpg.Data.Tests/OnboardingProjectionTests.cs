using FusionRpg.Contracts;
using FusionRpg.Core.Progression;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("VerificationId", "data.onboarding")]
public sealed class OnboardingProjectionTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public OnboardingProjectionTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Projection_exposes_authoritative_rows_and_player_level()
    {
        var player = _store.GetCurrentPlayer()!;
        _store.TryEarnOnboardingCheckpoint(player.Id, "first-win-dave", 4, "fact:8", "{\"commanderId\":\"commander:dave\"}");

        var rows = _store.ListOnboardingCheckpoints(player.Id)!;

        var row = Assert.Single(rows);
        Assert.Equal("first-win-dave", row.CheckpointId);
        Assert.Equal("earned", row.State);
        Assert.Equal("fact:8", row.RewardRef);
        Assert.Equal(1, row.Revision);
    }

    [Fact]
    public void Unknown_checkpoint_claim_is_named_and_does_not_write()
    {
        var player = _store.GetCurrentPlayer()!;

        var result = _store.ClaimOnboardingCheckpoint(player.Id, "not-a-checkpoint");

        Assert.False(result.Ok);
        Assert.Equal("onboarding.checkpoint-unknown", result.Reason);
        Assert.Empty(_store.ListOnboardingCheckpoints(player.Id)!);
    }
}
