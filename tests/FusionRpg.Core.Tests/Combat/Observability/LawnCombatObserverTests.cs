using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Observability;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Observability;

/// <summary>
/// Task 0 (`lawn-combat-wire`) — the ruler itself. Every test resets the observer's static state first
/// (it mirrors `EventDrainHost`'s own always-static, main-thread-only shape) so tests never leak into
/// each other regardless of xUnit's parallel class scheduling within this collection.
/// </summary>
public class LawnCombatObserverTests
{
    public LawnCombatObserverTests() => LawnCombatObserver.ResetForTest();

    [Fact]
    public void Fresh_window_reports_real_zero_not_missing()
    {
        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(0, snap.TotalHits);
        Assert.Equal(0, snap.TotalSwings);
        Assert.Equal(0, snap.ActionTriggers);
        Assert.Equal(0, snap.StaminaSpent);
        Assert.Equal(0, snap.RegenAccrued);
        Assert.Equal(0, snap.ExhaustionEvents);
        Assert.Equal(0, snap.RpgDeltaMergedHits);
        Assert.Equal(0, snap.DroppedRecords);
        Assert.Empty(snap.RecentHits);
    }

    [Fact]
    public void Vanilla_hit_is_captured_with_explicit_zero_rpg_delta()
    {
        LawnCombatObserver.RecordVanillaHit(
            swingId: "bullet-1", attackerPtr: "AAA", victimPtr: "BBB",
            attackerSide: "plant", vanillaAmount: 20, frame: 100);

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(1, snap.TotalHits);
        Assert.Equal(1, snap.TotalSwings);
        var hit = Assert.Single(snap.RecentHits);
        Assert.Equal("bullet-1", hit.SwingId);
        Assert.Equal("AAA", hit.AttackerPtr);
        Assert.Equal("BBB", hit.VictimPtr);
        Assert.Equal(20, hit.VanillaAmount);
        // The load-bearing distinction this whole record shape exists for: a real zero, not absence.
        Assert.Equal(0, hit.RpgDelta);
        Assert.False(hit.RpgDeltaObserved);
        Assert.Null(hit.MatchupRelation);
    }

    [Fact]
    public void One_swing_many_victims_is_one_swing_many_hits()
    {
        // D8's shape, at the observer's own counting level: a piercing bullet's single swing id hits
        // three different victims — one swing, three hits, never three swings.
        LawnCombatObserver.RecordVanillaHit("bullet-1", "AAA", "V1", "plant", 10, 1);
        LawnCombatObserver.RecordVanillaHit("bullet-1", "AAA", "V2", "plant", 10, 1);
        LawnCombatObserver.RecordVanillaHit("bullet-1", "AAA", "V3", "plant", 10, 1);

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(3, snap.TotalHits);
        Assert.Equal(1, snap.TotalSwings);
        Assert.Equal(3, snap.RecentHits.Count);
    }

    [Fact]
    public void Rpg_delta_merges_into_the_matching_vanilla_record_by_swing_and_victim()
    {
        LawnCombatObserver.RecordVanillaHit("bullet-1", "AAA", "BBB", "plant", 20, 100);
        LawnCombatObserver.RecordRpgDelta(
            "bullet-1", "AAA", "BBB", rpgDelta: -6,
            attackerElement: ElementTypeId.Fire, victimElement: ElementTypeId.Ice,
            matchupRelation: ElementMatchupRelation.Strong, outcome: LawnCombatObserver.Outcomes.Crit);

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(1, snap.RecentHits.Count); // merged into ONE record, never a second row
        var hit = snap.RecentHits[0];
        Assert.Equal(20, hit.VanillaAmount);
        Assert.Equal(-6, hit.RpgDelta);
        Assert.True(hit.RpgDeltaObserved);
        Assert.Equal(ElementTypeId.Fire, hit.AttackerElement);
        Assert.Equal(ElementTypeId.Ice, hit.VictimElement);
        Assert.Equal(ElementMatchupRelation.Strong, hit.MatchupRelation);
        Assert.Equal("crit", hit.RpgOutcome);
        Assert.Equal(1, snap.RpgDeltaMergedHits);
        Assert.Equal(0, snap.RpgDeltaUnmergedRecords);
    }

    [Fact]
    public void Rpg_delta_with_no_matching_vanilla_record_is_still_recorded_not_dropped()
    {
        // The two halves can miss each other across a window boundary — the RPG-side observation must
        // still surface, on its own, rather than vanish.
        LawnCombatObserver.RecordRpgDelta(
            "bullet-9", "AAA", "BBB", rpgDelta: -3,
            attackerElement: null, victimElement: null, matchupRelation: ElementMatchupRelation.Neutral);

        var snap = LawnCombatObserver.SnapshotAndReset();

        // lawn-combat-wire L-N7: an observation that merged into nothing is counted as unmerged, never as merged.
        Assert.Equal(0, snap.RpgDeltaMergedHits);
        Assert.Equal(1, snap.RpgDeltaUnmergedRecords);
        var hit = Assert.Single(snap.RecentHits);
        Assert.True(hit.RpgDeltaObserved);
        Assert.Equal(-3, hit.RpgDelta);
        Assert.Equal(0, hit.VanillaAmount); // no vanilla half arrived — an honest zero, not a merge
    }

    /// <summary>lawn-combat-wire L-N7: an overlay miss yields a delta of 0, the same number a zero-damage hit gives; the
    /// record names the breakdown branch so a run file can explain every zero.</summary>
    [Theory]
    [InlineData(false, false, false, false, "miss")]
    [InlineData(false, true, true, true, "miss")]
    [InlineData(true, true, false, false, "parried")]
    [InlineData(true, false, true, false, "blocked")]
    [InlineData(true, false, false, true, "crit")]
    [InlineData(true, false, false, false, "hit")]
    public void Outcome_names_the_breakdown_branch(bool hit, bool parried, bool blocked, bool crit, string expected) =>
        Assert.Equal(expected, LawnCombatObserver.Outcomes.Of(hit, parried, blocked, crit));

    [Fact]
    public void Misses_are_counted_and_carried_on_the_record()
    {
        LawnCombatObserver.RecordRpgDelta("P:1", "P", "Z", 0, null, null, null, LawnCombatObserver.Outcomes.Miss);
        LawnCombatObserver.RecordRpgDelta("P:2", "P", "Z", -4326, null, null, null, LawnCombatObserver.Outcomes.Hit);

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(1, snap.RpgMisses);
        Assert.Equal(2, snap.RpgDeltaUnmergedRecords);
        Assert.Equal(new[] { "miss", "hit" }, snap.RecentHits.Select(h => h.RpgOutcome));
    }

    /// <summary>lawn-combat-wire L-N35: the overlay packet names its swing `shooter:tick`, the vanilla hit names the bullet.
    /// The RPG record must still join the vanilla hit by shooter and victim, oldest first, whatever the ptr casing.</summary>
    [Fact]
    public void Rpg_delta_joins_the_oldest_unmerged_vanilla_hit_by_shooter_and_victim()
    {
        LawnCombatObserver.RecordVanillaHit("B1", "226C30A5480", "226C3130320", "plant", 2122, 10);
        LawnCombatObserver.RecordVanillaHit("B2", "226C30A5480", "226C3130320", "plant", 2122, 20);
        LawnCombatObserver.RecordVanillaHit("B3", "226C30A5480", "OTHER", "plant", 2122, 30);

        LawnCombatObserver.RecordRpgDelta("226C30A5480:1", "226c30a5480", "226c3130320", 0, null, null, null, LawnCombatObserver.Outcomes.Miss);
        LawnCombatObserver.RecordRpgDelta("226C30A5480:2", "0x226c30a5480", "226c3130320", -4326, null, null, null, LawnCombatObserver.Outcomes.Crit);
        LawnCombatObserver.RecordRpgDelta("226C30A5480:3", "226c30a5480", "226c3130320", -9, null, null, null, LawnCombatObserver.Outcomes.Hit);

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(2, snap.RpgDeltaMergedHits);
        Assert.Equal(1, snap.RpgDeltaUnmergedRecords); // a third RPG record with no third vanilla hit stays its own row
        Assert.Equal(("B1", "miss", 0L), (snap.RecentHits[0].SwingId, snap.RecentHits[0].RpgOutcome, snap.RecentHits[0].RpgDelta));
        Assert.Equal(("B2", "crit", -4326L), (snap.RecentHits[1].SwingId, snap.RecentHits[1].RpgOutcome, snap.RecentHits[1].RpgDelta));
        Assert.False(snap.RecentHits[2].RpgDeltaObserved); // a different victim is never joined
        Assert.Equal(4, snap.RecentHits.Count);
    }

    [Fact]
    public void Overflow_beyond_the_window_cap_is_counted_never_silent()
    {
        for (var i = 0; i < LawnCombatObserver.MaxRecentHitsPerWindow + 10; i++)
            LawnCombatObserver.RecordVanillaHit($"swing-{i}", "AAA", $"V{i}", "plant", 1, i);

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(LawnCombatObserver.MaxRecentHitsPerWindow + 10, snap.TotalHits);
        Assert.Equal(LawnCombatObserver.MaxRecentHitsPerWindow, snap.RecentHits.Count);
        Assert.Equal(10, snap.DroppedRecords); // D9-shaped: the counter is the proof, not a guess
    }

    [Fact]
    public void SnapshotAndReset_starts_the_next_window_clean()
    {
        LawnCombatObserver.RecordVanillaHit("bullet-1", "AAA", "BBB", "plant", 20, 1);
        LawnCombatObserver.SnapshotAndReset();

        var second = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(0, second.TotalHits);
        Assert.Equal(0, second.TotalSwings);
        Assert.Empty(second.RecentHits);
    }

    [Fact]
    public void Not_yet_wired_hooks_are_plain_additive_counters()
    {
        LawnCombatObserver.RecordActionTrigger("bullet-1");
        LawnCombatObserver.RecordActionTrigger("bullet-1");
        LawnCombatObserver.RecordStaminaSpent(5);
        LawnCombatObserver.RecordRegenAccrued(3);
        LawnCombatObserver.RecordExhaustion();

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(2, snap.ActionTriggers);
        Assert.Equal(5, snap.StaminaSpent);
        Assert.Equal(3, snap.RegenAccrued);
        Assert.Equal(1, snap.ExhaustionEvents);
    }

    [Fact]
    public void Missing_swing_id_falls_back_to_attacker_ptr_never_drops_the_hit()
    {
        LawnCombatObserver.RecordVanillaHit(swingId: "", attackerPtr: "AAA", victimPtr: "BBB",
            attackerSide: "zombie", vanillaAmount: 7, frame: 1);

        var snap = LawnCombatObserver.SnapshotAndReset();

        Assert.Equal(1, snap.TotalHits);
        Assert.Equal(1, snap.TotalSwings);
        Assert.Equal("AAA", Assert.Single(snap.RecentHits).SwingId);
    }
}
