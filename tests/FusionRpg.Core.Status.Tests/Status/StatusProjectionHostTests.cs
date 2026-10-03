using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// ST1.4/ST1.5/ST1.6/ST1.7 (<c>status-tracks</c>) — the reusable out-of-combat projection host.
///
/// <para><b>The host is extracted, not adopted, and that is deliberate.</b> ST1.4 says no existing
/// policy may be rewritten in the same change as the extraction, so <c>ExhaustionPolicy</c> and
/// <c>NervePolicy</c> keep their own hand-written <c>Sync</c>s and this host ships alongside them.
/// "Byte-identical behaviour" therefore cannot be claimed by construction — it has to be
/// PROVEN, which is what the parity rows do: two independent implementations of the same rule,
/// driven over the same call sequence on two runtimes, compared step by step on both the returned
/// "did this write" signal and the resulting live instance.</para>
///
/// <para><b>Fixture choice.</b> The parity rows use the REAL shipped ids (<c>nerve.*</c>,
/// <c>exhaustion.{id}</c>) because those are already in <c>StatusCatalogBootstrap</c>, and the
/// exhaustion family is registered into the process-wide <see cref="StatusCategoryRegistry"/> by
/// <c>ExhaustionPolicy</c>'s own constructor — a real, additive, already-shipped extension point
/// (<c>StatusCategoryRegistry.cs:40-52</c>). Tests that need a purely synthetic family only ever
/// construct one and never Apply it, so no synthetic id ever reaches the evaluator.</para>
/// </summary>
public class StatusProjectionHostTests
{
    // Fixed instants only. An out-of-combat track has no clock, and a test that reached for wall time
    // would be testing the harness rather than the host.
    static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    static readonly StatusStatMod Accuracy = new("combat.accuracy.omni", "increased", -0.1);
    static readonly StatusStatMod Defense = new("combat.defense.omni", "flat", -25);

    static readonly string[] NerveStages = { "unsettled", "shaken", "afflicted" };
    static readonly int[] NerveThresholds = { 0, 2, 4 };

    // ---- the two ladders, in BOTH the shipped shape and the host's shape ---------------------------

    static StatusProjectionRung[] NerveRungs() => new[]
    {
        new StatusProjectionRung(NerveStatusIds.For("unsettled"), new[] { Accuracy }),
        new StatusProjectionRung(NerveStatusIds.For("shaken"), new[] { Accuracy }),
        new StatusProjectionRung(NerveStatusIds.For("afflicted"), Array.Empty<StatusStatMod>()),
    };

    static IReadOnlyDictionary<string, IReadOnlyList<StatusStatMod>> NerveModsByStage() =>
        new Dictionary<string, IReadOnlyList<StatusStatMod>>(StringComparer.Ordinal)
        {
            ["unsettled"] = new[] { Accuracy },
            ["shaken"] = new[] { Accuracy },
            ["afflicted"] = Array.Empty<StatusStatMod>(),
        };

    static StatusProjectionHost NerveHost() => new(
        "nerve", StatusProjectionMatch.Prefix, NerveRungs(), "spirit", NerveStatusIds.GrantIdFor);

    static StatusProjectionHost ExhaustionHost(string resourceId) => new(
        $"exhaustion.{resourceId}", StatusProjectionMatch.Exact,
        new[] { new StatusProjectionRung(ExhaustionStatusIds.For(resourceId), new[] { Defense }) },
        resourceId,
        // exhaustion's own shape puts the resource AFTER the host, unlike nerve's single scope: a
        // host that hardcoded "{track}:{host}" would silently re-scope the shipped grant id.
        hostPtr => ExhaustionStatusIds.GrantIdFor(hostPtr, resourceId));

    static StatusCatalog Catalog() => StatusCatalogBootstrap.CreateDefault();

    static StatusRuntime Runtime(StatusCatalog catalog) => new(catalog, (_, _) => ActorDerivedSnapshot.Empty);

    /// <summary>Constructing the shipped policy is what registers the <c>exhaustion.*</c> family into
    /// the catalog AND the category registry — so this is both the parity subject and the fixture.</summary>
    static ExhaustionPolicy ShippedExhaustion(StatusCatalog catalog, params string[] resourceIds) =>
        new(catalog, resourceIds.ToDictionary(
            id => id,
            _ => (IReadOnlyList<StatusStatMod>)new[] { Defense },
            StringComparer.Ordinal));

    static NervePolicy ShippedNerve(StatusCatalog catalog) =>
        new(catalog, NerveStages, NerveModsByStage());

    static string? OnlyId(StatusRuntime runtime, string hostPtr)
    {
        var live = runtime.ForHost(hostPtr);
        Assert.True(live.Count <= 1, "one track holds at most one instance on one host");
        return live.Count == 0 ? null : live[0].StatusId;
    }

    // =========================================================================
    // ST1.4 — the reusable host
    // =========================================================================

    // ---- construction -----------------------------------------------------------------------------

    [Fact]
    public void Constructor_null_or_empty_arguments_throw()
    {
        var rungs = NerveRungs();
        Assert.Throws<ArgumentException>(() => new StatusProjectionHost(" ", StatusProjectionMatch.Prefix, rungs));
        Assert.Throws<ArgumentNullException>(() => new StatusProjectionHost("nerve", StatusProjectionMatch.Prefix, null!));
        Assert.Throws<ArgumentException>(() =>
            new StatusProjectionHost("nerve", StatusProjectionMatch.Prefix, Array.Empty<StatusProjectionRung>()));
    }

    [Fact]
    public void A_rung_with_no_statmods_list_is_refused_rather_than_assumed_empty()
    {
        // An omitted payload and an explicitly empty one are different claims about the stage, and
        // only the second is content. Collapsing them would let a rung ship with no authored stat
        // block at all.
        var rungs = new[] { new StatusProjectionRung(NerveStatusIds.For("unsettled"), null!) };

        var ex = Assert.Throws<ArgumentException>(() =>
            new StatusProjectionHost("nerve", StatusProjectionMatch.Prefix, rungs));
        Assert.Contains("unsettled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rung_with_a_blank_status_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new StatusProjectionHost(
            "nerve", StatusProjectionMatch.Prefix,
            new[] { new StatusProjectionRung("  ", Array.Empty<StatusStatMod>()) }));
    }

    [Fact]
    public void Two_stages_on_one_status_id_are_refused_because_the_middle_stage_becomes_unreachable()
    {
        // A ladder [a, b, a] collapses to [a, a]: entering stage 1 reads the live id as "already
        // projected" and writes nothing, so stage b's payload never lands. The constructor is the
        // last place that is still visible.
        var rungs = new[]
        {
            new StatusProjectionRung("testtrack.a", Array.Empty<StatusStatMod>()),
            new StatusProjectionRung("testtrack.b", Array.Empty<StatusStatMod>()),
            new StatusProjectionRung("testtrack.a", Array.Empty<StatusStatMod>()),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new StatusProjectionHost("testtrack", StatusProjectionMatch.Prefix, rungs));
        Assert.Contains("testtrack.a", ex.Message, StringComparison.Ordinal);
    }

    // ---- sync: the ladder shape -------------------------------------------------------------------

    [Fact]
    public void First_entry_into_a_stage_applies_exactly_that_stages_id()
    {
        var runtime = Runtime(Catalog());

        var result = NerveHost().Sync(runtime, "creature:1", 0, T0);

        Assert.Equal(StatusProjectionChange.Applied, result.Change);
        Assert.Equal("nerve.unsettled", Assert.Single(runtime.ForHost("creature:1")).StatusId);
    }

    [Fact]
    public void N_calls_at_an_unchanged_stage_produce_exactly_one_apply()
    {
        var runtime = Runtime(Catalog());
        var host = NerveHost();
        var writes = 0;

        // The acceptance line, COUNTED rather than inferred from end state: a counter held at a
        // threshold calls this every tick, and only the transition may write.
        for (var tick = 0; tick < 10; tick++)
            if (host.Sync(runtime, "creature:1", 1, At(tick)).Changed)
                writes++;

        Assert.Equal(1, writes);
        Assert.Single(runtime.ForHost("creature:1"));
    }

    [Fact]
    public void A_repeated_sync_at_an_unchanged_stage_reports_no_change_and_carries_no_resist_reason()
    {
        var runtime = Runtime(Catalog());
        var host = NerveHost();
        host.Sync(runtime, "creature:1", 1, T0);

        var again = host.Sync(runtime, "creature:1", 1, At(1));

        Assert.Equal(StatusProjectionChange.NoChange, again.Change);
        Assert.False(again.Changed);
        Assert.Null(again.ResistReason);
        Assert.Equal("nerve.shaken", again.StatusId);
    }

    [Fact]
    public void A_stage_change_withdraws_the_old_id_before_applying_the_new_one_so_they_never_coexist()
    {
        var runtime = Runtime(Catalog());
        var host = NerveHost();

        host.Sync(runtime, "creature:1", 0, T0);
        var changed = host.Sync(runtime, "creature:1", 2, At(1));

        Assert.Equal(StatusProjectionChange.Applied, changed.Change);
        Assert.Equal("nerve.afflicted", OnlyId(runtime, "creature:1"));
    }

    [Fact]
    public void A_stage_drop_to_minus_one_withdraws_and_is_reported_as_a_withdrawal_not_an_apply()
    {
        var runtime = Runtime(Catalog());
        var host = NerveHost();
        host.Sync(runtime, "creature:1", 1, T0);

        var dropped = host.Sync(runtime, "creature:1", -1, At(1));

        Assert.Equal(StatusProjectionChange.Withdrew, dropped.Change);
        Assert.Empty(runtime.ForHost("creature:1"));
    }

    [Fact]
    public void Stage_minus_one_with_nothing_live_is_a_no_op_and_not_a_withdrawal()
    {
        // A result that only said "wrote / did not write" could not tell these apart. Counting a
        // never-applied track's first sync as a transition would make it look like it churned.
        var runtime = Runtime(Catalog());

        var result = NerveHost().Sync(runtime, "creature:1", -1, T0);

        Assert.Equal(StatusProjectionChange.NoChange, result.Change);
        Assert.Empty(runtime.ForHost("creature:1"));
    }

    [Fact]
    public void The_applied_instance_carries_the_stage_authored_mods_verbatim()
    {
        var runtime = Runtime(Catalog());

        NerveHost().Sync(runtime, "creature:1", 1, T0);

        Assert.Equal(new[] { Accuracy }, Assert.Single(runtime.ForHost("creature:1")).StatMods);
    }

    [Fact]
    public void The_applied_instance_is_attackerless_and_never_expires_because_it_has_no_duration()
    {
        // BaseDuration 0 IS the lifetime rule for this track: a projection must not decay on a clock
        // that the scalar driving it knows nothing about, or the mirror goes stale on its own.
        var runtime = Runtime(Catalog());

        NerveHost().Sync(runtime, "creature:1", 1, T0);
        var live = Assert.Single(runtime.ForHost("creature:1"));

        Assert.Null(live.AttackerPtr);
        Assert.Equal(DateTimeOffset.MaxValue, live.ExpiresAt);
        Assert.Equal(0, live.PeriodMs);

        // A timed status is gone by now; this one is not, which is the property stated as a fact
        // rather than argued from the ExpiresAt field alone.
        runtime.Tick(At(1_000_000), sink: null);
        Assert.Single(runtime.ForHost("creature:1"));
    }

    // ---- sync: the boolean/edge shape --------------------------------------------------------------

    [Fact]
    public void The_boolean_overload_applies_rung_zero_when_active_and_withdraws_when_not()
    {
        var catalog = Catalog();
        ShippedExhaustion(catalog, "stamina"); // registers exhaustion.stamina into catalog + registry
        var runtime = Runtime(catalog);
        var host = ExhaustionHost("stamina");

        var on = host.Sync(runtime, "wave:0", active: true, T0);
        Assert.Equal(StatusProjectionChange.Applied, on.Change);
        Assert.Equal("exhaustion.stamina", OnlyId(runtime, "wave:0"));

        var off = host.Sync(runtime, "wave:0", active: false, At(1));
        Assert.Equal(StatusProjectionChange.Withdrew, off.Change);
        Assert.Empty(runtime.ForHost("wave:0"));
    }

    [Fact]
    public void A_boolean_track_repeated_at_an_unchanged_value_writes_once()
    {
        var catalog = Catalog();
        ShippedExhaustion(catalog, "stamina");
        var runtime = Runtime(catalog);
        var host = ExhaustionHost("stamina");
        var writes = 0;

        for (var tick = 0; tick < 10; tick++)
            if (host.Sync(runtime, "wave:0", active: true, At(tick)).Changed)
                writes++;

        Assert.Equal(1, writes);
        Assert.Single(runtime.ForHost("wave:0"));
    }

    [Fact]
    public void A_re_entry_after_a_withdrawal_is_a_second_real_apply()
    {
        var catalog = Catalog();
        ShippedExhaustion(catalog, "stamina");
        var runtime = Runtime(catalog);
        var host = ExhaustionHost("stamina");

        Assert.True(host.Sync(runtime, "wave:0", active: true, T0).Changed);
        Assert.True(host.Sync(runtime, "wave:0", active: false, At(1)).Changed);
        var reentry = host.Sync(runtime, "wave:0", active: true, At(2));

        Assert.Equal(StatusProjectionChange.Applied, reentry.Change);
        Assert.Single(runtime.ForHost("wave:0"));
    }

    // ---- grant ids -------------------------------------------------------------------------------

    [Fact]
    public void The_default_grant_id_is_host_scoped_and_track_named()
    {
        var host = new StatusProjectionHost("morale", StatusProjectionMatch.Exact,
            new[] { new StatusProjectionRung("morale.low", Array.Empty<StatusStatMod>()) });

        Assert.Equal("morale:creature:1", host.GrantIdFor("creature:1"));
        Assert.NotEqual(host.GrantIdFor("creature:1"), host.GrantIdFor("creature:2"));
    }

    [Fact]
    public void A_custom_grant_id_shape_is_honoured_verbatim()
    {
        Assert.Equal(
            ExhaustionStatusIds.GrantIdFor("wave:0", "poise"),
            ExhaustionHost("poise").GrantIdFor("wave:0"));
        Assert.Equal(NerveStatusIds.GrantIdFor("creature:1"), NerveHost().GrantIdFor("creature:1"));
    }

    // ---- argument validation ---------------------------------------------------------------------

    [Fact]
    public void Sync_validates_its_arguments()
    {
        var host = NerveHost();
        var runtime = Runtime(Catalog());

        Assert.Throws<ArgumentNullException>(() => host.Sync(null!, "creature:1", 0, T0));
        Assert.Throws<ArgumentException>(() => host.Sync(runtime, "  ", 0, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Sync(runtime, "creature:1", -2, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Sync(runtime, "creature:1", 3, T0));
    }

    [Fact]
    public void The_read_only_accessors_validate_the_same_way_as_Sync()
    {
        var host = NerveHost();

        Assert.Equal("nerve.shaken", host.StatusIdFor(1));
        Assert.Equal("", host.StatusIdFor(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.StatusIdFor(-2));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.StatusIdFor(3));
        Assert.Throws<ArgumentException>(() => host.GrantIdFor(" "));
    }

    [Fact]
    public void The_status_id_prefix_is_the_track_id_plus_a_dot()
    {
        Assert.Equal("nerve.", NerveHost().StatusIdPrefix);
        Assert.Equal("exhaustion.stamina.", ExhaustionHost("stamina").StatusIdPrefix);
    }

    // ---- two tracks, one runtime ------------------------------------------------------------------

    [Fact]
    public void Two_tracks_on_one_host_in_one_runtime_withdraw_independently()
    {
        var catalog = Catalog();
        ShippedExhaustion(catalog, "stamina");
        var runtime = Runtime(catalog);
        var nerve = NerveHost();
        var exhaustion = ExhaustionHost("stamina");

        nerve.Sync(runtime, "creature:1", 1, T0);
        exhaustion.Sync(runtime, "creature:1", active: true, T0);
        Assert.Equal(2, runtime.ForHost("creature:1").Count);

        runtime.ClearGrant(exhaustion.GrantIdFor("creature:1"));

        Assert.Equal("nerve.shaken", OnlyId(runtime, "creature:1"));
    }

    [Fact]
    public void Different_hosts_project_independently()
    {
        var runtime = Runtime(Catalog());
        var host = NerveHost();

        host.Sync(runtime, "creature:1", 2, T0);
        host.Sync(runtime, "creature:2", 0, T0);

        Assert.Equal("nerve.afflicted", OnlyId(runtime, "creature:1"));
        Assert.Equal("nerve.unsettled", OnlyId(runtime, "creature:2"));
    }

    // =========================================================================
    // Parity with the two SHIPPED projections, which this change does not migrate
    // =========================================================================

    [Theory]
    [InlineData(-1, 0, 1, 2, -1)]
    [InlineData(0, 1, 2, 2, 1)]
    [InlineData(-1, -1, 2, 0, -1)]
    [InlineData(2, 0, 1, 2, 2)]
    [InlineData(-1, 2, 2, 1, 0)]
    [InlineData(0, 0, 0, 0, 0)]
    public void The_host_and_NervePolicy_Sync_agree_step_for_step_over_a_walked_ladder(
        int first, int second, int third, int fourth, int fifth)
    {
        var catalog = Catalog();
        var shippedRuntime = Runtime(catalog);
        var hostRuntime = Runtime(catalog);
        var shipped = ShippedNerve(catalog);
        var host = NerveHost();
        var walked = new[] { first, second, third, fourth, fifth };

        for (var i = 0; i < walked.Length; i++)
        {
            var at = At(i);
            var shippedApplied = shipped.Sync(shippedRuntime, "creature:1", walked[i], at);
            var hostResult = host.Sync(hostRuntime, "creature:1", walked[i], at);

            // Compared on APPLIED, not on "did anything change": both shipped Syncs return true only
            // on a fresh apply and false on a withdrawal, so Change == Applied is the exact
            // reproduction of either one. `Changed` is deliberately the wider count.
            Assert.Equal(shippedApplied, hostResult.Change == StatusProjectionChange.Applied);
            Assert.Equal(OnlyId(shippedRuntime, "creature:1"), OnlyId(hostRuntime, "creature:1"));
            Assert.Equal(
                OnlyId(shippedRuntime, "creature:1") is null ? "" : shippedRuntime.ForHost("creature:1")[0].GrantId,
                OnlyId(hostRuntime, "creature:1") is null ? "" : hostRuntime.ForHost("creature:1")[0].GrantId);
        }
    }

    [Fact]
    public void The_host_and_ExhaustionPolicy_Sync_agree_step_for_step_over_a_walked_pool()
    {
        var catalog = Catalog();
        var shipped = ShippedExhaustion(catalog, "stamina");
        var shippedRuntime = Runtime(catalog);
        var hostRuntime = Runtime(catalog);
        var host = ExhaustionHost("stamina");

        // A pool oscillating across the exhaustion rail: fine, exhausted, held at the threshold,
        // recovered, exhausted again. The re-entry is the line that must produce a SECOND real apply.
        var reads = new[] { 5L, 0L, 0L, 3L, 0L };

        for (var i = 0; i < reads.Length; i++)
        {
            var at = At(i);
            var shippedApplied = shipped.Sync(shippedRuntime, "wave:0", "stamina", reads[i], at);
            var hostResult = host.Sync(hostRuntime, "wave:0", ExhaustionPolicy.IsExhausted(reads[i]), at);

            Assert.Equal(shippedApplied, hostResult.Change == StatusProjectionChange.Applied);
            Assert.Equal(OnlyId(shippedRuntime, "wave:0"), OnlyId(hostRuntime, "wave:0"));
        }

        Assert.Equal("exhaustion.stamina", OnlyId(hostRuntime, "wave:0"));
    }

    [Fact]
    public void The_host_and_NervePolicy_Sync_agree_when_the_ladder_climbs_and_falls_from_a_real_ladder_function()
    {
        // The same walk driven by NerveLadder.StageFor over a moving stack counter, which is the
        // shape a caller actually has: the ladder function decides the stage, the host writes it.
        var catalog = Catalog();
        var shippedRuntime = Runtime(catalog);
        var hostRuntime = Runtime(catalog);
        var shipped = ShippedNerve(catalog);
        var host = NerveHost();

        foreach (var (stacks, at) in new[] { (0, At(0)), (1, At(1)), (3, At(2)), (5, At(3)), (5, At(4)), (1, At(5)), (0, At(6)) })
        {
            var stage = NerveLadder.StageFor(stacks, spiritResolved: 1000L, thresholds: NerveThresholds);

            Assert.Equal(
                shipped.Sync(shippedRuntime, "creature:1", stage, at),
                host.Sync(hostRuntime, "creature:1", stage, at).Change == StatusProjectionChange.Applied);
            Assert.Equal(OnlyId(shippedRuntime, "creature:1"), OnlyId(hostRuntime, "creature:1"));
        }

        // thresholds {0, 2, 4}: zero stacks is stage 0, so the walk ENDS on the first rung. The point
        // of the row is the step-for-step agreement above, not where this particular walk lands.
        Assert.Equal("nerve.unsettled", OnlyId(hostRuntime, "creature:1"));
    }

    [Fact]
    public void The_host_writes_the_shipped_nerve_stat_payload_for_a_stage()
    {
        var catalog = Catalog();
        var shippedRuntime = Runtime(catalog);
        var hostRuntime = Runtime(catalog);
        ShippedNerve(catalog).Sync(shippedRuntime, "creature:1", 2, T0);
        NerveHost().Sync(hostRuntime, "creature:1", 2, T0);

        var shipped = Assert.Single(shippedRuntime.ForHost("creature:1"));
        var hosted = Assert.Single(hostRuntime.ForHost("creature:1"));

        Assert.Equal(shipped.StatusId, hosted.StatusId);
        Assert.Equal(shipped.StatMods, hosted.StatMods);
        Assert.Equal(shipped.GrantId, hosted.GrantId);
        Assert.Equal(shipped.ExpiresAt, hosted.ExpiresAt);
        Assert.Equal(shipped.EffectiveMagnitude, hosted.EffectiveMagnitude);
    }

    // =========================================================================
    // ST1.5 — the anti-spiral refusal
    // =========================================================================

    [Fact]
    public void A_stage_touching_its_own_driving_pools_regen_channel_is_refused_at_construction()
    {
        var rungs = new[]
        {
            new StatusProjectionRung(NerveStatusIds.For("unsettled"),
                new[] { new StatusStatMod(DerivedStatChannels.ResourceRegen("spirit"), "flat", -1) }),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new StatusProjectionHost("nerve", StatusProjectionMatch.Prefix, rungs, "spirit"));
        Assert.Contains("spirit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("resource.regen.spirit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_self_regen_refusal_covers_every_stage_not_only_the_first()
    {
        var rungs = new[]
        {
            new StatusProjectionRung("nerve.a", Array.Empty<StatusStatMod>()),
            new StatusProjectionRung("nerve.b", new[] { new StatusStatMod(DerivedStatChannels.ResourceRegen("spirit"), "flat", -1) }),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new StatusProjectionHost("nerve", StatusProjectionMatch.Prefix, rungs, "spirit"));
        Assert.Contains("nerve.b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stage_touching_a_different_pools_regen_channel_is_accepted()
    {
        // The rule is the SELF-cycle, not "no regen channel may ever appear" -- mirrors
        // NervePolicyTests.Constructor_accepts_a_debuff_touching_a_different_resources_regen.
        var host = new StatusProjectionHost("nerve", StatusProjectionMatch.Prefix,
            new[]
            {
                new StatusProjectionRung(NerveStatusIds.For("unsettled"),
                    new[] { new StatusStatMod(DerivedStatChannels.ResourceRegen("hunger"), "flat", -1) }),
            },
            "spirit");

        Assert.Equal("spirit", host.DrivingPoolResourceId);
    }

    [Fact]
    public void A_track_that_declares_no_driving_pool_is_never_refused_on_regen()
    {
        // The guard is only as good as its declaration. With nothing declared there is no channel to
        // compare against, so a track whose truth is not one of the six pools is not blocked by a
        // rule written about pools.
        var host = new StatusProjectionHost("morale", StatusProjectionMatch.Exact,
            new[] { new StatusProjectionRung("morale.low", new[] { Defense }) });

        Assert.Null(host.DrivingPoolResourceId);
    }

    [Fact]
    public void The_exhaustion_track_refuses_poises_own_regen_channel_the_way_the_shipped_policy_does()
    {
        // Poise by name, because spec-action-costs.md §7 names it: a refactor that narrowed the
        // generic check would still fail here. Both sides are given the SAME offending payload --
        // the refusal is about the channel a stage names, not about the track it came from.
        var spiral = new StatusStatMod(DerivedStatChannels.ResourceRegen("poise"), "flat", -1);

        var shipped = Assert.Throws<ArgumentException>(() => new ExhaustionPolicy(
            new StatusCatalog(),
            new Dictionary<string, IReadOnlyList<StatusStatMod>> { ["poise"] = new[] { spiral } }));
        var hosted = Assert.Throws<ArgumentException>(() => new StatusProjectionHost(
            "exhaustion.poise", StatusProjectionMatch.Exact,
            new[] { new StatusProjectionRung(ExhaustionStatusIds.For("poise"), new[] { spiral }) },
            "poise"));

        Assert.Contains("poise", shipped.Message, StringComparison.Ordinal);
        Assert.Contains("resource.regen.poise", shipped.Message, StringComparison.Ordinal);
        Assert.Contains("poise", hosted.Message, StringComparison.Ordinal);
        Assert.Contains("resource.regen.poise", hosted.Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // ST1.6 — at-most-one atomicity
    // =========================================================================

    [Fact]
    public void A_prefix_matched_ladder_holds_at_most_one_instance_per_host_ever()
    {
        var runtime = Runtime(Catalog());
        var host = NerveHost();

        foreach (var stage in new[] { 0, 1, 2, 2, 1, 0, -1, 2, 0 })
        {
            host.Sync(runtime, "creature:1", stage, At(stage + 3));
            // At most one, not exactly one: stage -1 is the withdrawn state, where the correct live
            // count is zero. Asserting "exactly one" here would make the correct empty state fail.
            Assert.True(runtime.ForHost("creature:1").Count <= 1);
        }

        // End of the walk is stage 0, so one instance is live and it is the first rung.
        Assert.Equal("nerve.unsettled", OnlyId(runtime, "creature:1"));
    }

    [Fact]
    public void A_prefix_matched_ladder_does_not_adopt_another_familys_instance_as_its_own()
    {
        var catalog = Catalog();
        ShippedExhaustion(catalog, "stamina");
        var runtime = Runtime(catalog);
        var host = NerveHost();
        ExhaustionHost("stamina").Sync(runtime, "creature:1", active: true, T0);

        host.Sync(runtime, "creature:1", 1, At(1));

        // Two instances on one host is legal -- two different tracks. The assertion is that the
        // exhaustion instance survived and that a repeat nerve sync still finds only its own.
        Assert.Equal(2, runtime.ForHost("creature:1").Count);
        Assert.Equal(StatusProjectionChange.NoChange, host.Sync(runtime, "creature:1", 1, At(2)).Change);
        Assert.Equal(2, runtime.ForHost("creature:1").Count);
    }

    [Fact]
    public void An_exact_matched_track_ignores_a_sibling_resources_instance_on_the_same_host()
    {
        var catalog = Catalog();
        ShippedExhaustion(catalog, "stamina", "hunger");
        var runtime = Runtime(catalog);

        ExhaustionHost("stamina").Sync(runtime, "wave:0", active: true, T0);
        ExhaustionHost("hunger").Sync(runtime, "wave:0", active: true, At(1));

        // Two resources on one actor are two tracks here, each with its own exact id and its own
        // grant; the exact match must read the sibling as not-mine rather than withdraw it.
        Assert.Equal(2, runtime.ForHost("wave:0").Count);
        Assert.Equal(
            new[] { "exhaustion.hunger", "exhaustion.stamina" },
            runtime.ForHost("wave:0").Select(i => i.StatusId).OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_repeated_withdraw_and_reapply_cycle_through_one_grant_leaves_at_most_one_instance()
    {
        var catalog = Catalog();
        ShippedExhaustion(catalog, "stamina");
        var runtime = Runtime(catalog);
        var host = ExhaustionHost("stamina");

        for (var i = 0; i < 6; i++)
        {
            host.Sync(runtime, "wave:0", active: i % 2 == 0, At(i));
            Assert.True(runtime.ForHost("wave:0").Count <= 1);
        }
    }

    [Fact]
    public void A_stage_change_fires_OnEnded_for_the_withdrawn_id_so_vfx_reaps_what_it_projected()
    {
        // Withdrowing explicitly rather than letting expiry handle it is what gives a caller a
        // cleanup signal; an expired instance fires OnEnded from Tick instead, which no projection
        // caller drives.
        var runtime = Runtime(Catalog());
        var ended = new List<string>();
        runtime.OnEnded = i => ended.Add(i.StatusId);

        NerveHost().Sync(runtime, "creature:1", 0, T0);
        NerveHost().Sync(runtime, "creature:1", 2, At(1));

        Assert.Equal(new[] { "nerve.unsettled" }, ended);
    }

    // =========================================================================
    // ST1.7 — the deferred-projection path is a SUPPORTED answer
    // =========================================================================

    [Fact]
    public void With_no_live_runtime_the_scalar_moves_and_the_projection_is_deferred_untouched()
    {
        // The EventOutcomeDispatch.cs:66-77 shape, exercised for real rather than described: a
        // between-rooms dispatch has no StatusRuntime and no hostPtr, so the authoritative counter
        // moves as plain arithmetic and no nerve.* id is written from here at all.
        var instance = new InstanceRow
        {
            InstanceId = "inst-1",
            ContainerId = "event:outcome",
            Atoms = new[]
            {
                new InstanceAtomRow(0, "atom.drain-spirit", "{\"channel\":\"spirit\",\"amount\":-50}"),
            },
        };

        var result = EventOutcomeDispatch.Dispatch(
            instance,
            new[] { Member("m1", nerveStacks: 0) },
            id => id == "atom.drain-spirit"
                ? new AtomRow
                {
                    AtomId = "atom.drain-spirit",
                    KindId = EventOutcomeDispatch.ResourceDeltaKind,
                    FamilyId = "atom.drain-spirit",
                    Tier = 1,
                }
                : null,
            _ => FullPools(),
            new RecordingUiPresentSink(),
            delveId: "42",
            nerveStackPerCurio: 1,
            atTick: 0);

        var after = Assert.Single(result.Members);
        Assert.Equal(1, after.NerveStacks); // the counter MOVED, and it is the authoritative one
        Assert.Equal(950, after.Pools["spirit"]);
    }

    [Fact]
    public void A_deferred_projection_reconciles_at_the_next_room_with_nothing_lost_and_nothing_replayed()
    {
        // The coherence claim ST1.7 asks to be demonstrated rather than assumed. Because the scalar
        // is the source of truth, a later Sync against a live runtime derives the SAME stage the
        // deferred moment would have written -- no replay log, no catch-up queue, nothing owed.
        var runtime = Runtime(Catalog());
        var host = NerveHost();

        // Between rooms: the counter moves and nothing is projected, because there is no runtime.
        var deferredStacks = 0 + 1;

        // Next room's battle setup, with a live runtime: the same scalar drives the same stage.
        var stage = NerveLadder.StageFor(deferredStacks, spiritResolved: 1000L, thresholds: NerveThresholds);
        var result = host.Sync(runtime, "creature:1", stage, T0);

        Assert.Equal(StatusProjectionChange.Applied, result.Change);
        Assert.Equal("nerve.unsettled", OnlyId(runtime, "creature:1"));
    }

    [Fact]
    public void The_host_refuses_a_missing_runtime_rather_than_absorbing_it_as_a_silent_no_op()
    {
        // "No runtime here" is a CALLER's decision to defer, and it is taken by not calling the host
        // at all. A host that swallowed a null instead would make a wiring mistake in battle setup
        // indistinguishable from a deliberate deferral -- the one distinction this shape rests on.
        Assert.Throws<ArgumentNullException>(() => NerveHost().Sync(null!, "creature:1", 0, T0));
    }

    // ---- fixtures for the deferred path ------------------------------------------------------------

    static ActorDerivedSnapshot FullPools() =>
        ActorDerivedSnapshot.FromValues(DerivedStatChannels.ResourceIds.SelectMany(id => new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax(id), 1000),
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceRegen(id), 0),
        }));

    static DelveMemberState Member(string id, int nerveStacks) => new(
        id,
        DerivedStatChannels.ResourceIds.ToDictionary(r => r, _ => 1000L, StringComparer.Ordinal),
        Array.Empty<BattleStatusSpec>(),
        Shield: null,
        nerveStacks,
        Downed: false,
        DownedOnce: false);
}
