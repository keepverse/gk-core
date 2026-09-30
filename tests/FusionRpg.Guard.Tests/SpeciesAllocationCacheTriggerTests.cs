using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `solid-remediation` T4.2 — the FULL trigger set of the injector's species-allocation cache, per
/// DESIGN-GATE §2 invariant 16: an edge-refreshed cache must enumerate every trigger that can
/// invalidate it, each with its own test, INCLUDING the edge where its key set moves.
///
/// <para><b>This trigger set is derived from the species cache's own behaviour, and is deliberately
/// NOT a copy of a sibling's.</b> That is the named trap in §2.16, and the two caches next door are
/// both genuinely different:</para>
/// <list type="bullet">
///   <item><description>The <b>unique</b> cache (<c>_uniqueAllocations</c>) is keyed by CURRENTLY-Bound
///   instanceIds, so its key set moves on a bind — a 4th trigger the species cache does not have and
///   must not grow. That edge was missing once and shipped a real defect (2026-09-07,
///   allocate-then-deploy produced an unbuffed actor); it is covered by
///   <c>UniqueAptitudeRefreshCadenceTests</c>.</description></item>
///   <item><description>The <b>commander</b> cache resolves through
///   <c>MatchCommanderSnapshotHolder</c>, so a match edge changes what it answers with no fetch at
///   all — which is why <c>MatchHost</c> calls <c>RefreshCommanderAllocationCache()</c> on those
///   edges. The species cache has no match-scoped override, so a match edge is NOT a species
///   trigger. Copying the commander set would add a trigger that cannot fire.</description></item>
/// </list>
///
/// <para><b>Structural, by necessity.</b> <c>RpgClient</c> has no HTTP seam to mock and the injector
/// assembly's own test project is not in CI (AGENTS.md), so these live here, where CI runs them.
/// They prove the WIRING exists — the same idiom as <c>UniqueAptitudeRefreshCadenceTests</c> and
/// <c>LawnElementResolverTests</c>. End-to-end proof needs the live game per
/// `live-probe-standard.md`.</para>
/// </summary>
[Trait("guard", "species-cache-triggers")]
public class SpeciesAllocationCacheTriggerTests
{
    // ---- the three triggers that refresh the species cache ------------------------------------
    //
    // All three reach the cache through ONE fetch: `RefreshCommanderAllocationAsync` parses the
    // `species` map out of the same `/api/aptitudes/{playerId}` response it already reads `shares`
    // from. So proving trigger N calls that method, plus proving that method applies the species
    // map, is the whole path — there is no second entry point to miss.

    [Fact]
    public void Trigger1_session_start_refreshes_the_species_cache()
    {
        var body = MethodBody(InjectorSource("RpgClient.cs"), "public async Task StartAsync()");
        Assert.Contains("RefreshCommanderAllocationAsync()", body);
    }

    [Fact]
    public void Trigger2_signalr_reconnect_refreshes_the_species_cache()
    {
        var source = InjectorSource("RpgClient.cs");
        var start = source.IndexOf("_hub.Reconnected +=", StringComparison.Ordinal);
        Assert.True(start >= 0, "missing _hub.Reconnected handler");
        var end = source.IndexOf("SignalR reconnected + re-joined", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not bound the Reconnected handler body");
        Assert.Contains("RefreshCommanderAllocationAsync()", source[start..end]);
    }

    [Fact]
    public void Trigger3_the_AptitudesUpdated_broadcast_refreshes_the_species_cache()
    {
        // The server's "AptitudesUpdated" SignalR broadcast enqueues the
        // "aptitudes.allocation.reload" command, which CheatCommandRunner handles — the two halves
        // of trigger 3, so both are asserted rather than just the handler.
        Assert.Contains("aptitudes.allocation.reload", InjectorSource("RpgClient.cs"), StringComparison.Ordinal);

        var runner = InjectorSource("CheatCommandRunner.cs");
        var idx = runner.IndexOf("aptitudes.allocation.reload", StringComparison.Ordinal);
        Assert.True(idx >= 0, "CheatCommandRunner does not handle aptitudes.allocation.reload");
        Assert.Contains("RefreshCommanderAllocationAsync()", runner[idx..Math.Min(runner.Length, idx + 800)]);
    }

    [Fact]
    public void All_three_triggers_reach_the_species_map_through_the_one_fetch_that_parses_it()
    {
        // The join that makes the three tests above sufficient. If the species parse ever moved out
        // of this method into its own fetch, the three triggers would still pass while the species
        // cache silently stopped refreshing — so assert the parse and the apply live HERE.
        var body = MethodBody(InjectorSource("RpgClient.cs"), "public async Task RefreshCommanderAllocationAsync()");
        Assert.Contains("\"species\"", body, StringComparison.Ordinal);
        Assert.Contains("ApplySpeciesAllocations(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void All_three_triggers_also_reach_speciesLayers_through_the_same_one_fetch()
    {
        // species-progression step 6.2, injector cache (SP6.4) — extends the join test above: the
        // SAME `RefreshCommanderAllocationAsync` fetch that already proved sufficient for `species`
        // must also carry `speciesLayers`, or triggers 1-3 would silently stop refreshing THIS cache
        // while still passing for the old one.
        var body = MethodBody(InjectorSource("RpgClient.cs"), "public async Task RefreshCommanderAllocationAsync()");
        Assert.Contains("\"speciesLayers\"", body, StringComparison.Ordinal);
        Assert.Contains("ApplySpeciesLayers(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_a_refresh_invalidates_live_stats_so_it_reaches_entities_already_spawned()
    {
        // The 2026-08-30 owner-caught defect, in its species form: a refreshed cache that does not
        // invalidate only reaches entities spawned AFTER it. §2.16 lists that incident by name.
        var body = MethodBody(InjectorSource("CheatState.cs"), "public static void ApplySpeciesAllocations(");
        Assert.Contains("Stats.Invalidate()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refresh_replaces_the_whole_cache_rather_than_merging_into_it()
    {
        // Wholesale replace is what makes "a row the player no longer has" impossible to leave
        // behind — an incremental merge would need a deletion trigger this set does not have.
        var body = MethodBody(InjectorSource("CheatState.cs"), "public static void ApplySpeciesAllocations(");
        Assert.Contains("_speciesAllocations = bySpeciesId", body, StringComparison.Ordinal);
    }

    // ---- the key-set edge ----------------------------------------------------------------------

    [Fact]
    public void The_key_set_edge_is_resolved_per_read_and_therefore_needs_no_fourth_trigger()
    {
        // T4.1 added the empire to the species key. §2.16's question is then: when does the KEY SET
        // move, and what fires then? Answer: never on a state change, because the empire is derived
        // from `ctx.Side` at read time and is not part of what the dictionary stores. An entity does
        // not "enter" an empire the way a specimen enters Bound — its Side is fixed for the read.
        //
        // This test pins that reasoning to code. If the empire ever becomes cached state rather
        // than a per-read derivation, this fails, and whoever makes that change owes this cache a
        // 4th trigger and a test for it.
        var core = CoreSource(Path.Combine("Stats", "Aptitudes", "SpeciesAllocationSource.cs"));
        var body = MethodBody(core, "public AptitudeAllocation Resolve(StatContext ctx)");
        Assert.Contains("EmpireForSide(ctx.Side)", body, StringComparison.Ordinal);
        Assert.Contains("_resolveSpeciesAllocation(empire,", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cache_answers_each_side_from_its_own_empire_and_never_the_other()
    {
        // species-progression step 6.2, trigger 6 (SP6.6) — REPLACES
        // The_cache_holds_exactly_one_empires_rows_and_refuses_to_answer_for_another (T4.2), which
        // pinned the OLD _speciesAllocations cache's own real limit at the time: Dave only, since
        // `/api/aptitudes/{playerId}` returned only the player's own species map, and
        // SpeciesAllocation's own resolveSpeciesAllocation delegate explicitly hardcoded
        // `empire == EmpireId.Dave`. That is no longer the honest current shape: the NEWER
        // speciesLayers cache (SP6.4) is fed from the SAME fetch's `speciesLayers.mod` field, which
        // SP6.3 keys by EVERY empire of the save that has ledger rows — so `SpeciesLayer`'s own
        // resolveModRows delegate looks up `empire.Value` generically, never hardcoding one side.
        var body = InitializerBody(
            InjectorSource("CheatState.cs"),
            "public static readonly FusionRpg.Core.Stats.Aptitudes.SpeciesLayerSource SpeciesLayer");
        Assert.Contains("resolveModRows: (empire, speciesId) =>", body, StringComparison.Ordinal);
        Assert.Contains("_speciesLayersMod.TryGetValue(empire.Value, out var perSpecies)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EmpireId.Dave", body, StringComparison.Ordinal); // never hardcoded to one side
    }

    [Fact]
    public void A_match_edge_is_not_a_species_trigger_and_the_trigger_set_was_not_copied()
    {
        // §2.16's actual instruction: do not copy a trigger set from a cache with different key-set
        // behaviour. `MatchHost`'s match edges refresh the COMMANDER cache, because that one
        // resolves through MatchCommanderSnapshotHolder and a match changes its answer with no
        // fetch. The species cache has no match-scoped override, so adding a match-edge species
        // fetch would be a trigger that cannot fire — noise that makes the real set harder to read.
        //
        // AMENDED (SP6.6, trigger 6): except the `board.start` after a mid-run save switch — the ONE
        // real match-edge exception, named here rather than silently reintroducing the "copied
        // trigger set" defect this test's whole point is to catch. `ApplySpeciesAllocations`/
        // `ApplySpeciesLayers` are still never called DIRECTLY from MatchHost.cs (that assertion
        // below is unchanged); the deferred refresh only ENQUEUES the SAME command the SignalR
        // handler already enqueues, at the one moment (board.start) a mid-run switch is allowed to
        // apply.
        var matchHost = InjectorSource(Path.Combine("Match", "MatchHost.cs"));
        Assert.Contains("RefreshCommanderAllocationCache()", matchHost, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySpeciesAllocations", matchHost, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySpeciesLayers", matchHost, StringComparison.Ordinal);
        Assert.Contains("_pendingSaveSwitchRefresh", matchHost, StringComparison.Ordinal);

        // And the reason that is correct, not an omission: the commander source resolves through the
        // match snapshot holder; the species dictionary is replaced only by the transport.
        var cheatState = InjectorSource("CheatState.cs");
        Assert.Contains("MatchCommanderSnapshotHolder.ResolveAllocation", cheatState, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------

    static string InjectorSource(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", relative));

    static string CoreSource(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", relative));

    /// <summary>Brace-matched body of the declaration whose text starts with <paramref name="signature"/>.
    /// Deliberately not a regex: a regex over C# braces is the kind of thing that silently matches the
    /// wrong closing brace and turns a real failure green.</summary>
    static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"declaration not found: {signature}");
        var open = source.IndexOf('{', start);
        Assert.True(open > start, $"no opening brace after: {signature}");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[open..(i + 1)];
            }
        }
        throw new InvalidOperationException($"unbalanced braces after: {signature}");
    }

    /// <summary>Paren-matched initializer of a field declared as <c>... Name = new(...);</c>. A field
    /// initializer is not brace-delimited, so <see cref="MethodBody"/> would match some later block
    /// entirely — the kind of near-miss that reads as a passing test.</summary>
    static string InitializerBody(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"declaration not found: {declaration}");
        var open = source.IndexOf('(', start);
        Assert.True(open > start, $"no opening paren after: {declaration}");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0) return source[open..(i + 1)];
            }
        }
        throw new InvalidOperationException($"unbalanced parens after: {declaration}");
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
