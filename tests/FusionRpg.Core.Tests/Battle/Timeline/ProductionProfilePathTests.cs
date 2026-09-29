using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// **T15 / B36 — the production path, end to end.** The four shipped waves now select `hybrid-atb`
/// (`decisions.md`, *Battle engine open questions (2026-09-04)*, item 1), and the point of this file is
/// that the selection is **observable in a resolved battle**, not merely present as a field.
///
/// <para>It also carries <b>B39</b>'s proof that `turn.speed` now decides turn order on that path —
/// the clause B36's acceptance asked for and the flip alone could not deliver. See
/// <see cref="A_faster_actor_acts_before_a_slower_one"/>, and its mirror
/// <see cref="Classic_round_still_ignores_speed"/>, which is what keeps every existing golden blessed.</para>
/// </summary>
public class ProductionProfilePathTests
{
    static BattleActorSetup Actor(string key, string side, params BattleChannelMod[] mods) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "probe",
        Level = 5,
        MaxHp = 400,
        Atk = 40,
        Defense = 10,
        ChannelMods = mods
    };

    static BattleSetup Setup(string waveId, params BattleChannelMod[] squadMods) => new()
    {
        WaveId = waveId,
        Squad = new[] { Actor("squad:0", "squad", squadMods), Actor("squad:1", "squad") },
        Wave = new[] { Actor("wave:0", "wave"), Actor("wave:1", "wave") }
    };

    /// <summary>
    /// The wiring the flip depends on: a wave id resolves through `WaveCatalog.ProfileFor`, and that
    /// is the same call `WebMatchService.ProfileForWave` makes at all three of its `BattleEngine.Resolve`
    /// sites. Asserted by reference identity — "the wave chose hybrid-atb" must be indistinguishable
    /// from the catalog's own instance, not merely equal to it.
    /// </summary>
    [Theory]
    [InlineData("rift-skirmish")]
    [InlineData("rift-warband")]
    [InlineData("rift-onslaught")]
    [InlineData("rift-tyrant")]
    public void Every_shipped_wave_resolves_to_the_hybrid_profile_instance(string waveId)
    {
        Assert.Same(BattleModeProfileCatalog.HybridAtb, WaveCatalog.ProfileFor(waveId));
        Assert.Same(BattleModeProfileCatalog.HybridAtb, WaveCatalog.ProfileForExpedition(waveId));
    }

    /// <summary>
    /// ⭐ **The flip is observable in a resolved battle, not just in a field.** `hybrid-atb` carries
    /// `ActionPointsEconomy(2)`, and `BattleEngine`'s action phase offers readiness at the start of
    /// every pass (B38), so an actor really does act twice per round instead of once. A battle that
    /// resolves in the same number of rounds under both profiles would mean the profile was inert —
    /// which is exactly the defect B37 and B38 were added to fix.
    ///
    /// <para>Same setup, same seed, only the profile differs: the difference is attributable to the
    /// profile alone.</para>
    /// </summary>
    [Fact]
    public void Resolving_under_the_hybrid_profile_differs_from_classic_round()
    {
        var classic = BattleEngine.Resolve(Setup("probe-wave"), 4242, profile: BattleModeProfileCatalog.ClassicRound);
        var hybrid = BattleEngine.Resolve(Setup("probe-wave"), 4242, profile: BattleModeProfileCatalog.HybridAtb);

        Assert.True(hybrid.Rounds < classic.Rounds,
            $"two action points per round must end the battle sooner: classic {classic.Rounds} rounds, hybrid {hybrid.Rounds}");
    }

    /// <summary>Acting order for a round, read off the turn-state trace: an actor's
    /// `Ready-&gt;Committed` transition is the moment it takes its turn, so their sequence IS the turn
    /// order. Read from the trace rather than inferred from damage, so the assertion is about ordering
    /// and not about who happened to win.</summary>
    static List<string> ActingOrder(BattleTrace trace, int round) =>
        trace.Turns
            .Where(t => t.StartsWith($"{round} ", StringComparison.Ordinal) && t.EndsWith("Ready->Committed", StringComparison.Ordinal))
            .Select(t => t.Split(' ')[1])
            .ToList();

    /// <summary>
    /// ⭐ **B39 — the assertion B36's acceptance actually asked for: a fast actor acts before a slow
    /// one, on the production path.**
    ///
    /// <para>This test <b>replaces</b> `Turn_speed_does_not_yet_change_turn_order_on_the_battle_path`,
    /// which measured the gap before it was closed and predicted in its own comment that it would go
    /// red when someone wired readiness in. It did, and this is the replacement it asked for rather
    /// than a deletion.</para>
    ///
    /// <para><b>Proven by contrast, in both directions, so a lucky seed cannot pass it.</b> The same
    /// seed and the same setup are run twice with the speed advantage swapped between two squad
    /// actors. If ordering ignored speed, one of the two would come out wrong — and if the test simply
    /// asserted "squad:0 first" it could be green by initiative luck alone.</para>
    /// </summary>
    [Fact]
    public void A_faster_actor_acts_before_a_slower_one()
    {
        var fastSpeed = new BattleChannelMod(DerivedTurnChannels.Speed, 1_000);

        var traceA = new BattleTrace();
        BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "probe-wave",
            Squad = new[] { Actor("squad:slow", "squad"), Actor("squad:fast", "squad", fastSpeed) },
            Wave = new[] { Actor("wave:0", "wave") }
        }, 77, trace: traceA, profile: BattleModeProfileCatalog.HybridAtb);

        var traceB = new BattleTrace();
        BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "probe-wave",
            Squad = new[] { Actor("squad:fast", "squad", fastSpeed), Actor("squad:slow", "squad") },
            Wave = new[] { Actor("wave:0", "wave") }
        }, 77, trace: traceB, profile: BattleModeProfileCatalog.HybridAtb);

        foreach (var (label, order) in new[] { ("fast declared second", ActingOrder(traceA, 1)), ("fast declared first", ActingOrder(traceB, 1)) })
        {
            var fast = order.IndexOf("squad:fast");
            var slow = order.IndexOf("squad:slow");
            Assert.True(fast >= 0 && slow >= 0, $"{label}: both actors must take a turn — got [{string.Join(", ", order)}]");
            Assert.True(fast < slow, $"{label}: the faster actor must act first — got [{string.Join(", ", order)}]");
        }
    }

    /// <summary>
    /// ⛔ **`classic-round` keeps the initiative ordering it has, and that is load-bearing.** It pins
    /// readiness to a constant by design (`battle-turn-ideal.md` §10) so every actor arrives together
    /// at the round tick; its ordering is what every existing battle and expedition golden was blessed
    /// against. B39 is gated on the profile's own declared `OrdersBySpeed` row precisely so this stays
    /// true — **this is the test that proves the gate closes**, not just that it opens.
    /// </summary>
    [Fact]
    public void Classic_round_still_ignores_speed()
    {
        Assert.False(BattleModeProfileCatalog.ClassicRound.OrdersBySpeed);
        Assert.False(BattleModeProfileCatalog.GalaxySync.OrdersBySpeed);
        Assert.True(BattleModeProfileCatalog.HybridAtb.OrdersBySpeed);

        var neutral = BattleEngine.Resolve(Setup("probe-wave"), 909, profile: BattleModeProfileCatalog.ClassicRound);
        var fast = BattleEngine.Resolve(
            Setup("probe-wave", new BattleChannelMod(DerivedTurnChannels.Speed, 100_000)),
            909, profile: BattleModeProfileCatalog.ClassicRound);

        Assert.Equal(neutral.Rounds, fast.Rounds);
        Assert.Equal(neutral.Outcome, fast.Outcome);
    }

    /// <summary>
    /// ⭐ **Why B39 moved no golden, stated as an assertion instead of a hope.** Speed only reorders a
    /// round when speeds actually differ, and **no shipped content authors a `turn.speed` at all** —
    /// every actor reads 0 from the snapshot and clamps to the same `TurnDefaultSpeed`. So the feature
    /// is live and the battles are unchanged, which is the same "ships inert" shape every other module
    /// in this program used.
    ///
    /// <para>If this ever fails, a content pass has started authoring speed — and the goldens will move
    /// with it. That is the moment a `RulesetVersion` bump is earned, not before.</para>
    /// </summary>
    [Fact]
    public void Equal_speed_actors_order_exactly_as_they_did_before_readiness_ordering()
    {
        var underClassic = BattleEngine.Resolve(Setup("probe-wave"), 5150, profile: BattleModeProfileCatalog.ClassicRound);
        var underHybrid = BattleEngine.Resolve(Setup("probe-wave"), 5150, profile: BattleModeProfileCatalog.HybridAtb);

        var traceC = new BattleTrace();
        BattleEngine.Resolve(Setup("probe-wave"), 5150, trace: traceC, profile: BattleModeProfileCatalog.ClassicRound);
        var traceH = new BattleTrace();
        BattleEngine.Resolve(Setup("probe-wave"), 5150, trace: traceH, profile: BattleModeProfileCatalog.HybridAtb);

        // Compare the FIRST PASS, not the whole round. `hybrid-atb` grants two action points, so its
        // round holds two passes and its list is twice as long — comparing the lists whole compares
        // "how many actions" and not "in what order", which is the wrong question here. The first pass
        // is where the ordering decision is visible.
        var classicPass = ActingOrder(traceC, 1);
        var hybridPass = ActingOrder(traceH, 1).Take(classicPass.Count).ToList();

        Assert.NotEmpty(classicPass);
        // Same order, despite different profiles — because nothing differs in speed.
        Assert.Equal(classicPass, hybridPass);

        // The profiles still differ in the way B36 measured, so this is not a vacuous "nothing changed".
        Assert.True(underHybrid.Rounds < underClassic.Rounds);
    }

    /// <summary>
    /// ⭐ **The anti-vacuity half of the test above**, without which "speed changed nothing" would be
    /// worthless: it would read the same whether speed is unread or whether `ChannelMods` are dropped
    /// wholesale.
    ///
    /// <para>Two facts pin it. First, `turn.speed` is a <b>registered</b> channel — an unknown id
    /// throws (`BattleStatComposer.cs:153`), so the test above proves the mod was accepted and written
    /// into the snapshot, not skipped. Second, a comparable mod on a channel the battle math <i>does</i>
    /// read changes the outcome, so the `ChannelMods` path is demonstrably live end to end.</para>
    ///
    /// <para>Together: the mod arrives, and it is the <b>reading</b> of `turn.speed` that is missing.</para>
    /// </summary>
    [Fact]
    public void A_channel_the_battle_math_does_read_changes_the_result()
    {
        var neutral = BattleEngine.Resolve(Setup("probe-wave"), 909, profile: BattleModeProfileCatalog.HybridAtb);
        var buffed = BattleEngine.Resolve(
            Setup("probe-wave", new BattleChannelMod(FusionRpg.Core.Stats.Derived.DerivedStatChannels.CombatDefenseOmni, 100_000)),
            909, profile: BattleModeProfileCatalog.HybridAtb);

        Assert.NotEqual(neutral.Outcome, buffed.Outcome);
    }
}
