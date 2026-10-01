using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>notify-vocabulary spec Testing 1-3. Reads the shipped files as text/JSON only (this
/// project carries no ProjectReference, by design - it is a redundant check independent of whether
/// `NotificationCatalogLoader`/`catalog.ts` themselves have a bug). Never asserts how many
/// categories exist; the list is open (validation-ssot).</summary>
public class NotificationCatalogContractTests
{
    [Theory]
    [InlineData("NotifySeverity")]
    [InlineData("NotifyArgKind")]
    [InlineData("NotifyDelivery")]
    [InlineData("NotifyRefKind")]
    public void Csharp_and_TS_hold_identical_members_for_shared_enums(string enumName)
    {
        var csMembers = CsEnumMembers(ReadDtos(), enumName);
        Assert.NotEmpty(csMembers);
        var tsMembers = TsUnionMembers(ReadCatalogTs(), enumName);
        Assert.NotEmpty(tsMembers);

        var csAsCamel = csMembers.Select(ToCamelCase).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var tsSorted = tsMembers.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(csAsCamel, tsSorted);
    }

    [Fact]
    public void No_category_row_carries_a_channel_or_a_severity_field()
    {
        foreach (var row in ReadCategories())
        {
            Assert.False(row.TryGetProperty("channel", out _), "category row carries 'channel': " + row);
            Assert.False(row.TryGetProperty("severity", out _), "category row carries 'severity': " + row);
        }
    }

    [Fact]
    public void Every_category_has_a_non_empty_domain_and_messageKeys()
    {
        foreach (var row in ReadCategories())
        {
            Assert.True(row.TryGetProperty("domain", out var domain) && domain.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(domain.GetString()), "category missing non-empty 'domain': " + row);
            Assert.True(row.TryGetProperty("messageKeys", out var keys) && keys.ValueKind == JsonValueKind.Array
                && keys.GetArrayLength() > 0, "category missing non-empty 'messageKeys': " + row);
        }
    }

    [Fact]
    public void Every_promotions_id_is_a_registered_category_and_critical_is_a_subset_of_toast()
    {
        var registered = ReadCategories().Select(r => r.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var (toast, critical) = ReadPromotions();

        foreach (var id in toast.Concat(critical))
            Assert.Contains(id, registered);

        foreach (var id in critical)
            Assert.Contains(id, toast);
    }

    static IEnumerable<JsonElement> ReadCategories()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "notification-catalog.v3.json")));
        foreach (var row in doc.RootElement.GetProperty("categories").EnumerateArray())
            yield return row.Clone();
    }

    static (HashSet<string> toast, HashSet<string> critical) ReadPromotions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "notification-catalog.v3.json")));
        var promotions = doc.RootElement.GetProperty("promotions");
        var toast = promotions.GetProperty("toast").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var critical = promotions.GetProperty("critical").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        return (toast, critical);
    }

    static string ReadDtos() => ReadSrc("FusionRpg.Contracts", "NotificationDtos.cs");
    static string ReadCatalogTs() => File.ReadAllText(Path.Combine(KeepverseRoots.Web(), "web", "fusion-rpg-web", "src", "shell", "notify", "catalog.ts"));

    static string[] CsEnumMembers(string source, string enumName)
    {
        var match = Regex.Match(source, @"enum\s+" + Regex.Escape(enumName) + @"\s*\{([^}]*)\}", RegexOptions.Singleline);
        Assert.True(match.Success, $"enum {enumName} not found in NotificationDtos.cs");
        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.Split('=')[0].Trim())
            .Where(m => m.Length > 0)
            .ToArray();
    }

    static string[] TsUnionMembers(string source, string csEnumName)
    {
        var tsName = csEnumName; // catalog.ts names its unions identically (NotifySeverity, NotifyArgKind, ...)
        var match = Regex.Match(source, @"export type\s+" + Regex.Escape(tsName) + @"\s*=\s*([^;]+);", RegexOptions.Singleline);
        Assert.True(match.Success, $"TS union {tsName} not found in catalog.ts");
        return Regex.Matches(match.Groups[1].Value, "\"([a-zA-Z0-9]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
    }

    static string ToCamelCase(string pascal) =>
        pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];

    static string ReadSrc(string project, params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", project }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
