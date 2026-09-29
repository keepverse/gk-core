using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// ⛔ THE NAMING FUNCTION (item module 8) — nothing owned this before; every dropped item was
/// nameless. `AffixNameTable` against the REAL, shipped `nameWords` corpus; `ItemNameComposer`
/// against synthetic rolls (the pure function itself needs no corpus).
/// </summary>
public class AffixNameTableTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static JsonElement FindFamily(string familyId)
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "affix-families");
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                if (e.GetProperty("id").GetString() == familyId)
                    return e;
        }

        throw new KeyNotFoundException(familyId);
    }

    [Fact]
    public void Every_family_has_a_name_word_for_every_band_its_tier_range_reaches()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "affix-families");
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (!e.TryGetProperty("nameWords", out var nw)) continue;
                foreach (var slot in new[] { "prefix", "suffix" })
                {
                    if (!nw.TryGetProperty(slot, out var rows)) continue;
                    var parsed = AffixNameTable.ParseSlot(rows);
                    Assert.NotEmpty(parsed);
                }
            }
        }
    }

    [Fact]
    public void An_element_family_supplies_one_word_per_variant_not_per_band()
    {
        var family = FindFamily("atom.elemental-defense");
        var rows = AffixNameTable.ParseSlot(family.GetProperty("nameWords").GetProperty("prefix"));
        Assert.All(rows, r => Assert.Null(r.Band));
        Assert.Contains(rows, r => r.Variant == "fire" && r.Word == "Ember");
        Assert.Contains(rows, r => r.Variant == "dark" && r.Word == "Umbral");
    }

    [Fact]
    public void A_regular_family_is_band_keyed_A_t1_t2_B_t3_C_t4_t5()
    {
        var family = FindFamily("atom.vitality");
        var rows = AffixNameTable.ParseSlot(family.GetProperty("nameWords").GetProperty("prefix"));
        Assert.All(rows, r => Assert.Null(r.Variant));
        Assert.Equal("A", AffixNameTable.BandOfTier(1));
        Assert.Equal("A", AffixNameTable.BandOfTier(2));
        Assert.Equal("B", AffixNameTable.BandOfTier(3));
        Assert.Equal("C", AffixNameTable.BandOfTier(4));
        Assert.Equal("C", AffixNameTable.BandOfTier(5));
    }

    [Fact]
    public void A_plant_frame_item_uses_wordPlant_where_present_and_the_humanoid_word_otherwise()
    {
        var family = FindFamily("atom.mending");
        var rows = AffixNameTable.ParseSlot(family.GetProperty("nameWords").GetProperty("prefix"));

        Assert.Equal("Verdant", AffixNameTable.Resolve(rows, tier: 5, variant: null, frame: "plant"));
        Assert.Equal("Restorative", AffixNameTable.Resolve(rows, tier: 5, variant: null, frame: "humanoid"));
        // Bands with no override fall through identically on both frames.
        Assert.Equal(AffixNameTable.Resolve(rows, tier: 1, variant: null, frame: "humanoid"),
                     AffixNameTable.Resolve(rows, tier: 1, variant: null, frame: "plant"));
    }

    [Fact]
    public void An_unlisted_variant_falls_back_to_the_first_word()
    {
        var family = FindFamily("atom.shield-capacity"); // 4 words, elements+omni
        var rows = AffixNameTable.ParseSlot(family.GetProperty("nameWords").GetProperty("prefix"));
        var fallback = AffixNameTable.Resolve(rows, tier: 3, variant: "omni", frame: "humanoid");
        Assert.Equal(rows[0].Word, fallback);
    }

    [Fact]
    public void A_bare_string_row_is_rejected() =>
        Assert.Throws<AffixNameRejection>(() => AffixNameTable.ParseSlot(JsonDocument.Parse("""["Ember","Frost"]""").RootElement));

    [Fact]
    public void A_row_with_both_band_and_variant_is_rejected() =>
        Assert.Throws<AffixNameRejection>(() =>
            AffixNameTable.ParseSlot(JsonDocument.Parse("""[{"band":"A","variant":"fire","word":"x"}]""").RootElement));

    /// <summary>
    /// ⛔ <b>Every band-keyed family must cover all three of D29's bands, or a perfectly ordinary roll
    /// throws while being named.</b> Found 2026-09-06 wiring the composer to the real corpus:
    /// <c>atom.bulwark</c> and <c>atom.tempo-stampede</c> each carried ONE row labelled
    /// <c>band: "A"</c> while declaring <c>tierRange: "t1-t5"</c> and while their own notes described a
    /// band-C-only word — three readings of the same fact. <c>FamilyExpansion</c> expands every family
    /// to all five tiers regardless of a "rare tier band only" flag, so a t4 bulwark atom really exists
    /// and really rolls; the one authored word now sits under all three bands.
    /// </summary>
    [Fact]
    public void Every_band_keyed_family_resolves_a_word_at_every_tier()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "affix-families");
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (Path.GetFileName(path).StartsWith('_')) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (!e.TryGetProperty("nameWords", out var nw)) continue;
                var id = e.GetProperty("id").GetString();

                foreach (var slot in new[] { "prefix", "suffix" })
                {
                    if (!nw.TryGetProperty(slot, out var rows)) continue;
                    var parsed = AffixNameTable.ParseSlot(rows);
                    if (parsed[0].Variant is not null) continue;   // variant-keyed: its own fallback rule

                    for (var tier = 1; tier <= 5; tier++)
                        Assert.False(
                            string.IsNullOrEmpty(AffixNameTable.Resolve(parsed, tier, null, "humanoid")),
                            $"{id}.{slot} resolves no word at tier {tier}");
                }
            }
        }
    }
}

public class ItemNameComposerTests
{
    static string Lookup(string familyId, string slot, int tier, string? variant) => $"{familyId}-{slot}-t{tier}";

    static (string, string) RareDraw(long seed) => ("Bramble", "Bite");

    [Fact]
    public void A_normal_item_is_named_by_its_base_type_alone()
    {
        var name = ItemNameComposer.Compose("Bark Helm", Array.Empty<NamedAffix>(), "humanoid", Lookup, RareDraw, 1);
        Assert.Equal("Bark Helm", name);
    }

    [Fact]
    public void A_magic_item_name_is_prefix_word_plus_base_name_plus_of_suffix_word()
    {
        var rolled = new[]
        {
            new NamedAffix(AffixClass.Prefix, "atom.vitality", 3, 1, null),
            new NamedAffix(AffixClass.Suffix, "atom.warded", 2, 2, null),
        };
        var name = ItemNameComposer.Compose("Bark Helm", rolled, "humanoid", Lookup, RareDraw, 1);
        Assert.Equal("atom.vitality-prefix-t3 Bark Helm of atom.warded-suffix-t2", name);
    }

    [Fact]
    public void A_single_prefix_only_roll_omits_the_of_clause()
    {
        var rolled = new[] { new NamedAffix(AffixClass.Prefix, "atom.vitality", 3, 1, null) };
        var name = ItemNameComposer.Compose("Bark Helm", rolled, "humanoid", Lookup, RareDraw, 1);
        Assert.Equal("atom.vitality-prefix-t3 Bark Helm", name);
    }

    [Fact]
    public void A_rare_item_gets_a_seeded_two_word_name_not_an_affix_name()
    {
        var rolled = Enumerable.Range(0, 3)
            .Select(i => new NamedAffix(AffixClass.Prefix, $"atom.f{i}", 3, i, null)).ToArray();
        var name = ItemNameComposer.Compose("Bark Helm", rolled, "humanoid", Lookup, RareDraw, 42);
        Assert.Equal("Bramble Bite", name);
    }

    [Fact]
    public void The_name_tiebreak_is_tier_desc_then_seq_asc_never_instance_or_binding_id()
    {
        // Two prefix candidates (below the 3-affix rare-name threshold): the lower tier carries the
        // later seq, so a naive instance/binding-id-ordered pick would choose wrong -- (tier DESC,
        // seq ASC) must choose the tier-4 one regardless.
        var rolled = new[]
        {
            new NamedAffix(AffixClass.Prefix, "atom.low-tier-late-seq", 2, 99, null),
            new NamedAffix(AffixClass.Prefix, "atom.high-tier", 4, 1, null),
        };
        var name = ItemNameComposer.Compose("Base", rolled, "humanoid", Lookup, RareDraw, 1);
        Assert.StartsWith("atom.high-tier-prefix-t4", name);
    }

    [Fact]
    public void A_mixed_affix_supplies_at_most_one_word_total()
    {
        // Only one rolled affix exists, and it is Mixed -- it may fill either the prefix or the
        // suffix slot, never both.
        var rolled = new[] { new NamedAffix(AffixClass.Mixed, "atom.hybrid", 3, 1, null) };
        var name = ItemNameComposer.Compose("Base", rolled, "humanoid", Lookup, RareDraw, 1);

        var usedAsPrefix = name == "atom.hybrid-prefix-t3 Base";
        var usedAsSuffix = name == "Base of atom.hybrid-suffix-t3";
        Assert.True(usedAsPrefix ^ usedAsSuffix, $"expected exactly one slot filled, got '{name}'");
    }

    /// <summary>
    /// ⛔ <b>Defect found and fixed 2026-09-06 (item-content T1).</b> All 153 suffix words in the
    /// shipped corpus author their own connective (<c>of Killing Frost</c>) — §4.12's sample table does
    /// too — while this function used to prepend one unconditionally, so a real item read
    /// <i>"Bark Helm of of Embers"</i>. Nothing caught it because the only caller was
    /// <see cref="Lookup"/> above, whose synthetic words carry no connective.
    ///
    /// <para>A corpus that authors a BARE word still gets the connective, which is why the other cases
    /// in this class are untouched — the grammar still owns it.</para>
    /// </summary>
    [Fact]
    public void A_suffix_word_that_already_carries_its_own_connective_is_not_given_a_second_one()
    {
        static string Authored(string familyId, string slot, int tier, string? variant) =>
            slot == "suffix" ? "of Embers" : "Sturdy";

        var rolled = new[]
        {
            new NamedAffix(AffixClass.Prefix, "atom.fortitude", 3, 1, null),
            new NamedAffix(AffixClass.Suffix, "atom.searing-strike", 4, 2, null),
        };

        Assert.Equal(
            "Sturdy Bark Helm of Embers",
            ItemNameComposer.Compose("Bark Helm", rolled, "humanoid", Authored, RareDraw, 1));

        // Suffix alone, same rule.
        Assert.Equal(
            "Bark Helm of Embers",
            ItemNameComposer.Compose("Bark Helm", rolled.Skip(1).ToArray(), "humanoid", Authored, RareDraw, 1));
    }

    [Fact]
    public void The_same_roll_seed_produces_a_byte_identical_name()
    {
        var rolled = new[] { new NamedAffix(AffixClass.Prefix, "atom.vitality", 3, 1, null) };
        var a = ItemNameComposer.Compose("Bark Helm", rolled, "humanoid", Lookup, RareDraw, 7);
        var b = ItemNameComposer.Compose("Bark Helm", rolled, "humanoid", Lookup, RareDraw, 7);
        Assert.Equal(a, b);
    }
}
