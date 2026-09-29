using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Generation;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// Two comparison defects found live on 2026-09-06 while building the card/compare routes, both
/// fixed the same day. Everything below runs on the <b>real</b> shipped affix corpus expanded through
/// the real <see cref="FamilyExpansion"/>, because both defects were invisible against a synthetic
/// atom and only appeared against the content the game actually ships.
///
/// <list type="number">
/// <item><description><b>Every <c>onApply</c> band read as 0.</b> <c>ArmouryCompare</c> took
/// <c>amount</c> only when it was a JSON number; <c>Instantiator.Freeze</c> deliberately copies an
/// <c>OnApply</c> spec through as <c>{min,max,roll}</c> and E43 authors that for EVERY family, so the
/// whole delta table was zeros while the card beside it rendered the same atoms as
/// <c>125–249 increased attack</c>.</description></item>
/// <item><description><b><c>ChannelDelta.Unit</c> disagreed with its group header.</b> The delta
/// labelled the OP (<c>per-mille</c> / <c>game-units</c>); <c>GroupByUnitClass</c> labels the CHANNEL
/// (<c>ChannelUnits.For</c>). <c>maxHp</c> came back <c>per-mille</c> inside a <c>GameUnits</c>
/// group.</description></item>
/// </list>
/// </summary>
public class ArmouryCompareBandAndUnitTests
{
    static string FindDataDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "data");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "data"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("could not locate data/ above " + AppContext.BaseDirectory);
    }

    static long? FlatReferenceBase(string channel) => channel switch
    {
        "maxHp" or "hp" => BattleRuleset.BaseHp(FamilyExpansion.ReferenceLevel),
        "atk" => BattleRuleset.BaseAtk(FamilyExpansion.ReferenceLevel),
        "defense" => BattleRuleset.BaseDefense(FamilyExpansion.ReferenceLevel),
        _ => null,
    };

    /// <summary>The real corpus, expanded once. 98 families → the shipped atom rows.</summary>
    static readonly Lazy<IReadOnlyList<AtomRow>> RealAtoms = new(() =>
    {
        var itemsRoot = Path.Combine(FindDataDir(), "seed", "items");
        var tierBands = TierBandsFile.Read(
            File.ReadAllText(Path.Combine(itemsRoot, "_tuning", "tier-bands.v1.json")));

        var families = new List<FamilyEntryInput>();
        foreach (var file in Directory.GetFiles(Path.Combine(itemsRoot, "affix-families"), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            families.AddRange(AffixFamilyFile.Read(Path.GetFileName(file), File.ReadAllText(file)));
        }

        return FamilyExpansion.Expand(families, tierBands, FlatReferenceBase).Rows;
    });

    /// <summary>
    /// One frozen instance atom for a real row. <c>Instantiator.Freeze</c> copies an <c>OnApply</c>
    /// value spec through <b>as authored</b> (the hit rolls it, not the item), so for these rows the
    /// frozen <c>values_json</c> is the authored <c>params_json</c> — which is exactly why the old
    /// number-only reader saw nothing.
    /// </summary>
    static CompareAtom Frozen(AtomRow atom) => new(atom, atom.ParamsJson);

    static AtomRow FirstOn(string channel) =>
        RealAtoms.Value.First(a => a.KindId == "stat.modify" && ChannelOf(a) == channel);

    static AtomRow SecondOn(string channel) =>
        RealAtoms.Value.Where(a => a.KindId == "stat.modify" && ChannelOf(a) == channel).Skip(1).First();

    static AtomRow ByFamily(string familyId) =>
        RealAtoms.Value.First(a => a.FamilyId == familyId);

    static string? ChannelOf(AtomRow atom)
    {
        using var doc = JsonDocument.Parse(atom.ParamsJson);
        return doc.RootElement.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
    }

    static (long Min, long Max) BandOf(AtomRow atom)
    {
        using var doc = JsonDocument.Parse(atom.ParamsJson);
        var amount = doc.RootElement.GetProperty("amount");
        Assert.True(AtomJson.TryReadValueSpec(amount, out var spec).IsOk);
        return (spec.Min, spec.Max);
    }

    // ---- the content is authored: this is a code defect, not a content gap ------------------------

    [Fact]
    public void The_shipped_corpus_really_does_author_an_onApply_band_on_every_generated_affix()
    {
        // Establishes which of the two possible diagnoses is true. If the corpus authored nothing
        // there would be no number to read and no code fix; it authors {min,max,roll:"onApply"} on
        // every row, so reading 0 was the reader's defect.
        var rows = RealAtoms.Value;
        Assert.NotEmpty(rows);

        foreach (var atom in rows)
        {
            using var doc = JsonDocument.Parse(atom.ParamsJson);
            Assert.True(doc.RootElement.TryGetProperty("amount", out var amount), atom.AtomId);
            Assert.Equal(JsonValueKind.Object, amount.ValueKind);
            Assert.True(AtomJson.TryReadValueSpec(amount, out var spec).IsOk, atom.AtomId);
            Assert.Equal(RollPolicy.OnApply, spec.Roll);
        }
    }

    // ---- defect 1: the band is no longer read as zero ----------------------------------------------

    [Fact]
    public void An_onApply_band_reports_its_real_magnitude_and_carries_both_bounds()
    {
        var low = FirstOn("atk");
        var high = SecondOn("atk");
        var (lowMin, lowMax) = BandOf(low);
        var (highMin, highMax) = BandOf(high);

        var result = ArmouryCompare.Compare(new[] { Frozen(low) }, new[] { Frozen(high) });

        var delta = Assert.Single(result.Deltas);
        Assert.Equal("atk", delta.Channel);

        // The whole point: not zero. The scalar column takes the band's minimum -- the same bound
        // ItemCard.Magnitude treats as the line's value -- and the top of each band rides along so
        // nothing the corpus authored is dropped.
        Assert.Equal(lowMin, delta.Incumbent);
        Assert.Equal(highMin, delta.Candidate);
        Assert.Equal(highMin - lowMin, delta.Delta);
        Assert.Equal(lowMax, delta.IncumbentMax);
        Assert.Equal(highMax, delta.CandidateMax);
        Assert.NotEqual(0, delta.Incumbent);
        Assert.NotEqual(0, delta.Candidate);
    }

    [Fact]
    public void A_point_value_carries_no_band_so_a_client_can_tell_the_two_apart()
    {
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.point", "", 1), KindId = "stat.modify",
            FamilyId = "atom.point", Variant = "", Tier = 1, Name = "point",
            ParamsJson = """{"channel":"atk","op":"flat","amount":5}""",
        };
        var frozen = new CompareAtom(atom, """{"channel":"atk","op":"flat","amount":5}""");

        var delta = Assert.Single(ArmouryCompare.Compare(Array.Empty<CompareAtom>(), new[] { frozen }).Deltas);

        Assert.Equal(5, delta.Candidate);
        Assert.Null(delta.CandidateMax);
        Assert.Null(delta.IncumbentMax);
    }

    [Fact]
    public void Two_bands_on_one_channel_add_bound_wise()
    {
        var a = FirstOn("atk");
        var b = SecondOn("atk");
        var (aMin, aMax) = BandOf(a);
        var (bMin, bMax) = BandOf(b);

        var delta = Assert.Single(
            ArmouryCompare.Compare(Array.Empty<CompareAtom>(), new[] { Frozen(a), Frozen(b) }).Deltas);

        Assert.Equal(aMin + bMin, delta.Candidate);
        Assert.Equal(aMax + bMax, delta.CandidateMax);
    }

    // ---- defect 2: one unit answer, not two --------------------------------------------------------

    [Fact]
    public void MaxHp_is_labelled_GameUnits_by_the_delta_and_by_its_group_header()
    {
        // The exact case the live run reported: `per-mille` on the delta, `GameUnits` on the header.
        // `atom.fortitude` is the real shipped maxHp family whose op is `Increased` — the op the old
        // code turned into a `per-mille` label, which is precisely what disagreed with the group.
        var registry = DerivedStatRegistry.CreateDefault();
        var hp = ByFamily("atom.fortitude");
        Assert.Equal("maxHp", ChannelOf(hp));

        var result = ArmouryCompare.Compare(Array.Empty<CompareAtom>(), new[] { Frozen(hp) }, registry);
        var delta = Assert.Single(result.Deltas);

        Assert.Equal(UnitClass.GameUnits, delta.Unit);
        Assert.Equal(ChannelUnits.For("maxHp", registry), delta.Unit);

        var group = Assert.Single(DominancePresentation.GroupByUnitClass(result.Deltas, registry));
        Assert.Equal(group.Unit, delta.Unit);
    }

    [Fact]
    public void Every_delta_agrees_with_the_group_header_it_lands_in_across_the_real_corpus()
    {
        var registry = DerivedStatRegistry.CreateDefault();

        // One atom per distinct channel the shipped stat.modify corpus touches -- a matrix, not a
        // hand-picked pair, so a channel family whose unit resolves differently cannot hide.
        var atoms = RealAtoms.Value
            .Where(a => a.KindId == "stat.modify" && ChannelOf(a) is { Length: > 0 })
            .GroupBy(a => ChannelOf(a)!, StringComparer.Ordinal)
            .Select(g => Frozen(g.First()))
            .ToList();
        Assert.True(atoms.Count > 1, "the real corpus should touch more than one channel");

        var result = ArmouryCompare.Compare(Array.Empty<CompareAtom>(), atoms, registry);
        var groups = DominancePresentation.GroupByUnitClass(result.Deltas, registry);

        Assert.Equal(result.Deltas.Count, groups.Sum(g => g.Deltas.Count));
        foreach (var group in groups)
        foreach (var delta in group.Deltas)
            Assert.Equal(group.Unit, delta.Unit);
    }
}
