using System.Linq;
using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// passive-tree-todo.md I2 — GET/POST /api/passive-tree against a REAL, minimal in-process host (same
/// pattern as <c>AptitudeEndpointsTests</c>/<c>AptitudesInjectorBroadcastTests</c>, not a mock). Proves
/// the shipped endpoint end to end: the resolve report is assembled from the store and catalog (not a
/// pass-through), a POST is one whole allocation (never a per-node call, and it fully replaces the
/// prior owned set), and the allocation-changed broadcast reaches both SignalR groups.
/// </summary>
[Trait("VerificationId", "server.passive-tree")]
public class PassiveTreeEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    string _baseUrl = "";
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        PassiveTreeTuningHub.Configure(Tuning());
        // The skill-points wallet (task I3) reads `powerIndex.ActorIndex` (theta) and
        // `AptitudeTuningHub.Tuning.PointEconomy.SkillPointsPerThetaMilliByScope[Commander]` --
        // both hubs need a real config, same as `AptitudeEndpointsTests`.
        PowerTuningHub.Configure(PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));

        // Two Force-posture Primary trees (to exercise D28 cross-unlock lending) plus a genuinely
        // unrecognized gate-quantity shape (D37 -- the exact "unproduced" state §9.1 describes) plus
        // one real Elemental and one real Status tree, each with the REAL wire-format gate quantity
        // (spec-tree-plan.md: `element_mastery.<id>@Aspect`, `status_applied.<id>` -- D35's own rule,
        // no @Scope suffix on the status shape) -- J1's own acceptance bullet: these two must resolve
        // `wired`, not `unproduced`, now that IsWiredGateQuantity recognizes their shapes.
        // ImportTreeCatalog is the ALREADY-SHIPPED, already-tested import path (task C4), never
        // re-derived here.
        var outcome = _store.ImportTreeCatalog(
            new[] { TreeJson("might", "primary", "aptitude.Might@Commander"),
                    TreeJson("fortitude", "primary", "aptitude.Fortitude@Commander"),
                    TreeJson("fire", "elemental", "element_mastery.fire@Aspect"),
                    TreeJson("blight", "status", "status_applied.blight"),
                    TreeJson("ashfall", "elemental", "element_mastery.notarealelement@Aspect") },
            Tuning());
        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        // RpgHub's own constructor dependencies (RpgHub.cs:17) -- a hub activation failure closes the
        // connection before an invoked method ever runs, which is what an unregistered dependency here
        // looks like from the client side ("InvokeCoreAsync ... connection is not active").
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>(); // RpgHub ctor dependency (NS1.6)
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapPassiveTree();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Get_unknownPlayer_returns404()
    {
        var resp = await _http.GetAsync("/api/passive-tree/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_onAFreshPlayer_returnsEveryTreeAtTierZero_speciesNeverAppears()
    {
        var body = await GetState();
        Assert.Equal(5, body.Trees.Count); // "might", "fortitude", "fire", "blight", "ashfall" -- never a species tree (none imported, but the filter itself is asserted by I5's own module; this proves the shared-only round-trip)
        Assert.All(body.Trees, t => Assert.Equal(0, t.TierReached));
        Assert.All(body.Trees, t => Assert.Equal(0, t.AptitudePoints));
        Assert.Empty(body.SoulLevelByNodeId);
        // Skill points (task I3, §4.1's third currency) -- a fresh player owns zero nodes, so the
        // rising D25 ladder has spent nothing yet and the whole budget is available.
        Assert.Equal(0, body.SkillPointsSpent);
        Assert.Equal(body.SkillPointsBudget, body.SkillPointsAvailable);
    }

    [Fact]
    public async Task Post_owningNodes_spendsSkillPoints_perTreeUnlockCosts_notAptitudePoints()
    {
        // TreeUnlockCost.Cumulative(2, first=5, step=2) = 2*5 + 2*2*1/2 = 12 -- proves this endpoint's
        // skill-points wallet is wired to the SAME pure function `TreeUnlockCost` already ships and
        // tests, not a private re-derivation (AGENTS.md's "one power ladder" rule, same spirit).
        await Post("/api/passive-tree/allocate",
            new
            {
                playerId = _playerId,
                nodes = new Dictionary<string, long>
                {
                    ["skill.might-off-t1-n0"] = 0,
                    ["skill.fortitude-off-t1-n0"] = 0
                }
            });

        var body = await GetState();
        Assert.Equal(12, body.SkillPointsSpent);
        Assert.Equal(body.SkillPointsBudget - 12, body.SkillPointsAvailable);
        // Owning nodes never touches the aptitude-points wallet -- the two currencies stay separate
        // wallets (§4.1's whole point: never share the bare word "points").
        Assert.All(body.Trees, t => Assert.Equal(0, t.AptitudePoints));
    }

    [Fact]
    public async Task Get_aTreeWithNoProducerYet_resolvesUnproduced_aWiredOneDoesNot()
    {
        var body = await GetState();
        var ashfall = body.Trees.Single(t => t.TreeId == "ashfall");
        var might = body.Trees.Single(t => t.TreeId == "might");
        Assert.Equal("unproduced", ashfall.GateState);
        Assert.Equal("wired", might.GateState);
    }

    [Fact]
    public async Task Get_realElementalAndStatusGateQuantities_resolveWired_J1sOwnAcceptanceBullet()
    {
        // J1 (passive-tree-todo.md): ElementMasterySource/StatusAppliedSource have been real, live-probed
        // producers since task G6 (2026-09-06), but PassiveTreeEndpoints' own GateState check was never
        // taught to recognize either wire shape -- fixed 2026-09-07 via IsWiredGateQuantity. Before that
        // fix, both trees below would have wrongly reported "unproduced" despite having a real producer.
        var body = await GetState();
        var fire = body.Trees.Single(t => t.TreeId == "fire");
        var blight = body.Trees.Single(t => t.TreeId == "blight");
        Assert.Equal("wired", fire.GateState);
        Assert.Equal("wired", blight.GateState);
    }

    [Fact]
    public async Task Allocating_aptitude_points_opens_the_tier_and_crossUnlock_lends_between_forcePostureMates()
    {
        // Seeded directly through the store -- AptitudeEndpoints' own budget check is a different
        // module's own concern (point-economy), not this endpoint's; this test only needs the
        // ALLOCATION to exist, however it got there. reqScalePoints=5: tier t opens at
        // 5*t*(t+1)/2 -- t=1:5, t=2:15, t=3:30.
        SeedAptitudes(("Might", 10), ("Fortitude", 20));

        var body = await GetState();
        var might = body.Trees.Single(t => t.TreeId == "might");
        var fortitude = body.Trees.Single(t => t.TreeId == "fortitude");

        // gate(might) = base(10) + credit(max other Force base = fortitude's 20) = 30 -> tier 3
        Assert.Equal(30, might.AptitudePoints);
        Assert.Equal("fortitude", might.LenderTreeId);
        Assert.Equal(3, might.TierReached);

        // gate(fortitude) = base(20) + credit(might's 10) = 30 -> tier 3, same lender rule, one name
        Assert.Equal(30, fortitude.AptitudePoints);
        Assert.Equal("might", fortitude.LenderTreeId);
        Assert.Equal(3, fortitude.TierReached);
    }

    [Fact]
    public async Task Get_carries_unlockCostRates_soAClientCanMirrorCumulative_forAHypotheticalCount()
    {
        // Task I8 (spec-tree-surface.md §5.2) -- the (first, step) pair itself, not just the two
        // already-wired totals, so a Plan preview can reproduce TreeUnlockCost.Cumulative for a
        // hypothetical owned count client-side. Tuning() above: UnlockCost(5, 2).
        var body = await GetState();
        Assert.Equal(5, body.UnlockCostFirstPoints);
        Assert.Equal(2, body.UnlockCostStepPoints);
    }

    [Fact]
    public async Task Get_carries_concentrationTuning_soAClientCanMirrorHF_forADraftPreview()
    {
        // Task I9 (spec-tree-surface.md §6) -- the real tuning dial values, not just the two
        // already-wired committed H/F numbers, so a client-side draft preview can reproduce
        // Concentration.HerfindahlMilli/BlendMilli/FmaxAppliedMilli exactly for a hypothetical
        // allocation. Tuning() above: Concentration(FmaxMilli: 1200, WMilli: 500).
        var body = await GetState();
        Assert.Equal(1200, body.ConcentrationFmaxMilli);
        Assert.Equal(500, body.ConcentrationWMilli);
    }

    [Fact]
    public async Task Get_ownAptitudePoints_isTheTreesOwnBase_neverIncludingALenderCredit()
    {
        // Task I8 (spec-tree-surface.md §7.2 part 2) -- the positive attribution's own split.
        // Reuses the exact D28 lending fixture `Allocating_aptitude_points_opens_the_tier_...` above:
        // might base=10, fortitude base=20, each lends its OWN base to the other (max, never a sum).
        SeedAptitudes(("Might", 10), ("Fortitude", 20));

        var body = await GetState();
        var might = body.Trees.Single(t => t.TreeId == "might");
        var fortitude = body.Trees.Single(t => t.TreeId == "fortitude");

        Assert.Equal(10, might.OwnAptitudePoints);
        Assert.Equal(20, fortitude.OwnAptitudePoints);
        // AptitudePoints (base + credit) minus OwnAptitudePoints (base alone) recovers exactly the
        // credited amount, which must equal the LENDER's own base -- proving the split is real, not
        // a guess: might.AptitudePoints(30) - might.OwnAptitudePoints(10) = 20 = fortitude's own base.
        Assert.Equal(fortitude.OwnAptitudePoints, might.AptitudePoints - might.OwnAptitudePoints);
        Assert.Equal(might.OwnAptitudePoints, fortitude.AptitudePoints - fortitude.OwnAptitudePoints);
    }

    [Fact]
    public async Task Post_isOneWholeAllocation_ownedNodeReplaces_thePriorSet_neverMerges()
    {
        await Post("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 2 } });
        var afterFirst = await GetState();
        Assert.Equal(2, afterFirst.SoulLevelByNodeId["skill.might-off-t1-n0"]);

        // A second POST naming a DIFFERENT node set replaces the first wholesale -- SaveTreeNodeState's
        // own full delete-then-insert contract, exercised through the endpoint rather than assumed.
        await Post("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.fortitude-off-t1-n0"] = 0 } });
        var afterSecond = await GetState();
        Assert.False(afterSecond.SoulLevelByNodeId.ContainsKey("skill.might-off-t1-n0"));
        Assert.True(afterSecond.SoulLevelByNodeId.ContainsKey("skill.fortitude-off-t1-n0"));
    }

    [Fact]
    public async Task Post_anOwnedTierOneNodeAfterOpeningTierOne_contributes()
    {
        SeedAptitudes(("Might", 10));
        await Post("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 } });

        var body = await GetState();
        var might = body.Trees.Single(t => t.TreeId == "might");
        Assert.Contains("skill.might-off-t1-n0", might.ContributingNodeIds);
    }

    [Fact]
    public async Task Get_carries_reqScalePoints_and_every_enabled_nodes_structural_slot()
    {
        // I6 (spec-tree-surface.md §2.3/§9): the lattice needs real (branch, tier, nodeId) identity
        // for cells nobody owns yet, which TreeResolveReportDto's owned-only node lists cannot
        // describe -- and the ONE global ladder constant the client's own distance math reads rather
        // than re-deriving (AGENTS.md's "one power ladder" rule).
        var body = await GetState();
        Assert.Equal(5, body.TierReqScalePoints); // Tuning() above: ReqScalePoints: 5

        var might = body.Trees.Single(t => t.TreeId == "might");
        var node = Assert.Single(might.Nodes);
        Assert.Equal("skill.might-off-t1-n0", node.NodeId);
        Assert.Equal("Off", node.Branch);
        Assert.Equal(1, node.Tier);
        Assert.Equal("Magnitude", node.NodeClass);
    }

    [Fact]
    public async Task Post_negativeSoulLevel_returns400()
    {
        var resp = await _http.PostAsJsonAsync("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = -1 } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_missingNodes_returns400()
    {
        var resp = await _http.PostAsJsonAsync("/api/passive-tree/allocate", new { playerId = _playerId });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_unknownPlayer_returns404()
    {
        var resp = await _http.PostAsJsonAsync("/api/passive-tree/allocate",
            new { playerId = 999999L, nodes = new Dictionary<string, long>() });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- I8's preview endpoint (spec-tree-surface.md §7.2 part 5) -------------------------------

    [Fact]
    public async Task Preview_unknownPlayer_returns404()
    {
        var resp = await _http.PostAsJsonAsync("/api/passive-tree/999999/preview",
            new { nodes = new Dictionary<string, long>() });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Preview_missingNodes_returns400()
    {
        var resp = await _http.PostAsJsonAsync($"/api/passive-tree/{_playerId}/preview", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Preview_negativeSoulLevel_returns400()
    {
        var resp = await _http.PostAsJsonAsync($"/api/passive-tree/{_playerId}/preview",
            new { nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = -1 } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Preview_unknownAptitudeId_returns400()
    {
        var resp = await _http.PostAsJsonAsync($"/api/passive-tree/{_playerId}/preview",
            new { nodes = new Dictionary<string, long>(), aptitudeDelta = new Dictionary<string, long> { ["NotARealAptitude"] = 5 } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Preview_aDeltaThatWouldGoNegative_isRefused_neverSilentlyClamped()
    {
        SeedAptitudes(("Might", 10));
        var resp = await _http.PostAsJsonAsync($"/api/passive-tree/{_playerId}/preview",
            new { nodes = new Dictionary<string, long>(), aptitudeDelta = new Dictionary<string, long> { ["Might"] = -11 } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // -10 lands exactly on zero -- legal, not a boundary-off-by-one refusal.
        var ok = await _http.PostAsJsonAsync($"/api/passive-tree/{_playerId}/preview",
            new { nodes = new Dictionary<string, long>(), aptitudeDelta = new Dictionary<string, long> { ["Might"] = -10 } });
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preview_neverPersists_aRepeatGetAfterPreview_seesOnlyTheOriginalCommittedState()
    {
        // Committed: nothing owned, Might at 10 aptitude points.
        SeedAptitudes(("Might", 10));
        await Post("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 } });
        var before = await GetState();

        // Preview a hypothetical: a NEW owned node plus a big Might increase.
        var previewResp = await _http.PostAsJsonAsync($"/api/passive-tree/{_playerId}/preview",
            new
            {
                nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0, ["skill.fortitude-off-t1-n0"] = 0 },
                aptitudeDelta = new Dictionary<string, long> { ["Might"] = 20 }
            });
        Assert.True(previewResp.IsSuccessStatusCode, await previewResp.Content.ReadAsStringAsync());
        var preview = await previewResp.Content.ReadFromJsonAsync<PassiveTreeStateDto>();
        Assert.NotNull(preview);

        // The hypothetical actually took effect IN THE RESPONSE (gate(might) = 10+20 = 30 -> tier 3
        // at reqScalePoints=5; owning fortitude's node too).
        Assert.Equal(30, preview!.Trees.Single(t => t.TreeId == "might").AptitudePoints);
        Assert.Equal(3, preview.Trees.Single(t => t.TreeId == "might").TierReached);
        Assert.True(preview.SoulLevelByNodeId.ContainsKey("skill.fortitude-off-t1-n0"));

        // But the COMMITTED state (a fresh GET) is completely unchanged -- neither the node nor the
        // aptitude delta was ever written.
        var after = await GetState();
        Assert.Equal(before.SoulLevelByNodeId.Keys.OrderBy(k => k), after.SoulLevelByNodeId.Keys.OrderBy(k => k));
        Assert.False(after.SoulLevelByNodeId.ContainsKey("skill.fortitude-off-t1-n0"));
        Assert.Equal(10, after.Trees.Single(t => t.TreeId == "might").AptitudePoints);
        Assert.Equal(1, after.Trees.Single(t => t.TreeId == "might").TierReached);
    }

    [Fact]
    public async Task Preview_movingPointsOutOfOneForcePosturePath_closesTheOtherMatesTier_bothSeeTheSameLoweredGate()
    {
        // D28's own worked scenario (spec-tree-surface.md §7.2 part 5's example sentence): both Might
        // and Fortitude sit at tier 3 today because each lends the other 20 (reqScalePoints=5: tier 3
        // opens at 30). Pulling Fortitude's OWN base down removes the credit Might was reading, which
        // closes Might's tier 3 in the SAME response -- proving the preview recomputes CrossUnlock's
        // cross-tree lending, not just the one tree the delta named.
        SeedAptitudes(("Might", 20), ("Fortitude", 20));
        var committed = await GetState();
        Assert.Equal(3, committed.Trees.Single(t => t.TreeId == "might").TierReached);

        var previewResp = await _http.PostAsJsonAsync($"/api/passive-tree/{_playerId}/preview",
            new
            {
                nodes = new Dictionary<string, long>(),
                aptitudeDelta = new Dictionary<string, long> { ["Fortitude"] = -20 }
            });
        Assert.True(previewResp.IsSuccessStatusCode, await previewResp.Content.ReadAsStringAsync());
        var preview = await previewResp.Content.ReadFromJsonAsync<PassiveTreeStateDto>();
        Assert.NotNull(preview);

        // Fortitude's own base is now 0 -- gate(might) = base(20) + credit(max(0)) = 20 -> tier 2, not 3.
        var mightPreview = preview!.Trees.Single(t => t.TreeId == "might");
        Assert.Equal(20, mightPreview.AptitudePoints);
        Assert.Equal(2, mightPreview.TierReached);

        var fortitudePreview = preview.Trees.Single(t => t.TreeId == "fortitude");
        Assert.Equal(0, fortitudePreview.OwnAptitudePoints);
    }

    // ---- broadcast: the exact AptitudeEndpoints.cs:115-117 mechanism, copied -------------------

    [Fact]
    public async Task Allocate_notifies_a_client_joined_as_injector_not_just_web()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("PassiveTreeUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        await Post("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 } });

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "an injector-group connection never received PassiveTreeUpdated");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Allocate_still_notifies_a_client_joined_as_web()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("PassiveTreeUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);

        await Post("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 } });

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "regression: the web-group notification broke");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Get_boundAtoms_unknownPlayer_returns404()
    {
        var resp = await _http.GetAsync("/api/passive-tree/bound-atoms/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_boundAtoms_onAFreshPlayer_isEmptyArray_notAnError()
    {
        var resp = await _http.GetAsync($"/api/passive-tree/bound-atoms/{_playerId}");
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var wire = await resp.Content.ReadFromJsonAsync<List<BoundDerivedAtomDto>>();
        Assert.NotNull(wire);
        Assert.Empty(wire!);
    }

    [Fact]
    public async Task Get_boundAtoms_matches_TreeBoundAtomsForPlayer_calledDirectly()
    {
        // lawn-tree-hydrate (T13) -- the Injector has no store, so this HTTP round trip IS its only
        // path to these atoms; this proves the wire response is a lossless mirror of the same
        // TreeBoundAtoms.ForPlayer the Server sheet itself already fans into Hub (UniqueActorHubCompose),
        // not a second, drifting projection. Re-imports the "might" tree with a real stat.derived atom
        // (the shared TreeJson() fixture's own node atom is deliberately "stat.modify" -- an ignored
        // kind on this projection, per TreeAtomSource.BoundAtomsFor's own "magnitude-class only" gate --
        // so it alone would prove nothing here).
        var outcome = _store.ImportTreeCatalog(new[] { DerivedTreeJson("might", "aptitude.Might@Commander") }, Tuning());
        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));

        SeedAptitudes(("Might", 30)); // opens tier 3 solo (reqScalePoints=5: t3 = 5*3*4/2 = 30)
        await Post("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, long> { ["skill.might-off-t1-n0"] = 0 } });

        var resp = await _http.GetAsync($"/api/passive-tree/bound-atoms/{_playerId}");
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var wire = await resp.Content.ReadFromJsonAsync<List<BoundDerivedAtomDto>>();
        Assert.NotNull(wire);
        Assert.NotEmpty(wire!);

        var powerIndex = new FusionRpg.Server.Power.ServerPowerIndexProvider(_store, PowerTuningHub.Tuning);
        var direct = FusionRpg.Server.TreeBoundAtoms.ForPlayer(_store, powerIndex, _playerId);
        Assert.Equal(direct.Count, wire!.Count);
        foreach (var atom in direct)
        {
            Assert.Contains(wire, w =>
                w.Channel == atom.Channel
                && w.Op == atom.Op.ToString()
                && Math.Abs(w.Amount - atom.Amount) < 0.0001
                && w.SourceId == atom.SourceId);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    async Task<PassiveTreeStateDto> GetState()
    {
        var resp = await _http.GetAsync($"/api/passive-tree/{_playerId}");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<PassiveTreeStateDto>();
        Assert.NotNull(body);
        return body!;
    }

    async Task Post(string url, object payload)
    {
        var resp = await _http.PostAsJsonAsync(url, payload);
        if (!resp.IsSuccessStatusCode) throw new Exception($"{url} -> {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    /// <summary>Writes a Commander-scope aptitude allocation directly through the store, bypassing
    /// `/api/aptitudes/allocate`'s own budget refusal -- that endpoint's budget math (point-economy,
    /// theta-derived) is a different module's concern, and coupling this endpoint's own tests to it
    /// would make them fail on an unrelated tuning change. This endpoint only ever READS the
    /// allocation `AptitudeEndpoints.ScopeKey` addresses (the same convention `PassiveTreeEndpoints`
    /// itself uses), so seeding it directly is a legitimate substitute for the real POST, not a
    /// workaround around a real refusal.</summary>
    void SeedAptitudes(params (string AptitudeId, long Points)[] shares)
    {
        var allocation = shares.Aggregate(AptitudeAllocation.Empty,
            (acc, s) => acc + AptitudeAllocation.Single(AllocationScope.Commander, s.AptitudeId, s.Points));
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(_playerId), allocation);
    }

    static PassiveTreeTuning Tuning() => new(
        SchemaVersion: 1, Version: 1,
        TierLadder: new TierLadderTuning(ReqScalePoints: 5),
        Budget: new BudgetTuning(1000, 500),
        TreeShareMilli: 1000, TreeBudgetMilli: 1000,
        Potency: new PotencyTuning(182, 1, new long[] { 46, 91, 137, 182 }),
        Mechanism: new MechanismTuning(0, 1000),
        Archetype: new ArchetypeTuning(6000),
        Exclusion: new ExclusionTuning(20),
        ArchetypeAssignment: "ordinal-round-robin",
        DesignTarget: new DesignTargetTuning(92),
        Concentration: new ConcentrationTuning(FmaxMilli: 1200, WMilli: 500),
        SoulTrack: new SoulTrackTuning(1000),
        UnlockCost: new UnlockCostTuning(5, 2),
        Respec: new RespecTuning(50, 500),
        GateCounters: new GateCountersTuning(23, 23, 4, 4, 5000, null));

    static string TreeJson(string treeId, string category, string gateQuantity) => $$"""
    {
      "treeId": "{{treeId}}",
      "category": "{{category}}",
      "gateQuantity": "{{gateQuantity}}",
      "shapeArchetype": "broad-and-flat",
      "tiers": 10,
      "branches": 2,
      "nodesPerTier": [2,2,2,2,2,2,2,2,2,2],
      "catalogVersion": 1,
      "enabled": true,
      "nodes": [
        {
          "id": "skill.{{treeId}}-off-t1-n0",
          "branch": "off",
          "tier": 1,
          "nodeKey": "n0",
          "prereqNodeIds": [],
          "nodeClass": "magnitude",
          "affixIds": ["affix.a"],
          "budgetShareMilli": 18,
          "atoms": [
            {
              "kindId": "stat.modify",
              "attachPoint": "Stat",
              "channelId": "atk",
              "op": "flat",
              "trigger": null,
              "whenJson": null,
              "kMicro": 12345,
              "scaleAxis": "PTheta",
              "unitClass": "GameUnits",
              "soulCurveId": null
            }
          ],
          "excludeProps": [],
          "exclusionForm": "None",
          "tagsJson": null,
          "enabled": true,
          "retiredAtRevision": null
        }
      ]
    }
    """;

    /// <summary>Same shape as <see cref="TreeJson"/> but with a real `stat.derived` node atom instead
    /// of `stat.modify` -- the ONE difference `TreeAtomSource.BoundAtomsFor`'s "magnitude-class only"
    /// gate actually cares about (T13's own bound-atoms endpoint test).</summary>
    static string DerivedTreeJson(string treeId, string gateQuantity) => $$"""
    {
      "treeId": "{{treeId}}",
      "category": "primary",
      "gateQuantity": "{{gateQuantity}}",
      "shapeArchetype": "broad-and-flat",
      "tiers": 10,
      "branches": 2,
      "nodesPerTier": [2,2,2,2,2,2,2,2,2,2],
      "catalogVersion": 1,
      "enabled": true,
      "nodes": [
        {
          "id": "skill.{{treeId}}-off-t1-n0",
          "branch": "off",
          "tier": 1,
          "nodeKey": "n0",
          "prereqNodeIds": [],
          "nodeClass": "magnitude",
          "affixIds": ["affix.a"],
          "budgetShareMilli": 18,
          "atoms": [
            {
              "kindId": "stat.derived",
              "attachPoint": "Stat",
              "channelId": "combat.power.fire",
              "op": "flat",
              "trigger": null,
              "whenJson": null,
              "kMicro": 12345,
              "scaleAxis": "PTheta",
              "unitClass": "GameUnits",
              "soulCurveId": null
            }
          ],
          "excludeProps": [],
          "exclusionForm": "None",
          "tagsJson": null,
          "enabled": true,
          "retiredAtRevision": null
        }
      ]
    }
    """;

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string RepoTuningDir() => Path.Combine(FindRepoRoot(), "data", "tuning");

    static string LatestAptitudesPath()
    {
        var dir = RepoTuningDir();
        var best = Directory.EnumerateFiles(dir, "aptitudes.v*.json")
            .Select(Path.GetFileName)
            .Select(n => (Name: n!, Match: System.Text.RegularExpressions.Regex.Match(n!, @"^aptitudes\.v(\d+)\.json$")))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .First();
        return Path.Combine(dir, best.Name);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
