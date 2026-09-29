using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>One thing the wearer actually has in a role. <c>ContainerId</c> is the base type the item
/// was minted from — what <c>item_set_member</c> names.</summary>
public readonly record struct EquippedPiece(ItemRole Role, string ContainerId);

/// <summary>A counted membership: this set, this role. Deduped by <c>(SetId, Role)</c> before counting.</summary>
public readonly record struct SetMemberHit(string SetId, ItemRole Role);

/// <summary>What one set looks like to the wearer right now — the "3 / 4" module 20 renders.</summary>
public readonly record struct SetProgress(string SetId, int Count, int Total, IReadOnlyList<string> WantedContainerIds);

/// <summary>
/// The set-bonus consumer of <see cref="ThresholdEvaluator"/> — ssot-sets.md §4.2 names this exact path.
/// It owns no counting logic of its own; it builds a consumer and hands it to the one machine.
///
/// <para><b>⭐ Two partial sets are legal, budgeted for, and expected</b> (I5 §3.6, and it is evaluator
/// behaviour rather than authoring, so it is claimed here). Four rules follow, all of them shape rather
/// than policy: the counter is per set id, so two sets at two pieces each produce two independent
/// reductions and never one merged count; breakpoints are looked up per set; each tier binding carries
/// <c>source = set:{set_id}</c>, so withdrawing one set touches nothing of the other; and
/// <b>there is no cap on how many sets a wearer may be partially in.</b> The slot count is the cap, and
/// the slot count is structural. A <c>maxActiveSets</c> here would be a hard progression ceiling wearing
/// a balance name, and it would undo module 13's most important authoring rule — I5 moved the capability
/// to the two-piece threshold BECAUSE two partials are expected; if the payoff sat at the top, two
/// partials would be two lots of stat filler and the choice would be fake.</para>
///
/// <para><b>Counting is per ROLE, not per item</b> (ssot-sets.md §4.5). Two copies of the same set ring
/// worn in <c>jewel-minor-a</c> and <c>jewel-minor-b</c> count as one, because the member row declares
/// one role. That closes the obvious cheese — buy four copies of the cheapest member, wear them
/// everywhere — with no special case, and it is a DISCLOSURE requirement for the tooltip rather than a
/// rejection: equipping a duplicate stays legal, so the UI must show "3 / 4" and say why the fourth did
/// not count.</para>
/// </summary>
public static class SetEvaluator
{
    /// <summary>
    /// Which member roles the wearer has filled, deduped per <c>(set, role)</c> so a duplicate base type
    /// in a second role cannot inflate a count.
    /// </summary>
    public static IReadOnlyList<SetMemberHit> Hits(
        IEnumerable<EquippedPiece> equipped, IReadOnlyList<SetDef> sets)
    {
        var byMember = new Dictionary<(string ContainerId, ItemRole Role), List<string>>();
        foreach (var set in sets)
            foreach (var m in set.Members)
            {
                var key = (m.ContainerId, m.Role);
                if (!byMember.TryGetValue(key, out var list)) byMember[key] = list = new List<string>();
                list.Add(set.SetId);
            }

        var seen = new HashSet<(string, ItemRole)>();
        var hits = new List<SetMemberHit>();
        foreach (var piece in equipped)
        {
            if (!byMember.TryGetValue((piece.ContainerId, piece.Role), out var setIds)) continue;
            foreach (var setId in setIds)
                if (seen.Add((setId, piece.Role)))
                    hits.Add(new SetMemberHit(setId, piece.Role));
        }

        return hits;
    }

    /// <summary>One set's consumer. Built per set id — never shared, so two partials never merge.</summary>
    public static ThresholdConsumer<SetMemberHit> Consumer(SetDef set) =>
        new(
            SourceKey: ThresholdContainerIds.SetSource(set.SetId),
            BucketKey: h => string.Equals(h.SetId, set.SetId, StringComparison.Ordinal) ? set.SetId : null,
            Reducer: ThresholdReducer.Sum,
            Weight: _ => 1,
            Breakpoints: set.Tiers
                .OrderBy(t => t.PiecesRequired)
                .Select(t => new ThresholdBreakpoint(t.PiecesRequired, t.ContainerId))
                .ToList(),
            Buckets: Array.Empty<string>(),
            Priority: ThresholdContainerIds.SetPriority);

    /// <summary>
    /// ssot-sets.md §4.4: a set tier binds to exactly the owner scope its member pieces are bound to,
    /// and <b>never to <c>match</c></b> — the wearer wears the set, the squad does not. Binding a tier
    /// match-wide would silently turn one creature's gear into a team buff, and it would make I5 §3.5's
    /// piece budget unenforceable, because the denominator (one actor's slots) would stop being the
    /// thing being paid for.
    /// </summary>
    public static AtomRejection RefuseUnsupportedScope(OwnerScope owner) => owner.Kind switch
    {
        OwnerKind.UniqueActor => AtomRejection.Ok,
        OwnerKind.Match => AtomRejection.Fail(AtomRejectionReason.ScopeUnsupported,
            "a set tier may not bind at match scope — one creature's gear must not become a team buff " +
            "(ssot-sets.md §4.4)"),
        OwnerKind.Player => AtomRejection.Fail(AtomRejectionReason.ScopeUnsupported,
            "a set tier may not bind at player: scope — StatApplyScope reports player: as match-wide, " +
            "so the tier would reach both sides of the lawn"),
        _ => AtomRejection.Fail(AtomRejectionReason.ScopeUnsupported,
            $"a set tier binds at unique-actor: scope, not {OwnerScope.Name(owner.Kind)}:"),
    };

    /// <summary>
    /// Every set the wearer touches at all, with its count and the tiers it wants. Sets the wearer has
    /// no piece of are absent — not present at zero, which would make "how many sets am I in" a question
    /// about the catalog rather than about the wearer.
    /// </summary>
    public static IReadOnlyList<SetProgress> Progress(
        IEnumerable<EquippedPiece> equipped, IReadOnlyList<SetDef> sets)
    {
        var hits = Hits(equipped, sets);
        var byId = sets.ToDictionary(s => s.SetId, StringComparer.Ordinal);

        var result = new List<SetProgress>();
        foreach (var group in hits.GroupBy(h => h.SetId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var set = byId[group.Key];
            var grant = ThresholdEvaluator.Grant(Consumer(set), group);
            result.Add(new SetProgress(set.SetId, (int)grant.Count, set.DistinctRoleCount, grant.WantedContainerIds));
        }
        return result;
    }
}
