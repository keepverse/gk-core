using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Match;
using FusionRpg.Core.Scope;

namespace FusionRpg.Core.Battle;

/// <summary>
/// T7 (buff-debuff-scope-todo.md Phase 3) — the shared front end. Resolves a <see cref="WhoSelector"/>
/// against a real board on either host: `BoardEntitySnap` is genuinely shared (confirmed this session —
/// `Combat/BoardSnapshot.cs`'s own shape matches the injector's live capture fields exactly, and
/// `Actions/ActionTargetResolver.cs` already consumes it from the SIM side), so this class takes
/// whichever host's board it's handed rather than fetching one itself. `Relation` (own/enemy side) is
/// explicitly NOT resolved here — that's T8's event-driven mechanism, never a one-shot board scan
/// (buff-debuff-scope-ideal.md §4.1/§4.4).
/// </summary>
public static class BattlefieldScopeExecutor
{
    /// <summary>
    /// Live entity pointers (normalized, `MatchUniqueBindingsFacet.NormalizePtr` shape) a
    /// target/type/uniqueCreature selector currently reaches. Empty, never throwing, when nothing
    /// currently qualifies — matching this program's own "false/empty, not throwing" posture for a
    /// scope with no board or no current match.
    /// </summary>
    public static IReadOnlyList<string> ResolvePtrs(
        WhoSelector who, IReadOnlyList<BoardEntitySnap> board, MatchUniqueBindingsFacet? uniqueBindings = null)
    {
        switch (who.Kind)
        {
            case WhoKind.Target:
                return string.IsNullOrWhiteSpace(who.TargetPtr)
                    ? Array.Empty<string>()
                    : new[] { MatchUniqueBindingsFacet.NormalizePtr(who.TargetPtr) };

            case WhoKind.Type:
                return ResolveByType(who.TypeIds, board);

            case WhoKind.UniqueCreature:
                return ResolveUniqueCreature(who.InstanceId, uniqueBindings);

            case WhoKind.Relation:
                throw new InvalidOperationException(
                    "WhoKind.Relation resolves via membership-events (T8), never a one-shot board scan.");

            default:
                return Array.Empty<string>();
        }
    }

    static IReadOnlyList<string> ResolveByType(IReadOnlyList<int>? typeIds, IReadOnlyList<BoardEntitySnap> board)
    {
        if (typeIds is null || typeIds.Count == 0 || board.Count == 0) return Array.Empty<string>();

        var typeSet = new HashSet<int>(typeIds);
        var matches = new List<string>();
        foreach (var entity in board)
        {
            if (typeSet.Contains(entity.TypeId))
                matches.Add(entity.Ptr);
        }
        return matches;
    }

    static IReadOnlyList<string> ResolveUniqueCreature(string? instanceId, MatchUniqueBindingsFacet? uniqueBindings)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || uniqueBindings is null) return Array.Empty<string>();
        if (!uniqueBindings.TryGet(instanceId, out var binding) || binding?.Ptr is null)
            return Array.Empty<string>();
        return new[] { binding.Ptr };
    }

    /// <summary>
    /// Builds one <see cref="EffectGrantDto"/> per resolved ptr, `owner_kind = entity` per
    /// `effect-atom/definitions.md` §6 — grant issuance itself (`EffectBag.Grant`) is the caller's,
    /// since that call is host-specific (SIM's `BattleEffectHost.Bag` vs. live's shared match `Bag`)
    /// while this shape is not.
    /// </summary>
    public static IReadOnlyList<EffectGrantDto> BuildGrants(
        IReadOnlyList<string> ptrs, string effectId, string pluginId, string grantIdPrefix)
    {
        var grants = new List<EffectGrantDto>(ptrs.Count);
        foreach (var ptr in ptrs)
        {
            grants.Add(new EffectGrantDto
            {
                GrantId = $"{grantIdPrefix}:{ptr}",
                EffectId = effectId,
                OwnerKind = "entity",
                OwnerKey = EffectOwnerKeys.Entity(ptr),
                PluginId = pluginId,
            });
        }
        return grants;
    }
}
