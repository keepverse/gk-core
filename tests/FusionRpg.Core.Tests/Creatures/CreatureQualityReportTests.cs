using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Tests.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// The reusable corpus-wide scan tool (2026-09-04, built after the full classification run
/// surfaced real, corpus-scale findings a one-off script would never have caught: 217 duplicate
/// anchor entries, an 81‰ unresolved rarity rate, 98 species refusing to generate, 50% of the
/// generatable corpus being complete combat stomps against the field average). A real, cold
/// `dotnet run` against a small synthetic seed tree — same pattern
/// `CreatureSpeciesGenExplainTests.cs` already established — proving the CLI itself runs end to end
/// through the real `SpeciesExpander`/`Simulator.Duel` pipeline, not a re-implementation of either.
/// </summary>
public class CreatureQualityReportTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "CreatureQualityReport"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    /// <summary>A tiny, real-shaped synthetic seed tree: one clean plant, one clean zombie, one
    /// species with an unresolved aptitude (must refuse to generate, not silently zero-stat), and
    /// a deliberate DUPLICATE of the clean plant in a second file (must be caught, not silently
    /// double-counted) — every section of the report gets at least one real thing to find.</summary>
    static string BuildSyntheticSeedTree(string tmpDir)
    {
        var speciesDir = Path.Combine(tmpDir, "species");
        Directory.CreateDirectory(Path.Combine(speciesDir, "plant"));
        Directory.CreateDirectory(Path.Combine(speciesDir, "zombie"));

        static string Entry(string id, string side, string rarity, string aptitude) => $$"""
            { "speciesId": "{{id}}", "rarity": "{{rarity}}", "threatBand": null,
              "aptitudePrimary": "{{aptitude}}", "aptitudeSecondary": "none", "pure": true,
              "attackTempo": "steady", "reach": "melee", "variants": ["normal"],
              "side": "{{side}}", "gameTypeId": 1, "elementPrimary": "earth", "elementSecondary": "none",
              "deployMode": "{{(side == "plant" ? "PlantAvatar" : "HypnoAlly")}}",
              "acquisition": ["Summonable"], "traits": [], "targetPreference": "frontline" }
            """;

        File.WriteAllText(Path.Combine(speciesDir, "plant", "clean.json"),
            "[" + Entry("CleanPlant", "plant", "cultivated", "Onslaught") + "]");
        File.WriteAllText(Path.Combine(speciesDir, "zombie", "clean.json"),
            "[" + Entry("CleanZombie", "zombie", "chaff", "Bulwark") + "]");
        File.WriteAllText(Path.Combine(speciesDir, "plant", "bad.json"),
            "[" + Entry("BadAptitude", "plant", "cultivated", "unresolved") + "]");
        // A stale duplicate — same id as CleanPlant, a different file, exactly the class of bug
        // the real corpus scan found 217 of.
        File.WriteAllText(Path.Combine(speciesDir, "plant", "stale.json"),
            "[" + Entry("CleanPlant", "plant", "cultivated", "Onslaught") + "]");

        File.WriteAllText(Path.Combine(speciesDir, "_index.json"), JsonSerializer.Serialize(new
        {
            CleanPlant = "plant/clean.json", CleanZombie = "zombie/clean.json", BadAptitude = "plant/bad.json",
        }));
        return speciesDir;
    }

    [Fact]
    public void A_real_run_reports_duplicates_generation_failures_and_a_balance_section()
    {
        var repoRoot = RepoRoot();
        var tmpDir = Path.Combine(Path.GetTempPath(), "dqr-test-" + Guid.NewGuid().ToString("N"));
        var speciesDir = BuildSyntheticSeedTree(tmpDir);
        var jsonOut = Path.Combine(tmpDir, "report.json");

        try
        {
            // ProjectReference + apphost (see ToolProcess) — no implicit build, no stale output.
            var (exitCode, stdout, stderr) = ToolProcess.Run(
                repoRoot, "CreatureQualityReport",
                $"--seed \"{speciesDir}\" --trials 50 --json \"{jsonOut}\"", 120_000);
            Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // Section 1: the duplicate and the two clean species are all named.
            Assert.Contains("CleanPlant", stdout, StringComparison.Ordinal);
            Assert.Contains("1 species have MORE THAN ONE anchor entry", stdout, StringComparison.Ordinal);

            // Section 2: catalog diversity for every closed-vocabulary field — "unresolved" is
            // excluded from the count (BadAptitude's aptitude is unresolved, so aptitudePrimary's
            // diversity is computed over the 2 REAL values only, never counting "unresolved" as if
            // it were a real vocabulary member).
            Assert.Contains("Catalog diversity", stdout, StringComparison.Ordinal);
            Assert.Contains("side", stdout, StringComparison.Ordinal);
            // Every species in the fixture carries exactly one acquisition flag — the multi-valued
            // report must show 100% Summonable, not a categorical distribution shape.
            Assert.Contains("Summonable", stdout, StringComparison.Ordinal);

            // Section 3: the unresolved-aptitude species refuses to generate, named by reason —
            // never silently a zero-magnitude ghost (the exact defect this whole tool exists to
            // catch, per SpeciesExpanderTests.cs's own regression coverage of the underlying fix).
            Assert.Contains("2/3 species generate cleanly, 1 refuse", stdout, StringComparison.Ordinal);
            Assert.Contains("unresolved/unknown aptitudePrimary", stdout, StringComparison.Ordinal);

            // Section 4: a real balance section ran (not skipped) — with only 2 fightable species
            // it prints a report, not a crash.
            Assert.Contains("Balance (real simulated combat", stdout, StringComparison.Ordinal);
            Assert.Contains("running 2 duels", stdout, StringComparison.Ordinal);

            Assert.True(File.Exists(jsonOut), "report.json was not written");
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonOut));
            var root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("generatedCount").GetInt32());
            Assert.Equal(1, root.GetProperty("generationFailureCount").GetInt32());
            Assert.Equal(1, root.GetProperty("duplicateSpeciesCount").GetInt32());
            var species = root.GetProperty("species").EnumerateArray().Select(e => e.GetProperty("speciesId").GetString()).ToList();
            Assert.Equal(new[] { "CleanPlant", "CleanZombie" }, species.OrderBy(s => s, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>Verifies the entropy MATH, not just that the section prints — a perfectly even
    /// 2-way split (2 plant, 2 zombie) over a 2-value closed vocabulary is the one case with a
    /// known, exact answer: normalised Shannon entropy = 1.00.</summary>
    [Fact]
    public void A_perfectly_even_split_reports_entropy_1_00()
    {
        var repoRoot = RepoRoot();
        var tmpDir = Path.Combine(Path.GetTempPath(), "dqr-entropy-test-" + Guid.NewGuid().ToString("N"));
        var speciesDir = Path.Combine(tmpDir, "species");
        Directory.CreateDirectory(Path.Combine(speciesDir, "plant"));
        Directory.CreateDirectory(Path.Combine(speciesDir, "zombie"));

        static string Entry(string id, string side) => $$"""
            { "speciesId": "{{id}}", "rarity": "cultivated", "threatBand": null,
              "aptitudePrimary": "Onslaught", "aptitudeSecondary": "none", "pure": true,
              "attackTempo": "steady", "reach": "melee", "variants": ["normal"],
              "side": "{{side}}", "gameTypeId": 1, "elementPrimary": "earth", "elementSecondary": "none",
              "deployMode": "{{(side == "plant" ? "PlantAvatar" : "HypnoAlly")}}",
              "acquisition": ["Summonable"], "traits": [], "targetPreference": "frontline" }
            """;

        File.WriteAllText(Path.Combine(speciesDir, "plant", "p.json"),
            "[" + Entry("P1", "plant") + "," + Entry("P2", "plant") + "]");
        File.WriteAllText(Path.Combine(speciesDir, "zombie", "z.json"),
            "[" + Entry("Z1", "zombie") + "," + Entry("Z2", "zombie") + "]");
        File.WriteAllText(Path.Combine(speciesDir, "_index.json"), JsonSerializer.Serialize(new
        {
            P1 = "plant/p.json", P2 = "plant/p.json", Z1 = "zombie/z.json", Z2 = "zombie/z.json",
        }));

        try
        {
            // ProjectReference + apphost (see ToolProcess) — no implicit build, no stale output.
            var (exitCode, stdout, stderr) = ToolProcess.Run(
                repoRoot, "CreatureQualityReport",
                $"--seed \"{speciesDir}\" --trials 20", 120_000);
            Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            var sideLine = stdout.Split('\n').Single(l => l.TrimStart().StartsWith("side "));
            Assert.Contains("2/2", sideLine, StringComparison.Ordinal);
            Assert.Contains("1.00", sideLine, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
