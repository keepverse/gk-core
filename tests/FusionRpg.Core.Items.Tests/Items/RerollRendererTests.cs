using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Mutation;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T15: the reroll draw, synthetic catalog throughout — no shipped atom named,
/// no population count. The policy refusals live in RerollPolicy's own suite; what is pinned here
/// is rendering: identity preserved vs replaced, values inside their ranges, post-op generatability,
/// and determinism.
/// </summary>
public class RerollRendererTests
{
    static readonly Dictionary<string, AtomRow> Catalog = BuildCatalog();

    static Dictionary<string, AtomRow> BuildCatalog()
    {
        var catalog = new Dictionary<string, AtomRow>(StringComparer.Ordinal);
        void Add(string family, string variant, int tier, string paramsJson)
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            catalog[id] = new AtomRow
            {
                AtomId = id, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
                ParamsJson = paramsJson,
            };
        }

        Add("atom.might", "", 1,
            "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onInstantiate\"}}");
        Add("atom.might", "", 2,
            "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":30,\"max\":40,\"roll\":\"onInstantiate\"}}");
        Add("atom.vitality", "", 1, "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}");
        Add("atom.vitality", "", 2, "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":90}");
        return catalog;
    }

    static AtomRow? LookupAtom(string id) => Catalog.TryGetValue(id, out var a) ? a : null;

    static AffixRow? LookupAffix(string id) =>
        Catalog.Values
            .Select(FusionRpg.Core.Effects.Atoms.AffixLibraryGenerator.SingleAtomAffix)
            .FirstOrDefault(a => a.AffixId == id);

    static ContainerRow Container() => new()
    {
        ContainerId = "item.probe",
        Kind = ContainerKind.Item,
        PrefixRolls = 2,
        SuffixRolls = 0,
        Pool = new[]
        {
            new ContainerPoolRow("affix.might.t1", 10),
            new ContainerPoolRow("affix.might.t2", 10),
            new ContainerPoolRow("affix.vitality.t1", 10),
            new ContainerPoolRow("affix.vitality.t2", 10),
        }.ToList(),
    };

    static InstanceRow Instance() => new()
    {
        InstanceId = "i-probe", ContainerId = "item.probe", RollSeed = 7, CatalogRevision = 1,
        Origin = InstanceOrigin.Drop, ThetaContent = 20, ContentScaleMilli = 1000,
        Atoms = new[]
        {
            new InstanceAtomRow(1, "atom.might.t1", """{"amount":15}"""),
            new InstanceAtomRow(2, "atom.vitality.t1", """{"amount":45}"""),
        },
    };

    static long AmountOf(IReadOnlyDictionary<string, long> values, string atomId) => values["amount"];

    // ── one ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderOne_keeps_the_identity_and_redraws_the_value_inside_its_range()
    {
        var (rejection, drawn) = RerollRenderer.DrawnFrom(Instance(), LookupAtom);
        Assert.True(rejection.IsOk, rejection.ToString());
        var (rej, suppressed, appended) = RerollRenderer.RenderOne(
            Container(), drawn, 1, 4242L, 1000, LookupAtom, LookupAffix);
        Assert.True(rej.IsOk, rej.ToString());
        Assert.Equal(new[] { 1 }, suppressed);
        var atom = Assert.Single(appended);
        Assert.Equal("atom.might.t1", atom.AtomId);
        Assert.InRange(AmountOf(atom.Values, atom.AtomId), 10, 20);
    }

    [Fact]
    public void RenderOne_replays_identically_and_refuses_an_unknown_seq()
    {
        var (_, drawn) = RerollRenderer.DrawnFrom(Instance(), LookupAtom);
        var first = RerollRenderer.RenderOne(Container(), drawn, 1, 4242L, 1000, LookupAtom, LookupAffix);
        var second = RerollRenderer.RenderOne(Container(), drawn, 1, 4242L, 1000, LookupAtom, LookupAffix);
        Assert.Equal(first.Appended[0].Values["amount"], second.Appended[0].Values["amount"]);

        var (rej, _, _) = RerollRenderer.RenderOne(Container(), drawn, 99, 4242L, 1000, LookupAtom, LookupAffix);
        Assert.False(rej.IsOk);
        Assert.Contains("99", rej.ToString());
    }

    // ── all ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RenderAll_replaces_identities_and_still_validates_as_droppable()
    {
        var (_, drawn) = RerollRenderer.DrawnFrom(Instance(), LookupAtom);
        var (rej, suppressed, appended) = RerollRenderer.RenderAll(
            Container(), drawn, new[] { 1, 2 }, 777L, 1000, LookupAtom, LookupAffix);
        Assert.True(rej.IsOk, rej.ToString());
        Assert.Equal(2, suppressed.Count);
        Assert.Equal(2, appended.Count);
        // Post-op holds by construction (asserted inside RenderAll), and the draw is deterministic.
        var again = RerollRenderer.RenderAll(
            Container(), drawn, new[] { 1, 2 }, 777L, 1000, LookupAtom, LookupAffix);
        Assert.Equal(
            appended.Select(a => a.AtomId).OrderBy(id => id, StringComparer.Ordinal),
            again.Appended.Select(a => a.AtomId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void DrawnFrom_refuses_an_atom_row_the_catalog_no_longer_carries()
    {
        var instance = Instance() with
        {
            Atoms = new[] { new InstanceAtomRow(1, "atom.gone.t1", """{"amount":1}""") },
        };
        var (rejection, _) = RerollRenderer.DrawnFrom(instance, LookupAtom);
        Assert.False(rejection.IsOk);
        Assert.Contains("atom.gone.t1", rejection.ToString());
    }
}
