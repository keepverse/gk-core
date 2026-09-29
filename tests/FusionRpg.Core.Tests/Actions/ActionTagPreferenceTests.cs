using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// A26 T67 (`action-choice-rung-tiebreak`): the tag-rank tiebreak is `Rung` DESCENDING before
/// `action_id`. Before it, two same-tag held actions resolved by STRING order, so a rung-1 strike
/// always beat a rung-9 combo whose id happened to sort later — "a property of naming, not of build",
/// and the second action could be granted, held and priced correctly and still never once fire.
///
/// <para>No pre-existing test asserted the old alphabetical tiebreak (searched, zero hits), so nothing
/// here replaces a legacy fixed expectation.</para>
/// </summary>
public class ActionTagPreferenceTests
{
    static CompiledAction Dummy(string id, int rung, params ActionTag[] tags) => new(
        ActionId: id, Kind: ActionKind.Skill, Rung: rung, Tags: tags,
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = id },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always, Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    /// <summary>Both id orders, because the old rule made the ANSWER depend on which id sorted first:
    /// the rung-9 action must win whichever way its name falls.</summary>
    [Theory]
    [InlineData("aaa.low", "zzz.high")]
    [InlineData("zzz.high", "aaa.low")]
    public void ASameTagPairOrdersByRungDescendingWhicheverIdSortsFirst(string idLow, string idHigh)
    {
        var low = Dummy(idLow, rung: 1, ActionTag.Offensive);
        var high = Dummy(idHigh, rung: 9, ActionTag.Offensive);

        Assert.True(ActionTagPreference.Compare(high, low) < 0, "the rung-9 action must sort first");
        Assert.True(ActionTagPreference.Compare(low, high) > 0, "and the rung-1 action must sort last");
    }

    /// <summary>The doc comment's old claim — "an untagged action ... tied only by `action_id`" — is
    /// corrected by this: two untagged actions order by rung too.</summary>
    [Fact]
    public void UntaggedActionsNowOrderByRungNotOnlyById()
    {
        var low = Dummy("aaa.untagged", rung: 1);
        var high = Dummy("zzz.untagged", rung: 5);

        Assert.Equal(ActionTagPreference.RankOf(low), ActionTagPreference.RankOf(high)); // both untagged
        Assert.True(ActionTagPreference.Compare(high, low) < 0);
    }

    [Fact]
    public void TheSameRungStillFallsBackToActionId()
    {
        var a = Dummy("aaa.same", rung: 3, ActionTag.Offensive);
        var b = Dummy("zzz.same", rung: 3, ActionTag.Offensive);

        Assert.True(ActionTagPreference.Compare(a, b) < 0);
        Assert.True(ActionTagPreference.Compare(b, a) > 0);
    }

    /// <summary>The rung step sits BETWEEN the tag rank and the id — it never lets a high-rung action
    /// outrank a better tag rank.</summary>
    [Fact]
    public void TheTagRankStillOutranksTheRung()
    {
        var offensiveLowRung = Dummy("zzz.off", rung: 1, ActionTag.Offensive);
        var defensiveHighRung = Dummy("aaa.def", rung: 9, ActionTag.Defensive);

        Assert.True(ActionTagPreference.Compare(offensiveLowRung, defensiveHighRung) < 0);
    }

    /// <summary>The live consequence, through the real loadout sort rather than the comparator alone:
    /// a battle actor holding both same-tag actions leads with the rung-9 one even though its id sorts
    /// LAST — the exact case that used to make the stronger action unfireable.</summary>
    [Fact]
    public void TheBattleLoadoutLeadsWithTheHigherRungSameTagAction()
    {
        var catalog = ActionCatalog.Build(new[]
        {
            Dummy("atk.basic_strike", rung: 1, ActionTag.Offensive),
            Dummy("atk.zzz_combo", rung: 9, ActionTag.Offensive),
        });
        var setup = new BattleSetup
        {
            WaveId = "t67",
            Squad = new[]
            {
                new BattleActorSetup
                {
                    Key = "squad:0", Side = "squad", SpeciesId = "t67", TypeId = 40_001, Level = 5,
                    MaxHp = 1000, Atk = 10,
                    EquippedActionIds = new[] { "atk.basic_strike", "atk.zzz_combo" },
                },
            },
            Wave = new[]
            {
                new BattleActorSetup { Key = "wave:0", Side = "wave", SpeciesId = "t67w", TypeId = 40_002, Level = 5, MaxHp = 1000, Atk = 10 },
            },
        };

        var held = BattleEngine.HeldActionIdsForTest(setup, seed: 1, actorKey: "squad:0", actionCatalog: catalog);

        Assert.Equal("atk.zzz_combo", held[0]);
    }

    /// <summary>"`SiegeAiIntentSource` shares the same `Compare`" — proven the way it is actually true:
    /// there is exactly ONE sort (the loadout sort above), and the siege AI consumes that already-sorted
    /// list through `HeldActionsOf` instead of re-sorting it. Source scan; a second sort would be the
    /// second ordering rule this task exists to prevent.</summary>
    [Fact]
    public void TheSiegeAiReadsTheOneSortedListRatherThanReSorting()
    {
        var siege = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "FusionRpg.Core", "Battle", "Siege", "SiegeAiIntentSource.cs"));
        Assert.Contains("HeldActionsOf", siege);
        Assert.DoesNotContain(".Sort(", siege);
        Assert.DoesNotContain("ActionTagPreference", siege);
    }

    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }
}
