using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// action-enrich `action-base` (spec-action-base.md acceptance 3): no production damage path calls
/// <c>LiveAtk(</c>. The hit's base is the action's, resolved through <c>ActionBaseDerivation</c>, so the
/// actor's live <c>atk</c> read is no longer an input to damage.
///
/// <para><b>Exactly one allowed occurrence</b>, the <c>LiveAtk</c> definition itself
/// (<c>BattleEngine.cs</c>) — allowlisted rather than deleted because deleting <c>LiveAtk</c>,
/// <c>Setup.Atk</c> and the <c>atk</c> channel is solid-enforcement <c>SE1.7</c>'s job, and this guard
/// is the line SE1.7 retires. A second occurrence anywhere under <c>gk-core/src/FusionRpg.Core/**</c> is the
/// defect (a re-introduced damage read).</para>
/// </summary>
public class ActionBaseNoAtkReadGuardTests
{
    const string EngineRel = "src/FusionRpg.Core/Battle/BattleEngine.cs";
    const string DefinitionLine = "public long LiveAtk(BattleStatModifierLedger ledger)";

    [Fact]
    public void No_core_file_calls_LiveAtk_except_its_own_definition()
    {
        var root = RepoRoot();
        var core = Path.Combine(root, "src", "FusionRpg.Core");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("LiveAtk(", StringComparison.Ordinal)) continue;

                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel == EngineRel && lines[i].Contains(DefinitionLine, StringComparison.Ordinal)) continue;

                offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "no file under src/FusionRpg.Core/** may call LiveAtk( on a damage path; the only allowed " +
            "occurrence is its own definition (" + EngineRel + ", retired by SE1.7). Offenders:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>The positive half, so the guard above cannot pass vacuously by the call being deleted:
    /// the hit site still resolves the swung action's base and its effective rung, and still names the
    /// actor and action when the envelope is not held. And the definition itself still exists, so the
    /// one-line allowlist is not hiding a removal.</summary>
    [Fact]
    public void The_hit_reads_the_action_base_and_the_LiveAtk_definition_still_exists()
    {
        var root = RepoRoot();
        var basic = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Core", "Battle", "BasicAttack.cs"));
        Assert.Contains("state.HeldActionOf(attacker.Setup.Key, envelope.ActionId)", basic, StringComparison.Ordinal);
        Assert.Contains("state.EffectiveRungOf(attacker.Setup.Key, envelope.ActionId)", basic, StringComparison.Ordinal);
        Assert.Contains("ActionBaseDerivation.BasePowerMilli(", basic, StringComparison.Ordinal);
        Assert.Contains("ActionBaseMath.BasePerHit(", basic, StringComparison.Ordinal);
        Assert.Contains("which it does not hold", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveAtk(", basic, StringComparison.Ordinal);

        var engine = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Core", "Battle", "BattleEngine.cs"));
        Assert.Contains(DefinitionLine, engine, StringComparison.Ordinal);
    }

    static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
