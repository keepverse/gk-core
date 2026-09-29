namespace FusionRpg.Core.Actions;

/// <summary>
/// A18a (spec-action-container-binding.md §1). Resolves a compiled action's <c>ContainerId</c> to the
/// <c>EffectDefDto</c> ids <c>AtomCompiler</c> produced from that container's atoms — the seam A20
/// (synthetic-loadout-harness) is the production supplier for; tests construct one directly, same as
/// <see cref="ActionCatalog"/> today.
/// </summary>
public interface IContainerEffectResolver
{
    /// <summary>Empty span for a non-existent or pooled container — loud rejection at bind time
    /// (battle's own loadout-compile loop), never a silent skip.</summary>
    IReadOnlyList<string> EffectIdsFor(string containerId);
}

/// <summary>The minimal, in-memory default — the same weight class as <see cref="ActionCatalog.Build"/>,
/// not a new content-authoring pipeline.</summary>
public sealed class DictionaryContainerEffectResolver : IContainerEffectResolver
{
    readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _map;

    public DictionaryContainerEffectResolver(IReadOnlyDictionary<string, IReadOnlyList<string>> map) =>
        _map = map ?? throw new ArgumentNullException(nameof(map));

    public IReadOnlyList<string> EffectIdsFor(string containerId) =>
        _map.TryGetValue(containerId, out var ids) ? ids : Array.Empty<string>();
}

/// <summary>
/// combat-ai `siege-loadout-wiring` (CAI3.3, spec-siege-loadout-wiring.md §2): several resolvers asked
/// in order, first non-empty answer wins. A DECORATOR over the one-method
/// <see cref="IContainerEffectResolver"/> seam — the same "wrap, never fork" shape
/// <see cref="FoggedBattleView"/> already uses for <c>IBattleView</c> — not a second resolution
/// mechanism.
///
/// <para><b>Why a composite rather than a merged dictionary.</b> A district assault has two container
/// sources that cannot see each other: the store bundle's authored containers, and
/// <c>ConstructionActions.ContainerResolver</c>'s four fixed siege-construction ids
/// (<c>ConstructionActions.cs:70-77</c>), which the store bundle does not know. Siege composes them as
/// <c>[store bundle, construction]</c> so the construction containers keep resolving exactly as today
/// while real authored containers resolve for the first time.</para>
///
/// <para><b>Ordinal, allocation-free per call, deterministic.</b> No LINQ and no closure: a plain
/// indexed loop over the constructor's own array, so the order is the CONSTRUCTOR's order and two
/// composites built the same way answer identically. The loop stops at the first resolver that returns
/// anything, so a later resolver is never even asked.</para>
///
/// <para><b>An empty answer is returned as empty, never null.</b> <c>BindContainers</c> turns empty into
/// its own loud rejection ("a container that does not resolve must fail loudly, never silently skip");
/// returning null here would move that failure to a null reference somewhere downstream.</para>
/// </summary>
public sealed class CompositeContainerEffectResolver : IContainerEffectResolver
{
    readonly IContainerEffectResolver[] _inner;

    /// <summary>Order IS the answer order: the first resolver to return a non-empty list wins. An empty
    /// array is legal and answers empty for everything — the honest "nothing can resolve here" state
    /// rather than a null.</summary>
    public CompositeContainerEffectResolver(params IContainerEffectResolver[] inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IReadOnlyList<string> EffectIdsFor(string containerId)
    {
        for (var i = 0; i < _inner.Length; i++)
        {
            var ids = _inner[i].EffectIdsFor(containerId);
            if (ids.Count > 0) return ids;
        }

        return Array.Empty<string>();
    }
}
