using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// One species' own lean signals, each a per-mille index in `[0, 1000]` (spec-per-species-lean.md
/// "Signals — a closed set"). Higher means "more of this", and every signal is *subtracted* from the
/// lean as a penalty, so a specialist keeps a sharper build than a generalist.
/// </summary>
/// <param name="Specialisation">How lopsided the species' own base stats are: the gap between its
/// per-mille rank of base attack and of base hp, within its own side. A glass cannon or a wall is a
/// specialist; a middling shooter is not.</param>
/// <param name="Pure">The anchor's own "no distinct secondary" mark: 1000 or 0.</param>
/// <param name="ThreatRung">How far up the threat ladder it sits, as the rung's ORDINAL
/// (never its <c>thetaOffset</c>, which is a Θ quantity and must not be read twice).</param>
public sealed record LeanSignalSet(string SpeciesId, long Specialisation, long Pure, long ThreatRung)
{
    /// <summary>The signal by name, over the closed set <see cref="LeanSignals.Names"/>. A name
    /// outside that set is a caller defect, never a silent zero.</summary>
    public long For(string signal) => signal switch
    {
        LeanSignals.Specialisation => Specialisation,
        LeanSignals.Pure => Pure,
        LeanSignals.ThreatRung => ThreatRung,
        _ => throw new ArgumentOutOfRangeException(
            nameof(signal), signal, $"not a lean signal; the closed set is {string.Join("/", LeanSignals.Names)}")
    };
}

/// <summary>Every population member's signals, plus what could not be measured
/// (<see cref="Missing"/>, `"&lt;speciesId&gt;:&lt;signal&gt;"`, ordinal-sorted) — a missing signal
/// is neutral and recorded, never a failure (the item ladder's "never reject; narrow and record").</summary>
public sealed record LeanSignalInputs(IReadOnlyList<LeanSignalSet> Sets, IReadOnlyList<string> Missing);

/// <summary>
/// `per-species-lean` (module 6) — the closed signal set a species' build lean is keyed to, and the
/// rank computation behind it.
///
/// <para><b>Ranks, not raw values.</b> Measured plant <c>hpBase</c> spans 300 to 640,000: a raw ratio
/// would let a handful of giants define the scale for everyone, the "fitted curve puts most of the
/// roster in two rungs" failure `threat-band` already hit and fixed with a table. A per-mille rank is
/// bounded and spreads by construction. Ranks are taken WITHIN A SIDE, so a zombie is compared with
/// zombies.</para>
///
/// <para><b>The population is the caller's, never re-filtered here.</b> This type takes the list
/// <see cref="BuildFavourMeasurer.RosterPopulation"/> produced and returns one set for every row it
/// was given — it does not re-check <c>speciesKind</c>, so there is exactly one filter in the module
/// and a phantom row cannot shift a real species' rank by being quietly dropped here instead.</para>
///
/// <para><b>A missing signal is neutral and recorded.</b> A species with no base-stat row (or whose
/// side has fewer than two ranked species, so there is no spread to rank on) gets
/// <see cref="NeutralPermille"/> for <c>specialisation</c> and appears in the missing list; the same
/// holds for <c>threatRung</c> when the anchor's band is unresolved or names no rung in the loaded
/// ladder. <c>pure</c> is a boolean the anchor always carries, so it is never missing.</para>
/// </summary>
public static class LeanSignals
{
    /// <summary>How lopsided the species' own base stats are.</summary>
    public const string Specialisation = "specialisation";

    /// <summary>The anchor's own "no distinct secondary".</summary>
    public const string Pure = "pure";

    /// <summary>The rung ordinal on the threat ladder.</summary>
    public const string ThreatRung = "threatRung";

    /// <summary>The closed signal set, in declaration order. A new signal is a reviewed change to
    /// this list and to `species-build.v{n}.json`'s `leanSignalWeights` together — never a new key
    /// slipped in beside them.</summary>
    public static readonly IReadOnlyList<string> Names = new[] { Specialisation, Pure, ThreatRung };

    /// <summary>What an unmeasurable signal reads as (the item ladder's narrowing default).</summary>
    public const long NeutralPermille = 500;

    public static LeanSignalInputs Compute(
        IReadOnlyList<AnchorRow> population,
        IReadOnlyList<BaseStatRow> baseStats,
        IReadOnlyList<ThreatRung> rungs)
    {
        if (population is null) throw new ArgumentNullException(nameof(population));
        if (baseStats is null) throw new ArgumentNullException(nameof(baseStats));
        if (rungs is null) throw new ArgumentNullException(nameof(rungs));

        var statByKey = baseStats
            .GroupBy(s => (s.Side, s.GameTypeId))
            .ToDictionary(g => g.Key, g => g.First());

        // The rank population: the rows this call was given that have a base-stat row, per side. An
        // excluded row never reaches here (the caller filtered), so it cannot shift a real rank.
        var ranked = new Dictionary<string, List<(string SpeciesId, double Hp, double Attack)>>(StringComparer.Ordinal);
        foreach (var row in population)
        {
            if (!statByKey.TryGetValue((row.Side, row.GameTypeId), out var stat)) continue;
            if (!ranked.TryGetValue(row.Side, out var sideRows))
                ranked[row.Side] = sideRows = new List<(string, double, double)>();
            sideRows.Add((row.SpeciesId, stat.HpBase, stat.AttackBase));
        }

        var hpRank = new Dictionary<string, long>(StringComparer.Ordinal);
        var attackRank = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (_, sideRows) in ranked)
        {
            Rank(sideRows.Select(r => r.Hp), sideRows.Select(r => r.SpeciesId), hpRank);
            Rank(sideRows.Select(r => r.Attack), sideRows.Select(r => r.SpeciesId), attackRank);
        }

        // The rung ORDINAL is read from the ladder's own declaration; `thetaOffset` is never touched.
        var rungByBand = rungs
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Rung, StringComparer.Ordinal);

        var sets = new List<LeanSignalSet>(population.Count);
        var missing = new List<string>();
        foreach (var row in population.OrderBy(s => s.SpeciesId, StringComparer.Ordinal))
        {
            long specialisation;
            // "Measured" means this species' own side had enough members to rank at all — a side with
            // a single ranked species has no spread, so nothing is ranked and the value is neutral.
            if (hpRank.ContainsKey(row.SpeciesId) && attackRank.ContainsKey(row.SpeciesId))
            {
                checked { specialisation = Math.Abs(attackRank[row.SpeciesId] - hpRank[row.SpeciesId]); }
            }
            else
            {
                specialisation = NeutralPermille;
                missing.Add($"{row.SpeciesId}:{Specialisation}");
            }

            long threatRung;
            if (rungs.Count > 1 && row.ThreatBand is { } band && rungByBand.TryGetValue(band, out var rung))
            {
                checked { threatRung = (rung - 1L) * 1000 / (rungs.Count - 1); }
            }
            else
            {
                threatRung = NeutralPermille;
                missing.Add($"{row.SpeciesId}:{ThreatRung}");
            }

            sets.Add(new LeanSignalSet(
                SpeciesId: row.SpeciesId,
                Specialisation: specialisation,
                Pure: row.Pure ? 1000 : 0,
                ThreatRung: threatRung));
        }

        missing.Sort(StringComparer.Ordinal);
        return new LeanSignalInputs(sets, missing);
    }

    /// <summary>Per-mille competition rank within one side: a value's rank is how many side members
    /// sit strictly below it, scaled to `[0, 1000]` (min → 0, max → 1000), so equal values share a
    /// rank and no pair of giants can define the scale for everyone. A side with fewer than two ranked
    /// species has no spread, so nothing is ranked — the caller records it as missing.</summary>
    static void Rank(IEnumerable<double> values, IEnumerable<string> speciesIds, Dictionary<string, long> into)
    {
        var ids = speciesIds.ToArray();
        var raw = values.ToArray();
        var n = raw.Length;
        if (n < 2) return;

        var ordered = raw.OrderBy(v => v).ToArray();
        // Ordinal speciesId order makes the write deterministic when a value ties.
        var pairs = ids.Zip(raw, (id, v) => (Id: id, Value: v)).OrderBy(p => p.Id, StringComparer.Ordinal);
        foreach (var (id, value) in pairs)
        {
            var below = 0;
            while (below < n && ordered[below] < value) below++;
            checked { into[id] = below * 1000L / (n - 1); }
        }
    }
}

/// <summary>One row of the committed game capture `gk-data/packs/fusion/data/seed/creatures/_dump/type-base-stats.json`:
/// the two stats the specialisation rank reads, joined to an anchor on `(side, gameTypeId)`
/// (911 entries on 2026-09-17 — a reading, never a constant).</summary>
public sealed record BaseStatRow(string Side, int GameTypeId, double HpBase, double AttackBase);

public sealed class BaseStatDumpRejection : Exception
{
    public BaseStatDumpRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O — the dump nests each capture as a JSON *string* in
/// <c>statsJson</c>, so this reads two layers and refuses by name rather than skipping a row.</summary>
public static class BaseStatDump
{
    public static IReadOnlyList<BaseStatRow> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new BaseStatDumpRejection("base-stat dump: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new BaseStatDumpRejection($"base-stat dump: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new BaseStatDumpRejection("base-stat dump: missing or non-array 'entries'");

            var rows = new List<BaseStatRow>();
            foreach (var entry in entries.EnumerateArray())
            {
                var side = Str(entry, "side");
                var typeId = entry.TryGetProperty("typeId", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var id)
                    ? id
                    : throw new BaseStatDumpRejection($"base-stat dump: entry '{side}' has no integer 'typeId'");

                JsonDocument stats;
                try { stats = JsonDocument.Parse(Str(entry, "statsJson")); }
                catch (JsonException ex)
                {
                    throw new BaseStatDumpRejection(
                        $"base-stat dump: {side}/{typeId} 'statsJson' is not valid JSON — {ex.Message}");
                }

                using (stats)
                {
                    rows.Add(new BaseStatRow(
                        Side: side,
                        GameTypeId: typeId,
                        HpBase: Number(stats.RootElement, "hpBase", side, typeId),
                        AttackBase: Number(stats.RootElement, "attackBase", side, typeId)));
                }
            }

            return rows;
        }
    }

    static double Number(JsonElement stats, string key, string side, int typeId) =>
        stats.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : throw new BaseStatDumpRejection($"base-stat dump: {side}/{typeId} has no numeric '{key}'");

    static string Str(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new BaseStatDumpRejection($"base-stat dump: entry missing or non-string '{key}'");
}
