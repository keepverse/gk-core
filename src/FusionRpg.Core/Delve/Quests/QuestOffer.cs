using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.10 (spec-delve-quests.md §1, "Counts are ints derived at entry") — `need = max(1, ceil(rooms_of_kind
/// × milli / 1000))`, the spec's own formula verbatim, one widen (`long × long`), one divide, at the
/// end. `RoomsOfKind`'s own denominator differs per template (§1's own table): `explore-rooms` counts
/// every non-secret room; `cleanse-fights`/`gather-curio-kind` count only rooms matching the quest's
/// own `TargetRef`. The six count-less templates never call `Need` at all — <see cref="QuestCatalog.CountLessTemplates"/>
/// is the same closed set D4.9 already validates a row's `CountBand` against.
/// </summary>
public static class QuestCounts
{
    public static int RoomsOfKind(QuestRow q, IReadOnlyList<DelveRoomFact> facts, Func<string, string, bool> archetypeEventPoolHasKind) =>
        q.TemplateId switch
        {
            "explore-rooms" => facts.Count(f => !f.IsSecret),
            "cleanse-fights" => facts.Count(f => f.Kind == q.TargetRef),
            "gather-curio-kind" => facts.Count(f => archetypeEventPoolHasKind(f.ArchetypeId, q.TargetRef!)),
            _ => 0, // count-less templates never read this
        };

    public static int Need(QuestRow q, int roomsOfKind, IReadOnlyDictionary<string, long> countBandMilli)
    {
        if (q.CountBand is null) return 0; // count-less -- structural, not a countBand read
        if (!countBandMilli.TryGetValue(q.CountBand, out var milli))
            throw new ArgumentException($"'{q.CountBand}' has no quests.countBand.*Milli entry.", nameof(countBandMilli));
        return (int)Math.Max(1, ((long)roomsOfKind * milli + 999) / 1000);
    }
}

/// <summary>
/// D4.10 (spec-delve-quests.md §2) — the satisfiability filter and the entry draw. Every corpus/graph
/// read arrives as a plain caller-supplied delegate or fact list (`DelveRoomFact` from
/// `delve-graph-roll`, already shipped and unmodified) — this file resolves none of
/// `LootContentView`/an archetype's own event-pool object itself, matching this program's own "read
/// model owned elsewhere" shape throughout.
/// </summary>
public static class QuestOffer
{
    /// <summary>
    /// Spec §2 step 2, verbatim: `cleanse-fights`/`explore-rooms`/`gather-curio-kind` need
    /// `RoomsOfKind >= Need` on the rolled graph; `extract-with-item-kind` needs the role in some
    /// `lootBinding` table's base-type set; the other five (`kill-boss` plus the four remaining
    /// count-less templates) "hold on every valid graph" — structural, always satisfiable.
    /// </summary>
    public static IReadOnlyList<QuestRow> Satisfiable(
        IReadOnlyList<QuestRow> pool, IReadOnlyList<DelveRoomFact> facts,
        Func<string, string, bool> archetypeEventPoolHasKind, Func<string, bool> lootBindingOffersRole,
        IReadOnlyDictionary<string, long> countBandMilli)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        if (facts is null) throw new ArgumentNullException(nameof(facts));
        if (archetypeEventPoolHasKind is null) throw new ArgumentNullException(nameof(archetypeEventPoolHasKind));
        if (lootBindingOffersRole is null) throw new ArgumentNullException(nameof(lootBindingOffersRole));
        if (countBandMilli is null) throw new ArgumentNullException(nameof(countBandMilli));

        var kept = new List<QuestRow>(pool.Count);
        foreach (var q in pool)
        {
            var ok = q.TemplateId switch
            {
                "cleanse-fights" or "explore-rooms" or "gather-curio-kind" => SatisfiesCount(q, facts, archetypeEventPoolHasKind, countBandMilli),
                "extract-with-item-kind" => lootBindingOffersRole(q.TargetRef!),
                _ => true,
            };
            if (ok) kept.Add(q);
        }
        return kept;
    }

    static bool SatisfiesCount(QuestRow q, IReadOnlyList<DelveRoomFact> facts, Func<string, string, bool> archetypeEventPoolHasKind, IReadOnlyDictionary<string, long> countBandMilli)
    {
        var roomsOfKind = QuestCounts.RoomsOfKind(q, facts, archetypeEventPoolHasKind);
        return roomsOfKind >= QuestCounts.Need(q, roomsOfKind, countBandMilli);
    }

    /// <summary>
    /// D14's own risk-quest set (spec §2 step 3, verbatim: "kill-boss, cleanse-fights, explore-rooms
    /// at most/all"). `kill-boss`/`cleanse-fights` always count. `explore-rooms` counts only at its
    /// TWO highest countBand members — but the spec's own "most/all" names do not exist in the real
    /// shipped countBand vocabulary (`lone·few·several·many`, D4.9's own named drift), so
    /// <paramref name="isHighCountBand"/> names, per real member, whether it is "most/all"-equivalent
    /// — a plain caller-supplied predicate rather than a guessed pair of real band names.
    /// </summary>
    public static bool IsRiskQuest(QuestRow q, Func<string, bool> isHighCountBand)
    {
        if (q.TemplateId is "kill-boss" or "cleanse-fights") return true;
        return q.TemplateId == "explore-rooms" && q.CountBand is not null && isHighCountBand(q.CountBand);
    }

    /// <summary>
    /// Spec §2 step 4, verbatim: draws without replacement, slot `n` = `WeightedChoice.Pick(options,
    /// seed_n, "dungeon:quest:{n}")` with EQUAL weights (Law 3/S2-12 — no anchor carries a weight),
    /// `seed_n = DeriveStream(delveSeed, "dungeon:quest:{n}").NextULong()`. Step 3's D14 filter is
    /// necessarily sequential (spec: "the set drawn so far holds a risk quest") — recomputed every
    /// slot against what THIS draw has already offered, never the whole pool at once. "Non-sink
    /// quests draw first" falls out of the rule itself below `hard`: a sink-avoidance quest is
    /// ineligible until a risk quest has already landed in the offer, and every risk quest is
    /// itself non-sink by construction (§5: "no template may require a sink" — sink-avoidance and
    /// risk are disjoint sets).
    /// </summary>
    public static IReadOnlyList<QuestRow> Draw(
        IReadOnlyList<QuestRow> satisfiablePool, IReadOnlyList<ObjectiveTemplateDef> objectiveTemplates,
        long delveSeed, int offeredAtEntry, int rungOrdinal, int hardOrdinal, Func<string, bool> isHighCountBand)
    {
        if (satisfiablePool is null) throw new ArgumentNullException(nameof(satisfiablePool));
        if (objectiveTemplates is null) throw new ArgumentNullException(nameof(objectiveTemplates));
        if (isHighCountBand is null) throw new ArgumentNullException(nameof(isHighCountBand));
        if (offeredAtEntry < 0) throw new ArgumentOutOfRangeException(nameof(offeredAtEntry));

        var sinkAvoidanceByTemplate = objectiveTemplates.ToDictionary(t => t.ObjectiveTemplateId, t => t.SinkAvoidance, StringComparer.Ordinal);
        bool IsSinkAvoidance(QuestRow q) => sinkAvoidanceByTemplate.TryGetValue(q.TemplateId, out var s) && s;

        var offered = new List<QuestRow>(offeredAtEntry);
        var remaining = satisfiablePool.OrderBy(q => q.QuestId, StringComparer.Ordinal).ToList(); // §2 step 1: ordinal questId order

        var rungEligible = rungOrdinal >= hardOrdinal;
        for (var n = 0; n < offeredAtEntry && remaining.Count > 0; n++)
        {
            var riskAlreadyDrawn = offered.Any(q => IsRiskQuest(q, isHighCountBand));
            var eligible = remaining.Where(q => !IsSinkAvoidance(q) || rungEligible || riskAlreadyDrawn).ToList();
            if (eligible.Count == 0) break; // fewer than offeredAtEntry left -- QuestPreflight's own refusal (D4.13), not this function's

            var options = eligible.Select(q => new WeightedOption<QuestRow>(q, 1)).ToList(); // equal weights, Law 3/S2-12
            var streamName = $"dungeon:quest:{n}";
            var seedN = unchecked((long)SeededRng.DeriveStream(unchecked((ulong)delveSeed), streamName).NextULong());
            var picked = WeightedChoice.Pick(options, seedN, streamName);

            offered.Add(picked);
            remaining.Remove(picked); // without replacement
        }

        return offered;
    }
}
