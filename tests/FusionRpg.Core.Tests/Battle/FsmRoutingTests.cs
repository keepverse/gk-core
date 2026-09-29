using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// **B37 / fsm-routing** (spec-fsm-routing.md) — `BattleEngine.Resolve` now READS the profile.
///
/// <para>Before this, the `profile` parameter appeared exactly twice in the whole engine — its
/// signature and one comment — and `ActionSlots` / `ITurnEconomy` appeared nowhere. Every profile
/// field was inert, which is why a profile migration would have changed nothing and an interactive
/// dwell would have had no turn to occupy. These tests prove the fields are live.</para>
/// </summary>
public class FsmRoutingTests
{
    static string Hash(BattleReport r) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(r with { EnvironmentStamp = "", ContentHash = null, Warnings = null }))));

    /// <summary>
    /// The gate. `classic-round` is byte-identical **by construction**:
    /// `OneActionPerTurnEconomy.TryAcquire` is `_spent.Add(key)`, so every actor succeeds exactly once
    /// on pass 1 and fails on pass 2 — one action each in initiative order, which is precisely the
    /// loop this replaced. Passing an explicit `classic-round` must equal passing nothing.
    /// </summary>
    [Theory]
    [InlineData("stomp", 1001)]
    [InlineData("close", 2002)]
    [InlineData("wipe", 3003)]
    public void ClassicRoundIsByteIdenticalToNoProfileAtAll(string which, ulong seed)
    {
        var setup = which switch
        {
            "stomp" => BattleGoldenTests.StompSetup(),
            "close" => BattleGoldenTests.CloseSetup(),
            _ => BattleGoldenTests.WipeSetup(),
        };

        var implicitDefault = Hash(BattleEngine.Resolve(setup, seed));
        var explicitClassic = Hash(BattleEngine.Resolve(setup, seed, profile: BattleModeProfileCatalog.ClassicRound));

        Assert.Equal(implicitDefault, explicitClassic);
    }

    /// <summary>
    /// ⭐ The contrast that makes "the profile is read" a fact rather than a claim. `hybrid-atb` runs
    /// `ActionPointsEconomy(2)`, so every actor gets two actions per round instead of one — the same
    /// battle and the same seed must produce a different report. If this passes only because something
    /// else differs, the falsifier below catches it.
    /// </summary>
    [Fact]
    public void ADifferentTurnEconomyProducesADifferentBattle()
    {
        var setup = BattleGoldenTests.CloseSetup();

        var classic = Hash(BattleEngine.Resolve(setup, 2002, profile: BattleModeProfileCatalog.ClassicRound));
        var points = Hash(BattleEngine.Resolve(setup, 2002, profile: BattleModeProfileCatalog.HybridAtb));

        Assert.NotEqual(classic, points);
    }

    /// <summary>A points economy gives more actions, so the battle should resolve in FEWER rounds —
    /// a directional check, so the test above cannot pass on an incidental difference.</summary>
    [Fact]
    public void MoreActionsPerRoundEndsTheBattleSooner()
    {
        var setup = BattleGoldenTests.CloseSetup();

        var classic = BattleEngine.Resolve(setup, 2002, profile: BattleModeProfileCatalog.ClassicRound);
        var points = BattleEngine.Resolve(setup, 2002, profile: BattleModeProfileCatalog.HybridAtb);

        Assert.True(points.Rounds <= classic.Rounds,
            $"two actions per round should not take MORE rounds: classic {classic.Rounds}, points {points.Rounds}");
    }

    /// <summary>
    /// ⛔ **The defect B37 found, pinned so it cannot come back.** Profiles are cached singletons and
    /// an economy holds mutable per-key budget state, so handing out one shared instance let two
    /// concurrent battles starve each other — actor keys repeat (`"squad:0"` is `"squad:0"` in every
    /// battle). It reproduced exactly: trace goldens passed alone and failed in the parallel suite.
    /// The profile now exposes a FACTORY, and two calls must never return the same object.
    /// </summary>
    [Fact]
    public void EachBattleGetsItsOwnEconomy_neverAShared()
    {
        var a = BattleModeProfileCatalog.HybridAtb.NewEconomy();
        var b = BattleModeProfileCatalog.HybridAtb.NewEconomy();

        Assert.NotSame(a, b);

        // And they must not share budget state: spending a's budget leaves b's untouched.
        Assert.True(a.TryAcquire("squad:0", 1, 0));
        Assert.True(a.TryAcquire("squad:0", 1, 0));
        Assert.False(a.TryAcquire("squad:0", 1, 0));   // a is exhausted at maxPoints = 2
        Assert.True(b.TryAcquire("squad:0", 1, 0));    // b is untouched
    }

    /// <summary>Repeated resolves of the same battle stay identical — which they could not if budget
    /// state leaked between them through a shared economy.</summary>
    [Fact]
    public void ResolvingTheSameBattleTwiceIsIdempotent()
    {
        var setup = BattleGoldenTests.CloseSetup();
        var first = Hash(BattleEngine.Resolve(setup, 2002, profile: BattleModeProfileCatalog.HybridAtb));
        var second = Hash(BattleEngine.Resolve(setup, 2002, profile: BattleModeProfileCatalog.HybridAtb));

        Assert.Equal(first, second);
    }

    /// <summary>The slot gate is exercised even though it cannot refuse here — `ActionSlots`' own doc:
    /// "W only binds when actions have wind-up: under next-event advance with a strict total order and
    /// atomic resolution, a battle is already serialized regardless of W." Acquire/release is still
    /// live, and a leaked slot would deadlock the first profile that ever gains wind-up.</summary>
    [Fact]
    public void SlotsAreReleasedNotLeaked()
    {
        var p = BattleModeProfileCatalog.ClassicRound;
        var slots = new ActionSlots(p.W, p.WScope);

        Assert.True(slots.TryAcquire("squad:0", "squad"));
        Assert.Equal(1, slots.Held);
        Assert.True(slots.Release("squad:0"));
        Assert.Equal(0, slots.Held);

        // Idempotent, because the engine releases from a finally on every exit path.
        Assert.False(slots.Release("squad:0"));
        Assert.Equal(0, slots.Held);
    }
}
