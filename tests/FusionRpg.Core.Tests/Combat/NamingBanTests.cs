using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// spec-unbuilt-reconcile.md F2 (reconcile pass, T6.2, 2026-08-25) — "No identifier here is named
/// `guard`, and none in A8 is named `block` or `parry`" (spec-evasion-chain.md §0). A8/`defence-actions`
/// (guard) is spec-only, not built, so there is no A8 code to scan yet — this guards the code that DOES
/// exist today (the evasion-chain module, and the action/timeline module A8 will extend) so a future
/// PR cannot reintroduce the vocabulary collision that made F2 necessary in the first place. Standing
/// guard, not a one-time check: re-run every time either module grows.
/// </summary>
public class NamingBanTests
{
    static readonly string[] EvasionChainFiles =
    {
        "Combat/OverlayCombatCalculator.cs",
        "Combat/CombatDerivedReader.cs",
        "Combat/CombatPolicy.cs",
        "Combat/CombatTuning.cs",
        "Stats/Derived/DerivedStatChannels.cs"
    };

    static readonly string[] ActionTimelineFiles =
    {
        "Battle/Timeline/ActionEnvelope.cs",
        "Battle/Timeline/ActionRunner.cs",
        "Battle/Timeline/ActionSlots.cs",
        "Battle/Timeline/ActorTurnMachine.cs",
        "Battle/Timeline/CooldownLedger.cs",
        "Battle/Timeline/CooldownMath.cs",
        "Battle/Timeline/DerivedTurnChannels.cs"
    };

    [Fact]
    public void NoGuardInEvasionModule()
    {
        // "guard" belongs to A8 (the action); block/parry's own module must never coin a same-named
        // C# identifier of its own for what is really A8's mechanic. Scoped to CODE, not comments —
        // ordinary English ("guard clause", "guarded by a lock") uses the word too, unrelated to the
        // A8/evasion-chain naming collision this test exists to catch.
        foreach (var relative in EvasionChainFiles)
        {
            var text = StripComments(ReadCoreFile(relative));
            Assert.DoesNotContain("guard", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoBlockOrParryInActionModule()
    {
        // The mirror direction: A8/the action-timeline module must never coin its own `block`/`parry`
        // C# identifier for what is really the evasion-chain stats' mechanic. Same comment-stripped scope.
        foreach (var relative in ActionTimelineFiles)
        {
            var text = StripComments(ReadCoreFile(relative));
            Assert.DoesNotContain("block", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("parry", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Strips // line comments (covers /// doc comments too) and /* */ block comments, so the
    /// naming-ban checks see only code identifiers, never prose that happens to share an English word.</summary>
    static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//.*$", "", RegexOptions.Multiline);
        return text;
    }

    [Fact]
    public void NoGuardInEvasionModule_failsOnAPlantedViolation()
    {
        // A guard never proven to fail is not evidence -- same discipline as guard-stat-pairs.ps1's
        // planted violations and every other standing-guard test in this program. Runs the REAL
        // StripComments function, not just a raw Contains, so both halves of the mechanism are proven:
        // a genuine code identifier still trips the ban, and ordinary prose about "guard clauses" does
        // not -- the exact false positive this test's own build hit and fixed.
        const string plantedCodeViolation = """
            /// no guard clause needed, byte-identical by arithmetic
            public double GuardChanceOmni { get; set; }
            """;
        var stripped = StripComments(plantedCodeViolation);
        Assert.DoesNotContain("guard clause", stripped, StringComparison.OrdinalIgnoreCase); // comment gone
        Assert.Contains("guard", stripped, StringComparison.OrdinalIgnoreCase); // the field name remains

        const string onlyACommentMentionsIt = "// a static slot guarded by a lock, kept for reuse";
        Assert.DoesNotContain("guard", StripComments(onlyACommentMentionsIt), StringComparison.OrdinalIgnoreCase);
    }

    static string ReadCoreFile(string relativeUnderCore)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", relativeUnderCore);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }
}
