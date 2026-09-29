using System.Text.Json;
using FusionRpg.Core.Combat.Element;

namespace FusionRpg.Core.Creatures.Fusion;

/// <summary>One cross-species fusion recipe: two band-below inputs mint the output species.
/// `CrossRungGapFill` is true only for a `fusion-recipe-generator` (module 17) gap-fill recipe
/// whose two inputs are NOT both at the single nearest-populated rung below the output —
/// decorative on every deterministic recipe (always false there), meaningful once module 18's
/// committed seed loads a real gap-fill entry.</summary>
public sealed record CreatureRecipeDef(
    string RecipeId, string OutputSpeciesId, string InputSpeciesIdA, string InputSpeciesIdB,
    bool CrossRungGapFill = false);

/// <summary>
/// `catalog-runtime`'s own seam pattern applied to recipes (`ds 18` `fusion-recipe-runtime`, T8.4,
/// spec-fusion-recipe-runtime.md §1-2, mirroring `SpeciesSnapshot.cs` exactly): <see cref="All"/>
/// reads a roster <see cref="Configure"/> or <see cref="UseScoped"/> loaded, never its own
/// computation. <see cref="BuildDeterministicOnly"/> — code-built from the species catalog
/// (WaveCatalog pattern: same catalog ⇒ same recipes; no capture data) — is no longer `All`'s
/// implementation, only `fusion-recipe-generator`'s (module 17) own dependency: the deterministic
/// pass every real committed seed starts from. Input A shares the output's primary element where
/// the band allows; input B prefers the secondary-element donor, then a ring-related element, then
/// catalog order — and pairs are forced unique so an input pair identifies its recipe. Capture-only
/// species appear nowhere as inputs (spec-creature-fusion.md, owner lock 6, enforced again by
/// <see cref="Validate"/> for anything <see cref="Configure"/> loads from a file, not just what
/// <see cref="BuildDeterministicOnly"/> itself produces).
/// </summary>
public static class CreatureRecipeCatalog
{
    /// <summary>
    /// Species eligible to be fusion OUTPUTS. Was `&gt;= CreatureRarity.Rare` (three of the old four
    /// rungs). Rare's own migration target is `Cultivated` (the rare band's lowest rung —
    /// ssot-rarity.md §4.3's forward map), so this is the same translation: "recipes exist from the
    /// old Rare band's target rung upward" (spec-rarity-migration.md §3's ordinal-arithmetic fix).
    /// A plain `const` — no field-declaration-order hazard to guard against any more now that
    /// nothing in this class is a `static readonly X = ...` evaluated at class-load time (T8.4).
    /// </summary>
    public const CreatureRarity OutputEligibilityFloor = CreatureRarity.Cultivated;

    static IReadOnlyList<CreatureRecipeDef>? _configured;
    static readonly AsyncLocal<IReadOnlyList<CreatureRecipeDef>?> Scoped = new();

    /// <summary>Non-throwing check, mirroring `CreatureSpeciesCatalog.IsConfigured` — for a caller
    /// that must treat the recipe list as an optional enrichment rather than a hard requirement.</summary>
    public static bool IsConfigured => Scoped.Value != null || _configured != null;

    /// <summary>
    /// Process-wide. What a host calls once, after reading the committed seed
    /// (<see cref="FusionRecipeSeedReader.Parse"/> over `gk-data/packs/fusion/data/generated/creatures/_fusion-recipes.json`)
    /// — call AFTER <c>CreatureSpeciesCatalog.Configure</c> so <see cref="Validate"/> can cross-check
    /// every input/output id against the real roster, not just this list's own internal shape.
    /// </summary>
    /// <exception cref="InvalidOperationException">The recipe list is empty (mirrors
    /// `SpeciesSnapshot.Configure`'s own "a server that starts empty reports healthy and fails
    /// later, untraceably" rule) or fails <see cref="Validate"/>.</exception>
    public static void Configure(IReadOnlyList<CreatureRecipeDef> recipes)
    {
        if (recipes is null) throw new ArgumentNullException(nameof(recipes));
        if (recipes.Count == 0)
            throw new InvalidOperationException(
                "CreatureRecipeCatalog.Configure received an empty recipe list. A server that starts " +
                "with zero fusion recipes reports healthy and fails later, untraceably, at the first " +
                "player's fusion attempt. Run 'python tools/seedsmith/seedsmith/adapters/creatures/fusion/" +
                "reconcile.py' against the data directory this host points at, or point at one that " +
                "already has a committed data/generated/creatures/_fusion-recipes.json.");

        _configured = Validate(recipes);
        _byId = null;
        _byPair = null;
    }

    /// <summary>Swap the recipe list for THIS async context only, restored on dispose — the same
    /// isolation `CreatureSpeciesCatalog.UseScoped` already gives the species roster, so one test's
    /// recipe set is never visible to a test running beside it under xUnit's default cross-class
    /// parallelism.</summary>
    public static IDisposable UseScoped(IReadOnlyList<CreatureRecipeDef> recipes)
    {
        if (recipes is null) throw new ArgumentNullException(nameof(recipes));
        var validated = Validate(recipes);
        var previous = Scoped.Value;
        Scoped.Value = validated;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly IReadOnlyList<CreatureRecipeDef>? _previous;
        public Restore(IReadOnlyList<CreatureRecipeDef>? previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }

    /// <summary>Reset the process-wide roster — test teardown only, mirroring
    /// `CreatureSpeciesCatalog.ResetToUnconfigured`'s own role (never called by a host).</summary>
    public static void ResetToUnconfigured() { _configured = null; _byId = null; _byPair = null; }

    /// <summary>The loaded recipe list — <see cref="Configure"/> or <see cref="UseScoped"/> must
    /// have run first, the same "no built-in default" discipline `CreatureSpeciesCatalog.All` already
    /// established. No consumer signature changed by this seam (T8.4's own acceptance criterion):
    /// every real call site (`FusionEndpoints.cs`, `RpgStore.Fusion.cs`) already reads this
    /// property and `TryMatch`/`Get`/`IsKnown` below exactly as it did before.</summary>
    public static IReadOnlyList<CreatureRecipeDef> All => Scoped.Value ?? _configured ?? throw new InvalidOperationException(
        "CreatureRecipeCatalog.Configure(...) has not run. Every host reads the recipe list " +
        "fusion-recipe-reconcile wrote to data/generated/creatures/_fusion-recipes.json and calls " +
        "Configure at startup — there is no built-in default to fall back to. Run " +
        "'python tools/seedsmith/seedsmith/adapters/creatures/fusion/reconcile.py' first.");

    /// <summary>Catalog discipline mirroring `CreatureSpeciesCatalog.Validate` — a bad recipe list is a
    /// startup error, never a runtime surprise. Cross-checks against the real species roster only
    /// when one is configured (`CreatureSpeciesCatalog.IsConfigured`) — this method has no ordering
    /// requirement of its own, but a real host always configures species first, so a real error
    /// there is always caught.</summary>
    public static IReadOnlyList<CreatureRecipeDef> Validate(IReadOnlyList<CreatureRecipeDef> recipes)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenOutputs = new HashSet<string>(StringComparer.Ordinal);
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in recipes)
        {
            if (string.IsNullOrWhiteSpace(r.RecipeId))
                throw new InvalidOperationException("Fusion recipe has an empty recipe id.");
            if (!seenIds.Add(r.RecipeId))
                throw new InvalidOperationException($"Duplicate fusion recipe id '{r.RecipeId}'.");
            if (!seenOutputs.Add(r.OutputSpeciesId))
                throw new InvalidOperationException($"Species '{r.OutputSpeciesId}' has more than one fusion recipe.");
            if (r.InputSpeciesIdA == r.InputSpeciesIdB)
                throw new InvalidOperationException($"Recipe '{r.RecipeId}' has the same species as both inputs.");
            if (!seenPairs.Add(PairKey(r.InputSpeciesIdA, r.InputSpeciesIdB)))
                throw new InvalidOperationException($"Input pair for recipe '{r.RecipeId}' is already claimed by another recipe.");

            if (CreatureSpeciesCatalog.IsConfigured)
            {
                if (!CreatureSpeciesCatalog.IsKnown(r.OutputSpeciesId))
                    throw new InvalidOperationException($"Recipe '{r.RecipeId}' outputs unknown species '{r.OutputSpeciesId}'.");
                if (!CreatureSpeciesCatalog.IsKnown(r.InputSpeciesIdA))
                    throw new InvalidOperationException($"Recipe '{r.RecipeId}' input A '{r.InputSpeciesIdA}' is not a known species.");
                if (!CreatureSpeciesCatalog.IsKnown(r.InputSpeciesIdB))
                    throw new InvalidOperationException($"Recipe '{r.RecipeId}' input B '{r.InputSpeciesIdB}' is not a known species.");
                if (CreatureSpeciesCatalog.Get(r.InputSpeciesIdA).Acquisition == CreatureAcquisition.CaptureOnly)
                    throw new InvalidOperationException($"Recipe '{r.RecipeId}' input A '{r.InputSpeciesIdA}' is CaptureOnly — owner lock 6 forbids a capture-only input.");
                if (CreatureSpeciesCatalog.Get(r.InputSpeciesIdB).Acquisition == CreatureAcquisition.CaptureOnly)
                    throw new InvalidOperationException($"Recipe '{r.RecipeId}' input B '{r.InputSpeciesIdB}' is CaptureOnly — owner lock 6 forbids a capture-only input.");
            }
        }
        return recipes;
    }

    // Mirrors `CreatureSpeciesCatalog.ByIdMap()` exactly: a scoped (test-only) roster is never cached
    // in the process-global fields below — caching it would leak one test's recipes into a call
    // from a DIFFERENT async context that happens to reuse the same thread.
    static Dictionary<string, CreatureRecipeDef>? _byId;
    static Dictionary<string, CreatureRecipeDef> ById()
    {
        if (Scoped.Value is { } scoped) return scoped.ToDictionary(r => r.RecipeId, StringComparer.Ordinal);
        return _byId ??= All.ToDictionary(r => r.RecipeId, StringComparer.Ordinal);
    }

    static Dictionary<string, CreatureRecipeDef>? _byPair;
    static Dictionary<string, CreatureRecipeDef> ByPair()
    {
        if (Scoped.Value is { } scoped) return scoped.ToDictionary(r => PairKey(r.InputSpeciesIdA, r.InputSpeciesIdB), StringComparer.Ordinal);
        return _byPair ??= All.ToDictionary(r => PairKey(r.InputSpeciesIdA, r.InputSpeciesIdB), StringComparer.Ordinal);
    }

    public static bool IsKnown(string? recipeId) => recipeId != null && ById().ContainsKey(recipeId);

    public static CreatureRecipeDef Get(string recipeId) =>
        ById().TryGetValue(recipeId, out var def)
            ? def
            : throw new ArgumentException($"Unknown fusion recipe id '{recipeId}'.");

    /// <summary>Orderless lookup by the two input species — null when no recipe matches.</summary>
    public static CreatureRecipeDef? TryMatch(string speciesA, string speciesB)
    {
        if (string.Equals(speciesA, speciesB, StringComparison.Ordinal)) return null;
        return ByPair().TryGetValue(PairKey(speciesA, speciesB), out var def) ? def : null;
    }

    /// <summary>
    /// `fusion-recipe-generator` (creature-seed module 17, spec §1 `distribution-index`) seam: exposes
    /// the exact pool `BuildDeterministicOnly()` itself draws inputs from, so an external capacity
    /// check can never silently disagree with the real search about which rung is "below" a given
    /// output — reused directly, never reimplemented as a second walk-down. Every element shares
    /// one `BaseRarity` (the rung `InputPoolBelow` settled on), so a caller reads `[0].BaseRarity`
    /// for "which rung", same as this class's own `BuildDeterministicOnly()` does implicitly.
    /// </summary>
    public static IReadOnlyList<CreatureSpeciesDef> NearestPopulatedRungBelow(CreatureRarity outputRarity) =>
        InputPoolBelow(outputRarity);

    static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + "+" + b : b + "+" + a;

    /// <summary>
    /// Every species eligible to be a fusion OUTPUT — the exact filter `BuildDeterministicOnly()`
    /// itself starts from, extracted so `UnresolvedOutputs()` (and `fusion-recipe-generator`'s own
    /// reconcile CLI seam) reads the SAME rule rather than a second copy that could drift.
    /// </summary>
    public static IReadOnlyList<CreatureSpeciesDef> EligibleOutputs() =>
        CreatureSpeciesCatalog.All
            .Where(s => CreatureRarityLadder.AtLeast(s.BaseRarity, OutputEligibilityFloor)
                        // spec-species-rank.md §6: the rank floor lands BESIDE the rarity floor above,
                        // never replacing it. Decided through the one shared policy, so a preview
                        // asking the same question cannot drift from this enforcing site. At the
                        // shipped bottom rung it admits every species (zero goldens move); a null
                        // rank — a rank the pipeline skipped — maps to bottom at the gate.
                        && CreatureRankFloors.Passes(CreatureRankFloors.FusionRecipeEligibility, s.Rank)
                        && s.Acquisition != CreatureAcquisition.CaptureOnly)
            .OrderBy(s => s.BaseRarity)
            .ThenBy(s => s.SpeciesId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// `fusion-recipe-generator` (creature-seed module 17, spec §3 step 1) seam: the exact deficit set
    /// the deterministic pass could not cover — every <see cref="EligibleOutputs"/> whose id never
    /// appears as a <see cref="CreatureRecipeDef.OutputSpeciesId"/> in a fresh
    /// <see cref="BuildDeterministicOnly"/>. Pinned to `SpeciesId` ordinal (already
    /// <see cref="EligibleOutputs"/>'s own order) so which outputs land here is a reproducible fact
    /// of that method's own existing iteration, never an accident of however a caller happens to
    /// enumerate a set.
    ///
    /// <para>Deliberately calls <see cref="BuildDeterministicOnly"/> (always recomputes fresh, never
    /// cached) rather than reading the loaded <see cref="All"/> — `All` reflects whatever the host
    /// last `Configure`d (which, once module 18's committed seed is loaded, legitimately INCLUDES
    /// gap-fills `BuildDeterministicOnly()` alone never produces), so diffing against `All` here
    /// would find every gap-fill as a false "still unresolved." This method answers a narrower,
    /// purely computational question — "what can the deterministic pass alone not cover right now"
    /// — and must never be confused with "what has no recipe in the currently loaded catalog."
    /// </para>
    /// </summary>
    public static IReadOnlyList<CreatureSpeciesDef> UnresolvedOutputs()
    {
        var covered = new HashSet<string>(BuildDeterministicOnly().Select(r => r.OutputSpeciesId), StringComparer.Ordinal);
        return EligibleOutputs().Where(o => !covered.Contains(o.SpeciesId)).ToList();
    }

    /// <summary>
    /// `fusion-recipe-generator` §2 seam: every eligible input candidate within the nearest
    /// <paramref name="maxPopulatedRungs"/> POPULATED rungs below <paramref name="outputRarity"/>
    /// (never a raw enum-ordinal distance — an unpopulated rung is not a step, the exact walk
    /// <see cref="InputPoolBelow"/> already performs for the single-rung case), each tagged with
    /// its own 1-based rung distance (1 = the nearest populated rung, matching what
    /// <see cref="NearestPopulatedRungBelow"/> alone returns). Bounds the LLM's own prompt (spec
    /// §2: "an unbounded prompt is unauditable and untunable") by construction — a caller can never
    /// accidentally show a rung further out without raising <paramref name="maxPopulatedRungs"/>.
    /// </summary>
    public static IReadOnlyList<(CreatureSpeciesDef Species, int RungDistance)> CandidatePoolBelow(
        CreatureRarity outputRarity, int maxPopulatedRungs = 3)
    {
        var result = new List<(CreatureSpeciesDef, int)>();
        var cursor = outputRarity;
        var populatedRungsSeen = 0;
        while (populatedRungsSeen < maxPopulatedRungs)
        {
            // Checked BEFORE stepping down, on the rung this iteration is about to leave — not
            // re-checked against the stale pre-step value at the end (the exact bug this comment
            // replaces: re-testing "was Chaff already the floor before this step" after `cursor`
            // has already moved TO Chaff let the loop revisit and double-add Chaff's own species on
            // the following iteration, once for the step that reached it and once more for the step
            // that merely confirmed it). If the CURRENT cursor is already the floor, there is
            // nothing lower to walk to — stop before reprocessing it.
            if (CreatureRarityLadder.IsBottomRung(cursor)) break;
            cursor = CreatureRarityLadder.OneRungBelow(cursor);

            var pool = CreatureSpeciesCatalog.All
                .Where(s => s.BaseRarity == cursor && s.Acquisition != CreatureAcquisition.CaptureOnly)
                .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
                .ToList();

            if (pool.Count > 0)
            {
                populatedRungsSeen++;
                foreach (var s in pool) result.Add((s, populatedRungsSeen));
            }

            if (CreatureRarityLadder.IsBottomRung(cursor)) break; // just processed the floor itself — nothing further down
        }
        return result;
    }

    /// <summary>
    /// The deterministic pass ONLY — `fusion-recipe-generator` (module 17, spec §3 step 1)'s own
    /// dependency, never by <see cref="All"/> any more (T8.4). Always recomputes fresh — no caching
    /// of its own, so a caller comparing it against a loaded, gap-filled <see cref="All"/> is always
    /// diffing against a live, current answer.
    ///
    /// <para><c>internal</c> since T8.5's own success criterion ("`Build()`'s public surface is
    /// gone; only the generator's own CLI calls the renamed method") — reachable from
    /// `gk-forge/tools/CreatureRecipeReconcileInput` (the caller today) and `gk-forge/tools/CreatureRecipeDistributionIndex`
    /// (the generator's sibling CLI, granted the same access for consistency even though it does not
    /// call this specific member today) via <c>InternalsVisibleTo</c> in
    /// `FusionRpg.Core.csproj` — never `public` again, so no player-facing code can silently start
    /// depending on live recomputation the way `Program.cs` itself used to before the flip.</para>
    /// </summary>
    internal static IReadOnlyList<CreatureRecipeDef> BuildDeterministicOnly()
    {
        var outputs = EligibleOutputs();

        var recipes = new List<CreatureRecipeDef>(outputs.Count);
        var usedPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            var pool = InputPoolBelow(output.BaseRarity);
            if (pool.Count < 2)
                throw new InvalidOperationException(
                    $"Recipe pool below {output.BaseRarity} has {pool.Count} species (searched down to "
                    + $"{CreatureRarity.Chaff}) — catalog cannot support fusion.");

            // Found running the real flip 2026-09-05: at 84 species, exactly one `a` candidate ever
            // existed per output's element (the primary-match, or pool[0]) and pairs never ran out
            // below it. At 829 species, many outputs share the same primary element and therefore
            // the same greedy `a` — once every unused-pair slot for that specific `a` is claimed by
            // an earlier output, the old single-`a` attempt had nowhere left to go and threw. The
            // preference order itself (A: primary-element match first; B: secondary-element donor,
            // then ring-related, then catalog order) is untouched — this only adds backtracking
            // across the SAME ordered `a` candidates the design already prefers, within the SAME
            // single rung `InputPoolBelow` returns (never mixes rungs — CreatureRecipeCatalogTests'
            // own `Inputs_are_distinct_band_below_and_never_capture_only` pins both inputs to the
            // exact same nearest-populated rung, an invariant this fix does not touch).
            var (chosenA, chosenB) = TryFindPair(pool, output, usedPairs);

            // A genuine capacity ceiling — {pool.Count} species below a rung support at most C(n,2)
            // distinct pairs, and 829 species can need more outputs at one rung than that (found
            // live running the real catalog-runtime flip, 2026-09-05, as a hard throw that crashed
            // server startup). This SKIPS the unresolvable output rather than fabricating a
            // cross-rung or reused pair on its own authority — this method is deliberately
            // deterministic-only (T8.4), so a skip here IS the correct, permanent behavior: it is
            // exactly what `UnresolvedOutputs()` reads to discover `fusion-recipe-generator`'s own
            // deficit set (module 17, spec §3 step 1), which the model then proposes into and the
            // reconciler validates — never invented as a fallback inside THIS method.
            if (chosenA is null || chosenB is null) continue;

            recipes.Add(new CreatureRecipeDef("recipe." + output.SpeciesId, output.SpeciesId, chosenA.SpeciesId, chosenB.SpeciesId, CrossRungGapFill: false));
        }

        return recipes;
    }

    /// <summary>Tries every `a` candidate in the design's own preference order (primary-element match
    /// first) against every `b` candidate in ITS preference order, stopping at the first unused pair.
    /// Returns (null, null) if the pool cannot support one more distinct pair at all.</summary>
    static (CreatureSpeciesDef? A, CreatureSpeciesDef? B) TryFindPair(
        List<CreatureSpeciesDef> pool, CreatureSpeciesDef output, HashSet<string> usedPairs)
    {
        var aCandidates = pool
            .OrderByDescending(p => p.ElementPrimary == output.ElementPrimary)
            .ThenBy(p => p.SpeciesId, StringComparer.Ordinal);
        foreach (var a in aCandidates)
        {
            var bCandidates = pool
                .Where(p => !ReferenceEquals(p, a))
                .OrderBy(p => BRank(p, output))
                .ThenBy(p => p.SpeciesId, StringComparer.Ordinal);
            var b = bCandidates.FirstOrDefault(p => usedPairs.Add(PairKey(a.SpeciesId, p.SpeciesId)));
            if (b is not null) return (a, b);
        }
        return (null, null);
    }

    /// <summary>
    /// Species one rung below <paramref name="outputRarity"/> — widening the search downward one
    /// rung at a time until at least two candidates exist, or the bottom (Chaff) has been included.
    /// A fixed "exactly one rung below" (the pre-migration shape) assumed every adjacent rung was
    /// populated; the ten-rung ladder does not guarantee that (spec-rarity-migration.md's own risk:
    /// widening an enum does not widen the roster that fills it). Walking down is the ladder-safe
    /// replacement — never a bare `(CreatureRarity)((int)r - 1)` cast.
    /// </summary>
    static List<CreatureSpeciesDef> InputPoolBelow(CreatureRarity outputRarity)
    {
        var pool = new List<CreatureSpeciesDef>();
        var cursor = outputRarity;
        while (true)
        {
            var atBottom = CreatureRarityLadder.IsBottomRung(cursor);
            cursor = atBottom ? cursor : CreatureRarityLadder.OneRungBelow(cursor);

            pool = CreatureSpeciesCatalog.All
                .Where(s => s.BaseRarity == cursor && s.Acquisition != CreatureAcquisition.CaptureOnly)
                .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
                .ToList();

            if (pool.Count >= 2 || atBottom)
                return pool;
        }
    }

    static int BRank(CreatureSpeciesDef candidate, CreatureSpeciesDef output)
    {
        if (output.ElementSecondary is { } secondary && candidate.ElementPrimary == secondary)
            return 0;
        return ElementRingMatrix.GetRelation(candidate.ElementPrimary, output.ElementPrimary)
               != ElementMatchupRelation.Neutral ? 1 : 2;
    }
}

public sealed class FusionRecipeSeedRejection : Exception
{
    public FusionRecipeSeedRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser, no file I/O (tunables-ssot.md §7.2) — reads the exact canonical shape
/// `fusion-recipe-generator`'s own `emit.py` writes to `gk-data/packs/fusion/data/generated/creatures/_fusion-recipes.json`
/// (spec-fusion-recipe-generator.md §4): `{ recipeId: { outputSpeciesId, inputSpeciesIdA,
/// inputSpeciesIdB, crossRungGapFill } }`. Mirrors `SpeciesBuildPlanReader`'s own shape and
/// discipline exactly — `SpeciesBuildPlanCatalog.cs`'s established precedent for a small,
/// file-backed catalog, per spec-fusion-recipe-runtime.md's own note that this module is
/// `SpeciesBuildPlanCatalog`'s file shape, not `CreatureSpeciesCatalog`'s DB shape.
///
/// <para>`provenance` (present only on `crossRungGapFill: true` entries, spec §4) is build-time-only
/// bookkeeping for `reconcile.py`'s own freeze check — this reader has no use for it at runtime and
/// skips it silently, the same way it never reads an anchor's own `_derived`/`_provenance` keys
/// either.</para>
/// </summary>
public static class FusionRecipeSeedReader
{
    public static IReadOnlyList<CreatureRecipeDef> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FusionRecipeSeedRejection("fusion recipe seed: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FusionRecipeSeedRejection($"fusion recipe seed: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new FusionRecipeSeedRejection("fusion recipe seed: expected a top-level object");

            var result = new List<CreatureRecipeDef>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var recipeId = prop.Name;
                var entry = prop.Value;
                if (entry.ValueKind != JsonValueKind.Object)
                    throw new FusionRecipeSeedRejection($"fusion recipe seed: '{recipeId}' is not an object");

                string RequireString(string field)
                {
                    if (!entry.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String)
                        throw new FusionRecipeSeedRejection($"fusion recipe seed: '{recipeId}.{field}' is missing or not a string");
                    return v.GetString()!;
                }

                bool RequireBool(string field)
                {
                    if (!entry.TryGetProperty(field, out var v) || (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False))
                        throw new FusionRecipeSeedRejection($"fusion recipe seed: '{recipeId}.{field}' is missing or not a boolean");
                    return v.ValueKind == JsonValueKind.True;
                }

                result.Add(new CreatureRecipeDef(
                    recipeId,
                    RequireString("outputSpeciesId"),
                    RequireString("inputSpeciesIdA"),
                    RequireString("inputSpeciesIdB"),
                    RequireBool("crossRungGapFill")));
            }
            return result;
        }
    }
}
