using FusionRpg.Contracts;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// aura-skill-todo.md Phase 5 — <c>DebugEndpoints.FindLatestLiveBoardStart</c>.
///
/// <para><b>The trap this closes, which cost two sessions on 2026-08-30.</b> A <c>board.end</c> is only
/// written on a clean exit. Kill the game mid-match — a crash, a redeploy, or an assistant tool call
/// whose process tree is reaped — and none is ever written, so that <c>board.start</c> row stays "live"
/// forever. <c>POST /api/debug/lawn/quick-start</c> then reports <c>entered:false</c> with null
/// <c>targetPtr</c>/<c>plantPtr</c> and skips level entry entirely, so every probe afterwards runs
/// against a board that does not exist.</para>
///
/// <para>It bit twice the same day: once read as an <c>attackDamage</c> regression that turned out to be
/// no board at all, and once blocking the A5 live proof outright. Documenting it a third time would not
/// have helped — <c>injector.hello</c> is emitted once per injector startup, so any <c>board.start</c>
/// older than the newest one belongs to a game process that is gone.</para>
/// </summary>
public class StaleBoardStartTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public StaleBoardStartTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        // RpgStore is not IDisposable; the temp dir is the only thing to clean up.
        _testStore.Dispose();
    }

    long Insert(string kind) => _store.InsertEvent(new EventEnvelope
    {
        T = DateTime.UtcNow.ToString("o"),
        Kind = kind,
        Payload = new Dictionary<string, object> { ["note"] = "test" },
    });

    /// <summary><b>The regression.</b> A board.start from a killed session, followed by a fresh injector
    /// startup, must NOT read as live — otherwise quick-start skips entry forever.</summary>
    [Fact]
    public void A_board_start_from_a_previous_injector_session_is_not_live()
    {
        Insert("injector.hello");
        Insert("board.start");      // the game was then killed: no board.end ever written
        Insert("injector.hello");   // ...and a new injector session started

        Assert.Null(DebugEndpoints.FindLatestLiveBoardStart(_store));
    }

    /// <summary>The positive control: a board.start in the CURRENT session is live. Without this, the
    /// test above would pass on a function that always returns null.</summary>
    [Fact]
    public void A_board_start_after_the_newest_injector_hello_is_live()
    {
        Insert("injector.hello");
        var id = Insert("board.start");

        var found = DebugEndpoints.FindLatestLiveBoardStart(_store);

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    /// <summary>A cleanly ended board is not live — the rule that already existed, kept covered so the
    /// new session check cannot be mistaken for the only thing this function does.</summary>
    [Fact]
    public void A_board_that_ended_cleanly_is_not_live()
    {
        Insert("injector.hello");
        Insert("board.start");
        Insert("board.end");

        Assert.Null(DebugEndpoints.FindLatestLiveBoardStart(_store));
    }

    /// <summary>No <c>injector.hello</c> at all (an old store, or one whose hello has aged out of the
    /// scan window) must not make a real board unreadable — the check only ever <i>rejects</i> a start
    /// that is provably older than a known session boundary.</summary>
    [Fact]
    public void With_no_injector_hello_a_board_start_is_still_live()
    {
        var id = Insert("board.start");

        var found = DebugEndpoints.FindLatestLiveBoardStart(_store);

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    /// <summary>The realistic recovery path: after the stale row, a real entry in the current session
    /// produces a live board again.</summary>
    [Fact]
    public void A_fresh_board_start_after_a_stale_one_reads_live_again()
    {
        Insert("injector.hello");
        Insert("board.start");      // stale, never ended
        Insert("injector.hello");   // new session
        var fresh = Insert("board.start");

        var found = DebugEndpoints.FindLatestLiveBoardStart(_store);

        Assert.NotNull(found);
        Assert.Equal(fresh, found!.Id);
    }

    /// <summary>
    /// <b>The false NEGATIVE the session rule itself introduced, found live within minutes of adding
    /// it.</b> A <b>server</b> restart makes the injector re-Hello from the <i>same</i> game process
    /// with the <i>same</i> live board — so a fresh `injector.hello` lands after a board.start that is
    /// still genuinely live, and the session rule wrongly calls it stale. Live symptom:
    /// <c>"enter-level reported board already live, but no live board.start was found"</c>.
    ///
    /// <para>The injector holds the real Board and outranks this event-log heuristic, so when it has
    /// just said "board already live" the filter is skipped.</para>
    /// </summary>
    [Fact]
    public void A_server_restart_does_not_make_a_still_live_board_look_stale_when_the_injector_says_otherwise()
    {
        Insert("injector.hello");
        var live = Insert("board.start");   // real board, still running
        Insert("injector.hello");           // SERVER restarted; same game re-Hello'd

        // Default (heuristic only): correctly conservative — it cannot tell this from a dead session.
        Assert.Null(DebugEndpoints.FindLatestLiveBoardStart(_store));

        // But when the injector itself reports a live board, it wins.
        var found = DebugEndpoints.FindLatestLiveBoardStart(_store, trustInjectorLiveBoard: true);
        Assert.NotNull(found);
        Assert.Equal(live, found!.Id);
    }

    /// <summary>Trusting the injector must not resurrect a board that ENDED cleanly — the
    /// board.end rule is independent of the session rule and still applies.</summary>
    [Fact]
    public void Trusting_the_injector_still_respects_a_clean_board_end()
    {
        Insert("injector.hello");
        Insert("board.start");
        Insert("board.end");

        Assert.Null(DebugEndpoints.FindLatestLiveBoardStart(_store, trustInjectorLiveBoard: true));
    }
}
