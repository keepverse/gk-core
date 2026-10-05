using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>spec-aptitude-allocation-surface.md — GET/POST /api/aptitudes against a REAL, minimal
/// in-process host (same pattern as RealRunCollectorTests.cs), not a mock. Proves the shipped endpoint
/// end to end: budget refusal never clamps (PS-8), an unknown aptitude id 400s, and a successful POST
/// actually round-trips through GET.</summary>
public class AptitudeEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        FusionRpg.Core.Power.PowerTuningHub.Configure(
            FusionRpg.Core.Power.PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Configure(
            FusionRpg.Core.Stats.Aptitudes.AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
        // EP1.9 -- TryReallocate's gate (RpgStore.AllocationRespec.cs) reads SpeciesBuildTuningHub.Tuning.UniqueRespec
        // for both the priced path and GetAllocationRespecCount's decay math, so every route this file exercises now
        // needs it configured, not only the species-respec tests.
        FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningHub.Configure(new FusionRpg.Core.Creatures.Generation.SpeciesBuildTuning(
            SchemaVersion: 1, Version: 1,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 633, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: FusionRpg.Core.Creatures.Generation.LeanSignalWeights.Zero));
        // EP1.14 -- EffectiveUniqueAllocation's ladder rung reads AptitudePresetTuningHub.Tuning
        // unconditionally once a specimen has no explicit allocation, so every unique-scope route this
        // file exercises now needs it configured deterministically here, rather than depending on
        // whichever OTHER test class in the same filtered run happened to configure the same
        // process-wide static hub first (a real, pre-existing cross-class hazard this task's own
        // assertions would otherwise silently ride on).
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(
            1, 1, SoftMaxPresets: 32, DefaultRowAbsMax: 1000,
            AssignLadder: new AssignLadderTuning(new[]
            {
                AptitudeAutoAssignRules.ActivePreset, AptitudeAutoAssignRules.SpeciesFavour,
                AssignLadder.PostureRung, AptitudeAutoAssignRules.Even
            })));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapAptitudes();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Get_onAFreshPlayer_returnsAllTwelveIdsAtZero()
    {
        var resp = await _http.GetAsync($"/api/aptitudes/{_playerId}");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.NotNull(body);
        // Reads AptitudeCatalog.Count live rather than a second literal (population-pin SE3.4,
        // 2026-09-20); pinned once, AptitudeCatalogTests.
        Assert.Equal(AptitudeCatalog.Count, body!.Shares.Count);
        Assert.All(body.Shares.Values, v => Assert.Equal(0, v));
        Assert.True(body.WithinBudget);
        Assert.Equal(0, body.Spent);
    }

    [Fact]
    public async Task Get_unknownPlayer_returns404()
    {
        var resp = await _http.GetAsync("/api/aptitudes/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_withinBudget_savesAndRoundTripsThroughGet()
    {
        var getBefore = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.NotNull(getBefore);
        var affordable = getBefore!.Budget; // spend exactly the whole budget on Might -- within budget by construction
        Assert.True(affordable > 0, "expected a nonzero commander budget on the shipped tuning");

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = affordable } });
        postResp.EnsureSuccessStatusCode();
        var postBody = await postResp.Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.NotNull(postBody);
        Assert.Equal(affordable, postBody!.Shares["Might"]);
        Assert.True(postBody.WithinBudget);

        var getAfter = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(affordable, getAfter!.Shares["Might"]);
    }

    [Fact]
    public async Task Post_overBudget_refusesAndDoesNotSave_neverClamps()
    {
        var before = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        var tooMuch = before!.Budget + 1;

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = tooMuch } });
        Assert.Equal(HttpStatusCode.Conflict, postResp.StatusCode);

        // PS-8: refused, never silently clamped to the budget -- re-GET must show the allocation
        // UNCHANGED from before the attempt, not truncated to the max legal value.
        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(before.Shares["Might"], after!.Shares["Might"]);
    }

    [Fact]
    public async Task Post_unknownAptitudeId_returns400()
    {
        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["NotARealAptitude"] = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, postResp.StatusCode);
    }

    // ---- the empire write path — POST /allocate?empire=, through the Tier-A seam ---------------

    /// <summary>
    /// THE silent-drop regression. Before the request type carried an <c>empire</c> property,
    /// System.Text.Json discarded this body field without complaint and the route answered 200 —
    /// having written the HUMAN pool for a caller that asked for an empire's. The read-back is
    /// <c>GET /api/aptitudes/{playerId}</c>'s own <c>commanderByEmpire</c> map: the ordinary read path
    /// a real client uses, never the write route's own response.
    /// </summary>
    [Fact]
    public async Task Post_allocate_naming_anEmpire_writesThatEmpiresPool_andReadsItBackThroughGet()
    {
        Assert.Contains("zomboss", _store.EmpiresOf(_playerId).Select(e => e.Empire.Value));
        // Zomboss's OWN commander budget, not the player's: a pool is sized by the empire that runs it.
        // Spending exactly that budget is within budget by construction -- no literal is pinned here.
        var affordable = ZombossCommanderBudget();
        Assert.True(affordable > 1, "expected a nonzero Zomboss commander budget");

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, empire = "zomboss", shares = new Dictionary<string, long> { ["Might"] = affordable - 1, ["Agility"] = 1 } });
        postResp.EnsureSuccessStatusCode();

        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}"))
            .Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.NotNull(after);
        Assert.True(after!.CommanderByEmpire.ContainsKey("zomboss"),
            "expected a zomboss pool on the ordinary read, got [" + string.Join(",", after.CommanderByEmpire.Keys) + "]");
        Assert.Equal(affordable - 1, after.CommanderByEmpire["zomboss"]["Might"]);
        Assert.Equal(1, after.CommanderByEmpire["zomboss"]["Agility"]);

        // The human's own sheet is a different pool and must be untouched by an empire-keyed write.
        Assert.Equal(0, after.Shares["Might"]);
    }

    /// <summary>The tier flag is genuinely Tier A now: a named non-human empire is SERVED — not refused
    /// with <c>empire_scope_not_widened</c>, and not answered 200 while quietly writing the human's
    /// pool. Both halves are asserted, because the second half is what the widening must actually buy:
    /// a refusal is loud, and so is a success that did something else.</summary>
    [Fact]
    public async Task Post_allocate_namingANonHumanEmpire_isServed_notRefusedAndNotSilentlyRedirected()
    {
        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, empire = "zomboss", shares = new Dictionary<string, long> { ["Might"] = 1 } });
        postResp.EnsureSuccessStatusCode();

        using var doc = System.Text.Json.JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("error", out _), "must not answer empire_scope_not_widened");

        // ...and it landed on the empire's own pool, not the human's.
        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}"))
            .Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(1, after!.CommanderByEmpire["zomboss"]["Might"]);
        Assert.Equal(0, after.Shares["Might"]);
    }

    [Fact]
    public async Task Post_allocate_anEmpireThisSaveDoesNotCarry_is404AndPersistsNothing()
    {
        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, empire = "not-an-empire-of-this-save", shares = new Dictionary<string, long> { ["Might"] = 1 } });

        Assert.Equal(HttpStatusCode.NotFound, postResp.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());
        Assert.Equal("empire_not_found", doc.RootElement.GetProperty("error").GetString());

        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}"))
            .Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(0, after!.Shares["Might"]);
    }

    /// <summary>Naming the save's own human empire is the ordinary priced path, byte for byte: it must
    /// not become a second unpriced writer for the player's own sheet.</summary>
    [Fact]
    public async Task Post_allocate_namingTheHumanEmpire_behavesExactlyLikeOmittingIt()
    {
        var human = _store.HumanEmpireOf(_playerId).Value;

        (await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, empire = human, shares = new Dictionary<string, long> { ["Might"] = 2 } }))
            .EnsureSuccessStatusCode();

        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}"))
            .Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(2, after!.Shares["Might"]);
    }

    /// <summary>The general form of the same defect: an UNRECOGNIZED field must be a loud 400, never a
    /// 200 that quietly did something else. This is what makes the silent drop a one-off rather than a
    /// property of the endpoint.</summary>
    [Fact]
    public async Task Post_allocate_anUnrecognizedField_isRejectedLoudly_neverSilentlyIgnored()
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = 2 }, emipre = "zomboss" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>An over-budget empire pool is refused, never clamped — the same PS-8 rule the human
    /// route has always held, checked against that EMPIRE's own Theta.</summary>
    [Fact]
    public async Task Post_allocate_anEmpirePoolOverThatEmpiresOwnBudget_isRefusedNotClamped()
    {
        var affordable = ZombossCommanderBudget();
        var before = await (await _http.GetAsync($"/api/aptitudes/{_playerId}"))
            .Content.ReadFromJsonAsync<AptitudesStateDto>();

        var resp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, empire = "zomboss", shares = new Dictionary<string, long> { ["Might"] = affordable + 1 } });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // The refusal NAMES the empire and the budget it was measured against — so a caller can tell
        // "your empire pool is too big" from "the human's sheet is too big". A refusal that does not say
        // which pool it refused is how a per-empire call ends up reported as a player-side failure.
        using (var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("aptitudes.overbudget", doc.RootElement.GetProperty("reason").GetString());
            Assert.Equal("zomboss", doc.RootElement.GetProperty("empire").GetString());
            Assert.Equal(affordable, doc.RootElement.GetProperty("budget").GetInt64());
        }

        // PS-8 for the empire arm: refused, never silently clamped to the budget. The pool reads back
        // UNCHANGED from before the attempt -- which for an unwritten pool is its read-time default.
        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}"))
            .Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(before!.CommanderByEmpire["zomboss"]["Might"], after!.CommanderByEmpire["zomboss"]["Might"]);
    }

    /// <summary>Zomboss's own commander budget — Theta from that empire's own commander level
    /// (EP4.16), never the player's, which is what makes the empire arm's budget check meaningful.</summary>
    long ZombossCommanderBudget()
    {
        var theta = new FusionRpg.Server.Power.ServerPowerIndexProvider(_store, PowerTuningHub.Tuning)
            .ActorIndexFor(new FusionRpg.Core.Saves.SaveId(_playerId), FusionRpg.Core.Commanders.EmpireId.Zomboss);
        return PointBudget.PointsFor(AllocationScope.Commander, theta, AptitudeTuningHub.Tuning);
    }

    // ---- EP1.9 -- the allocate routes go through RpgStore.TryReallocate; POST /respec-quote -----

    [Fact]
    public async Task Post_allocate_firstAllocation_isFreeAndCarriesTheNewReallocationFields()
    {
        var before = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        var affordable = before!.Budget;
        Assert.True(affordable > 0);

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = affordable } });
        postResp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("priced").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("priceAmount").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("respecCount").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("soulBalance").GetInt64()); // no souls awarded this test
    }

    [Fact]
    public async Task Post_allocate_aRespecWithoutCorrelationId_refusesAndLeavesTheAllocationUnchanged()
    {
        var before = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        var affordable = before!.Budget;
        Assert.True(affordable > 1);

        (await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = affordable } }))
            .EnsureSuccessStatusCode();

        // A decrease is a respec (RespecPolicy.IsRespec), so it needs a correlationId -- omitting one refuses.
        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = affordable - 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, postResp.StatusCode);
        using (var doc = System.Text.Json.JsonDocument.Parse(await postResp.Content.ReadAsStringAsync()))
            Assert.Equal("correlation.missing", doc.RootElement.GetProperty("reason").GetString());

        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(affordable, after!.Shares["Might"]); // refused, never partially applied
    }

    [Fact]
    public async Task Post_allocate_aPricedRespec_chargesSoulsAndReturnsTheReallocationFields()
    {
        var before = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        var affordable = before!.Budget;
        Assert.True(affordable > 1);

        (await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = affordable } }))
            .EnsureSuccessStatusCode();

        (_, var seedBalance) = _store.AwardSouls(_playerId, 1000, "seed", "ep1.9-bank-a");

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new
            {
                playerId = _playerId,
                shares = new Dictionary<string, long> { ["Might"] = affordable - 1 },
                correlationId = "ep1.9-server-respec-a"
            });
        postResp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());

        var expectedPrice = RespecPolicy.PriceOf(SpeciesBuildTuningHub.Tuning.UniqueRespec, 0).Amount;
        Assert.True(doc.RootElement.GetProperty("priced").GetBoolean());
        Assert.Equal(expectedPrice, doc.RootElement.GetProperty("priceAmount").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("respecCount").GetInt64());
        Assert.Equal(seedBalance.Balance - expectedPrice, doc.RootElement.GetProperty("soulBalance").GetInt64());

        var after = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        Assert.Equal(affordable - 1, after!.Shares["Might"]);
    }

    [Fact]
    public async Task Post_uniqueAllocate_aPricedRespec_chargesTheSpecimensOwnerAndReturnsTheReallocationFields()
    {
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-ep19-respec", "plant", typeId: 5, level: 10);
        var before = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        var spend = before!.Budget;
        Assert.True(spend > 1);

        (await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = actor.InstanceId, shares = new Dictionary<string, long> { ["Might"] = spend } }))
            .EnsureSuccessStatusCode();

        (_, var seedBalance) = _store.AwardSouls(_playerId, 1000, "seed", "ep1.9-bank-b");

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new
            {
                instanceId = actor.InstanceId,
                shares = new Dictionary<string, long> { ["Might"] = spend - 1 },
                correlationId = "ep1.9-server-respec-b"
            });
        postResp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());

        var expectedPrice = RespecPolicy.PriceOf(SpeciesBuildTuningHub.Tuning.UniqueRespec, 0).Amount;
        Assert.True(doc.RootElement.GetProperty("priced").GetBoolean());
        Assert.Equal(expectedPrice, doc.RootElement.GetProperty("priceAmount").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("respecCount").GetInt64());
        Assert.Equal(seedBalance.Balance - expectedPrice, doc.RootElement.GetProperty("soulBalance").GetInt64());
    }

    [Fact]
    public async Task RespecQuote_anAdditiveChange_isFreeAndNamesTheCurrentRespecCount()
    {
        var before = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        var affordable = before!.Budget;

        var quoteResp = await _http.PostAsJsonAsync("/api/aptitudes/respec-quote",
            new { scope = "commander", playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = affordable } });
        quoteResp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await quoteResp.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("isRespec").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("soulPrice").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("respecCount").GetInt64());
    }

    /// <summary>Spec test 8: "respec-quote equals what the save then charges, for counts 0 to 3" --
    /// four successive respecs, each quoted immediately before it is actually charged by
    /// `/allocate`, checking parity (and the escalating price) at every one of the named counts.
    /// Toggles ONE point between two aptitudes (never the total spent) so the test needs only
    /// `Budget >= 1` -- already guaranteed by the shipped tuning (`Post_withinBudget_...` above) --
    /// rather than a specific commander budget size, which is content this test must not pin.</summary>
    [Fact]
    public async Task RespecQuote_matchesWhatTheSubsequentAllocateThenCharges_forCounts0To3()
    {
        var before = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        var affordable = before!.Budget;
        Assert.True(affordable > 0);

        (await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = affordable } }))
            .EnsureSuccessStatusCode();
        _store.AwardSouls(_playerId, 100_000, "seed", "ep1.9-bank-c");

        var withVigor = new Dictionary<string, long> { ["Might"] = affordable - 1, ["Vigor"] = 1 };
        var withoutVigor = new Dictionary<string, long> { ["Might"] = affordable, ["Vigor"] = 0 };

        for (var count = 0; count < 4; count++)
        {
            var proposed = count % 2 == 0 ? withVigor : withoutVigor; // each toggle decreases exactly one of the two

            var quoteResp = await _http.PostAsJsonAsync("/api/aptitudes/respec-quote",
                new { scope = "commander", playerId = _playerId, shares = proposed });
            quoteResp.EnsureSuccessStatusCode();
            using var quoteDoc = System.Text.Json.JsonDocument.Parse(await quoteResp.Content.ReadAsStringAsync());
            Assert.True(quoteDoc.RootElement.GetProperty("isRespec").GetBoolean());
            Assert.Equal(count, quoteDoc.RootElement.GetProperty("respecCount").GetInt64());
            var quotedPrice = quoteDoc.RootElement.GetProperty("soulPrice").GetInt64();
            Assert.Equal(RespecPolicy.PriceOf(SpeciesBuildTuningHub.Tuning.UniqueRespec, count).Amount, quotedPrice);

            var allocateResp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
                new { playerId = _playerId, shares = proposed, correlationId = $"ep1.9-server-quote-parity-{count}" });
            allocateResp.EnsureSuccessStatusCode();
            using var allocateDoc = System.Text.Json.JsonDocument.Parse(await allocateResp.Content.ReadAsStringAsync());
            Assert.Equal(quotedPrice, allocateDoc.RootElement.GetProperty("priceAmount").GetInt64());
            Assert.Equal(count + 1, allocateDoc.RootElement.GetProperty("respecCount").GetInt64());
        }
    }

    // ---- aptitude-sheet unique-allocate ---------------------------------------------------------

    [Fact]
    public async Task UniqueGet_missingActor_returns404()
    {
        var resp = await _http.GetAsync("/api/aptitudes/unique/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task UniqueGet_freshSpecimen_withNoSpeciesProfile_resolvesTheEvenDefault_notEmpty()
    {
        // EP1.14 (spec-default-build.md): a levelled specimen with no explicit allocation used to
        // read Empty at every seam; it now reads the assign ladder's own default. This actor has no
        // creature profile (EnsureUniqueActorForAudit never writes one), so species-favour and posture
        // both refuse and the ladder lands on its terminal "even" rung -- never zero.
        const int specimenLevel = 10;
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-empty", "plant", typeId: 1, level: specimenLevel);
        var body = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        Assert.NotNull(body);
        Assert.Equal(actor.InstanceId, body!.InstanceId);
        Assert.Equal(_playerId, body.PlayerId);
        // Self-referential (population-pin SE3.4, 2026-09-20): this test created the actor at
        // specimenLevel itself, so it proves the level round-trips, not a fact about shipped content.
        Assert.Equal(specimenLevel, body.SpecimenLevel);
        // Reads AptitudeCatalog.Count live rather than a second literal; pinned once,
        // AptitudeCatalogTests.
        Assert.Equal(AptitudeCatalog.Count, body.Shares.Count);
        // The relation the default-build spec asks for -- never a literal point value: "even" gives
        // every one of the twelve a nonzero share, and the whole budget is spent (largest-remainder
        // rounding leaves no leftover), never Empty.
        Assert.All(body.Shares.Values, v => Assert.True(v > 0));
        Assert.True(body.Budget > 0);
        Assert.Equal(body.Budget, body.Spent);
        Assert.Equal(0, body.Leftover);
        Assert.True(body.WithinBudget);
    }

    [Fact]
    public async Task UniqueGet_carriesTheLadderIndexItsSheetHubComposesWith_neverTheSpecimenLevel()
    {
        // unique-theta-wire (T17): UniqueActorHubCompose resolves a specimen through ServerPowerIndexProvider with the
        // owner's player id, so that index is the real Θ behind the specimen's magnitudes. The specimen level is a
        // different number and must never stand in for it.
        const int specimenLevel = 37;
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-theta", "zombie", typeId: 4, level: specimenLevel);
        var expected = new FusionRpg.Server.Power.ServerPowerIndexProvider(_store, PowerTuningHub.Tuning)
            .ActorIndex(new FusionRpg.Core.Stats.StatContext { PlayerId = _playerId });
        Assert.NotEqual(specimenLevel, expected);

        var body = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();

        Assert.NotNull(body);
        Assert.Equal(expected, body!.Theta);
        Assert.Equal(specimenLevel, body.SpecimenLevel);
    }

    // ---- EP1.17: the sheet labels a default as a default ------------------------------------------

    [Fact]
    public async Task UniqueGet_withNoExplicitAllocation_reportsIsDefaultTrue_andTheWinningRuleId()
    {
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-isdefault", "plant", typeId: 1, level: 10);

        var body = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();

        Assert.NotNull(body);
        Assert.True(body!.IsDefault);
        // No creature profile on this bare audit actor -- species-favour and posture both refuse,
        // so the ladder's own winning rung is its terminal "even".
        Assert.Equal(AptitudeAutoAssignRules.Even, body.DefaultRuleId);
    }

    [Fact]
    public async Task UniqueGet_afterAnExplicitAllocation_reportsIsDefaultFalse_andNoRuleId()
    {
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-notdefault", "plant", typeId: 2, level: 10);
        var before = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        Assert.True(before!.IsDefault);

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = actor.InstanceId, shares = new Dictionary<string, long> { ["Might"] = 1 } });
        postResp.EnsureSuccessStatusCode();

        var after = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        Assert.NotNull(after);
        Assert.False(after!.IsDefault);
        Assert.Null(after.DefaultRuleId);
    }

    [Fact]
    public async Task UniquePost_withinBudget_savesLoadAllocationAndRoundTrips()
    {
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-spend", "plant", typeId: 2, level: 10);
        var before = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        var spend = before!.Budget;
        Assert.True(spend > 0);

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = actor.InstanceId, shares = new Dictionary<string, long> { ["Might"] = spend } });
        postResp.EnsureSuccessStatusCode();
        var postBody = await postResp.Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        Assert.Equal(spend, postBody!.Shares["Might"]);
        Assert.Equal(0, postBody.Leftover);

        var loaded = _store.LoadAllocation(AllocationScope.UniqueCreature, actor.InstanceId);
        Assert.Equal(spend, loaded.PointsAt(AllocationScope.UniqueCreature, "Might"));
        Assert.Equal(0, loaded.PointsAt(AllocationScope.Commander, "Might"));

        var getAfter = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        Assert.Equal(spend, getAfter!.Shares["Might"]);
    }

    [Fact]
    public async Task UniquePost_overBudget_409_neverClamps()
    {
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-over", "plant", typeId: 3, level: 5);
        var before = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = actor.InstanceId, shares = new Dictionary<string, long> { ["Might"] = before!.Budget + 1 } });
        Assert.Equal(HttpStatusCode.Conflict, postResp.StatusCode);

        var after = await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>();
        // EP1.14: PS-8 is "refused, never clamped" -- compared against BEFORE, which is now the
        // ladder's own default (nonzero), never a hardcoded 0.
        Assert.Equal(before.Shares["Might"], after!.Shares["Might"]);
        // The raw PERSISTED row is untouched by the refusal -- still literally empty, since nothing
        // was ever explicitly saved (the default above is computed at read, never written).
        Assert.Equal(0, _store.LoadAllocation(AllocationScope.UniqueCreature, actor.InstanceId)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));
    }

    [Fact]
    public async Task UniquePost_doesNotWriteCommanderRows()
    {
        var actor = _store.EnsureUniqueActorForAudit(_playerId, "ua-apt-iso", "plant", typeId: 4, level: 8);
        var beforeCmd = _store.LoadAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(_playerId));
        var budget = (await (await _http.GetAsync($"/api/aptitudes/unique/{actor.InstanceId}"))
            .Content.ReadFromJsonAsync<UniqueAptitudesStateDto>())!.Budget;

        var postResp = await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = actor.InstanceId, shares = new Dictionary<string, long> { ["Might"] = budget } });
        postResp.EnsureSuccessStatusCode();

        var afterCmd = _store.LoadAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(_playerId));
        Assert.Equal(beforeCmd.PointsAt(AllocationScope.Commander, "Might"),
            afterCmd.PointsAt(AllocationScope.Commander, "Might"));
    }

    // ---- species-build T3.1 (allocation-transport): the additive `species` field --------------

    [Fact]
    public async Task Get_forAPlayerWithNoSpecies_hasAnEmptySpeciesMap_commanderHalfUnaffected()
    {
        var resp = await _http.GetAsync($"/api/aptitudes/{_playerId}");
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(raw);

        // The commander half's own keys are exactly what shipped before this task -- `species` is
        // additive, not a replacement shape (spec's own ⛔ callout).
        Assert.True(doc.RootElement.TryGetProperty("theta", out _));
        Assert.True(doc.RootElement.TryGetProperty("budget", out _));
        Assert.True(doc.RootElement.TryGetProperty("spent", out _));
        Assert.True(doc.RootElement.TryGetProperty("withinBudget", out _));
        Assert.True(doc.RootElement.TryGetProperty("shares", out var shares));
        Assert.Equal(12, shares.EnumerateObject().Count());

        Assert.True(doc.RootElement.TryGetProperty("species", out var species));
        Assert.Empty(species.EnumerateObject());
    }

    [Fact]
    public async Task Get_sendsOnlyTheSpeciesThePlayerHasActuallyLevelled()
    {
        const int fumeshroomCreatureTypeId = 60007;
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        SpeciesBuildPlanCatalog.Configure(new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            ["fumeshroom"] = new Dictionary<string, long>(StringComparer.Ordinal) { ["Might"] = 700, ["Vigor"] = 300 }
        });
        FusionRpg.Core.Progression.SpeciesProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.SpeciesProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "species-progression.v1.json"))));

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rpg_actor_progression(
                  save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, scope_key)
                VALUES ($p, $e, 'species', $tid, 21, 0, 21, 0, 0, $now, 'fumeshroom');
                """;
            cmd.Parameters.AddWithValue("$p", _playerId);
                cmd.Parameters.AddWithValue("$e", _store.HumanEmpireOf(_playerId).Value);
            cmd.Parameters.AddWithValue("$tid", fumeshroomCreatureTypeId);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        var body = await (await _http.GetAsync($"/api/aptitudes/{_playerId}"))
            .Content.ReadFromJsonAsync<AptitudesStateDto>();

        Assert.NotNull(body);
        var fumeshroom = Assert.Single(body!.Species);
        Assert.Equal("fumeshroom", fumeshroom.Key);
        Assert.True(fumeshroom.Value["Might"] > fumeshroom.Value["Vigor"]);
        Assert.True(fumeshroom.Value["Might"] > 0); // NOT the silent-zero this program keeps naming
    }

    // ── species-progression step 6.2, Transport (SP6.3) — speciesLayers ─────────────────────────────

    /// <summary>species-passive.{speciesId} template (1a's own core atom at seq 1) + a real
    /// materialised `effect_instance` whose ROLLED pick sits at seq 2 (strictly after the template's
    /// highest core seq, matching the real Instantiator's own numbering rule and
    /// SpeciesLayerProjector.ProjectPlayerMod's "1a never repeats 1a" filter -- reusing seq 1 for both,
    /// as a naive fixture would, makes every 1b row look like the template's own core and get
    /// silently skipped) + a real ledger row pointing at it.</summary>
    (string SpeciesId, string InstanceId) FuseASpecies(long playerId, string speciesId, string channel, long amount)
    {
        var coreAtomId = $"atom.{speciesId}-core.t1";
        Assert.True(_store.UpsertAtom(new FusionRpg.Core.Effects.Atoms.AtomRow
        {
            AtomId = coreAtomId, KindId = "stat.derived", FamilyId = $"atom.{speciesId}-core", Tier = 1,
            Name = $"{speciesId} core", ParamsJson = """{"channel":"resource.max.stamina","op":"flat","amount":1}""",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new FusionRpg.Core.Effects.Atoms.ContainerRow
        {
            ContainerId = $"species-passive.{speciesId}", Kind = FusionRpg.Core.Effects.Atoms.ContainerKind.SpeciesPassive,
            Atoms = new[] { new FusionRpg.Core.Effects.Atoms.ContainerAtomRow(1, coreAtomId) },
        }).IsOk);

        var pickAtomId = $"atom.{speciesId}-pick.t1";
        Assert.True(_store.UpsertAtom(new FusionRpg.Core.Effects.Atoms.AtomRow
        {
            AtomId = pickAtomId, KindId = "stat.derived", FamilyId = $"atom.{speciesId}-pick", Tier = 1,
            Name = $"{speciesId} pick", ParamsJson = $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""",
        }).IsOk);

        var instanceId = Guid.NewGuid().ToString("N");
        using (var raw = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO effect_instance
                      (instance_id, container_id, roll_seed, catalog_revision, created_utc, origin,
                       theta_content, content_scale_milli)
                    VALUES ($id, $c, 1, 0, $utc, 'test', 0, 0);
                    """;
                cmd.Parameters.AddWithValue("$id", instanceId);
                cmd.Parameters.AddWithValue("$c", $"species-passive.{speciesId}");
                cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO effect_instance_atom (instance_id, seq, atom_id, values_json, power_json) " +
                    "VALUES ($id, 2, $atom, '{}', NULL);"; // seq 2: strictly after the template's own seq 1
                cmd.Parameters.AddWithValue("$id", instanceId);
                cmd.Parameters.AddWithValue("$atom", pickAtomId);
                cmd.ExecuteNonQuery();
            }
        }

        var owner = new FusionRpg.Core.Saves.EmpireRef(
            new FusionRpg.Core.Saves.SaveId(playerId), _store.HumanEmpireOf(playerId));
        Assert.True(_store.AppendSpeciesMod(owner, speciesId,
            FusionRpg.Core.Creatures.Layers.SpeciesModMechanism.FusionPick,
            correlationId: instanceId, instanceId: instanceId, catalogRevision: 0));
        return (speciesId, instanceId);
    }

    [Fact]
    public async Task Get_speciesLayers_onAFreshPlayer_isEmptyEverywhere()
    {
        var body = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();

        Assert.NotNull(body);
        Assert.Equal(_playerId, body!.SpeciesLayers.SaveId);
        Assert.Empty(body.SpeciesLayers.Base);
        Assert.Empty(body.SpeciesLayers.Mod);
        Assert.Empty(body.SpeciesLayers.Empire);
    }

    [Fact]
    public async Task Get_speciesLayers_afterAFusion_modIsKeyedByTheRealEmpireId_andBaseAccompaniesIt()
    {
        FuseASpecies(_playerId, "melon-pult", "resource.max.hp", 500);
        var humanEmpire = _store.HumanEmpireOf(_playerId).Value;

        var body = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();

        Assert.NotNull(body);
        Assert.True(body!.SpeciesLayers.Mod.ContainsKey(humanEmpire), $"expected mod keyed by the real empire id '{humanEmpire}', got [{string.Join(",", body.SpeciesLayers.Mod.Keys)}]");
        var melonPultMod = body.SpeciesLayers.Mod[humanEmpire]["melon-pult"];
        Assert.NotEmpty(melonPultMod);
        Assert.Contains(melonPultMod, r => r.Channel == "resource.max.hp" && r.Amount == 500);

        // 1a accompanies the SAME species mod delivered -- its own core row, not the 1b pick
        Assert.True(body.SpeciesLayers.Base.ContainsKey("melon-pult"));
        Assert.NotEmpty(body.SpeciesLayers.Base["melon-pult"]);
        Assert.Contains(body.SpeciesLayers.Base["melon-pult"], r => r.Channel == "resource.max.stamina" && r.Amount == 1);
        // ...and the pick row must NOT also appear in 1a (1a never repeats 1b, the reverse of the
        // "1a never repeats 1a" filter ProjectPlayerMod itself applies)
        Assert.DoesNotContain(body.SpeciesLayers.Base["melon-pult"], r => r.Channel == "resource.max.hp");
        Assert.Empty(body.SpeciesLayers.Empire); // 2b stays {} until SP6.10
    }

    [Fact]
    public async Task Get_speciesLayers_anEmpireWithNoLedgerRows_isAbsentFromMod_notPresentWithAnEmptyMap()
    {
        // Zomboss is a real empire of this save (seeded by SaveEmpires' registry) but never fuses --
        // it must be ABSENT from mod, never a key pointing at {}.
        var body = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();

        Assert.NotNull(body);
        Assert.DoesNotContain("zomboss", body!.SpeciesLayers.Mod.Keys);
    }

    [Fact]
    public async Task Get_speciesLayers_saveIsolation_saveBNeverContainsSaveAsRows()
    {
        FuseASpecies(_playerId, "melon-pult", "resource.max.hp", 500);
        var second = _store.CreatePlayer("SpeciesLayersSecond");
        FuseASpecies(second.Id, "wallnut", "combat.defense.omni", 300);

        var bodyA = await (await _http.GetAsync($"/api/aptitudes/{_playerId}")).Content.ReadFromJsonAsync<AptitudesStateDto>();
        var bodyB = await (await _http.GetAsync($"/api/aptitudes/{second.Id}")).Content.ReadFromJsonAsync<AptitudesStateDto>();

        Assert.NotNull(bodyA);
        Assert.NotNull(bodyB);
        Assert.True(bodyA!.SpeciesLayers.Base.ContainsKey("melon-pult"));
        Assert.False(bodyA.SpeciesLayers.Base.ContainsKey("wallnut"));
        Assert.True(bodyB!.SpeciesLayers.Base.ContainsKey("wallnut"));
        Assert.False(bodyB.SpeciesLayers.Base.ContainsKey("melon-pult"));
    }

    sealed class AptitudesStateDto
    {
        public long Theta { get; set; }
        public long Budget { get; set; }
        public long Spent { get; set; }
        public bool WithinBudget { get; set; }
        public Dictionary<string, long> Shares { get; set; } = new();
        public Dictionary<string, Dictionary<string, long>> Species { get; set; } = new();
        public SpeciesLayersDto SpeciesLayers { get; set; } = new();
        public string HumanEmpire { get; set; } = "";
        /// <summary>ai-empire-species EP4.18 (R23) — every NON-human empire's commander pool for this
        /// save, the ordinary read endpoint a real client consumes.</summary>
        public Dictionary<string, Dictionary<string, long>> CommanderByEmpire { get; set; } = new();
    }

    sealed class SpeciesLayerRowDto
    {
        public string Channel { get; set; } = "";
        public string Op { get; set; } = "";
        public string Kind { get; set; } = "";
        public double? Amount { get; set; }
        public long? KMicro { get; set; }
        public string SourceId { get; set; } = "";
    }

    sealed class SpeciesLayersDto
    {
        public long SaveId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("base")]
        public Dictionary<string, List<SpeciesLayerRowDto>> Base { get; set; } = new();
        public Dictionary<string, Dictionary<string, List<SpeciesLayerRowDto>>> Mod { get; set; } = new();
        public Dictionary<string, object> Empire { get; set; } = new();
    }

    sealed class UniqueAptitudesStateDto
    {
        public string InstanceId { get; set; } = "";
        public long PlayerId { get; set; }
        public long SpecimenLevel { get; set; }
        public long Budget { get; set; }
        public long Spent { get; set; }
        public long Leftover { get; set; }
        public bool WithinBudget { get; set; }
        public Dictionary<string, long> Shares { get; set; } = new();
        public long? Theta { get; set; }
        public bool IsDefault { get; set; }
        public string? DefaultRuleId { get; set; }
    }

    static string RepoTuningDir() => Path.Combine(KeepverseRoots.Core(), "data", "tuning");

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

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
