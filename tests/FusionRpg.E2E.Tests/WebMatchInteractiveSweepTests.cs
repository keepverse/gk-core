using FusionRpg.Core.Battle;
using FusionRpg.Data;
using FusionRpg.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// party-dungeon D2.16 — the boot sweep must never finish a steered party on autopilot across a
/// restart. `WebMatchService.IsInteractive` reads a row's own `profile_id` (D2.15) directly via
/// `BattleModeProfileCatalog.Resolve`, because a delve row's `wave_id` is an encounter-anchor id
/// `WaveCatalog` has never heard of and the ORIGINAL wave-lookup path could never see it as
/// interactive on its own — regression coverage for the exact gap found while wiring D2.16.
/// </summary>
[Collection("e2e")]
public class WebMatchInteractiveSweepTests
{
    readonly RpgStore _store;
    readonly WebMatchService _matches;

    public WebMatchInteractiveSweepTests(RpgApiFactory factory)
    {
        _store = factory.Services.GetRequiredService<RpgStore>();
        _matches = factory.Services.GetRequiredService<WebMatchService>();
    }

    /// <summary>
    /// An unresolved log row carrying a <b>resolvable</b> setup (B40.6) — a real wave and a real squad
    /// from the production <c>BuildSquad</c>, so a test whose subject is the interactive bar or the
    /// trace can observe a genuine heal. Pass <paramref name="waveId"/> to exercise the unresolvable-wave
    /// path with the squad still real.
    /// </summary>
    (long Id, string Corr) Unresolved(string tag, string? profileId, string? decisionsJson = null,
        string waveId = SweepSetupFixture.KnownWaveId) =>
        SweepSetupFixture.AppendUnresolved(
            _store, _matches, "interactive-", tag, _store.ComputeContentHash().ToCompact(),
            profileId, decisionsJson, waveId);

    string? RefusalFor(string corr) => _store.TryGetWebMatchLog(1, corr)?.SweepRefused;

    [Fact]
    public void A_delve_profiled_row_with_no_decision_trace_is_refused()
    {
        var row = Unresolved("no-trace", profileId: "delve");

        _matches.SweepUnresolved();

        Assert.NotNull(RefusalFor(row.Corr));
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id); // terminal
    }

    /// <summary>⛔ The exact hole found while wiring D2.16: BEFORE the fix, `IsInteractive` looked only
    /// at the wave (which a delve row's synthetic id can never resolve), so this row would have been
    /// silently healed by `ResolveAndIngest` on the default AI policy — finishing a steered fight on
    /// autopilot across a restart. A trace being present does not make that safe either:
    /// `ResolveAndIngest` has no path to replay it or to pick this row's own profile.</summary>
    [Fact]
    public void A_delve_profiled_row_with_a_partial_decision_trace_is_still_refused_not_silently_healed()
    {
        var row = Unresolved("with-trace", profileId: "delve", decisionsJson: "[]");

        _matches.SweepUnresolved();

        Assert.NotNull(RefusalFor(row.Corr));
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id);
    }

    [Fact]
    public void A_row_with_no_profile_id_and_an_unresolvable_wave_is_not_treated_as_interactive()
    {
        // Baseline: a match whose setup names a wave the catalog cannot resolve must keep its exact
        // prior behaviour — an unknown WaveId resolves to no profile at all (`ProfileForWave` returns
        // null for an unknown id), which is not `RequiresLiveInput`, so the row is healed, not refused.
        //
        // B40.3: the setup must be RESOLVABLE in every other respect (a real squad), or the sweep
        // fails on "Squad is empty." before the profile question is ever reached — the old `{}` row
        // could never heal, so this test silently observed a resolve failure while claiming to test
        // the interactive bar. WaveId is the only unresolvable part on purpose.
        var row = Unresolved("legacy", profileId: null, waveId: "wave-that-does-not-exist");

        _matches.SweepUnresolved();

        Assert.Null(RefusalFor(row.Corr));
        // And it genuinely HEALED — the assertion the old empty setup could never satisfy.
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id);
    }

    [Fact]
    public void A_row_with_an_unrecognised_profile_id_is_refused_rather_than_crashing_the_sweep()
    {
        var bad = Unresolved("garbage-profile", profileId: "not-a-real-profile");
        // A SECOND unrecognised-profile row, created AFTER the first. If the sweep threw out of `bad`
        // instead of refusing it and continuing, this row would never be reached and would stay
        // unrefused — so its refusal is what proves the loop continued past the bad row.
        //
        // This replaces an earlier claim that a following row was "actually HEALED": that row was
        // created with `setupJson: "{}"`, and an empty squad can never heal — `ResolveAndIngest`
        // throws "Squad is empty." and `SweepUnresolved`'s catch logs but marks nothing, so such a row
        // is neither refused nor healed and could not discriminate a continued loop from an aborted
        // one (which is why the assertion failed). A second *refusal* proves the same property with a
        // shape production actually produces. (Production never logs an empty-squad row either:
        // `BuildSquad` always falls back to a deterministic synthetic squad.)
        var alsoBad = Unresolved("after-garbage", profileId: "also-not-a-real-profile");

        _matches.SweepUnresolved(); // must not throw and abort before reaching the later row

        Assert.NotNull(RefusalFor(bad.Corr));
        Assert.NotNull(RefusalFor(alsoBad.Corr));
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == alsoBad.Id); // terminal
    }

    // ── B40.1: the sweep must refuse a DETERMINISTIC resolve failure and retry a TRANSIENT one ──
    //
    // Before B40.1 the sweep's catch logged a resolve failure and marked nothing, so a row whose
    // setup can never resolve was neither refused nor healed — re-listed every boot forever. That is
    // the hazard `WebMatchService.SweepUnresolved`'s own comment names for refusals: enough unmarked
    // rows crowd every newer row out of the `ORDER BY id ASC LIMIT` window, so crash recovery dies
    // silently while still reporting a clean sweep. `spec-interactive-turns.md` §4 states the rule for
    // the trace case ("The sweep must refuse, not heal"); B40.1 applies it to the resolve case.
    //
    // Reachable across a deploy, not from one build: `setupJson` is authored by the PREVIOUS build,
    // while `WaveCatalog` is code-authored and NOT covered by the content hash and
    // `BattleEngine.ValidateActorKey`'s rules can tighten — so an older row can deterministically fail
    // under newer code.

    /// <summary>
    /// A deterministic failure — the row's own persisted setup is unusable ("Squad is empty.") — is
    /// marked refused and leaves the window. This is the case a production row reaches only across a
    /// deploy, but it is exactly "the data itself is unusable", which is what the refusal marks.
    /// </summary>
    [Fact]
    public void A_row_whose_setup_can_never_resolve_is_refused_terminally()
    {
        // Deliberately the old empty-setup shape: this is the row B40.1 is ABOUT, so it is the one
        // place `{}` is still the correct input.
        var corr = "interactive-unresolvable-" + Guid.NewGuid().ToString("N")[..8];
        var (created, entry) = _store.AppendWebMatchLog(
            playerId: 1, correlationId: corr, matchKey: "match-" + corr,
            setupJson: "{}", seed: 7,
            engineVersion: BattleRuleset.EngineVersion,
            rulesetVersion: BattleRuleset.RulesetVersion,
            rngAlgoVersion: SeededRng.RngAlgoVersion,
            environmentStamp: BattleEnvironment.Stamp,
            contentHash: _store.ComputeContentHash().ToCompact());
        Assert.True(created);

        _matches.SweepUnresolved();

        var refusal = RefusalFor(corr);
        Assert.NotNull(refusal);
        Assert.Contains("Squad is empty", refusal!); // the reason names the real failure
        // Terminal: it leaves the window, so a second sweep cannot pick it up again.
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == entry.Id);

        var healedAfterSecondSweep = _matches.SweepUnresolved();
        Assert.Equal(0, healedAfterSecondSweep); // nothing left to re-resolve
    }

    /// <summary>
    /// The contrast that proves the split matters: a row whose setup resolves is NOT refused — it
    /// heals. Without this, "everything is refused" would also pass the test above.
    /// </summary>
    [Fact]
    public void A_resolvable_row_is_healed_not_refused_so_the_refusal_is_not_blanket()
    {
        var row = Unresolved("resolvable", profileId: null);

        _matches.SweepUnresolved();

        Assert.Null(RefusalFor(row.Corr));
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id);
    }
}
