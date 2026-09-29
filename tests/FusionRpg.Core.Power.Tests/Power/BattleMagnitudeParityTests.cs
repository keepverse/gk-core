using System;
using System.IO;
using System.Text.RegularExpressions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// battle-magnitude (T2.1, spec-battle-magnitude.md §5). Proves <see cref="BattleRuleset"/>'s three
/// magnitude functions are byte-identical to their pre-migration literal formulas, and that the
/// per-channel derivation (§2.1's disproof of a single-ratio model, and F1's regression against a
/// shared-absolute-B model) holds. Golden-suite parity itself is proven by the existing
/// BattleGoldenTests.cs staying green — this file is the ladder-level math, not a duplicate of that.
/// </summary>
public class BattleMagnitudeParityTests
{
    // ---- §7.1: parity against the shipped literal formulas, at B=0 (retired 2026-08-24) -----------
    //
    // T4.2 (power-dial) turned the shipped dial from B=0 to B=400, so "byte-identical to the old
    // 80+30*level literal" stopped being a true statement about shipped behavior BY DESIGN — that
    // was T2.1's own proof that the migration itself was safe, not an invariant meant to survive the
    // dial. Replaced below with two more durable checks: parity against the LIVE ladder (holds at any
    // future B, not just the one shipped today) and the B=0 formula preserved as its own explicit,
    // clearly-labeled historical fact via a LOCAL zero-B tuning, not the ambient (now B=400) hub.

    [Fact]
    public void BaseHp_MatchesTheLiveLadder_AcrossFullRange()
    {
        var ladder = new PowerLadder(PowerTuningHub.Tuning);
        for (int level = 0; level <= 5000; level++)
            Assert.Equal(ladder.Value(level), BattleRuleset.BaseHp(level));
    }

    [Fact]
    public void BaseAtk_MatchesTheLiveChannelLadder_AcrossFullRange()
    {
        var channel = new ChannelLadder(PowerTuningHub.Tuning.Curve.BMilli, PowerTuning.FixedPinValue,
            PowerTuningHub.Tuning.ChannelsOrEmpty["atk"]);
        for (int level = 0; level <= 5000; level++)
            Assert.Equal(channel.Value(level), BattleRuleset.BaseAtk(level));
    }

    [Fact]
    public void BaseDefense_MatchesTheLiveChannelLadder_AcrossFullRange()
    {
        var channel = new ChannelLadder(PowerTuningHub.Tuning.Curve.BMilli, PowerTuning.FixedPinValue,
            PowerTuningHub.Tuning.ChannelsOrEmpty["defense"]);
        for (int level = 0; level <= 5000; level++)
            Assert.Equal(channel.Value(level), BattleRuleset.BaseDefense(level));
    }

    [Fact]
    public void BaseHp_AtBZero_StillMatchesTheOriginalPreMigrationLiteral_HistoricalFact()
    {
        // T2.1's original claim, preserved exactly, against a LOCAL bMilli=0 ladder rather than the
        // ambient hub (which is B=400 since T4.2) -- proves the ladder ITSELF still reproduces the
        // pre-migration formula at B=0, independent of whatever the currently-shipped dial is.
        var ladder = new PowerLadder(BuildTuning(bMilli: 0));
        for (int level = 0; level <= 5000; level++)
            Assert.Equal(80 + 30L * level, ladder.Value(level));
    }

    [Fact]
    public void Pins_MatchAtTheta20()
    {
        Assert.Equal(680, BattleRuleset.BaseHp(20));
        Assert.Equal(92, BattleRuleset.BaseAtk(20));
        Assert.Equal(22, BattleRuleset.BaseDefense(20));
    }

    // ---- §2.1's disproof, asserted so it can never be silently re-litigated -------------------------

    [Fact]
    public void SingleRatioModel_CannotReproduceAllThreePins_TheDisproof()
    {
        // BaseAtk(Theta) = Value(Theta) * 92/680 is wrong: at Theta=0 the true ratio is 12/80=0.150,
        // at Theta=20 it is 92/680=0.135. A single ratio hits one point, not both.
        long valueAt0 = BattleRuleset.BaseHp(0);
        long ratioProjectedAt0 = valueAt0 * 92 / 680;
        Assert.NotEqual(12L, ratioProjectedAt0);
    }

    // ---- F1 regression: every channel's derived A must stay positive ------------------------------

    // 9998 deliberately excluded (mirrors PowerLadderTests' own exclusion for the same reason): atk's
    // pin (92) is even smaller than hp's (680), so its derived A crosses zero at bMilli~3113 — lower
    // than hp's own ~3158 threshold (T1.2). 9998 is 25x the decided dial; no real tuning reaches it.
    // This theory proves A > 0 across the whole realistic/documented range (0 through the "steep"
    // example, 1000), which is what F1 actually regresses against — not an unbounded B claim.
    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(1000)]
    public void F1Regression_EveryChannelsDerivedA_StaysPositive(long bMilli)
    {
        // A shared ABSOLUTE B (the broken earlier draft, audit F1) gives defense A = -2.8 at B=0.4.
        // The proportional B_ch = B*pinCh/pinHp design must never regress to that shape.
        var atk = new ChannelLadder(bMilli, PowerTuning.FixedPinValue, new PowerChannelTuning(12_000, 92));
        var defense = new ChannelLadder(bMilli, PowerTuning.FixedPinValue, new PowerChannelTuning(2_000, 22));

        Assert.True(atk.AMilliNumerator > 0, $"atk A must be > 0 at bMilli={bMilli}, numerator was {atk.AMilliNumerator}");
        Assert.True(defense.AMilliNumerator > 0, $"defense A must be > 0 at bMilli={bMilli}, numerator was {defense.AMilliNumerator}");
    }

    [Fact]
    public void F1Regression_AtDecidedDialB400_MatchesSpecsWorkedExample()
    {
        // spec-battle-magnitude.md §2.1's table: atk A=3.4859, defense... values given to 4 decimals.
        // Cross-checked independently by hand (see power-todo.md T2.1) against P(100): atk=628, defense=154.
        var atk = new ChannelLadder(400, PowerTuning.FixedPinValue, new PowerChannelTuning(12_000, 92));
        var defense = new ChannelLadder(400, PowerTuning.FixedPinValue, new PowerChannelTuning(2_000, 22));

        Assert.Equal(628, atk.Value(100));
        Assert.Equal(154, defense.Value(100));
    }

    [Fact]
    public void ProportionalGrowth_AllThreeChannelsMoveTogether_WhenBIsRaised()
    {
        // "B > 0 moves all three" (§5) — none drifts alone. Compare growth at B=0 vs B=400.
        var hpB0 = new PowerLadder(BuildTuning(bMilli: 0));
        var hpB400 = new PowerLadder(BuildTuning(bMilli: 400));
        var atkB0 = new ChannelLadder(0, PowerTuning.FixedPinValue, new PowerChannelTuning(12_000, 92));
        var atkB400 = new ChannelLadder(400, PowerTuning.FixedPinValue, new PowerChannelTuning(12_000, 92));
        var defB0 = new ChannelLadder(0, PowerTuning.FixedPinValue, new PowerChannelTuning(2_000, 22));
        var defB400 = new ChannelLadder(400, PowerTuning.FixedPinValue, new PowerChannelTuning(2_000, 22));

        Assert.True(hpB400.Value(200) > hpB0.Value(200));
        Assert.True(atkB400.Value(200) > atkB0.Value(200));
        Assert.True(defB400.Value(200) > defB0.Value(200));
    }

    static PowerTuning BuildTuning(long bMilli) =>
        PowerTuning.Build(1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
            1000, 25000, 250, 1000, 5000, 5000, 25000);

    // ---- shield BaseHp untouched — source scan, not just "the shield suite happens to pass" --------

    [Fact]
    public void NoShieldFileReferencesBattleRuleset()
    {
        var shieldDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Combat", "Shield");
        Assert.True(Directory.Exists(shieldDir), shieldDir);

        var offenders = new System.Collections.Generic.List<string>();
        foreach (var file in Directory.GetFiles(shieldDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = Regex.Replace(lines[i], "//.*$", "");
                if (code.Contains("BattleRuleset"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
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
