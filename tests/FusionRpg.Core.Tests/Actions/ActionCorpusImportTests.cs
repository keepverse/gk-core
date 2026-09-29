using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T59.3 (spec-action-instance-and-grant.md §1, corrected during BUILD): brief → concrete row,
/// against hand-built brief fixtures — never the live corpus file (T59.5's own job, against real
/// content). Uses the real, already-configured `RungPolicy.Table` (`ContractTuningTestBootstrap`)
/// so a second, private rung curve cannot creep in unnoticed, matching `ActionTimingTests.cs`'s own
/// established convention.
/// </summary>
public class ActionCorpusImportTests
{
    static AtomRow Atom(string family, int tier, string? whenJson = null) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", tier),
        KindId = "stat.modify",
        FamilyId = family,
        Variant = "",
        Tier = tier,
        Name = family,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        WhenJson = whenJson,
    };

    sealed class Catalog
    {
        readonly Dictionary<string, List<AtomRow>> _byFamily = new(StringComparer.Ordinal);
        readonly Dictionary<string, AtomRow> _byId = new(StringComparer.Ordinal);

        public Catalog Add(AtomRow atom)
        {
            if (!_byFamily.TryGetValue(atom.FamilyId, out var list))
                _byFamily[atom.FamilyId] = list = new List<AtomRow>();
            list.Add(atom);
            _byId[atom.AtomId] = atom;
            return this;
        }

        public IReadOnlyList<AtomRow> AtomsInFamily(string family) =>
            _byFamily.TryGetValue(family, out var list) ? list : Array.Empty<AtomRow>();

        public AtomRow? LookupAtom(string id) => _byId.TryGetValue(id, out var a) ? a : null;
    }

    static ActionCorpusCostTemplate FullCostTemplate() => new(
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

    static ActionCorpusBrief SimpleBrief(string id = "action.family.test.001") => new(
        Id: id, Name: "Test Volley", Category: "attack", Scope: "family", ScopeKey: "cactus",
        RungFloor: 1, RungCeiling: 3, AtomFamilies: new[] { "atom.test-family" },
        TargetMode: "single", Relation: "enemy");

    static Catalog SimpleCatalog() => new Catalog()
        .Add(Atom("atom.test-family", tier: 1))
        .Add(Atom("atom.test-family", tier: 2))
        .Add(Atom("atom.test-family", tier: 3))
        .Add(Atom("atom.test-family", tier: 4))
        .Add(Atom("atom.test-family", tier: 5));

    [Fact]
    public void SameBriefComposedTwiceIsByteIdentical()
    {
        var brief = SimpleBrief();
        var catalog = SimpleCatalog();
        var template = FullCostTemplate();

        var first = ActionCorpusComposer.Compose(brief, template, RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);
        var second = ActionCorpusComposer.Compose(brief, template, RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(first.Row, second.Row);
        // ContainerRow has no hand-rolled Equals (unlike ActionEnvelope's own, which exists for
        // exactly this reason) -- its record-default Equals compares Atoms/Pool by REFERENCE, so two
        // structurally-identical but separately-built lists read as unequal. Assert.Equal's own
        // IEnumerable<T> overload does a real sequence comparison instead.
        Assert.Equal(first.Container.ContainerId, second.Container.ContainerId);
        Assert.Equal(first.Container.Kind, second.Container.Kind);
        Assert.Equal(first.Container.PrefixRolls, second.Container.PrefixRolls);
        Assert.Equal(first.Container.SuffixRolls, second.Container.SuffixRolls);
        Assert.Equal(first.Container.Atoms, second.Container.Atoms);
        Assert.Equal(first.Container.Pool, second.Container.Pool);
        Assert.Equal(first.Costs.Count, second.Costs.Count);
        Assert.Equal(first.Costs[0], second.Costs[0]);
    }

    [Fact]
    public void RungEqualsTheRungBandsCeiling()
    {
        var brief = SimpleBrief() with { RungFloor = 2, RungCeiling = 5 };
        var catalog = SimpleCatalog();

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(5, result.Row.Rung);
        Assert.Equal(new RungBand(2, 5), result.Row.RungBand);
    }

    [Fact]
    public void TheComposedRowCarriesEveryBriefFieldByteForByte()
    {
        var brief = SimpleBrief();
        var catalog = SimpleCatalog();

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(brief.Id, result.Row.ActionId);
        Assert.Equal(brief.Name, result.Row.Name);
        Assert.Equal(ActionCategory.Attack, result.Row.Category);
        Assert.Equal(EligibilityScope.Family, result.Row.Scope);
        Assert.Equal("cactus", result.Row.ScopeKey);
        Assert.Equal(ActionTargetMode.Single, result.Row.Targeting.Mode);
        Assert.Equal(ActionRelation.Enemy, result.Row.Targeting.Relation);
        Assert.NotEmpty(result.Container.Atoms);
        Assert.Empty(result.Container.Pool); // discarded after the one roll -- never re-rollable
        Assert.Equal(0, result.Container.PrefixRolls);
        Assert.Equal(0, result.Container.SuffixRolls);
    }

    /// <summary>The channel mapping T59.1 built actually reaches the composed row.</summary>
    [Fact]
    public void TheComposedEnvelopeCarriesTheCategorysChannels()
    {
        var brief = SimpleBrief();
        var catalog = SimpleCatalog();

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(ActionCategoryChannels.CooldownChannelFor(ActionCategory.Attack), result.Row.Envelope.CooldownChannel);
        Assert.Equal(ActionCategoryChannels.EffectivenessChannelFor(ActionCategory.Attack), result.Row.Envelope.EffectivenessChannel);
    }

    [Fact]
    public void AnUnknownCategoryRejectsNamingIt()
    {
        var brief = SimpleBrief() with { Category = "not-a-real-category" };
        var catalog = SimpleCatalog();

        var ex = Assert.Throws<ActionCorpusComposeRejection>(() =>
            ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom));
        Assert.Contains("not-a-real-category", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACategoryMissingFromTheCostTemplateRejectsNamingIt()
    {
        var brief = SimpleBrief() with { Category = "support" };
        var catalog = SimpleCatalog();
        var incompleteTemplate = new ActionCorpusCostTemplate(new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>
        {
            [ActionCategory.Attack] = new("qi", 20, ActionCostTiming.OnCommit),
            // Support deliberately missing.
        });

        var ex = Assert.Throws<ActionCorpusCostTemplateRejection>(() =>
            ActionCorpusComposer.Compose(brief, incompleteTemplate, RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom));
        Assert.Contains("Support", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyThatResolvesNoAtomsRejectsNamingTheBrief()
    {
        var brief = SimpleBrief() with { AtomFamilies = new[] { "atom.nothing-here" } };
        var catalog = SimpleCatalog(); // does not contain "atom.nothing-here"

        var ex = Assert.Throws<ActionCorpusComposeRejection>(() =>
            ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom));
        Assert.Contains(brief.Id, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A family mixing Prefix- and Suffix-class atoms (no `WhenJson` vs. a real trigger) drops
    /// the mismatched side and reports it — `UniqueContainerBuild`'s own policy, reused, not
    /// reinvented — rather than silently shrinking the pool or throwing.</summary>
    [Fact]
    public void AMixedClassFamilyDropsTheMismatchedSideAndReportsIt()
    {
        var catalog = new Catalog()
            .Add(Atom("atom.mixed", tier: 1)) // no WhenJson -> Prefix
            .Add(Atom("atom.mixed", tier: 2, whenJson: "{\"trigger\":\"onHit\"}")); // Suffix
        var brief = SimpleBrief() with { AtomFamilies = new[] { "atom.mixed" } };

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Single(result.Dropped);
        Assert.NotEmpty(result.Container.Atoms); // the surviving Prefix-side atom still drew fine
    }

    [Fact]
    public void RungBandOneAllowsRungOneWithNoStructureBudget()
    {
        var brief = SimpleBrief() with { RungFloor = 1, RungCeiling = 1 };
        var catalog = SimpleCatalog();

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(1, result.Row.Rung);
    }

    // ---- T7 (basic-attack-seed): Kind honoring + Kind-aware cost resolution ----

    /// <summary>The composer's own back-compat guarantee: a brief with no `kindHint` at all (every
    /// brief authored before this task) still composes to `Kind = Skill` and its cost still comes from
    /// `Category`, byte-for-byte the pre-T7 behaviour. All the OTHER tests in this file already prove
    /// this implicitly (none of them set `KindHint`); this one states it as an explicit contract.</summary>
    [Fact]
    public void ABriefWithNoKindHintComposesToSkillAndCostsByCategory()
    {
        var brief = SimpleBrief(); // KindHint defaults to null
        var catalog = SimpleCatalog();

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(ActionKind.Skill, result.Row.Kind);
        Assert.Equal("qi", result.Costs[0].ResourceId); // category "attack" -> qi 20, unchanged
        Assert.Equal(ValueSpec.Of(20), result.Costs[0].AmountSpec);
    }

    [Fact]
    public void ABriefWithKindHintBasicComposesToBasicAndCostsStamina()
    {
        var brief = SimpleBrief() with { KindHint = ActionKind.Basic };
        var catalog = SimpleCatalog();

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(ActionKind.Basic, result.Row.Kind);
        Assert.Equal("stamina", result.Costs[0].ResourceId);
        Assert.Equal(ValueSpec.Of(20), result.Costs[0].AmountSpec);
    }

    /// <summary>Category is "attack" here too (same as the no-hint test above), and "attack" also
    /// costs `qi` — proving the Innate row (`qi 25`), not the Category row (`qi 20`), is what actually
    /// drove this cost.</summary>
    [Fact]
    public void ABriefWithKindHintInnateComposesToInnateAndCostsItsOwnQiAmountNotTheCategorysQiAmount()
    {
        var brief = SimpleBrief() with { KindHint = ActionKind.Innate }; // Category stays "attack"
        var catalog = SimpleCatalog();

        var result = ActionCorpusComposer.Compose(brief, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(ActionKind.Innate, result.Row.Kind);
        Assert.Equal("qi", result.Costs[0].ResourceId);
        Assert.Equal(ValueSpec.Of(25), result.Costs[0].AmountSpec); // kinds.innate's 25, not attack's 20
    }

    /// <summary>A brief whose `kindHint` explicitly names `"skill"` (a real, present value — not an
    /// absent field) resolves identically to a brief with no `kindHint` at all.</summary>
    [Fact]
    public void ABriefWithKindHintExplicitlySkillResolvesTheSameAsNoKindHint()
    {
        var withHint = SimpleBrief() with { KindHint = ActionKind.Skill };
        var withoutHint = SimpleBrief();
        var catalog = SimpleCatalog();

        var a = ActionCorpusComposer.Compose(withHint, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);
        var b = ActionCorpusComposer.Compose(withoutHint, FullCostTemplate(), RungPolicy.Table, catalog.AtomsInFamily, catalog.LookupAtom);

        Assert.Equal(b.Row.Kind, a.Row.Kind);
        Assert.Equal(b.Costs[0], a.Costs[0]);
    }
}
