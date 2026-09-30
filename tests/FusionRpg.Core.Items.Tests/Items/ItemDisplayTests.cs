using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Generation;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `item-card` (item module 10) — the projection that turns atoms into text a player can read.
/// Everything here is pure Core, no browser, no Unity (SC8).
/// </summary>
public class ItemDisplayTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static List<DisplayTemplateRow> LoadAllTemplates()
    {
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "display-templates");
        return Directory.EnumerateFiles(dir, "*.json")
            .SelectMany(f => DisplayTemplates.Parse(File.ReadAllText(f)))
            .ToList();
    }

    // ---- N1: the already-authored template corpus --------------------------------------------

    /// <summary>
    /// 98 → 107 on 2026-09-06: the nine formerly phantom families below were authored, and each one
    /// brought its own template row (seven into `triggered.json`, two into `derived.json`).
    /// 107 → 109 the same day (item-content T11): `atom.chill-punisher` and `atom.rot-punisher` were
    /// authored into `g-punisher.json` without matching rows, and got them (`disptpl.p3-050`/`-051`).
    ///
    /// <para>The count is kept AND the name is earned: the second assertion reads the real family
    /// corpus and requires one template per family, so the literal above can never again be the only
    /// thing standing between a newly authored family and a raw id on a tooltip.</para>
    /// </summary>
    [Fact]
    public void Every_shipped_family_has_a_display_template()
    {
        // CONTRACT, not a count. The template corpus is generator-authored one row per shipped family,
        // so both the row total and the family total move every generation (98 → 107 → 109 → 112 → 125
        // and climbing), and pinning either is stale by construction. What must hold structurally is the
        // 1:1 the count was proxying for: every shipped family resolves to exactly one template row, and
        // no template names a family that is not shipped. A missing template (raw id on a tooltip) or a
        // stale orphan row is the defect this catches, independent of how many families ship.
        var templates = LoadAllTemplates();
        Assert.NotEmpty(templates);

        var templated = templates.Select(r => r.RuntimeFamily).ToHashSet(StringComparer.Ordinal);
        var familyDir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "affix-families");
        var families = Directory.EnumerateFiles(familyDir, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .SelectMany(f => AffixFamilyFile.Read(Path.GetFileName(f), File.ReadAllText(f)))
            .Select(f => f.Id)
            .ToList();

        Assert.NotEmpty(families);
        Assert.Empty(families.Where(id => !templated.Contains(id)).OrderBy(id => id, StringComparer.Ordinal));

        // Exactly one row per family — a duplicate template would silently drop one from a lookup.
        var duplicateFamilyRows = templates.GroupBy(r => r.RuntimeFamily, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Empty(duplicateFamilyRows);

        // And no orphan rows: a template for a family that no longer ships is dead content.
        var familySet = families.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(templated.Where(id => !familySet.Contains(id)).OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>
    /// ✅ <b>Closed 2026-09-06.</b> This test used to pin the opposite: eight `implicit.family` values
    /// used by real `base-types/infusion/**` entries (and by several `sets`/`uniques` atom lists) named
    /// no `affix-families/*.json` entry at all, despite sitting in `classes.v2.json`'s own frozen
    /// `infusion.legalFamilies` and despite `atom-family-library.md` §3.4 calling every one of them
    /// "shipped". A ninth, <c>atom.elemental-power</c>, had the same shape from a different direction —
    /// it existed only inside <c>_exemplars/affix-family.exemplar.json</c>, which
    /// <c>SeedFile.IsExemplar</c> excludes from the corpus by construction.
    ///
    /// <para>All nine are now authored, and the sense of the assertion is inverted rather than the test
    /// deleted: the set is still named explicitly, so a regression that drops one of them fails HERE,
    /// with the same nine names in front of the reader, instead of vanishing into a count.</para>
    /// </summary>
    [Fact]
    public void The_nine_formerly_phantom_families_now_resolve_to_a_display_template()
    {
        string[] formerlyPhantom =
        {
            "atom.buttering", "atom.chilling", "atom.blighting", "atom.rotting",
            "atom.sparking", "atom.marking", "atom.bonding", "atom.affliction",
            "atom.elemental-power",
        };
        var templated = LoadAllTemplates().Select(r => r.RuntimeFamily).ToHashSet(StringComparer.Ordinal);

        foreach (var family in formerlyPhantom)
            Assert.Contains(family, templated);
    }

    /// <summary>
    /// The other half of the same fix: a template row is only half a definition, so the nine also have
    /// to resolve against the real `affix-families/*.json` corpus. Reads the families through the same
    /// parser the generator uses, so this cannot pass on a row the loader would refuse.
    /// </summary>
    [Fact]
    public void The_nine_formerly_phantom_families_now_resolve_to_a_real_affix_family()
    {
        string[] formerlyPhantom =
        {
            "atom.buttering", "atom.chilling", "atom.blighting", "atom.rotting",
            "atom.sparking", "atom.marking", "atom.bonding", "atom.affliction",
            "atom.elemental-power",
        };

        var dir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "affix-families");
        var byId = Directory.EnumerateFiles(dir, "*.json")
            .SelectMany(f => FusionRpg.Core.Effects.Atoms.Generation.AffixFamilyFile.Read(
                Path.GetFileName(f), File.ReadAllText(f)))
            .ToDictionary(e => e.Id, StringComparer.Ordinal);

        // The seven status.apply families carry a status, never a channel; the two stat.derived ones
        // carry a channel and an op. Asserted so a row that resolves but is shaped wrong still fails.
        foreach (var family in formerlyPhantom)
        {
            Assert.True(byId.ContainsKey(family), $"'{family}' names no affix-family entry");
            var entry = byId[family];
            Assert.False(string.IsNullOrEmpty(entry.KindId));
            Assert.False(string.IsNullOrEmpty(entry.PowerBand));
            if (entry.KindId == "stat.derived") Assert.False(string.IsNullOrEmpty(entry.Channel));
        }

        Assert.Equal("stat.derived", byId["atom.elemental-power"].KindId);
        Assert.Equal("combat.power.{variant}", byId["atom.elemental-power"].Channel);
        Assert.Equal("stat.derived", byId["atom.affliction"].KindId);
        Assert.Equal("status.power", byId["atom.affliction"].Channel);

        // The seven that carry a status.apply rider, named rather than sliced off the array above.
        string[] statusApply =
        {
            "atom.buttering", "atom.chilling", "atom.blighting", "atom.rotting",
            "atom.sparking", "atom.marking", "atom.bonding",
        };
        Assert.All(statusApply, f => Assert.Equal("status.apply", byId[f].KindId));
    }

    [Fact]
    public void Stalwart_renders_live_C2_is_fixed()
    {
        var stalwart = LoadAllTemplates().Single(r => r.RuntimeFamily == "atom.stalwart");
        Assert.Equal("live", stalwart.Status);
    }

    [Fact]
    public void Every_template_resolves_with_no_leftover_placeholder_for_both_frames()
    {
        foreach (var row in LoadAllTemplates())
        {
            var placeholders = DisplayTemplates.PlaceholdersOf(row.Template);
            var args = placeholders.ToDictionary(p => p, p => "42", StringComparer.Ordinal);

            foreach (var frame in new[] { "humanoid", "plant" })
            {
                var rendered = DisplayTemplates.Render(row, frame, args);
                Assert.DoesNotContain('{', rendered);
                Assert.DoesNotContain('}', rendered);
                Assert.False(string.IsNullOrWhiteSpace(rendered));
            }
        }
    }

    [Fact]
    public void A_plant_frame_item_uses_the_override_where_present_and_the_humanoid_template_otherwise()
    {
        var evasion = LoadAllTemplates().Single(r => r.RuntimeFamily == "atom.evasion");
        Assert.NotNull(evasion.PlantOverrideTemplate);

        var args = new Dictionary<string, string> { ["value"] = "20", ["element"] = "fire" };
        var humanoid = DisplayTemplates.Render(evasion, "humanoid", args);
        var plant = DisplayTemplates.Render(evasion, "plant", args);
        Assert.NotEqual(humanoid, plant);

        var vitality = LoadAllTemplates().Single(r => r.RuntimeFamily == "atom.vitality");
        var vArgs = new Dictionary<string, string> { ["value"] = "45" };
        Assert.Equal(DisplayTemplates.Render(vitality, "humanoid", vArgs), DisplayTemplates.Render(vitality, "plant", vArgs));
    }

    [Fact]
    public void A_pending_status_family_is_refused_by_the_line_producer()
    {
        var atom = new AtomRow { AtomId = "atom.entangling.t1", KindId = "status.apply", FamilyId = "atom.entangling", Tier = 1, Name = "Entangling" };
        var template = LoadAllTemplates().Single(r => r.RuntimeFamily == "atom.entangling");
        Assert.Equal("pending", template.Status);

        Assert.Throws<DisplayTemplateRejection>(() =>
            ItemDisplayRenderer.Line(template, atom, "humanoid", 100, SourceKind.AffixPrefix, 0, UnitClass.PerMilleRatio));
    }

    // ---- Rule 1: the shipped percent conversion, adopted -----------------------------------------

    [Theory]
    [InlineData(150, "15%")]
    [InlineData(153, "15.3%")]
    [InlineData(10, "1%")]
    [InlineData(1, "0.1%")]
    public void FormatPerMille_matches_the_shipped_patronView_conversion(int milli, string expected) =>
        Assert.Equal(expected, ItemDisplayRenderer.FormatPerMille(milli));

    [Fact]
    public void A_nonzero_per_mille_never_renders_as_zero_percent()
    {
        var rendered = ItemDisplayRenderer.FormatPerMille(1);
        Assert.NotEqual("0%", rendered);
    }

    [Theory]
    [InlineData(250, "250 ms")]
    [InlineData(4000, "4.0 s")]
    public void FormatMilliseconds_renders_short_durations_as_ms_and_long_as_seconds(int ms, string expected) =>
        Assert.Equal(expected, ItemDisplayRenderer.FormatMilliseconds(ms));

    // ---- T17 (combat-math-dedup): the sigmoid context read is the sigmoid's shift, not delta/scale --

    /// <summary>
    /// The fixture is `docs/design/spec-magnitude-and-units.md` §4.1/§14 D.1's own worked example
    /// (`+150 crit rate` reads `≈ 31.8 pp`), and the same values
    /// `gk-web/web/fusion-rpg-web/src/i18n/magnitude.ts`'s `formatSigmoidContext` test pins. This C# side used
    /// to return `deltaPoints / scale` (1.0 pp at delta=100), which is not a sigmoid at all.
    ///
    /// <para><b>Still open on T17:</b> the cross-language parity test and the TS constants' check
    /// against <c>gk-core/data/tuning/stats.v1.json</c> live under <c>gk-web/web/fusion-rpg-web/**</c>, outside this
    /// lane's fence. The reference label and sign glyph stay the web surface's; this helper owns the
    /// shared numeric read only.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 100.0, "≈ 0.0 pp")]
    [InlineData(30, 100.0, "≈ 7.4 pp")]
    [InlineData(150, 100.0, "≈ 31.8 pp")]
    [InlineData(-150, 100.0, "≈ -31.8 pp")]
    public void FormatSigmoidContext_is_the_sigmoids_shift_from_neutral_in_pp(
        int deltaPoints, double scale, string expected) =>
        Assert.Equal(expected, ItemDisplayRenderer.FormatSigmoidContext(deltaPoints, scale));

    // ---- roll-quality bar: only OnInstantiate gets one -------------------------------------------

    [Fact]
    public void Fixed_roll_policy_never_gets_a_bar()
    {
        Assert.Null(ItemDisplayRenderer.BarFor(RollPolicy.Fixed, 1000));
    }

    [Fact]
    public void OnApply_never_gets_a_bar_the_hit_rolled_it_not_the_item()
    {
        Assert.Null(ItemDisplayRenderer.BarFor(RollPolicy.OnApply, 500));
    }

    [Fact]
    public void A_real_roll_never_renders_as_an_empty_bar()
    {
        var bar = ItemDisplayRenderer.BarFor(RollPolicy.OnInstantiate, qualityPerMille: 1);
        Assert.NotNull(bar);
        Assert.True(bar!.Value.Segments >= 1);
    }

    [Theory]
    [InlineData(0, 1)]    // the floor: even the worst roll shows one segment, never an empty bar
    [InlineData(200, 1)]
    [InlineData(201, 2)]
    [InlineData(999, 5)]
    [InlineData(1000, 5)]
    public void Segments_scale_with_quality_and_clamp_to_five(int qualityPerMille, int expectedSegments)
    {
        var bar = ItemDisplayRenderer.BarFor(RollPolicy.OnInstantiate, qualityPerMille);
        Assert.Equal(expectedSegments, bar!.Value.Segments);
    }

    // ---- ChannelUnits: N3 is already-shipped, this is the facade ---------------------------------

    [Fact]
    public void Primary_channels_resolve_to_game_units()
    {
        Assert.Equal(UnitClass.GameUnits, ChannelUnits.For("maxHp"));
        Assert.Equal(UnitClass.GameUnits, ChannelUnits.For("atk"));
    }

    [Fact]
    public void Attack_interval_resolves_to_milliseconds()
    {
        Assert.Equal(UnitClass.Milliseconds, ChannelUnits.For("attackInterval"));
    }

    [Fact]
    public void A_status_power_derived_channel_resolves_via_the_shipped_registry()
    {
        var unit = ChannelUnits.For("status.power.dot");
        Assert.Equal(UnitClass.StatusPotencyPoints, unit);
    }

    [Fact]
    public void An_unknown_channel_resolves_to_null_never_a_guess()
    {
        Assert.Null(ChannelUnits.For("not.a.real.channel"));
    }

    // ---- rarity palette ---------------------------------------------------------------------------

    [Fact]
    public void The_dark_palette_l_star_runs_from_42_to_92_and_is_monotone()
    {
        var result = RarityPalette.Validate(RarityPalette.Dark, RarityPalette.LightnessDirection.Increasing);
        Assert.True(result.Ok, string.Join("\n", result.Failures));

        Assert.Equal(42.1, RarityPalette.LStar(RarityPalette.Dark[0]), 1);
        Assert.Equal(91.9, RarityPalette.LStar(RarityPalette.Dark[^1]), 1);
    }

    [Fact]
    public void Dark_and_light_palettes_both_satisfy_the_monotone_rules()
    {
        var dark = RarityPalette.Validate(RarityPalette.Dark, RarityPalette.LightnessDirection.Increasing);
        var light = RarityPalette.Validate(RarityPalette.Light, RarityPalette.LightnessDirection.Decreasing);

        Assert.True(dark.Ok, string.Join("\n", dark.Failures));
        Assert.True(light.Ok, string.Join("\n", light.Failures));
    }

    [Fact]
    public void Rung_name_meets_wcag_aa_on_the_light_theme_ground()
    {
        foreach (var hex in RarityPalette.Light)
            Assert.True(RarityPalette.RungNameMeetsWcagAa(hex, "#ffffff"), $"{hex} fails WCAG AA against white");
    }

    [Fact]
    public void Pip_count_and_display_key_do_not_fork_only_color_hex_gains_a_second_value()
    {
        // Structural guarantee: both palettes are the SAME length (one slot per rung), and nothing in
        // this module carries a per-theme pip count or display key -- only the hex list itself forks.
        Assert.Equal(RarityPalette.Dark.Count, RarityPalette.Light.Count);
        // Reads CreatureRarityLadder.RungCount live rather than a literal (population-pin SE3.3,
        // 2026-09-19) -- one colour token per rarity rung, chaff..almanac.
        Assert.Equal(FusionRpg.Core.Creatures.CreatureRarityLadder.RungCount, RarityPalette.Dark.Count);
    }

    [Fact]
    public void A_uniform_palette_fails_the_adjacent_delta_rule()
    {
        // Negative control: proves Validate() actually rejects a bad palette rather than always passing.
        var flat = Enumerable.Repeat("#808080", 10).ToList();
        var result = RarityPalette.Validate(flat, RarityPalette.LightnessDirection.Increasing);
        Assert.False(result.Ok);
    }
}
