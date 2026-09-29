using FusionRpg.Core.Actions;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T40 (action-todo.md Phase 12, spec-action-container-binding.md §1): the standalone contract for
/// <see cref="DictionaryContainerEffectResolver"/>, proven before anything in <c>BattleRunState</c>
/// consumes it (T41).
/// </summary>
public class ContainerEffectResolverTests
{
    [Fact]
    public void A_known_container_id_returns_its_mapped_effect_ids()
    {
        var resolver = new DictionaryContainerEffectResolver(
            new Dictionary<string, IReadOnlyList<string>> { ["item.fireball"] = new[] { "fx.fireball_core", "fx.fireball_burn" } });

        var ids = resolver.EffectIdsFor("item.fireball");

        Assert.Equal(new[] { "fx.fireball_core", "fx.fireball_burn" }, ids);
    }

    [Fact]
    public void An_unknown_container_id_returns_an_empty_span_not_null()
    {
        var resolver = new DictionaryContainerEffectResolver(
            new Dictionary<string, IReadOnlyList<string>> { ["item.fireball"] = new[] { "fx.fireball_core" } });

        var ids = resolver.EffectIdsFor("item.unknown");

        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    [Fact]
    public void A_null_map_is_rejected_loudly_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new DictionaryContainerEffectResolver(null!));
    }
}
