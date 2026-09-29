using FusionRpg.Injector.Hud;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudUniqueFlagsTests : IDisposable
{
    public ActorHudUniqueFlagsTests() => ActorHudUniqueFlags.Clear();

    public void Dispose() => ActorHudUniqueFlags.Clear();

    [Fact]
    public void Mark_and_TryIsUnique_normalizes_ptr()
    {
        ActorHudUniqueFlags.Mark("1a2b");

        Assert.True(ActorHudUniqueFlags.TryIsUnique("1A2B"));
        Assert.False(ActorHudUniqueFlags.TryIsUnique("FFFF"));
    }

    [Fact]
    public void Remove_clears_flag()
    {
        ActorHudUniqueFlags.Mark("ABC");
        ActorHudUniqueFlags.Remove("abc");

        Assert.False(ActorHudUniqueFlags.TryIsUnique("ABC"));
    }

    [Fact]
    public void Clear_empties_all()
    {
        ActorHudUniqueFlags.Mark("A");
        ActorHudUniqueFlags.Mark("B");
        ActorHudUniqueFlags.Clear();

        Assert.False(ActorHudUniqueFlags.TryIsUnique("A"));
        Assert.False(ActorHudUniqueFlags.TryIsUnique("B"));
    }
}
