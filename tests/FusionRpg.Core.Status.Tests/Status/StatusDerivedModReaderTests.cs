using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// mechanism-wiring G1's injector half (spec-mechanism-wiring.md §4.1) —
/// <see cref="CoreStatus.StatusDerivedModReader"/>, the Unity-free projection the injector's
/// `StatusDerivedMods.For` adapter delegates to, mirroring `GrantedDerivedAtomReaderTests`'s coverage
/// of the sibling bound-atom reader.
/// </summary>
public class StatusDerivedModReaderTests
{
    const string Defense = "combat.defense.omni";
    const string ResistDot = "status.resist.dot";

    static CoreStatus.StatusInstance Instance(string instanceId, params CoreStatus.StatusStatMod[] mods) =>
        new()
        {
            InstanceId = instanceId,
            StatusId = "rally",
            HostPtr = "Z1",
            GrantId = "g1",
            StatMods = mods,
        };

    [Fact]
    public void A_derived_channel_mod_projects_with_the_instance_as_SourceId()
    {
        var instance = Instance("g1:rally:1", new CoreStatus.StatusStatMod(Defense, "flat", 25));
        var mods = CoreStatus.StatusDerivedModReader.Read(new[] { instance });

        var mod = Assert.Single(mods);
        Assert.Equal(Defense, mod.Channel);
        Assert.Equal(DerivedModifierOp.Flat, mod.Op);
        Assert.Equal(25, mod.Amount);
        // SourceId names the INSTANCE, matching CoreStatus.StatusStatPayload.SourceIdOf exactly — the
        // same id the PRIMARY path withdraws by, so attribution agrees across both paths.
        Assert.Equal("status:g1:rally:1", mod.SourceId);
        Assert.Equal(CoreStatus.StatusStatPayload.SourceIdOf(instance), mod.SourceId);
    }

    [Fact]
    public void A_primary_channel_mod_never_projects()
    {
        // "atk" is one of the 23 PRIMARY channels -- already composed by StatSystem.Resolve via
        // StatusStatPayload.ToModifiers. Projecting it here too would double it.
        var instance = Instance("g1:rally:1", new CoreStatus.StatusStatMod("atk", "more", -0.1));
        Assert.Empty(CoreStatus.StatusDerivedModReader.Read(new[] { instance }));
    }

    [Theory]
    [InlineData("flat", true)]
    [InlineData("increased", true)]
    [InlineData("more", false)]
    public void More_is_skipped_never_coerced(string op, bool projects)
    {
        var instance = Instance("g1:rally:1", new CoreStatus.StatusStatMod(Defense, op, 10));
        var mods = CoreStatus.StatusDerivedModReader.Read(new[] { instance });
        Assert.Equal(projects, mods.Count == 1);
    }

    [Fact]
    public void Two_instances_of_the_same_status_project_two_independent_SourceIds()
    {
        var a = Instance("g1:ember:1", new CoreStatus.StatusStatMod(Defense, "flat", 10));
        var b = Instance("g2:ember:2", new CoreStatus.StatusStatMod(Defense, "flat", 10));
        var mods = CoreStatus.StatusDerivedModReader.Read(new[] { a, b });

        Assert.Equal(2, mods.Count);
        Assert.Equal(2, mods.Select(m => m.SourceId).Distinct().Count());
    }

    [Fact]
    public void An_instance_with_no_stat_mods_contributes_nothing()
    {
        var instance = Instance("g1:poison:1");
        Assert.Empty(CoreStatus.StatusDerivedModReader.Read(new[] { instance }));
    }

    [Fact]
    public void Null_or_empty_inputs_never_throw()
    {
        Assert.Empty(CoreStatus.StatusDerivedModReader.Read((IReadOnlyList<CoreStatus.StatusInstance>?)null));
        Assert.Empty(CoreStatus.StatusDerivedModReader.Read(Array.Empty<CoreStatus.StatusInstance>()));
        Assert.Empty(CoreStatus.StatusDerivedModReader.Read((CoreStatus.StatusRuntime?)null, ctx: null));
    }

    [Fact]
    public void A_context_with_no_entity_key_returns_empty_without_touching_the_runtime()
    {
        var runtime = new CoreStatus.StatusRuntime(
            CoreStatus.StatusCatalogBootstrap.CreateDefault(),
            (_, _) => ActorDerivedSnapshot.AttackerLess());
        var ctx = new StatContext { EntityKey = "" };
        Assert.Empty(CoreStatus.StatusDerivedModReader.Read(runtime, ctx));
    }

    [Fact]
    public void A_live_runtime_wired_through_the_reader_reaches_the_composed_value()
    {
        // End-to-end: the same shape StatusDerivedMods.For (injector) drives in production, minus the
        // Unity-specific field poke -- a real ActorHub composing a real StatusRuntime's live instances.
        CoreStatus.StatusRuntime? runtimeRef = null;
        var hub = ActorHubBootstrap.CreateDefault(
            statusDerivedMods: ctx => CoreStatus.StatusDerivedModReader.Read(runtimeRef, ctx));
        var runtime = new CoreStatus.StatusRuntime(
            CoreStatus.StatusCatalogBootstrap.CreateDefault(),
            (_, _) => ActorDerivedSnapshot.AttackerLess());
        runtimeRef = runtime;

        var now = DateTimeOffset.UtcNow;
        var input = new CoreStatus.StatusApplyInput(
            "rally", "Z1", null, "g1", BaseMagnitude: 0, BaseDuration: 5000,
            PeriodMs: 0, DurationMs: 5000, AttackerLess: true,
            StatMods: new[] { new CoreStatus.StatusStatMod(Defense, "flat", 25) });
        var outcome = runtime.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);
        Assert.True(outcome.Applied);

        var ctx = new StatContext { EntityKey = "Z1" };
        Assert.Equal(25, hub.ResolveDerived(ctx).Get(Defense));
    }
}
