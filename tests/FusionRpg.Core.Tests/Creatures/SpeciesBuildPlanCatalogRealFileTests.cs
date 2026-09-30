using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Tests.Creatures.Fusion;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// `species-build` G1/G2 (casing-bug fix, 2026-09-05) — the one test that was missing and let the
/// bug ship. Every existing caller of <see cref="SpeciesBuildPlanCatalog"/> (e.g.
/// <c>FusionRpg.Server.Tests.SpeciesBuildEndpointsTests</c>) configures it with a hand-built fixture
/// dictionary that is ALREADY keyed with the correct lowercase runtime id, so none of them could
/// ever notice that the real committed file used a completely different key space (seedsmith-anchor
/// PascalCase, e.g. <c>"FumeShroom"</c>, vs. <see cref="CreatureSpeciesCatalog"/>'s lowercase runtime id,
/// e.g. <c>"fumeshroom"</c> — zero exact-string overlap between the two). This file loads the REAL
/// committed <c>gk-data/packs/fusion/data/generated/creatures/_species-build-plan.json</c> through the real
/// <see cref="SpeciesBuildPlanReader"/> — no hand-built fixture anywhere in it.
///
/// <para><b>⛔ Real bug fixed 2026-09-07 (owner caught it: "why did we still stuck at 84 creature"):</b>
/// this file used to check the plan against <see cref="CreatureSpeciesCatalog.ConfigureFromCompiledDefault"/>
/// — the compiled, 84-species snapshot from BEFORE `catalog-runtime`'s real flip (2026-09-05). That
/// flip already moved the real, live game (<c>Server/Program.cs:350</c>,
/// <c>Injector/Host/RpgHost.cs</c>) onto the full store-backed roster (904 species today); this test
/// file alone never followed, so it was silently validating the plan against a roster the real game
/// no longer uses. `gk-forge/tools/CreatureBuildPlanGen` itself had the exact same bug (fixed the same day,
/// same root cause) — together they explain why the committed plan was stuck at 84 species long
/// after the corpus grew to 904. Fixed by scoping every test here to
/// <see cref="RealCorpusFixture.Snapshot"/> — the SAME real, store-backed 904-species roster
/// <c>FusionRecipeReconcileTests</c> already uses, re-derived from the real anchor corpus through the
/// real pipeline, never the stale compiled default.</para>
/// </summary>
public class SpeciesBuildPlanCatalogRealFileTests
{
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the content root, so the `gk-data/packs/fusion/data/generated` plan read stays valid once content moves to the
        // gk-data pack. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.ContentRoot.Path;
    }

    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> LoadRealPlan()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "generated", "creatures", "_species-build-plan.json");
        return SpeciesBuildPlanReader.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void Real_plan_resolves_a_real_shipped_species_to_a_real_non_empty_vector()
    {
        // The exact defect (found live, 2026-09-05): the committed plan's keys were seedsmith-anchor
        // PascalCase ("FumeShroom"), while CreatureSpeciesCatalog's runtime id is lowercase
        // ("fumeshroom") — SharesFor("fumeshroom") silently returned EmptyShares (all-zero
        // aptitudes) for every live species, indistinguishable from the legitimate "not yet
        // classified" case. `gk-forge/tools/CreatureBuildPlanGen` now keys the plan by the runtime speciesId,
        // joined via the game's own stable (Side, GameTypeId) identity. This proves the fix against
        // the file actually on disk, not a re-derived expectation.
        using (CreatureSpeciesCatalog.UseScoped(RealCorpusFixture.Snapshot))
        {
            SpeciesBuildPlanCatalog.Configure(LoadRealPlan());

            var shares = SpeciesBuildPlanCatalog.SharesFor("fumeshroom");

            Assert.NotEmpty(shares);
            Assert.Equal(1000, shares.Values.Sum());
        }
    }

    [Fact]
    public void Real_plan_keys_are_all_real_runtime_species_ids_never_anchor_text()
    {
        // A regression guard for the whole BUG CLASS, not just the one species above: every key in
        // the committed file must be a live species id. A PascalCase (or any other unrecognised) key
        // sneaking back in means the generator's (Side, GameTypeId) join broke.
        using (CreatureSpeciesCatalog.UseScoped(RealCorpusFixture.Snapshot))
        {
            var plan = LoadRealPlan();

            var unknownKeys = plan.Keys.Where(k => !CreatureSpeciesCatalog.IsKnown(k)).ToList();

            Assert.True(unknownKeys.Count == 0,
                $"plan has {unknownKeys.Count} key(s) that are not real runtime species ids: " +
                string.Join(", ", unknownKeys));
        }
    }

    /// <summary>
    /// G2 — a species with no plan entry is legitimate BY DESIGN (still <c>unresolved</c> on a voted
    /// classification field — <see cref="SpeciesBuildPlanCatalog.SharesFor"/>'s own documented
    /// contract), but which species that is must be a named, checked-in fact, not something that can
    /// silently grow. If this set ever changes, this test fails and NAMES exactly what changed,
    /// instead of staying green through a regression of G1's own bug class or a new species shipping
    /// with no plan behind it — this is the test that would have caught G1 before it shipped.
    /// </summary>
    // 2026-09-07: T2.11's full classification run (840 -> 903 -> 904 species) plus DoubleCherry's own
    // owner-directed manual attackTempo fix closed every real content gap; separately,
    // `CreatureBuildPlanGen`/this test file's own stale "compiled 84" scope was fixed the same day (see
    // the class doc above) — the plan now genuinely covers all 904 live species. This allowlist is empty.
    static readonly IReadOnlyList<string> KnownMissingPlanSpecies = Array.Empty<string>();

    [Fact]
    public void Species_with_no_real_plan_entry_matches_the_named_checked_in_allowlist()
    {
        using (CreatureSpeciesCatalog.UseScoped(RealCorpusFixture.Snapshot))
        {
            var plan = LoadRealPlan();

            var missing = CreatureSpeciesCatalog.All
                .Select(s => s.SpeciesId)
                .Where(id => !plan.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var expected = KnownMissingPlanSpecies.OrderBy(id => id, StringComparer.Ordinal).ToList();

            var newlyMissing = missing.Except(expected).ToList();
            var newlyCovered = expected.Except(missing).ToList();

            Assert.True(newlyMissing.Count == 0 && newlyCovered.Count == 0,
                "the set of live species with no species-build plan entry has changed since " +
                "tasks/species-build-todo.md G3 was last investigated. " +
                (newlyMissing.Count > 0 ? $"NEWLY MISSING, investigate why (regression or new unclassified species): {string.Join(", ", newlyMissing)}. " : "") +
                (newlyCovered.Count > 0 ? $"NEWLY COVERED (update KnownMissingPlanSpecies above — one of these got classified/planned): {string.Join(", ", newlyCovered)}. " : ""));
        }
    }
}
