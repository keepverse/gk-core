using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Narrative.Doctrine;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// npc-story-events NR6.2, registry row `ns6-reading-aggregate-only` (spec-counter-doctrine.md §1, R13/R18):
/// the reading's output is AGGREGATE ONLY — lean keys, counts and per-mille shares, and nothing per-entity.
/// A member carrying an entity id, a member, a snapshot or a remembered force on the OUTPUT types would be a
/// second, finer read of the fog than the study bar may advance on, and would let a consumer rebuild "which
/// of his legions did he see" from the reading itself.
///
/// <para>The member set is enforced twice, because the two mechanisms see different declarations, and
/// each has a planted control that proves it bites. The first is the source scan below, which reads only
/// a one-line <c>public</c>/<c>internal</c> property. The second is reflection over the two output records
/// themselves, which also sees a positional record parameter and a plain field — and this reading declares
/// most of its output that way, so the text scan alone never saw it. The reflection half lives HERE, beside
/// the row that carries this rule, because this project references FusionRpg.Core (it has to: the suite's
/// own sources use <c>KeepverseRoots</c>, and without the reference nothing in it compiles).</para>
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
        return KeepverseRoots.Core();
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

    // ---- the member set over the real types ----------------------------------------------------------------
    //
    // The source scan above can only read a declaration it can see whole on one line, and it requires
    // `{ get` or `=>` on that line. Of the reading's public output members that leaves exactly one it
    // reads — the computed `ShareMilli`. `Key`, `Count`, `Total`, `Shares`, `LeanKey` and
    // `LeanShareMilli` are POSITIONAL RECORD PARAMETERS and `NothingSeen` is a plain field; none of
    // them carries a `{ get` or an `=>` where the scan can see it, so a per-entity member added in any
    // of those seven shapes would pass the text scan untouched. Reflection over the types is the check
    // that covers the whole surface, and this project can do it.

    /// <summary>The reading's two output records — the closed set the row's rule is about.</summary>
    static Type[] OutputTypes() => new[] { typeof(DoctrineShare), typeof(DoctrineReadingResult) };

    /// <summary>Every public property and field name on <paramref name="outputTypes"/>, instance and static,
    /// inherited members included. No <c>DeclaredOnly</c>: a member inherited onto an output record is just
    /// as much part of what a consumer can read off it.</summary>
    public static IReadOnlyList<string> ReflectedMembers(params Type[] outputTypes)
    {
        const BindingFlags visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        return outputTypes
            .SelectMany(type => type.GetProperties(visible).Select(p => p.Name)
                .Concat(type.GetFields(visible).Select(f => f.Name)))
            .ToArray();
    }

    /// <summary>Reflected members whose NAME carries a leak marker. The same rule the source scan applies,
    /// applied to the types themselves.</summary>
    public static IReadOnlyList<string> ReflectedLeaks(params Type[] outputTypes) =>
        ReflectedMembers(outputTypes)
            .Where(name => LeakMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal)))
            .ToArray();

    /// <summary>A planted output record, in the three member shapes a one-line text scan cannot all read:
    /// a positional parameter, a computed property, an init property. The reflection rule must report the
    /// per-entity one and pass the aggregate ones.</summary>
    record PlantedOutput(string Key, int Count)
    {
        public int ShareMilli => Count * 2;
        public string ObservedEntityId { get; init; } = "";
    }

    [Fact]
    public void The_reflection_rule_bites_on_a_planted_output_type()
    {
        // The exhaustive member set, so this control also proves the ENUMERATION is real. A binding-flag
        // typo that silently matched nothing would leave `ReflectedLeaks` empty and the test below free.
        Assert.Equal(
            new[] { "Count", "Key", "ObservedEntityId", "ShareMilli" },
            ReflectedMembers(typeof(PlantedOutput)).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(new[] { "ObservedEntityId" }, ReflectedLeaks(typeof(PlantedOutput)));
    }

    [Fact]
    public void The_output_types_declare_no_per_entity_member_by_reflection()
    {
        // Anti-vacuity, applied to the enumeration the rule below actually used: what it read has to be
        // the DECLARED public surface, not nothing and not the compiler's storage. A binding-flag slip
        // aimed at backing fields returns a non-empty, marker-free list, which would make the emptiness
        // below free. The planted control's exhaustive member set is the other half of this.
        var members = ReflectedMembers(OutputTypes());

        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, name => name.Contains("k__BackingField", StringComparison.Ordinal));

        var leaks = ReflectedLeaks(OutputTypes());

        Assert.True(leaks.Count == 0,
            "the reading's output types must be aggregate-only; found: " + string.Join(", ", leaks));
    }
}
