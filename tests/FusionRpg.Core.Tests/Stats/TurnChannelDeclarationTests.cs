using System.Linq;
using System.Reflection;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// W15 (battle-derived-wire T17): a channel DECLARED in <see cref="DerivedTurnChannels"/> must be
/// REGISTERED. `turn.moveSpeed` was the counter-example — declared, never registered, read by
/// nothing — and a declared-unregistered channel is exactly the shape that makes the next channel
/// census wrong. It was deleted 2026-09-23; this test is the guard that would have caught it, and it
/// stays useful for whatever movement channel is added next.
/// </summary>
public class TurnChannelDeclarationTests
{
    static IReadOnlyList<string> DeclaredTurnChannelIds() =>
        typeof(DerivedTurnChannels)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Every_declared_turn_channel_is_registered()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var declared = DeclaredTurnChannelIds();

        Assert.NotEmpty(declared);
        Assert.Empty(declared.Where(id => !registry.IsKnown(id)));
    }

    [Fact]
    public void Turn_moveSpeed_is_gone_not_merely_unregistered()
    {
        Assert.DoesNotContain("turn.moveSpeed", DeclaredTurnChannelIds());
    }
}
