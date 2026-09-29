using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Tests.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>
/// D3.11 (spec-dungeon-loot.md §3's own literal Code-style pseudocode) — `DelveLoot.RollRoom`, the
/// first production host of `LootPipeline.Resolve`/`Instantiator.TryInstantiate`. A small, hand-built
/// table (one guaranteed-weight equipment entry) rather than the real item corpus, for full control
/// over exactly what draws — this file's own claim is about `RollRoom`'s ORCHESTRATION (which theta
/// goes where, which stream, which table gets patched), not about the pipeline's own draw math, which
/// `LootPipelineTests.cs` already covers extensively.
///
/// <para>Uses the REAL shipped rarity ladder (<c>DropVolumeCorpusTests.Ladder()</c>), not a synthetic
/// one — `RarityDraw.Draw`'s own pity mechanism (`LootPityState.ItemsSinceHeirloom`/`SinceSunwoven`)
/// reads specific named rungs (`heirloom`, `sunwoven`) that only the real ladder carries; a hand-built
/// 5-rung ladder (fine for `RarityShiftTests`'s own pure-math claims) throws `KeyNotFoundException`
/// here.</para>
/// </summary>
public class DelveLootRollRoomTests
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

    /// <summary>"Identity" rung tuning (this file's own doc comment: "hard is the identity row — every
    /// *MultMilli is 1000, every delta is 0"), varying only the two fields `RollRoom` actually reads.
    /// Hand-built rather than read through `DungeonTuningHub` — self-contained, no bootstrap-ordering
    /// dependency on which test runs first.</summary>
    static DifficultyRungTuning RungTuning(string? rarityFloor = null, int rarityShiftRungs = 0) => new(
        BandDelta: 0, EliteWeightMultMilli: 1000, RestWeightMultMilli: 1000, RestHealMultMilli: 1000,
        HungerMultMilli: 1000, SpiritDrainMultMilli: 1000, MerchantMarkupMultMilli: 1000,
        WildDispositionShiftRungs: 0, EnemyCountDeltaFight: 0, EnemyCountDeltaElite: 0,
        BossRetinuePerPartyDelta: 0, BossWDelta: 0, ProvisionCellsDelta: 0,
        RestEveryOtherRow: false, RestRowsOnlyBeforeBoss: false, DoubleBoss: false,
        EventSeverityTier: 2, EliteKitTier: 0, BossKitTier: 0,
        UnknownPityStepMultMilliCache: 1000, UnknownPityStepMultMilliMerchant: 1000, UnknownPityStepMultMilliFight: 1000,
        RarityFloor: rarityFloor, RarityShiftRungs: rarityShiftRungs);

    static RoomLootInput Room(
        int thetaRoom = 20, int thetaActor = 20, string tableId = "t1", ulong delveSeed = 1,
        string? keyForLaneId = null, string? roomKindFloor = null, int roomKindShift = 0, string? onceEntryFloor = null) => new(
        PlayerId: "p1", SourceKind: "dungeon-room", SourceId: "delve-1:2:3", TableId: tableId,
        Row: 2, Col: 3, DelveSeed: delveSeed, ThetaRoom: thetaRoom, ThetaActor: thetaActor,
        Rung: RungTuning(), RoomKindRarityFloor: roomKindFloor, RoomKindRarityShiftRungs: roomKindShift,
        OnceEntryBossRarityFloor: onceEntryFloor, KeyForLaneId: keyForLaneId);

    /// <summary>Records every `(grant, thetaRoom)` pair it was called with, and always succeeds with a
    /// synthetic instance id — the "counting fake" idiom this program already uses for `Mint`-shaped
    /// seams (`DelveUiPresentSink`'s own recording style).</summary>
    sealed class RecordingMint
    {
        public readonly List<(LootGrant Grant, int ThetaRoom)> Calls = new();
        public LootMintResult MintAt(LootGrant grant, int thetaRoom)
        {
            Calls.Add((grant, thetaRoom));
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
            DelveLoot.RollRoom(null!, View("t1"), Drops(), LootPityState.Empty, mint.MintAt, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.RollRoom(Room(), null!, Drops(), LootPityState.Empty, mint.MintAt, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.RollRoom(Room(), View("t1"), Drops(), LootPityState.Empty, null!, out _));
    }

    // ---- the room-drop golden ---------------------------------------------------------------------

    [Fact]
    public void A_guaranteed_draw_produces_a_manifest_and_a_matching_DropResult_row()
    {
        var mint = new RecordingMint();
        var rejection = DelveLoot.RollRoom(Room(), View("t1"), Drops(), LootPityState.Empty, mint.MintAt, out var result);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Manifest.Grants);
        Assert.Equal(result.Manifest.Grants.Count, result.Grants.Count);
        Assert.All(result.Grants, g => Assert.Equal(2, g.Row));
        Assert.All(result.Grants, g => Assert.Equal(3, g.Col));
        Assert.Null(result.Key); // no KeyForLaneId supplied
    }

    [Fact]
    public void A_room_with_a_lane_key_produces_a_deterministic_key_result_alongside_the_manifest()
    {
        var mint = new RecordingMint();
        var rejection = DelveLoot.RollRoom(
            Room(keyForLaneId: "lane-a"), View("t1"), Drops(), LootPityState.Empty, mint.MintAt, out var result);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(result!.Key);
        Assert.Equal(DropResultKind.Key, result.Key!.Kind);
        Assert.Equal("lane-a", result.Key.RefId);
        Assert.Equal(2, result.Key.Row);
        Assert.Equal(3, result.Key.Col);
    }

    // ---- the verify line's own headline: the two Θ reads are never swapped -------------------------

    [Fact]
    public void Mint_closes_over_theta_room_never_theta_actor()
    {
        var mint = new RecordingMint();
        var rejection = DelveLoot.RollRoom(
            Room(thetaRoom: 70, thetaActor: 150), View("t1"), Drops(), LootPityState.Empty, mint.MintAt, out var result);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotEmpty(mint.Calls); // the guaranteed-weight entry drew and minted at least once
        Assert.All(mint.Calls, c => Assert.Equal(70, c.ThetaRoom));
        Assert.DoesNotContain(mint.Calls, c => c.ThetaRoom == 150);
    }

    [Fact]
    public void Theta_actor_governs_count_not_content_two_rooms_differing_only_in_theta_actor_can_draw_different_counts()
    {
        // D18/§3: Θ_actor governs the VOLUME scale (count), never which item level is drawn. Two rooms
        // at the SAME Θ_room but different Θ_actor may draw a different number of grants; sampled
        // across seeds rather than pinned to one, since the volume roll is itself probabilistic.
        var lowCounts = new List<int>();
        var highCounts = new List<int>();
        for (ulong seed = 0; seed < 30; seed++)
        {
            var mintLow = new RecordingMint();
            DelveLoot.RollRoom(Room(thetaRoom: 20, thetaActor: 20, delveSeed: seed), View("t1"), Drops(),
                LootPityState.Empty, mintLow.MintAt, out var rLow);
            lowCounts.Add(rLow!.Grants.Count);

            var mintHigh = new RecordingMint();
            DelveLoot.RollRoom(Room(thetaRoom: 20, thetaActor: 400, delveSeed: seed), View("t1"), Drops(),
                LootPityState.Empty, mintHigh.MintAt, out var rHigh);
            highCounts.Add(rHigh!.Grants.Count);
        }

        Assert.True(highCounts.Sum() > lowCounts.Sum(),
            $"higher Θ_actor should draw at least as many total grants over {lowCounts.Count} seeds (low={lowCounts.Sum()}, high={highCounts.Sum()})");
    }

    // ---- determinism and stream namespacing ---------------------------------------------------------

    [Fact]
    public void Same_delve_seed_and_room_reproduce_an_identical_manifest()
    {
        var mintA = new RecordingMint();
        DelveLoot.RollRoom(Room(delveSeed: 777), View("t1"), Drops(), LootPityState.Empty, mintA.MintAt, out var a);
        var mintB = new RecordingMint();
        DelveLoot.RollRoom(Room(delveSeed: 777), View("t1"), Drops(), LootPityState.Empty, mintB.MintAt, out var b);

        Assert.Equal(a!.Manifest.Grants.Select(g => g.RollSeed), b!.Manifest.Grants.Select(g => g.RollSeed));
    }

    [Fact]
    public void Different_rooms_off_the_same_delve_seed_draw_on_their_own_stream()
    {
        var mintAt23 = new RecordingMint();
        var room23 = Room() with { Row = 2, Col = 3 };
        DelveLoot.RollRoom(room23, View("t1"), Drops(), LootPityState.Empty, mintAt23.MintAt, out var r23);

        var mintAt99 = new RecordingMint();
        var room99 = Room() with { Row = 9, Col = 9 };
        DelveLoot.RollRoom(room99, View("t1"), Drops(), LootPityState.Empty, mintAt99.MintAt, out var r99);

        Assert.NotEqual(r23!.Manifest.LootSeed, r99!.Manifest.LootSeed);
    }

    // ---- RarityShift.Apply reaches the pipeline: a floor genuinely removes low rungs ----------------

    [Fact]
    public void A_room_kind_floor_is_composed_into_the_live_table_not_the_shared_one()
    {
        var mint = new RecordingMint();
        var view = View("t1");
        DelveLoot.RollRoom(Room(roomKindFloor: "cultivated"), view, Drops(), LootPityState.Empty, mint.MintAt, out _);

        // The shared view's own table is never mutated in place -- RollRoom must patch a COPY.
        Assert.Null(view.Tables["t1"].Groups[0].Entries[0].RarityFloor);
    }
}
