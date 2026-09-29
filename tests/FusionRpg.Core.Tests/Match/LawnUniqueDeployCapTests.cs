using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Match;

/// <summary>
/// lawn `LW5.1` (<c>creature-lawn-deploy/spec-unique-deploy-cap.md</c>): the one admission rule for the
/// concurrency axis of a unique lawn deploy — owner ruling D6, "at most 5 unique creatures per side, 10
/// on the board".
///
/// <para><b>Contract, never a reading.</b> The policy is asserted on its own numbers (two counts in, a
/// reason out) and the shipped file is asserted on its schema invariant; nothing here asserts how many
/// uniques a real board holds, because that is a gameplay reading.</para>
/// </summary>
[Trait("VerificationId", "core.lawn-unique-deploy-cap")]
public class LawnUniqueDeployCapTests
{
    static readonly LawnDeployLimits Limits = new(MaxConcurrentUniquesPerEmpire: 5, MaxConcurrentUniquesOnBoard: 10);

    [Fact]
    public void Below_both_limits_the_deploy_is_admitted()
    {
        Assert.True(LawnUniqueDeployCap.TryAdmit(0, 0, Limits).Ok);
        Assert.True(LawnUniqueDeployCap.TryAdmit(4, 9, Limits).Ok);
    }

    [Fact]
    public void At_the_per_empire_limit_it_refuses_with_the_per_empire_reason()
    {
        var result = LawnUniqueDeployCap.TryAdmit(liveUniquesForEmpire: 5, liveUniquesOnBoard: 5, Limits);

        Assert.False(result.Ok);
        Assert.Equal(GateReasons.CapUniquePerEmpire, result.Reason);
        Assert.Equal("cap.unique_per_empire", result.Reason); // the wire string, pinned
    }

    [Fact]
    public void Under_the_per_empire_limit_but_at_the_board_limit_it_refuses_with_the_board_reason()
    {
        var result = LawnUniqueDeployCap.TryAdmit(liveUniquesForEmpire: 1, liveUniquesOnBoard: 10, Limits);

        Assert.False(result.Ok);
        Assert.Equal(GateReasons.CapUniqueBoard, result.Reason);
        Assert.Equal("cap.unique_board", result.Reason);
    }

    [Fact]
    public void The_per_empire_reason_wins_when_both_limits_are_reached()
    {
        // Stated precedence, not incidental: the caller needs "this empire already holds its five", and
        // the board number is the frame-budget backstop behind it.
        var result = LawnUniqueDeployCap.TryAdmit(liveUniquesForEmpire: 5, liveUniquesOnBoard: 10, Limits);

        Assert.False(result.Ok);
        Assert.Equal(GateReasons.CapUniquePerEmpire, result.Reason);
    }

    [Fact]
    public void The_shipped_file_states_both_limits_and_its_board_limit_does_not_contradict_the_per_empire_one()
    {
        var tuning = LawnDeployLimitsTuningLoader.Parse(File.ReadAllText(FindTuning("lawn-deploy.v1.json")));

        Assert.Equal(1, tuning.SchemaVersion);
        Assert.Equal(1, tuning.Version);
        // The invariant, asserted against the SHIPPED file: a board limit below the per-empire one would
        // make the per-empire limit unreachable, i.e. the file would contradict itself.
        Assert.True(tuning.Limits.MaxConcurrentUniquesOnBoard >= tuning.Limits.MaxConcurrentUniquesPerEmpire);
        Assert.True(tuning.Limits.MaxConcurrentUniquesPerEmpire > 0);
        Assert.True(tuning.Limits.MaxConcurrentUniquesOnBoard > 0);
    }

    [Fact]
    public void A_contradictory_or_non_positive_limit_is_refused_at_load_rather_than_clamped()
    {
        // An absolute bound that cannot hold is a load error, never a silent clamp (the no-ceilings
        // rule's own "throws, never clamps"), and a non-positive limit is not a limit.
        Assert.Throws<LawnDeployLimitsTuningRejection>(() => LawnDeployLimitsTuningLoader.Parse(Document(board: 4)));
        Assert.Throws<LawnDeployLimitsTuningRejection>(() => LawnDeployLimitsTuningLoader.Parse(Document(perEmpire: 0)));
        Assert.Throws<LawnDeployLimitsTuningRejection>(() => LawnDeployLimitsTuningLoader.Parse(Document(board: -1)));
        Assert.Throws<LawnDeployLimitsTuningRejection>(() => LawnDeployLimitsTuningLoader.Parse("{}"));
    }

    [Fact]
    public void The_hub_refuses_to_answer_before_the_host_configured_it()
    {
        LawnDeployLimitsTuningHub.Reset();

        var error = Assert.Throws<InvalidOperationException>(() => LawnDeployLimitsTuningHub.Tuning);
        Assert.Contains("lawn-deploy.v1.json", error.Message, StringComparison.Ordinal);

        LawnDeployLimitsTuningHub.Configure(
            LawnDeployLimitsTuningLoader.Parse(File.ReadAllText(FindTuning("lawn-deploy.v1.json"))));
        try
        {
            Assert.True(LawnDeployLimitsTuningHub.IsConfigured);
            Assert.Equal(5, LawnDeployLimitsTuningHub.Tuning.Limits.MaxConcurrentUniquesPerEmpire);
        }
        finally
        {
            LawnDeployLimitsTuningHub.Reset();
        }
    }

    static string Document(int perEmpire = 5, int board = 10) =>
        $$"""
        {
          "schemaVersion": 1,
          "version": 1,
          "limits": {
            "maxConcurrentUniquesPerEmpire": {{perEmpire}},
            "maxConcurrentUniquesOnBoard": {{board}}
          }
        }
        """;

    static string FindTuning(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"no data/tuning/{fileName} above the test output");
    }
}
