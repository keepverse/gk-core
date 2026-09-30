using System.Text.Json;
using FusionRpg.Core.Items;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `rarity-bands` (item module 7) against the REAL, shipped `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` and the
/// frozen `gk-data/packs/fusion/data/seed/items/_registry/core.v1.json` — never a copy, for the same reason
/// <c>SlotRolesTests</c> reads the real `core.v1.json`: a fixture cannot catch a seed file drifting
/// from the registry it must match.
/// </summary>
public class RarityLadderTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    sealed record SeededRung(string Id, int Ordinal, int PrefixRolls, int SuffixRolls, int MinTier, int MaxTier);

    static List<SeededRung> LoadSeed()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "rarity", "ladder.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<SeededRung>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            result.Add(new SeededRung(
                e.GetProperty("id").GetString()!,
                e.GetProperty("ordinal").GetInt32(),
                e.GetProperty("prefixRolls").GetInt32(),
                e.GetProperty("suffixRolls").GetInt32(),
                e.GetProperty("minTier").GetInt32(),
                e.GetProperty("maxTier").GetInt32()));
        }

        return result;
    }

    sealed record RegistryRung(string Id, int Ordinal, int? TierMin, int? TierMax, int CountMin, int CountMax, string ColourToken);

    static List<RegistryRung> LoadRegistry()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry", "core.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<RegistryRung>();
        foreach (var e in doc.RootElement.GetProperty("rarity").GetProperty("ladder").EnumerateArray())
        {
            var tw = e.TryGetProperty("tierWindow", out var t) && t.ValueKind == JsonValueKind.Object ? t : (JsonElement?)null;
            var cb = e.GetProperty("countBand");
            result.Add(new RegistryRung(
                e.GetProperty("id").GetString()!,
                e.GetProperty("ordinal").GetInt32(),
                tw?.GetProperty("min").GetInt32(),
                tw?.GetProperty("max").GetInt32(),
                cb.GetProperty("min").GetInt32(),
                cb.GetProperty("max").GetInt32(),
                e.GetProperty("colourToken").GetString()!));
        }

        return result;
    }

    [Fact]
    public void The_ten_rungs_match_the_frozen_registry_exactly()
    {
        var seed = LoadSeed();
        var registry = LoadRegistry();

        // The count is fully implied by the element-wise equality below -- never pinned separately
        // (population-pin SE3.3, 2026-09-19).
        Assert.Equal(RarityLadder.RungIds, seed.Select(s => s.Id));
        Assert.Equal(registry.Select(r => r.Id), RarityLadder.RungIds);

        foreach (var s in seed)
        {
            var r = registry.Single(x => x.Id == s.Id);
            Assert.Equal(r.Ordinal, s.Ordinal);
            if (r.TierMin is not null)
            {
                Assert.Equal(r.TierMin, s.MinTier);
                Assert.Equal(r.TierMax, s.MaxTier);
            }
        }
    }

    [Fact]
    public void Rarity_ordinal_is_never_the_enum_member_index()
    {
        // The two-space rule, as a test rather than a comment (spec-rarity-bands.md): the registry
        // space is 10..100, spaced by 10 -- never the consecutive 0..9 a C# enum member index would give.
        var registry = LoadRegistry();
        Assert.Equal(Enumerable.Range(1, 10).Select(n => n * 10), registry.Select(r => r.Ordinal));
        Assert.DoesNotContain(registry, r => r.Ordinal < 10);
    }

    [Fact]
    public void Every_rung_halves_sum_to_its_published_count_band_floor()
    {
        // sprout and heirloom are the E3-corrected rows -- red before the fix, per spec's own note.
        var seed = LoadSeed();
        var registry = LoadRegistry();

        foreach (var s in seed)
        {
            var r = registry.Single(x => x.Id == s.Id);
            Assert.Equal(r.CountMin, s.PrefixRolls + s.SuffixRolls);
        }
    }

    [Fact]
    public void A_window_step_keeps_the_halves_of_the_rung_immediately_below()
    {
        // §3.4's alternation: grafted/fused/heirloom/sunwoven are window steps and must carry the
        // exact halves of the count step directly below them (sprout/cultivated/chimeric/firstseed).
        var byId = LoadSeed().ToDictionary(s => s.Id, s => s);

        (string window, string below)[] pairs =
        {
            ("grafted", "sprout"), ("fused", "cultivated"), ("heirloom", "chimeric"), ("sunwoven", "firstseed"),
        };

        foreach (var (window, below) in pairs)
        {
            Assert.Equal(byId[below].PrefixRolls, byId[window].PrefixRolls);
            Assert.Equal(byId[below].SuffixRolls, byId[window].SuffixRolls);
        }
    }

    [Fact]
    public void Promote_from_is_one_on_all_ten_rungs()
    {
        // D7 lifted rule 7 -- the old "0 for ordinals 80-100" row is gone.
        foreach (var id in RarityLadder.RungIds)
            Assert.Equal(1, RarityLadder.PromoteFrom(id));
    }

    [Fact]
    public void Only_heirloom_and_sunwoven_are_pity_guarded()
    {
        // Almanac is deliberately unguarded: D7 lifted rule 7, so it is reachable by promotion.
        foreach (var id in RarityLadder.RungIds)
            Assert.Equal(id is "heirloom" or "sunwoven", RarityLadder.IsPityGuarded(id));
    }

    [Fact]
    public void No_rarity_id_collides_with_a_slot_role()
    {
        // The two-axes guard: rarity and role are different closed vocabularies and must never share
        // a string id, or a lookup keyed by one could silently resolve against the other.
        var rarityIds = RarityLadder.RungIds.ToHashSet(StringComparer.Ordinal);
        var roleIds = Enum.GetValues<ItemRole>().Select(ItemRoles.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(rarityIds.Intersect(roleIds));
    }

    [Fact]
    public void Every_rung_has_a_distinct_colour_token()
    {
        var registry = LoadRegistry();
        Assert.Equal(10, registry.Select(r => r.ColourToken).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
