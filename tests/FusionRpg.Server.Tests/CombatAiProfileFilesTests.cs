using System.Text.Json.Nodes;
using FusionRpg.Core.Actions.Ai;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// combat-ai `replay-identity` module 8, CAI2.2 (spec-replay-identity.md §2): the Server's
/// version-addressed profile source. The happy path runs against the repo's OWN real
/// <c>gk-core/data/tuning</c> tree — the shape a real boot hits — and the refusals run against a temp directory,
/// because there the disk IS the thing under test.
///
/// <para><b>No assertion here pins how many versions are published.</b> That is a reading that grows
/// whenever a balance pass publishes, and a test that pins it fails on the normal case. What is pinned
/// is the CONTRACT: <c>Current</c> is the newest the directory holds, <see cref="CombatAiProfileFiles.ForVersion"/>
/// answers for exactly the versions that are there and <c>null</c> for one that is not, and each
/// load-time refusal names the file it refused.</para>
/// </summary>
public class CombatAiProfileFilesTests
{
    [Fact]
    public void Current_is_the_newest_published_version_and_every_version_round_trips_by_number()
    {
        var source = new CombatAiProfileFiles(RepoTuningDir());

        var versions = source.Versions;
        Assert.NotEmpty(versions);
        Assert.Equal(versions[^1], source.Current.Version);

        // Ascending, no duplicates — the map is built from a sorted set, not from enumeration order.
        for (var i = 1; i < versions.Count; i++)
            Assert.True(versions[i] > versions[i - 1]);

        // Every supplied version round-trips, and its version field agrees with the key it is filed
        // under — the same property the loader refuses a disagreement on.
        foreach (var version in versions)
        {
            var tuning = source.ForVersion(version);
            Assert.NotNull(tuning);
            Assert.Equal(version, tuning!.Version);
        }
    }

    /// <summary>An unresolvable pin is a REFUSAL, never a fallback: the caller must be able to tell
    /// "this host cannot supply v{n}" from "here is the newest".</summary>
    [Fact]
    public void An_unknown_version_returns_null_never_the_current_set()
    {
        var source = new CombatAiProfileFiles(RepoTuningDir());

        Assert.Null(source.ForVersion(source.Current.Version + 1_000));
        Assert.Null(source.ForVersion(int.MinValue));
        Assert.Null(source.ForVersion(0));
    }

    [Fact]
    public void A_file_whose_name_version_disagrees_with_its_document_is_refused_naming_it()
    {
        WithTempTuningDir(dir =>
        {
            // Named v3, carrying version 2 — the shape a rename or a hand-edit leaves behind.
            File.WriteAllText(Path.Combine(dir, "combat-ai.v3.json"), Document(version: 2));

            var ex = Assert.Throws<CombatAiTuningRejection>(() => new CombatAiProfileFiles(dir));
            Assert.Contains("combat-ai.v3.json", ex.Message);
            Assert.Contains("2", ex.Message);
        });
    }

    [Fact]
    public void A_version_published_twice_is_refused_naming_both_files()
    {
        WithTempTuningDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "combat-ai.v2.json"), Document(version: 2));
            File.WriteAllText(Path.Combine(dir, "combat-ai.v02.json"), Document(version: 2));

            var ex = Assert.Throws<CombatAiTuningRejection>(() => new CombatAiProfileFiles(dir));
            Assert.Contains("published twice", ex.Message);
            Assert.Contains("combat-ai.v2.json", ex.Message);
            Assert.Contains("combat-ai.v02.json", ex.Message);
        });
    }

    [Fact]
    public void A_tuning_directory_with_no_combat_ai_file_is_refused()
    {
        WithTempTuningDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "ai.v9.json"), "{}"); // another domain's file, not ours
            File.WriteAllText(Path.Combine(dir, "combat-ai.vault.json"), "{}"); // matches the glob, not the shape

            var ex = Assert.Throws<CombatAiTuningRejection>(() => new CombatAiProfileFiles(dir));
            Assert.Contains("combat-ai.v", ex.Message);
        });
    }

    [Fact]
    public void A_malformed_document_is_refused_by_the_loader_not_silently_skipped()
    {
        WithTempTuningDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "combat-ai.v1.json"), """{"schemaVersion": 1, "version": 1}""");

            Assert.Throws<CombatAiTuningRejection>(() => new CombatAiProfileFiles(dir));
        });
    }

    /// <summary>The newest version wins and the older one stays addressable — the property the whole pin
    /// rests on, and the one enumeration order must not be able to change.</summary>
    [Fact]
    public void The_newest_version_is_current_and_the_older_one_is_still_addressable()
    {
        WithTempTuningDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "combat-ai.v1.json"), Document(version: 1));
            File.WriteAllText(Path.Combine(dir, "combat-ai.v7.json"), Document(version: 7));

            var source = new CombatAiProfileFiles(dir);

            Assert.Equal(7, source.Current.Version);
            Assert.Equal(new[] { 1, 7 }, source.Versions);
            Assert.Equal(1, source.ForVersion(1)!.Version);
        });
    }

    [Fact]
    public void A_missing_or_empty_directory_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new CombatAiProfileFiles(""));

        var missing = Path.Combine(Path.GetTempPath(), "combat-ai-absent-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<CombatAiTuningRejection>(() => new CombatAiProfileFiles(missing));
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    /// <summary>The repo's own shipped document with one field moved, so the fixture keeps tracking the
    /// real schema instead of being a second, stale copy of it.</summary>
    static string Document(int version)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "combat-ai.v1.json")))!;
        node["version"] = version;
        return node.ToJsonString();
    }

    static void WithTempTuningDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "combat-ai-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static string RepoTuningDir() => Path.Combine(FindRepoRoot(), "data", "tuning");

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
