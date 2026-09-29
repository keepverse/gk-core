using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// T59.4 (spec-action-instance-and-grant.md §1): the composer wired to a real SQLite database,
/// through the real `UpsertContainer`/`UpsertAction`/`UpsertCost` idempotency guards — proven by
/// running the same import twice and reading revisions back, not assumed from the guard's own doc
/// comments.
/// </summary>
public class ActionCorpusImporterTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionCorpusImporterTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        SeedAtoms();
    }

    public void Dispose() => _testStore.Dispose();

    void SeedAtoms()
    {
        foreach (var tier in new[] { 1, 2 })
        {
            var atom = new AtomRow
            {
                AtomId = AtomRow.DeriveId("atom.import-test", "", tier),
                KindId = "stat.modify",
                FamilyId = "atom.import-test",
                Variant = "",
                Tier = tier,
                Name = "import test",
                ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
            };
            var result = _store.UpsertAtoms(new[] { atom });
            Assert.Empty(result.Rejected);
        }
    }

    static ActionCorpusCostTemplate CostTemplate() => new(
        new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>
        {
            [ActionCategory.Attack] = new("qi", 20, ActionCostTiming.OnCommit),
            [ActionCategory.Defense] = new("qi", 30, ActionCostTiming.OnCommit),
            [ActionCategory.Support] = new("qi", 40, ActionCostTiming.OnCommit),
            [ActionCategory.Movement] = new("qi", 15, ActionCostTiming.OnCommit),
            [ActionCategory.Status] = new("qi", 35, ActionCostTiming.OnCommit),
        },
        // T7 (basic-attack-seed): Kind-aware rows, mirroring the real shipped
        // action-corpus-cost-templates.v1.json's own "kinds" block.
        new Dictionary<ActionKind, ActionCorpusCostTemplateRow>
        {
            [ActionKind.Basic] = new("stamina", 20, ActionCostTiming.OnCommit),
            [ActionKind.Innate] = new("qi", 25, ActionCostTiming.OnCommit),
        });

    static ActionCorpusBrief Brief(string id = "action.import.test.001") => new(
        Id: id, Name: "Import Test Volley", Category: "attack", Scope: "general", ScopeKey: null,
        RungFloor: 1, RungCeiling: 1, AtomFamilies: new[] { "atom.import-test" },
        TargetMode: "single", Relation: "enemy");

    [Fact]
    public void AFreshBriefImportsARealActionContainerAndCost()
    {
        var result = ActionCorpusImporter.Import(_store, new[] { Brief() }, CostTemplate(), RungPolicy.Table);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.RejectedCount);

        var row = _store.GetAction("action.import.test.001");
        Assert.NotNull(row);
        Assert.Equal(ActionCategory.Attack, row!.Category);
        Assert.NotEmpty(_store.GetContainer(row.ContainerId)!.Atoms);
        Assert.NotEmpty(_store.ListCosts("action.import.test.001"));
    }

    /// <summary>Acceptance criterion 1 (spec's own words): "importing the corpus twice produces
    /// byte-identical rpg_action/rpg_action_cost/container rows — the second import's
    /// UpsertAction/UpsertCost calls move zero revisions."</summary>
    [Fact]
    public void ImportingTheSameBriefSetTwiceMovesZeroRevisionsOnTheSecondPass()
    {
        var briefs = new[] { Brief() };
        var template = CostTemplate();

        ActionCorpusImporter.Import(_store, briefs, template, RungPolicy.Table);
        var revisionAfterFirst = _store.GetAction("action.import.test.001")!.Revision;
        var containerRevisionAfterFirst = _store.GetContainer("skill.action-import-test-001")!.Revision;

        var second = ActionCorpusImporter.Import(_store, briefs, template, RungPolicy.Table);
        var revisionAfterSecond = _store.GetAction("action.import.test.001")!.Revision;
        var containerRevisionAfterSecond = _store.GetContainer("skill.action-import-test-001")!.Revision;

        Assert.Equal(1, second.ImportedCount); // still reports success -- it just wrote nothing new
        Assert.Equal(revisionAfterFirst, revisionAfterSecond);
        Assert.Equal(containerRevisionAfterFirst, containerRevisionAfterSecond);
    }

    [Fact]
    public void ARejectedBriefDoesNotBlockTheRestOfTheBatch()
    {
        var badBrief = Brief("action.import.bad.001") with { Category = "not-a-category" };
        var goodBrief = Brief("action.import.good.001");

        var result = ActionCorpusImporter.Import(_store, new[] { badBrief, goodBrief }, CostTemplate(), RungPolicy.Table);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.NotNull(_store.GetAction("action.import.good.001"));
        Assert.Null(_store.GetAction("action.import.bad.001"));
    }

    // ---- T7 (basic-attack-seed): the real authored-basics.json + the real committed corpus ----

    static string ContentPath(params string[] parts) => Path.Combine(new[] { ContentRoot.Path }.Concat(parts).ToArray());

    static string CorePath(params string[] parts) => Path.Combine(new[] { CoreRoot.Path }.Concat(parts).ToArray());

    /// <summary>The real shipped tuning file, not the inline mirror above — proves the actual bytes on
    /// disk (with the "kinds" block this task added) parse and resolve correctly.</summary>
    static ActionCorpusCostTemplate RealCostTemplate() =>
        ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(CorePath("data", "tuning", "action-corpus-cost-templates.v1.json")));

    void SeedRealAtomFile(string relativePath)
    {
        var path = ContentPath(relativePath.Split('/'));
        var collect = AtomSeedFile.Collect(new[] { (path, File.ReadAllText(path)) });
        Assert.True(collect.IsOk, string.Join("; ", collect.Errors));
        var result = _store.UpsertAtoms(collect.Content.Atoms);
        Assert.Empty(result.Rejected);
    }

    /// <summary>Data test (spec-basic-attack-seed.md's own testing-strategy table): importing
    /// `authored-basics.json` yields exactly one row, id `act.attack`, `Kind = Basic`, with a `stamina`
    /// cost row attached — against the REAL seed file, the REAL atom (`atom.fx-overlay-damage`,
    /// `gk-data/packs/fusion/data/seed/atoms/fx-core.json`) and the REAL tuning file, not hand-built fixtures.</summary>
    [Fact]
    public void ImportingTheRealAuthoredBasicsFileYieldsActAttackAsBasicWithAStaminaCost()
    {
        SeedRealAtomFile("data/seed/atoms/fx-core.json");
        var briefs = ActionCorpusBriefJson.Parse(File.ReadAllText(ContentPath("data", "seed", "actions", "authored-basics.json")));
        Assert.Single(briefs);
        Assert.Equal("act.attack", briefs[0].Id);
        Assert.Equal(ActionKind.Basic, briefs[0].KindHint);

        var result = ActionCorpusImporter.Import(_store, briefs, RealCostTemplate(), RungPolicy.Table);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.RejectedCount);

        var row = _store.GetAction("act.attack");
        Assert.NotNull(row);
        Assert.Equal(ActionKind.Basic, row!.Kind);

        var costs = _store.ListCosts("act.attack");
        Assert.Single(costs);
        Assert.Equal("stamina", costs[0].ResourceId);
    }

    /// <summary>
    /// The task's own "most important regression check", run for real against the real shipped
    /// `committed-round-{1,2}.json` (24 briefs) and the real tuning file — mirroring
    /// `ActionCorpusRealContentQualityTests`'s established atom fixture (only `atom.fortitude` and
    /// `atom.vitality` resolve, so exactly 3 of 24 briefs import; unchanged by this task, since kindHint
    /// honoring never touches atom-family resolution).
    ///
    /// <para><b>Premise found wrong while verifying this, reported rather than hidden:</b> the todo's
    /// own acceptance bar reads "ALL 179 existing action briefs import completely UNCHANGED — no Kind
    /// drift, no cost drift for any of them." That is false for one of the three briefs that actually
    /// import today: `action.species.cabbagepult.002` already authors `"kindHint": "innate"` in the
    /// real shipped file (measured directly, not assumed) — before this task every brief hardcoded to
    /// `Kind = Skill` regardless of `kindHint`; after this task, honoring `kindHint` (which is the
    /// task's own primary acceptance criterion) necessarily flips this ONE already-imported brief's
    /// `Kind` from `Skill` to `Innate`, and its cost from the `defense` category row (`qi` 30) to the
    /// `kinds.innate` row (`qi` 25). This is the correct, intended effect of finally consuming a field
    /// the parser used to discard (spec-basic-attack-seed.md: "That is the defect; the field is not
    /// new") — not a regression introduced by this change. The other two composing briefs
    /// (`action.general.0003`, `action.species.cabbagepult.001`) carry no `kindHint` and are provably
    /// unaffected, asserted below.</para>
    /// </summary>
    /// <remarks>lawn-combat-wire audit 2026-09-15 (L-N14): this test used to pin the corpus population
    /// (24 briefs, 3 imported, 21 rejected) and three named brief ids — readings that change whenever
    /// content ships, banned by the guardrail rule (validation-ssot.md). It now asserts the contract
    /// every imported brief must satisfy, whatever the corpus size.</remarks>
    [Fact]
    public void TheRealShippedCorpusHonoursKindHintAndKindAwareCostForEveryImportedBrief()
    {
        SeedRealAtomFile("data/seed/atoms/generated/family-expand.g-life.json");
        var briefs = new List<ActionCorpusBrief>();
        foreach (var f in new[] { "committed-round-1.json", "committed-round-2.json" })
            briefs.AddRange(ActionCorpusBriefJson.Parse(File.ReadAllText(ContentPath("data", "seed", "actions", f))));
        Assert.NotEmpty(briefs); // liveness only; the population size is a reading, never pinned

        var template = RealCostTemplate();
        var result = ActionCorpusImporter.Import(_store, briefs, template, RungPolicy.Table);

        // Reconciliation: every parsed brief has exactly one outcome.
        Assert.Equal(briefs.Count, result.Outcomes.Count);
        Assert.Equal(briefs.Count, result.ImportedCount + result.RejectedCount);
        Assert.True(result.ImportedCount > 0, "the real corpus must import at least one brief for this contract to be exercised");

        var byId = briefs.ToDictionary(b => b.Id, StringComparer.Ordinal);
        foreach (var outcome in result.Outcomes.Where(o => o.Imported))
        {
            var brief = byId[outcome.BriefId];
            var stored = _store.GetAction(brief.Id)!;

            // Kind contract: the authored kindHint, or Skill when absent.
            var expectedKind = brief.KindHint ?? ActionKind.Skill;
            Assert.Equal(expectedKind, stored.Kind);

            // Cost contract: the template row for (kind, category) — kind rows for Basic/Innate,
            // the category row otherwise.
            Assert.True(ActionCategories.TryParse(brief.Category, out var category), brief.Id);
            var expectedRow = template.ResolveFor(expectedKind, category);
            var cost = _store.ListCosts(brief.Id).Single();
            Assert.Equal(expectedRow.ResourceId, cost.ResourceId);
        }
    }

    /// <summary>ST1 spec test 7: re-import over a store holding the pre-window container bumps its
    /// revision (not a no-op, not a failure). The store is seeded with a real pre-ST1 row first: the
    /// same action id persisted with a container that carries no tier window (MinTier/MaxTier null) and
    /// a t3 atom — data the current composer could never produce for rung 1's [1,1] window. Re-import
    /// must then write the stamped container and bump the revision, and report success.</summary>
    [Fact]
    public void ReimportOverAPreWindowContainerBumpsItsRevision()
    {
        var brief = Brief();
        var preWindowContainer = new ContainerRow
        {
            ContainerId = "skill.action-import-test-001",
            Kind = ContainerKind.Skill,
            PrefixRolls = 0,
            SuffixRolls = 0,
            Atoms = new[] { new ContainerAtomRow(0, AtomRow.DeriveId("atom.import-test", "", 2)) },
        };
        Assert.True(_store.UpsertContainer(preWindowContainer).IsOk);
        var preWindowRow = new ActionRow
        {
            ActionId = brief.Id,
            Name = brief.Name,
            Kind = ActionKind.Skill,
            Rung = 1,
            RungBand = new RungBand(1, 1),
            Enabled = true,
            Grantable = true,
            ContainerId = preWindowContainer.ContainerId,
            Category = ActionCategory.Attack,
            Scope = EligibilityScope.General,
        };
        Assert.True(_store.UpsertAction(preWindowRow).IsOk);
        Assert.True(_store.UpsertCost(new ActionCostRow(brief.Id, "qi", ValueSpec.Of(20), ActionCostTiming.OnCommit)).IsOk);
        var actionRevisionBefore = _store.GetAction(brief.Id)!.Revision;
        var containerRevisionBefore = _store.GetContainer(preWindowContainer.ContainerId)!.Revision;
        Assert.Null(_store.GetContainer(preWindowContainer.ContainerId)!.MinTier);

        var result = ActionCorpusImporter.Import(_store, new[] { brief }, CostTemplate(), RungPolicy.Table);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.RejectedCount);
        var stored = _store.GetAction(brief.Id)!;
        Assert.True(stored.Revision > actionRevisionBefore);
        var container = _store.GetContainer(preWindowContainer.ContainerId)!;
        Assert.True(container.Revision > containerRevisionBefore);
        Assert.Equal(1, container.MinTier);
        Assert.Equal(1, container.MaxTier);
        Assert.True(result.Outcomes.Single(o => o.BriefId == brief.Id).Imported);
    }

    // ---- A29 (action-corpus-import-completion): all four committed rounds + the cross-file guard ----

    /// <summary>T65's schema-compat finding, proven in C# rather than only read in Python: the two
    /// newer rounds parse and reconcile through the real production path exactly like round-1/2 —
    /// every parsed brief gets exactly one outcome, whatever the corpus's current size (a population,
    /// never pinned, per this repo's own guardrail-vs-population rule).</summary>
    [Fact]
    public void AllFourCommittedRoundsParseAndReconcileThroughTheRealImporter()
    {
        SeedRealAtomFile("data/seed/atoms/generated/family-expand.g-life.json");
        var briefs = new List<ActionCorpusBrief>();
        foreach (var f in new[] { "committed-round-1.json", "committed-round-2.json", "committed-round-909.json", "committed-round-2000.json" })
            briefs.AddRange(ActionCorpusBriefJson.Parse(File.ReadAllText(ContentPath("data", "seed", "actions", f))));
        Assert.NotEmpty(briefs); // liveness only

        var result = ActionCorpusImporter.Import(_store, briefs, RealCostTemplate(), RungPolicy.Table);

        Assert.Equal(briefs.Count, result.Outcomes.Count);
        Assert.Equal(briefs.Count, result.ImportedCount + result.RejectedCount);
        Assert.True(result.ImportedCount > 0, "at least one brief across all four files must import for this contract to be exercised");

        // Re-import (idempotency, A29's own acceptance line): zero duplicate rows -- every stored
        // action's revision is unchanged on the second pass.
        var revisionsAfterFirst = briefs.ToDictionary(b => b.Id, b => _store.GetAction(b.Id)?.Revision);
        var second = ActionCorpusImporter.Import(_store, briefs, RealCostTemplate(), RungPolicy.Table);
        Assert.Equal(result.ImportedCount, second.ImportedCount);
        foreach (var b in briefs)
            Assert.Equal(revisionsAfterFirst[b.Id], _store.GetAction(b.Id)?.Revision);
    }

    /// <summary>The real four committed-round files, read directly: zero ids collide across files
    /// today (verified 2026-09-19 while scoping T66 — 180 unique ids, no overlap). This test proves the
    /// contract stays true through the real guard, not just by reading the files with a script.</summary>
    [Fact]
    public void TheRealCommittedRoundFilesHaveNoCrossFileIdCollisionsToday()
    {
        var byFile = new Dictionary<string, IReadOnlyList<ActionCorpusBrief>>();
        foreach (var f in new[] { "committed-round-1.json", "committed-round-2.json", "committed-round-909.json", "committed-round-2000.json" })
            byFile[f] = ActionCorpusBriefJson.Parse(File.ReadAllText(ContentPath("data", "seed", "actions", f)));

        var ex = Record.Exception(() => ActionCorpusImporter.AssertNoCrossFileIdCollisions(byFile));

        Assert.Null(ex);
    }

    [Fact]
    public void SameIdSamePayloadAcrossTwoFilesIsAllowed()
    {
        var brief = Brief("action.cross-file.same.001");
        var byFile = new Dictionary<string, IReadOnlyList<ActionCorpusBrief>>
        {
            ["file-a.json"] = new[] { brief },
            // A separately-constructed instance with an equal-but-not-reference-equal AtomFamilies
            // array — proves the comparison is a deep value check, not `==`/reference equality.
            ["file-b.json"] = new[] { brief with { AtomFamilies = new[] { "atom.import-test" } } },
        };

        var ex = Record.Exception(() => ActionCorpusImporter.AssertNoCrossFileIdCollisions(byFile));

        Assert.Null(ex);
    }

    [Fact]
    public void SameIdDifferentPayloadAcrossTwoFilesRejectsLoudlyNamingIdAndBothFiles()
    {
        var a = Brief("action.cross-file.diff.001");
        var b = a with { Category = "defense" }; // same id, different payload
        var byFile = new Dictionary<string, IReadOnlyList<ActionCorpusBrief>>
        {
            ["round-a.json"] = new[] { a },
            ["round-b.json"] = new[] { b },
        };

        var ex = Assert.Throws<ActionCorpusCrossFileCollisionException>(
            () => ActionCorpusImporter.AssertNoCrossFileIdCollisions(byFile));

        Assert.Contains("action.cross-file.diff.001", ex.Message, StringComparison.Ordinal);
        Assert.Contains("round-a.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("round-b.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APreviouslyImportedBriefThatNowRefusesIsDisabledReportedAndAbsentFromTheCatalog()
    {
        var first = ActionCorpusImporter.Import(_store, new[] { Brief() }, CostTemplate(), RungPolicy.Table);
        Assert.Equal(1, first.ImportedCount);
        var revisionAfterFirst = _store.GetAction("action.import.test.001")!.Revision;

        // A rung table whose rung-1 window excludes the only seeded tier: the same brief now refuses.
        var rows = RungPolicy.Table.Rows.Select(r =>
            r.Rung == 1 ? r with { MinTier = 5, MaxTier = 5 } : r).ToList();
        var narrowTable = new RungTable(RungPolicy.Table.Cap, rows);

        var second = ActionCorpusImporter.Import(_store, new[] { Brief() }, CostTemplate(), narrowTable);
        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(1, second.RejectedCount);
        Assert.Equal(1, second.DisabledCount);
        var disabled = second.Outcomes.Single(o => o.BriefId == "action.import.test.001");
        Assert.True(disabled.Disabled);
        Assert.Contains("window [5,5]", disabled.Rejection, StringComparison.Ordinal);

        var stored = _store.GetAction("action.import.test.001")!;
        Assert.False(stored.Enabled);
        Assert.True(stored.Revision > revisionAfterFirst);

        var catalog = _store.BuildActionCatalog(narrowTable);
        Assert.Null(catalog.Get("action.import.test.001"));
    }
}
