using FusionRpg.Core.Battle;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

/// <summary>
/// `commander-identity` SE4.4 — the owner's ruling made executable: *"a commander literally a unique
/// creature"*, so a third commander is a directory row, never a code change. A third row runs the two
/// paths a lawn run actually uses — the session cache that answers a commander id at <c>board.start</c>
/// and kill attribution — with zero production edits anywhere.
/// </summary>
[Collection(CommanderDirectoryCollection.Name)]
public class ThirdCommanderOpenClosedTests
{
    static readonly CommanderRow[] WithThirdCommander =
    {
        new("commander:dave", EmpireId.Dave, "Commander", true, "player:{id}"),
        new("commander:zomboss", EmpireId.Zomboss, "Dr. Zomboss", false, "zomboss:{id}"),
        new("commander:test-creature", EmpireId.Dave, "Test Creature", false, "player:{id}"),
    };

    [Fact]
    public void A_third_commander_row_runs_a_lawn_session_and_kill_attribution_with_no_production_edit()
    {
        CommanderDirectoryHub.Configure(new DataCommanderDirectory(WithThirdCommander));
        try
        {
            // The injector's real path at board.start: the cache applies a fetched commander id and
            // builds the match snapshot. It resolves the third commander because the directory does.
            MatchCommanderSessionCache.ResetForTests();
            MatchCommanderSessionCache.Apply(
                "commander:test-creature", "Test Creature", "Might", "Might", AptitudeAllocation.Empty);
            var snapshot = MatchCommanderSessionCache.BuildFromSessionCache();

            Assert.False(MatchCommanderSessionCache.LastBuildUsedFallback);
            Assert.Equal("commander:test-creature", snapshot.LeadingCommanderId);

            // Kill attribution is the other half: the empire it credits comes from the same directory,
            // so a new commander changes nothing here — only a new EMPIRE would.
            var directory = CommanderDirectoryHub.Current;
            directory.TryResolve("commander:test-creature", out var third);
            Assert.Equal(EmpireId.Dave, directory.EmpireOf(third));
            Assert.Equal(directory.EmpireOf(third), KillAttribution.EmpireOf(StatSide.Plant));
            Assert.Equal(directory.AllocationScopeKey(third, 7), "player:7");
        }
        finally
        {
            CommanderDirectoryHub.Configure(ShippedCommanders.Directory);
            MatchCommanderSessionCache.ResetForTests();
        }
    }
}
