using System.Linq;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// RS-CF2 (`tasks/rpg-simulator-todo.md`): `POST /api/unique/actors/{instanceId}/xp` answered <b>500</b>
/// for roughly one specimen in eight, so `UniqueEquipmentE2ETests.Award_xp_levels_and_refuses_retired`
/// failed in some runs and passed in others.
///
/// <para><b>The cause this test pins.</b> A level gain rolls the unlock ladder once
/// (`RpgStore.TryRollActionUnlocks`). The roll's candidate pool counted every <i>eligible</i> row —
/// eligibility answers "who may hold this action" (general / family / species), never "may this be
/// granted" — so a catalog that also held <c>act.attack</c> (<c>kind = basic</c>, which
/// `ActionValidator.ValidateGrant` refuses by construction) put an un-grantable row in the draw. When
/// the seeded pick landed on it, the wiring's grant delegate threw
/// <c>InvalidOperationException: action unlock grant refused: BasicCollision</c> inside the XP award's
/// own transaction, and the route answered 500. It fired only <i>sometimes</i> because the pick is
/// seeded off the specimen's freshly minted instance id: measured on this host, 7 of 40 one-level
/// awards answered 500 before the fix, 0 of 40 after.</para>
///
/// <para><b>Why the precondition is part of the arrange.</b> The shared host's store only holds
/// drawable action rows once atoms are present — the boot import refuses rows whose container atoms it
/// cannot resolve — so the corpus is brought in through the real import path (the same arrangement
/// `ActionBudgetReportTests.SeedThroughTheRealImportPath` establishes, and the same one that made this
/// defect reachable in the suite at all). The presence of a row the grant refuses is then <b>asserted,
/// never assumed</b>: without it the loop below would pass because the pool is empty (a legal no-op),
/// not because the contract holds.</para>
///
/// <para>⛔ Not a retry, not a skip, and not a widened validator: the route, the store, the tuning and
/// the roll are all real, and the fix is that a refused row is no longer a drawable candidate.</para>
/// </summary>
[Collection("e2e")]
public class UniqueActorXpUnlockRollTests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly RpgApiFactory _factory;
    readonly HttpClient _http;
    readonly RpgStore _store;

    public UniqueActorXpUnlockRollTests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
        // The host's OWN store, resolved from its DI container -- the same instance the route writes
        // through, never a second handle opened on the same substrate. It is also the only accessor
        // that holds whatever data plan the fixture runs on (file or shared memory), so this test
        // never depends on `DataDir` existing.
        _store = factory.Services.GetRequiredService<RpgStore>();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Awarding_a_level_never_draws_an_action_a_grant_would_refuse()
    {
        var store = _store;
        SeedThroughTheRealImportPath(store);

        var catalog = store.ListActionIds()
            .Select(store.GetAction)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
        Assert.Contains(catalog, a => !ActionValidator.ValidateGrantable(a).IsOk);

        // 40 fresh specimens, one level crossed each: the pool of a fresh specimen is the whole
        // catalog, so the draw is a real one every time rather than a single lucky roll.
        for (var i = 0; i < 40; i++)
        {
            var create = await _http.PostAsJsonAsync("/api/unique/actors", new { side = "zombie", typeId = 2 });
            create.EnsureSuccessStatusCode();
            var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>(Json);
            Assert.NotNull(actor);

            var xp = await _http.PostAsJsonAsync($"/api/unique/actors/{actor!.InstanceId}/xp",
                new { delta = 150.0, reason = "e2e" });
            if (!xp.IsSuccessStatusCode)
            {
                var body = await xp.Content.ReadAsStringAsync();
                Assert.Fail($"specimen {actor.InstanceId}: xp answered {(int)xp.StatusCode}\n{body}");
            }

            // The contract, asserted on the state the roll wrote — not only on the status code: no
            // specimen may ever hold an action a grant refuses.
            foreach (var grant in store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId)))
            {
                var granted = store.GetAction(grant.ActionId);
                Assert.NotNull(granted);
                Assert.True(ActionValidator.ValidateGrantable(granted!).IsOk,
                    $"specimen {actor.InstanceId} holds an un-grantable action: {grant.ActionId}");
            }
        }
    }

    /// <summary>The real import path — atoms first, then the committed briefs — the same arrangement
    /// `ActionBudgetReportTests.SeedThroughTheRealImportPath` establishes on this shared store, so this
    /// test's precondition is that test's precondition, not a fixture of its own.</summary>
    static void SeedThroughTheRealImportPath(RpgStore store)
    {
        var root = RepoRoot();
        var atomsPath = Path.Combine(root, "data", "seed", "atoms", "generated", "family-expand.g-life.json");
        var collected = AtomSeedFile.Collect(new[] { (atomsPath, File.ReadAllText(atomsPath)) });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        store.UpsertAtoms(collected.Content.Atoms);

        if (store.ListActionIds().Count > 0) return;

        var template = ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(
            Path.Combine(root, "data", "tuning", "action-corpus-cost-templates.v2.json")));

        var briefs = new List<ActionCorpusBrief>();
        foreach (var file in new[] { "committed-round-1.json", "committed-round-2.json", "authored-basics.json" })
        {
            var path = Path.Combine(root, "data", "seed", "actions", file);
            if (File.Exists(path)) briefs.AddRange(ActionCorpusBriefJson.Parse(File.ReadAllText(path)));
        }

        ActionCorpusImporter.Import(store, briefs, template, FusionRpg.Core.Actions.Rungs.RungPolicy.Table);
    }

    static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("could not find the repo root above the E2E test sources");
    }
}
