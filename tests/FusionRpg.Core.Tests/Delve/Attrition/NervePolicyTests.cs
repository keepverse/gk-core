using System.IO;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Attrition;

/// <summary>D2.19 (spec-delve-attrition.md §4) — `NervePolicy.Sync`, the `ExhaustionPolicy.Sync` shape
/// applied to a projection rather than a threshold crossing, plus `NerveContainer.Load`'s own
/// container-file parsing. `DungeonTuningHub`/`DungeonRegistryHub` are configured for the whole
/// assembly by `Dungeon.DungeonHubTestBootstrap`'s module initializer.</summary>
public class NervePolicyTests
{
    static readonly string[] Stages = { "unsettled", "shaken", "afflicted" };

    static IReadOnlyDictionary<string, IReadOnlyList<StatusStatMod>> Mods(params (string stage, StatusStatMod mod)[] rows)
    {
        var byStage = new Dictionary<string, IReadOnlyList<StatusStatMod>>(StringComparer.Ordinal);
        foreach (var stage in Stages)
            byStage[stage] = Array.Empty<StatusStatMod>();
        foreach (var (stage, mod) in rows)
            byStage[stage] = new[] { mod };
        return byStage;
    }

    static StatusCatalog CatalogWithNerve()
    {
        var catalog = new StatusCatalog();
        foreach (var stage in Stages)
            catalog.Register(new StatusDef(
                NerveStatusIds.For(stage), StatusKind.Debuff, "nerve",
                new[] { StatusL2bCategory.Dot }, Array.Empty<string>(),
                StatusStacking.Replace, new[] { StatusPayloadKind.ModifyStat }));
        return catalog;
    }

    static StatusRuntime MakeRuntime(StatusCatalog catalog) =>
        new(catalog, (_, _) => ActorDerivedSnapshot.Empty);

    // ---- constructor validation ----

    [Fact]
    public void Constructor_rejects_a_stage_missing_from_modsByStage()
    {
        var incomplete = new Dictionary<string, IReadOnlyList<StatusStatMod>>(StringComparer.Ordinal)
        {
            ["unsettled"] = Array.Empty<StatusStatMod>(),
            ["shaken"] = Array.Empty<StatusStatMod>(),
            // "afflicted" missing
        };

        var ex = Assert.Throws<ArgumentException>(() => new NervePolicy(CatalogWithNerve(), Stages, incomplete));
        Assert.Contains("afflicted", ex.Message);
    }

    [Fact]
    public void Constructor_rejects_a_self_regen_cycle_against_spirits_own_regen_channel()
    {
        var selfRegen = Mods(("shaken", new StatusStatMod(DerivedStatChannels.ResourceRegen("spirit"), "flat", -1)));

        var ex = Assert.Throws<ArgumentException>(() => new NervePolicy(CatalogWithNerve(), Stages, selfRegen));
        Assert.Contains("spirit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_accepts_a_debuff_touching_a_different_resources_regen()
    {
        // Touches hunger's regen, not spirit's -- not the cycle the rule bans (mirrors
        // ExhaustionPolicyTests.ANonSelfRegenDebuffIsAcceptedAtConstruction).
        var mods = Mods(("shaken", new StatusStatMod(DerivedStatChannels.ResourceRegen("hunger"), "flat", -1)));

        var policy = new NervePolicy(CatalogWithNerve(), Stages, mods);

        Assert.NotNull(policy); // reaching here without throwing is the assertion
    }

    [Fact]
    public void Constructor_rejects_a_stage_the_catalog_never_registered()
    {
        var catalog = new StatusCatalog(); // no nerve.* ids registered at all
        var mods = Mods();

        Assert.Throws<UnknownStatusIdException>(() => new NervePolicy(catalog, Stages, mods));
    }

    [Fact]
    public void Constructor_null_arguments_throw()
    {
        var catalog = CatalogWithNerve();
        var mods = Mods();
        Assert.Throws<ArgumentNullException>(() => new NervePolicy(null!, Stages, mods));
        Assert.Throws<ArgumentNullException>(() => new NervePolicy(catalog, null!, mods));
        Assert.Throws<ArgumentNullException>(() => new NervePolicy(catalog, Stages, null!));
    }

    // ---- Sync: stage -1 (never nerved) ----

    [Fact]
    public void Stage_minus_one_with_nothing_live_is_a_no_op()
    {
        var policy = new NervePolicy(CatalogWithNerve(), Stages, Mods());
        var runtime = MakeRuntime(CatalogWithNerve());

        var applied = policy.Sync(runtime, "creature:1", stage: -1, DateTimeOffset.UnixEpoch);

        Assert.False(applied);
        Assert.Empty(runtime.ForHost("creature:1"));
    }

    // ---- Sync: first entry into a stage ----

    [Fact]
    public void First_entry_into_a_stage_applies_exactly_that_stages_id()
    {
        var catalog = CatalogWithNerve();
        var policy = new NervePolicy(catalog, Stages, Mods());
        var runtime = MakeRuntime(catalog);

        var applied = policy.Sync(runtime, "creature:1", stage: 0, DateTimeOffset.UnixEpoch);

        Assert.True(applied);
        var live = Assert.Single(runtime.ForHost("creature:1"));
        Assert.Equal("nerve.unsettled", live.StatusId);
    }

    [Fact]
    public void Syncing_the_same_stage_again_is_idempotent_no_op()
    {
        var catalog = CatalogWithNerve();
        var policy = new NervePolicy(catalog, Stages, Mods());
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        Assert.True(policy.Sync(runtime, "creature:1", 1, now));
        var again = policy.Sync(runtime, "creature:1", 1, now.AddSeconds(1));

        Assert.False(again);
        Assert.Single(runtime.ForHost("creature:1")); // still exactly one instance, not a second stack
    }

    // ---- Sync: stage change replaces the live instance, never coexists ----

    [Fact]
    public void A_stage_increase_clears_the_old_id_and_applies_the_new_one_with_never_two_live_at_once()
    {
        var catalog = CatalogWithNerve();
        var policy = new NervePolicy(catalog, Stages, Mods());
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        policy.Sync(runtime, "creature:1", 0, now); // unsettled
        var changed = policy.Sync(runtime, "creature:1", 2, now.AddSeconds(1)); // straight to afflicted

        Assert.True(changed);
        var live = Assert.Single(runtime.ForHost("creature:1")); // never two nerve.* instances live together
        Assert.Equal("nerve.afflicted", live.StatusId);
    }

    [Fact]
    public void A_stage_decrease_to_a_lower_stage_also_replaces_never_stacks()
    {
        var catalog = CatalogWithNerve();
        var policy = new NervePolicy(catalog, Stages, Mods());
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        policy.Sync(runtime, "creature:1", 2, now);
        policy.Sync(runtime, "creature:1", 0, now.AddSeconds(1));

        var live = Assert.Single(runtime.ForHost("creature:1"));
        Assert.Equal("nerve.unsettled", live.StatusId);
    }

    // ---- Sync: dropping to -1 withdraws only, never re-applies ----

    [Fact]
    public void Dropping_to_minus_one_withdraws_the_live_instance_and_reports_no_apply()
    {
        var catalog = CatalogWithNerve();
        var policy = new NervePolicy(catalog, Stages, Mods());
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        policy.Sync(runtime, "creature:1", 1, now);
        var withdrew = policy.Sync(runtime, "creature:1", -1, now.AddSeconds(1));

        Assert.False(withdrew); // a withdraw is not counted as an apply -- matches ExhaustionPolicy.Sync
        Assert.Empty(runtime.ForHost("creature:1"));
    }

    // ---- Sync: carries the authored StatMods verbatim, never a hardcoded channel ----

    [Fact]
    public void The_applied_instance_carries_that_stages_authored_mods_verbatim()
    {
        var catalog = CatalogWithNerve();
        var mod = new StatusStatMod("combat.dodge.omni", "increased", -0.2);
        var policy = new NervePolicy(catalog, Stages, Mods(("shaken", mod)));
        var runtime = MakeRuntime(catalog);

        policy.Sync(runtime, "creature:1", 1, DateTimeOffset.UnixEpoch);

        var live = Assert.Single(runtime.ForHost("creature:1"));
        Assert.Equal(new[] { mod }, live.StatMods);
    }

    // ---- Sync: validation ----

    [Fact]
    public void Sync_null_runtime_throws()
    {
        var policy = new NervePolicy(CatalogWithNerve(), Stages, Mods());
        Assert.Throws<ArgumentNullException>(() => policy.Sync(null!, "creature:1", 0, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(3)]
    public void Sync_rejects_a_stage_outside_minus_one_to_stage_count_minus_one(int badStage)
    {
        var policy = new NervePolicy(CatalogWithNerve(), Stages, Mods());
        var runtime = MakeRuntime(CatalogWithNerve());

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Sync(runtime, "creature:1", badStage, DateTimeOffset.UnixEpoch));
    }

    // ---- multiple creatures are independent ----

    [Fact]
    public void Different_creatures_nerve_independently()
    {
        var catalog = CatalogWithNerve();
        var policy = new NervePolicy(catalog, Stages, Mods());
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        policy.Sync(runtime, "creature:1", 2, now);
        policy.Sync(runtime, "creature:2", 0, now);

        Assert.Equal("nerve.afflicted", Assert.Single(runtime.ForHost("creature:1")).StatusId);
        Assert.Equal("nerve.unsettled", Assert.Single(runtime.ForHost("creature:2")).StatusId);
    }
}

/// <summary>`NerveContainer.Load` — the JSON-to-`StatusStatMod` container parse, and the full,
/// real-file production pipeline (`nerveStage` registry + `dungeon.v1.json` thresholds + the real
/// `nerve.v1.json`) wired end to end.</summary>
public class NerveContainerTests
{
    const string ThreeStageJson = """
    {
      "schemaVersion": 1,
      "kind": "container",
      "entries": [
        { "id": "nerve.unsettled", "stage": "unsettled", "stat": { "combat.accuracy.omni": { "increased": -0.1 } } },
        { "id": "nerve.shaken", "stage": "shaken", "stat": { "combat.accuracy.omni": { "increased": -0.2 } } },
        { "id": "nerve.afflicted", "stage": "afflicted", "stat": {} }
      ]
    }
    """;

    [Fact]
    public void Loads_one_entry_per_stage_with_its_own_mods()
    {
        var byStage = NerveContainer.Load(ThreeStageJson);

        Assert.Equal(3, byStage.Count);
        Assert.Equal(new StatusStatMod("combat.accuracy.omni", "increased", -0.1), Assert.Single(byStage["unsettled"]));
        Assert.Equal(new StatusStatMod("combat.accuracy.omni", "increased", -0.2), Assert.Single(byStage["shaken"]));
        Assert.Empty(byStage["afflicted"]);
    }

    [Fact]
    public void Null_json_throws()
    {
        Assert.Throws<ArgumentNullException>(() => NerveContainer.Load(null!));
    }

    [Fact]
    public void An_entry_missing_stage_throws()
    {
        const string bad = """{ "schemaVersion": 1, "kind": "container", "entries": [ { "id": "x", "stat": {} } ] }""";
        Assert.Throws<FormatException>(() => NerveContainer.Load(bad));
    }

    [Fact]
    public void An_unknown_stat_channel_throws_by_name()
    {
        const string bad = """
        { "entries": [ { "id": "x", "stage": "unsettled", "stat": { "not.a.real.channel": { "flat": 1 } } } ] }
        """;
        var ex = Assert.Throws<FormatException>(() => NerveContainer.Load(bad));
        Assert.Contains("not.a.real.channel", ex.Message);
    }

    // ---- the real, shipped file ----

    [Fact]
    public void The_real_shipped_container_loads_all_three_stages_with_nonEmpty_mods()
    {
        var json = File.ReadAllText(DungeonTestFiles.NerveContainerPath());
        var byStage = NerveContainer.Load(json);

        Assert.Equal(3, byStage.Count);
        foreach (var stage in new[] { "unsettled", "shaken", "afflicted" })
            Assert.NotEmpty(byStage[stage]); // every real stage actually debuffs something
    }

    [Fact]
    public void The_real_shipped_container_gets_worse_each_stage_on_every_channel_it_touches()
    {
        // Darkest-Dungeon-style ladder: the spec's own framing (§4) is a WORSENING ladder, so whatever
        // channel a later stage shares with an earlier one must never read as an improvement.
        var byStage = NerveContainer.Load(File.ReadAllText(DungeonTestFiles.NerveContainerPath()));
        var unsettled = byStage["unsettled"].ToDictionary(m => m.ChannelId, m => m.Value);
        var shaken = byStage["shaken"].ToDictionary(m => m.ChannelId, m => m.Value);
        var afflicted = byStage["afflicted"].ToDictionary(m => m.ChannelId, m => m.Value);

        foreach (var (channel, unsettledValue) in unsettled)
        {
            Assert.True(shaken.ContainsKey(channel), $"'{channel}' present at unsettled must still be present at shaken");
            Assert.True(afflicted.ContainsKey(channel), $"'{channel}' present at unsettled must still be present at afflicted");
            Assert.True(shaken[channel] <= unsettledValue, $"'{channel}' must not improve from unsettled to shaken");
            Assert.True(afflicted[channel] <= shaken[channel], $"'{channel}' must not improve from shaken to afflicted");
        }
    }

    [Fact]
    public void The_real_container_never_touches_spirits_own_regen_channel()
    {
        // The self-regen-cycle rule, proven against the actual shipped content rather than only a
        // synthetic fixture -- if this ever failed, NervePolicy's own constructor would refuse to boot.
        var byStage = NerveContainer.Load(File.ReadAllText(DungeonTestFiles.NerveContainerPath()));
        var spiritRegen = DerivedStatChannels.ResourceRegen("spirit");

        foreach (var mods in byStage.Values)
            Assert.DoesNotContain(mods, m => m.ChannelId == spiritRegen);
    }

    // ---- the full, real production pipeline: registry + tuning + container + catalog, wired together ----

    [Fact]
    public void The_real_nerveStage_registry_dungeon_tuning_and_container_wire_into_a_working_NervePolicy()
    {
        var stages = BandCatalog.Get("nerveStage").Members;
        var thresholds = DungeonTuningHub.Tuning.AttritionNerve.StageThresholds;
        var modsByStage = NerveContainer.Load(File.ReadAllText(DungeonTestFiles.NerveContainerPath()));
        var catalog = StatusCatalogBootstrap.CreateDefault();

        var policy = new NervePolicy(catalog, stages, modsByStage);
        var runtime = new StatusRuntime(catalog, (_, _) => ActorDerivedSnapshot.Empty);
        var now = DateTimeOffset.UnixEpoch;

        // Drive a realistic progression through NerveLadder.StageFor, exactly as a caller would each
        // room: stacks climb from a curio hit, spirit is untouched (not exhausted) until the end.
        foreach (var (stacks, spiritResolved, expectedId) in new[]
        {
            (0, 1000L, (string?)null),
            (1, 1000L, "nerve.unsettled"),
            (3, 1000L, "nerve.shaken"),
            (5, 1000L, "nerve.afflicted"),
            (5, 0L, "nerve.afflicted"),   // spirit exhausted -- already at the top, stays there
            (1, 1000L, "nerve.unsettled"), // a rest drops stacks back down -- the ladder re-resolves down too
        })
        {
            var stage = NerveLadder.StageFor(stacks, spiritResolved, thresholds);
            policy.Sync(runtime, "creature:real", stage, now);
            var live = runtime.ForHost("creature:real");

            if (expectedId is null)
                Assert.Empty(live);
            else
                Assert.Equal(expectedId, Assert.Single(live).StatusId);

            now = now.AddSeconds(1);
        }
    }
}
