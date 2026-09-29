using FusionRpg.Core.Items.Drops;

namespace FusionRpg.Core.Delve.Loot;

/// <summary>
/// `dungeon-loot` D3.14 (spec-dungeon-loot.md §4, "Rung reward columns — the floor and the shift") —
/// the two pure, math-only pieces of the rung reward columns: a FLOOR (removes low rungs entirely) and
/// a WEIGHT SHIFT (moves the default weight column up or down `n` rungs, "never a multiplier"). Both
/// are breadth and ceiling, never `contentScale`/`P(Θ)`/an atom range (spec, verbatim).
///
/// <para><see cref="Apply"/> (D3.11) is the per-table orchestrator composing the two into a room's own
/// live table, called from <see cref="DelveLoot.RollRoom"/> — never the pipeline's own shared `Tables`
/// map directly, so one room's floor/shift can never leak into another's.</para>
/// </summary>
public static class RarityShift
{
    /// <summary>
    /// Spec §4, verbatim: "floor = the highest ordinal among the entry's authored `RarityFloor`, the
    /// room kind's, the rung's and — once-domain boss — `domain.onceEntry.bossRarityFloor`." Any source
    /// may be `null` (not authored, or not a once-domain boss); the floor is the strongest of whichever
    /// are present, never an average or a sum. `null` when NONE are present — no floor at all.
    /// </summary>
    public static string? ComposeFloor(IReadOnlyList<RarityRung> ladder, params string?[] floors)
    {
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (floors is null) throw new ArgumentNullException(nameof(floors));

        var ordinals = floors.Where(f => f is not null).Select(f => RarityDraw.OrdinalOf(ladder, f!)).ToList();
        if (ordinals.Count == 0) return null;

        var maxOrdinal = ordinals.Max();
        return ladder.First(r => r.Ordinal == maxOrdinal).RarityId;
    }

    /// <summary>
    /// Spec §4, verbatim: "`delta[o] = w(o − 10n) − w(o)` per ordinal, bottom `n` rungs zeroed, top
    /// absorbing — the default weight column moved up `n` rungs; the window shifts, nothing is
    /// multiplied." `10` is not hardcoded here — it is the ladder's own real ordinal step, read from
    /// the supplied ladder (10 for the shipped ten-rung ladder, `core.v1.json`), so this stays correct
    /// even if a future ladder spaces ordinals differently.
    ///
    /// <para><b>Verified directly against the spec's own precise test citation</b> (`spec-dungeon-loot.md`
    /// Testing strategy: *"`ToWeightShift(ladder, 1)` sums to zero and zeroes exactly the bottom rung"*):
    /// working the formula by hand for `n=1` over a 10-rung ladder shows `Σ delta[o] = -w(max)` under
    /// the NAIVE "zero outside range" reading alone — for the sum to reach zero as the spec demands,
    /// the TOP ordinal's own delta cannot be the plain formula; it must be the RESIDUAL that makes the
    /// total conserve (`delta[max] = −Σ(every other delta)`), which is exactly what "top absorbing"
    /// means: the top rung keeps its own weight AND gains whatever shifted up into it, rather than the
    /// plain formula overwriting it and losing `w(max)` to a nonexistent ordinal above the ladder.</para>
    ///
    /// <para><b>Assumed, not separately cited:</b> for a NEGATIVE `n` ("shifts down, the ladder's
    /// `very-easy`", spec verbatim), the roles mirror by symmetry — the BOTTOM ordinal becomes the
    /// absorbing edge and the TOP zeroes naturally. The spec's own precise test citation only exercises
    /// `n=1`; this file's own tests prove the conservation property (`Σ delta = 0`) holds for a negative
    /// shift too, as the strongest evidence available that the extrapolation is sound, but the exact
    /// negative-shift boundary is this file's own reasoned choice, not a second literal citation.</para>
    /// </summary>
    public static IReadOnlyDictionary<int, int> ToWeightShift(IReadOnlyList<RarityRung> ladder, int n)
    {
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (ladder.Count == 0) throw new ArgumentException("ladder must not be empty", nameof(ladder));

        var ordinals = ladder.Select(r => r.Ordinal).OrderBy(o => o).ToList();
        if (n == 0) return ordinals.ToDictionary(o => o, _ => 0); // spec, verbatim: "n = 0 is empty"

        if (ordinals.Count < 2)
            throw new ArgumentException("a shift needs at least two rungs to have a step between them", nameof(ladder));

        var step = ordinals[1] - ordinals[0];
        var minOrdinal = ordinals[0];
        var maxOrdinal = ordinals[^1];
        var byOrdinal = ladder.ToDictionary(r => r.Ordinal, r => r.DropWeightPer100k);

        int W(int x) => x >= minOrdinal && x <= maxOrdinal && byOrdinal.TryGetValue(x, out var w) ? w : 0;

        // The edge opposite the shift's own source direction absorbs the residual (conserves total
        // weight); the other edge zeroes naturally through W's own "0 outside [min,max]" rule.
        var absorbOrdinal = n > 0 ? maxOrdinal : minOrdinal;

        var delta = new Dictionary<int, int>();
        var runningSum = 0;
        foreach (var o in ordinals.Where(o => o != absorbOrdinal))
        {
            var d = checked(W(o - step * n) - W(o));
            delta[o] = d;
            runningSum = checked(runningSum + d);
        }
        delta[absorbOrdinal] = checked(-runningSum);
        return delta;
    }

    /// <summary>
    /// D3.11: composes <see cref="ComposeFloor"/>/<see cref="ToWeightShift"/> into the ONE table at
    /// <paramref name="tableId"/> — every other table in <paramref name="tables"/> passes through
    /// untouched, so a nested `Table`-kind entry pointing elsewhere never sees this room's own floor
    /// or shift. Returns <paramref name="tables"/> itself, unmodified, when <paramref name="tableId"/>
    /// is not present — an unknown table id is <see cref="LootPipeline.Resolve"/>'s own refusal to
    /// raise, not this function's.
    /// </summary>
    /// <param name="shiftRungs">The rung's own `RarityShiftRungs` plus the room kind's (`loot.rooms
    /// .boss.rarityShiftRungs`) — "kind and rung shifts add" (spec §4, verbatim); summed by the
    /// caller before this runs, matching <see cref="ComposeFloor"/>'s own "caller gathers, function
    /// composes" shape rather than this function reading either tunable itself.</param>
    /// <param name="floors">The room-kind's, the rung's, and — once-domain boss only — the domain's
    /// own `bossRarityFloor`; each entry's OWN authored `RarityFloor` is prepended automatically, so a
    /// caller never repeats it here.</param>
    public static IReadOnlyDictionary<string, DropTableRow> Apply(
        IReadOnlyDictionary<string, DropTableRow> tables, IReadOnlyList<RarityRung> ladder,
        string tableId, int shiftRungs, params string?[] floors)
    {
        if (tables is null) throw new ArgumentNullException(nameof(tables));
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (tableId is null) throw new ArgumentNullException(nameof(tableId));
        if (floors is null) throw new ArgumentNullException(nameof(floors));

        if (!tables.TryGetValue(tableId, out var table))
            return tables;

        var shift = ToWeightShift(ladder, shiftRungs);
        var patched = table with
        {
            Groups = table.Groups.Select(g => g with
            {
                Entries = g.Entries.Select(e => e with
                {
                    RarityFloor = ComposeFloor(ladder, new[] { e.RarityFloor }.Concat(floors).ToArray()),
                    RarityWeightShift = shift,
                }).ToList(),
            }).ToList(),
        };

        return new Dictionary<string, DropTableRow>(tables, StringComparer.Ordinal) { [tableId] = patched };
    }

    /// <summary>
    /// `delve-quests` D4.12 (spec-delve-quests.md §4 step 3, verbatim: *"`ceilRung` zeroes every rung
    /// above it in `RarityWeightShift` — 'row kept, never drawn'"*). Distinct in kind from
    /// <see cref="ToWeightShift"/>: a ceiling never redistributes weight — the quest reward's own spec
    /// text makes no "sums to zero" claim the way the floor-and-shift's does — it only deletes the top
    /// end, so the delta needed is simply enough to zero each rung above the ceiling outright
    /// (<c>-w(o)</c>), and nothing at or below it.
    /// </summary>
    public static IReadOnlyDictionary<int, int> ToCeilingShift(IReadOnlyList<RarityRung> ladder, string ceilRung)
    {
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (ladder.Count == 0) throw new ArgumentException("ladder must not be empty", nameof(ladder));
        if (ceilRung is null) throw new ArgumentNullException(nameof(ceilRung));

        var ceilOrdinal = RarityDraw.OrdinalOf(ladder, ceilRung);
        var delta = new Dictionary<int, int>();
        foreach (var rung in ladder)
            delta[rung.Ordinal] = rung.Ordinal > ceilOrdinal ? checked(-rung.DropWeightPer100k) : 0;
        return delta;
    }

    /// <summary>
    /// `delve-quests` D4.12: the quest-reward WINDOW composer — a floor and a ceiling, never a shift.
    /// A quest's own <c>QuestRewardWindow</c> carries no `shiftRungs` at all (unlike a room, which
    /// always has a rung's own shift to fold in via <see cref="Apply"/>) — the two functions patch the
    /// same two fields on the same table shape, but never the same axis, so this is a genuinely
    /// separate composer rather than a special case of <see cref="Apply"/>. Patches the ONE table at
    /// <paramref name="tableId"/> exactly like <see cref="Apply"/> does — every other table in
    /// <paramref name="tables"/>, and every other entry's own <see cref="DropTableEntryRow.RarityFloor"/>
    /// source, untouched.
    /// </summary>
    /// <param name="composedFloorRung">Already the strongest of every floor source the caller holds
    /// (<see cref="ComposeFloor"/>) — folded here with each entry's OWN authored floor, never
    /// overriding it.</param>
    public static IReadOnlyDictionary<string, DropTableRow> ApplyWindow(
        IReadOnlyDictionary<string, DropTableRow> tables, IReadOnlyList<RarityRung> ladder,
        string tableId, string? composedFloorRung, string ceilRung)
    {
        if (tables is null) throw new ArgumentNullException(nameof(tables));
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (tableId is null) throw new ArgumentNullException(nameof(tableId));
        if (ceilRung is null) throw new ArgumentNullException(nameof(ceilRung));

        if (!tables.TryGetValue(tableId, out var table))
            return tables;

        var ceilingShift = ToCeilingShift(ladder, ceilRung);
        var patched = table with
        {
            Groups = table.Groups.Select(g => g with
            {
                Entries = g.Entries.Select(e => e with
                {
                    RarityFloor = ComposeFloor(ladder, e.RarityFloor, composedFloorRung),
                    RarityWeightShift = ceilingShift,
                }).ToList(),
            }).ToList(),
        };

        return new Dictionary<string, DropTableRow>(tables, StringComparer.Ordinal) { [tableId] = patched };
    }
}
