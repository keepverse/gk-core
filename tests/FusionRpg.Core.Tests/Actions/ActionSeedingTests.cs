using System.Linq;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T31 (action-todo.md, spec-action-seeding.md): the runtime generator. "The generator already
/// exists" for the atom half — <see cref="Instantiator.Draw"/> is reused verbatim (visibility widened,
/// nothing reimplemented); this file proves the NEW action-specific layer around it: the target-shape
/// roll, name-template composition, the <c>sharePermille</c> reject-not-default table, and that
/// determinism/group-exclusion survive being wrapped.
///
/// <para><b>Scope, decided by reading the todo's own acceptance line rather than the full spec's wider
/// design ambition:</b> per-creature-type category/element weight vectors (§3) and enabler/payoff pairing
/// (§5, T32's own separate item) are NOT built here — T31's acceptance line names determinism, share
/// rejection, group exclusion, and the area/board gate, and that is what this file proves.</para>
/// </summary>
public class ActionSeedingTests
{
    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static ActionSeedingTests()
    {
        void Add(string family, string variant = "") =>
            Catalog[AtomRow.DeriveId(family, variant, 1)] = new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, variant, 1), KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = 1,
                ParamsJson = "{}",
            };

        Add("atom.strike");
        Add("atom.fireball");
        Add("atom.poison-rider");
        Add("atom.elemental-power", "fire");
        Add("atom.elemental-power", "ice");
    }

    static AtomRow? Lookup(string id) => Catalog.TryGetValue(id, out var a) ? a : null;
    static string Id(string family, string variant = "") => AtomRow.DeriveId(family, variant, 1);

    // T3.1 (affix-schema): the pool now draws affix ids, not bare atom ids. `affix-library`
    // (module 3, not yet built) will generate a single-ref affix 1:1 for every atom for real; this
    // fixture simulates exactly that shape — one affix per atom, same id, so every existing
    // ContainerPoolRow(Id(...), weight) fixture below keeps meaning "draw this one atom."
    static AffixRow? LookupAffix(string id) =>
        Catalog.TryGetValue(id, out var atom) ? new AffixRow(id, AffixClass.Prefix, new[] { new AffixRefRow(1, atom.AtomId) }) : null;

    static ContainerRow Container(IEnumerable<ContainerPoolRow> pool, int prefixRolls) => new()
    {
        ContainerId = "item.generated-test",
        Kind = ContainerKind.Item,
        PrefixRolls = prefixRolls,
        Pool = pool.ToList(),
    };

    static ActionNameTemplates NameTemplates() => ActionNameTemplates.Parse("""
        {
          "base": { "atom.strike": "Strike", "atom.fireball": "Fireball", "atom.elemental-power": "Elemental Power" },
          "modifiers": { "atom.poison-rider": "Venomous {name}" }
        }
        """);

    static readonly ActionTargetSpec SingleSpec = new() { Mode = ActionTargetMode.Single };
    static readonly ActionTargetSpec AreaSpec = new() { Mode = ActionTargetMode.Area, Shape = ActionAreaShape.Square, Size = 1 };

    static IReadOnlyList<WeightedOption<ActionTargetSpec>> ShapePool() => new[]
    {
        new WeightedOption<ActionTargetSpec>(SingleSpec, 500),
        new WeightedOption<ActionTargetSpec>(AreaSpec, 500),
    };

    // ---- determinism ---------------------------------------------------------------------------

    [Fact]
    public void TheSameSeedProducesByteIdenticalGenerationsTwice()
    {
        var container = Container(new[] { new ContainerPoolRow(Id("atom.strike"), 1000) }, prefixRolls: 1);

        var a = ActionSeeder.Generate(container, Lookup, LookupAffix, rollSeed: 4242, ShapePool(), boardAvailable: true, NameTemplates());
        var b = ActionSeeder.Generate(container, Lookup, LookupAffix, rollSeed: 4242, ShapePool(), boardAvailable: true, NameTemplates());

        Assert.Equal(a.AtomIds, b.AtomIds);
        Assert.Equal(a.Targeting, b.Targeting);
        Assert.Equal(a.Name, b.Name);
    }

    [Fact]
    public void DifferentSeedsCanProduceDifferentShapePicks()
    {
        var container = Container(new[] { new ContainerPoolRow(Id("atom.strike"), 1000) }, prefixRolls: 1);
        var modes = new HashSet<ActionTargetMode>();

        for (long seed = 1; seed <= 50; seed++)
            modes.Add(ActionSeeder.Generate(container, Lookup, LookupAffix, seed, ShapePool(), boardAvailable: true, NameTemplates()).Targeting.Mode);

        Assert.Contains(ActionTargetMode.Single, modes);
        Assert.Contains(ActionTargetMode.Area, modes); // proves the pool is genuinely live, not stuck on one branch
    }

    // ---- group exclusion survives the wrapper --------------------------------------------------

    [Fact]
    public void AtMostOneAtomFromASharedGroupIsEverDrawnThroughTheSeeder()
    {
        var pool = new[]
        {
            new ContainerPoolRow(Id("atom.elemental-power", "fire"), 500, Group: "elemental-power"),
            new ContainerPoolRow(Id("atom.elemental-power", "ice"), 500, Group: "elemental-power"),
            new ContainerPoolRow(Id("atom.strike"), 500, Group: "strike"),
        };
        var container = Container(pool, prefixRolls: 2); // enough rolls that, absent group exclusion, both elemental variants could land
        var namesEitherAsBaseOrRider = ActionNameTemplates.Parse("""
            {
              "base": { "atom.strike": "Strike", "atom.elemental-power": "Elemental Power" },
              "modifiers": { "atom.strike": "{name} of Striking", "atom.elemental-power": "{name} of the Elements" }
            }
            """);

        for (long seed = 1; seed <= 200; seed++)
        {
            var seeded = ActionSeeder.Generate(container, Lookup, LookupAffix, seed, ShapePool(), boardAvailable: true, namesEitherAsBaseOrRider);
            var elementalCount = seeded.AtomIds.Count(id => id.StartsWith("atom.elemental-power", StringComparison.Ordinal));
            Assert.True(elementalCount <= 1, $"seed {seed} drew {elementalCount} elemental-power atoms — group exclusion did not survive the wrapper");
        }
    }

    // ---- the shape pool is board-gated -----------------------------------------------------------

    [Fact]
    public void AreaIsNeverRolledWithNoBoardEvenWhenHeavilyWeighted()
    {
        var container = Container(new[] { new ContainerPoolRow(Id("atom.strike"), 1000) }, prefixRolls: 1);
        var heavilyAreaWeighted = new[]
        {
            new WeightedOption<ActionTargetSpec>(SingleSpec, 1),
            new WeightedOption<ActionTargetSpec>(AreaSpec, 999),
        };

        for (long seed = 1; seed <= 100; seed++)
        {
            var seeded = ActionSeeder.Generate(container, Lookup, LookupAffix, seed, heavilyAreaWeighted, boardAvailable: false, NameTemplates());
            Assert.NotEqual(ActionTargetMode.Area, seeded.Targeting.Mode);
        }
    }

    [Fact]
    public void AllCandidatesExcludedLeavesNothingDrawableAndThrowsRatherThanSilentlyPickingArea()
    {
        var container = Container(new[] { new ContainerPoolRow(Id("atom.strike"), 1000) }, prefixRolls: 1);
        var onlyArea = new[] { new WeightedOption<ActionTargetSpec>(AreaSpec, 1000) };

        Assert.Throws<NoDrawableWeightedOptionException>(() =>
            ActionSeeder.Generate(container, Lookup, LookupAffix, 1, onlyArea, boardAvailable: false, NameTemplates()));
    }

    /// <summary>Closes the loop the spec's own acceptance line names: even if a caller bypassed the
    /// pool gate (a hand-authored row, or a bug), the EXISTING T30 bind-time check still refuses an
    /// Area action with no board — this is not new machinery, it is proof the two gates compose.</summary>
    [Fact]
    public void AnAreaActionThatBypassedThePoolGateIsStillRejectedAtBindTime()
    {
        var row = new ActionRow
        {
            ActionId = "skill.generated-area",
            Kind = ActionKind.Skill,
            ContainerId = "item.generated-test",
            Rung = 1,
            Envelope = ActionEnvelope.NoOp with { ActionId = "skill.generated-area" },
            Targeting = AreaSpec, // as if a generator bug handed this straight to the compiler
        };
        var table = new RungTable(1, new[] { new RungRow(1, 1, 1, 1, 1000, 1000, 1000, Array.Empty<string>()) });

        var (rejection, compiled) = ActionCompiler.Compile(
            row, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(),
            new HashSet<string> { Id("atom.strike") }, boardAvailable: false, table);

        Assert.Null(compiled);
        Assert.Equal(ActionRejectionReason.AreaRequiresBoard, rejection.Reason);
    }

    // ---- name templates --------------------------------------------------------------------------

    [Fact]
    public void ANameComposesFromTheBaseAtomWithNoRiders()
    {
        Assert.Equal("Strike", NameTemplates().Compose(new[] { "atom.strike" }));
    }

    [Fact]
    public void ARiderWrapsTheBaseNameInPickOrder()
    {
        Assert.Equal("Venomous Strike", NameTemplates().Compose(new[] { "atom.strike", "atom.poison-rider" }));
    }

    [Fact]
    public void AnUnauthoredBaseFamilyRejectsRatherThanComposingAFallback() =>
        Assert.Throws<ActionNameTemplateRejection>(() => NameTemplates().Compose(new[] { "atom.unknown-family" }));

    [Fact]
    public void AnUnauthoredRiderFamilyRejectsRatherThanComposingAFallback() =>
        Assert.Throws<ActionNameTemplateRejection>(() => NameTemplates().Compose(new[] { "atom.strike", "atom.unknown-rider" }));

    [Fact]
    public void ZeroAtomsRejectsRatherThanComposingAnEmptyName() =>
        Assert.Throws<ActionNameTemplateRejection>(() => NameTemplates().Compose(Array.Empty<string>()));

    [Fact]
    public void AModifierTemplateWithNoPlaceholderIsRejectedAtParseTime() =>
        Assert.Throws<ActionNameTemplateRejection>(() => ActionNameTemplates.Parse("""
            { "base": {"atom.strike":"Strike"}, "modifiers": {"atom.broken-rider":"no placeholder here"} }
            """));

    // ---- sharePermille: reject, never default ------------------------------------------------

    [Fact]
    public void AKnownChannelReturnsItsExactAuthoredPermille()
    {
        var table = ActionShareTable.Parse("""{ "atom.strike": 750 }""");
        Assert.Equal(750, table.PermilleOf("atom.strike"));
    }

    [Fact]
    public void AnUnauthoredChannelRejectsRatherThanDefaulting()
    {
        var table = ActionShareTable.Parse("""{ "atom.strike": 750 }""");
        var ex = Assert.Throws<UnsharedChannelException>(() => table.PermilleOf("atom.never-authored"));
        Assert.Contains("atom.never-authored", ex.Message);
    }

    [Fact]
    public void HasChannelDistinguishesAuthoredFromUnauthoredWithoutThrowing()
    {
        var table = ActionShareTable.Parse("""{ "atom.strike": 750 }""");
        Assert.True(table.HasChannel("atom.strike"));
        Assert.False(table.HasChannel("atom.never-authored"));
    }

    [Fact]
    public void ANegativePermilleIsRejectedAtLoad() =>
        Assert.Throws<ActionShareRejection>(() => ActionShareTable.Parse("""{ "atom.strike": -1 }"""));

    [Fact]
    public void TheShippedShareFileLoadsAndCoversItsOwnDeclaredChannels()
    {
        var path = ShippedFilePath(new[] { "data", "tuning", "action-shares.v1.json" });
        var table = ActionShareTable.Parse(File.ReadAllText(path));
        Assert.True(table.HasChannel("atom.strike"));
    }

    // ---- WeightedChoice itself ----------------------------------------------------------------

    [Fact]
    public void AllZeroOrNegativeWeightsThrowsRatherThanPickingTheFirstOption()
    {
        var options = new[] { new WeightedOption<string>("a", 0), new WeightedOption<string>("b", -5) };
        Assert.Throws<NoDrawableWeightedOptionException>(() => WeightedChoice.Pick(options, 1, "test"));
    }

    [Fact]
    public void AnEmptyPoolThrows() =>
        Assert.Throws<NoDrawableWeightedOptionException>(() => WeightedChoice.Pick(Array.Empty<WeightedOption<string>>(), 1, "test"));

    [Fact]
    public void TheSameSeedAndStreamAlwaysPicksTheSameOption()
    {
        var options = new[] { new WeightedOption<string>("a", 300), new WeightedOption<string>("b", 700) };
        var first = WeightedChoice.Pick(options, 999, "stream-x");
        var second = WeightedChoice.Pick(options, 999, "stream-x");
        Assert.Equal(first, second);
    }

    static string ShippedFilePath(string[] relative, [CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(new[] { repo }.Concat(relative).ToArray());
    }
}
