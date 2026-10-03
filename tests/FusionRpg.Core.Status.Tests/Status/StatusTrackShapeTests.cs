using System.Reflection;
using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// status-tracks Wave 0 (ST0.1, ST0.2) — the TWO track shapes, pinned against the shipped
/// <see cref="CoreStatus.StatusRuntime"/>, plus the negative rules that keep them from collapsing
/// into one another.
///
/// <para>One mechanism, two drives (status-tracks-ideal.md §1). The combat track PULSES on a period
/// the caller passes in and expires on a stamp the caller computed; the out-of-combat track PROJECTS
/// a scalar that lives somewhere else and writes only when that scalar changes. The two differ in
/// who calls <c>Sync</c> and which clock answers the decay, and in nothing else — both go through the
/// same bag and the same vocabulary. Anything that lets a projection decay, or lets a combat status
/// accumulate, is that difference being lost.</para>
///
/// <para><b>No wall clock anywhere below.</b> Every instant is a literal and every advance is an
/// explicit <c>now.AddMilliseconds(n)</c>. A test that measured elapsed real time would assert
/// something about the machine rather than about the code, and the repo's clock-seam rule
/// (<c>scripts/guard-clock-seam.py</c>) bans the ambient read in the first place.</para>
/// </summary>
public class StatusTrackShapeTests
{
    /// <summary>A fixed instant, never <c>DateTimeOffset.UtcNow</c> — the shape
    /// <c>StatusAuthoringContractTests.Now</c> already uses in this project for the same reason.</summary>
    static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    static CoreStatus.IStatusRng Scripted => new CoreStatus.FixedStatusRng(0.0);

    // ==========================================================================================
    // ST0.1 — the combat track: apply, pulse on the passed period, gone after the passed duration
    // ==========================================================================================

    [Fact] // "apply -> Tick(now + period) -> exactly one PulseHp, instance gone after ExpiresAt"
    public void A_combat_status_pulses_once_per_period_and_is_gone_once_its_duration_has_passed()
    {
        var rt = Runtime();
        var now = Origin;
        var applied = rt.Apply(WitherApply(now), Scripted, now);
        var sink = new RecordingPulseSink();

        Assert.True(applied.Applied);
        Assert.NotNull(applied.Instance);

        // The apply's own stamp, off the caller's `now` and nothing else. A status that computed its
        // own duration would make every line below machine-dependent.
        Assert.Equal(now, applied.Instance!.AppliedAt);
        Assert.Equal(now.AddMilliseconds(5000), applied.Instance.ExpiresAt);

        // Exact counts, never a tolerance: the pulse is due the instant `now >= NextPulse`, and the
        // first instant that satisfies it is `apply + period`.
        Assert.Equal(0, rt.Tick(now, sink));                            // apply instant: nothing is due yet
        Assert.Equal(1, rt.Tick(now.AddMilliseconds(1000), sink));      // exactly one, not "at least one"
        Assert.Equal(1, sink.Count);

        // Past `ExpiresAt` the instance is pruned BEFORE the pulse gate is even reached, so the tick
        // that removes it cannot also fire it — expiry and damage never land on the same call.
        Assert.Equal(0, rt.Tick(now.AddMilliseconds(6000), sink));
        Assert.Empty(rt.ForHost("Z1"));
        Assert.Equal(1, sink.Count);                                    // still one: the prune cost nothing
    }

    // ==========================================================================================
    // ST0.1 — the out-of-combat track: a Sync drive, built here rather than borrowed
    // ==========================================================================================
    //
    // `ExhaustionPolicy.Sync` (Actions/Cost/ExhaustionPolicy.cs:99) and `NervePolicy.Sync`
    // (Delve/Attrition/NervePolicy.cs:92) are the two shipped precedents, and ST1.4's whole job is to
    // extract their shared shape into one reusable host. Until that lands, a copy in a TEST is the
    // honest thing to drive: it states the shape the program says every new track must have, without
    // pinning the assertions to a policy that does not exist yet. Mirrors `NervePolicy.Sync` field
    // for field — same live-scan, same scripted rng, same ClearGrant-then-Apply transition.
    //
    // The two ids are the shipped `nerve.*` ladder (registered by `StatusCatalogBootstrap` §9.5, each
    // `Replace`-stacked and `ModifyStat`-payloaded). Using them keeps the fixture off the process-wide
    // `StatusCategoryRegistry`, which is additive with no remove — a throwaway id planted here would
    // stay in that dictionary for the rest of the run.

    const string StageLow = "nerve.unsettled";
    const string StageHigh = "nerve.shaken";

    static readonly CoreStatus.StatusStatMod[] StageMods =
        { new CoreStatus.StatusStatMod("combat.dodge.omni", "increased", -0.1) };

    static bool SyncStage(CoreStatus.StatusRuntime rt, string hostPtr, string stageId, DateTimeOffset now)
    {
        var grantId = $"track:{hostPtr}";
        var live = rt.ForHost(hostPtr).FirstOrDefault(i =>
            i.StatusId.StartsWith("nerve.", StringComparison.Ordinal));

        if (live != null && live.StatusId == stageId)
            return false; // unchanged value: no write at all

        if (live != null)
            rt.ClearGrant(grantId); // explicit withdraw; this status never expires on its own

        return rt.Apply(
            new CoreStatus.StatusApplyInput(
                StatusId: stageId,
                HostPtr: hostPtr,
                AttackerPtr: null,
                GrantId: grantId,
                BaseMagnitude: 1.0, // inert -- ModifyStat reads StatMods directly, never EffectiveMagnitude
                BaseDuration: 0,    // 0 -> ExpiresAt = DateTimeOffset.MaxValue: persists until ClearGrant
                PeriodMs: 0,
                DurationMs: 0,
                AttackerLess: true,
                StatMods: StageMods),
            Scripted,
            now).Applied;
    }

    [Fact] // "apply -> repeat Sync at an unchanged value -> exactly one apply, no re-write"
    public void A_projection_writes_once_at_an_unchanged_value_and_never_again()
    {
        var rt = Runtime();
        var fresh = new List<string>();
        rt.OnFreshApplication += e => fresh.Add(e.Instance.StatusId);

        // Two calls a long virtual span apart at the SAME value — the shape of a pool held at its
        // threshold with regen trickling, which is the case the idempotence exists for.
        var first = SyncStage(rt, "creature:1", StageLow, Origin);
        var second = SyncStage(rt, "creature:1", StageLow, Origin.AddSeconds(30));

        // `StatusRuntime` exposes no apply counter, so idempotence is read two ways: the bool this
        // drive returns (true only on a call that really applied) and what the bag holds afterwards.
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(new[] { StageLow }, fresh);                         // one fresh slot, not one per call
        Assert.Equal(StageLow, Assert.Single(rt.ForHost("creature:1")).StatusId);
    }

    [Fact] // "a changed stage -> ClearGrant then Apply"
    public void A_changed_value_clears_the_old_grant_then_applies_the_new_one_in_that_order()
    {
        var rt = Runtime();
        var order = new List<string>();
        rt.OnEnded += i => order.Add("ended:" + i.StatusId);
        rt.OnApplied += i => order.Add("applied:" + i.StatusId);

        Assert.True(SyncStage(rt, "creature:1", StageLow, Origin));
        order.Clear();

        var changed = SyncStage(rt, "creature:1", StageHigh, Origin.AddSeconds(30));

        Assert.True(changed);
        // `OnEnded` firing at all is what makes this a transition rather than a re-apply: these ids
        // stack as `Replace`, and `UpsertInstance`'s Replace branch drops the old instance SILENTLY
        // (StatusRuntime.cs:325-331). Only `ClearGrant` withdraws a status and announces it.
        Assert.Equal(new[] { "ended:" + StageLow, "applied:" + StageHigh }, order);
        Assert.Equal(StageHigh, Assert.Single(rt.ForHost("creature:1")).StatusId);
    }

    [Fact]
    public void A_projection_instance_carries_no_expiry_stamp_and_no_pulse_schedule()
    {
        var rt = Runtime();
        SyncStage(rt, "creature:1", StageLow, Origin);

        var live = Assert.Single(rt.ForHost("creature:1"));
        // `BaseDuration: 0` and `PeriodMs: 0` both land on MaxValue (StatusRuntime.Apply:275, :288).
        // These two stamps ARE the "the out-of-combat track has no clock" rule — there is nothing for
        // elapsed time to act on.
        Assert.Equal(DateTimeOffset.MaxValue, live.ExpiresAt);
        Assert.Equal(DateTimeOffset.MaxValue, live.NextPulse);
        Assert.Null(live.AttackerPtr);
    }

    [Fact]
    public void A_projection_is_not_withdrawn_by_the_tick_however_much_time_the_caller_passes()
    {
        var rt = Runtime();
        SyncStage(rt, "creature:1", StageLow, Origin);
        var sink = new RecordingPulseSink();

        // The behavioural half a MaxValue stamp cannot prove by itself: ten years on an EXPLICIT
        // instant, `Tick` prunes nothing and pulses nothing. Only a status with no clock is immune to
        // one — and the instance's own `StatMods` are still live and readable.
        var far = Origin.AddDays(3650);

        Assert.Equal(0, rt.Tick(far, sink));
        Assert.Equal(0, sink.Count);
        var live = Assert.Single(rt.ForHost("creature:1"));
        Assert.Equal(StageMods, live.StatMods);
    }

    [Fact]
    public void A_projection_resolves_attacker_less_while_a_combat_status_resolves_with_an_attacker()
    {
        var resolutions = new List<(string? Ptr, bool AttackerLess)>();
        var rt = new CoreStatus.StatusRuntime(CoreStatus.StatusCatalogBootstrap.CreateDefault(),
            (ptr, attackerLess) =>
            {
                resolutions.Add((ptr, attackerLess));
                return attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral();
            });

        rt.Apply(WitherApply(Origin), Scripted, Origin);  // combat: an attacker exists
        SyncStage(rt, "creature:1", StageLow, Origin);    // projection: none, and none is wanted

        // `Apply` resolves the attacker slot and the defender slot separately (StatusRuntime.cs:228-229).
        // A projection must take the first one attacker-less, or `ResistFromPowerRatio` would make a
        // scripted status permanently inert the moment a real attacker stood nearby (T3.1).
        Assert.Contains(resolutions, r => r is { Ptr: "P1", AttackerLess: false });
        Assert.Contains(resolutions, r => r is { Ptr: null, AttackerLess: true });
    }

    // ==========================================================================================
    // ST0.2 — the negative rules
    // ==========================================================================================

    /// <summary>Any member whose name would read as an accumulator. Kept as a pattern rather than a
    /// list of banned words so a <c>StackDepth</c> or a <c>ApplicationCount</c> is caught by the same
    /// rule that catches <c>Count</c>.</summary>
    static readonly Regex CounterShapedName = new("count|stack", RegexOptions.IgnoreCase);

    [Fact]
    public void StatusInstance_exposes_no_member_that_could_hold_a_count_or_a_stack()
    {
        var type = typeof(CoreStatus.StatusInstance);
        var offenders = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => CounterShapedName.IsMatch(m.Name))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // A status is a DESCRIPTOR; the number a projection mirrors lives in party or durable state
        // and the status is its projection (NervePolicy.cs:22-28). A member here would make two
        // truths for one value, and the runtime would own neither.
        Assert.True(offenders.Length == 0,
            "StatusInstance must stay a descriptor, not a counter. Offending members: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Coexist_produces_two_independent_instances_never_an_accumulated_value()
    {
        var rt = Runtime();
        var fresh = 0;
        rt.OnFreshApplication += _ => fresh++;
        var now = Origin;

        // `ember` is the one shipped id whose stacking is Coexist (StatusCatalogBootstrap.cs:23), so
        // this exercises the production branch of `UpsertInstance` — its fallthrough — and not a
        // fixture's. Same id, same grant, same host, same instant.
        Assert.True(rt.Apply(EmberApply(now, "g1"), Scripted, now).Applied);
        Assert.True(rt.Apply(EmberApply(now, "g1"), Scripted, now).Applied);

        var instances = rt.ForHost("Z1");
        Assert.Equal(2, fresh);              // Coexist is always a fresh slot (UpsertInstance's fallthrough returns true)
        Assert.Equal(2, instances.Count);
        Assert.All(instances, i => Assert.Equal("ember", i.StatusId));
        // Two slots, not one rewritten: a slot that collapsed would carry one InstanceId twice.
        Assert.Equal(2, instances.Select(i => i.InstanceId).Distinct().Count());

        // THE claim. Two applications of 20 are two pulses of 20 — never one pulse of 40. A
        // counter-shaped accumulate would have moved the second instance's magnitude instead.
        Assert.All(instances, i => Assert.Equal(20.0, i.EffectiveMagnitude));
    }

    [Fact]
    public void Refresh_is_the_one_stacking_that_replaces_in_place_which_is_why_a_projection_clears_first()
    {
        var rt = Runtime();
        var now = Origin;

        rt.Apply(WitherApply(now, "g1"), Scripted, now);
        rt.Apply(WitherApply(now.AddMilliseconds(500), "g1"), Scripted, now.AddMilliseconds(500));

        // The contrast the Coexist case above needs: `wither` stacks Refresh, so the second apply
        // replaces the slot keyed on (StatusId, GrantId) rather than adding one. Nothing counts.
        var live = Assert.Single(rt.ForHost("Z1"));
        Assert.Equal(now.AddMilliseconds(500), live.AppliedAt);
    }

    // ==========================================================================================
    // fixtures
    // ==========================================================================================

    /// <summary>A real shipped DoT: `wither` is `StatusKind.OverTime` with a `PulseHp` payload, which
    /// is the combat track in its entirety (`StatusCatalogBootstrap.cs:28`).</summary>
    static CoreStatus.StatusApplyInput WitherApply(DateTimeOffset now, string grantId = "g1") => new(
        "wither",
        HostPtr: "Z1",
        AttackerPtr: "P1",
        GrantId: grantId,
        BaseMagnitude: 20,
        BaseDuration: 5000,
        PeriodMs: 1000,
        DurationMs: 5000);

    /// <summary>`ember` is `StatusStacking.Coexist` — the only shipped id that is.</summary>
    static CoreStatus.StatusApplyInput EmberApply(DateTimeOffset now, string grantId) => new(
        "ember",
        HostPtr: "Z1",
        AttackerPtr: "P1",
        GrantId: grantId,
        BaseMagnitude: 20,
        BaseDuration: 5000,
        PeriodMs: 1000,
        DurationMs: 5000);

    sealed class RecordingPulseSink : CoreStatus.IStatusPulseSink
    {
        public int Count { get; private set; }

        public void PulseHp(CoreStatus.StatusInstance instance, double amount) => Count++;
    }
}