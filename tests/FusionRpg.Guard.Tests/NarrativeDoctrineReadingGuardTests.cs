using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// npc-story-events NR6.2, registry row `ns6-reading-aggregate-only` (spec-counter-doctrine.md §1, R13/R18):
/// the reading's output is AGGREGATE ONLY — lean keys, counts and per-mille shares, and nothing per-entity.
/// A member carrying an entity id, a member, a snapshot or a remembered force on the OUTPUT types would be a
/// second, finer read of the fog than the study bar may advance on, and would let a consumer rebuild "which
/// of his legions did he see" from the reading itself.
///
/// <para>This project references no Core assembly (it is a guard/meta-test project), so the rule is a source
/// scan of the reading's own file: every declared property on the output records is checked by name, and a
/// planted violation proves the rule bites.</para>
/// </summary>
[Trait("VerificationId", "core.narrative")]
[Trait("Guard", "narrative")]
public sealed class NarrativeDoctrineReadingGuardTests
{
    /// <summary>Identifier fragments that would make an output member a per-entity read rather than an
    /// aggregate.</summary>
    static readonly string[] LeakMarkers =
    {
        "EntityId", "Member", "Snapshot", "Force", "Sector", "Slot", "Species", "Intel"
    };

    static string ReadingSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Narrative", "Doctrine", "DoctrineReading.cs"));

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

    /// <summary>Every declared property in <paramref name="source"/> whose NAME carries a leak marker. Pure
    /// over text, so a planted source proves it can fail.</summary>
    public static IReadOnlyList<string> Leaks(string source)
    {
        var leaks = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                continue;
            if (!Regex.IsMatch(trimmed, @"^(public|internal)\s")) continue;
            if (!trimmed.Contains("{ get", StringComparison.Ordinal) && !trimmed.Contains("=>", StringComparison.Ordinal))
                continue;

            var boundary = trimmed.IndexOf("{ get", StringComparison.Ordinal);
            if (boundary < 0) boundary = trimmed.IndexOf("=>", StringComparison.Ordinal);
            var beforeBoundary = trimmed[..boundary].TrimEnd();
            var name = beforeBoundary.Split(' ', '\t', '(', ')', '<', '>', ',').Last(s => s.Length > 0);

            foreach (var marker in LeakMarkers)
                if (name.Contains(marker, StringComparison.Ordinal))
                {
                    leaks.Add(name);
                    break;
                }
        }
        return leaks;
    }

    [Fact]
    public void The_readings_file_declares_no_per_entity_output_member()
    {
        var leaks = Leaks(ReadingSource());

        Assert.True(leaks.Count == 0,
            "DoctrineReading's declared members must be aggregate-only; found: " + string.Join(", ", leaks));
    }

    [Fact]
    public void The_rule_bites_on_a_planted_per_entity_member()
    {
        const string planted = """
            public sealed record PlantedShare
            {
                public string EntityId { get; init; } = "";
                public int ShareMilli { get; init; }
            }
            """;

        var leaks = Leaks(planted);

        Assert.Contains("EntityId", leaks);
        Assert.DoesNotContain("ShareMilli", leaks);
    }

    [Fact]
    public void The_read_is_pinned_to_world_state_battles_and_two_faction_ids_in_the_source()
    {
        // The input contract, read from the file the row's registry entry names: a widened parameter list (a
        // third faction, a cast, a corpus handle) would be a read of state the fog rule has not approved.
        var source = ReadingSource();
        var signature = source[source.IndexOf("public static DoctrineReadingResult Of(", StringComparison.Ordinal)..];

        Assert.StartsWith("public static DoctrineReadingResult Of(\n        WorldState world, IReadOnlyList<TurnReportEntry> turnBattles,\n        string antagonistFactionId, string playerFactionId)", signature);
    }

    [Fact]
    public void The_reflection_over_the_output_types_is_a_compile_time_shape_this_project_cannot_see()
    {
        // Stated so the gap is explicit rather than implied: this project references no Core assembly, so the
        // member set above is enforced over the source text. The Core-side reflection assertion lives in
        // DoctrineReadingTests (FusionRpg.Core.Tests), which does reference it.
        Assert.False(typeof(NarrativeDoctrineReadingGuardTests).Assembly.GetReferencedAssemblies()
            .Any(a => a.Name == "FusionRpg.Core"));
    }
}
