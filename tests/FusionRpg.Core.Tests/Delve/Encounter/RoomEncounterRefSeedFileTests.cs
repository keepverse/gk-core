using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

public class RoomEncounterRefSeedFileTests
{
    [Fact]
    public void LoadAll_null_directory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RoomEncounterRefSeedFile.LoadAll(null!));
    }

    [Fact]
    public void LoadAll_a_missing_directory_returns_empty_never_throws()
    {
        Assert.Empty(RoomEncounterRefSeedFile.LoadAll(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist")));
    }

    [Fact]
    public void LoadAll_reads_the_real_shipped_rooms()
    {
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        // The room count is a growing content population, never pinned (population-pin SE3.3,
        // 2026-09-19).
        Assert.NotEmpty(refs);
    }

    [Fact]
    public void A_real_fight_room_resolves_to_its_real_encounterRef()
    {
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        Assert.Equal("encounter.pack-rainbow-001", refs["room.fight-fire-001"]);
    }

    [Fact]
    public void A_real_none_sentinel_resolves_to_null_not_the_literal_string()
    {
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        Assert.Null(refs["room.rest-none-002"]);
    }
}
