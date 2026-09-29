using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// B12 / T4b — three real profile rows, and a basic attack driven through the full envelope end to
/// end under `galaxy-sync`. Closes Checkpoint A (battle-timeline-todo.md): "every state and
/// transition covered; W proven by contrast; Downed revive proven; a real attack runs under
/// galaxy-sync; PressTurn written; the no-branch architecture test green; zero production code
/// rewired." Nothing here touches <c>BattleEngine</c> — that is B13, explicitly out of scope.
/// </summary>
public class ModeProfileCapabilityTests
{
    // --- WaveCatalog.Get(waveId).Profile ?? classic-round ---------------------------------------

    [Fact]
    public void Every_shipped_wave_now_runs_hybrid_atb()
    {
        // T15/B36, 2026-09-04 — all four authored waves moved from `classic-round` to `hybrid-atb`
        // (decisions.md, "Battle engine open questions (2026-09-04)", item 1). This test used to be
        // `Every_shipped_wave_resolves_to_classic_round_since_none_has_chosen_yet` and asserted
        // `Assert.Null(wave.Profile)`; B36's own plan named it as the one tripwire the flip would
        // trip, and it tripped exactly as predicted.
        //
        // It is kept, inverted, rather than deleted: its job was never "the default is classic-round"
        // but "content, not a hidden default, decides the profile" — so it still earns its place by
        // failing if a row silently loses its choice and falls back.
        Assert.NotEmpty(WaveCatalog.All);
        foreach (var wave in WaveCatalog.All)
        {
            Assert.Equal(BattleModeProfileCatalog.HybridAtbId, wave.Profile);
            Assert.Same(BattleModeProfileCatalog.HybridAtb, BattleModeProfileCatalog.Resolve(wave.Profile));
        }
    }

    /// <summary>
    /// The fallback branch the test above used to cover. Deleting that coverage along with the
    /// default would have been the quiet loss in B36: `Resolve(null)` is still reachable — any wave a
    /// future content pass adds without a profile lands here — so it keeps its own assertion instead
    /// of riding on a roster that no longer exercises it.
    /// </summary>
    [Fact]
    public void A_wave_that_chooses_no_profile_still_falls_back_to_classic_round()
    {
        Assert.Same(BattleModeProfileCatalog.ClassicRound, BattleModeProfileCatalog.Resolve(null));
    }

    [Fact]
    public void A_wave_that_chose_galaxy_sync_resolves_to_it()
    {
        var wave = WaveCatalog.Get("rift-skirmish") with { Profile = BattleModeProfileCatalog.GalaxySyncId };
        Assert.Same(BattleModeProfileCatalog.GalaxySync, BattleModeProfileCatalog.Resolve(wave.Profile));
    }

    [Fact]
    public void An_unknown_profile_id_throws_rather_than_silently_falling_back()
    {
        // "Content did not choose" (null) and "content chose wrong" (a typo) are different failures
        // — only the first is the documented default.
        Assert.Throws<ArgumentException>(() => BattleModeProfileCatalog.Resolve("no-such-profile"));
    }

    [Fact]
    public void Adding_a_profile_field_never_touches_BattleSetup()
    {
        // The map's own "named Never" — asserted structurally so a future edit that adds one trips
        // a test instead of silently moving the four expedition hashes.
        var setupFields = typeof(BattleSetup).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.DoesNotContain(setupFields, p => p.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase));
    }

    // --- party-dungeon D2.9: the delve profile row -------------------------------------------------

    [Fact]
    public void DownedOnDeplete_is_false_on_every_shipped_row_except_delve()
    {
        Assert.False(BattleModeProfileCatalog.ClassicRound.DownedOnDeplete);
        Assert.False(BattleModeProfileCatalog.GalaxySync.DownedOnDeplete);
        Assert.False(BattleModeProfileCatalog.HybridAtb.DownedOnDeplete);
        Assert.False(BattleModeProfileCatalog.Siege.DownedOnDeplete);
        Assert.True(BattleModeProfileCatalog.Delve.DownedOnDeplete);
    }

    [Fact]
    public void The_ruleset_version_is_unchanged_by_adding_a_fifth_profile()
    {
        // battle-hub-fuse (2026-09-13): RulesetVersion 4 -> 5 -- an unrelated bump, but this test's
        // own claim (adding a profile alone does not move the version) still holds at the new value.
        Assert.Equal(5, BattleRuleset.RulesetVersion);
    }

    [Fact]
    public void Delve_is_hybrid_atb_shaped_but_PerSide_and_interactive()
    {
        var delve = BattleModeProfileCatalog.Delve;
        var hybrid = BattleModeProfileCatalog.HybridAtb;

        Assert.Equal(hybrid.AdvancePolicy, delve.AdvancePolicy);
        Assert.Equal(hybrid.DefaultCommitment, delve.DefaultCommitment);
        Assert.Equal(hybrid.ForecastExactness, delve.ForecastExactness);
        Assert.Equal(hybrid.OrdersBySpeed, delve.OrdersBySpeed);
        Assert.Equal(hybrid.WReact, delve.WReact); // inherited through the shared tuning row, and inert here

        Assert.Equal(WScope.PerSide, delve.WScope); // the one structural difference from hybrid-atb's Global
        Assert.True(delve.RequiresLiveInput);
        Assert.True(delve.DownedOnDeplete);
        Assert.False(delve.UsesTimelineDispatch); // not wired in this task, same status as every row but classic/galaxy/hybrid
    }

    // --- W proven by contrast, WScope=PerSide covered --------------------------------------------

    [Fact]
    public void ClassicRounds_w_one_serializes_but_galaxy_syncs_w_two_overlaps()
    {
        var classic = new ActionSlots(BattleModeProfileCatalog.ClassicRound.W, BattleModeProfileCatalog.ClassicRound.WScope);
        Assert.True(classic.TryAcquire("a", "left"));
        Assert.False(classic.TryAcquire("b", "left")); // W=1 — cannot overlap, by contrast with below

        var galaxy = new ActionSlots(BattleModeProfileCatalog.GalaxySync.W, BattleModeProfileCatalog.GalaxySync.WScope);
        Assert.True(galaxy.TryAcquire("a", "left"));
        Assert.True(galaxy.TryAcquire("b", "left")); // W=2 — provably DOES overlap, same file as above
    }

    [Fact]
    public void GalaxySyncs_PerSide_scope_lets_each_side_reach_its_own_width_independently()
    {
        var slots = new ActionSlots(BattleModeProfileCatalog.GalaxySync.W, BattleModeProfileCatalog.GalaxySync.WScope);

        Assert.True(slots.TryAcquire("left1", "left"));
        Assert.True(slots.TryAcquire("left2", "left"));
        Assert.False(slots.TryAcquire("left3", "left")); // left side's own W=2 is exhausted

        // Right side is UNAFFECTED — this is what PerSide means and Global would not give: under
        // Global W=2 the pool is shared and "right1" below would be refused too.
        Assert.True(slots.TryAcquire("right1", "right"));
        Assert.True(slots.TryAcquire("right2", "right"));
    }

    // --- the capability proof: a real basic attack, driven end to end, under galaxy-sync ---------

    static readonly ActionEnvelope BasicAttack = new()
    {
        ActionId = "basic-attack",
        TimeCostTicks = TurnReadiness.OneTurnWork,
        WindupTicks = 30,   // non-zero — the todo's own acceptance line names this explicitly
        RecoveryTicks = 20,
        ResolveOffsets = new long[] { 0 },
        SlotConsuming = true,
        Class = CooldownClass.None
    };

    /// <summary>
    /// A minimal event-loop harness — same shape as <c>TurnFsmActionEnvelopeTests.Rig</c> (advance,
    /// drain, dispatch by <see cref="TimelineEventKind"/>, repeat) but built from a real
    /// <see cref="BattleModeProfile"/> (its <c>W</c>/<c>WScope</c> govern the slot pool) and wired to
    /// <see cref="ReadinessDriver"/> so actors cycle Charging → Ready → Committed → Resolving →
    /// Recovering → Charging on their own, across many rounds, with no hand-driven transitions.
    /// </summary>
    sealed class Rig
    {
        public readonly EventQueue Queue = new(64);
        public readonly SimulationClock Clock = new();
        public readonly ActionSlots Slots;
        public readonly CooldownLedger Cooldowns = new();
        public readonly ReadinessDriver Readiness;
        public readonly ActionRunner Runner;
        public readonly List<(long Tick, string Actor, string Kind)> Report = new();

        readonly Dictionary<string, ActorTurnMachine> _actors = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _sideOf = new(StringComparer.Ordinal);
        readonly NextEventAdvance _advance = new();
        readonly List<ScheduledEvent> _buffer = new(32);

        public Rig(BattleModeProfile profile)
        {
            Slots = new ActionSlots(profile.W, profile.WScope);
            Readiness = new ReadinessDriver(Queue);
            Runner = new ActionRunner(Queue, Slots, Cooldowns, key => _actors.ContainsKey(key));
        }

        public void AddActor(string key, string side)
        {
            _actors[key] = new ActorTurnMachine(key);
            _sideOf[key] = side;
            Readiness.BeginCharging(key, TurnReadiness.OneTurnWork, TurnReadiness.SpeedScale, Clock.Now);
        }

        public void Pump(long untilTick)
        {
            for (var guard = 0; guard < 10_000; guard++)
            {
                var due = Queue.PeekDueTick();
                if (due is not { } d || d > untilTick) return;

                Clock.TryAdvance(_advance, Queue);
                _buffer.Clear();
                Queue.PopDue(Clock.Now, _buffer);

                for (var i = 0; i < _buffer.Count; i++)
                {
                    var e = _buffer[i];
                    var actor = _actors[e.OwnerKey];
                    switch ((TimelineEventKind)e.Kind)
                    {
                        case TimelineEventKind.Readiness:
                            Readiness.OnReadinessDue(actor);
                            Report.Add((Clock.Now, e.OwnerKey, "ready"));
                            var refusal = Runner.TryCommit(
                                actor, _sideOf[e.OwnerKey], new ActionIntent(BasicAttack.ActionId, null, BasicAttack), Clock.Now);
                            Report.Add((Clock.Now, e.OwnerKey, refusal == CommitRefusal.None ? "committed" : $"refused:{refusal}"));
                            break;

                        case TimelineEventKind.Resolve:
                            var outcome = Runner.OnResolveDue(actor, e);
                            Report.Add((Clock.Now, e.OwnerKey, $"resolve:{outcome}"));
                            break;

                        case TimelineEventKind.Recovery:
                            Runner.OnRecoveryDue(actor, e);
                            Report.Add((Clock.Now, e.OwnerKey, "recovered"));
                            Readiness.BeginCharging(e.OwnerKey, TurnReadiness.OneTurnWork, TurnReadiness.SpeedScale, Clock.Now);
                            break;
                    }
                }
            }
        }
    }

    [Fact]
    public void A_real_battle_resolves_end_to_end_under_galaxy_sync_and_its_report_is_inspected()
    {
        var rig = new Rig(BattleModeProfileCatalog.GalaxySync); // W=2, PerSide — sized to this battle's 2-per-side roster
        var actors = new[] { "left1", "left2", "right1", "right2" };
        rig.AddActor("left1", "left");
        rig.AddActor("left2", "left");
        rig.AddActor("right1", "right");
        rig.AddActor("right2", "right");

        // Three full rounds per actor: charge(100) -> windup(30) -> recover(20), repeating.
        const int roundsPerActor = 3;
        rig.Pump(untilTick: 500);

        var resolves = rig.Report.FindAll(r => r.Kind == "resolve:Resolved");
        // Formula, not a literal (population-pin SE3.3, 2026-09-19): actors.Length x roundsPerActor —
        // not "it didn't crash", an exact count.
        Assert.Equal(actors.Length * roundsPerActor, resolves.Count);

        foreach (var actor in actors)
            Assert.Equal(3, resolves.FindAll(r => r.Actor == actor).Count);

        // The non-zero wind-up actually elapsed: commit at 100, resolve at 130 — never the same tick.
        var committedAt100 = rig.Report.FindAll(r => r.Kind == "committed" && r.Tick == 100);
        Assert.Equal(4, committedAt100.Count); // all four actors ready and committed at the same tick
        Assert.DoesNotContain(rig.Report, r => r.Kind == "resolve:Resolved" && r.Tick == 100);
        Assert.Contains(rig.Report, r => r.Kind == "resolve:Resolved" && r.Tick == 130);

        // W=2 PerSide, exercised inside a REAL driven battle, not just ActionSlots in isolation:
        // both left actors resolve at the identical tick — impossible under W=1 — and the right
        // side does too, independently, proving the "Per" half of PerSide inside this same battle.
        var leftResolveTicks130 = resolves.FindAll(r => r.Tick == 130 && r.Actor.StartsWith("left"));
        var rightResolveTicks130 = resolves.FindAll(r => r.Tick == 130 && r.Actor.StartsWith("right"));
        Assert.Equal(2, leftResolveTicks130.Count);
        Assert.Equal(2, rightResolveTicks130.Count);

        // No refusals anywhere — the roster was sized to the profile's own width on purpose, so
        // this battle proves the concurrency mechanism rather than a contention edge case (which
        // ActionSlots's own direct tests above already cover in isolation).
        Assert.DoesNotContain(rig.Report, r => r.Kind.StartsWith("refused"));
    }

    [Fact]
    public void The_same_battle_under_classic_round_never_overlaps()
    {
        // The direct contrast: identical actors, identical basic attack, ONLY the profile differs.
        var rig = new Rig(BattleModeProfileCatalog.ClassicRound); // W=1, Global
        rig.AddActor("left1", "left");
        rig.AddActor("left2", "left");

        rig.Pump(untilTick: 500);

        // Under W=1 the second actor cannot commit until the first releases — so at least one
        // "refused:NoSlot" must appear, and no two committed actors ever share a resolve tick.
        Assert.Contains(rig.Report, r => r.Kind == "refused:NoSlot");

        var resolveTicks = rig.Report.FindAll(r => r.Kind == "resolve:Resolved").ConvertAll(r => r.Tick);
        var distinctTicks = new HashSet<long>(resolveTicks);
        Assert.Equal(resolveTicks.Count, distinctTicks.Count); // every resolve landed on its OWN tick
    }
}
