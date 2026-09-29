using System.Runtime.CompilerServices;
using System.Text.Json;
using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// **The corpus-wide rarity → variant-count band, enforced.**
///
/// <para>Added 2026-09-04 after two anchors slipped past every existing check.
/// <c>SpeciesExpanderTests</c> already asserted this band, but only for <b>two hand-named species</b>,
/// so a bad classification anywhere else in the corpus was invisible — and two were live at once:
/// <c>Peashooter</c> at <c>almanac</c> with <b>6</b> variants and <c>SunFlower</c> at <c>almanac</c>
/// with <b>7</b>, in a corpus where every other almanac row has exactly 4.</para>
///
/// <para>⭐ <b>The law was always in the data; nothing enforced it.</b> Across 841 rows the variant
/// count tracks the rarity rung tightly and monotonically — which is exactly what makes an outlier
/// meaningful rather than merely unusual. Stating it here means a future classification pass cannot
/// quietly break it, the same "make the implicit rule explicit" move the power ladder and the atom
/// vocabulary already use elsewhere in this repo.</para>
///
/// <para><b>The bands are measured from the corpus, not invented</b> — each is the observed
/// (min..max) for that rung after the two corrections above.</para>
/// </summary>
public class VariantCountBandTests
{
    /// <summary>
    /// The ladder, low rung to high, with each rung's observed variant-count band.
    ///
    /// <para><c>unresolved</c> is deliberately absent: it means "no rarity was classified", so there is
    /// no rung to band against. Those rows are skipped rather than silently passed — see
    /// <see cref="Unresolved_rows_are_exempt_and_that_exemption_is_bounded"/>, which stops that escape
    /// hatch from becoming a hiding place.</para>
    /// </summary>
    public static readonly (string Rarity, int Min, int Max)[] Bands =
    {
        ("chaff", 0, 0),
        ("sprout", 1, 1),
        ("grafted", 1, 2),
        ("cultivated", 1, 2),
        ("fused", 2, 3),
        ("chimeric", 2, 3),
        ("heirloom", 2, 3),
        ("firstseed", 3, 3),
        ("sunwoven", 3, 4),
        ("almanac", 4, 4),
    };

    static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                        // tests/.../Creatures
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));  // repo root
    }

    /// <summary>Every anchor row in the shipped corpus, resolved through <c>_index.json</c> — the same
    /// lookup `run-control` uses, and the same one `SpeciesExpanderTests.RealAnchor` switched to after
    /// hardcoded paths broke when the pipeline moved a species between family files.</summary>
    static List<AnchorRow> AllAnchorRows()
    {
        var speciesDir = Path.Combine(RepoRoot(), "data", "seed", "creatures", "species");
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(speciesDir, "_index.json")))!;

        var rows = new List<AnchorRow>();
        foreach (var rel in index.Values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal))
        {
            var path = Path.Combine(speciesDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            rows.AddRange(AnchorRowReader.ReadAll(File.ReadAllText(path)));
        }
        return rows;
    }

    [Fact]
    public void Every_classified_anchor_variant_count_sits_inside_its_rarity_band()
    {
        var offenders = new List<string>();
        var checkedRows = 0;

        foreach (var row in AllAnchorRows())
        {
            var band = Bands.FirstOrDefault(b => string.Equals(b.Rarity, row.Rarity, StringComparison.Ordinal));
            if (band.Rarity is null) continue;   // unresolved rung — covered by the test below

            checkedRows++;
            var n = row.Variants.Count;
            if (n < band.Min || n > band.Max)
                offenders.Add($"{row.SpeciesId}: rarity '{row.Rarity}' allows {band.Min}..{band.Max} variants, has {n}");
        }

        Assert.True(checkedRows > 500, $"the sweep must actually read the corpus — only saw {checkedRows} classified rows");
        Assert.Empty(offenders);
    }

    /// <summary>
    /// ⛔ **The exemption, bounded.** `unresolved` rows carry no rarity, so no band applies — but an
    /// unbounded exemption is how a hiding place forms. If the unresolved population grows, this
    /// guard's coverage silently shrinks, so the size of the hole is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void Unresolved_rows_are_exempt_and_that_exemption_is_bounded()
    {
        var rows = AllAnchorRows();
        var unresolved = rows.Count(r => !Bands.Any(b => string.Equals(b.Rarity, r.Rarity, StringComparison.Ordinal)));

        Assert.True(rows.Count > 500, $"the corpus should have hundreds of rows, saw {rows.Count}");
        Assert.True(unresolved * 20 < rows.Count,
            $"{unresolved} of {rows.Count} anchors have no classified rarity — the band guard now covers " +
            "too little of the corpus to mean anything. Heal the unresolved rows rather than widening this bound.");
    }

    /// <summary>
    /// The bands must stay **monotonic** — a higher rung never allows fewer variants than a lower one.
    /// That property is what makes an outlier detectable at all: without it, "6 variants" could belong
    /// to any rung and the guard above would have nothing to catch.
    /// </summary>
    [Fact]
    public void The_ladder_bands_never_go_backwards()
    {
        for (var i = 1; i < Bands.Length; i++)
        {
            Assert.True(Bands[i].Min >= Bands[i - 1].Min,
                $"{Bands[i].Rarity} allows a lower minimum than {Bands[i - 1].Rarity}");
            Assert.True(Bands[i].Max >= Bands[i - 1].Max,
                $"{Bands[i].Rarity} allows a lower maximum than {Bands[i - 1].Rarity}");
        }
    }
}
