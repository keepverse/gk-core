using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Power;
using FusionRpg.Core.Items.Sockets;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// ⭐ <b>The chaff-chassis watch, answered instead of carried.</b> `item-todo.md` P2.4 filed it as a
/// real, carried-forward question: D21 makes a Strain or Splice legal only on a **low-rarity** chassis
/// — a `chaff` breastplate is the only chassis a Splice can live in, and that is meant to be "a genuine
/// second progression route rather than a consolation prize". The risk the watch names is the mirror of
/// that: if one shipped Splice's granted atoms are worth more than an `almanac`'s own budget, a cheap
/// low-rarity chassis outruns the top rung and module 7's rarity bands need re-deriving.
///
/// <para><b>Why it could not be answered until now, and can be now.</b> The bullet said it was
/// "unanswerable before module 21 `strain-splice-gen` exists". It exists, and its authoring pass ran
/// partially: `gk-data/packs/fusion/data/seed/items/combinations/` ships `splices.json` and `strains.json`, and each entry
/// declares its granted atoms BY ID (`combo.splice-agility-bulwark` →
/// `["atom.evd-harden", "atom.tempo-pulse"]`), which is exactly what a price needs.</para>
///
/// <para>⛔ <b>The formulation, stated because it is a choice and not a fact.</b> The share is taken
/// over the WHOLE GRANT SET — the item's actual gain — rather than per granted atom, because D21's
/// question is what a chassis carrying that Splice is worth, not what one of its atoms is worth. The
/// per-atom maximum is reported beside it so the other reading is visible rather than assumed away.
/// The ratio stays coefficient-insensitive either way: numerator and denominator are both prices from
/// the one cost function, which is `ImplicitShare`'s own stated property.</para>
///
/// <para>⭐ <b>Which TIER is priced, and why it changed.</b> The first cut of this measurement priced
/// each family's WORST tier, because `gk-data/packs/fusion/data/seed/items/combinations/**` carries no `grantedTier`. That
/// is no longer the right reading: SSH7.5/7.6 (`spec-tier-ladder` §3) moved the tier a shape grants to
/// TUNING and re-emitted the corpus with no tier number, so the shipped tier is
/// <c>StrainSpliceTuning.BaseTierFor(shape) + ladder[rung].grantDelta + attuned bonus</c> — a real,
/// readable fact. The measurement now prices at exactly that tier, for every (rung, attuned) pair the
/// shipped ladder can reach, and keeps the worst-tier sum BESIDE it as the upper bound. The worst-tier
/// number is what the row's 5.5× headline came from; the granted-tier number is what a fill produces.</para>
///
/// <para>⚠ The atom ids are read from the raw authoring field rather than through
/// <c>CombinationCorpus.Parse</c>, which maps a combination's payoff as a recipe and does not carry
/// <c>grants</c> — this question is about the granted atoms, so the authoring field is the right
/// source.</para>
/// </summary>
public class ChaffChassisWatchTests
{
    readonly ITestOutputHelper _out;

    public ChaffChassisWatchTests(ITestOutputHelper output) => _out = output;

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>The shipped ten rungs, verbatim from `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` — written out,
    /// the same idiom `RarityPowerCeilingTests` uses, so a seed edit shows up as a diff.</summary>
    static IReadOnlyList<RarityRow> Ladder() => new[]
    {
        new RarityRow("chaff", 10, 0, 0, 1, 1),
        new RarityRow("sprout", 20, 0, 1, 1, 1),
        new RarityRow("grafted", 30, 0, 1, 1, 3),
        new RarityRow("cultivated", 40, 1, 1, 1, 3),
        new RarityRow("fused", 50, 1, 1, 2, 4),
        new RarityRow("chimeric", 60, 1, 2, 2, 4),
        new RarityRow("heirloom", 70, 1, 2, 3, 5),
        new RarityRow("firstseed", 80, 2, 2, 3, 5),
        new RarityRow("sunwoven", 90, 2, 2, 4, 5),
        new RarityRow("almanac", 100, 3, 2, 4, 5),
    };

    /// <summary>`gk-core/data/tuning/item-rarity.v1.json`'s own ‰ column — the `power_ceiling` share per rung,
    /// which is the number that makes a rung's ceiling a PRICE rather than a guess.</summary>
    static readonly IReadOnlyDictionary<string, int> Shares = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["chaff"] = 0,
        ["sprout"] = 22,
        ["grafted"] = 51,
        ["cultivated"] = 84,
        ["fused"] = 173,
        ["chimeric"] = 243,
        ["heirloom"] = 492,
        ["firstseed"] = 632,
        ["sunwoven"] = 818,
        ["almanac"] = 1000,
    };

    static int? ShareOf(string rarityId) => Shares.TryGetValue(rarityId, out var v) ? v : null;

    /// <summary>Every authored atom, grouped by FAMILY — the whole `gk-data/packs/fusion/data/seed/atoms/**` tree,
    /// `generated/` included, because that is where the splice grants live. Grouped by family because a
    /// combination's `grants` name **families** (`atom.evd-harden`), and — see the class doc — the
    /// corpus authors NO `grantedTier`, so the tier is not a fact this measurement may assume. It
    /// prices the family's WORST tier instead, which is the bound a watch wants.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<AtomRow>> AtomsByFamily()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "atoms");
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path: f, Json: File.ReadAllText(f)))
            .ToList();
        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk,
            "the atom seed tree did not collect cleanly:\n"
            + string.Join("\n", collected.Errors.Take(10).Select(e => e.ToString())));
        return collected.Content.Atoms
            .GroupBy(a => a.FamilyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AtomRow>)g.OrderBy(a => a.Tier).ToList(), StringComparer.Ordinal);
    }

    /// <summary>(entry id, shape, granted families) for every shipped Strain and Splice, off the raw
    /// authoring fields — see the class doc for why not through the Core parser's model.</summary>
    static IReadOnlyList<(string Id, string Shape, IReadOnlyList<string> Grants)> Combinations()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "combinations");
        var result = new List<(string, string, IReadOnlyList<string>)>();
        foreach (var file in new[] { "splices.json", "strains.json" })
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString()! : "(unnamed)";
                var shape = e.TryGetProperty("shape", out var sEl) ? sEl.GetString()! : "";
                var grants = e.TryGetProperty("grants", out var gEl) && gEl.ValueKind == JsonValueKind.Array
                    ? gEl.EnumerateArray().Select(x => x.GetString()!).ToList()
                    : new List<string>();
                result.Add((id, shape, grants));
            }
        }
        return result;
    }

    /// <summary>The shipped channel-pool catalog (`gk-data/packs/fusion/data/seed/channel-pools/pools.v1.json`) by id — what
    /// `CostFunction.Price` needs to price a <c>channel: {pool: …}</c> atom as
    /// <c>count × weighted_mean(price(member))</c>. Without it a pooled atom is Unpriced, which is how
    /// the first cut of this measurement priced only 36 of the 82 combinations.</summary>
    static IReadOnlyDictionary<string, ChannelPoolRow> PoolsById()
    {
        var path = Path.Combine(RepoRoot(), "data", "seed", "channel-pools", "pools.v1.json");
        var read = ChannelPoolFile.TryParse(File.ReadAllText(path), out var pools);
        Assert.True(read.IsOk, $"the channel-pool catalog did not parse: {read}");
        return pools.GroupBy(p => p.PoolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    [Fact]
    public void No_shipped_combination_grant_set_outruns_the_almanac_ceiling()
    {
        var ceilings = RarityPowerCeilings.Build(Ladder(), ShareOf, PowerTables.Authored());
        var almanac = ceilings.CeilingFor("almanac");
        Assert.True(almanac is { } ceiling && ceiling > 0,
            "the almanac rung has no seeded power ceiling — this watch is unanswerable without one");

        var atoms = AtomsByFamily();
        var entries = Combinations();
        Assert.NotEmpty(entries);
        var pools = PoolsById();
        ChannelPoolRow? PoolOf(string id) => pools.TryGetValue(id, out var p) ? p : null;

        var tuning = new ItemPowerTuning(
            ImplicitShareCapMilli: 150, GrantedActionShareCapMilli: null,
            ShowPowerOnCard: true, PowerDisplaySigFigs: 2, PowerDisplayBandPercent: 25);
        var tables = PowerTables.Authored();

        // ── The reading is taken at the tier the RUNTIME grants, not at the family's worst tier ──
        // SSH7.5/7.6 (spec-tier-ladder §3) moved the tier a shape grants to TUNING and re-emitted the
        // corpus with no tier number, so the shipped corpus authors NO `grantedTier` and
        // `StrainSpliceTuning` owns it: baseTier[shape] + ladder[rung].grantDelta + attuned bonus.
        // ITEM-grantedtier-1 deleted the vestigial entry member. So the number this watch quotes is the
        // number a real fill produces; the worst-tier sum is kept BESIDE it as the upper bound.
        var sockets = SocketTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));
        var strainSplice = StrainSpliceTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice)), sockets);
        var readings = strainSplice.TierLadder
            .SelectMany(rung => new[] { false, true }.Select(attuned => (
                rung.Rung, Attuned: attuned,
                Label: $"rung {rung.Rung}{(attuned ? "+attuned" : "")}")))
            .ToList();
        var maxByReading = readings.ToDictionary(r => r.Label, _ => 0L, StringComparer.Ordinal);
        var idByReading = readings.ToDictionary(r => r.Label, _ => "", StringComparer.Ordinal);
        var overByReading = readings.ToDictionary(r => r.Label, _ => 0, StringComparer.Ordinal);

        long maxSetShareWorst = 0, maxAtomShare = 0;
        var maxSetIdWorst = ""; var maxAtomId = "";
        var unpriced = 0; var pricedEntries = 0; var overWorst = 0;
        var unpricedNoFamily = 0; var unpricedNoTier = 0; var grantSlots = 0;
        var noAtomAtGrantedTier = 0; var unpricedAtGrantedTier = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var missingFamilies = new SortedSet<string>(StringComparer.Ordinal);
        var over = new List<string>();

        foreach (var (id, shapeText, grants) in entries)
        {
            grantSlots += grants.Count;
            if (!ComboShapes.TryParse(shapeText, out var shape))
            {
                over.Add($"{id} → shape '{shapeText}' is not a Strain or Splice — no granted tier to price at");
                continue;
            }

            // The worst-tier bound: the family's highest-priced atom, whatever tier it sits on.
            long worstTotal = 0; var worstPriced = true;
            foreach (var family in grants)
            {
                if (!atoms.TryGetValue(family, out var tiers))
                {
                    unpriced++; unpricedNoFamily++; missingFamilies.Add(family); worstPriced = false; continue;
                }
                // ⛔ The sentinel matters: a `best = -1` "nothing priced" marker CANNOT be told from a
                // negative price, and a drawback atom prices negative — which is why the first cut of
                // this loop reported 17 slots as "no tier priced" while recording ZERO verdict reasons
                // (every price was Ok). An explicit flag is the only honest way to say "nothing priced".
                long best = 0; var anyTierPriced = false;
                foreach (var atom in tiers)
                {
                    var p = CostFunction.Price(atom, tables, lookupPool: PoolOf);
                    if (!p.Ok)
                    {
                        // The REASON, not a guess: the first cut of this measurement asserted in prose
                        // that the unpriced atoms were pooled ones whose pool id the catalog lacks, and
                        // that was wrong — every pool id a grant references IS in the catalog, and the
                        // 17 slots were this loop's own sentinel. The verdict's own words are what may
                        // be quoted, so the RAW string is counted.
                        var key = p.Verdict.Reason;
                        if (key.Length == 0) key = "(empty reason)";
                        reasons[key] = reasons.GetValueOrDefault(key) + 1;
                        continue;
                    }
                    if (!anyTierPriced || p.Power.Total > best) best = p.Power.Total;
                    anyTierPriced = true;
                    var s = checked(p.Power.Total * 1000L) / almanac!.Value;
                    if (s > maxAtomShare) { maxAtomShare = s; maxAtomId = $"{id}/{family}.t{atom.Tier}"; }
                }
                if (!anyTierPriced) { unpriced++; unpricedNoTier++; worstPriced = false; continue; }
                worstTotal += best;
            }

            if (worstPriced)
            {
                pricedEntries++;
                var share = checked(worstTotal * 1000L) / almanac!.Value;
                if (share > maxSetShareWorst) { maxSetShareWorst = share; maxSetIdWorst = id; }
                if (share > tuning.ImplicitShareCapMilli) { overWorst++; over.Add($"{id} → {share}‰ (worst tier)"); }
            }

            // The real reading: one sum per (rung, attuned) pair the shipped ladder can reach.
            foreach (var reading in readings)
            {
                var tier = strainSplice.GrantedTier(shape, reading.Rung, sockets, reading.Attuned);
                long total = 0; var priced = true; var badSlot = "";
                foreach (var family in grants)
                {
                    if (!atoms.TryGetValue(family, out var tiers)) { priced = false; badSlot = $"{family} (no family)"; continue; }
                    var atom = tiers.FirstOrDefault(a => a.Tier == tier);
                    if (atom is null) { noAtomAtGrantedTier++; priced = false; badSlot = $"{family} (no atom at t{tier})"; continue; }
                    var p = CostFunction.Price(atom, tables, lookupPool: PoolOf);
                    if (!p.Ok) { unpricedAtGrantedTier++; priced = false; badSlot = $"{family} ({p.Verdict.Reason})"; continue; }
                    total += p.Power.Total;
                }
                if (!priced)
                {
                    over.Add($"{id} → unpriced at {reading.Label} (t{tier}): {badSlot}");
                    continue;
                }
                var setShare = checked(total * 1000L) / almanac!.Value;
                if (setShare > maxByReading[reading.Label]) { maxByReading[reading.Label] = setShare; idByReading[reading.Label] = id; }
                if (setShare > tuning.ImplicitShareCapMilli)
                {
                    overByReading[reading.Label]++;
                    over.Add($"{id} → {setShare}‰ at {reading.Label} (t{tier})");
                }
            }
        }

        _out.WriteLine($"almanac ceiling: {almanac} · combinations: {entries.Count} "
            + $"({pricedEntries} fully priced at some tier, {unpriced} unpriced grant slots) · the corpus "
            + "authors no grantedTier (SSH7.5/7.6) — the tier comes from tuning");
        _out.WriteLine($"granted tiers the shipped ladder reaches: "
            + string.Join(", ", readings.Select(r => $"{r.Label} = t{strainSplice.GrantedTier(
                ComboShape.Splice, r.Rung, sockets, r.Attuned)}")));
        foreach (var reading in readings)
            _out.WriteLine($"  at {reading.Label}: max grant-SET share {maxByReading[reading.Label]}‰ "
                + $"({idByReading[reading.Label]}) · over the {tuning.ImplicitShareCapMilli}‰ cap: "
                + $"{overByReading[reading.Label]} of {pricedEntries}");
        _out.WriteLine($"  WORST-tier BOUND (kept beside it): max {maxSetShareWorst}‰ ({maxSetIdWorst}) · "
            + $"over the cap: {overWorst} of {pricedEntries} · max single-atom share: {maxAtomShare}‰ ({maxAtomId})");
        foreach (var kv in reasons.OrderByDescending(r => r.Value).Take(4)) _out.WriteLine($"  unpriced: {kv.Value}× {kv.Key}");
        _out.WriteLine($"  unpriced slots: {unpriced} of {grantSlots} — no-family {unpricedNoFamily}, "
            + $"no-tier-priced {unpricedNoTier}, distinct reasons {reasons.Count}; at the granted tier: "
            + $"no-atom {noAtomAtGrantedTier}, unpriced {unpricedAtGrantedTier}");
        foreach (var line in over.OrderByDescending(l => l.Length).Take(8)) _out.WriteLine("  " + line);

        // ⛔ This test does NOT assert that the cap holds — it does not hold, and asserting "no set is
        // over the cap" would pin a defect as a contract: it would fail the moment the bands are fixed,
        // which is the normal case and the very thing the watch asks for. So the measurement REPORTS and
        // the verdict lives in tasks/item-todo.md's own row (the chaff-chassis watch + its consequence
        // row against module 7's bands), which is where a design question belongs. What IS asserted is
        // that the measurement is non-vacuous — a watch that silently priced nothing would be the
        // failure mode this repo has paid for before.
        Assert.True(pricedEntries > 0, "no combination's grants could be priced — the measurement proves nothing");
        Assert.True(maxSetShareWorst > 0, "no grant set produced a share — the measurement proves nothing");
        Assert.True(maxByReading.Values.Max() > 0,
            "no grant set priced at the tier the tuning actually grants — the real reading is empty");
    }
}
