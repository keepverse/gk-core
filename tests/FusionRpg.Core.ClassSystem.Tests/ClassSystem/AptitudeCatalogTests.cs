using System.Text.Json;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P1.1 — the twelve aptitudes as a shipped thing: closed id set,
/// posture grouping, computed count, no channel-namespace collision, spelling parity with the
/// gk-core/tools/CombatSim POC this program is ported from and eventually diffed against.</summary>
public class AptitudeCatalogTests
{
    [Fact]
    public void CountIsComputedAndPinnedAtTwelve()
    {
        // Canonical site (population-pin SE3.3, 2026-09-19): every other file asserting this SAME
        // total (AptitudeMatrixTests, AptitudeTuningTests, StrainSpliceGridTests) now reads it live
        // off this same catalog, never a second literal.
        // pin: closed-vocabulary AptitudeCatalog.Count — a new aptitude is a reviewed change
        Assert.Equal(12, AptitudeCatalog.Count);
        Assert.Equal(AptitudeCatalog.PostureCount * AptitudeCatalog.PerPosture, AptitudeCatalog.Count);
        Assert.Equal(AptitudeCatalog.Count, AptitudeCatalog.All.Count);
    }

    [Fact]
    public void IdsAreCollisionFree()
    {
        var ids = AptitudeCatalog.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Distinct(StringComparer.Ordinal).Count(), ids.Count);
        Assert.Equal(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), ids.Count);
    }

    [Fact]
    public void NoIdCollidesWithARegisteredChannelOrFamily()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var collisions = AptitudeCatalog.ChannelCollisions(registry);
        Assert.True(collisions.Count == 0, "aptitude/channel collision(s): " + string.Join(", ", collisions));
    }

    [Fact]
    public void OrdinalsAreDenseAndAppendOnlyByPosture()
    {
        var ordered = AptitudeCatalog.All.OrderBy(a => a.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++)
            Assert.Equal(i, ordered[i].Ordinal);

        // Four contiguous per posture, in the order Force -> Finesse -> Bastion (spec-primary-stats.md SS3).
        Assert.Equal(Enumerable.Repeat(Posture.Force, 4), ordered.Take(4).Select(a => a.Posture));
        Assert.Equal(Enumerable.Repeat(Posture.Finesse, 4), ordered.Skip(4).Take(4).Select(a => a.Posture));
        Assert.Equal(Enumerable.Repeat(Posture.Bastion, 4), ordered.Skip(8).Take(4).Select(a => a.Posture));
    }

    [Fact]
    public void IdsEqualCombatSimsPocEdgeSources()
    {
        // gk-core/tools/CombatSim/tuning/aptitudes.v1.json's edges name every aptitude as a `source` -- residual-fit
        // (Phase 8) compares this program's output against that tool's, so a spelling drift here would
        // silently break that comparison rather than fail loudly. Parsed directly, not hand-copied.
        var path = Path.Combine(FindRepoRoot(), "tools", "CombatSim", "tuning", "aptitudes.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var sources = doc.RootElement.GetProperty("edges").EnumerateArray()
            .Where(e => e.TryGetProperty("source", out _))
            .Select(e => e.GetProperty("source").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var catalogIds = AptitudeCatalog.All.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(sources, catalogIds);
    }

    [Fact]
    public void RosterJsonAgreesWithCode()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "seed", "aptitudes", "roster.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => (
                Id: e.GetProperty("id").GetString()!,
                Posture: e.GetProperty("posture").GetString()!,
                Ordinal: e.GetProperty("ordinal").GetInt32(),
                Role: e.GetProperty("role").GetString()!,
                Reading: e.GetProperty("reading").GetString()!))
            .OrderBy(e => e.Ordinal)
            .ToList();

        var code = AptitudeCatalog.All.OrderBy(a => a.Ordinal).ToList();
        Assert.Equal(code.Count, entries.Count);
        for (var i = 0; i < code.Count; i++)
        {
            Assert.Equal(code[i].Id, entries[i].Id);
            Assert.Equal(code[i].Posture.ToString(), entries[i].Posture, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(code[i].Ordinal, entries[i].Ordinal);
            Assert.Equal(code[i].Role, entries[i].Role);
            Assert.Equal(code[i].Reading, entries[i].Reading);
        }
    }

    [Theory]
    [InlineData("Might")]
    [InlineData("Ferocity")]
    public void TryGetResolvesKnownIds(string id)
    {
        Assert.True(AptitudeCatalog.TryGet(id, out var row));
        Assert.Equal(id, row.Id);
        Assert.True(AptitudeCatalog.IsAptitudeId(id));
    }

    [Fact]
    public void UnknownIdRejects()
    {
        Assert.False(AptitudeCatalog.IsAptitudeId("Nonexistent"));
        Assert.Throws<KeyNotFoundException>(() => AptitudeCatalog.Get("Nonexistent"));
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
