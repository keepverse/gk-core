using System.Text.Json;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Battle;

/// <summary>
/// content-authoring (T2.3, spec-content-authoring.md §2.2): <c>ContentIndex</c> is Θ_content —
/// same values as the old <c>RecommendedLevel</c> name (1/3/6/10), a vocabulary rename only. No
/// external reader referenced the old name (verified: zero hits for "RecommendedLevel" anywhere else
/// in the repo), so this is a full rename, not an alias — unlike <see cref="BattleActorSetup.Level"/>,
/// nothing serializes <c>WaveDef</c> itself.
/// </summary>
/// <summary>
/// <see cref="Profile"/> is a battle-timeline mode-profile id (B12, battle-timeline-map.md §"Decision
/// 4"), resolved via <c>BattleModeProfileCatalog.Resolve</c> — <c>null</c> means "content did not
/// choose," which resolves to <c>classic-round</c>. Deliberately optional-with-default rather than a
/// required 5th positional argument, so the four waves already authored below need no edit and this
/// stays additive. **Never reaches <c>BattleSetup</c>** — the profile is looked up from the existing
/// <c>WaveId</c> at resolve time, never serialized (a field on `BattleSetup` would move all four
/// expedition hashes; named a "Never" in both `battle-timeline-map.md` and `spec-mode-profiles.md`).
/// </summary>
/// <param name="W">battle-timeline T15/B33 — a per-wave override of the resolved profile's
/// concurrency width, from the owner decision of 2026-09-04 ("`W` is content-configurable per wave").
/// <c>null</c> means "this wave does not care", which is every wave shipped today: the mechanism lands
/// inert on purpose, so the profile migration's own delta stays attributable to the profile switch
/// alone. Authoring a strictly-serialized encounter (<c>W = 1</c> on a wide profile) is content work.
///
/// <para>Optional-with-default, exactly like <paramref name="Profile"/>, so the four authored rows
/// below need no edit. And like <paramref name="Profile"/> it **never reaches <c>BattleSetup</c>** —
/// a field there would move all four expedition hashes for no gameplay reason, which both
/// <c>battle-timeline-map.md</c> and <c>spec-mode-profiles.md</c> name as a "Never".</para></param>
public sealed record WaveDef(string WaveId, string Name, int ContentIndex, IReadOnlyList<BattleActorSetup> Enemies, string? Profile = null, int? W = null);

/// <summary>One wave pick (species-gear-chain T6): a rarity WINDOW over admitted species, drawn
/// without replacement under the same-species cap. Weights are RELATIVE rung weights (a bounded
/// ratio, exempt from the magnitude rules and saying so here) — the long-tail mechanism; flat
/// today, shaped by a balance pass tomorrow, in the file rather than a rebuild.</summary>
public sealed record WavePick(
    List<CreatureSpeciesDef> Pool, int Count, IReadOnlyDictionary<string, int> Weights, long SameSpeciesMaxMilli);

/// <summary>
/// Code-authored wave roster built over the generated creature species catalog — enemies are wild
/// creatures wearing the same species the player collects. Deterministic: same catalog ⇒ same waves.
/// </summary>
public static class WaveCatalog
{
    // Lazy, not `static readonly ... = Build()` (T4.7, catalog-runtime §3a): first touch must happen
    // after CreatureSpeciesCatalog.Configure runs, not at an unpredictable point tied to class-load
    // order. Behaviour-preserving today — the source is still the compiled roster either way.
    //
    // base-defense siege-waves §3.5 (task 12.4, 2026-09-06): production now calls Configure with the
    // roster parsed from gk-core/data/tuning/waves.v1.json (WaveCatalogLoader.Parse), the same
    // Loader.Parse(File.ReadAllText(...)) -> Xyz.Configure(...) shape already used for
    // SiegeTuningPolicy/BattleResourceTuningLoader/etc. — never a second hand-written array beside
    // this one. `All`'s own lazy fallback to the compiled `Build()` roster is UNCHANGED and stays the
    // default for any caller that never configures (every test written before this task, and any
    // future one that only needs a deterministic roster, not the authored data file specifically) —
    // so this migration moves the CONTENT out of code without moving the ACCESS CONTRACT.
    static IReadOnlyList<WaveDef>? _all;
    public static IReadOnlyList<WaveDef> All => _all ??= Build();

    /// <summary>Overrides the roster with a data-sourced one (normally `WaveCatalogLoader.Parse`'s
    /// result). Throws on an empty snapshot — a wave-less roster is a load error, not a valid content
    /// state, matching `CreatureSpeciesCatalog.Configure`'s own precedent for the same failure shape.</summary>
    public static void Configure(IReadOnlyList<WaveDef> snapshot)
    {
        if (snapshot is null || snapshot.Count == 0)
            throw new ArgumentException(
                "WaveCatalog.Configure received an empty wave roster — data/tuning/waves.v1.json is " +
                "missing, empty, or failed to parse.", nameof(snapshot));
        _all = snapshot;
    }

    /// <summary>Test-only escape hatch back to the compiled roster — mirrors
    /// `CreatureSpeciesCatalog.ConfigureFromCompiledDefault`'s own name and role.</summary>
    public static void ConfigureFromCompiledDefault() => _all = Build();

    public static WaveDef Get(string waveId) =>
        All.FirstOrDefault(w => string.Equals(w.WaveId, waveId, StringComparison.Ordinal))
        ?? throw new ArgumentException($"Unknown wave id '{waveId}'.");

    /// <summary>
    /// T15/B33 — the profile a wave actually runs under: <c>BattleModeProfileCatalog.Resolve</c> for
    /// the row's <see cref="WaveDef.Profile"/>, then the row's own <see cref="WaveDef.W"/> applied on
    /// top if it set one.
    ///
    /// <para>When the wave sets no <c>W</c> — every wave today — this returns the catalog's <b>cached
    /// instance itself</b>, not a copy. That is deliberate and load-bearing: it keeps reference
    /// identity for callers that compare with <c>Assert.Same</c>, and it makes "wave did not override"
    /// byte-identical to "no per-wave W mechanism exists" rather than merely equal to it.</para>
    /// </summary>
    /// <summary>
    /// T6/B21 — an expedition may never run an interactive profile. **An assertion, not a
    /// convention**: an expedition resolves server-side with nobody watching, so an interactive
    /// profile could only ever time out every turn — a slow way to produce a worse auto-resolve. It
    /// fails loudly instead of degrading quietly.
    /// </summary>
    public static Timeline.BattleModeProfile ProfileForExpedition(string waveId)
    {
        var profile = ProfileFor(waveId);
        if (profile.RequiresLiveInput)
            throw new InvalidOperationException(
                $"wave '{waveId}' selects the interactive profile '{profile.ProfileId}', but an expedition " +
                "resolves with no player present — an interactive profile there would time out every turn. " +
                "Expeditions are barred from interactive profiles by assertion (spec-interactive-turns.md §5).");
        return profile;
    }

    public static Timeline.BattleModeProfile ProfileFor(string waveId)
    {
        var wave = Get(waveId);
        var profile = Timeline.BattleModeProfileCatalog.Resolve(wave.Profile);
        if (wave.W is not { } w) return profile;
        if (w <= 0)
            throw new ArgumentOutOfRangeException(nameof(waveId), w,
                $"wave '{waveId}' sets W = {w}; a concurrency width is a slot count and must be > 0.");
        return profile with { W = w };
    }

    public static bool IsKnown(string? waveId) =>
        waveId != null && All.Any(w => string.Equals(w.WaveId, waveId, StringComparison.Ordinal));

    /// <summary>Widened from `private` so `WaveCatalogLoaderTests` can compare the data-sourced
    /// roster against this one directly, without mutating the shared `_all`/`Configure` state a
    /// parallel test run could race on (siege-waves 12.4).</summary>
    internal static IReadOnlyList<WaveDef> Build()
    {
        // The compiled fallback mirrors gk-core/data/tuning/waves.v1.json's own four waves value for value
        // (counts, windows, weights, caps) — the file owns future changes, this array owns
        // file-less startup and every test that needs a deterministic roster without configuring
        // one. WaveCatalogLoaderTests proves the two agree enemy-for-enemy; widening a window is a
        // file edit first, mirrored here second, never code-only.
        return new[]
        {
            // T15/B36 — expeditions and web matches run on `hybrid-atb` (decisions.md, "Battle engine
            // open questions (2026-09-04)", item 1): W=4, FixedIncrement, EarlyBoundWithFallback,
            // ActionPoints(2). Both surfaces move together because they share this roster. This is
            // what makes `turn.speed` / `turn.haste` live in production for the first time.
            //
            // No `RulesetVersion` bump accompanies it, and that was MEASURED, not assumed (B35): the
            // golden fixtures use "golden-*" wave ids that are not in this roster, and the expedition
            // tier hash covers the expedition *plan*, not resolved battle reports. The joint 4 -> 5
            // re-bless this task once predicted -- shared with B26's scaled clock -- has no remaining
            // cause: B26 is injector-side and Core cannot observe it either.
            new WaveDef("rift-skirmish", "Rift Skirmish", 1, Enemies(theta: 1, "rift-skirmish",
                new WavePick(Band(CreatureRarity.Chaff, CreatureRarity.Fused), 4, FlatWeights(CreatureRarity.Chaff, CreatureRarity.Fused), 0)), Profile: Timeline.BattleModeProfileCatalog.HybridAtbId),
            new WaveDef("rift-warband", "Rift Warband", 3, Enemies(theta: 3, "rift-warband",
                new WavePick(Band(CreatureRarity.Chaff, CreatureRarity.Fused), 4, FlatWeights(CreatureRarity.Chaff, CreatureRarity.Fused), 0),
                new WavePick(Band(CreatureRarity.Cultivated, CreatureRarity.Heirloom), 2, FlatWeights(CreatureRarity.Cultivated, CreatureRarity.Heirloom), 0)), Profile: Timeline.BattleModeProfileCatalog.HybridAtbId),
            new WaveDef("rift-onslaught", "Rift Onslaught", 6, Enemies(theta: 6, "rift-onslaught",
                new WavePick(Band(CreatureRarity.Chaff, CreatureRarity.Fused), 3, FlatWeights(CreatureRarity.Chaff, CreatureRarity.Fused), 0),
                new WavePick(Band(CreatureRarity.Cultivated, CreatureRarity.Heirloom), 3, FlatWeights(CreatureRarity.Cultivated, CreatureRarity.Heirloom), 0),
                new WavePick(Band(CreatureRarity.Heirloom, CreatureRarity.Almanac), 1, FlatWeights(CreatureRarity.Heirloom, CreatureRarity.Almanac), 0)), Profile: Timeline.BattleModeProfileCatalog.HybridAtbId),
            new WaveDef("rift-tyrant", "Rift Tyrant", 10, Enemies(theta: 10, "rift-tyrant",
                new WavePick(Band(CreatureRarity.Cultivated, CreatureRarity.Heirloom), 3, FlatWeights(CreatureRarity.Cultivated, CreatureRarity.Heirloom), 0),
                new WavePick(Band(CreatureRarity.Heirloom, CreatureRarity.Almanac), 2, FlatWeights(CreatureRarity.Heirloom, CreatureRarity.Almanac), 0),
                new WavePick(Band(CreatureRarity.Sunwoven, CreatureRarity.Almanac), 1, FlatWeights(CreatureRarity.Sunwoven, CreatureRarity.Almanac), 0)), Profile: Timeline.BattleModeProfileCatalog.HybridAtbId)
        };
    }

    /// <summary>The pool for one pick: a rarity WINDOW (not a single rung), filtered by admission.
    /// The filter is the point — an EventOnly species must never march in a wave, and today it is kept
    /// out only by alphabetical luck. `OrderBy` stays: it stops being the selection rule and becomes
    /// the determinism substrate — the seeded draw walks a stable order. `source` is the test seam:
    /// production passes nothing (the live catalog); tests inject synthetic rosters without mutating
    /// shared global state a parallel run could race on.</summary>
    internal static List<CreatureSpeciesDef> Band(
        CreatureRarity from, CreatureRarity to, IEnumerable<CreatureSpeciesDef>? source = null) =>
        (source ?? CreatureSpeciesCatalog.All)
            .Where(s => CreatureRarityLadder.AtLeast(s.BaseRarity, from)
                     && CreatureRarityLadder.AtMost(s.BaseRarity, to))
            .Where(CreatureAdmission.ForWave)
            // spec-species-rank.md §6: the rank floor lands BESIDE the window and the admission rule,
            // never replacing either — it can only narrow. It deliberately adds NO acquisition rule of
            // its own: whatever `ForWave` admits today is what this band admits, and a species with no
            // rank maps to the bottom rung AT THIS GATE, so the ladder never sees a null.
            .Where(s => CreatureRankFloors.Passes(CreatureRankFloors.WaveBand, s.Rank))
            .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
            .ToList();

    /// <summary>Flat starting weights for a window — every in-window rung 100. The neutral point the
    /// long tail shapes later; the file carries the real table, this carries the compiled mirror.</summary>
    internal static IReadOnlyDictionary<string, int> FlatWeights(CreatureRarity from, CreatureRarity to)
    {
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rung in CreatureRarityLadder.RungsBetween(from, to))
            weights[rung.ToId()] = 100;
        return weights;
    }

    /// <summary>FNV-1a 64-bit over the wave id — the deterministic seed both the compiled roster and
    /// the data-sourced loader derive identically, so the two agree enemy-for-enemy. Never
    /// <c>string.GetHashCode</c>, which is randomised per process. The offset basis and prime are the
    /// algorithm's definition (structural, not tunable — same standing as
    /// <c>FamilyExpansion.TierCount</c>).</summary>
    const ulong FnvOffsetBasis = 14695981039346656037UL;
    const ulong FnvPrime = 1099511628211UL;

    internal static ulong StableSeed(string waveId)
    {
        unchecked
        {
            var h = FnvOffsetBasis;
            foreach (var ch in waveId)
            {
                h ^= (ulong)ch;
                h *= FnvPrime;
            }
            return h;
        }
    }

    /// <summary>Widened from `private` for `WaveCatalogLoader`'s own use (siege-waves 12.4). Seeded,
    /// weighted, without replacement under the pick's same-species cap — mirroring Delve's
    /// `SlotFill.Draw` shape (named per-draw streams, cap-and-skip instead of retry loops). A pick
    /// whose window admits nothing at all contributes nothing rather than throwing: the corpus
    /// grows and shrinks, and a wave must never crash the game — the occupancy report (a reading,
    /// printed, never asserted) is what surfaces the gap. A pick that names MORE draws than its
    /// window holds distinct species IS a content error and throws, naming the wave and pick.</summary>
    internal static IReadOnlyList<BattleActorSetup> Enemies(int theta, string waveId, params WavePick[] picks)
    {
        var seed = StableSeed(waveId);
        var list = new List<BattleActorSetup>();
        var n = 0;
        for (var k = 0; k < picks.Length; k++)
        {
            var pick = picks[k];
            // Re-sort at the draw site too: the pool arrives sorted from Band, but a hand-built pool
            // (or a future caller) must not be able to smuggle platform hash order into the seed.
            var pool = pick.Pool.OrderBy(s => s.SpeciesId, StringComparer.Ordinal).ToList();
            if (pool.Count == 0) continue;

            // ceil(count * milli / 1000), long widened before the multiply, divided once — Delve's
            // own cap shape. A 0 cap means strict without-replacement (every species at most one
            // seat), which is the shipped behaviour this preserves.
            var cap = (int)((checked((long)pick.Count * pick.SameSpeciesMaxMilli) + (1000 - 1)) / 1000);
            if (cap < 1) cap = 1;

            var byRung = pool.GroupBy(s => s.BaseRarity).ToDictionary(g => g.Key, g => g.ToList());
            var seats = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < pick.Count; j++)
            {
                var species = DrawOne(byRung, pick, seats, cap, seed, waveId, k, j);
                if (species is null)
                    throw new WaveCatalogRejection(
                        $"wave '{waveId}' pick {k}: {pick.Count} draws need more distinct admissible species " +
                        "than the window holds — widen the window or lower the count, never repeat silently");
                seats[species.SpeciesId] = seats.GetValueOrDefault(species.SpeciesId) + 1;
                list.Add(new BattleActorSetup
                {
                    Key = $"wave:{n++}",
                    Side = "wave",
                    SpeciesId = species.SpeciesId,
                    TypeId = species.CreatureTypeId,
                    Level = theta,
                    ElementPrimary = species.ElementPrimary,
                    ElementSecondary = species.ElementSecondary,
                    TraitIds = species.TraitPool,
                    MaxHp = BattleRuleset.BaseHp(theta),
                    Atk = BattleRuleset.BaseAtk(theta),
                    Defense = BattleRuleset.BaseDefense(theta),
                    AttackIntervalMs = species.AttackIntervalMs
                });
            }
        }

        return list;
    }

    /// <summary>One draw: rung ∝ weights among rungs holding a drawable species (empty rungs drop
    /// out, weights renormalise — a window covering a zero-occupant rung does not throw), then a
    /// uniform species within the rung. Null when no rung holds anything drawable.</summary>
    internal static CreatureSpeciesDef? DrawOne(
        IReadOnlyDictionary<CreatureRarity, List<CreatureSpeciesDef>> byRung,
        WavePick pick,
        IReadOnlyDictionary<string, int> seats,
        int cap,
        ulong seed,
        string waveId,
        int pickIndex,
        int drawIndex)
    {
        var scope = $"wave:{waveId}:pick:{pickIndex}:draw:{drawIndex}";
        var drawRng = SeededRng.DeriveStream(seed, scope);

        var rungOptions = new List<Actions.Seeding.WeightedOption<CreatureRarity>>();
        foreach (var r in byRung.Keys.OrderBy(x => x))
        {
            if (!pick.Weights.TryGetValue(r.ToId(), out var weight) || weight <= 0)
                continue;
            if (!byRung[r].Any(s => seats.GetValueOrDefault(s.SpeciesId) < cap))
                continue;
            rungOptions.Add(new Actions.Seeding.WeightedOption<CreatureRarity>(r, weight));
        }
        if (rungOptions.Count == 0) return null;

        var rung = Actions.Seeding.WeightedChoice.Pick(
            rungOptions, unchecked((long)drawRng.NextULong()), scope + ":rung");
        var candidates = byRung[rung]
            .Where(s => seats.GetValueOrDefault(s.SpeciesId) < cap)
            .Select(s => new Actions.Seeding.WeightedOption<CreatureSpeciesDef>(s, 1))
            .ToList();
        return Actions.Seeding.WeightedChoice.Pick(
            candidates, unchecked((long)drawRng.NextULong()), scope + ":species");
    }
}

public sealed class WaveCatalogRejection : Exception
{
    public WaveCatalogRejection(string message) : base(message) { }
}

/// <summary>
/// base-defense `siege-waves` §3.5 (task 12.4): parses `gk-core/data/tuning/waves.v1.json` into the same
/// `WaveDef` shape `WaveCatalog.Build()`'s own compiled array produces — the migration moves WHICH
/// waves exist and their rarity-band picks into data; the species-selection RULE itself
/// (`WaveCatalog.Band`/`Enemies`, ordered-by-id, deterministic) stays in code, reused verbatim rather
/// than re-derived, so "same catalog ⇒ same waves" holds exactly as it did before this task.
/// </summary>
public static class WaveCatalogLoader
{
    public static IReadOnlyList<WaveDef> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new WaveCatalogRejection("wave roster: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new WaveCatalogRejection($"wave roster: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("waves", out var wavesEl) || wavesEl.ValueKind != JsonValueKind.Array)
                throw new WaveCatalogRejection("wave roster: missing or non-array 'waves' property");

            var result = new List<WaveDef>();
            foreach (var w in wavesEl.EnumerateArray())
            {
                var waveId = RequireString(w, "waveId");
                var name = RequireString(w, "name");
                var contentIndex = RequireInt(w, "contentIndex");
                var profile = w.TryGetProperty("profile", out var profileEl) && profileEl.ValueKind == JsonValueKind.String
                    ? profileEl.GetString()
                    : null;
                var width = w.TryGetProperty("w", out var wEl) && wEl.ValueKind == JsonValueKind.Number
                    ? wEl.GetInt32()
                    : (int?)null;

                if (!w.TryGetProperty("picks", out var picksEl) || picksEl.ValueKind != JsonValueKind.Array || picksEl.GetArrayLength() == 0)
                    throw new WaveCatalogRejection($"wave '{waveId}': missing or empty 'picks' array");

                var picks = new List<WavePick>();
                foreach (var p in picksEl.EnumerateArray())
                {
                    var fromText = RequireString(p, "rarityFrom", waveId);
                    var toText = RequireString(p, "rarityTo", waveId);
                    if (!CreatureRarityIds.TryParse(fromText, out var from))
                        throw new WaveCatalogRejection($"wave '{waveId}': unknown rarityFrom '{fromText}'");
                    if (!CreatureRarityIds.TryParse(toText, out var to))
                        throw new WaveCatalogRejection($"wave '{waveId}': unknown rarityTo '{toText}'");
                    if (CreatureRarityLadder.RungsBetween(from, to).Count == 0)
                        throw new WaveCatalogRejection(
                            $"wave '{waveId}': rarity window [{fromText}..{toText}] is inverted");                    var count = RequireInt(p, "count", waveId);
                    if (count <= 0)
                        throw new WaveCatalogRejection($"wave '{waveId}': pick count must be > 0, got {count}");
                    var weights = RequireWeights(p, waveId, from, to);
                    var cap = RequireMilli(p, "sameSpeciesMaxMilli", waveId);
                    picks.Add(new WavePick(WaveCatalog.Band(from, to), count, weights, cap));
                }

                result.Add(new WaveDef(waveId, name, contentIndex, WaveCatalog.Enemies(contentIndex, waveId, picks.ToArray()), profile, width));
            }

            return result;
        }
    }

    static string RequireString(JsonElement el, string prop, string? waveId = null) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new WaveCatalogRejection($"wave roster{(waveId is null ? "" : $" '{waveId}'")}: missing or empty '{prop}'");

    static int RequireInt(JsonElement el, string prop, string? waveId = null) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : throw new WaveCatalogRejection($"wave roster{(waveId is null ? "" : $" '{waveId}'")}: missing or non-numeric '{prop}'");

    static long RequireMilli(JsonElement el, string prop, string waveId)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
            throw new WaveCatalogRejection($"wave '{waveId}': missing or non-numeric '{prop}'");
        var milli = v.GetInt64();
        if (milli < 0 || milli > 1000)
            throw new WaveCatalogRejection($"wave '{waveId}': '{prop}' must be a per-mille share in [0..1000], got {milli}");
        return milli;
    }

    /// <summary>Relative rung weights for a window (species-gear-chain T6): every rung in the window
    /// is required (no silent zero), unknown rungs and non-numeric weights throw, and an all-zero
    /// table throws — a pick with nothing drawable is an authoring error, not a quiet skip. Rungs
    /// with zero OCCUPANTS are fine (the draw renormalises over the rest); rungs with zero WEIGHT
    /// are muted on purpose.</summary>
    static IReadOnlyDictionary<string, int> RequireWeights(JsonElement el, string waveId, CreatureRarity from, CreatureRarity to)
    {
        if (!el.TryGetProperty("weights", out var w) || w.ValueKind != JsonValueKind.Object)
            throw new WaveCatalogRejection($"wave '{waveId}': missing or non-object 'weights'");
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rung in CreatureRarityLadder.RungsBetween(from, to))
        {
            var id = rung.ToId();
            if (!w.TryGetProperty(id, out var wv) || wv.ValueKind != JsonValueKind.Number)
                throw new WaveCatalogRejection($"wave '{waveId}': weights has no entry for in-window rung '{id}'");
            var weight = wv.GetInt32();
            if (weight < 0)
                throw new WaveCatalogRejection($"wave '{waveId}': weights['{id}'] is negative ({weight})");
            weights[id] = weight;
        }
        foreach (var name in w.EnumerateObject().Select(p => p.Name))
            if (!weights.ContainsKey(name))
                throw new WaveCatalogRejection($"wave '{waveId}': weights names unknown rung '{name}'");
        if (weights.Values.All(v => v == 0))
            throw new WaveCatalogRejection($"wave '{waveId}': weights are all zero — nothing is drawable");
        return weights;
    }
}
