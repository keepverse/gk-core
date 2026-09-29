using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` (2026-09-07, session 5, owner-authorized): the FIRST live wiring of
/// `SiegeAiIntentSource` into a real `BattleEngine.Resolve` round, through the REAL atomic
/// action-phase loop `DeclareBasicAttack` uses (`Siege`/`Delve` both dispatch through it) — proving
/// `BattleRunState.DefaultAiIntentSource` genuinely reaches and changes a real targeting decision, not
/// merely that the two classes compile together. Before this task, `SiegeAiIntentSource` had zero
/// production callers anywhere in the game (confirmed repeat-edly this session); every real siege
/// used `StubIntentSource`'s purely geometric nearest-enemy fallback regardless of how "smart" the
/// scoring formulas underneath it were.
/// </summary>
public class SiegeAiLiveWiringTests
{
    static BattleActorSetup Attacker(string key) => new() { Key = key, Side = "squad", MaxHp = 1000 };

    /// <summary>A geometrically NEAR but statistically terrible target: real, shipped
    /// `CombatDodgeOmni` channel mod (the same mechanism `Dodge_mod_swings_fixed_battles` already
    /// proves swings real combat), pushed far enough that a hit is vanishingly unlikely, and full HP
    /// so neither "kill" nor "low HP" ever recommends it either.</summary>
    static BattleActorSetup NearButUnhittable(string key) => new()
    {
        Key = key, Side = "wave", MaxHp = 1000,
        ChannelMods = new[] { new BattleChannelMod(DerivedStatChannels.CombatDodgeOmni, 5000) },
    };

    /// <summary>A geometrically FAR but statistically perfect target: 1 HP (guaranteed kill,
    /// guaranteed lowest-missing-HP-fraction) and no defensive stat at all (ordinary hit chance).
    /// Every term `AiScoring.Score` sums — hit chance, kill, low HP, round -- agrees this is the
    /// better target; nothing conflicts, so the expected outcome is unambiguous.</summary>
    static BattleActorSetup FarButLethal(string key) => new() { Key = key, Side = "wave", MaxHp = 1, };

    static BoardState BoardWithNearAndFar()
    {
        var board = new BoardState(new GridSpec(20, 20));
        board.Place("squad:0", new GridPos(0, 0));
        board.Place("wave:near", new GridPos(1, 0));   // Chebyshev 1 -- the nearest possible cell
        board.Place("wave:far", new GridPos(19, 19));  // as far as this board allows
        return board;
    }

    static BattleSetup Setup() => new()
    {
        Squad = new[] { Attacker("squad:0") },
        Wave = new[] { NearButUnhittable("wave:near"), FarButLethal("wave:far") },
    };

    [Fact]
    public void Without_aiTuning_the_stub_fallback_attacks_the_geometrically_nearest_enemy()
    {
        // Baseline: proves the scenario itself is discriminating (StubIntentSource really does pick
        // by distance alone) before trusting what changes once aiTuning is supplied below.
        var report = BattleEngine.Resolve(Setup(), seed: 1, board: BoardWithNearAndFar());

        var far = report.Actors.Single(a => a.Key == "wave:far");
        Assert.Equal(1, far.HpRemaining);   // never targeted -- still at its starting 1 HP, not dead
    }

    [Fact]
    public void With_aiTuning_SiegeAiIntentSource_attacks_the_scored_better_enemy_instead()
    {
        // The real fix: identical setup, only `aiTuning` added -- BattleRunState.DefaultAiIntentSource
        // now exists and DeclareBasicAttack's own fallback tries it before StubIntentSource.
        var report = BattleEngine.Resolve(Setup(), seed: 1, board: BoardWithNearAndFar(),
            aiTuning: SiegeAiTuningForTest());

        var far = report.Actors.Single(a => a.Key == "wave:far");
        Assert.True(far.HpRemaining <= 0, $"expected the scored-better (guaranteed-kill) target to die; far.HpRemaining={far.HpRemaining}");
    }

    // -- combat-ai `intent-router` CAI1.11: `bloodthirsty` reaches the SCORED POLICY, not only the
    //    stub fallback (audit M1) -------------------------------------------------------------------

    /// <summary>A 33-enemy board, spread wider than `ThreatRadiusCells` so the risk term cancels
    /// across candidates, with the 1-HP enemy placed LAST in `setup.Wave` — which is also last in
    /// `BattleRunState.LiveActorKeys`, i.e. the one entry `MaxCandidatesScored = 32` truncates. The
    /// whole scenario exists to make a reorder observably change who gets hit.</summary>
    static readonly int CrowdedEnemyCount = 32;

    static BoardState CrowdedBoard()
    {
        var board = new BoardState(new GridSpec(200, 8));
        board.Place("squad:0", new GridPos(0, 0));
        for (var i = 1; i <= CrowdedEnemyCount; i++)
            board.Place($"wave:{i:00}", new GridPos(5 * i, 0));
        board.Place("wave:low", new GridPos(5 * (CrowdedEnemyCount + 1), 0));
        return board;
    }

    static BattleSetup CrowdedSetup(bool bloodthirsty)
    {
        var wave = new List<BattleActorSetup>(CrowdedEnemyCount + 1);
        for (var i = 1; i <= CrowdedEnemyCount; i++)
            wave.Add(new BattleActorSetup { Key = $"wave:{i:00}", Side = "wave", MaxHp = 1000 });
        wave.Add(new BattleActorSetup { Key = "wave:low", Side = "wave", MaxHp = 1 });

        // A durable attacker so the observation is about TARGETING, never about who survives: the
        // battle must run long enough for a missed round-1 swing to be retried.
        var attacker = new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", MaxHp = 100_000,
            TraitIds = bloodthirsty ? new[] { "bloodthirsty" } : Array.Empty<string>(),
        };
        return new BattleSetup { Squad = new[] { attacker }, Wave = wave.ToArray() };
    }

    [Fact]
    public void With_aiTuning_a_cap_dropped_candidate_is_scored_once_a_bloodthirsty_actor_carries_the_trait()
    {
        // Control: no trait -> the 33rd candidate falls outside `MaxCandidatesScored` (32) and is
        // never in the scored set at all.
        var plain = BattleEngine.Resolve(CrowdedSetup(bloodthirsty: false), seed: 1, board: CrowdedBoard(),
            aiTuning: SiegeAiTuningForTest());
        Assert.True(plain.Actors.Single(a => a.Key == "wave:low").HpRemaining > 0,
            "control: the cap-dropped candidate must be untouched without the trait");

        // Trait: the SAME setup with `bloodthirsty`. `TraitAwareBattleView.LiveActorKeysFor` moves the
        // lowest-HP enemy to the front of the set the scorer walks, so the REAL `SiegeAiIntentSource`
        // (this battle's `DefaultAiIntentSource`, not the stub) now includes and kills it.
        var trait = BattleEngine.Resolve(CrowdedSetup(bloodthirsty: true), seed: 1, board: CrowdedBoard(),
            aiTuning: SiegeAiTuningForTest());
        Assert.True(trait.Actors.Single(a => a.Key == "wave:low").HpRemaining <= 0,
            "the bloodthirsty decorator must reach the scored policy, not only the stub fallback");
    }

    // combat-ai `profile-schema` CAI1.8 (H7): AiTuning narrowed to geometry/dead keys. The ten scorer
    // weights this test relies on to make "far" the scored-better target now come from
    // CombatAiProfilePolicy.For(AiPlace.Siege, AiRole.Default) -- configured process-wide by
    // ContractTuningTestBootstrap with the SAME shipped identity values, so BattleRunState's own
    // aiTuning != null construction path (which reads that hub) sees the same numbers this test used
    // to pass inline.
    static AiTuning SiegeAiTuningForTest() => new(
        ObjectiveReferenceDistanceCells: 10, ThreatRadiusCells: 4);
}
