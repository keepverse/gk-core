using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// ⭐ <b>`rpg_unique_equipment` is retired as an SSOT — enforced, not asserted in prose.</b>
///
/// <para>`decision-d1-durable-ownership.md` §10 M1/M2 moved every per-actor equipped-slot row into
/// module 4's <c>rpg_item_assignment</c> on 2026-09-06, closing an item the item program had
/// deferred twice (module 4's P1.4 to module 17, module 17's P5.1 back to module 4, neither
/// building it). Retirement only stays true if nothing quietly starts reading the old table again,
/// so the allowlist below is the whole of what may name it.</para>
///
/// <para><b>Why the table is not dropped.</b> D1 §10 makes the <c>DROP</c> step <b>M4</b> and calls
/// it *"the only irreversible act; do it last"* — and M4's own precondition, a
/// <c>ref_kind = 'rolled'</c> grant path reading <c>effect_instance</c> instead of
/// <c>UniqueEquipmentCatalog.Items</c>, does not exist: no concrete unique container has been
/// minted (module 17's own top-listed deferral, owned by the runtime seed→concrete generator). So
/// the rows stay readable by the one-way migration until that lands. This guard is what makes
/// "retired" mean something in the meantime.</para>
/// </summary>
public class LegacyEquipTableRetirementGuardTests
{
    const string LegacyTable = "rpg_unique_equipment";

    /// <summary>The only three places production code may still name the retired table: its DDL,
    /// the <c>Reset()</c> sweep that clears it, and the one-way migration that reads it.</summary>
    static readonly (string File, string Reason)[] Allowed =
    {
        (Path.Combine("src", "FusionRpg.Data", "Sqlite", "RpgStore.cs"),
            "the CREATE TABLE IF NOT EXISTS that keeps an existing save readable, plus the Reset() sweep"),
        (Path.Combine("src", "FusionRpg.Data", "Sqlite", "RpgStore.UniqueActors.cs"),
            "MigrateUniqueEquipmentToAssignmentsUnlocked -- the D1 M1 one-way read"),
    };

    [Fact]
    public void No_production_file_outside_the_migration_names_the_retired_table()
    {
        var offenders = new List<string>();
        foreach (var file in ProductionSources())
        {
            var relative = Path.GetRelativePath(FindRepoRoot(), file);
            if (Allowed.Any(a => string.Equals(a.File, relative, StringComparison.OrdinalIgnoreCase))) continue;
            if (StripComments(File.ReadAllText(file)).Contains(LegacyTable, StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            $"{LegacyTable} is retired (D1 §10 M1/M2). These files still name it: " +
            string.Join(", ", offenders) +
            ". Equipment state belongs in rpg_item_assignment; see RpgStore.MigrateUniqueEquipmentToAssignments.");
    }

    /// <summary>
    /// ⭐ The retirement's real content: inside the one file still allowed to touch it, the table
    /// appears in exactly one statement and that statement is a <c>SELECT</c>. An
    /// <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c> would mean equipment is being written to two
    /// places at once — which is the state this migration existed to end.
    /// </summary>
    [Fact]
    public void The_migration_only_reads_the_retired_table_and_never_writes_it()
    {
        var text = StripComments(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Data", "Sqlite", "RpgStore.UniqueActors.cs")));

        var mentions = Regex.Matches(text, Regex.Escape(LegacyTable)).Count;
        Assert.Equal(1, mentions);

        foreach (var write in new[] { "INSERT INTO " + LegacyTable, "UPDATE " + LegacyTable, "DELETE FROM " + LegacyTable })
            Assert.DoesNotContain(write, text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("FROM " + LegacyTable, text, StringComparison.Ordinal);
    }

    /// <summary>Reset() must clear the table that now holds equipment. It cleared only the legacy
    /// one until 2026-09-06 — the same orphan bug the world-stage and delve rows beside it already
    /// carry comments about.</summary>
    [Fact]
    public void Reset_clears_the_table_that_replaced_it()
    {
        var text = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Data", "Sqlite", "RpgStore.cs"));
        Assert.Contains("DELETE FROM rpg_item_assignment;", text, StringComparison.Ordinal);
    }

    /// <summary>Drop <c>//</c> and <c>///</c> comment bodies. The guard is about a table still being
    /// <b>used</b>; a doc comment that explains the retirement — several of which this change
    /// deliberately added — is the opposite of a violation, and a guard that punished writing one
    /// would push the explanation out of the code. SQL heredocs (<c>"""…"""</c>) contain <c>--</c>
    /// comments but never <c>//</c>, so line-comment stripping cannot eat a statement.</summary>
    static string StripComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx < 0 ? line : line[..idx];
        }));

    static IEnumerable<string> ProductionSources()
    {
        var root = FindRepoRoot();
        foreach (var project in new[] { "FusionRpg.Core", "FusionRpg.Data", "FusionRpg.Server", "FusionRpg.Contracts", "FusionRpg.Injector" })
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                if (rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                yield return file;
            }
        }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }
}
