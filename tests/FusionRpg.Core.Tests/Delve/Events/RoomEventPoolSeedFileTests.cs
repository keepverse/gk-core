using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

public class RoomEventPoolSeedFileTests
{
    [Fact]
    public void LoadAll_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RoomEventPoolSeedFile.LoadAll(null!));
    }

    [Fact]
    public void LoadAll_a_missing_directory_returns_empty_never_throws()
    {
        Assert.Empty(RoomEventPoolSeedFile.LoadAll(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist")));
    }

    [Fact]
    public void LoadAll_reads_the_real_shipped_rooms()
    {
        var pools = RoomEventPoolSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        // The room count is a growing content population, never pinned (population-pin SE3.3,
        // 2026-09-19).
        Assert.NotEmpty(pools);
    }

    [Fact]
    public void A_real_rest_room_resolves_to_its_own_real_non_empty_eventPool()
    {
        // D4.17 row 7's own cell-headroom fix (party-dungeon-todo.md, 2026-09-08) widened every
        // real `rest` room's pool from its original single entry to the full 10-entry
        // `encounter-event`-kind corpus (8 distinct themes) -- content-completeness, not a code
        // change; `event.encounter-event-creature.dolldiamond-001` (this test's own original pin) is
        // still a member, just no longer the sole one.
        var pools = RoomEventPoolSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        Assert.Contains("event.encounter-event-creature.dolldiamond-001", pools["room.rest-none-002"]);
        Assert.True(pools["room.rest-none-002"].Count > 3, "rest pool must clear events.noRepeatRooms (3)");
    }

    [Fact]
    public void A_real_fight_room_with_no_authored_pool_resolves_to_an_empty_list_never_null()
    {
        var pools = RoomEventPoolSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        Assert.NotNull(pools["room.fight-fire-001"]);
        Assert.Empty(pools["room.fight-fire-001"]);
    }
}
