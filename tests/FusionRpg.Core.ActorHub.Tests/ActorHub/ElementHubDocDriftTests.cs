using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>
/// spec-element-families.md §6 — element-hub-ssot.md §6 was a hand-written table that drifted 44
/// channels out of date over three months because nothing enforced it. Its replacement states the
/// generation rule instead, but a generation *rule* still names the 28 families in prose (§6.2's
/// table) — this test is what stops THAT copy from drifting the same way. Reads the doc as text,
/// matching every other drift/guard test in this project (`ContentTableReaderGuardTests` and friends).
/// </summary>
public class ElementHubDocDriftTests
{
    [Fact]
    public void Section6MatchesGeneration()
    {
        var families = ExtractFamiliesFromElementHubSection6(ReadDoc("architecture", "element-hub-ssot.md"));
        var expected = DerivedStatChannels.CombatChannelFamilies.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), families.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Section6MatchesGeneration_failsOnAPlantedDrift()
    {
        // A guard never proven to fail is not evidence (the same rule guard-stat-pairs.ps1's planted
        // violations follow). Runs the REAL extraction function against synthetic doc text -- not just
        // a comparison of two in-memory sets -- so the regex itself is proven to catch a real drift,
        // not merely assumed to.
        const string missingOneFamily = """
            ## 6. Derived channel catalog

            | Family group | Families |
            |---|---|
            | Offense | `combat.power` · `combat.crit.rate` |

            ## 7. Omni rule
            """;
        var extracted = ExtractFamiliesFromElementHubSection6(missingOneFamily);
        Assert.NotEqual(DerivedStatChannels.CombatChannelFamilies.ToHashSet(StringComparer.Ordinal), extracted);

        const string extraInventedFamily = """
            ## 6. Derived channel catalog

            | Family group | Families |
            |---|---|
            | Offense | `combat.power` · `combat.invented` |

            ## 7. Omni rule
            """;
        var extractedExtra = ExtractFamiliesFromElementHubSection6(extraInventedFamily);
        Assert.NotEqual(DerivedStatChannels.CombatChannelFamilies.ToHashSet(StringComparer.Ordinal), extractedExtra);
        Assert.Contains("combat.invented", extractedExtra);
    }

    [Fact]
    public void StatSheetCountsMatchGeneration()
    {
        var text = ReadDesignDoc("spec-derived-stat-sheet.md");
        var combatExpected = DerivedStatChannels.CombatChannelFamilies.Count * (ElementRoster.Concrete.Count + 1);
        var registry = DerivedStatRegistry.CreateDefault();

        // solid-enforcement population-pin (SE3.2, 2026-09-19): both counts used to carry a literal
        // "sanity anchor" beside these two Assert.Contains lines (196 here, 268 for the registry) --
        // an UNMARKED, unowned literal duplicating a check the Contains line right below it already
        // makes for real: it reads the LIVE count and asserts the doc's text still names it, so a
        // real drift (the doc going stale, or the registry actually changing) fails HERE regardless.
        // The literal only forced a human to notice the number changing -- exactly the population-pin
        // shape AGENTS.md's hard rule forbids. `combatExpected`'s own history (196 = 28 combat
        // families x (6 concrete elements + 1 omni)) and the registry's own count history
        // (`AtomCatalogSsotDriftTests.cs`, the canonical `closed-vocabulary DerivedStatRegistry.
        // AllRegistered` pin) are the two places that trail now lives.
        Assert.Contains($"**{combatExpected}**", text);
        Assert.Contains($"**{registry.AllRegistered.Count}**", text);
    }

    [Fact]
    public void StatSheetCountsMatchGeneration_failsOnAPlantedDrift()
    {
        var text = ReadDesignDoc("spec-derived-stat-sheet.md");
        // The doc must NOT still carry the pre-T2 numbers this test would otherwise silently tolerate
        // if it only checked for presence of the new numbers without checking absence of the old ones.
        Assert.DoesNotContain("**84**", text);
        Assert.DoesNotContain("**99**", text);
    }

    [Fact]
    public void AtomFamilyCountMatchesCatalog()
    {
        // spec-unbuilt-reconcile.md F6/T6.3 (reconcile pass, 2026-08-25): atom-family-library.md's
        // stat.derived sizing tracks CombatChannelFamilies.Count LIVE, not a hand-copied literal --
        // the exact drift (12 authored families / ~420 rows, stale since T5.1-T5.4 added 16 more
        // families) that made F6 necessary in the first place.
        var text = ReadDoc("architecture", "effect-atom", "atom-family-library.md");
        var families = DerivedStatChannels.CombatChannelFamilies.Count;
        var slots = ElementRoster.Concrete.Count + 1; // 6 concrete elements + omni
        var channels = families * slots;
        var rows = channels * 5; // 5 tiers per family

        Assert.Equal(28, families); // sanity: today's actual value, not a guess
        Assert.Equal(196, channels);
        Assert.Equal(980, rows);
        Assert.Contains($"{families} generated families (~{rows} rows)", text, StringComparison.Ordinal);
        Assert.Contains($"{families} combat families × {slots} element slots = {channels} channels", text, StringComparison.Ordinal);
        Assert.Contains($"**{families} authored families** producing ~{rows} generated rows", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomFamilyCountMatchesCatalog_failsOnAPlantedDrift()
    {
        var text = ReadDoc("architecture", "effect-atom", "atom-family-library.md");
        // The doc must not still carry the pre-F6 numbers -- checking absence, not just presence of the
        // new ones, is what a Contains-only assertion would silently tolerate.
        Assert.DoesNotContain("12 generated families (~420 rows)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("12 combat families × 7 element slots = 84 channels", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(DerivedStatChannels.CombatPenetrationPrefix))]
    [InlineData(nameof(DerivedStatChannels.CombatAbsorptionPrefix))]
    [InlineData(nameof(DerivedStatChannels.CombatAmplificationPrefix))]
    [InlineData(nameof(DerivedStatChannels.CombatReductionPrefix))]
    [InlineData(nameof(DerivedStatChannels.CombatReflectRatePrefix))]
    [InlineData(nameof(DerivedStatChannels.CombatParryRatePrefix))]
    [InlineData(nameof(DerivedStatChannels.CombatBlockRatePrefix))]
    public void OmniAdditiveForNewFamilies(string familyConstName)
    {
        // Every new H.1 family composes omni + element as a pure sum -- FlatSum has no multiplicative
        // path in DerivedComposer at all, so "omni x element" cannot arise by construction. Proven
        // per-family (not just asserted of the mechanism in general) so a future compose-kind change
        // on ONE family would be caught here rather than only in a generic architecture test.
        var family = familyConstName switch
        {
            nameof(DerivedStatChannels.CombatPenetrationPrefix) => DerivedStatChannels.CombatPenetrationPrefix,
            nameof(DerivedStatChannels.CombatAbsorptionPrefix) => DerivedStatChannels.CombatAbsorptionPrefix,
            nameof(DerivedStatChannels.CombatAmplificationPrefix) => DerivedStatChannels.CombatAmplificationPrefix,
            nameof(DerivedStatChannels.CombatReductionPrefix) => DerivedStatChannels.CombatReductionPrefix,
            nameof(DerivedStatChannels.CombatReflectRatePrefix) => DerivedStatChannels.CombatReflectRatePrefix,
            nameof(DerivedStatChannels.CombatParryRatePrefix) => DerivedStatChannels.CombatParryRatePrefix,
            nameof(DerivedStatChannels.CombatBlockRatePrefix) => DerivedStatChannels.CombatBlockRatePrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(familyConstName))
        };

        var composer = new DerivedComposer();
        var omniChannel = $"{family}.omni";
        var fireChannel = $"{family}.fire";
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(omniChannel, DerivedModifierOp.Flat, 4.0),
            new DerivedModifier(fireChannel, DerivedModifierOp.Flat, 3.0)
        });

        // Each channel composes independently (additive by construction) -- there is no "total" reader
        // yet to sum them (that is mitigation-chain/evasion-chain/reflection's job, T5), so what this
        // phase can prove is that omni and the element slot are SEPARATE additive channels, neither
        // multiplying the other, which is the precondition the future reader's "totalX = omni + element"
        // formula depends on.
        Assert.Equal(4.0, snap.Get(omniChannel));
        Assert.Equal(3.0, snap.Get(fireChannel));
    }

    [Fact]
    public void AllSixteenNewFamiliesNowHaveReaders()
    {
        // Superseded 2026-08-25 (reconcile pass, F6): this test used to be NoReaderTouchesTheNewFamiliesYet,
        // asserting the OPPOSITE — that OverlayCombatCalculator.cs's raw text named none of these
        // families, because nothing read them yet. That premise is false as of T5.1–T5.4: mitigation-
        // chain, evasion-chain, and reflection all shipped. It still passed textually even after they
        // shipped, because every reader goes through CombatDerivedReader's named methods
        // (Penetration/Absorption/... snap.Get(DerivedStatChannels.X)), never a raw "combat.penetration"
        // string literal inside OverlayCombatCalculator.cs itself — a misleading green, not a true one.
        // This replacement asserts the CURRENT, positive fact instead: CombatDerivedReader carries a
        // named reader for every one of the 16 new families, so none of them is silently unread.
        var text = ReadCoreFile("Combat", "CombatDerivedReader.cs");
        string[] newFamilyReaderNames =
        {
            "Penetration", "Absorption", "Amplification", "Reduction",
            "ParryRate", "ParryBreak", "ParryStrength", "ParryShred",
            "BlockRate", "BlockBreak", "BlockStrength", "BlockShred",
            "ReflectRate", "ReflectResistRate", "ReflectDamage", "ReflectResistDamage"
        };
        foreach (var readerName in newFamilyReaderNames)
            Assert.Contains($"public static double {readerName}(", text, StringComparison.Ordinal);
    }

    static HashSet<string> ExtractFamiliesFromElementHubSection6(string text)
    {
        var section6Start = text.IndexOf("## 6. Derived channel catalog", StringComparison.Ordinal);
        var section6End = text.IndexOf("## 7. Omni rule", StringComparison.Ordinal);
        Assert.True(section6Start >= 0, "element-hub-ssot.md: §6 heading not found");
        Assert.True(section6End > section6Start, "element-hub-ssot.md: §7 heading not found after §6");
        var section6 = text[section6Start..section6End];

        // §6.2's table lists families as backtick-wrapped `combat.foo` tokens, `·`-separated within a
        // cell. Every such token in the section IS a family (no other backtick content in §6 starts
        // with "combat." and has no further dots-then-element-suffix appended).
        var families = new HashSet<string>(StringComparer.Ordinal);
        // FusionRpg.Core.Match is a namespace (Match tuning) that collides with
        // System.Text.RegularExpressions.Match's short name — fully qualified to disambiguate.
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(section6, "`(combat\\.[a-zA-Z.]+)`"))
            families.Add(m.Groups[1].Value);
        return families;
    }

    static string ReadDoc(params string[] relativeUnderDocs)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "docs" }.Concat(relativeUnderDocs).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string ReadDesignDoc(string fileName) => ReadDoc("design", fileName);

    static string ReadCoreFile(params string[] relativeUnderCore)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Core" }.Concat(relativeUnderCore).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

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
