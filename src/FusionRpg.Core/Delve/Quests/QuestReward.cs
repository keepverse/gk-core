using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Items.Drops;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>The window a completed quest brings to the domain's own `cache` table — never a private
/// table (spec §4, verbatim: "No private table: the domain's `cache` binding is the table; the quest
/// brings only its window"). <see cref="ComposedFloorRung"/> is already the STRONGEST of every floor
/// source folded together (`RarityShift.ComposeFloor`); <see cref="CeilRung"/> is the raw rung id —
/// "zeroes every rung above it in [the room's own weight shift]" is `RarityShift.Apply`'s own job
/// (see this file's own doc comment for why that step is not built here).</summary>
public sealed record QuestRewardWindow(string? ComposedFloorRung, string CeilRung);

/// <summary>The three pieces `CloseDelve(Extracted)` needs to fold a completed quest into one more
/// `LootPipeline` request on the SAME transaction as attrition's settlement and loot's victory rows
/// (spec §4). Building the actual `LootRequest`/rolling it is D4.14's own store wiring.</summary>
public sealed record QuestRewardRequest(LootSourceRow Source, string CorrelationId, QuestRewardWindow Window);

/// <summary>
/// D4.12 (spec-delve-quests.md §4, "Completion and reward") — assembles the three pieces above. Every
/// number/table this needs is a plain caller-supplied value straight from `DungeonTuning.QuestsRewardBand`
/// and the domain's own `lootBinding` — this file resolves none of `dungeon.v1.json`'s own object
/// shapes itself.
///
/// <para><b>Confirmed already built, contrary to the spec's own "today throws" note</b>: the spec
/// says `LootCorrelation.Derive`/`DropTableValidator.KnownSourceKinds` do not yet accept
/// `"dungeon-quest"` — reading both directly (`LootPipeline.cs:108-109`, `DropTableValidator.cs:56-57`)
/// shows `"dungeon-quest"` is ALREADY a known source kind with its own correlation arm, landed
/// proactively during this same session's earlier `dungeon-loot` work (D3.13/D3.16) — the gap the
/// spec named is already closed, so this file calls both directly rather than extending them.</para>
///
/// <para><b>Honestly NOT built, the same pre-existing gap D3.11/D3.14 already named</b>:
/// `DelveLoot.RollRoom` and `RarityShift.Apply` (the orchestrator that actually composes a floor AND
/// a weight-shift into one live per-room table) do not exist anywhere in the tree — confirmed by
/// reading `DelveLoot.cs`/`DropResult.cs`'s own doc comments directly, both still citing the identical
/// gap. `QuestReward.Request` therefore returns the CORRECT INPUTS `RollRoom` will need
/// (<see cref="QuestRewardRequest"/>), never a synthesized table — actually rolling the quest's own
/// reward waits on that same upstream gap closing, not on anything specific to this task.</para>
/// </summary>
public static class QuestReward
{
    public static QuestRewardRequest Request(
        QuestRow quest, long delveId, IReadOnlyDictionary<string, string> lootBinding,
        IReadOnlyDictionary<string, RewardWindow> rewardBandsByMember, IReadOnlyList<RarityRung> ladder,
        int thetaRun, string? additionalFloor = null)
    {
        if (quest is null) throw new ArgumentNullException(nameof(quest));
        if (lootBinding is null) throw new ArgumentNullException(nameof(lootBinding));
        if (rewardBandsByMember is null) throw new ArgumentNullException(nameof(rewardBandsByMember));
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));

        if (!lootBinding.TryGetValue("cache", out var tableId))
            throw new ArgumentException("the domain has no 'cache' lootBinding entry", nameof(lootBinding));
        if (!rewardBandsByMember.TryGetValue(quest.RewardBand, out var window))
            throw new ArgumentException($"'{quest.RewardBand}' has no quests.rewardBand.* entry", nameof(rewardBandsByMember));

        // Spec §4 step 2, verbatim: sourceId is pre-joined "{delveId}:quest:{questId}" — the exact
        // shape LootCorrelation.Derive's own "dungeon-quest" arm already expects (prepend-only).
        var sourceId = $"{delveId}:quest:{quest.QuestId}";
        var source = new LootSourceRow("dungeon-quest", sourceId, tableId, ContentLevel: thetaRun);
        var correlationId = LootCorrelation.Derive("dungeon-quest", sourceId);

        // Spec §4 step 3: "floorRung joins RarityShift.ComposeFloor" -- the quest's own window floor
        // folded together with whatever OTHER floor sources the caller already has on hand (a room
        // kind's own floor, a once-domain boss floor), never overriding them.
        var composedFloor = RarityShift.ComposeFloor(ladder, additionalFloor, window.FloorRung);

        return new QuestRewardRequest(source, correlationId, new QuestRewardWindow(composedFloor, window.CeilRung));
    }
}
