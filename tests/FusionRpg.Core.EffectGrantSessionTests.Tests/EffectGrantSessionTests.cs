using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectGrantSessionTests
{
    [Fact]
    public void Upsert_overwrite_remove_clear_snapshot()
    {
        var s = new EffectGrantSession();
        s.Upsert(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        s.Upsert(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "fx.freeze_on_hit",
            OwnerKey = EffectOwnerKeys.Match
        });
        s.Upsert(new EffectGrantDto
        {
            GrantId = "g2",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.PlantType(0)
        });

        var snap = s.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal("fx.freeze_on_hit", snap.Single(x => x.GrantId == "g1").EffectId);

        Assert.True(s.Remove("g2"));
        Assert.Equal(1, s.Count);
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.Empty(s.Snapshot());
    }

    [Fact]
    public void Upsert_requires_grantId_and_effectId()
    {
        var s = new EffectGrantSession();
        Assert.Throws<ArgumentException>(() => s.Upsert(new EffectGrantDto { GrantId = "", EffectId = "fx.x" }));
        Assert.Throws<ArgumentException>(() => s.Upsert(new EffectGrantDto { GrantId = "g", EffectId = "" }));
    }

    [Fact]
    public void TryBuildApplyCommand_null_when_empty_else_named()
    {
        Assert.Null(EffectGrantRehydrate.TryBuildApplyCommand(Array.Empty<EffectGrantDto>()));
        Assert.Null(EffectGrantRehydrate.TryBuildApplyCommand(null!));

        var cmd = EffectGrantRehydrate.TryBuildApplyCommand(new[]
        {
            new EffectGrantDto { GrantId = "a", EffectId = "fx.butter_on_hit", OwnerKey = EffectOwnerKeys.Match }
        }, cmdId: "hello-1");

        Assert.NotNull(cmd);
        Assert.Equal(EffectGrantRehydrate.ApplyCommandName, cmd!.Name);
        Assert.Equal("hello-1", cmd.Id);
        Assert.NotNull(cmd.Payload);
    }

    [Fact]
    public void Hello_rehydrate_path_preserves_grantIds()
    {
        var s = new EffectGrantSession();
        s.Upsert(new EffectGrantDto { GrantId = "butter-1", EffectId = "fx.butter_on_hit", OwnerKey = EffectOwnerKeys.Match });
        s.Upsert(new EffectGrantDto { GrantId = "freeze-1", EffectId = "fx.freeze_on_hit", OwnerKey = EffectOwnerKeys.Match });

        var cmd = EffectGrantRehydrate.TryBuildApplyCommand(s.Snapshot());
        Assert.NotNull(cmd);
        Assert.Equal("effects.grants.apply", cmd!.Name);

        // Sim bag apply (same upsert semantics as injector EffectRuntime.Grant)
        var host = new SimEffectHost();
        foreach (var g in s.Snapshot())
            host.Grant(g);
        var bag = host.Snapshot();
        Assert.Equal(2, bag.Grants.Count);
        Assert.Contains(bag.Grants, x => x.GrantId == "butter-1");
        Assert.Contains(bag.Grants, x => x.GrantId == "freeze-1");
    }

    [Fact]
    public void Grant_same_grantId_is_idempotent_single_row()
    {
        var host = new SimEffectHost();
        var dto = new EffectGrantDto
        {
            GrantId = "same",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        };
        host.Grant(dto);
        host.Grant(dto);
        var bag = host.Snapshot();
        Assert.Single(bag.Grants);
        Assert.Equal("same", bag.Grants[0].GrantId);
    }
}
