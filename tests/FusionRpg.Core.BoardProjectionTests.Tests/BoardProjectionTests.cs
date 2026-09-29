using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

public class BoardProjectionTests
{
    [Fact]
    public void TryUpsert_blank_ptr_returns_false()
    {
        var board = new BoardProjection();
        Assert.False(board.TryUpsert(BoardSide.Plant, null));
        Assert.False(board.TryUpsert(BoardSide.Plant, " "));
        Assert.Equal(0, board.PlantCount);
    }

    [Fact]
    public void TryRemove_missing_returns_false()
    {
        var board = new BoardProjection();
        Assert.False(board.TryRemove(BoardSide.Zombie, "0x1"));
    }

    [Fact]
    public void Clear_empties_all_maps()
    {
        var board = new BoardProjection();
        board.TryUpsert(BoardSide.Plant, "0xP", 1);
        board.TryUpsert(BoardSide.Zombie, "0xZ", 2);
        board.Clear();
        Assert.Equal(0, board.PlantCount);
        Assert.Equal(0, board.ZombieCount);
        Assert.Empty(board.ToEntityArray());
    }

    [Fact]
    public void Ptr_compare_is_case_insensitive()
    {
        var board = new BoardProjection();
        board.TryUpsert(BoardSide.Plant, "0xAb", 1);
        Assert.False(board.TryUpsert(BoardSide.Plant, "0xAB", 1));
        Assert.True(board.TryUpsert(BoardSide.Plant, "0xAB", 5));
        Assert.Equal(1, board.PlantCount);
        Assert.True(board.TryRemove(BoardSide.Plant, "0xab"));
        Assert.Equal(0, board.PlantCount);
    }

    [Fact]
    public void TryUpsert_identical_typeId_returns_false()
    {
        var board = new BoardProjection();
        Assert.True(board.TryUpsert(BoardSide.Plant, "0x1", 3));
        Assert.False(board.TryUpsert(BoardSide.Plant, "0x1", 3));
        Assert.Equal(1, board.PlantCount);
        Assert.Equal(3, board.ToEntityArray()[0].TypeId);
    }

    [Fact]
    public void TryUpsert_typeId_change_returns_true()
    {
        var board = new BoardProjection();
        Assert.True(board.TryUpsert(BoardSide.Zombie, "0xZ", 1));
        Assert.True(board.TryUpsert(BoardSide.Zombie, "0xZ", 9));
        Assert.Equal(9, board.ToEntityArray()[0].TypeId);
    }

    [Fact]
    public void ToEntityArray_copies_entities()
    {
        var board = new BoardProjection();
        board.TryUpsert(BoardSide.Plant, "0xP", 2);
        var a = board.ToEntityArray();
        a[0].TypeId = 99;
        Assert.Equal(2, board.ToEntityArray()[0].TypeId);
    }
}
