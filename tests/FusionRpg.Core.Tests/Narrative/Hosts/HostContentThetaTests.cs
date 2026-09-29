using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Narrative.Hosts;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.Narrative.Hosts;

/// <summary>
/// npc-story-events NR2.18 (spec-host-content-theta.md §3, §Testing strategy): every host's
/// Θ_content comes from the ONE composer, and the two "one producer" rules are enforced as source
/// scans with planted-violation falsifiers beside them — a scan that cannot fail is decoration.
///
/// <para>The tuning is built inline rather than read from <c>data/tuning/power-scale.v{n}.json</c>
/// on purpose: every assertion here is a relation between this module and
/// <see cref="PowerIndexComposer"/> under the SAME tuning, so a disk read would add a path
/// assumption without adding a claim (<c>PowerTuningLoader</c>'s own doc: "tests construct a JSON
/// string inline").</para>
/// </summary>
[Trait("VerificationId", "core.narrative")]
[Trait("Guard", "narrative")]
public sealed class HostContentThetaTests
{
    // The shipped weights (power-scale.v3.json), mirrored so Θ is composed by the real composer
    // under the real dial. Wf must equal Wa (ssot-power-scale.md §5.1) or the composer refuses.
    const string TuningJson = """
        {
          "schemaVersion": 1,
          "version": 3,
          "curve": { "cMilli": 80000, "bMilli": 400, "pinIndex": 20, "pinValue": 680 },
          "weights": {
            "WdMilli": 1000, "WaMilli": 25000, "WrMilli": 250,
            "WzMilli": 1000, "WmMilli": 5000, "WwMilli": 5000, "WfMilli": 25000
          }
        }
        """;

    static readonly ParentWorldTerms NonZeroWorld = new(WorldTier: 2, ZombossLevel: 7, RealmsAdvanced: 3);

    static PowerTuning Power() => PowerTuningLoader.Parse(TuningJson);

    static WorldSector Sector(int dangerBand) => new() { SectorId = "s", TypeId = "stable", DangerBand = dangerBand };

    // ---- the one composer --------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ForSector_composes_through_the_one_composer(int dangerBand)
    {
        var power = Power();
        var expected = PowerIndexComposer.ContentExplain(
            power, new ContentContext(dangerBand, NonZeroWorld.WorldTier, NonZeroWorld.ZombossLevel, NonZeroWorld.RealmsAdvanced));

        var actual = HostContentTheta.ForSector(power, Sector(dangerBand), NonZeroWorld);

        Assert.Equal(expected.Total, actual.Theta);
    }

    [Fact]
    public void ForSector_carries_the_sector_band_and_the_world_terms_into_the_context()
    {
        var actual = HostContentTheta.ForSector(Power(), Sector(4), NonZeroWorld);

        Assert.Equal(4, actual.Context.DangerBand);
        Assert.Equal(NonZeroWorld.WorldTier, actual.Context.WorldTier);
        Assert.Equal(NonZeroWorld.ZombossLevel, actual.Context.ZombossLevel);
        Assert.Equal(NonZeroWorld.RealmsAdvanced, actual.Context.RealmsAdvanced);
    }

    [Fact]
    public void ForDelveRoom_passes_the_room_theta_and_context_through_unchanged()
    {
        var room = new RoomTheta(new ContentContext(3, 1, 2, 4), Theta: 12345, Band: 3);

        var actual = HostContentTheta.ForDelveRoom(room);

        Assert.Equal(room.Theta, actual.Theta);
        Assert.Same(room.Context, actual.Context); // the same reference: this arm composed nothing
    }

    [Fact]
    public void ForExpedition_composes_through_the_one_composer_from_the_tiers_own_band()
    {
        var power = Power();
        var tier = ExpeditionTierCatalog.Get("hunt-8h");
        var expected = PowerIndexComposer.ContentExplain(
            power, new ContentContext(tier.DangerBand, NonZeroWorld.WorldTier, NonZeroWorld.ZombossLevel, NonZeroWorld.RealmsAdvanced));

        var actual = HostContentTheta.ForExpedition(power, tier, NonZeroWorld);

        Assert.Equal(expected.Total, actual.Theta);
        Assert.Equal(tier.DangerBand, actual.Context.DangerBand);
    }

    [Fact]
    public void ForHomeworld_equals_a_band_zero_sector()
    {
        var power = Power();
        var homeworld = HostContentTheta.ForHomeworld(power, NonZeroWorld);
        var bandZeroSector = HostContentTheta.ForSector(power, Sector(0), NonZeroWorld);

        Assert.Equal(bandZeroSector.Theta, homeworld.Theta);
        Assert.Equal(bandZeroSector.Context, homeworld.Context);
    }

    [Fact]
    public void Theta_does_not_decrease_as_the_band_rises()
    {
        var power = Power();
        var previous = int.MinValue;
        for (var band = 0; band <= 6; band++)
        {
            var theta = HostContentTheta.ForSector(power, Sector(band), NonZeroWorld).Theta;
            Assert.True(theta >= previous, $"Θ fell from {previous} to {theta} at band {band}");
            previous = theta;
        }
    }

    [Fact]
    public void Parent_world_terms_are_zero_today_and_that_is_the_named_absence_not_a_guess()
    {
        // Three of the four Θ_content inputs have no live source (spec §1). The producer returns
        // absence, names each owner in code, and never throws — the reading the composer already
        // applies to a missing progression row.
        var terms = ParentWorldTermsSource.For(world: null);

        Assert.Equal(0, terms.WorldTier);
        Assert.Equal(0, terms.ZombossLevel);
        Assert.Equal(0, terms.RealmsAdvanced);
    }

    // ---- one producer, enforced as a source scan ---------------------------------------------------

    [Fact]
    public void One_producer_of_parent_world_terms()
    {
        var sites = ConstructionSites(Path.Combine(RepoRoot(), "src"), "new ParentWorldTerms(");

        Assert.Equal(
            new[] { "src/FusionRpg.Core/Narrative/Hosts/ParentWorldTermsSource.cs" },
            sites);
    }

    [Fact]
    public void No_narrative_type_builds_a_content_context_outside_host_content_theta()
    {
        var sites = ConstructionSites(Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Narrative"), "new ContentContext(");

        Assert.Equal(
            new[] { "src/FusionRpg.Core/Narrative/Hosts/HostContentTheta.cs" },
            sites);
    }

    [Fact]
    public void The_producer_scan_names_a_planted_second_construction_site()
    {
        var root = NewTempRoot();
        try
        {
            File.WriteAllLines(Path.Combine(root, "SecondProducer.cs"), new[]
            {
                "public static class SecondProducer",
                "{",
                "    public static object For() => new ParentWorldTerms(0, 0, 0);",
                "}"
            });

            var sites = ConstructionSites(root, "new ParentWorldTerms(");

            Assert.Single(sites);
            Assert.EndsWith("SecondProducer.cs", sites[0], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_narrative_scan_ignores_a_mention_inside_a_comment()
    {
        var root = NewTempRoot();
        try
        {
            File.WriteAllLines(Path.Combine(root, "Mentioned.cs"), new[]
            {
                "/// <summary>Mirrors the new ContentContext( call in the composer.</summary>",
                "// new ContentContext( is built in HostContentTheta",
                "public static class Mentioned { }"
            });

            Assert.Empty(ConstructionSites(root, "new ContentContext("));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Repo-relative paths of every non-test .cs file under <paramref name="dir"/> that
    /// constructs the token in CODE — a mention inside a comment or doc comment is not a site.</summary>
    static IReadOnlyList<string> ConstructionSites(string dir, string token)
    {
        var repo = RepoRoot();
        var sites = new List<string>();
        if (!Directory.Exists(dir)) return sites;

        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal) ||
                normalized.Contains("/bin/", StringComparison.Ordinal)) continue;

            var inCode = File.ReadAllLines(file).Any(line =>
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("/*", StringComparison.Ordinal)) return false;
                return line.Contains(token, StringComparison.Ordinal);
            });

            if (inCode) sites.Add(Path.GetRelativePath(repo, file).Replace('\\', '/'));
        }

        sites.Sort(StringComparer.Ordinal);
        return sites;
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                               // tests/.../Narrative/Hosts
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", "..", ".."));   // repo root
    }

    static string NewTempRoot()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-host-theta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }
}
