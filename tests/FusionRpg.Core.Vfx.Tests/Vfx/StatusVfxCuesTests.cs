using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Vfx;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>SPEC W5: status applies emit status.{id}.apply cues; resists and ICD emit nothing.</summary>
public class StatusVfxCuesTests
{
    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(), (_, attackerLess) =>
            attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

    static (CoreStatus.StatusRuntime Rt, RecordingVfxSink Sink) Wired()
    {
        var rt = Runtime();
        var sink = new RecordingVfxSink();
        rt.OnApplied = inst => sink.Play(StatusVfxCues.Cue(inst));
        return (rt, sink);
    }

    [Fact]
    public void Cue_id_shape_is_locked()
    {
        Assert.Equal("status.wither.apply", StatusVfxCues.CueId("wither"));
        Assert.Equal("status.charm_pulse.apply", StatusVfxCues.CueId("Charm_Pulse"));
    }

    [Fact]
    public void Successful_apply_emits_one_ptr_anchored_cue()
    {
        var (rt, sink) = Wired();
        rt.Apply(
            new CoreStatus.StatusApplyInput("wither", "Z1", "P1", "g1", 20, 5000),
            new CoreStatus.FixedStatusRng(0.0),
            DateTimeOffset.UtcNow);
        var cue = Assert.Single(sink.Items);
        Assert.Equal("status.wither.apply", cue.CueId);
        Assert.Equal("Z1", cue.TargetPtr);
        Assert.Null(cue.Col);
    }

    [Fact]
    public void Resisted_apply_emits_nothing()
    {
        var (rt, sink) = Wired();
        rt.OnResisted = _ => { };
        rt.Apply(
            new CoreStatus.StatusApplyInput("wither", "Z1", "P1", "g1", 20, 5000),
            new CoreStatus.FixedStatusRng(1.0),
            DateTimeOffset.UtcNow);
        Assert.Empty(sink.Items);
        Assert.Single(rt.ResistedEvents);
    }

    [Fact]
    public void Spread_hop_emits_a_cue_for_the_new_host()
    {
        var (rt, sink) = Wired();
        var now = DateTimeOffset.UtcNow;
        var input = new CoreStatus.StatusApplyInput(
            "blight", "Z1", "P1", "g-blight", -12, 5000,
            SpreadChance: 1.0, SpreadStatusId: "blight", SpreadMaxHops: 2);
        rt.Apply(input, new CoreStatus.FixedStatusRng(0.0), now);
        var inst = rt.ForHost("Z1").Single();
        var template = input with { HostPtr = "", HopDepth = 1 };
        CoreStatus.StatusSpread.Execute(
            rt,
            new CoreStatus.StatusSpreadRequest(inst, new[] { "Z2" }, 1, 1.0, template),
            new CoreStatus.FixedStatusRng(0.0),
            now);
        Assert.Equal(2, sink.Items.Count);
        Assert.Contains(sink.Items, c => c.TargetPtr == "Z1");
        Assert.Contains(sink.Items, c => c.TargetPtr == "Z2");
    }

    [Fact]
    public void Every_catalog_status_has_a_seeded_apply_recipe()
    {
        // `nerve.*` (delve-attrition D2.19, 21 -> 24) is excluded from THIS loop's stricter checks
        // (below, custom vs. engine-wrapped sustained-composition classification) because no one has
        // authored a sustained composition or marker for it yet -- that classification decision still
        // belongs to the delve-attrition stream. It DOES now have a real transient apply cue, fixed
        // 2026-09-20 (combat-math-dedup D17, backlog-clean-up BCU8.1) -- see
        // StatusFxCategoryClosureTests for that coverage, which this loop does not duplicate.
        var statusIds = CoreStatus.StatusCatalogBootstrap.CreateDefault().All()
            .Select(d => d.StatusId)
            .Where(id => !id.StartsWith("nerve.", StringComparison.Ordinal))
            .ToList();
        // The count (21 today) is fully implied by the bootstrap total minus nerve.* -- a formula, not
        // a literal (population-pin SE3.3, 2026-09-19). The per-id loop below is the real coverage
        // check; this only proves the filter is not vacuous.
        Assert.NotEmpty(statusIds);
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());
        foreach (var id in statusIds)
        {
            Assert.True(catalog.TryGet(StatusVfxCues.CueId(id), out var recipe), id);
            // transient specs need a life; sustained specs live apply→expire
            Assert.All(recipe.Primitives, p => Assert.True(p.IsSustained || p.LifeSeconds > 0f));
        }

        // vfx-v3: every one of the 13 CUSTOM statuses has a sustained composition;
        // the 8 engine-wrapped vanilla statuses have NONE (original visuals untouched).
        var custom = new[]
        {
            "wither", "blight", "rot", "spark", "spore", "pact_mark", "leech",
            "expose", "shatter", "bond", "rally", "command", "charm_pulse"
        };
        var engineWrapped = new[] { "butter", "freeze", "cold", "poison", "hypno", "ember", "jala", "kelp" };
        foreach (var id in custom)
        {
            Assert.True(catalog.TryGet(StatusVfxCues.CueId(id), out var r), id);
            Assert.True(r.HasSustained, id + " must have a sustained composition");
        }

        foreach (var id in engineWrapped)
        {
            Assert.True(catalog.TryGet(StatusVfxCues.CueId(id), out var r), id);
            Assert.False(r.HasSustained, id + " is vanilla-visualized — no sustained set allowed");
        }

        // markers only on react-to states (SPEC §4 grammar)
        var markerIds = custom.Where(id =>
        {
            catalog.TryGet(StatusVfxCues.CueId(id), out var r);
            return r.HasMarker;
        }).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "bond", "command", "expose", "pact_mark" }, markerIds);

        Assert.Contains(
            VfxSeedCatalog.StatusSustainFx.First(s => s.Id == "wither").Aura,
            new VfxAuraStyle?[] { VfxAuraStyle.WispOut });
        Assert.Equal(VfxAuraStyle.SporeDrift,
            VfxSeedCatalog.StatusSustainFx.First(s => s.Id == "spore").Aura);
        Assert.Equal(VfxAuraStyle.CharmHeartbeat,
            VfxSeedCatalog.StatusSustainFx.First(s => s.Id == "charm_pulse").Aura);
        Assert.Equal(VfxAuraStyle.Orbit,
            VfxSeedCatalog.StatusSustainFx.First(s => s.Id == "bond").Aura);
        Assert.Equal(VfxAuraStyle.PactFootPulse,
            VfxSeedCatalog.StatusSustainFx.First(s => s.Id == "pact_mark").Aura);
        Assert.Equal(VfxAuraStyle.CommandCrownPulse,
            VfxSeedCatalog.StatusSustainFx.First(s => s.Id == "command").Aura);

        // Same fact as VfxRulesAndCatalogTests.Seed_catalog_contains_v1_roster_and_validates (pinned
        // there, VfxSeedCatalog.CreateAll) -- never a second literal for the same catalog construction
        // (population-pin SE3.3, 2026-09-19).
    }
}
