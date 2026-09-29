using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// party-dungeon D2.16 (spec-delve-battle-profile.md §4, party-dungeon-todo.md's own 2026-09-08
/// correction) — the compile-time-caught regression guard the freeze mechanism's own default
/// explicitly names as a condition of adopting it: "cancel-and-discard via an uncaught exception
/// unwinding a live simulation mid-loop... guarded by one concrete commitment the live wiring must
/// ship alongside it, not instead of a decision: a named, architecture-test-enforced ban... on any
/// `catch` ever being added to `BattleEngine.Resolve`'s own call stack, so the 'silently reintroduces
/// finish-on-autopilot' risk is a compile-time-caught regression, not a hoped-for discipline."
///
/// <para><b>Why THESE files, not "the whole Battle folder."</b> `git grep`-confirmed real (non-comment)
/// `catch` blocks already exist under <c>gk-core/src/FusionRpg.Core/Battle/</c> today —
/// <c>BattleTuning.cs</c>, <c>WaveCatalog.cs</c>, <c>BattleResourceTuning.cs</c>,
/// <c>Board/BattleBoardTuning.cs</c>, <c>Board/SiegeTuning.cs</c>, <c>Ai/ZombossAdaptiveTuning.cs</c>,
/// <c>Timeline/ReactionLaneTuning.cs</c> — every one a config-load-time <c>catch (JsonException) =>
/// throw SomeRejection(...)</c>, run once at startup/reload, never from inside
/// <see cref="FusionRpg.Core.Battle.BattleEngine.Resolve"/>'s own synchronous simulation loop. Banning
/// the whole directory would either force those legitimate, unrelated catch-and-rethrow loaders onto
/// an ever-growing exemption list (silent scope creep, the exact failure
/// <c>ModeProfileArchitectureTests.ProfileDefinitionFiles</c>'s own doc comment warns against) or ban
/// them outright, which is not this task's call to make. Instead this guard names, explicitly, every
/// file actually reachable from <c>Resolve</c>'s own call chain down to
/// <see cref="FusionRpg.Core.Battle.Timeline.InteractiveIntentSource.TryDeclare"/>'s live <c>_ask</c>
/// call — traced by hand this session (`grep`-confirmed <c>TryDeclare</c> callers): <c>BasicAttack.cs</c>
/// and <c>TimelineDispatch.cs</c> (both in <c>Actions/</c>, not <c>Battle/</c>) call
/// <c>IIntentSource.TryDeclare</c>, which is what a live freeze's <c>OperationCanceledException</c>
/// must cross uncaught. <c>DelveBattle.cs</c> is this program's own added link in that same chain.
/// combat-ai `intent-router` (module 4, CAI1.10) deleted <c>RaidIntentSource.cs</c> (folded into the
/// shared <c>Actions/IntentRouter.cs</c>, now reachable from BOTH <c>BasicAttack.cs</c> and
/// <c>TimelineDispatch.cs</c> for every mode, not only delve) — the list below tracks that move.
/// Confirmed (this session, by direct read) that NONE of the named files hold a real <c>catch</c>
/// today — the whole list is "still zero," and this test is what keeps it that way.</para>
///
/// <para>Same acknowledged limits as <c>ModeProfileArchitectureTests</c>'s own doc comment: a
/// line-based heuristic, not a proof — skips whole-line comments only, not fully string/comment
/// aware, an acceptable gap for a token this code-shaped (<c>catch</c> followed by <c>(</c> or
/// <c>{</c> cannot appear in ordinary prose or a string literal by accident).</para>
/// </summary>
public class NoCatchInLiveBattleCallStackTests
{
    /// <summary>
    /// Every file this session traced as reachable from <c>BattleEngine.Resolve</c> down to the live
    /// `_ask` call site, relative to <c>gk-core/src/FusionRpg.Core/</c>. Adding a new file to this chain (a
    /// future refactor that moves turn-declaration code) means adding a row here too — the same
    /// "the list is the closed inventory" discipline <c>ModeProfileArchitectureTests.KnownProfileIds</c>
    /// already uses.
    /// </summary>
    static readonly string[] CallStackFiles =
    {
        "Battle/BattleEngine.cs",
        "Battle/BattleRunState.cs",
        "Battle/BattleHubCompose.cs",
        "Battle/BattleModels.cs",
        "Stats/Derived/Subsystems/BattleBaselineSubsystem.cs",
        "Stats/Derived/Subsystems/BattleAffinitySubsystem.cs",
        "Stats/Derived/Subsystems/BattleTraitSubsystem.cs",
        "Stats/Derived/Subsystems/BattleTempoSubsystem.cs",
        "Battle/Timeline/ActionRunner.cs",
        "Battle/Timeline/ActorTurnMachine.cs",
        "Battle/Timeline/TurnEconomy.cs",
        "Battle/Timeline/EventQueue.cs",
        "Battle/Timeline/DeltaTickAdvance.cs",
        "Battle/Timeline/TurnReadiness.cs",
        "Battle/Timeline/ReadinessDriver.cs",
        "Battle/Timeline/TimelineDrive.cs",
        "Battle/Timeline/ReactionLane.cs",
        "Battle/Timeline/ReactionCounter.cs",
        "Battle/Timeline/RendezvousLane.cs",
        "Battle/Timeline/TurnOrderForecast.cs",
        "Battle/Timeline/CooldownLedger.cs",
        "Battle/Timeline/CooldownMath.cs",
        "Battle/Timeline/DerivedTurnChannels.cs",
        "Battle/Timeline/PvzObserverProjection.cs",
        "Battle/Timeline/TriggerPhase.cs",
        "Battle/Timeline/IntentSource.cs",
        "Battle/Timeline/InteractiveIntentSource.cs",
        "Battle/Timeline/DecisionTrace.cs",
        "Battle/Timeline/ActionSlots.cs",
        "Battle/Timeline/TurnState.cs",
        "Battle/Timeline/SimulationClock.cs",
        "Battle/BasicAttack.cs",
        "Battle/TimelineDispatch.cs",
        "Actions/IntentRouter.cs",
        "Delve/Battle/DelveBattle.cs",
    };

    static readonly Regex CatchClause = new(@"\bcatch\s*[({]", RegexOptions.Compiled);

    static string CoreSrcDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", "..", ".."));
        return Path.Combine(repo, "src", "FusionRpg.Core");
    }

    static List<string> Scan(IEnumerable<string> filePaths)
    {
        var offences = new List<string>();
        foreach (var file in filePaths)
        {
            if (!File.Exists(file))
            {
                offences.Add($"{file} -> MISSING (the call-stack inventory is stale — file moved or renamed)");
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue; // whole-line comments only

                if (CatchClause.IsMatch(lines[i]))
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} -> {lines[i].Trim()}");
            }
        }

        return offences;
    }

    [Fact]
    public void No_catch_exists_anywhere_in_BattleEngine_Resolves_live_call_stack()
    {
        var coreDir = CoreSrcDir();
        Assert.True(Directory.Exists(coreDir), $"Core src dir not found: {coreDir}");

        var paths = CallStackFiles.Select(rel => Path.Combine(coreDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        var offences = Scan(paths);

        Assert.True(offences.Count == 0,
            "A catch block was added to BattleEngine.Resolve's own live call stack -- this would " +
            "silently swallow the OperationCanceledException a delve freeze throws to unwind an " +
            "in-flight Resolve call, reintroducing finish-on-autopilot for a steered fight " +
            "(spec-delve-battle-profile.md §4):\n" + string.Join("\n", offences));
    }

    [Fact]
    public void The_guard_actually_detects_a_planted_catch()
    {
        // A guard that cannot fail is decoration -- proven against a temp file, the same discipline
        // ModeProfileArchitectureTests.The_guard_actually_detects_a_planted_violation already applies.
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-nocatch-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllLines(tmp, new[]
        {
            "namespace X;",
            "sealed class Offender",
            "{",
            "    void M() { try { DoWork(); } catch (Exception ex) { Swallow(ex); } }",
            "}"
        });

        try
        {
            var offences = Scan(new[] { tmp });
            Assert.Contains(offences, o => o.Contains("catch (Exception ex)", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void A_whole_line_comment_mentioning_catch_is_not_a_violation()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-nocatch-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllLines(tmp, new[]
        {
            "namespace X;",
            "// this method must never catch anything, on purpose",
            "sealed class Harmless { void M() { DoWork(); } }"
        });

        try
        {
            Assert.Empty(Scan(new[] { tmp }));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Every_named_call_stack_file_actually_exists()
    {
        // Guards the guard's own inventory: a silently-stale path (the file was renamed/moved) would
        // make the main test vacuously pass over nothing, which is worse than not having the test.
        var coreDir = CoreSrcDir();
        foreach (var rel in CallStackFiles)
        {
            var path = Path.Combine(coreDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"call-stack inventory names a file that does not exist: {rel}");
        }
    }
}
