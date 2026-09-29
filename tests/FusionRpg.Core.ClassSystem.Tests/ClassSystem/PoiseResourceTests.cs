using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>
/// spec-poise-resource.md §8 — registers the sixth actor resource. Tests 2-8 (test 1,
/// SpecChannelClaimTests, already exists and is asserted separately). "It is a row, not a
/// system" (§3): every assertion here is the SAME loop ActorChannelsTests already runs over the
/// other five, now walking six -- proving no special case was added for poise.
/// </summary>
public class PoiseResourceTests
{
    [Fact]
    public void Poise_registers_all_three_channels()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceMax("poise"), out _));
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceRegen("poise"), out _));
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceEfficiency("poise"), out _));
    }

    [Fact]
    public void Poise_channels_match_the_other_five()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceMax("hp"), out var hpMax));
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceMax("poise"), out var poiseMax));
        Assert.Equal(hpMax.Compose, poiseMax.Compose);
        Assert.Equal(hpMax.Cap, poiseMax.Cap);
        Assert.Equal(hpMax.Class, poiseMax.Class);

        Assert.True(registry.TryGet(DerivedStatChannels.ResourceRegen("hp"), out var hpRegen));
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceRegen("poise"), out var poiseRegen));
        Assert.Equal(hpRegen.Compose, poiseRegen.Compose);
        Assert.Equal(hpRegen.Cap, poiseRegen.Cap);
        Assert.Equal(hpRegen.Class, poiseRegen.Class);

        Assert.True(registry.TryGet(DerivedStatChannels.ResourceEfficiency("hp"), out var hpEff));
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceEfficiency("poise"), out var poiseEff));
        Assert.Equal(hpEff.Compose, poiseEff.Compose);
        Assert.Equal(hpEff.Cap, poiseEff.Cap);
        Assert.Equal(DerivedStatPolicy.ResourceEfficiencyCap, poiseEff.Cap);
        Assert.Equal(hpEff.Class, poiseEff.Class);
    }

    [Fact]
    public void Roster_and_ResourceIds_agree_in_order()
    {
        var repoRoot = FindRepoRoot();
        var rosterPath = Path.Combine(repoRoot, "data", "seed", "resources", "roster.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rosterPath));
        var rosterIds = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => (Id: e.GetProperty("id").GetString()!, Ordinal: e.GetProperty("ordinal").GetInt32()))
            .OrderBy(e => e.Ordinal)
            .Select(e => e.Id)
            .ToList();

        Assert.Equal(DerivedStatChannels.ResourceIds, rosterIds);
        Assert.Equal("poise", rosterIds[5]);
        Assert.Equal(5, rosterIds.IndexOf("poise"));
    }

    [Fact]
    public void Stamina_no_longer_claims_guard()
    {
        var repoRoot = FindRepoRoot();

        var rosterPath = Path.Combine(repoRoot, "data", "seed", "resources", "roster.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rosterPath));
        var stamina = doc.RootElement.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == "stamina");
        var staminaNote = stamina.GetProperty("paysNote").GetString()!;
        Assert.DoesNotContain("guard", staminaNote, StringComparison.OrdinalIgnoreCase);

        var poise = doc.RootElement.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == "poise");
        Assert.Equal("guard", poise.GetProperty("pays").GetString());

        var hubText = ReadNormalized(Path.Combine(repoRoot, "docs", "architecture", "resource-hub-ssot.md"));
        // SS2's pays-for table row for `stamina` must not list guard among its actions.
        var staminaRow = Regex.Match(hubText, @"^\|\s*`stamina`\s*\|.*\|$", RegexOptions.Multiline);
        Assert.True(staminaRow.Success, "resource-hub-ssot.md SS2 has no `stamina` row.");
        Assert.DoesNotContain("guard", staminaRow.Value, StringComparison.OrdinalIgnoreCase);

        var poiseRow = Regex.Match(hubText, @"^\|\s*`poise`\s*\|.*\|$", RegexOptions.Multiline);
        Assert.True(poiseRow.Success, "resource-hub-ssot.md SS2 has no `poise` row.");
        Assert.Contains("Guard", poiseRow.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Poise_exhaustion_rule_is_documented_and_the_roster_note_does_not_violate_it()
    {
        // No exhaustion-debuff CONTENT exists anywhere in the repo yet for ANY of the six resources
        // (resource-hub-ssot.md SS10 is design-only -- grep confirms zero "exhaustion" hits in src/).
        // So this cannot be an end-to-end behavioural proof; it is the strongest mechanisable form
        // available today (founding constraint #2, class-system-plan.md SS0): the spiral rule is
        // pinned in the SSOT text, and poise's own roster note is checked for the specific mistake
        // the rule warns against ("regen comes back slower" is the wrong, tempting phrasing).
        var repoRoot = FindRepoRoot();
        var hubText = ReadNormalized(Path.Combine(repoRoot, "docs", "architecture", "resource-hub-ssot.md"));
        Assert.Contains(
            "An exhaustion debuff must never touch a channel feeding its own resource's regen.",
            hubText, StringComparison.Ordinal);

        var rosterPath = Path.Combine(repoRoot, "data", "seed", "resources", "roster.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rosterPath));
        var poiseNote = doc.RootElement.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == "poise")
            .GetProperty("paysNote").GetString()!;
        Assert.DoesNotContain("regen", poiseNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Six_resources_everywhere()
    {
        Assert.Equal(6, DerivedStatChannels.ResourceIds.Count);

        var repoRoot = FindRepoRoot();
        var rosterPath = Path.Combine(repoRoot, "data", "seed", "resources", "roster.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rosterPath));
        Assert.Equal(6, doc.RootElement.GetProperty("entries").GetArrayLength());

        var hubText = ReadNormalized(Path.Combine(repoRoot, "docs", "architecture", "resource-hub-ssot.md"));
        Assert.Contains("Six actor resources", hubText, StringComparison.Ordinal);
        Assert.DoesNotContain("Five actor resources", hubText, StringComparison.Ordinal);

        var decisionsText = File.ReadAllText(Path.Combine(repoRoot, "docs", "architecture", "decisions.md"));
        Assert.Contains("Six actor resources", decisionsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_goldens_move_composing_with_no_modifiers()
    {
        // Registering an unfed channel changes no value -- the direct, targeted half of the
        // "zero goldens moved" claim (the systemic half is the full existing suite passing unchanged,
        // asserted by running the whole project, not by this one test).
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var snapshot = composer.Compose();

        Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ResourceMax("poise")));
        Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ResourceRegen("poise")));
        Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ResourceEfficiency("poise")));
    }

    static string ReadNormalized(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
