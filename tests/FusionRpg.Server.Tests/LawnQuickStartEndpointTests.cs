using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>Live-lawn-quick-start session: POST /api/debug/lawn/quick-start against a REAL in-process
/// host, proving the orchestration logic (already-live skip, bad-level-type refusal, unknown-scenario
/// 404, and the polling state machine's own timeout behavior) without needing a live game — the actual
/// Unity-side enter-level/board.start/run-steps.done handshake can't be proven here, only the server's
/// own decision logic around it (matching this session's established precedent for debug-orchestration
/// code paths that terminate at "sent a command, waiting for a real game to answer").</summary>
[Trait("VerificationId", "server.lawn-quick-start")]
public class LawnQuickStartEndpointTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapDebug();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    void SeedLiveBoardStart(string levelType = "Adventure")
    {
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            Payload = JsonSerializer.SerializeToElement(new { levelType, boardLevel = 1, levelName = "" })
        });
    }

    [Fact]
    public async Task Post_boardCyclingRapidly_refusesFastWithMetric_neverPollsIntoTheTimeout()
    {
        // Observability gap found live 2026-09-14: a board stuck in a rapid match-end/retry loop (a
        // "quick" setup-skip with no real plants placed loses every wave instantly) produced a
        // confusing, silent "debug.level.enter did not ack" timeout with nothing pointing at the real
        // cause -- diagnosing it required manually diffing thousands of raw events. quick-start must
        // detect this directly, fail fast, and report the real cause plus how long it actually waited.
        _store.Heartbeat(RpgConstants.SourceInjector);
        for (var i = 0; i < 4; i++)
        {
            _store.InsertEvent(new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Kind = "board.end",
                Payload = JsonSerializer.SerializeToElement(new { })
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { timeoutSec = 30 });
        sw.Stop();

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("cycling", body!["error"].ToString());
        Assert.True(((JsonElement)body["recentBoardEnds"]).GetInt32() >= 3);
        Assert.True(body.ContainsKey("waitedMs"), "expected a waitedMs metric on the refusal");
        // The whole point: this must be a fast, immediate refusal, never a poll into the 30s timeout.
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"expected an immediate refusal, took {sw.ElapsedMilliseconds}ms — cycling detection did not short-circuit the poll");
    }

    [Fact]
    public async Task Post_latestMatchResultIsDefeat_exitsToMenuBeforeEnterLevel_neverProbesOrResetsTheDeadBoard()
    {
        // Real gap found live 2026-09-14: after a genuine defeat (match.result payload result:
        // "defeat"), debug.reset-board restored API-level spawn capability but the operator
        // confirmed the game's own visual defeat overlay stayed up -- reset-board clears entities on
        // the SAME dead board, it never leaves it. First fix (force:true on debug.enter-level over
        // the live board) worked once live, then proven live TWICE more to simply never ack --
        // consistent with the known forced-entry engine-stability hazard. Current fix: call the real
        // UIMgr.BackToMenu() (debug.ui-nav, action:"back-to-menu") first so the board is actually torn
        // down, then a normal (non-forced) enter-level. A detected defeat must skip the mid-entry
        // probe entirely (the old board is known-dead, not a fresh seed-picker) and go straight to
        // ui-nav, never debug.reset-board.
        _store.Heartbeat(RpgConstants.SourceInjector);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "match.result",
            Payload = JsonSerializer.SerializeToElement(new { result = "defeat", activeMatchMs = 57311 })
        });
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 1 });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode); // honest timeout -- no real game answering ui-nav
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("debug.ui-nav", body!["error"].ToString());
        Assert.Contains("did not ack", body["error"].ToString());
        Assert.True(((JsonElement)body["defeatReset"]).GetBoolean());

        var sent = inbox.Drain(int.MaxValue).ToList();
        Assert.DoesNotContain(sent, c => c.Name == "debug.reset-board");
        Assert.DoesNotContain(sent, c => c.Name == "debug.skip-setup"); // the mid-entry probe must be skipped
        var navCmd = sent.SingleOrDefault(c => c.Name == "debug.ui-nav");
        Assert.NotNull(navCmd);
        Assert.Contains("\"action\":\"back-to-menu\"", JsonSerializer.Serialize(navCmd!.Payload));
        // ui-nav never acked in this test (no real injector), so enter-level must never be reached.
        Assert.DoesNotContain(sent, c => c.Name == "debug.enter-level");
    }

    [Fact]
    public async Task Post_defeatFollowedByServerRestartHello_stillExitsToMenu_notDiscardedAsStale()
    {
        // Real bug found live 2026-09-14, same afternoon as the fix above: this used to invalidate
        // match.result against the newest injector.hello, matching FindLatestLiveBoardStart's own
        // guard for board.start. That guard is right for board.start (a KILLED game process leaving a
        // stale row forever); it was wrong here -- restarting the SERVER (not the game) mints a fresh
        // hello for the SAME still-running, still-defeated game, and the old logic discarded a
        // genuinely current defeat as if it belonged to a dead process. quick-start then treated the
        // dead board as live instead of recovering it. Fixed: only a NEWER board.economy proves the
        // board moved on since the defeat; a hello alone does not.
        _store.Heartbeat(RpgConstants.SourceInjector);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "match.result",
            Payload = JsonSerializer.SerializeToElement(new { result = "defeat" })
        });
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "injector.hello",
            Payload = JsonSerializer.SerializeToElement(new { game = "pvzrh-3.9", version = "1.0.0" })
        });
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 1 });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.True(((JsonElement)body!["defeatReset"]).GetBoolean(), "a hello with no newer board.economy must not discard a real defeat");

        var sent = inbox.Drain(int.MaxValue).ToList();
        var navCmd = sent.SingleOrDefault(c => c.Name == "debug.ui-nav");
        Assert.NotNull(navCmd);
        Assert.Contains("\"action\":\"back-to-menu\"", JsonSerializer.Serialize(navCmd!.Payload));
    }

    [Fact]
    public async Task Post_latestMatchResultIsWin_doesNotResetBoard()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "match.result",
            Payload = JsonSerializer.SerializeToElement(new { result = "victory" })
        });
        SeedLiveBoardStart();
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 1 });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.False(((JsonElement)body!["defeatReset"]).GetBoolean());

        var sent = inbox.Drain(int.MaxValue).Select(c => c.Name).ToList();
        Assert.DoesNotContain("debug.reset-board", sent);
    }

    [Fact]
    public async Task Post_injectorNotConnected_refusesBeforeTouchingAnything()
    {
        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("injector not connected", body!["error"].ToString());
    }

    [Fact]
    public async Task Post_boardAlreadyLive_skipsEnterLevel_andRefusesOnBadLevelTypeBeforeAnyWait()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        SeedLiveBoardStart(levelType: "Explore");

        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { timeoutSec = 1 });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("levelType=Explore", body!["error"].ToString());
    }

    [Fact]
    public async Task Post_boardAlreadyLive_unknownScenario_returns404BeforeAnyWait()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        SeedLiveBoardStart();

        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "not-a-real-scenario", timeoutSec = 1 });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_boardAlreadyLive_realScenario_sendsFreezeAndScenario_thenTimesOutHonestly()
    {
        // No real game is answering, so debug.run-steps.done never arrives -- this proves the
        // orchestration reaches and dispatches the real scenario (skipping enter-level entirely
        // because a board is already live) rather than claiming success it never observed.
        _store.Heartbeat(RpgConstants.SourceInjector);
        SeedLiveBoardStart();
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var request = _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 1 });
        await AnswerSetupSkipOk(inbox, new List<CommandDto>());

        var resp = await request;
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("did not complete within 1s", body!["error"].ToString());
    }

    /// <summary>2026-09-15 owner-reported, every run: a level left behind the seed-picker has no game time and
    /// no usable seed bank. quick-start used to carry on to wave-freeze and the scenario anyway and report a
    /// "ready" lab; a failed or unacknowledged setup skip is now terminal and names the injector's stage.</summary>
    [Fact]
    public async Task Post_setupSkipFails_isTerminal_neverFreezesWavesOrRunsTheScenario()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        SeedLiveBoardStart();
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var request = _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 3 });
        var seen = new List<CommandDto>();
        // The first debug.skip-setup is quick-start's mid-entry probe (left unanswered, so it times out);
        // the second is the real post-entry dismissal, which the injector reports as failed.
        await WaitForCommand(inbox, seen, "debug.skip-setup", count: 2);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.setup.skip",
            Payload = JsonSerializer.SerializeToElement(new { ok = false, stage = "picker-never-appeared", error = "seed-picker StartGameButton never appeared within 30s" })
        });

        var resp = await request;
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("never appeared", body!["error"].ToString());
        Assert.Equal("picker-never-appeared", body["stage"].ToString());

        seen.AddRange(inbox.Drain(int.MaxValue));
        Assert.DoesNotContain(seen, c => c.Name == "debug.wave-freeze");
        Assert.DoesNotContain(seen, c => c.Name == "debug.run-steps");
    }

    static async Task WaitForCommand(InjectorCommandInbox inbox, List<CommandDto> seen, string name, int count = 1)
    {
        for (var i = 0; i < 400 && seen.Count(c => c.Name == name) < count; i++)
        {
            await Task.Delay(25);
            seen.AddRange(inbox.Drain(int.MaxValue));
        }
        Assert.True(seen.Count(c => c.Name == name) >= count, $"expected {count} x {name}");
    }

    /// <summary>Answers the post-entry <c>debug.skip-setup</c> the way the injector does once the picker is
    /// dismissed (the ack is emitted only after InitBoard.ready, DebugActions.TickPendingSkipSetup).</summary>
    async Task AnswerSetupSkipOk(InjectorCommandInbox inbox, List<CommandDto> seen)
    {
        await WaitForCommand(inbox, seen, "debug.skip-setup");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.setup.skip",
            Payload = JsonSerializer.SerializeToElement(new { ok = true, method = "button", stage = "started" })
        });
    }

    [Fact]
    public async Task Post_boardAlreadyLive_sendsSetupSkip_beforeWaveFreezeAndScenario()
    {
        // Real bug found live 2026-09-13: EnterGame opens the level, but the vanilla "Choose Your
        // Plants" seed-picker screen stays up until debug.skip-setup dismisses it — with it open,
        // waves never start and plants/zombies never act. quick-start must self-enable the
        // DEBUG-SETUP-SKIP gate and send debug.skip-setup before any wave/scenario work, the same way
        // it already self-enables DEBUG-LEVEL-ENTRY for entering the level in the first place.
        _store.Heartbeat(RpgConstants.SourceInjector);
        SeedLiveBoardStart();
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var request = _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 1 });
        var seenCommands = new List<CommandDto>();
        await AnswerSetupSkipOk(inbox, seenCommands);
        var resp = await request;
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode); // honest run-steps timeout -- no real game answering

        seenCommands.AddRange(inbox.Drain(int.MaxValue));
        var sent = seenCommands.Select(c => c.Name).ToList();
        var toggleIdx = sent.FindIndex(n => n == "cheat.toggle");
        var skipIdx = sent.FindIndex(n => n == "debug.skip-setup");
        var freezeIdx = sent.FindIndex(n => n == "debug.wave-freeze");
        var sessionIdx = sent.FindIndex(n => n == "debug.session");
        var runStepsIdx = sent.FindIndex(n => n == "debug.run-steps");

        Assert.True(toggleIdx >= 0, "expected a cheat.toggle command (DEBUG-SETUP-SKIP) to be sent");
        Assert.True(skipIdx >= 0, "expected debug.skip-setup to be sent");
        Assert.True(freezeIdx >= 0, "expected debug.wave-freeze to be sent");
        Assert.True(runStepsIdx >= 0, "expected debug.run-steps to be sent");
        Assert.True(toggleIdx < skipIdx, "the DEBUG-SETUP-SKIP toggle must be enabled before debug.skip-setup is sent");
        Assert.True(skipIdx < freezeIdx, "the seed-picker screen must be dismissed before waves are frozen");
        Assert.True(sessionIdx >= 0 && sessionIdx < freezeIdx,
            "the debug session must start before the freeze, or the injector's pre-session cheat snapshot captures the freeze and restores it after the lab");
        Assert.True(freezeIdx < runStepsIdx, "waves must be frozen before scenario steps run");
    }

    [Fact]
    public async Task Post_noBoardStartRecorded_butSetupSkipProbeSucceeds_neverAttemptsEnterLevel()
    {
        // Real bug found live 2026-09-14: some game profiles never emit board.start for a board that
        // already exists behind the seed-picker screen (confirmed on pvzrh-3.9 -- several full match
        // cycles, zero board.start events, ever), so FindLatestLiveBoardStart sees "no board" even
        // though a real one is sitting there mid-entry. quick-start used to always attempt
        // debug.enter-level in that case, which times out because EnterGame is undefined when called
        // a second time on an already-mid-entry level. The fix: probe with debug.skip-setup FIRST --
        // a success proves a real Board already exists, so enter-level must never be attempted.
        _store.Heartbeat(RpgConstants.SourceInjector);
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var request = _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 2 });

        // Wait for the probe's debug.skip-setup to be enqueued, then answer it ok:true -- simulating
        // a real injector that is already sitting on the seed-picker (Board exists, not yet entered
        // through a fresh EnterGame call this request).
        List<CommandDto> queued = new();
        for (var i = 0; i < 80 && !queued.Any(c => c.Name == "debug.skip-setup"); i++)
        {
            await Task.Delay(25);
            queued.AddRange(inbox.Drain(int.MaxValue));
        }
        Assert.Contains(queued, c => c.Name == "debug.skip-setup");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.setup.skip",
            Payload = JsonSerializer.SerializeToElement(new { ok = true, method = "quick", board = true, ui = true })
        });

        var resp = await request;
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode); // honest scenario timeout -- no real game answering run-steps

        var sentAfter = queued.Concat(inbox.Drain(int.MaxValue)).Select(c => c.Name).ToList();
        Assert.DoesNotContain("debug.enter-level", sentAfter);
        Assert.Contains("debug.wave-freeze", sentAfter);
        Assert.Contains("debug.run-steps", sentAfter);
    }

    [Fact]
    public async Task Post_enterLevelSaysBoardAlreadyLive_butNoLevelMetadataFound_proceedsInstadOfRefusing()
    {
        // Real bug found live 2026-09-14: on a profile where board.start never fires, and a
        // long-running server session had already scrolled catalog.zombies out of
        // FindLatestKind's lookback window, quick-start used to hard-refuse a board the injector
        // had JUST confirmed was live ("enter-level reported board already live, but no level
        // metadata was found") -- treating a real, confirmed-live board as unusable. It must
        // instead be tolerated the same way the mid-entry probe already tolerates an unresolvable
        // levelType: proceed to wave-freeze/scenario rather than refuse.
        _store.Heartbeat(RpgConstants.SourceInjector);
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var request = _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 2 });

        // Answer the proactive mid-entry probe with a genuine "not on the seed-picker" refusal.
        List<CommandDto> seen = new();
        for (var i = 0; i < 80 && !seen.Any(c => c.Name == "debug.skip-setup"); i++)
        {
            await Task.Delay(25);
            seen.AddRange(inbox.Drain(int.MaxValue));
        }
        Assert.Contains(seen, c => c.Name == "debug.skip-setup");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.setup.skip",
            Payload = JsonSerializer.SerializeToElement(new { ok = false, method = "quick", error = "InitBoard.Instance is null" })
        });

        // Answer debug.enter-level with the real, observed "board already live" rejection -- no
        // catalog.zombies event is seeded, so the old code's levelType fallback comes up empty.
        for (var i = 0; i < 80 && !seen.Any(c => c.Name == "debug.enter-level"); i++)
        {
            await Task.Delay(25);
            seen.AddRange(inbox.Drain(int.MaxValue));
        }
        Assert.Contains(seen, c => c.Name == "debug.enter-level");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.level.enter",
            Payload = JsonSerializer.SerializeToElement(new { ok = false, error = "board already live — return to main menu, or pass force=true (unsafe)" })
        });

        var resp = await request;
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode); // still an honest run-steps.done timeout
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.DoesNotContain("no level metadata was found", body!["error"].ToString());

        var sentAfter = seen.Concat(inbox.Drain(int.MaxValue)).Select(c => c.Name).ToList();
        Assert.Contains("debug.wave-freeze", sentAfter);
        Assert.Contains("debug.run-steps", sentAfter);
    }

    [Fact]
    public async Task Post_scenarioCompletes_foldsRealGameStateIntoLiveEntities()
    {
        // 2026-09-15 (live-probe-mcp overview): quick-start used to return ok:true purely from the
        // injector ack chain, with no honest read of whether the board was ACTUALLY live -- every
        // live probe session had to make a SEPARATE debug_game_state call afterward to find out
        // (real incident: a caller read entered:true against a board still stuck on the seed-picker
        // with zero real plants/zombies). quick-start must now fold a real debug.game-state read
        // into its own response so a caller sees the truth in one round trip.
        _store.Heartbeat(RpgConstants.SourceInjector);
        SeedLiveBoardStart();
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        var request = _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 5 });

        List<CommandDto> seen = new();
        async Task WaitFor(string name)
        {
            for (var i = 0; i < 120 && !seen.Any(c => c.Name == name); i++)
            {
                await Task.Delay(25);
                seen.AddRange(inbox.Drain(int.MaxValue));
            }
            Assert.Contains(seen, c => c.Name == name);
        }

        await WaitFor("debug.skip-setup");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.setup.skip",
            Payload = JsonSerializer.SerializeToElement(new { ok = true, method = "quick" })
        });

        await WaitFor("debug.run-steps");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.run-steps.done",
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        await WaitFor("debug.game-state");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.game-state",
            Payload = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                plantCount = 1,
                zombieCount = 1,
                liveState = "InMatch",
                phaseMismatch = false
            })
        });

        await WaitFor("debug.effect.board-snapshot");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.effect.board-snapshot",
            Payload = JsonSerializer.SerializeToElement(new
            {
                entities = new[]
                {
                    new { ptr = "0xz1", side = "zombie", living = true },
                    new { ptr = "0xp1", side = "plant", living = true }
                }
            })
        });

        var resp = await request;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var liveEntities = (JsonElement)body!["liveEntities"];
        Assert.Equal(1, liveEntities.GetProperty("plantCount").GetInt32());
        Assert.Equal(1, liveEntities.GetProperty("zombieCount").GetInt32());
        Assert.Equal("InMatch", liveEntities.GetProperty("liveState").GetString());
        Assert.False(liveEntities.GetProperty("phaseMismatch").GetBoolean());
        Assert.Equal("0xz1", body["targetPtr"].ToString());
        Assert.Equal("0xp1", body["plantPtr"].ToString());
    }

    [Fact]
    public async Task Post_gameStateReadTimesOut_stillReturnsSuccess_withLiveEntitiesNull()
    {
        // The game-state fold-in is best-effort: a slow/missing ack must never turn an otherwise
        // real, successful setup into a failure -- it degrades to liveEntities:null.
        _store.Heartbeat(RpgConstants.SourceInjector);
        SeedLiveBoardStart();
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();

        // Kept small so the deliberately-unanswered debug.game-state poll (min(timeoutSec, 10)s)
        // resolves quickly instead of stretching this test out.
        var request = _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { scenario = "lab-overlay", timeoutSec = 2 });

        List<CommandDto> seen = new();
        async Task WaitFor(string name, int maxIterations = 120)
        {
            for (var i = 0; i < maxIterations && !seen.Any(c => c.Name == name); i++)
            {
                await Task.Delay(25);
                seen.AddRange(inbox.Drain(int.MaxValue));
            }
            Assert.Contains(seen, c => c.Name == name);
        }

        await WaitFor("debug.skip-setup");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.setup.skip",
            Payload = JsonSerializer.SerializeToElement(new { ok = true, method = "quick" })
        });

        await WaitFor("debug.run-steps");
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.run-steps.done",
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        // debug.game-state is sent but deliberately never acked here -- its own bounded poll
        // (min(timeoutSec, 10)s = 2s) must time out without failing the request. The NEXT command
        // (debug.effect.board-snapshot) only gets sent after that full wait elapses, so give this
        // WaitFor enough iterations to outlast it (2s poll + scheduling slack).
        await WaitFor("debug.effect.board-snapshot", maxIterations: 200);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.effect.board-snapshot",
            Payload = JsonSerializer.SerializeToElement(new { entities = Array.Empty<object>() })
        });

        var resp = await request;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        // A JSON null deserializes to a C# null reference here (Dictionary<string, object>), not a
        // boxed JsonElement -- assert the reference directly rather than casting it.
        Assert.Null(body!["liveEntities"]);
    }

    [Fact]
    public async Task Post_noBoardLive_attemptsEnterLevel_andTimesOutHonestlyWhenNothingAcks()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        // No board.start seeded -- the endpoint must attempt debug.enter-level and wait for an ack
        // that (with no real game) will never come, timing out honestly rather than guessing.
        var resp = await _http.PostAsJsonAsync("/api/debug/lawn/quick-start", new { timeoutSec = 1 });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("debug.level.enter did not ack", body!["error"].ToString());
    }

    [Fact]
    public async Task Post_setupSkip_injectorNotConnected_refusesBeforeQueueing()
    {
        var resp = await _http.PostAsJsonAsync("/api/debug/setup/skip", new { });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("injector not connected", body!["error"].ToString());
    }

    [Fact]
    public async Task Post_setupSkip_connected_timesOutHonestlyWhenNoUnityAckArrives()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);

        var resp = await _http.PostAsJsonAsync("/api/debug/setup/skip",
            new { method = "quick", timeoutSec = 1 });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("debug.setup.skip did not ack", body!["error"].ToString());
    }

    [Fact]
    public async Task Post_setupSkip_acknowledgement_returns_success()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);
        var inbox = _app.Services.GetRequiredService<InjectorCommandInbox>();
        var request = _http.PostAsJsonAsync("/api/debug/setup/skip",
            new { method = "quick", timeoutSec = 5 });

        for (var i = 0; i < 40 && inbox.Count == 0; i++)
            await Task.Delay(25);

        Assert.True(inbox.Count > 0);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "debug.setup.skip",
            Payload = JsonSerializer.SerializeToElement(new { ok = true, method = "quick", ready = true })
        });

        var resp = await request;

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("quick", body!["method"].ToString());
    }

    [Fact]
    public async Task Post_setupSkip_unknownMethod_refusesBeforeQueueing()
    {
        _store.Heartbeat(RpgConstants.SourceInjector);

        var resp = await _http.PostAsJsonAsync("/api/debug/setup/skip",
            new { method = "hide-panel" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
