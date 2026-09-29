using FusionRpg.Core.Items.Thresholds;

namespace FusionRpg.Core.Items.Surfaces;

/// <summary>
/// What one equipped piece is doing for the wearer's sets, from the piece's own point of view — the
/// direction the tooltip asks in and the direction <see cref="SetEvaluator.Progress"/> does not
/// answer, because it reports per set.
/// </summary>
/// <param name="AdvancesSetIds">Every set this piece moves the counter of. <b>Routinely more than
/// one.</b></param>
/// <param name="RedundantSetIds">Sets where this piece names a <c>(set, role)</c> pair an
/// earlier-listed piece already claimed. Counting is per ROLE (ssot-sets.md §4.5), so the second one
/// does not count — and equipping it stays LEGAL, which is exactly why it is a disclosure rather than
/// a rejection. <i>"The UI must show '3 / 4' and say why the fourth did not count."</i></param>
public readonly record struct PieceSetDisclosure(
    EquippedPiece Piece,
    IReadOnlyList<string> AdvancesSetIds,
    IReadOnlyList<string> RedundantSetIds);

/// <summary>
/// The tooltip half of module 12's set evaluator: which sets each worn piece advances, and which it
/// silently does not.
///
/// <para>⭐ <b>This exists because of a measured corpus fact, not a hypothetical.</b> Module 12's
/// <c>One_shipped_item_can_advance_more_than_one_set_and_the_corpus_already_relies_on_it</c> pinned
/// it: the 30 shipped sets declare <b>154</b> distinct <c>(role, base type)</c> member pairs and
/// <b>25</b> of them belong to more than one set, one to three. So a single equipped item
/// legitimately advances three different "3 / 4"s at once, and a card that renders one of them has
/// rendered a third of the truth. Module 12 filed the disclosure requirement here by name; this is
/// the pick-up.</para>
///
/// <para>⛔ <b>It counts nothing of its own.</b> Membership comes from
/// <see cref="SetEvaluator.Hits"/>' own <c>(set, role)</c> dedupe discipline, re-expressed per piece —
/// a second counter here could disagree with the "3 / 4" beside it, which is the same
/// two-implementations failure the near-miss rule forbids on the socket side.</para>
/// </summary>
public static class SetDisclosure
{
    /// <summary>
    /// Per worn piece, the sets it advances and the sets it is redundant in.
    ///
    /// <para><b>Order is load-bearing and is the caller's.</b> "Which copy counted" is decided by
    /// position in <paramref name="equipped"/>, exactly as <see cref="SetEvaluator.Hits"/> decides it,
    /// so the two never disagree about which of two interchangeable pieces is the redundant one.</para>
    /// </summary>
    public static IReadOnlyList<PieceSetDisclosure> ForWearer(
        IReadOnlyList<EquippedPiece> equipped, IReadOnlyList<SetDef> sets)
    {
        if (equipped is null) throw new ArgumentNullException(nameof(equipped));
        if (sets is null) throw new ArgumentNullException(nameof(sets));

        var byMember = new Dictionary<(string ContainerId, ItemRole Role), List<string>>();
        foreach (var set in sets)
            foreach (var m in set.Members)
            {
                var key = (m.ContainerId, m.Role);
                if (!byMember.TryGetValue(key, out var list)) byMember[key] = list = new List<string>();
                if (!list.Contains(set.SetId)) list.Add(set.SetId);
            }

        var claimed = new HashSet<(string SetId, ItemRole Role)>();
        var result = new List<PieceSetDisclosure>(equipped.Count);

        foreach (var piece in equipped)
        {
            var advances = new List<string>();
            var redundant = new List<string>();

            if (byMember.TryGetValue((piece.ContainerId, piece.Role), out var setIds))
                foreach (var setId in setIds.OrderBy(s => s, StringComparer.Ordinal))
                    (claimed.Add((setId, piece.Role)) ? advances : redundant).Add(setId);

            result.Add(new PieceSetDisclosure(piece, advances, redundant));
        }

        return result;
    }

    /// <summary>
    /// The corpus-level fact the card needs before a player has equipped anything: which
    /// <c>(role, base type)</c> pairs belong to more than one set. A compendium/base-type tooltip can
    /// then say "this piece belongs to three sets" without simulating a wearer.
    /// </summary>
    public static IReadOnlyDictionary<(ItemRole Role, string ContainerId), IReadOnlyList<string>> SharedMembers(
        IReadOnlyList<SetDef> sets)
    {
        if (sets is null) throw new ArgumentNullException(nameof(sets));

        var owners = new Dictionary<(ItemRole, string), List<string>>();
        foreach (var set in sets)
            foreach (var m in set.Members)
            {
                var key = (m.Role, m.ContainerId);
                if (!owners.TryGetValue(key, out var list)) owners[key] = list = new List<string>();
                if (!list.Contains(set.SetId)) list.Add(set.SetId);
            }

        return owners
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }
}
