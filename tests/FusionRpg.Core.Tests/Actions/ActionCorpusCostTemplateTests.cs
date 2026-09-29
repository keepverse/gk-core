using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T59.1 (spec-action-instance-and-grant.md §2, narrowed during BUILD 2026-09-06): the ONE
/// per-category number `ActionTimingTuning` does not already own — a default onCommit resource cost
/// per `ActionCategory` for the corpus importer (T59.3) to fall back on. Mirrors
/// `ActionTimingTuningLoader`'s own established shape and this same convention's inline-JSON unit-test
/// style (tunables-ssot.md §7.2: "construct one inline; no fixture files").
/// </summary>
public class ActionCorpusCostTemplateTests
{
    // Mirrors the real gk-core/data/tuning/action-corpus-cost-templates.v1.json byte-for-byte at the time
    // this test was written (T7, basic-attack-seed, added the "kinds" block).
    const string FullTemplate = """
    {
      "schemaVersion": 1, "version": 1,
      "categories": {
        "attack": { "resourceId": "qi", "baseAmountAtRung1": 20, "timing": "onCommit" },
        "defense": { "resourceId": "qi", "baseAmountAtRung1": 30, "timing": "onCommit" },
        "support": { "resourceId": "qi", "baseAmountAtRung1": 40, "timing": "onCommit" },
        "movement": { "resourceId": "qi", "baseAmountAtRung1": 15, "timing": "onCommit" },
        "status": { "resourceId": "qi", "baseAmountAtRung1": 35, "timing": "onCommit" }
      },
      "kinds": {
        "basic": { "resourceId": "stamina", "baseAmountAtRung1": 20, "timing": "onCommit" },
        "innate": { "resourceId": "qi", "baseAmountAtRung1": 25, "timing": "onCommit" }
      }
    }
    """;

    static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                      // tests/.../Actions
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));  // repo root
    }

    /// <summary>The inline mirror above proves the loader's own contract; this proves the actually-
    /// shipped file has no typo the mirror wouldn't catch (matching `UnlockLadderTests.cs`'s own
    /// `Shipped` real-file pattern).</summary>
    [Fact]
    public void TheRealShippedFileLoadsAndCoversAllFiveCategories()
    {
        var path = Path.Combine(RepoRoot(), "data", "tuning", "action-corpus-cost-templates.v1.json");
        var template = ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(path));

        foreach (var category in new[] { ActionCategory.Attack, ActionCategory.Defense, ActionCategory.Support, ActionCategory.Movement, ActionCategory.Status })
        {
            var row = template.CategoryOf(category);
            Assert.False(string.IsNullOrWhiteSpace(row.ResourceId));
            Assert.True(row.BaseAmountAtRung1 > 0);
        }
    }

    [Fact]
    public void EveryCategoryLoadsItsOwnCompleteRow()
    {
        var template = ActionCorpusCostTemplateLoader.Parse(FullTemplate);

        var attack = template.CategoryOf(ActionCategory.Attack);
        Assert.Equal("qi", attack.ResourceId);
        Assert.Equal(20, attack.BaseAmountAtRung1);
        Assert.Equal(ActionCostTiming.OnCommit, attack.Timing);

        // Every one of the 5 categories resolves without throwing -- the exhaustive-coverage half of
        // the acceptance bar.
        foreach (var category in new[] { ActionCategory.Attack, ActionCategory.Defense, ActionCategory.Support, ActionCategory.Movement, ActionCategory.Status })
            template.CategoryOf(category); // must not throw
    }

    [Theory]
    [InlineData("attack")]
    [InlineData("defense")]
    [InlineData("support")]
    [InlineData("movement")]
    [InlineData("status")]
    public void AMissingCategoryRejectsNamingWhichOne(string missingKey)
    {
        var withoutOne = FullTemplate.Replace($"\"{missingKey}\": {{ \"resourceId\": \"qi\"", "\"__removed__\": { \"resourceId\": \"qi\"");
        Assert.NotEqual(FullTemplate, withoutOne); // the replace actually took effect

        var ex = Assert.Throws<ActionCorpusCostTemplateRejection>(() => ActionCorpusCostTemplateLoader.Parse(withoutOne));
        Assert.Contains(missingKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveBaseAmountIsRejected()
    {
        var zeroed = FullTemplate.Replace("\"baseAmountAtRung1\": 20", "\"baseAmountAtRung1\": 0");
        var ex = Assert.Throws<ActionCorpusCostTemplateRejection>(() => ActionCorpusCostTemplateLoader.Parse(zeroed));
        Assert.Contains("baseAmountAtRung1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTimingStringIsRejected()
    {
        var badTiming = FullTemplate.Replace("\"timing\": \"onCommit\"", "\"timing\": \"wheneverIFeelLikeIt\"");
        Assert.Throws<ActionCorpusCostTemplateRejection>(() => ActionCorpusCostTemplateLoader.Parse(badTiming));
    }

    /// <summary>T7 (basic-attack-seed): the real shipped file must load both `kinds` rows, not just the
    /// five `categories` this test already covered before this task.</summary>
    [Fact]
    public void TheRealShippedFileLoadsBothKindRows()
    {
        var path = Path.Combine(RepoRoot(), "data", "tuning", "action-corpus-cost-templates.v1.json");
        var template = ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(path));

        var basic = template.ResolveFor(ActionKind.Basic, ActionCategory.Attack);
        Assert.False(string.IsNullOrWhiteSpace(basic.ResourceId));
        Assert.True(basic.BaseAmountAtRung1 > 0);

        var innate = template.ResolveFor(ActionKind.Innate, ActionCategory.Attack);
        Assert.False(string.IsNullOrWhiteSpace(innate.ResourceId));
        Assert.True(innate.BaseAmountAtRung1 > 0);
    }

    /// <summary>T7's own acceptance criterion, exercised directly: Basic resolves `stamina`, Innate
    /// resolves `qi`, and the SAME brief's Category (Attack, whose own category row is also `qi` but a
    /// different amount) plays no part in either -- proving the resolution is Kind-driven, not merely
    /// "happens to be qi again".</summary>
    [Fact]
    public void ResolveForIsKindAwareBasicIsStaminaInnateIsQi()
    {
        var template = ActionCorpusCostTemplateLoader.Parse(FullTemplate);

        var basic = template.ResolveFor(ActionKind.Basic, ActionCategory.Attack);
        Assert.Equal("stamina", basic.ResourceId);
        Assert.Equal(20, basic.BaseAmountAtRung1);

        var innate = template.ResolveFor(ActionKind.Innate, ActionCategory.Attack);
        Assert.Equal("qi", innate.ResourceId);
        Assert.Equal(25, innate.BaseAmountAtRung1);
        // Distinct from the SAME category's own row (also qi, but a different amount) -- proves this
        // came from `kinds.innate`, not from `categories.attack` under a different name.
        Assert.NotEqual(template.CategoryOf(ActionCategory.Attack).BaseAmountAtRung1, innate.BaseAmountAtRung1);
    }

    /// <summary>The back-compat half of T7's acceptance bar: `ActionKind.Skill` — what every brief
    /// authored before `kindHint` existed still composes to — resolves EXACTLY through `CategoryOf`,
    /// byte-for-byte, never through `kinds`. This is what makes "kinds is required but Skill never
    /// looks at it" true.</summary>
    [Theory]
    [InlineData("attack")]
    [InlineData("defense")]
    [InlineData("support")]
    [InlineData("movement")]
    [InlineData("status")]
    public void ResolveForOnSkillIsByteIdenticalToCategoryOf(string categoryName)
    {
        var template = ActionCorpusCostTemplateLoader.Parse(FullTemplate);
        Assert.True(ActionCategories.TryParse(categoryName, out var category));

        Assert.Equal(template.CategoryOf(category), template.ResolveFor(ActionKind.Skill, category));
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("innate")]
    public void AMissingKindRowRejectsNamingWhichOne(string missingKey)
    {
        var withoutOne = FullTemplate.Replace(
            $"\"{missingKey}\": {{ \"resourceId\":", "\"__removed__\": { \"resourceId\":");
        Assert.NotEqual(FullTemplate, withoutOne); // the replace actually took effect

        var ex = Assert.Throws<ActionCorpusCostTemplateRejection>(() => ActionCorpusCostTemplateLoader.Parse(withoutOne));
        Assert.Contains(missingKey, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A standalone minimal literal (not derived from <see cref="FullTemplate"/> by string
    /// surgery, which the raw-string-literal whitespace stripping makes fragile) with no `kinds`
    /// object at all — pre-T7 shaped, and it must be rejected, not silently accepted with an empty
    /// `Kinds`.</summary>
    [Fact]
    public void AMissingKindsObjectEntirelyIsRejected()
    {
        const string noKinds = """
        {
          "schemaVersion": 1, "version": 1,
          "categories": {
            "attack": { "resourceId": "qi", "baseAmountAtRung1": 20, "timing": "onCommit" },
            "defense": { "resourceId": "qi", "baseAmountAtRung1": 30, "timing": "onCommit" },
            "support": { "resourceId": "qi", "baseAmountAtRung1": 40, "timing": "onCommit" },
            "movement": { "resourceId": "qi", "baseAmountAtRung1": 15, "timing": "onCommit" },
            "status": { "resourceId": "qi", "baseAmountAtRung1": 35, "timing": "onCommit" }
          }
        }
        """;

        var ex = Assert.Throws<ActionCorpusCostTemplateRejection>(() => ActionCorpusCostTemplateLoader.Parse(noKinds));
        Assert.Contains("kinds", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A kind that HAS no cost row (nothing calls `ResolveFor(Skill, ...)` other than through
    /// `CategoryOf`) never throws for lacking one -- only Basic/Innate need `kinds` rows.</summary>
    [Fact]
    public void ResolveForNeverConsultsKindsForSkill()
    {
        var noKindsAtAllTemplate = new ActionCorpusCostTemplate(new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>
        {
            [ActionCategory.Attack] = new("qi", 20, ActionCostTiming.OnCommit),
        }); // Kinds omitted entirely (defaults null) -- constructing this the pre-T7 one-argument way
            // must still compile and still resolve Skill correctly.

        var row = noKindsAtAllTemplate.ResolveFor(ActionKind.Skill, ActionCategory.Attack);
        Assert.Equal("qi", row.ResourceId);
    }

    [Fact]
    public void ResolveForRejectsANonSkillKindWithNoKindsRowAtAllNamingIt()
    {
        var noKindsAtAllTemplate = new ActionCorpusCostTemplate(new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>
        {
            [ActionCategory.Attack] = new("qi", 20, ActionCostTiming.OnCommit),
        }); // Kinds omitted -- Basic/Innate have nowhere to resolve and must reject, never silently
            // fall back to Category.

        var ex = Assert.Throws<ActionCorpusCostTemplateRejection>(() =>
            noKindsAtAllTemplate.ResolveFor(ActionKind.Basic, ActionCategory.Attack));
        Assert.Contains("basic", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>T59.1's other genuine gap: neither `ActionCompiler.Compile` nor
    /// `ActionTimingDerivation.Derive` ever sets `CooldownChannel`/`EffectivenessChannel` — confirmed
    /// by reading both during BUILD. `ActionCategoryChannels` is the structural (non-tunable) mapping
    /// this module adds instead; it must cover all 5 categories with the exact
    /// `skill.cooldown.*`/`skill.effectiveness.*` strings `DerivedStatChannels` already defines.</summary>
    [Fact]
    public void ActionCategoryChannelsCoversAllFiveCategoriesWithTheRealChannelStrings()
    {
        Assert.Equal(DerivedStatChannels.SkillCooldown("attack"), ActionCategoryChannels.CooldownChannelFor(ActionCategory.Attack));
        Assert.Equal(DerivedStatChannels.SkillCooldown("defense"), ActionCategoryChannels.CooldownChannelFor(ActionCategory.Defense));
        Assert.Equal(DerivedStatChannels.SkillCooldown("support"), ActionCategoryChannels.CooldownChannelFor(ActionCategory.Support));
        Assert.Equal(DerivedStatChannels.SkillCooldown("movement"), ActionCategoryChannels.CooldownChannelFor(ActionCategory.Movement));
        Assert.Equal(DerivedStatChannels.SkillCooldown("status"), ActionCategoryChannels.CooldownChannelFor(ActionCategory.Status));

        Assert.Equal(DerivedStatChannels.SkillEffectiveness("attack"), ActionCategoryChannels.EffectivenessChannelFor(ActionCategory.Attack));
        Assert.Equal(DerivedStatChannels.SkillEffectiveness("defense"), ActionCategoryChannels.EffectivenessChannelFor(ActionCategory.Defense));
        Assert.Equal(DerivedStatChannels.SkillEffectiveness("support"), ActionCategoryChannels.EffectivenessChannelFor(ActionCategory.Support));
        Assert.Equal(DerivedStatChannels.SkillEffectiveness("movement"), ActionCategoryChannels.EffectivenessChannelFor(ActionCategory.Movement));
        Assert.Equal(DerivedStatChannels.SkillEffectiveness("status"), ActionCategoryChannels.EffectivenessChannelFor(ActionCategory.Status));
    }
}
