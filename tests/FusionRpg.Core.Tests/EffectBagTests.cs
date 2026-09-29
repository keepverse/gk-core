using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectBagTests
{
    static EffectEventDto Dealt(string actor = "0xA", string target = "0xB", long tick = 1) => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        MatchKey = "m1",
        Side = "plant",
        ActorPtr = actor,
        TargetPtr = target,
        TypeId = 0,
        TargetTypeId = 0,
        Damage = 20,
        Tick = tick
    };

    [Fact]
    public void Butter_on_hit_plans_ApplyStatus()
    {
        var h = new FoundationHarness(42);
        h.Grant(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var (plan, fired) = h.Run(Dealt());
        Assert.Single(plan.Actions);
        Assert.Equal(EffectActions.ApplyStatus, plan.Actions[0].Action);
        Assert.Equal("butter", plan.Actions[0].Params["status"]?.ToString());
        Assert.True(fired[0].Ok);
        Assert.Equal(FoundationContractVersion.Current, plan.ContractVersion);
    }

    [Fact]
    public void Icd_blocks_second_hit_within_window()
    {
        var h = new FoundationHarness(1);
        h.Grant(new EffectGrantDto
        {
            GrantId = "g-icd",
            EffectId = "fx.icd_butter",
            OwnerKey = EffectOwnerKeys.Match
            // default damage ICD 250ms
        });
        var p1 = h.OnEvent(Dealt(tick: 1));
        Assert.Single(p1.Actions);
        var p2 = h.OnEvent(Dealt(tick: 2));
        Assert.Empty(p2.Actions);
        Assert.Contains(p2.Skipped, s => s.Contains("icd"));
        h.AdvanceTime(250);
        var p3 = h.OnEvent(Dealt(tick: 3));
        Assert.Single(p3.Actions);
    }

    [Fact]
    public void Chance_zero_never_fires()
    {
        var h = new FoundationHarness(99);
        h.Grant(new EffectGrantDto
        {
            GrantId = "g-c",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["chance"] = 0.0, ["icd_ms"] = 0 }
        });
        var plan = h.OnEvent(Dealt());
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Skipped, s => s.Contains("chance"));
    }

    [Fact]
    public void Unknown_overlay_key_rejected_on_grant()
    {
        var h = new FoundationHarness();
        Assert.Throws<InvalidOperationException>(() => h.Grant(new EffectGrantDto
        {
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["nope"] = 1 }
        }));
    }

    [Fact]
    public void Withdraw_stops_further_fires()
    {
        var h = new FoundationHarness();
        var g = h.Grant(new EffectGrantDto
        {
            GrantId = "gw",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        Assert.Single(h.OnEvent(Dealt()).Actions);
        Assert.True(h.Withdraw(g.GrantId));
        Assert.Empty(h.OnEvent(Dealt(tick: 9)).Actions);
    }

    [Fact]
    public void Passive_fires_ModifyStat_on_grant_and_remove_on_withdraw()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto { GrantId = "gp", EffectId = "fx.passive_atk_flat" });
        Assert.Contains(h.Sink.Items, i => i.Action == EffectActions.ModifyStat && !JsonOverlay.GetBool(i.Params, "remove"));
        h.Sink.Items.Clear();
        h.Withdraw("gp");
        Assert.Contains(h.Sink.Items, i => i.Action == EffectActions.ModifyStat && JsonOverlay.GetBool(i.Params, "remove"));
    }

    [Fact]
    public void Executor_failure_stops_sequence()
    {
        var clock = new FakeEffectClock();
        var rng = new SeededEffectRandom(1);
        var sink = new RecordingEffectSink();
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectSeedCatalog.CreateAll());
        var bag = new EffectBag(catalog, new InMemoryEffectGrantStore(), new EffectProcPolicy(clock, rng), sink);
        bag.Grant(new EffectGrantDto
        {
            GrantId = "g2",
            EffectId = "fx.spawn_plant_bullet",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        sink.Items.Clear();
        sink.Fired.Clear();
        sink.FailNext = true;
        var plan = bag.OnEvent(Dealt());
        Assert.Single(plan.Actions); // first planned then stop — RecordingSink still records failed item
        Assert.Contains(plan.Skipped, s => s.Contains("executor-stop"));
        Assert.False(sink.Fired[0].Ok);
    }

    [Fact]
    public void Owner_plant_type_filters_dealt()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gt",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.PlantType(7),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        Assert.Empty(h.OnEvent(Dealt()).Actions); // typeId 0
        var ev = Dealt();
        ev.TypeId = 7;
        Assert.Single(h.OnEvent(ev).Actions);
    }

    [Fact]
    public void OnSpawn_type_grant_fires()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "gs",
            EffectId = "fx.spawn_butter",
            OwnerKey = EffectOwnerKeys.PlantType(0),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnSpawn,
            Side = "plant",
            TypeId = 0,
            ActorPtr = "0xP",
            MatchKey = "m1",
            Tick = 1
        });
        Assert.Single(plan.Actions);
        Assert.Equal(EffectActions.ApplyStatus, plan.Actions[0].Action);
    }

    [Fact]
    public void Dedupe_blocks_same_logical_hit()
    {
        var d = new EffectEventDedupe();
        var a = Dealt(tick: 10);
        Assert.True(d.ShouldEmit(a));
        Assert.False(d.ShouldEmit(Dealt(tick: 10)));
        Assert.True(d.ShouldEmit(Dealt(tick: 11)));
    }

    [Fact]
    public void Golden_butter_plan_matches_fixture()
    {
        var h = new FoundationHarness(42);
        h.Grant(new EffectGrantDto
        {
            GrantId = "golden-butter",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var plan = h.OnEvent(Dealt());
        var expectedPath = FindFixture("butter_on_hit.plan.json");
        var expected = JsonSerializer.Deserialize<IntentPlanDto>(File.ReadAllText(expectedPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(expected.ContractVersion, plan.ContractVersion);
        Assert.Equal(expected.Trigger, plan.Trigger);
        Assert.Equal(expected.Actions.Count, plan.Actions.Count);
        Assert.Equal(expected.Actions[0].Action, plan.Actions[0].Action);
        Assert.Equal(expected.Actions[0].EffectId, plan.Actions[0].EffectId);
        Assert.Equal("butter", plan.Actions[0].Params["status"]?.ToString());
    }

    [Fact]
    public void Golden_death_spawn_plan_matches_fixture()
    {
        var h = new FoundationHarness(42);
        h.Grant(new EffectGrantDto
        {
            GrantId = "golden-death",
            EffectId = "fx.spawn_zombie_ondeath",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDeath,
            Side = "zombie",
            ActorPtr = "0xZ",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        });
        var expectedPath = FindFixture("spawn_ondeath.plan.json");
        var expected = JsonSerializer.Deserialize<IntentPlanDto>(File.ReadAllText(expectedPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(expected.Actions[0].Action, plan.Actions[0].Action);
        Assert.Equal("zombie", plan.Actions[0].Params["kind"]?.ToString());
    }

    static string FindFixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "effects", name);
            if (File.Exists(candidate)) return candidate;
            var up = Path.Combine(dir, "..", "..", "..", "..", "fixtures", "effects", name);
            if (File.Exists(Path.GetFullPath(up))) return Path.GetFullPath(up);
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new FileNotFoundException("fixture " + name);
    }
}

/// <summary>
/// Real, previously-undiscovered defect found running the full `FusionRpg.Core.Tests` suite (never in
/// isolation): `InMemoryEffectCatalog.Upsert`/`ReplaceAll` used to call `def.Actions.Sort(...)` IN
/// PLACE on the caller's own list. `ConstructionActions.CompiledEffects` is a `static { get; }`-cached
/// list shared by every battle/test that references it, so every `Upsert` of one of its defs re-sorted
/// (and version-bumped) that ONE shared list -- a concurrent `EffectBag.FireGrant` enumerating
/// `def.Actions` on another thread then throws `InvalidOperationException: Collection was modified`.
/// Reproduced as `ConstructionActionsTests.Firing_at_a_non_adjacent_cell_is_refused_by_the_shared_placement_gate`
/// failing only under full-suite parallel execution. `EffectDef.Actions` is `init`-only, so the fix is
/// a defensive copy (`WithSortedActions`), never a mutation of the caller's object.
/// </summary>
public class InMemoryEffectCatalogTests
{
    static EffectDef SharedDef(string id = "fx.shared-static-like") => new()
    {
        EffectId = id,
        Actions = new List<EffectActionRow>
        {
            new() { Seq = 2, Action = EffectActions.ApplyStatus },
            new() { Seq = 0, Action = EffectActions.ModifyStat },
            new() { Seq = 1, Action = EffectActions.GrantShield },
        }
    };

    [Fact]
    public void Upsert_never_mutates_the_callers_own_Actions_list()
    {
        var def = SharedDef();
        var originalList = def.Actions;
        var originalOrder = originalList.Select(a => a.Seq).ToList();

        new InMemoryEffectCatalog().Upsert(def);

        Assert.Same(originalList, def.Actions); // same object -- never reassigned, never sorted in place
        Assert.Equal(originalOrder, def.Actions.Select(a => a.Seq)); // still in the CALLER's original order
    }

    [Fact]
    public void Two_catalogs_upserting_the_same_shared_def_each_read_back_sorted_and_independent()
    {
        var shared = SharedDef();

        var catalogA = new InMemoryEffectCatalog();
        var catalogB = new InMemoryEffectCatalog();
        catalogA.Upsert(shared);
        catalogB.Upsert(shared);

        Assert.Equal(new[] { 0, 1, 2 }, catalogA.Get(shared.EffectId)!.Actions.Select(a => a.Seq));
        Assert.Equal(new[] { 0, 1, 2 }, catalogB.Get(shared.EffectId)!.Actions.Select(a => a.Seq));
        Assert.NotSame(catalogA.Get(shared.EffectId)!.Actions, catalogB.Get(shared.EffectId)!.Actions);
    }

    [Fact]
    public void ReplaceAll_also_never_mutates_the_callers_own_Actions_lists()
    {
        var def = SharedDef();
        var originalList = def.Actions;

        new InMemoryEffectCatalog().ReplaceAll(new[] { def });

        Assert.Same(originalList, def.Actions);
        Assert.Equal(new[] { 2, 0, 1 }, def.Actions.Select(a => a.Seq)); // unchanged, still the authored order
    }

    [Fact]
    public void Concurrent_upserts_of_a_shared_static_like_def_never_race_a_concurrent_reader()
    {
        // Deliberately amplified reproduction of the original crash: one thread continuously
        // enumerates the shared def's own Actions list while several others repeatedly Upsert that
        // SAME def into their own fresh catalog -- exactly ConstructionActions.CompiledEffects's own
        // shape (one static list, many independent per-battle/per-test catalogs).
        var shared = SharedDef();
        Exception? seen = null;
        var stop = 0;

        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                try { foreach (var _ in shared.Actions) { } }
                catch (Exception ex) { seen = ex; return; }
            }
        });

        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
                new InMemoryEffectCatalog().Upsert(shared);
        })).ToArray();

        Task.WaitAll(writers);
        Interlocked.Exchange(ref stop, 1);
        reader.Wait();

        Assert.Null(seen);
    }
}
