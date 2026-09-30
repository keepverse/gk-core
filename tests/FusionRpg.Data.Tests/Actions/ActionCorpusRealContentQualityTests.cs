using System.Linq;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// T59.5 (spec-action-instance-and-grant.md, criterion 2): the REAL shipped
/// `data/seed/actions/committed-round-{1,2}.json` (24 rows), imported through T59.4's real importer
/// against a real atom catalog seeded from the REAL `gk-data/packs/fusion/data/seed/atoms/` files — not a hand-built
/// fixture, matching `AuthoredEligibilityResolvesTests.cs`'s own precedent of reading real files.
///
/// <para><b>Real, measured finding (2026-09-06), not assumed</b>: only 2 of the 30 unique
/// `atomFamilies` the real corpus names (`atom.fortitude`, `atom.vitality`) exist anywhere under
/// `gk-data/packs/fusion/data/seed/atoms/` — 28 do not (`atom.sporing`, `atom.volley`, `atom.cherry-bloom`, etc.), confirmed
/// by a direct cross-check of every seed file. This mirrors the already-documented item-unique-corpus
/// atom-family gap (144 anchors name 68 families, only 28 real) — the SAME small, early-stage atom
/// catalog, a different content type hitting the identical wall. Exactly 3 of 24 briefs reference at
/// least one of the two resolvable families, so exactly 3 import; the other 21 correctly refuse,
/// naming why. This is a real content-authoring gap in a sibling pipeline (the atom/family generator),
/// not a defect in A21's own import machinery — fixing it is out of this module's scope, matching how
/// the item-unique gap was left "correctly refusing" rather than force-fixed.</para>
/// </summary>
public class ActionCorpusRealContentQualityTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionCorpusRealContentQualityTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        // The one real atom seed file backing the two families the real corpus can actually reach.
        var atomsPath = SeedPath("data", "seed", "atoms", "generated", "family-expand.g-life.json");
        var collect = AtomSeedFile.Collect(new[] { (atomsPath, File.ReadAllText(atomsPath)) });
        Assert.True(collect.IsOk, string.Join("; ", collect.Errors));
        var atomResult = _store.UpsertAtoms(collect.Content.Atoms);
        Assert.Empty(atomResult.Rejected);
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>Path into the authored seed corpus, which the split put in the gk-data pack.
    /// Both of this file's reads are data/seed/**, so the helper is named for the corpus rather than
    /// for a repository: the old name said "repo" while resolving gk-core, which is a repository with
    /// no data/seed at all, and its unused [CallerFilePath] parameter implied a search that never
    /// happened.</summary>
    static string SeedPath(params string[] parts) => Path.Combine(new[] { KeepverseRoots.Content() }.Concat(parts).ToArray());

    static ActionCorpusCostTemplate CostTemplate() => new(
        new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>
        {
            [ActionCategory.Attack] = new("qi", 20, ActionCostTiming.OnCommit),
            [ActionCategory.Defense] = new("qi", 30, ActionCostTiming.OnCommit),
            [ActionCategory.Support] = new("qi", 40, ActionCostTiming.OnCommit),
            [ActionCategory.Movement] = new("qi", 15, ActionCostTiming.OnCommit),
            [ActionCategory.Status] = new("qi", 35, ActionCostTiming.OnCommit),
        },
        // T7 (basic-attack-seed): one of the 24 real briefs this file imports
        // (`action.species.cabbagepult.002`) already authors `"kindHint": "innate"` — Kind-aware rows
        // are required here too, mirroring the real shipped action-corpus-cost-templates.v1.json's own
        // "kinds" block, or that one brief's compose now rejects for lacking a `kinds.innate` row.
        new Dictionary<ActionKind, ActionCorpusCostTemplateRow>
        {
            [ActionKind.Basic] = new("stamina", 20, ActionCostTiming.OnCommit),
            [ActionKind.Innate] = new("qi", 25, ActionCostTiming.OnCommit),
        });

    static IReadOnlyList<ActionCorpusBrief> RealBriefs()
    {
        var briefs = new List<ActionCorpusBrief>();
        foreach (var f in new[] { "committed-round-1.json", "committed-round-2.json" })
            briefs.AddRange(ActionCorpusBriefJson.Parse(File.ReadAllText(SeedPath("data", "seed", "actions", f))));
        return briefs;
    }

    [Fact]
    public void ImportingTheRealShippedCorpusSucceedsExactlyWhereItsFamiliesResolveAndRejectsHonestlyElsewhere()
    {
        var briefs = RealBriefs();

        // Liveness and reconciliation, never sizes. This asserted three literals -- 24 briefs,
        // 3 imported, 21 rejected -- and all three are READINGS: the committed corpus grows whenever
        // content ships, and accepted/rejected-per-cycle is the exact shape validation-ssot names as
        // unpinnable. It failed on 24 -> 25 (solid-remediation T0.3/T0.4) and the "fix" would have been
        // to type the new numbers, which guards nothing and trains the next reader to do the same.
        //
        // What this test's own name promises is the contract asserted below: the import SUCCEEDS where
        // families resolve, REJECTS HONESTLY elsewhere, and accounts for every row either way.
        Assert.NotEmpty(briefs);

        var result = ActionCorpusImporter.Import(_store, briefs, CostTemplate(), RungPolicy.Table);

        // Every row is accounted for exactly once -- nothing silently vanishes between the two counts.
        Assert.Equal(briefs.Count, result.ImportedCount + result.RejectedCount);
        Assert.Equal(briefs.Count, result.Outcomes.Count);
        Assert.Equal(result.ImportedCount, result.Outcomes.Count(o => o.Imported));

        // The path actually works on real content, rather than rejecting everything and passing.
        Assert.True(result.ImportedCount > 0,
            "no real brief imported -- a corpus that rejects everything would satisfy every other assertion here");

        // And every rejection is honest: it names why, rather than failing silently.
        Assert.All(result.Outcomes.Where(o => !o.Imported),
            o => Assert.Contains("resolved any atom", o.Rejection ?? "", StringComparison.Ordinal));
    }

    /// <summary>Criterion 2, exercised for real: every row that DOES import must clear
    /// `StructureBudgetGuard.Check` at its own authored rung — a content-quality check, not just a
    /// schema check.</summary>
    [Fact]
    public void EveryImportedRowClearsItsOwnStructureBudgetAtItsAuthoredRung()
    {
        var result = ActionCorpusImporter.Import(_store, RealBriefs(), CostTemplate(), RungPolicy.Table);
        var imported = result.Outcomes.Where(o => o.Imported).ToList();
        Assert.NotEmpty(imported); // liveness -- if this ever hits zero, the test below is vacuous

        foreach (var outcome in imported)
        {
            var row = _store.GetAction(outcome.BriefId);
            Assert.NotNull(row);
            var costs = _store.ListCosts(outcome.BriefId);
            var scopes = _store.ListScopes(outcome.BriefId);

            var check = StructureBudgetGuard.Check(row!, costs, scopes, RungPolicy.Table);
            Assert.True(check.IsOk, $"{outcome.BriefId} (rung {row!.Rung}): {check.Detail}");
        }
    }

    /// <summary>
    /// A24 (spec-container-effect-resolver-production.md §Objective): the precise, empirical proof
    /// that closing `container-effect-resolver-not-wired` for the Compiled-path class of content does
    /// NOT make these 3 real, already-imported actions activate. Both real seed atom families
    /// (`atom.fortitude`, `atom.vitality`) are authored `stat.modify` with `roll: onApply` and
    /// `min != max` — `Compilability.Classify`'s Rule 3 routes both to `AtomPath.Runner`, never
    /// `Compiled`, confirmed directly against the seed file. So the real resolver correctly reports
    /// ZERO effect ids for every one of these 3 containers — not because the resolver is broken, but
    /// because `BattleEngine.Resolve` has no execution mechanism for the Runner path at all
    /// (`battle-runner-path-not-wired`, named in the same spec, not fixed by it).
    /// </summary>
    [Fact]
    public void TheThreeRealImportedActionsCompileToZeroEffectDefsBecauseTheirAtomsAreRunnerPathOnly()
    {
        var result = ActionCorpusImporter.Import(_store, RealBriefs(), CostTemplate(), RungPolicy.Table);
        var imported = result.Outcomes.Where(o => o.Imported).ToList();
        Assert.Equal(3, imported.Count); // liveness, matching the test above

        var (resolver, defs, runnerBindings, _) = ActionContainerEffectResolverFactory.Build(_store);
        Assert.Empty(defs); // nothing at all compiled -- both real families are Runner-path only
        // A25: also empty on the Runner-path seam -- both real families are triggerless (Compilability
        // routes them to Runner, but neither authors a `when.trigger`), so they are skipped there too,
        // not merely re-routed from one empty result to another.
        Assert.Empty(runnerBindings);

        foreach (var outcome in imported)
        {
            var row = _store.GetAction(outcome.BriefId);
            Assert.NotNull(row);
            Assert.NotEmpty(row!.ContainerId); // liveness -- the composer never draws zero atoms

            // Empty, not a throw: BindContainers' own existing "resolved to nothing" rejection is what
            // surfaces this at battle setup, precisely, not a new failure mode invented here.
            Assert.Empty(resolver.EffectIdsFor(row.ContainerId));
        }
    }
}
