using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.World.Loam;

/// <summary>One rolled warband member: the species, whether the future hunt may recruit it, and
/// its HP. The HP is the species' own derived `P(Θ)` magnitude (T18b), falling back to the named
/// interim only when the species carries no magnitude. The spawn writes the pick's HP onto the
/// member verbatim — never recomputed, never a second derivation. The recruitable flag rides the
/// table for the hunt program to read (species-gear-chain T7, "Introduced, not designed").</summary>
public sealed record WildSpawnPick(string SpeciesId, bool Recruitable, long Hp);

/// <summary>Which species a wild warband is made of (species-gear-chain T7,
/// spec-wild-species-spawn.md). Seeded from (worldSeed, sectorId, turn) so a replay never
/// disagrees with itself — the same determinism guarantee <c>RaiseResolver.SpeciesFor</c>
/// documents. CaptureOnly is ADMITTED here and refused in waves: the wild IS its acquisition
/// route, so refusing it strands the species entirely. EventOnly is refused first and
/// unconditionally, in every context.</summary>
public static class WildSpawnRoller
{
    /// <summary>The max-HP magnitude channel a wild member's HP reads (species-gear-chain T18b):
    /// the species' own derived `P(Θ)` value under this channel, never a second derivation and
    /// never a Hub read — a plain dictionary lookup, so no ActorHub composer is involved.</summary>
    internal const string HpChannel = "resource.max.hp";

    /// <summary>T7's interim, retained as THE fallback (species-gear-chain T18b): a species with no
    /// magnitude container — should not exist post-T18, but defensively — spawns at this named
    /// value, never a crash and never a hardcoded fresh number at the spawn site.
    /// Long-typed like its source: hp is a magnitude, never an int.</summary>
    internal static long InterimFlatMemberHp => LoamPolicy.UnmadeMemberHp;

    public static IReadOnlyList<WildSpawnPick> Roll(
        ulong worldSeed, string sectorId, int turn, WorldSector sector, int count,
        WorldSpawnTuning tuning, IEnumerable<CreatureSpeciesDef>? source = null)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "a warband's member count is never negative");

        var seed = SeededRng.DeriveStream(worldSeed, $"wild:{sectorId}:{turn}").NextULong();

        // The candidate defs by id, built once: window draws resolve HP from the drawn def, and
        // authored-member / fallback picks (bare ids from tuning) resolve through the same table.
        // A pick naming a species outside the candidates falls back to the interim — never a crash.
        var defsById = (source ?? CreatureSpeciesCatalog.All)
            .GroupBy(s => s.SpeciesId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        if (!tuning.Sectors.TryGetValue(sector.TypeId, out var row))
            return Fallback(count, tuning, defsById); // unknown sector type: a named species, never a crash

        if (row.Members.Count > 0)
            return RollMembers(row, count, seed, sectorId, turn, defsById);

        var pool = (source ?? CreatureSpeciesCatalog.All)
            .Where(s => CreatureRarityLadder.AtLeast(s.BaseRarity, row.RarityFrom)
                     && CreatureRarityLadder.AtMost(s.BaseRarity, row.RarityTo))
            .Where(CreatureAdmission.ForWildMap)
            .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
            .ToList();
        if (pool.Count == 0)
            return Fallback(count, tuning, defsById); // admissible pool empty: a named species, never a crash

        // ceil(count * milli / 1000), long widened before the multiply, divided once — the same
        // cap shape Delve's SlotFill.Draw and WaveCatalog.Enemies own. A 0 cap is strict
        // without-replacement, which is what ships.
        var cap = (int)((checked((long)count * row.SameSpeciesMaxMilli) + 999) / 1000);
        if (cap < 1) cap = 1;

        var byRung = pool.GroupBy(s => s.BaseRarity).ToDictionary(g => g.Key, g => g.ToList());
        var seats = new Dictionary<string, int>(StringComparer.Ordinal);
        var picks = new List<WildSpawnPick>(count);
        for (var j = 0; j < count; j++)
        {
            var species = DrawOne(byRung, row, seats, cap, sector.Climate, tuning.OffClimateMilli, seed, sectorId, turn, j);
            if (species is null)
                return Fallback(count, tuning, defsById); // count exceeds distinct admissibles: restart named, never half a warband
            seats[species.SpeciesId] = seats.GetValueOrDefault(species.SpeciesId) + 1;
            picks.Add(new WildSpawnPick(species.SpeciesId, Recruitable: true, HpFor(species)));
        }
        return picks;
    }

    /// <summary>An authored member list replaces the window draw for its sector (T7): explicitly
    /// named species — including future non-recruitables — drawn ∝ weight, carrying their flag.</summary>
    internal static IReadOnlyList<WildSpawnPick> RollMembers(
        WorldSpawnSectorTuning row, int count, ulong seed, string sectorId, int turn,
        IReadOnlyDictionary<string, CreatureSpeciesDef> defsById)
    {
        var picks = new List<WildSpawnPick>(count);
        for (var j = 0; j < count; j++)
        {
            var scope = $"wild:{sectorId}:{turn}:members:{j}";
            var options = row.Members
                .Select(m => new WeightedOption<WorldSpawnMemberEntry>(m, m.WeightMilli))
                .ToList();
            var chosen = WeightedChoice.Pick(
                options, unchecked((long)SeededRng.DeriveStream(seed, scope).NextULong()), scope);
            picks.Add(new WildSpawnPick(chosen.SpeciesId, chosen.Recruitable,
                HpFor(chosen.SpeciesId, defsById)));
        }
        return picks;
    }

    internal static CreatureSpeciesDef? DrawOne(
        IReadOnlyDictionary<CreatureRarity, List<CreatureSpeciesDef>> byRung,
        WorldSpawnSectorTuning row,
        IReadOnlyDictionary<string, int> seats,
        int cap,
        ElementTypeId? climate,
        long offClimateMilli,
        ulong seed,
        string sectorId,
        int turn,
        int drawIndex)
    {
        var scope = $"wild:{sectorId}:{turn}:draw:{drawIndex}";
        var drawRng = SeededRng.DeriveStream(seed, scope);

        var rungOptions = new List<WeightedOption<CreatureRarity>>();
        foreach (var r in byRung.Keys.OrderBy(x => x))
        {
            if (!row.Weights.TryGetValue(r.ToId(), out var weight) || weight <= 0)
                continue;
            if (!byRung[r].Any(s => seats.GetValueOrDefault(s.SpeciesId) < cap))
                continue;
            rungOptions.Add(new WeightedOption<CreatureRarity>(r, weight));
        }
        if (rungOptions.Count == 0) return null;

        var rung = WeightedChoice.Pick(
            rungOptions, unchecked((long)drawRng.NextULong()), scope + ":rung");
        // A rung can survive the seat check yet hold nothing drawable once climate weights apply
        // (an off-climate-muted species under offClimateMilli 0): that is a null — the caller
        // falls back — never a throw mid-turn.
        var candidates = byRung[rung]
            .Where(s => seats.GetValueOrDefault(s.SpeciesId) < cap)
            .Select(s => new WeightedOption<CreatureSpeciesDef>(
                s, climate is { } stated && s.ElementPrimary == stated ? 1000 : (int)offClimateMilli))
            .Where(o => o.Weight > 0)
            .ToList();
        if (candidates.Count == 0) return null;
        return WeightedChoice.Pick(
            candidates, unchecked((long)drawRng.NextULong()), scope + ":species");
    }

    /// <summary>T18b: the species' own derived `P(Θ)` HP. A drawn def resolves from itself; a bare
    /// id (authored member, fallback) resolves through the candidate table. A species with no
    /// magnitude — should not exist post-T18, but defensively — takes the named interim, never a
    /// crash and never a hardcoded fresh number. Read-only dictionary lookup: no ActorHub composer
    /// is involved, per the task's own constraint.</summary>
    internal static long HpFor(
        string speciesId, IReadOnlyDictionary<string, CreatureSpeciesDef> defsById) =>
        defsById.TryGetValue(speciesId, out var def)
        && def.Magnitudes.TryGetValue(HpChannel, out var hp)
            ? hp
            : InterimFlatMemberHp;

    internal static long HpFor(CreatureSpeciesDef drawn) =>
        drawn.Magnitudes.TryGetValue(HpChannel, out var hp) ? hp : InterimFlatMemberHp;

    static IReadOnlyList<WildSpawnPick> Fallback(
        int count, WorldSpawnTuning tuning, IReadOnlyDictionary<string, CreatureSpeciesDef> defsById)
    {
        // The named, documented fallback: an unknown sector type, an empty admissible pool, or a
        // count beyond the distinct admissibles all land HERE — one real species, recruitable by
        // its own nature, never a crash mid-turn and never a silent skip of the spawn itself.
        // The fallback species reads its own magnitude like any other pick; only a magnitudeless
        // fallback takes the interim.
        var picks = new List<WildSpawnPick>(count);
        for (var j = 0; j < count; j++)
            picks.Add(new WildSpawnPick(tuning.FallbackSpeciesId, Recruitable: true,
                HpFor(tuning.FallbackSpeciesId, defsById)));
        return picks;
    }
}
