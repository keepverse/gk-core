using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Match;
using FusionRpg.Core.Scope;

namespace FusionRpg.Core.Battle;

/// <summary>
/// Answers "whose side is this ptr on right now" for a <see cref="BattlefieldOwnSideReactor"/>.
/// A deliberate seam (matching this program's own `IContainerEffectResolver` precedent): the real
/// answer needs specimen ownership when a creature specimen exists, and the mechanical PvZ type
/// otherwise (buff-debuff-scope-ideal.md §2.3/§4.1) — the specimen-ownership half needs a Cold-plane
/// `player_id` bridge that does not exist yet (confirmed: no such read path in Core today). Building
/// against this interface now, rather than half-implementing that bridge, matches
/// `StubIntentSource`'s own precedent of building against `IBattleView` before its full caller
/// existed.
/// </summary>
public interface IOwnSideOracle
{
    /// <summary>Null when genuinely unknown (e.g. no board position tracked for this ptr at all).</summary>
    RelationKind? RelationOf(string ptr);
}

/// <summary>
/// T8 (buff-debuff-scope-todo.md Phase 3) — the event-driven grant/withdraw mechanism itself.
/// Subscribes to <see cref="ScopeMembershipEvent"/> (from `membership-events`, T5/T6) and reacts:
/// grants on Bound/MindControlToggled when the oracle says this ptr now matches <see cref="Wants"/>,
/// withdraws (unconditionally — safe no-op if nothing was granted) on Cleared or a
/// MindControlToggled away from <see cref="Wants"/>. Never a cached or rescanned population
/// (buff-debuff-scope-ideal.md §4.1/§4.4 already settled this).
/// </summary>
public sealed class BattlefieldOwnSideReactor
{
    readonly EffectBag _bag;
    readonly IOwnSideOracle _oracle;
    readonly string _effectId;
    readonly string _pluginId;
    readonly string _grantIdPrefix;
    readonly HashSet<string> _grantedPtrs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// T10: <paramref name="atomKindId"/>/<paramref name="host"/>/<paramref name="channel"/> are
    /// checked against <see cref="ScopeCompatibility"/> at construction time — never at grant time,
    /// so a caller cannot build a reactor that would silently issue an inert (or actively wrong)
    /// per-entity grant for a kind the table marks <see cref="ScopeDeliveryShape.SideWideConstant"/>
    /// on this host (the G8 case). Rejects `ScopeUnsupported` up front instead.
    /// </summary>
    public BattlefieldOwnSideReactor(
        EffectBag bag, IOwnSideOracle oracle, RelationKind wants,
        string effectId, string pluginId, string grantIdPrefix,
        string atomKindId, ScopeHost host, string? channel = null)
    {
        var support = ScopeCompatibility.Resolve(atomKindId, WhereScope.Battlefield, WhoKind.Relation, host, channel);
        if (support.Shape != ScopeDeliveryShape.PerEntityGrant)
            throw new ScopeUnsupportedException(atomKindId, WhereScope.Battlefield, WhoKind.Relation, host);

        _bag = bag;
        _oracle = oracle;
        Wants = wants;
        _effectId = effectId;
        _pluginId = pluginId;
        _grantIdPrefix = grantIdPrefix;
    }

    /// <summary>Typically <see cref="RelationKind.Ally"/> (buff) or <see cref="RelationKind.Enemy"/> (debuff).</summary>
    public RelationKind Wants { get; }

    /// <summary>The plugin id every grant this reactor issues carries — for a caller's own bulk sweeps.</summary>
    public string PluginId => _pluginId;

    public void OnMembershipChanged(ScopeMembershipEvent e)
    {
        switch (e.Transition)
        {
            case ScopeMembershipTransition.Bound:
                if (_oracle.RelationOf(e.Ptr) == Wants)
                    Grant(e.Ptr);
                break;

            case ScopeMembershipTransition.Cleared:
                // Unconditional: if we granted it, clean it up; if we didn't, this is a safe no-op
                // (EffectBag.WithdrawForOwner returns 0 for an owner with nothing granted).
                Withdraw(e.Ptr);
                break;

            case ScopeMembershipTransition.MindControlToggled:
                if (_oracle.RelationOf(e.Ptr) == Wants)
                    Grant(e.Ptr);
                else
                    Withdraw(e.Ptr);
                break;
        }
    }

    /// <summary>
    /// Withdraws every grant this reactor has issued so far — for a caller tearing the whole reactor
    /// down (e.g. a debug session ending), not a per-entity Cleared/MindControlToggled reaction.
    /// </summary>
    public void WithdrawAll()
    {
        foreach (var ptr in _grantedPtrs.ToArray())
            Withdraw(ptr);
    }

    void Grant(string ptr)
    {
        _bag.Grant(BuildGrant(ptr));
        _grantedPtrs.Add(ptr);
    }

    void Withdraw(string ptr)
    {
        _bag.WithdrawForOwner("entity", EffectOwnerKeys.Entity(ptr));
        _grantedPtrs.Remove(ptr);
    }

    EffectGrantDto BuildGrant(string ptr) => new()
    {
        GrantId = $"{_grantIdPrefix}:{ptr}",
        EffectId = _effectId,
        OwnerKind = "entity",
        OwnerKey = EffectOwnerKeys.Entity(ptr),
        PluginId = _pluginId,
    };
}
