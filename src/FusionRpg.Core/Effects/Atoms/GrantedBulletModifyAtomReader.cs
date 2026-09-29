using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// One resolved <c>bullet.modify</c> grant, as read off a bound def's own action row — E37
/// (spec-projectile-control.md §2b). <paramref name="Op"/> is <c>set</c>/<c>add</c>/<c>scale</c>
/// (§2b's own closed vocabulary); <paramref name="Amount"/> is whole damage units for
/// <c>set</c>/<c>add</c> and per-mille for <c>scale</c> (1500 = x1.5) — the caller (the injector's
/// <c>CheatPrefixes.BulletInitCheat</c> postfix) is what knows which, because that is where the op
/// actually gets applied.
/// </summary>
public sealed record BoundBulletModifyAtom(
    string Op, long Amount, int? BulletType, string? MoveWay, string SourceId);

/// <summary>
/// The Unity-free half of the lawn <c>bullet.modify</c> executor: turns the grants an
/// <see cref="IEffectGrantStore"/> already holds for a firing plant (or the match) into the
/// <see cref="BoundBulletModifyAtom"/> list <c>CheatPrefixes.BulletInitCheat</c> applies.
///
/// <para><b>Why this is simpler than <c>GrantedDerivedAtomReader</c>.</b> That reader supports two
/// transports (a namespaced grant overlay, and the def's own action rows) because <c>stat.derived</c>'s
/// <c>amount</c> is <c>OverlayOrParam</c> — a rolled magnitude can ride the overlay. <c>bullet.modify</c>'s
/// <c>amount</c> is plain <c>Required</c> (never overlay-driven, per its own <c>ParamSchema</c> in
/// <see cref="AtomKindRegistry"/>), so there is exactly one transport: the def's compiled
/// <c>BulletModify</c> action row, read straight off <c>row.Params</c> — <c>AtomCompiler.ToOpcodeShape</c>
/// only rewrites <c>stat.modify</c>/<c>stat.derived</c>, so <c>op</c>/<c>amount</c>/<c>bulletType</c>/
/// <c>moveWay</c> reach the def exactly as authored, with no op-as-key rewrite to undo here.</para>
/// </summary>
public static class GrantedBulletModifyAtomReader
{
    /// <summary>
    /// Every bound <c>bullet.modify</c> atom that applies to the plant that fired this bullet (plus
    /// any match-scoped grant). Returns an empty array — never null — when nothing is bound, so a
    /// missing catalog/grant store is a normal state (no live match), not an error.
    /// </summary>
    public static IReadOnlyList<BoundBulletModifyAtom> Read(
        IEffectGrantStore? grants, IEffectCatalog? catalog, string? firingPlantOwnerKey)
    {
        if (grants is null || catalog is null) return Array.Empty<BoundBulletModifyAtom>();

        List<BoundBulletModifyAtom>? found = null;

        Collect(grants, catalog, "match", EffectOwnerKeys.Match, ref found);

        if (!string.IsNullOrWhiteSpace(firingPlantOwnerKey))
            Collect(grants, catalog, "plant", firingPlantOwnerKey!, ref found);

        return (IReadOnlyList<BoundBulletModifyAtom>?)found ?? Array.Empty<BoundBulletModifyAtom>();
    }

    static void Collect(
        IEffectGrantStore grants, IEffectCatalog catalog, string ownerKind, string ownerKey,
        ref List<BoundBulletModifyAtom>? into)
    {
        IReadOnlyList<EffectGrant> list;
        try { list = grants.ForOwner(ownerKind, ownerKey); }
        catch { return; }

        if (list is null) return;

        for (var i = 0; i < list.Count; i++)
        {
            var g = list[i];
            if (g is null) continue;

            EffectDef? def;
            try { def = catalog.Get(g.EffectId); }
            catch { continue; }

            if (def?.Actions is null || def.Actions.Count == 0) continue;

            for (var a = 0; a < def.Actions.Count; a++)
            {
                var row = def.Actions[a];
                if (row?.Params is null) continue;
                if (!string.Equals(row.Action, EffectActions.BulletModify, StringComparison.OrdinalIgnoreCase))
                    continue;

                var op = JsonOverlay.GetString(row.Params, "op");
                if (string.IsNullOrEmpty(op)) continue; // load-time Vocabulary already refused a bad op

                var amount = JsonOverlay.GetLong(row.Params, "amount");
                var bulletType = JsonOverlay.GetIntOrNull(row.Params, "bulletType");
                var moveWay = JsonOverlay.GetString(row.Params, "moveWay");

                (into ??= new List<BoundBulletModifyAtom>()).Add(new BoundBulletModifyAtom(
                    op!, amount, bulletType, moveWay,
                    SourceId: string.IsNullOrWhiteSpace(g.EffectId) ? g.GrantId : g.EffectId));
            }
        }
    }
}
