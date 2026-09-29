using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using FusionRpg.Core.Tests.World.Topology;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// world-map W34 — momentum, built as hysteresis (spec-ai-commander.md §Momentum, amended
/// 2026-08-31 on the owner's decision).
///
/// <para><b>The defect.</b> Zomboss alternated <c>defend black-gate</c> (threat 899) /
/// <c>expand to verdant-shelf</c> (value 436) from turn 8 onward and never arrived anywhere. That is
/// a feedback loop between two rules, not a near-tie inside one: Defend pulls the legion home, the
/// garrison rises, threat stops exceeding it, Expand sends it out, the garrison drops. A bonus term
/// has nothing to attach to in a rule ladder — hysteresis is what breaks a loop like that.</para>
///
/// <para><b>These tests exist because the rest of the suite cannot see this feature.</b> Every other
/// fixture builds <c>new BelievedWorldView(world, factionId)</c>, where the last-orders argument
/// defaults to empty and momentum therefore never fires. The full Core suite going green after the
/// change proved only that nothing regressed — not that anything happened. This file supplies the
/// argument.</para>
/// </summary>
public class MomentumHysteresisTests
{
    // Same shape the existing Expand coverage uses: a line of four, Zomboss owning everything but
    // the far end, so nothing is unknown and Explore declines — which is what lets the ladder fall
    // through to Expand, the rule momentum actually damps.
    static WorldState Map() => GraphShapes.From(600, "a-b", "b-c", "b-d") with
    {
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction
            {
                FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Z",
                PolicyId = FrontierRulesPolicy.Id
            }
        }
    };

    static WorldSlot Slot(int index, string typeId) => new()
    {
        SlotIndex = index,
        SlotTypeId = typeId,
        GuardState = GuardState.Cleared
    };

    // Copied from FrontierRulesTests' own proven band rather than invented. A Legion of Fighters
    // fails SurvivesTheRoute at the march-loam gate, so the ladder falls to Hold and never reaches
    // Expand — which is exactly how the first draft of this file ended up asserting nothing.
    const string LegionId = "e-zomboss-1";

    static WorldEntity Band(string at) => new()
    {
        EntityId = LegionId,
        Kind = WorldEntityKind.Warband,
        OwnerFactionId = "zomboss",
        AtSectorId = at,
        Stance = "march",
        MovementRemaining = MovementPolicy.BudgetFor("march"),
        Members = new[]
        {
            new WorldEntityMember
            {
                SpeciesId = "normalzombie", Level = 1, Hp = 200,
                Role = WorldEntityMemberRole.Bearer
            }
        }
    };

    /// <summary>Builds a view whose faction remembers where it sent the legion last turn.</summary>
    static IWorldView View(string? standingDestination)
    {
        // TWO symmetric expansion candidates on purpose. With only one unowned sector there is no
        // alternative to hold course toward, and the hysteresis branch is unreachable — the second
        // way this file managed to assert nothing.
        var owners = new[] { "a", "b" };
        var world = Map();
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => s with
                {
                    OwnerFactionId = owners.Contains(s.SectorId) ? "zomboss" : null,
                    Slots = s.SectorId == "a" ? new[] { Slot(0, "seat") }
                          : s.SectorId is "c" or "d" ? new[] { Slot(0, "essence-deposit") }
                          : s.Slots
                })
                .ToList(),
            Entities = new[] { Band("a") }
        };
        dressed = dressed with { Intel = IntelRecorder.Observe(dressed, dressed, 0) };

        var last = standingDestination is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { [LegionId] = standingDestination };

        return new BelievedWorldView(dressed, "zomboss", last);
    }

    static IReadOnlyList<PolicyOrder> Decide(IWorldView view) =>
        FrontierRulesPolicy.Instance.Decide(view, seed: 1);

    /// <summary>
    /// The baseline, and the control this whole file depends on: with no standing choice the policy
    /// is exactly what it was before momentum existed. Without this, every assertion below could be
    /// satisfied by a policy that had simply stopped working.
    /// </summary>
    [Fact]
    public void With_no_standing_choice_the_policy_is_unchanged()
    {
        var orders = Decide(View(standingDestination: null));

        Assert.NotEmpty(orders);
        Assert.DoesNotContain(orders, o => o.Reason.Contains("holding course", StringComparison.Ordinal));
    }

    /// <summary>
    /// The margin is live and readable — a `0` here would make every test in this file pass for the
    /// wrong reason, since hysteresis with a zero margin is indistinguishable from none.
    /// </summary>
    [Fact]
    public void The_momentum_margin_is_a_configured_non_zero_tunable()
    {
        Assert.True(FrontierRulesPolicy.MomentumMarginMilli > 0,
            "momentum margin is 0 — hysteresis is inert and every other test here is vacuous");
    }

    /// <summary>
    /// <b>The behaviour.</b> When the legion is already committed to a destination, an alternative
    /// that does not beat it by the margin must not divert it — and the reason must say so, because
    /// an AI decision nobody can read is one nobody can debug.
    ///
    /// <para><b>What this test does and does not reach, established by falsifying rather than by
    /// reading.</b> Disabling the hold-course branch reddens it, so the branch is genuinely live.
    /// Setting the margin to <c>0</c> does <b>not</b> redden it: the two candidates are equal by
    /// construction, and an exact tie holds course at any margin, since the comparison is
    /// <c>bestScore &lt;= required</c>. So this covers <i>that hysteresis happens</i>, not <i>where
    /// the threshold sits</i>. Proving the threshold needs a fixture whose rival beats the standing
    /// choice by a controlled percentage — worth adding when the margin is next tuned, and named
    /// here so it is not mistaken for coverage that already exists.</para>
    /// </summary>
    [Fact]
    public void A_committed_legion_holds_course_when_the_alternative_does_not_clear_the_margin()
    {
        var free = Decide(View(standingDestination: null));

        // No early return. A `if (didn't reach Expand) return;` here made this test pass with the
        // margin set to ZERO — i.e. it asserted nothing at all. Found by falsifying, not by reading.
        var chosen = Assert.Single(free, o => o.Reason.Contains("expand to", StringComparison.Ordinal));

        // Name a DIFFERENT standing destination and re-decide. Any sector the free run did not pick
        // is by construction not better than the one it did, so it cannot clear a positive margin.
        // The rival candidate — equal in value by construction, so it cannot clear a 25% margin.
        var other = new[] { "c", "d" }
            .FirstOrDefault(s => !chosen.Reason.Contains($"expand to {s},", StringComparison.Ordinal));
        Assert.NotNull(other);

        var held = Decide(View(standingDestination: other));
        Assert.Contains(held, o => o.Reason.Contains("holding course", StringComparison.Ordinal));
    }

    /// <summary>
    /// Hysteresis must not become paralysis: a standing destination the legion has already reached is
    /// not a commitment, and must never suppress a fresh decision. Without this the term would strand
    /// a legion on the sector it arrived at — the same "never arrives anywhere" symptom it was built
    /// to cure, wearing the opposite sign.
    /// </summary>
    [Fact]
    public void A_reached_destination_is_not_a_standing_commitment()
    {
        var orders = Decide(View(standingDestination: "a"));   // the legion is standing at "a"

        Assert.DoesNotContain(orders, o => o.Reason.Contains("holding course", StringComparison.Ordinal));
    }
}
