using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Delve.Quests;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Tests.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>
/// `delve-quests` D4.12/D4.14 (spec-delve-quests.md §4 steps 2-3) — `DelveLoot.RollQuestReward`, the
/// orchestration this codebase's own `QuestReward.Request` (D4.12) always said it was building the
/// correct INPUTS for, once `DelveLoot.RollRoom`/`RarityShift.Apply` existed (D3.11/D3.14, closed later
/// the same day `QuestReward.Request`'s own doc comment was written and never revisited — see the
/// dated 2026-09-07 update on this task's own todo entry). This file's own claim is about
/// `RollQuestReward`'s ORCHESTRATION (which stream, which theta, which window reaches the live table),
/// not `LootPipeline`/`RarityDraw`'s own draw math, already covered exhaustively elsewhere.
///
/// <para>Uses the REAL shipped rarity ladder (`DropVolumeCorpusTests.Ladder()`), not the hand-built
/// 5-rung fixture `QuestRewardTests.cs`/`RarityShiftTests.cs` use for their own pure-math claims —
/// `RarityDraw.Draw`'s own pity mechanism unconditionally reads `sunwoven`, so a hand-built ladder
/// throws `KeyNotFoundException` the moment a real draw is attempted (`DelveLootRollRoomTests.cs`'s own
/// identical reason for the same choice).</para>
/// </summary>
public class DelveLootRollQuestRewardTests
{
    static readonly IReadOnlyList<RarityRung> Ladder = DropVolumeCorpusTests.Ladder();

    static DropTableRow OneEquipmentTable(string tableId) => new(
        tableId, SourceAllow: new[] { "web" }, MinIlvl: null, MaxIlvl: null, Enabled: true, Revision: 1,
        Groups: new[]
        {
            new DropTableGroupRow("main", Seq: 0, Rolls: 1, Entries: new[]
            {
                new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Equipment, RefId: "", Weight: 100),
            }),
        });

    static LootContentView View(string tableId) => new(
        Sources: new Dictionary<string, LootSourceRow>(),
        Tables: new Dictionary<string, DropTableRow> { [tableId] = OneEquipmentTable(tableId) },
        Ladder: Ladder,
        BaseTypesFor: (frame, role) => new[] { "item.base-a" });

    static QuestRewardRequest Reward(
        string tableId = "table.forest-cache", string questId = "quest.slay-the-warden", long delveId = 42,
        string? composedFloorRung = null, string ceilRung = "fused", int thetaRun = 20) => new(
        new LootSourceRow("dungeon-quest", $"{delveId}:quest:{questId}", tableId, ContentLevel: thetaRun),
        $"loot:delve:{delveId}:quest:{questId}",
        new QuestRewardWindow(composedFloorRung, ceilRung));

    /// <summary>Records every `(grant, contentLevel)` pair it was called with — the same "counting
    /// fake" idiom `DelveLootRollRoomTests.cs`'s own `RecordingMint` already established.</summary>
    sealed class RecordingMint
    {
        public readonly List<(LootGrant Grant, int ContentLevel)> Calls = new();
        public LootMintResult MintAt(LootGrant grant, int contentLevel)
        {
            Calls.Add((grant, contentLevel));
            return new LootMintResult(FusionRpg.Core.Effects.Atoms.AtomRejection.Ok, $"inst-{grant.Index}");
        }
    }

    static DropVolumeTuning Drops() => Tests.Items.DropVolumeTests.Tuning();

    // ---- argument validation ----

    [Fact]
    public void Null_arguments_throw()
    {
        var mint = new RecordingMint();
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.RollQuestReward(null!, "q1", 1, "p1", 20, View("t1"), Drops(), LootPityState.Empty, mint.MintAt, 0, 0, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.RollQuestReward(Reward(), null!, 1, "p1", 20, View("t1"), Drops(), LootPityState.Empty, mint.MintAt, 0, 0, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.RollQuestReward(Reward(), "q1", 1, "p1", 20, null!, Drops(), LootPityState.Empty, mint.MintAt, 0, 0, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.RollQuestReward(Reward(), "q1", 1, "p1", 20, View("t1"), Drops(), LootPityState.Empty, null!, 0, 0, out _));
    }

    // ---- the reward-rolling golden ----

    [Fact]
    public void A_guaranteed_draw_produces_a_manifest()
    {
        var mint = new RecordingMint();
        var reward = Reward(tableId: "t1");
        var rejection = DelveLoot.RollQuestReward(
            reward, "quest.slay-the-warden", delveSeed: 1, "p1", thetaActor: 20,
            View("t1"), Drops(), LootPityState.Empty, mint.MintAt, 0, 0, out var manifest);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(manifest);
        Assert.NotEmpty(manifest!.Grants);
        Assert.Equal(reward.CorrelationId, manifest.CorrelationId);
    }

    // ---- the verify line's own headline: the two Θ reads are never swapped -------------------------

    [Fact]
    public void Mint_closes_over_the_rewards_own_content_level_never_thetaActor()
    {
        var mint = new RecordingMint();
        var reward = Reward(tableId: "t1", thetaRun: 83);
        var rejection = DelveLoot.RollQuestReward(
            reward, "quest.x", delveSeed: 1, "p1", thetaActor: 500,
            View("t1"), Drops(), LootPityState.Empty, mint.MintAt, 0, 0, out _);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotEmpty(mint.Calls);
        Assert.All(mint.Calls, c => Assert.Equal(83, c.ContentLevel));
        Assert.DoesNotContain(mint.Calls, c => c.ContentLevel == 500);
    }

    [Fact]
    public void ThetaActor_governs_count_not_content_two_rewards_differing_only_in_thetaActor_can_draw_different_counts()
    {
        var lowCounts = new List<int>();
        var highCounts = new List<int>();
        for (ulong seed = 0; seed < 30; seed++)
        {
            var mintLow = new RecordingMint();
            DelveLoot.RollQuestReward(Reward(tableId: "t1"), "quest.x", seed, "p1", thetaActor: 20,
                View("t1"), Drops(), LootPityState.Empty, mintLow.MintAt, 0, 0, out var mLow);
            lowCounts.Add(mLow!.Grants.Count);

            var mintHigh = new RecordingMint();
            DelveLoot.RollQuestReward(Reward(tableId: "t1"), "quest.x", seed, "p1", thetaActor: 400,
                View("t1"), Drops(), LootPityState.Empty, mintHigh.MintAt, 0, 0, out var mHigh);
            highCounts.Add(mHigh!.Grants.Count);
        }

        Assert.True(highCounts.Sum() > lowCounts.Sum(),
            $"higher Θ_actor should draw more total grants over {lowCounts.Count} seeds (low={lowCounts.Sum()}, high={highCounts.Sum()})");
    }

    // ---- determinism and stream namespacing ---------------------------------------------------------

    [Fact]
    public void Same_delve_seed_and_quest_reproduce_an_identical_manifest()
    {
        var mintA = new RecordingMint();
        DelveLoot.RollQuestReward(Reward(tableId: "t1"), "quest.x", 777, "p1", 20,
            View("t1"), Drops(), LootPityState.Empty, mintA.MintAt, 0, 0, out var a);
        var mintB = new RecordingMint();
        DelveLoot.RollQuestReward(Reward(tableId: "t1"), "quest.x", 777, "p1", 20,
            View("t1"), Drops(), LootPityState.Empty, mintB.MintAt, 0, 0, out var b);

        Assert.Equal(a!.Grants.Select(g => g.RollSeed), b!.Grants.Select(g => g.RollSeed));
    }

    [Fact]
    public void Different_quests_off_the_same_delve_seed_draw_on_their_own_stream()
    {
        var mintA = new RecordingMint();
        DelveLoot.RollQuestReward(Reward(tableId: "t1"), "quest.slay-the-warden", 1, "p1", 20,
            View("t1"), Drops(), LootPityState.Empty, mintA.MintAt, 0, 0, out var a);
        var mintB = new RecordingMint();
        DelveLoot.RollQuestReward(Reward(tableId: "t1"), "quest.gather-herbs", 1, "p1", 20,
            View("t1"), Drops(), LootPityState.Empty, mintB.MintAt, 0, 0, out var b);

        Assert.NotEqual(a!.LootSeed, b!.LootSeed);
    }

    // ---- the window reaches the live table, never the shared one -------------------------------------

    [Fact]
    public void The_windows_ceiling_is_composed_into_the_live_table_not_the_shared_one()
    {
        var mint = new RecordingMint();
        var view = View("t1");
        DelveLoot.RollQuestReward(Reward(tableId: "t1", ceilRung: "fused"), "quest.x", 1, "p1", 20,
            view, Drops(), LootPityState.Empty, mint.MintAt, 0, 0, out _);

        // The shared view's own table is never mutated in place -- RollQuestReward must patch a COPY.
        Assert.Null(view.Tables["t1"].Groups[0].Entries[0].RarityWeightShift);
    }

    [Fact]
    public void No_drawn_grant_is_ever_above_the_windows_own_ceiling()
    {
        var ceilOrdinal = RarityDraw.OrdinalOf(Ladder, "fused");
        for (ulong seed = 0; seed < 60; seed++)
        {
            var mint = new RecordingMint();
            var rejection = DelveLoot.RollQuestReward(
                Reward(tableId: "t1", ceilRung: "fused"), "quest.x", seed, "p1", thetaActor: 200,
                View("t1"), Drops(), LootPityState.Empty, mint.MintAt, 0, 0, out var manifest);
            if (!rejection.IsOk || manifest is null) continue;

            Assert.All(manifest.Grants, g => Assert.True(g.RarityOrdinal <= ceilOrdinal,
                $"seed {seed}: drew ordinal {g.RarityOrdinal}, above the window's own ceiling ({ceilOrdinal})"));
        }
    }
}
