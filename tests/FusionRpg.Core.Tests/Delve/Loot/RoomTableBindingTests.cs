using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>D3.14 (spec-dungeon-loot.md §5, "Room-kind table map") — the kind-to-table lookup half of
/// `RoomTableBinding.For`.</summary>
public class RoomTableBindingTests
{
    static readonly IReadOnlyDictionary<string, string> Binding = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fight"] = "drop.dungeon.fire.fight",
        ["elite"] = "drop.dungeon.fire.elite",
        ["cache"] = "drop.dungeon.fire.cache",
        ["boss"] = "drop.dungeon.fire.boss",
    };

    [Fact]
    public void For_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => RoomTableBinding.For(null!, Binding, out _));
        Assert.Throws<ArgumentNullException>(() => RoomTableBinding.For("fight", null!, out _));
    }

    [Theory]
    [InlineData("curio")]
    [InlineData("shrine")]
    [InlineData("trap")]
    [InlineData("wild")]
    [InlineData("rest")]
    [InlineData("merchant")]
    [InlineData("unknown")]
    public void Every_no_table_kind_resolves_to_no_binding_at_all_never_a_refusal(string kind)
    {
        var r = RoomTableBinding.For(kind, Binding, out var result);
        Assert.True(r.IsOk);
        Assert.Null(result);
    }

    [Fact]
    public void A_no_table_kind_ignores_the_lootBinding_entirely_even_if_one_exists_for_it()
    {
        // Proves the no-table check runs BEFORE the dictionary lookup -- an accidental lootBinding
        // entry for a no-table kind must never leak a table.
        var binding = new Dictionary<string, string>(StringComparer.Ordinal) { ["curio"] = "drop.should-never-be-read" };
        var r = RoomTableBinding.For("curio", binding, out var result);
        Assert.True(r.IsOk);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("fight")]
    [InlineData("elite")]
    [InlineData("cache")]
    public void A_table_having_kind_with_a_real_binding_resolves_to_dungeon_room(string kind)
    {
        var r = RoomTableBinding.For(kind, Binding, out var result);
        Assert.True(r.IsOk);
        Assert.NotNull(result);
        Assert.Equal(DungeonSourceKinds.DungeonRoom, result!.SourceKind);
        Assert.Equal(Binding[kind], result.TableId);
    }

    [Fact]
    public void Boss_resolves_through_the_same_lookup_as_every_other_table_having_kind()
    {
        // No special-casing: boss's own fight-side table reads lootBinding["boss"] exactly like fight/
        // elite/cache do. The SEPARATE dungeon-clear relic binding is D3.15's own job, not this one's.
        var r = RoomTableBinding.For("boss", Binding, out var result);
        Assert.True(r.IsOk);
        Assert.Equal(DungeonSourceKinds.DungeonRoom, result!.SourceKind);
        Assert.Equal("drop.dungeon.fire.boss", result.TableId);
    }

    [Fact]
    public void A_table_having_kind_with_no_binding_entry_refuses_unknown_loot_source()
    {
        var emptyBinding = new Dictionary<string, string>(StringComparer.Ordinal);
        var r = RoomTableBinding.For("fight", emptyBinding, out var result);
        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, r.Reason);
        Assert.Contains("drop.unknown-loot-source", r.Detail);
        Assert.Contains("fight", r.Detail);
        Assert.Null(result);
    }
}
