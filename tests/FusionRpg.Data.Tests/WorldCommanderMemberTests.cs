using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>`commander-roster` EP3.7 — a world holding a `Commander` member round-trips through
/// `RpgStore.World.cs` (spec-legion-commander.md). Persistence writes the role with `ToString()` and
/// reads it with `Enum.Parse`, so the enum member is the whole change — this test proves it, rather than
/// assuming it.</summary>
public class WorldCommanderMemberTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldCommanderMemberTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void A_legion_led_by_a_Commander_member_round_trips_through_the_store()
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 99, worldId: "w-cmd");
        var target = built.Entities.FirstOrDefault(e => e.Members.Count > 0);
        Assert.NotNull(target);

        var promoted = built with
        {
            Entities = built.Entities
                .Select(e => e.EntityId == target!.EntityId
                    ? e with
                    {
                        Members = e.Members
                            .Select((m, i) => i == 0 ? m with { Role = WorldEntityMemberRole.Commander } : m)
                            .ToList(),
                    }
                    : e)
                .ToList(),
        };

        var (ok, reason, _) = _store.CreateWorld(playerId: 1, promoted);
        Assert.True(ok, reason);

        var loaded = _store.LoadWorldState("w-cmd");
        Assert.NotNull(loaded);
        Assert.Equal(WorldCanonical.Write(promoted), WorldCanonical.Write(loaded!));
        Assert.Contains(
            loaded!.Entities.SelectMany(e => e.Members),
            m => m.Role == WorldEntityMemberRole.Commander);
    }
}
