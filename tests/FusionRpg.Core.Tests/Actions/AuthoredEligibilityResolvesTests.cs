using System.Runtime.CompilerServices;
using System.Text.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Eligibility;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// **S5 — the authored eligibility content actually resolves.**
///
/// <para><c>EligibilityAxisTests</c> already proves the <b>mechanism</b> thoroughly, but every one of
/// its scope cases is a synthetic row. Nothing read the <b>shipped, committed</b> content, so a
/// `family`/`species` row whose `scopeKey` had gone stale — the exact risk S5 was parked on while the
/// creature corpus was being re-classified — would have resolved to nothing, silently, and no test would
/// have said a word.</para>
///
/// <para>This file closes S5's two acceptance clauses against the real files:
/// <b>authored rows resolve</b>, and <b>S1's neutral invariant still holds for an actor with no
/// species</b>.</para>
/// </summary>
public class AuthoredEligibilityResolvesTests
{
    static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                        // tests/.../Actions
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));  // repo root
    }

    sealed record Authored(string Id, string Scope, string? ScopeKey);

    /// <summary>Every committed action entry, read from the real seed files rather than a fixture.</summary>
    static List<Authored> AuthoredRows()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "actions");
        var rows = new List<Authored>();

        foreach (var file in new[] { "committed-round-1.json", "committed-round-2.json" })
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;

            foreach (var e in entries.EnumerateArray())
            {
                var scope = e.TryGetProperty("scope", out var s) ? s.GetString() ?? "general" : "general";
                var key = e.TryGetProperty("scopeKey", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString()
                    : null;
                rows.Add(new Authored(e.GetProperty("id").GetString()!, scope, key));
            }
        }
        return rows;
    }

    static IReadOnlyDictionary<string, IReadOnlyList<string>> RealFamilyMap() =>
        FamilyMap.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "actions", "_generated", "family-map.json")));

    /// <summary>The family ids a `family`-scoped row may name: the live map's values UNION the
    /// `families.v1.json` compatibility registry, matching `vocab.py:load_family_map_keys` exactly.
    /// The registry keeps previously committed rows loadable while the live roster's family vocabulary
    /// evolves — a scoped row naming a registry family is shipped and intentional, not dangling.</summary>
    static HashSet<string> LoadableFamilyIds()
    {
        var families = RealFamilyMap().Values.SelectMany(v => v).ToList();
        var registryPath = Path.Combine(RepoRoot(), "data", "seed", "creatures", "_registry", "families.v1.json");
        if (File.Exists(registryPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(registryPath));
            if (doc.RootElement.TryGetProperty("families", out var reg) && reg.ValueKind == JsonValueKind.Object)
                foreach (var p in reg.EnumerateObject()) families.Add(p.Name);
        }
        return families.ToHashSet(StringComparer.Ordinal);
    }

    static ActionRow Row(Authored a) => new()
    {
        ActionId = a.Id,
        Scope = a.Scope switch
        {
            "family" => EligibilityScope.Family,
            "species" => EligibilityScope.Species,
            _ => EligibilityScope.General,
        },
        ScopeKey = a.ScopeKey,
    };

    /// <summary>
    /// ⭐ **S5 clause 1 — every authored scope key names something that exists.**
    ///
    /// <para>A `family` row's key must be a family the map actually assigns, and a `species` row's key
    /// must be a species the creature catalog actually ships. A dangling key is not a crash: the row
    /// simply never joins any candidate set, so the action is authored, shipped, and unreachable.
    /// That is the failure this asserts against, and it is invisible without a check like this.</para>
    /// </summary>
    [Fact]
    public void Every_authored_scope_key_resolves_against_the_shipped_content()
    {
        var rows = AuthoredRows();
        var families = LoadableFamilyIds();

        var indexPath = Path.Combine(RepoRoot(), "data", "seed", "creatures", "species", "_index.json");
        var species = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(indexPath))!
            .Keys.Select(k => k.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var dangling = new List<string>();
        var familyRows = 0;
        var speciesRows = 0;

        foreach (var r in rows)
        {
            switch (r.Scope)
            {
                case "family":
                    familyRows++;
                    if (r.ScopeKey is null || !families.Contains(r.ScopeKey))
                        dangling.Add($"{r.Id}: family '{r.ScopeKey}' is not a family the map assigns");
                    break;
                case "species":
                    speciesRows++;
                    if (r.ScopeKey is null || !species.Contains(r.ScopeKey))
                        dangling.Add($"{r.Id}: species '{r.ScopeKey}' is not in the creature catalog");
                    break;
            }
        }

        // Liveness: if the seed files move or the parse silently yields nothing, "no danglers" would be
        // trivially true. Assert we actually read scoped content before trusting the result.
        Assert.True(familyRows > 0, "no family-scoped authored rows were read — the parse or the path is wrong");
        Assert.True(speciesRows > 0, "no species-scoped authored rows were read — the parse or the path is wrong");
        Assert.Empty(dangling);
    }

    /// <summary>
    /// ⭐ **S5 clause 2 — S1's neutral invariant, against the real content.** An actor with no species
    /// key gets the general tier and nothing else: no family row, no species row, no accidental match.
    ///
    /// <para>The specific accident guarded here is two nulls comparing equal — an actor with a null key
    /// against a row with a null key — which would make a mis-authored row universal. `EligibilityAxisTests`
    /// checks that with a planted synthetic row; this checks it holds over everything actually shipped.</para>
    /// </summary>
    [Fact]
    public void An_actor_with_no_species_sees_only_general_authored_rows()
    {
        var all = AuthoredRows().Select(Row).ToList();
        var candidates = ActionEligibility.Candidates(all, actorSpeciesKey: null, familyOf: RealFamilyMap());

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Equal(EligibilityScope.General, c.Scope));
        Assert.Equal(all.Count(a => a.Scope == EligibilityScope.General), candidates.Count);
    }

    /// <summary>
    /// **The positive half — a real species really does reach its family's authored actions.** Clause 1
    /// only proves no key dangles; without this, a `familyOf` lookup that returned nothing for everyone
    /// would still pass it. Driven by whichever authored family row the content actually carries, so it
    /// does not hardcode a species that a future re-classification may rename.
    /// </summary>
    [Fact]
    public void A_species_in_a_mapped_family_reaches_that_familys_authored_rows()
    {
        var map = RealFamilyMap();
        var all = AuthoredRows().Select(Row).ToList();

        var authoredFamilies = all.Where(r => r.Scope == EligibilityScope.Family)
                                  .Select(r => r.ScopeKey!)
                                  .ToHashSet(StringComparer.Ordinal);

        var pair = map.FirstOrDefault(kv => kv.Value.Any(authoredFamilies.Contains));
        Assert.False(pair.Key is null,
            "no mapped species belongs to any family that has an authored action — family scope is shipped but unreachable");

        var candidates = ActionEligibility.Candidates(all, pair.Key, map);
        Assert.Contains(candidates, c => c.Scope == EligibilityScope.Family && authoredFamilies.Contains(c.ScopeKey!));
    }
}
