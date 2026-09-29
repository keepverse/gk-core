using System;
using System.IO;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `ai-empire-species` EP4.18 (R23) — the WIRING of the per-empire commander pool, end to end, as a
/// structural proof.
///
/// <para><b>Why structural.</b> The Injector's sources are compiled only by a host whose refs are
/// present (`FusionRpg.Injector.MelonLoader.csproj` puts its whole `Compile Include` behind a
/// <c>HasMelonRefs</c> condition), so a machine without the game's loader refs cannot compile them at
/// all — and the Injector test project is not in CI (AGENTS.md). This is the same idiom
/// <c>SpeciesAllocationCacheTriggerTests</c> already established for exactly that reason: prove the
/// WIRING exists, and leave end-to-end proof to a live probe.</para>
/// </summary>
[Trait("guard", "commander-pool-transport")]
public class CommanderPoolTransportGuardTests
{
    [Fact]
    public void The_server_payload_carries_the_human_empire_and_a_per_empire_pool_map()
    {
        var endpoints = Source("src/FusionRpg.Server/AptitudeEndpoints.cs");
        Assert.Contains("humanEmpire = store.HumanEmpireOf(playerId).Value", endpoints, StringComparison.Ordinal);
        Assert.Contains("commanderByEmpire = ProjectCommanderPools(", endpoints, StringComparison.Ordinal);

        // The map is built through the ONE empire-keyed pool read at that empire's own Theta, and the
        // human empire is excluded (its pool is `shares`).
        var pools = MethodBody(endpoints, "static object ProjectCommanderPools(");
        Assert.Contains("powerIndex.ActorIndexFor(new SaveId(saveId), row.Empire)", pools, StringComparison.Ordinal);
        Assert.Contains("store.CommanderPoolOf(", pools, StringComparison.Ordinal);
        Assert.Contains("if (row.Empire == humanEmpire) continue;", pools, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transport_parses_both_fields_and_replaces_the_cache_wholesale()
    {
        var client = Source("src/FusionRpg.Injector/RpgClient.cs");
        Assert.Contains("TryGetProperty(\"humanEmpire\"", client, StringComparison.Ordinal);
        Assert.Contains("TryGetProperty(\"commanderByEmpire\"", client, StringComparison.Ordinal);
        // The SAME fetch every other cache rides (no second round trip), applied through the one seam.
        Assert.Contains("CheatState.ApplyCommanderPools(humanEmpire, commanderPoolsByEmpire)", client,
            StringComparison.Ordinal);

        var apply = MethodBody(Source("src/FusionRpg.Injector/CheatState.cs"), "public static void ApplyCommanderPools(");
        Assert.Contains("_commanderPoolsByEmpire = byEmpire", apply, StringComparison.Ordinal);
        Assert.Contains("Stats.Invalidate()", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_injector_resolves_the_owners_empire_and_never_borrows_the_humans()
    {
        var cheat = Source("src/FusionRpg.Injector/CheatState.cs");

        // The source's commander delegate is the empire-keyed read, not a player-scoped lambda.
        Assert.Contains("resolveCommanderAllocation: CommanderPoolFor,", cheat, StringComparison.Ordinal);

        var pool = MethodBody(cheat, "public static FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation CommanderPoolFor(");
        // The human empire keeps the match-scoped cache (an answer that changes on a match edge with no
        // fetch -- a pre-existing behaviour this must not take over) ...
        Assert.Contains("CommanderAllocation.Resolve(DummyStatContextForCommanderRead)", pool, StringComparison.Ordinal);
        // ... and an empire the payload does not carry answers Empty, NEVER the human's pool under
        // another empire's name (the S1 defect the delegate's EMPIRE argument exists to prevent).
        Assert.Contains("_commanderPoolsByEmpire.TryGetValue(empire.Value, out var pool)", pool, StringComparison.Ordinal);
        Assert.Contains(": FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;", pool, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_commander_consumer_reads_by_empire_not_by_player()
    {
        // `SpeciesAllocationSource`'s delegate takes the EMPIRE (the lawn general and the lawn Bound
        // unique both pass the actor's own), and the four consumers all read through it or through the
        // store's empire-keyed read -- no consumer still loads the human's scope key for an empire it
        // was not asked about.
        var source = Source("src/FusionRpg.Core/Stats/Aptitudes/SpeciesAllocationSource.cs");
        Assert.Contains("Func<Commanders.EmpireId, AptitudeAllocation> resolveCommanderAllocation", source,
            StringComparison.Ordinal);
        Assert.Contains("_resolveCommanderAllocation(owner)", source, StringComparison.Ordinal);
        Assert.Contains("_resolveCommanderAllocation(empire)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_resolveCommanderAllocation(ctx.PlayerId)", source, StringComparison.Ordinal);

        Assert.Contains("CommanderPoolForUnlocked(db, ownerRef)", Source("src/FusionRpg.Data/Sqlite/RpgStore.WorldTurns.cs"),
            StringComparison.Ordinal);
        Assert.Contains("store.CommanderPoolFor(", Source("src/FusionRpg.Server/UniqueActorHubCompose.cs"),
            StringComparison.Ordinal);
        Assert.Contains("_store.CommanderPoolFor(", Source("src/FusionRpg.Server/WebMatchService.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_selector_no_longer_gates_the_commander_term_to_the_human_empire()
    {
        // R23/R4 mirrored: which empire's pool the term is rides on `Empire`; whether it is empty is the
        // pool read's answer. The old `empire == humanEmpire` gate must not come back.
        var selector = Source("src/FusionRpg.Core/Stats/Aptitudes/ProgressionLayerSelector.cs");
        Assert.DoesNotContain("CarriesCommander, empire == humanEmpire", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("new(empire, empire == humanEmpire,", selector, StringComparison.Ordinal);
        Assert.Contains("new(empire, true,", selector, StringComparison.Ordinal);
    }

    static string Source(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Brace-matched body of the declaration whose text starts with <paramref name="signature"/>,
    /// plus a following tail for expression-bodied members — deliberately not a regex over C# braces.</summary>
    static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"declaration not found: {signature}");
        var open = source.IndexOf('{', start);
        var arrow = source.IndexOf("=>", start, StringComparison.Ordinal);
        if (arrow > start && (open < 0 || arrow < open))
        {
            var end = source.IndexOf("\n    ", arrow, StringComparison.Ordinal);
            return end < 0 ? source[start..] : source[start..end];
        }

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

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
