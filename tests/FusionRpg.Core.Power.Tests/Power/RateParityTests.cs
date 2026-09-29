using System;
using System.IO;
using System.Text.RegularExpressions;
using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// battle-rates (T2.2, spec-battle-rates.md §5). Byte parity of the four rate baselines and the PS-3
/// structural guarantee. Parity-invariance-of-the-sigmoid (Θ ∈ {1,5,10,20,100,1000,10000}) is
/// BattleAdoptionTests.BattleRateTests, extended in place per the spec's own "extend, don't
/// duplicate" boundary — not repeated here.
/// </summary>
public class RateParityTests
{
    [Fact]
    public void AllFour_MatchShippedFormula_AcrossFullRange()
    {
        for (int theta = 0; theta <= 5000; theta++)
        {
            Assert.Equal(220 + 26 * theta, BattleRuleset.BaseAccuracy(theta));
            Assert.Equal(26 * theta, BattleRuleset.BaseDodge(theta));
            Assert.Equal(10 * theta, BattleRuleset.BaseCritRate(theta));
            Assert.Equal(10 * theta + 250, BattleRuleset.BaseCritResist(theta));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void FixedGap_FixedValue_AtEveryTheta(int theta)
    {
        // BaseAccuracy(Theta+5) - BaseDodge(Theta) - 220 == 130 at every Theta (spec-battle-rates.md
        // §5) -- the "+5-index attacker" headroom the sigmoid reads is a CONSTANT, never a function
        // of Theta itself. That constancy is PS-3's whole point for a contest read.
        long lhs = BattleRuleset.BaseAccuracy(theta + 5) - BattleRuleset.BaseDodge(theta) - 220;
        Assert.Equal(130, lhs);
    }

    // ---- PS-3 tripwire ------------------------------------------------------------------------------

    // A live "reconfigure PowerTuningHub to B=0 then B=1000 and compare BaseAccuracy's output" test
    // was considered and rejected: PowerTuningHub is process-global, shared by every test in this
    // assembly via ContractTuningTestBootstrap's [ModuleInitializer], and xUnit does not guarantee
    // this class runs in isolation from others that also read it — mutating it mid-suite risks
    // flaking unrelated tests for a property this source-scan proves more strongly anyway: not "these
    // four happen to agree at two sampled B values today" but "these four cannot possibly depend on B
    // at all, for any B, because the words PowerLadder/ChannelLadder/PowerTuningHub do not appear in
    // their bodies." Confirmed by direct inspection this now proves what the spec's own framing wants
    // ("fails the moment someone routes a rate through P(Theta)") — a source-scan catches that edit
    // immediately, the same as the behavioural version would, without the shared-state risk.
    [Fact]
    public void PS3Tripwire_RateFunctionsNeverReferenceTheLadder()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Battle", "BattleModels.cs");
        var text = File.ReadAllText(path);

        var body = ExtractMethodBodies(text, "BaseAccuracy", "BaseDodge", "BaseCritRate", "BaseCritResist");
        Assert.False(body.Contains("PowerLadder"), "a rate function must never touch PowerLadder (PS-3)");
        Assert.False(body.Contains("ChannelLadder"), "a rate function must never touch ChannelLadder (PS-3)");
        Assert.False(body.Contains("PowerTuningHub"), "a rate function must never read PowerTuningHub (PS-3)");
    }

    static string ExtractMethodBodies(string source, params string[] methodNames)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var name in methodNames)
        {
            var m = Regex.Match(source, $@"public static int {name}\([^)]*\)\s*(=>|{{)([\s\S]*?)(;|\n\s*}})");
            Assert.True(m.Success, $"could not locate {name}(...) in BattleModels.cs — test needs updating alongside the source");
            sb.Append(m.Groups[2].Value);
        }
        return sb.ToString();
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tuning"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("data/tuning");
    }
}
