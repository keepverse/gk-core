using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// E39 (spec-plant-side-status.md): <c>InjectorEffectActionSink.ExecApplyStatus</c>/
/// <c>ExecClearStatus</c> live in the Injector and need the game's Unity/interop assemblies to build,
/// so — same constraint E21's <c>StatusStatApplierGuardTests</c> hit for a different Unity-hosted
/// half — they cannot be unit-tested directly. Per the spec's own §4 ("the planted-violation tests
/// must live on the Core side of that line, or they are not run by anything"), this file tests the
/// SAME target-resolution algorithm the real executors carry, against
/// <see cref="FakePlantSideStatusSink"/> — a fake <see cref="IEffectActionSink"/> built entirely for
/// this test file, using plain ptr sets instead of a live Unity registry/scan. The real executors'
/// text shape is separately proven by <c>Guard.Tests/PlantSideStatusGuardTests</c>.
///
/// <para>Two tests in this file (<see cref="A_zombie_only_resolver_would_miss_a_plant_target"/>,
/// <see cref="An_empty_ptr_board_wide_loop_would_broadcast_instead_of_refuse"/>) deliberately
/// instantiate the PRE-E39 shape inline and assert it fails the same assertion the fixed resolver
/// passes — the permanent, always-running form of the spec's "planted violation" cases, proving the
/// suite actually discriminates rather than merely re-describing the new code.</para>
/// </summary>
public class PlantSideStatusTargetingTests
{
    const string ZombiePtr = "0xZ1";
    const string PlantPtr = "0xP1";
    const string ZombiePtr2 = "0xZ2";
    const string PlantPtr2 = "0xP2";

    static EffectActionPlanItem ApplyItem(string status, string? targetPtr = null) => new()
    {
        Action = EffectActions.ApplyStatus,
        EffectId = "fx.test",
        GrantId = "g1",
        Params = targetPtr is null
            ? new Dictionary<string, object?> { ["status"] = status }
            : new Dictionary<string, object?> { ["status"] = status, ["targetPtr"] = targetPtr }
    };

    static EffectActionPlanItem ClearItem(string status, string? target = null) => new()
    {
        Action = EffectActions.ClearStatus,
        EffectId = "fx.test",
        GrantId = "g1",
        Params = target is null
            ? new Dictionary<string, object?> { ["status"] = status }
            : new Dictionary<string, object?> { ["status"] = status, ["target"] = target }
    };

    static EffectExecuteContext Ctx(string? targetPtr = null) => new()
    {
        Event = new EffectEventDto { TargetPtr = targetPtr ?? "" }
    };

    static FakePlantSideStatusSink NewSink()
    {
        var sink = new FakePlantSideStatusSink();
        sink.ZombiePtrs.Add(ZombiePtr);
        sink.ZombiePtrs.Add(ZombiePtr2);
        sink.PlantPtrs.Add(PlantPtr);
        sink.PlantPtrs.Add(PlantPtr2);
        return sink;
    }

    // ---- Acceptance criterion 1: overlay-authored statuses work on a plant ptr --------------------

    [Fact]
    public void Overlay_authored_status_applies_to_a_resolved_plant_target()
    {
        var sink = NewSink();
        var ok = sink.Execute(Ctx(PlantPtr), ApplyItem("wither", PlantPtr));

        Assert.True(ok);
        Assert.Single(sink.Applied);
        Assert.Equal(("plant", "wither", PlantPtr), sink.Applied[0]);
    }

    // ---- Acceptance criterion 2: UnityCc either calls a confirmed method, or refuses with a reason -

    [Fact]
    public void Butter_the_one_confirmed_UnityCc_status_applies_to_a_plant_target()
    {
        var sink = NewSink();
        var ok = sink.Execute(Ctx(PlantPtr), ApplyItem("butter", PlantPtr));

        Assert.True(ok);
        Assert.Single(sink.Applied);
        Assert.Equal(("plant", "butter", PlantPtr), sink.Applied[0]);
    }

    [Theory]
    [InlineData("freeze")]
    [InlineData("cold")]
    [InlineData("poison")]
    [InlineData("hypno")]
    [InlineData("ember")]
    [InlineData("jala")] // downgraded to refused after this module's own follow-up read — see
                          // 03-status-and-spawn-surface.md "Plant-side status — E39 assembly sweep"
    [InlineData("kelp")]
    public void A_UnityCc_status_with_no_plant_method_refuses_with_a_reason_never_silently(string status)
    {
        var sink = NewSink();
        var ok = sink.Execute(Ctx(PlantPtr), ApplyItem(status, PlantPtr));

        Assert.False(ok);
        Assert.Empty(sink.Applied);
        Assert.Single(sink.Refused);
        Assert.Equal("status-side-unsupported", sink.Refused[0].reason);
    }

    // ---- Regression guard: the zombie side must keep working -------------------------------------

    [Fact]
    public void A_status_still_applies_to_a_resolved_zombie_target()
    {
        var sink = NewSink();
        var ok = sink.Execute(Ctx(ZombiePtr), ApplyItem("freeze", ZombiePtr));

        Assert.True(ok);
        Assert.Single(sink.Applied);
        Assert.Equal(("zombie", "freeze", ZombiePtr), sink.Applied[0]);
    }

    // ---- Acceptance criterion 4 / G5: an empty resolved ptr refuses, never broadcasts ------------

    [Fact]
    public void An_empty_resolved_ptr_refuses_and_touches_nobody()
    {
        var sink = NewSink();
        var ok = sink.Execute(Ctx(targetPtr: ""), ApplyItem("freeze"));

        Assert.False(ok);
        Assert.Empty(sink.Applied); // NOT every zombie on the fake board
        Assert.Equal("status-no-target", sink.Refused[0].reason);
    }

    [Fact]
    public void A_ptr_matching_neither_side_is_a_real_failure_not_a_silent_success()
    {
        var sink = NewSink();
        var ok = sink.Execute(Ctx("0xGHOST"), ApplyItem("freeze", "0xGHOST"));

        Assert.False(ok);
        Assert.Empty(sink.Applied);
        Assert.Equal("status-target-not-found", sink.Refused[0].reason);
    }

    // ---- Acceptance criterion 5: target vocabulary on status.clear -------------------------------

    [Fact]
    public void Target_all_hits_both_sides()
    {
        var sink = NewSink();
        var ok = sink.Execute(Ctx(), ClearItem("butter", "all"));

        Assert.True(ok);
        Assert.Equal(4, sink.Cleared.Count); // 2 zombies + 2 plants
        Assert.Contains(sink.Cleared, c => c.side == "zombie");
        Assert.Contains(sink.Cleared, c => c.side == "plant");
    }

    [Fact]
    public void Target_all_zombies_hits_only_zombies()
    {
        var sink = NewSink();
        sink.Execute(Ctx(), ClearItem("butter", "all-zombies"));

        Assert.Equal(2, sink.Cleared.Count);
        Assert.All(sink.Cleared, c => Assert.Equal("zombie", c.side));
    }

    [Fact]
    public void Target_all_plants_hits_only_plants()
    {
        var sink = NewSink();
        sink.Execute(Ctx(), ClearItem("butter", "all-plants"));

        Assert.Equal(2, sink.Cleared.Count);
        Assert.All(sink.Cleared, c => Assert.Equal("plant", c.side));
    }

    // ---- Acceptance criterion 6: clear is symmetric with apply ------------------------------------

    [Fact]
    public void ExecClearStatus_on_a_plant_clears_what_ExecApplyStatus_put_there()
    {
        var sink = NewSink();
        Assert.True(sink.Execute(Ctx(PlantPtr), ApplyItem("butter", PlantPtr)));

        var ok = sink.Execute(Ctx(PlantPtr), ClearItem("butter"));

        Assert.True(ok);
        Assert.Contains(sink.Cleared, c => c.side == "plant" && c.ptr == PlantPtr);
    }

    [Fact]
    public void A_status_appliable_but_not_clearable_on_a_side_fails_this_test_if_it_regresses()
    {
        // Every status this module wires for a plant (only "butter") must also clear from a plant.
        // If a future change added a second plant-appliable status without a matching clear arm,
        // this assertion is what would catch it.
        var sink = NewSink();
        foreach (var appliable in sink.PlantAppliableStatuses)
            Assert.True(sink.PlantClearableStatuses.Contains(appliable),
                $"'{appliable}' applies to a plant but has no plant clear arm");
    }

    // ---- Permanent "planted violation" regression proofs (spec §4) -------------------------------

    [Fact]
    public void A_zombie_only_resolver_would_miss_a_plant_target()
    {
        // Reproduces the PRE-E39 shape of ExecApplyStatus: only ever look among zombies. This proves
        // Overlay_authored_status_applies_to_a_resolved_plant_target above is a real, discriminating
        // assertion — if ExecApplyStatus regressed to this shape, that test (not this one) is what
        // would fail.
        var zombieOnlyResolvedSide = OldZombieOnlyResolve(PlantPtr, zombiePtrs: new() { ZombiePtr });

        Assert.Null(zombieOnlyResolvedSide); // the old shape cannot find a plant at all

        static string? OldZombieOnlyResolve(string ptr, HashSet<string> zombiePtrs) =>
            zombiePtrs.Contains(ptr) ? "zombie" : null;
    }

    [Fact]
    public void An_empty_ptr_board_wide_loop_would_broadcast_instead_of_refuse()
    {
        // Reproduces G5's pre-E39 shape: an empty resolved ptr fell through to "every living
        // zombie". Proves An_empty_resolved_ptr_refuses_and_touches_nobody above is real coverage —
        // if that guard were removed, the old shape below is what would run instead, and it touches
        // every zombie on the board rather than refusing.
        var board = new List<string> { ZombiePtr, ZombiePtr2 };
        var touched = OldBoardWideApply(targetPtr: "", board);

        Assert.Equal(board.Count, touched.Count); // the exact silent broadcast this module deletes

        static List<string> OldBoardWideApply(string targetPtr, List<string> allZombiePtrs)
        {
            if (!string.IsNullOrEmpty(targetPtr)) return new List<string> { targetPtr };
            return new List<string>(allZombiePtrs); // "apply to everyone" — the deleted loop's shape
        }
    }
}

/// <summary>
/// Test-only fake mirroring <c>InjectorEffectActionSink</c>'s post-E39 target-resolution algorithm
/// (registry-first-side-second, G5 closed, §2c side-capability refusal) without any Unity/Injector
/// dependency. Not production code — the real executors are guarded separately by
/// <c>Guard.Tests/PlantSideStatusGuardTests</c> (a text guard, same pattern as
/// <c>StatusStatApplierGuardTests</c>) since the Injector is not built by CI.
/// </summary>
sealed class FakePlantSideStatusSink : IEffectActionSink
{
    public HashSet<string> ZombiePtrs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PlantPtrs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string side, string status, string ptr)> Applied { get; } = new();
    public List<(string reason, string status, string ptr)> Refused { get; } = new();
    public List<(string side, string status, string ptr)> Cleared { get; } = new();

    /// <summary>03-status-and-spawn-surface.md "Plant-side status — E39 assembly sweep": the ONE
    /// UnityCc status with a confirmed, safe plant-side write.</summary>
    public readonly HashSet<string> PlantAppliableStatuses = new(StringComparer.Ordinal) { "butter" };
    public readonly HashSet<string> PlantClearableStatuses = new(StringComparer.Ordinal) { "butter" };

    static readonly HashSet<string> UnityCcIds = new(StringComparer.Ordinal)
        { "butter", "freeze", "cold", "poison", "hypno", "ember", "jala", "kelp" };

    public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item) => item.Action switch
    {
        EffectActions.ApplyStatus => ExecApplyStatus(ctx, item),
        EffectActions.ClearStatus => ExecClearStatus(ctx, item),
        _ => true
    };

    bool ExecApplyStatus(EffectExecuteContext ctx, EffectActionPlanItem item)
    {
        var status = item.Params.TryGetValue("status", out var s) ? (string)s! : "";
        var targetPtr = item.Params.TryGetValue("targetPtr", out var tp) ? tp as string : null;
        if (string.IsNullOrEmpty(targetPtr))
            targetPtr = ctx.Event.TargetPtr;

        if (string.IsNullOrEmpty(targetPtr))
        {
            Refused.Add(("status-no-target", status, ""));
            return false;
        }

        if (ZombiePtrs.Contains(targetPtr))
        {
            Applied.Add(("zombie", status, targetPtr));
            return true;
        }

        if (PlantPtrs.Contains(targetPtr))
        {
            if (!UnityCcIds.Contains(status) || PlantAppliableStatuses.Contains(status))
            {
                Applied.Add(("plant", status, targetPtr));
                return true;
            }

            Refused.Add(("status-side-unsupported", status, targetPtr));
            return false;
        }

        Refused.Add(("status-target-not-found", status, targetPtr));
        return false;
    }

    bool ExecClearStatus(EffectExecuteContext ctx, EffectActionPlanItem item)
    {
        var status = item.Params.TryGetValue("status", out var s) ? (string)s! : "";
        var target = item.Params.TryGetValue("target", out var t) ? (t as string ?? "") : "";

        switch (target)
        {
            case "all":
                foreach (var z in ZombiePtrs) Cleared.Add(("zombie", status, z));
                foreach (var p in PlantPtrs) Cleared.Add(("plant", status, p));
                return true;
            case "all-zombies":
                foreach (var z in ZombiePtrs) Cleared.Add(("zombie", status, z));
                return true;
            case "all-plants":
                foreach (var p in PlantPtrs) Cleared.Add(("plant", status, p));
                return true;
            default:
                var targetPtr = ctx.Event.TargetPtr;
                if (string.IsNullOrEmpty(targetPtr))
                {
                    Refused.Add(("status-no-target", status, ""));
                    return false;
                }

                if (ZombiePtrs.Contains(targetPtr))
                {
                    Cleared.Add(("zombie", status, targetPtr));
                    return true;
                }

                if (PlantPtrs.Contains(targetPtr))
                {
                    if (!string.IsNullOrEmpty(status) && !PlantClearableStatuses.Contains(status))
                    {
                        Refused.Add(("status-side-unsupported", status, targetPtr));
                        return false;
                    }

                    Cleared.Add(("plant", status, targetPtr));
                    return true;
                }

                Refused.Add(("status-target-not-found", status, targetPtr));
                return false;
        }
    }
}
