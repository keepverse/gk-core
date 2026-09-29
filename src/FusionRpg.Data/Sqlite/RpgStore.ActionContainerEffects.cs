using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>
    /// A24 (spec-container-effect-resolver-production.md §1): every distinct container a real,
    /// authored action references — the same join <see cref="BuildActionCatalog"/> already performs
    /// at `RpgStore.ActionCatalog.cs:58` for scope validation, reused here to scope a resolver build to
    /// exactly the action program's own content, never Item/Trait/Patron/WorldBuff containers.
    ///
    /// <para>Deduplicated by container id (no shipped content shares one container across two actions
    /// yet, but the dedup costs one <see cref="HashSet{T}"/> and removes the question). A raced delete
    /// between <see cref="ListActionIds"/> and <see cref="GetAction"/>/<see cref="GetContainer"/> is
    /// skipped, not fatal — matching <see cref="BuildActionCatalog"/>'s own tolerance.</para>
    /// </summary>
    public IReadOnlyList<ContainerRow> ListActionContainers()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var containers = new List<ContainerRow>();
        foreach (var actionId in ListActionIds())
        {
            var row = GetAction(actionId);
            if (row is null || string.IsNullOrEmpty(row.ContainerId)) continue;
            if (!seen.Add(row.ContainerId)) continue;

            var container = GetContainer(row.ContainerId);
            if (container is not null) containers.Add(container);
        }
        return containers;
    }
}
