using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// Item module 3 (<c>recipe-import</c>, spec-recipe-import.md §2/§4) — the boot import of the
/// authored combination corpus.
///
/// <para>⭐ <b>"Print and skip", never "print and seed".</b> A recipe the grid refuses is content no
/// cell asked for; seeding it anyway makes the refusal decorative. Every accepted recipe is seeded
/// beside the generated resonances, and any authored row this boot did not accept is disabled (never
/// deleted) so a retired Strain stops firing.</para>
///
/// <para>⛔ <b>Core reads no file.</b> This class is the host's file I/O; the parsing and the mapping
/// are <see cref="CombinationCorpus.Parse"/> / <see cref="CombinationCorpus.ToRecipes"/>, and the
/// content rules are <see cref="StrainSpliceGrid.ValidateRecipe"/> — this file decides nothing about
/// what a legal recipe is.</para>
/// </summary>
public static class CombinationBoot
{
    /// <summary>Where the authored corpus lives under the content root (seedsmith's
    /// <c>combinations/</c> partition).</summary>
    public const string CombinationsRelativeDir = "data/seed/items/combinations";

    /// <summary>The archetype axis, READ from module 13's registry — Core never declares it, and
    /// neither does this file (spec-recipe-import §2's "the host passes them in").</summary>
    public static IReadOnlyList<string> ReadArchetypes(string seedRoot)
    {
        if (seedRoot is null) throw new ArgumentNullException(nameof(seedRoot));

        var path = Path.Combine(seedRoot, "data", "seed", "items", "_registry",
            "build-themes.v1.json");
        var archetypes = new List<string>();
        if (!File.Exists(path)) return archetypes;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var theme in doc.RootElement.GetProperty("themes").EnumerateArray())
            if (theme.TryGetProperty("archetype", out var arch) &&
                arch.GetString() is { Length: > 0 } value && !archetypes.Contains(value))
                archetypes.Add(value);
        return archetypes;
    }

    /// <summary>What one boot import did — the counts the boot log prints, returned so a test can
    /// assert the same numbers without scraping stdout.</summary>
    public sealed record ImportResult(
        int EntriesOnDisk,
        int RecipesAccepted,
        int Disabled,
        IReadOnlyList<string> Refusals)
    {
        /// <summary>Every row on disk either became a recipe or was refused by name.</summary>
        public bool Partitioned => EntriesOnDisk == RecipesAccepted + Refusals.Count;
    }

    /// <summary>
    /// Reads every seed document under <paramref name="combinationsDir"/>, maps and validates them,
    /// seeds the accepted recipes beside <see cref="ResonanceGenerator"/>'s resonances, then disables
    /// authored rows the accepted set no longer carries.
    ///
    /// <para>The resonances are always seeded — they are generated from the element roster, not
    /// authored, and this import has no standing to retire one. An empty archetype axis (the registry
    /// is missing) means the grid cannot be evaluated, so NO authored recipe is seeded and every
    /// authored row is disabled rather than fired unvalidated.</para>
    /// <para>⭐ <b>One acceptance set (SSH4.4, spec-combo-bind §1).</b> Before the recipes are seeded,
    /// every grid-accepted recipe's container is built for every tier the ladder can grant and upserted
    /// into <c>effect_container</c>. A recipe the grid ACCEPTS but whose container build REFUSES (a grant
    /// family with no atom) would otherwise seed, evaluate as firing, preview as firing — and bind
    /// nothing. So the accepted set is <i>grid-valid ∩ buildable at every reachable tier</i>, the
    /// container refusal is printed by name beside the grid refusals, and the recipe is left out of the
    /// seed and disabled by the same <c>DisableCombinationsNotIn</c>.</para>
    /// </summary>
    public static ImportResult Seed(
        RpgStore store,
        SocketTuning socketTuning,
        StrainSpliceTuning strainSpliceTuning,
        IReadOnlyList<string> archetypes,
        string combinationsDir,
        ComboContainerBuild.ComboContainerLookups containerLookups,
        Action<string> log)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (socketTuning is null) throw new ArgumentNullException(nameof(socketTuning));
        if (strainSpliceTuning is null) throw new ArgumentNullException(nameof(strainSpliceTuning));
        if (archetypes is null) throw new ArgumentNullException(nameof(archetypes));
        if (combinationsDir is null) throw new ArgumentNullException(nameof(combinationsDir));
        if (containerLookups is null) throw new ArgumentNullException(nameof(containerLookups));
        if (log is null) throw new ArgumentNullException(nameof(log));

        var entries = ReadEntries(combinationsDir);
        var grantsById = ReadGrants(combinationsDir);
        var (mapped, refusals) = CombinationCorpus.ToRecipes(entries, strainSpliceTuning);
        var refused = new List<string>(refusals.Select(r => r.Detail));

        var accepted = new List<ComboRecipe>(mapped.Count);
        foreach (var recipe in mapped)
        {
            if (archetypes.Count == 0)
            {
                refused.Add(
                    $"'{recipe.ComboId}': the archetype axis (build-themes.v1.json) is absent, so " +
                    $"the derived grid cannot be evaluated — refusing to seed an unvalidated recipe");
                continue;
            }

            var problems = StrainSpliceGrid.ValidateRecipe(
                recipe, socketTuning, archetypes, strainSpliceTuning);
            if (problems.Count > 0)
            {
                refused.AddRange(problems.Select(p => $"'{recipe.ComboId}': {p.Detail}"));
                continue;
            }

            // One acceptance set: grid-valid ∩ buildable at EVERY reachable tier. The container is
            // content (an ordinary Combo row), upserted BEFORE the recipe seed, so a recipe that ships
            // always has the containers its GrantedTier can name.
            var grants = grantsById.TryGetValue(recipe.ComboId, out var granted)
                ? granted
                : Array.Empty<string>();
            var buildable = true;
            foreach (var tier in ReachableTiers(recipe.Shape, strainSpliceTuning, socketTuning))
            {
                var container = ComboContainerBuild.TryBuild(
                    recipe.ComboId, grants, tier, containerLookups, out var refusal);
                if (container is null)
                {
                    refused.Add($"'{recipe.ComboId}': {refusal}");
                    buildable = false;
                    break;
                }
                var rejection = store.UpsertContainer(container);
                if (!rejection.IsOk)
                {
                    refused.Add($"'{recipe.ComboId}': container '{container.ContainerId}' was refused " +
                                $"by the store: {rejection.Reason} {rejection.Detail}");
                    buildable = false;
                    break;
                }
            }
            if (buildable) accepted.Add(recipe);
        }

        var resonances = ResonanceGenerator.Generate(socketTuning);
        store.SeedComboRecipes(resonances.Concat(accepted).ToList());
        var disabled = store.DisableCombinationsNotIn(
            accepted.Select(r => r.ComboId).ToHashSet(StringComparer.Ordinal));

        foreach (var detail in refused) log($"[items] combo recipe refused: {detail}");
        log($"[items] combination corpus: {entries.Count} on disk, {accepted.Count} accepted, " +
            $"{refused.Count} refused, {disabled} retired, {resonances.Count} resonances");

        return new ImportResult(entries.Count, accepted.Count, disabled, refused);
    }

    static IReadOnlyList<CombinationEntry> ReadEntries(string combinationsDir)
    {
        if (!Directory.Exists(combinationsDir)) return Array.Empty<CombinationEntry>();

        var entries = new List<CombinationEntry>();
        foreach (var file in Directory.EnumerateFiles(combinationsDir, "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
            entries.AddRange(CombinationCorpus.Parse(File.ReadAllText(file)));
        return entries;
    }

    /// <summary>Every entry's <c>grants</c>, keyed by id — the field a combination's CONTAINER is built
    /// from and <see cref="ComboRecipe"/> deliberately does not carry (a recipe is a firing rule).
    /// Read through <see cref="CombinationCorpus.ReadGrants"/>, the same parse the pricing dump uses.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadGrants(string combinationsDir)
    {
        var byId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!Directory.Exists(combinationsDir)) return byId;

        foreach (var file in Directory.EnumerateFiles(combinationsDir, "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
            foreach (var (id, granted) in CombinationCorpus.ReadGrants(File.ReadAllText(file)))
                byId[id] = granted;
        return byId;
    }

    /// <summary>Every tier the ladder can GRANT this shape, plain and attuned — the container ids a
    /// recipe must have. Distinct, so a one-rung ladder that grants one tier twice builds it once.</summary>
    static IReadOnlyList<int> ReachableTiers(
        ComboShape shape, StrainSpliceTuning strainSpliceTuning, SocketTuning socketTuning)
    {
        var tiers = new SortedSet<int>();
        foreach (var rung in strainSpliceTuning.TierLadder)
            foreach (var attuned in new[] { false, true })
                tiers.Add(strainSpliceTuning.GrantedTier(shape, rung.Rung, socketTuning, attuned));
        return tiers.ToList();
    }
}
