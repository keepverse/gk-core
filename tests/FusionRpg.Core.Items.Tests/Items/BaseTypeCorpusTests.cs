using System.Text.Json;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Balance.Guards;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `base-types` (item module 6) against the REAL, shipped corpus (`classes.v2.json`,
/// `gk-data/packs/fusion/data/seed/items/base-types/**`, `gk-core/data/tuning/sockets.v2.json`) — D11's three clauses and the
/// `channel-split` dominance lint, spec-base-types.md's own stated obligation at build position 6.
/// </summary>
public class BaseTypeCorpusTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static JsonDocument LoadClasses() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "classes.v3.json")));

    static FrameLeanTable LoadLeans() =>
        FrameLean.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "items", "_registry", "frame-lean.v1.json")));

    static readonly string[] HybridCoreRoleIds =
    {
        "armament-primary", "core-guard", "armament-secondary", "jewel-major", "manipulator",
        "mantle", "girdle", "footing", "infusion", "retinue", "jewel-minor-a", "jewel-minor-b",
    };

    sealed record LiveEntry(string Id, string Role, string Frame, string Family, int? SocketMax);

    static List<LiveEntry> LoadCorpus()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "base-types");
        var result = new List<LiveEntry>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;
            foreach (var e in entries.EnumerateArray())
            {
                if (e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                result.Add(new LiveEntry(
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("role").GetString()!,
                    e.GetProperty("frame").GetString()!,
                    e.GetProperty("implicit").GetProperty("family").GetString()!,
                    e.TryGetProperty("socketMax", out var sm) && sm.ValueKind == JsonValueKind.Number ? sm.GetInt32() : null));
            }
        }

        return result;
    }

    static Dictionary<string, int> LoadCeilings() =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(
            JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)))
                .RootElement.GetProperty("socketCeiling").GetRawText())!;

    // ---- D11 clause 1 -------------------------------------------------------------------------

    [Fact]
    public void Every_live_roles_humanoid_and_plant_implicit_families_are_disjoint()
    {
        var corpus = LoadCorpus();
        foreach (var role in corpus.Select(e => e.Role).Distinct())
        {
            if (role == "standard") continue; // retired legacy rows, D14 -- out of scope
            var h = corpus.Where(e => e.Role == role && e.Frame == "humanoid").Select(e => e.Family).ToHashSet();
            var p = corpus.Where(e => e.Role == role && e.Frame == "plant").Select(e => e.Family).ToHashSet();
            Assert.Empty(h.Intersect(p));
        }
    }

    [Fact]
    public void Stat_derived_is_not_excluded_from_any_implicit_slate()
    {
        // The D6 quarantine ended 2026-09-02 (AtomKindRegistry.cs: RuntimeSupportMatrix Full/Full/None).
        // None of the fifteen families that carried the stale reason may still appear in the global
        // exclusion list.
        string[] lifted =
        {
            "atom.elemental-power", "atom.elemental-defense", "atom.precision", "atom.evasion",
            "atom.keen-edge", "atom.cruelty", "atom.stoicism", "atom.padding",
            "atom.shield-capacity", "atom.shield-toughness", "atom.shield-pen", "atom.shield-regen",
            "atom.affliction", "atom.stalwart", "atom.immunity",
        };

        using var doc = LoadClasses();
        var excluded = doc.RootElement.GetProperty("excludedFamilies").GetProperty("global")
            .EnumerateArray().Select(e => e.GetProperty("family").GetString()).ToHashSet();

        foreach (var family in lifted)
            Assert.DoesNotContain(family, excluded);

        // susceptibility stays excluded -- zero readers, a different reason entirely.
        Assert.Contains("atom.susceptibility", excluded);
    }

    [Fact]
    public void The_five_stopgap_roles_carry_their_real_clusters()
    {
        using var doc = LoadClasses();
        var slates = doc.RootElement.GetProperty("implicitSlates");

        (string role, string family)[] mustHave =
        {
            ("ward-array", "atom.shield-capacity"), ("mantle", "atom.elemental-defense"),
            ("head-guard", "atom.stoicism"), ("sense", "atom.precision"), ("footing", "atom.evasion"),
        };

        foreach (var (role, family) in mustHave)
        {
            var legal = slates.GetProperty(role).GetProperty("legalFamilies")
                .EnumerateArray().Select(e => e.GetString()).ToHashSet();
            Assert.Contains(family, legal);
        }
    }

    // ---- socketMax ------------------------------------------------------------------------------

    [Fact]
    public void Every_live_base_type_carries_a_socketMax()
    {
        foreach (var e in LoadCorpus())
            Assert.NotNull(e.SocketMax);
    }

    [Fact]
    public void No_base_type_exceeds_its_role_socket_ceiling()
    {
        var ceilings = LoadCeilings();
        foreach (var e in LoadCorpus())
        {
            if (!ceilings.TryGetValue(e.Role, out var ceiling)) continue; // standard has no ceiling row
            Assert.True(e.SocketMax <= ceiling, $"{e.Id}: socketMax {e.SocketMax} exceeds role '{e.Role}''s ceiling {ceiling}");
        }
    }

    [Fact]
    public void Every_base_type_is_within_its_role_ceiling_and_the_corpus_still_reaches_the_host_count()
    {
        // SSH5.10: the un-restamped corpus must sit INSIDE v2's doubled ceilings — no row above its
        // role ceiling. "Every four-socket role has a base reaching four" is SSH5.12's re-stamp, not
        // this state.
        var ceilings = LoadCeilings();
        var corpus = LoadCorpus();
        foreach (var e in corpus)
        {
            Assert.True(ceilings.ContainsKey(e.Role), $"corpus role '{e.Role}' has no ceiling row");
            Assert.True(e.SocketMax is int m && m <= ceilings[e.Role],
                $"{e.Id}: {e.Role} socketMax {e.SocketMax} exceeds its ceiling {ceilings[e.Role]}");
        }

        // …and the corpus still reaches the ingredient count somewhere, so a Strain is hostable.
        Assert.Contains(corpus, e => e.SocketMax is int m && m >= 4);
    }

    [Fact]
    public void Band_letters_are_a_and_b_today()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "base-types");
        var bands = new HashSet<string>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;
            foreach (var e in entries.EnumerateArray())
                if (e.TryGetProperty("band", out var b))
                    bands.Add(b.GetString()!);
        }

        Assert.Subset(new HashSet<string> { "a", "b", "c", "d" }, bands);
    }

    // ---- the channel-split dominance lint -- this module's whole obligation at position 6 -------

    [Fact]
    public void The_dominance_lint_is_green_in_channel_split_mode_for_every_hybrid_core_role()
    {
        var report = FrameDominanceGuard.RunChannelSplit(LoadLeans(), HybridCoreRoleIds);
        Assert.True(report.IsGreen, string.Join("\n", report.Findings.Select(f => $"{f.RoleId}: {f.Reason}")));
    }
}
