using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// species-progression module 6 (`species-layer-delivery`) step 6.2, injector cache (SP6.4) — the
/// `speciesLayers` cache's own wholesale-replace + invalidate contract, and the wire converter every
/// row goes through. `RpgClient` has no HTTP seam to mock and the injector assembly's own test project
/// is not in CI (AGENTS.md), so these live here as source-text scans, the SAME idiom
/// `SpeciesAllocationCacheTriggerTests` already establishes for the sibling species-allocation cache
/// (whose triggers 1-3 this task ALSO extends, in that file, to prove the SAME one fetch carries both
/// caches). End-to-end proof needs the live game per `live-probe-standard.md`.
///
/// <para>The delegate's own answer shape (a general `Species` answer gets 1a+1b of its side's empire;
/// a `Specimen` answer gets 1a+1b of its OWNER empire, never 2b; a `None` answer gets nothing) is
/// proven in Core, not here — `SpeciesLayerSourceTests` exercises `SpeciesLayerSource.Resolve` with
/// fake resolvers, and needs no live game or text scan to do it.</para>
/// </summary>
[Trait("guard", "species-layer-cache-triggers")]
public class SpeciesLayerCacheTriggerTests
{
    [Fact]
    public void A_refresh_replaces_the_whole_layer_cache_rather_than_merging_into_it()
    {
        // Wholesale replace is what makes "a row the player no longer has" impossible to leave
        // behind — matching ApplySpeciesAllocations' own contract exactly, for both dictionaries this
        // cache carries.
        var body = MethodBody(InjectorSource("CheatState.cs"), "public static void ApplySpeciesLayers(");
        Assert.Contains("_speciesLayersBase = baseBySpeciesId", body, StringComparison.Ordinal);
        Assert.Contains("_speciesLayersMod = modByEmpireThenSpecies", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_species_layers_invalidates_live_stats()
    {
        // The 2026-08-30 owner-caught defect, in this cache's own form: a refreshed cache that does
        // not invalidate only reaches entities spawned AFTER it.
        var body = MethodBody(InjectorSource("CheatState.cs"), "public static void ApplySpeciesLayers(");
        Assert.Contains("Stats.Invalidate()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transport_parses_speciesLayers_base_and_mod_through_the_one_wire_converter()
    {
        // Both the server's emit (AptitudeEndpoints.ProjectSpeciesLayers) and this parse must agree on
        // the SAME wire shape -- ProjectedLayerRowJson.FromWire is the one place that agreement lives,
        // never a second hand-rolled parse that could silently drift from the server's own ToWire.
        var body = InjectorSource("RpgClient.cs");
        Assert.Contains("\"base\"", body, StringComparison.Ordinal);
        Assert.Contains("\"mod\"", body, StringComparison.Ordinal);
        Assert.Contains("ProjectedLayerRowJson.FromWire(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_speciesLayer_delegate_reuses_the_same_species_lookup_and_bound_specimen_resolvers()
    {
        // Never a second copy of the species-lookup / Bound-instance / specimen-owner plumbing
        // SpeciesAllocation already established -- a second copy is exactly the kind of drift
        // §2.16 warns a copied trigger set invites.
        var body = InitializerBody(
            InjectorSource("CheatState.cs"),
            "public static readonly FusionRpg.Core.Stats.Aptitudes.SpeciesLayerSource SpeciesLayer");
        Assert.Contains("resolveSpeciesId: ResolveSpeciesLookup", body, StringComparison.Ordinal);
        Assert.Contains("resolveBoundInstanceId: ResolveBoundInstanceId", body, StringComparison.Ordinal);
        Assert.Contains("resolveSpecimenOwnerEmpire: TryGetSpecimenEmpire", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_speciesLayer_delegate_is_wired_into_the_registered_ActorHub()
    {
        var body = InjectorSource("CheatState.cs");
        Assert.Contains("speciesLayers: SpeciesLayer.Resolve", body, StringComparison.Ordinal);
    }

    // ---- triggers 4 and 5 (SP6.5) -- the server-side emission this cache's refresh depends on -----

    [Fact]
    public void A_species_level_up_mid_run_reaches_actors_already_on_the_board()
    {
        // Trigger 4: EventIngest's own progression dirty-set loop broadcasts AptitudesUpdated(scope:
        // "species") through the ONE shared emitter whenever the dirty set holds a Species-kind level
        // change -- which the injector's existing trigger-3 handler already turns into a full
        // RefreshCommanderAllocationAsync() re-fetch (carrying speciesLayers since SP6.4), reaching
        // actors already spawned without a reconnect.
        var body = MethodBody(ServerSource("EventIngest.cs"), "async Task BroadcastProgressionAsync(IReadOnlyList<RpgProgressionDirty> dirty)");
        Assert.Contains("RpgActorKinds.Species", body, StringComparison.Ordinal);
        Assert.Contains("AptitudeEndpoints.BroadcastBestEffort(", body, StringComparison.Ordinal);
        Assert.Contains("\"species\"", body, StringComparison.Ordinal);
    }

    // ---- trigger 6 (SP6.6) -- the save-switch key-set edge, and the mid-run deferral ---------------

    [Fact]
    public void A_save_switch_replaces_every_species_layer_row()
    {
        // The server side: PUT /api/players/current broadcasts AptitudesUpdated(scope: "save")
        // through the ONE existing emitter (no second route, no second key). The injector side reuses
        // the SAME wholesale-replace ApplySpeciesLayers/ApplySpeciesAllocations both already have --
        // proven once, structurally, by those methods' own tests (A_refresh_replaces_the_whole_layer_cache,
        // A_refresh_replaces_the_whole_cache_rather_than_merging_into_it); a wholesale replace is
        // order-independent by construction, so "hydrate then switch" and "switch then hydrate" both
        // converge to the SAME final state without a separate test needed per order.
        var program = ServerSource("Program.cs");
        Assert.Contains("MapPut(\"/api/players/current\"", program, StringComparison.Ordinal);
        Assert.Contains("AptitudeEndpoints.BroadcastBestEffort(hub,", program, StringComparison.Ordinal);
        Assert.Contains("\"save\"", program, StringComparison.Ordinal);

        var rpgClient = InjectorSource("RpgClient.cs");
        Assert.Contains("AptitudesUpdatedScopeDto", rpgClient, StringComparison.Ordinal);
        Assert.Contains("\"save\"", rpgClient, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mid_run_save_switch_keeps_the_runs_rows_until_the_run_ends()
    {
        // decisions.md "Mid-match switch: open run keeps the player it started with" — a save-switch
        // notice arriving mid-run must NOT apply immediately. RpgClient defers when a run is open;
        // MatchHost consumes the deferred flag exactly once, at the NEXT board.start (never board.end
        // or any other match edge), matching AptitudeSubsystem's own "the memo self-corrects, no
        // explicit bump needed elsewhere" discipline applied here to a cross-type flag instead.
        var rpgClient = InjectorSource("RpgClient.cs");
        Assert.Contains("MatchHost.IsRunOpen", rpgClient, StringComparison.Ordinal);
        Assert.Contains("MatchHost.RequestDeferredSaveSwitchRefresh()", rpgClient, StringComparison.Ordinal);

        var matchHost = InjectorSource(Path.Combine("Match", "MatchHost.cs"));
        var isStartBody = MethodBody(matchHost, "public static void Apply(string kind, IReadOnlyDictionary<string, object>? payload)");
        Assert.Contains("_pendingSaveSwitchRefresh = false", isStartBody, StringComparison.Ordinal);
        Assert.Contains("aptitudes.allocation.reload", isStartBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fusion_pick_reaches_actors_already_spawned()
    {
        // Trigger 5: a real (non-replayed) /api/fusion/execute appends a 1b ledger row and broadcasts
        // through the SAME emitter, gated on !Replayed exactly like the pre-existing
        // CreaturesUpdated/SoulsUpdated broadcasts in the same block (a replay appends no new row).
        var body = MethodBody(ServerSource("FusionEndpoints.cs"), "if (!outcome!.Replayed)");
        Assert.Contains("AptitudeEndpoints.BroadcastBestEffort(", body, StringComparison.Ordinal);
        Assert.Contains("\"species\"", body, StringComparison.Ordinal);
    }

    // ---- helpers (mirrors SpeciesAllocationCacheTriggerTests.cs's own, deliberately not shared:
    // that file's own comment explains why a copied helper would be the wrong kind of coupling for a
    // guard test whose whole point is independence from the thing it checks) -----------------------

    static string InjectorSource(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FusionRpg.Injector", relative));

    static string ServerSource(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FusionRpg.Server", relative));

    /// <summary>Brace-matched body of the declaration whose text starts with <paramref name="signature"/>.</summary>
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

    /// <summary>Paren-matched initializer of a field declared as <c>... Name = new(...);</c>.</summary>
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
