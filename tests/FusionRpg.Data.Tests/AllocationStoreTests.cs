using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>class-system-todo.md P6.2 — <c>rpg_aptitude_allocation</c> / <c>RpgStore.Aptitudes.cs</c>
/// (spec-point-economy.md, read in full this session; table in §7: tests 7 and 8 covered here — the
/// ones that are this store's own concern, not `PointBudget`'s (P6.1, already covered in
/// `PointBudgetTests.cs`) or `RespecPolicy`'s (P6.3, unbuilt).</summary>
[Collection("SpeciesBuildPlanCatalogGlobal")] // EP1.13: SpeciesBuildPlanCatalog is a global, unscoped
                                               // singleton (see its static ctor's own comment below) --
                                               // EffectiveUniqueAllocationTests.cs also configures it,
                                               // so both classes share this collection to serialize
                                               // their execution and remove the race two xUnit-parallel
                                               // static-ctor writers would otherwise create.
public class AllocationStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AllocationStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Allocation_roundTrips_perScope()
    {
        // spec-point-economy.md §7 test 7: round-trips PER SCOPE -- two different scopes, saved and
        // loaded independently, must not bleed into each other.
        var commander = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 12)
                       + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 30);
        var uniqueCreature = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Bulwark", 55);

        _store.SaveAllocation(AllocationScope.Commander, "player:1", commander);
        _store.SaveAllocation(AllocationScope.UniqueCreature, "instance:abc", uniqueCreature);

        var loadedCommander = _store.LoadAllocation(AllocationScope.Commander, "player:1");
        var loadedUnique = _store.LoadAllocation(AllocationScope.UniqueCreature, "instance:abc");

        Assert.Equal(12, loadedCommander.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(30, loadedCommander.PointsAt(AllocationScope.Commander, "Vigor"));
        Assert.Equal(0, loadedCommander.PointsAt(AllocationScope.UniqueCreature, "Bulwark")); // no bleed

        Assert.Equal(55, loadedUnique.PointsAt(AllocationScope.UniqueCreature, "Bulwark"));
        Assert.Equal(0, loadedUnique.PointsAt(AllocationScope.Commander, "Might")); // no bleed
    }

    [Fact]
    public void Allocation_roundTrips_forDifferentKeysInTheSameScope()
    {
        // Two DIFFERENT commanders (different scopeKey, same scope) must not collide.
        var a = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 10);
        var b = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 90);

        _store.SaveAllocation(AllocationScope.Commander, "player:a", a);
        _store.SaveAllocation(AllocationScope.Commander, "player:b", b);

        Assert.Equal(10, _store.LoadAllocation(AllocationScope.Commander, "player:a").PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(90, _store.LoadAllocation(AllocationScope.Commander, "player:b").PointsAt(AllocationScope.Commander, "Might"));
    }

    [Fact]
    public void LoadAllocation_neverSaved_returnsEmpty_notNullNotThrown()
    {
        // AptitudeAllocation's own contract: "empty means all-zero shares, never invent a default."
        var loaded = _store.LoadAllocation(AllocationScope.Aspect, "never-saved-key");

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded.GrandTotal());
    }

    [Fact]
    public void SaveAllocation_resavingWithFewerPoints_removesTheStaleRow()
    {
        // "The store holds the current allocation, not a change log" -- a respec that zeroes an
        // aptitude must actually delete its row, not leave a stale nonzero value behind.
        var first = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 40)
                  + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 20);
        _store.SaveAllocation(AllocationScope.Commander, "player:1", first);

        var respecced = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 60); // Vigor dropped entirely
        _store.SaveAllocation(AllocationScope.Commander, "player:1", respecced);

        var loaded = _store.LoadAllocation(AllocationScope.Commander, "player:1");
        Assert.Equal(60, loaded.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(0, loaded.PointsAt(AllocationScope.Commander, "Vigor")); // gone, not stale
        Assert.Equal(60, loaded.GrandTotal());
    }

    // ---- "unknown scope rejects" (§7 test 7) ---------------------------------------------------------

    [Fact]
    public void ScopeFromText_unknownScope_rejectsNamingIt()
    {
        var ex = Assert.Throws<ArgumentException>(() => RpgStore.ScopeFromText("guildmaster"));
        Assert.Contains("guildmaster", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeToText_andBack_roundTripsForAllFourScopes()
    {
        foreach (var scope in new[] { AllocationScope.Commander, AllocationScope.CreatureType, AllocationScope.Aspect, AllocationScope.UniqueCreature })
            Assert.Equal(scope, RpgStore.ScopeFromText(RpgStore.ScopeToText(scope)));
    }

    [Fact]
    public void SaveAllocation_emptyScopeKey_rejects()
    {
        Assert.Throws<ArgumentException>(() => _store.SaveAllocation(AllocationScope.Commander, "", AptitudeAllocation.Empty));
        Assert.Throws<ArgumentException>(() => _store.SaveAllocation(AllocationScope.Commander, "   ", AptitudeAllocation.Empty));
    }

    // ---- "no channel-value column exists in the schema" (§7 test 8) ---------------------------------

    [Fact]
    public void Schema_storesInputsOnly_noResolvedChannelValueColumn()
    {
        // spec-point-economy.md §6: "Persistence stores the allocation, never the resolved channels."
        // Asserted directly on the live schema, not on the store's own C# API surface -- a resolved
        // value could otherwise sneak in as an extra column nothing in this test file's own method
        // calls would ever exercise.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(rpg_aptitude_allocation);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read())
            columns.Add(r.GetString(1)); // column 1 of table_info is the column name

        Assert.Equal(
            new[] { "scope", "scope_key", "aptitude_id", "points" }.OrderBy(x => x, StringComparer.Ordinal),
            columns.OrderBy(x => x, StringComparer.Ordinal));

        // Explicitly not just "the four expected columns exist" -- also that nothing ELSE is there,
        // which is the actual "no resolved channel value" claim (a channel id would look like
        // "combat.power.omni", nothing in the four columns above resembles one).
        Assert.DoesNotContain(columns, c => c.Contains('.', StringComparison.Ordinal));
    }

    [Fact]
    public void Reset_clearsPersistedAllocations()
    {
        _store.SaveAllocation(AllocationScope.Commander, "player:1",
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50));

        _store.Reset();

        var loaded = _store.LoadAllocation(AllocationScope.Commander, "player:1");
        Assert.Equal(0, loaded.GrandTotal());
    }

    // ---- species-build T2.1/T2.2 (creature-type-allocation) --------------------------------------

    const int FumeshroomCreatureTypeId = 60007;

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static readonly AptitudeTuning RealTuning = AptitudeTuningLoader.Parse(
        // passive-tree C6 (2026-09-06): v5 -> v6, hosts moved with it; D55 (2026-09-06): v6 -> v7,
        // kept in sync so "the real shipped tuning" stays true rather than quietly drifting behind
        // RpgHost.cs/Program.cs.
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitudes.v10.json")));

    static AllocationStoreTests()
    {
        // Global, unscoped (SpeciesBuildPlanCatalog has no test-scoping mechanism, unlike
        // CreatureSpeciesCatalog's UseScoped). EP1.13 (2026-09-20): a SECOND file,
        // EffectiveUniqueAllocationTests.cs, also configures this catalog -- the two share the
        // "SpeciesBuildPlanCatalogGlobal" xUnit collection above so they never run in parallel with
        // each other, and both configure the identical "fumeshroom" dictionary so the last write to
        // run is never observably different from the first. Configured once via this static ctor,
        // matching SpeciesProgressionTuningHub's own "construct one inline" convention for a hub with
        // no fixture file behind it.
        SpeciesBuildPlanCatalog.Configure(new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            ["fumeshroom"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["Might"] = 500, ["Vigor"] = 300, ["Fortitude"] = 200
            }
        });
    }

    /// <summary>Writes a species-progression row directly (bypassing the real XP curve entirely) —
    /// this file is testing `EffectiveSpeciesAllocation`'s own composition logic, not re-proving
    /// `species-xp`'s leveling pipeline (already covered in `SpeciesProgressionTests.cs`).</summary>
    void SeedSpeciesLevel(long playerId, int creatureTypeId, long level, string speciesId,
        FusionRpg.Core.Commanders.EmpireId? empire = null)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_actor_progression(
              save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc, scope_key)
            VALUES ($p, $e, 'species', $tid, $lvl, 0, $lvl, 0, 0, $now, $sk);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$e", (empire ?? _store.HumanEmpireOf(playerId)).Value);
        cmd.Parameters.AddWithValue("$tid", creatureTypeId);
        cmd.Parameters.AddWithValue("$lvl", level);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$sk", speciesId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void EffectiveSpeciesAllocation_withNoOverride_resolvesToThePlansBaseline_notZero()
    {
        var player = _store.CreatePlayer("SpeciesAllocBaseline");
        SeedSpeciesLevel(player.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom"); // source = 20

        var effective = _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning);

        // The silent-zero risk the spec calls out by name: a never-overridden species must NOT read
        // AptitudeAllocation.Empty once it has a real level.
        Assert.True(effective.TotalForScope(AllocationScope.CreatureType) > 0);
        Assert.True(effective.PointsAt(AllocationScope.CreatureType, "Might")
            > effective.PointsAt(AllocationScope.CreatureType, "Vigor"));
    }

    [Fact]
    public void A_level_four_species_composes_differently_from_a_level_one_species()
    {
        // solid-remediation T4.4 (S7) stated as the audit states it. The hinge already existed —
        // SpeciesBaselineAllocation derives from the species LEVEL row — so this asserts the property
        // the acceptance names, and it is what the battle compose now receives.
        var low = _store.CreatePlayer("SpeciesLevelLow");
        SeedSpeciesLevel(low.Id, FumeshroomCreatureTypeId, level: 4, "fumeshroom");

        var high = _store.CreatePlayer("SpeciesLevelHigh");
        SeedSpeciesLevel(high.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");

        var atFour = _store.EffectiveSpeciesAllocation(low.Id, "fumeshroom", RealTuning);
        var atTwentyOne = _store.EffectiveSpeciesAllocation(high.Id, "fumeshroom", RealTuning);

        // Different, and specifically MORE at the higher level — an assertion that a mapping returning
        // a constant would fail. Never a pinned magnitude: the values are plan/tuning-owned and a
        // balance pass must not turn this red.
        Assert.NotEqual(
            atFour.TotalForScope(AllocationScope.CreatureType),
            atTwentyOne.TotalForScope(AllocationScope.CreatureType));
        Assert.True(
            atTwentyOne.TotalForScope(AllocationScope.CreatureType)
            > atFour.TotalForScope(AllocationScope.CreatureType));
    }

    [Fact]
    public void A_Zomboss_empire_ask_never_reads_the_players_species_level_baseline()
    {
        // solid-remediation T4.4 (S3), re-stated by ai-empire-species EP4.15. T4.1 threaded the empire
        // through the OVERRIDE key but the baseline half derived from a per-player species LEVEL row,
        // and the parameter was accepted and then ignored — so a Zomboss ask silently returned the
        // human player's level-derived baseline. That is defect S1 surviving one path further down.
        //
        // EP4.15 removed the `empire != Dave -> Empty` branch this file used to prove. It still holds,
        // for the honest reason: the ask reads ZOMBOSS'S OWN row, and this save has none (level 1,
        // budget 0). The player's level 21 row is not what answers it.
        var player = _store.CreatePlayer("SpeciesAllocEmpireBaseline");
        SeedSpeciesLevel(player.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");

        var dave = _store.EffectiveSpeciesAllocation(
            player.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Dave);
        var zomboss = _store.EffectiveSpeciesAllocation(
            player.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Zomboss);

        // The player's own empire still reads its real baseline — the refusal is specific, not a
        // blanket zero that would also break Dave.
        Assert.True(dave.TotalForScope(AllocationScope.CreatureType) > 0);

        // Zomboss's own row is the level-1 default, so Empty is the honest answer rather than
        // someone else's progression.
        Assert.Equal(0, zomboss.TotalForScope(AllocationScope.CreatureType));
        Assert.Equal(1, _store.SpeciesLevelOf(
            new FusionRpg.Core.Saves.SaveId(player.Id), FusionRpg.Core.Commanders.EmpireId.Zomboss,
            FumeshroomCreatureTypeId));
    }

    [Fact]
    public void An_AI_empires_species_above_level_one_resolves_its_own_levels_distribution()
    {
        // ai-empire-species EP4.15 (spec tests 3 and 4). The same species at opposite levels in the two
        // empires of ONE save: each ask must follow its OWN row, which an implementation that still read
        // the human's row (or still returned Empty) cannot do.
        var daveHigh = _store.CreatePlayer("SpeciesAllocAiDaveHigh");
        SeedSpeciesLevel(daveHigh.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");
        SeedSpeciesLevel(daveHigh.Id, FumeshroomCreatureTypeId, level: 4, "fumeshroom",
            FusionRpg.Core.Commanders.EmpireId.Zomboss);

        var aiHigh = _store.CreatePlayer("SpeciesAllocAiHigh");
        SeedSpeciesLevel(aiHigh.Id, FumeshroomCreatureTypeId, level: 4, "fumeshroom");
        SeedSpeciesLevel(aiHigh.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom",
            FusionRpg.Core.Commanders.EmpireId.Zomboss);

        var daveAt21 = _store.EffectiveSpeciesAllocation(
            daveHigh.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Dave);
        var daveAt4 = _store.EffectiveSpeciesAllocation(
            aiHigh.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Dave);
        var zombossAt4 = _store.EffectiveSpeciesAllocation(
            daveHigh.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Zomboss);
        var zombossAt21 = _store.EffectiveSpeciesAllocation(
            aiHigh.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Zomboss);

        // Above level 1 the AI empire's own plan distribution composes — not Empty, and not a copy of
        // the other empire's level. Never a pinned magnitude: plan and tuning own the values.
        Assert.True(zombossAt21.TotalForScope(AllocationScope.CreatureType) > 0);
        Assert.True(zombossAt21.TotalForScope(AllocationScope.CreatureType)
            > zombossAt4.TotalForScope(AllocationScope.CreatureType));
        Assert.True(daveAt21.TotalForScope(AllocationScope.CreatureType)
            > daveAt4.TotalForScope(AllocationScope.CreatureType));
    }

    [Fact]
    public void The_baseline_call_itself_honours_the_empire_it_accepts()
    {
        // Asserted directly on SpeciesBaselineAllocation too: EffectiveSpeciesAllocation could start
        // short-circuiting earlier and leave this parameter lying again without anything noticing.
        // EP4.15: each empire reads its OWN row, so the Zomboss ask is 0 because that row is absent,
        // not because a branch refuses non-Dave empires.
        var player = _store.CreatePlayer("SpeciesBaselineEmpire");
        SeedSpeciesLevel(player.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");

        Assert.True(_store.SpeciesBaselineAllocation(
            player.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Dave)
            .TotalForScope(AllocationScope.CreatureType) > 0);

        Assert.Equal(0, _store.SpeciesBaselineAllocation(
            player.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Zomboss)
            .TotalForScope(AllocationScope.CreatureType));

        SeedSpeciesLevel(player.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom",
            FusionRpg.Core.Commanders.EmpireId.Zomboss);
        Assert.True(_store.SpeciesBaselineAllocation(
            player.Id, "fumeshroom", RealTuning, FusionRpg.Core.Commanders.EmpireId.Zomboss)
            .TotalForScope(AllocationScope.CreatureType) > 0);
    }

    [Fact]
    public void EffectiveSpeciesAllocation_atLevelOne_isEmpty()
    {
        var player = _store.CreatePlayer("SpeciesAllocLevelOne");
        // No SeedSpeciesLevel call at all -- GetRpgActor returns null, EffectiveSpeciesAllocation
        // must default to level 1, matching RpgActorState's own default (RpgStore.Progression.cs).
        var effective = _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning);
        Assert.Equal(0, effective.TotalForScope(AllocationScope.CreatureType));
    }

    [Fact]
    public void EffectiveSpeciesAllocation_override_replaces_the_baseline_wholesale()
    {
        var player = _store.CreatePlayer("SpeciesAllocOverride");
        SeedSpeciesLevel(player.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");
        var baseline = _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning);
        Assert.True(baseline.PointsAt(AllocationScope.CreatureType, "Fortitude") > 0); // present in the baseline

        // A DIFFERENT vector spending the same budget on ONE aptitude only -- if override merely
        // layered onto the baseline, Fortitude would still show up; a true replace zeroes it.
        var budget = baseline.TotalForScope(AllocationScope.CreatureType);
        var wholeVectorOverride = AptitudeAllocation.Single(AllocationScope.CreatureType, "Ferocity", budget);
        _store.SaveAllocation(AllocationScope.CreatureType, SpeciesAllocation.ScopeKey(player.Id, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom"), wholeVectorOverride);

        var effective = _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning);
        Assert.Equal(budget, effective.PointsAt(AllocationScope.CreatureType, "Ferocity"));
        Assert.Equal(0, effective.PointsAt(AllocationScope.CreatureType, "Might"));
        Assert.Equal(0, effective.PointsAt(AllocationScope.CreatureType, "Fortitude"));
    }

    [Fact]
    public void EffectiveSpeciesAllocation_deletingTheOverride_returnsExactlyTheBaseline_forFree()
    {
        var player = _store.CreatePlayer("SpeciesAllocRevert");
        SeedSpeciesLevel(player.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");
        var baseline = _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning);

        var budget = baseline.TotalForScope(AllocationScope.CreatureType);
        _store.SaveAllocation(AllocationScope.CreatureType, SpeciesAllocation.ScopeKey(player.Id, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom"),
            AptitudeAllocation.Single(AllocationScope.CreatureType, "Ferocity", budget));
        Assert.NotEqual(baseline.PointsAt(AllocationScope.CreatureType, "Might"),
            _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning).PointsAt(AllocationScope.CreatureType, "Might"));

        // "Deleting the row" == saving Empty (SaveAllocation's own delete-then-insert-nonzero shape
        // leaves no rows for an all-zero save) -- reverting is free, no soul cost, no separate API.
        _store.SaveAllocation(AllocationScope.CreatureType, SpeciesAllocation.ScopeKey(player.Id, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom"), AptitudeAllocation.Empty);

        var reverted = _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning);
        foreach (var apt in AptitudeCatalog.All)
            Assert.Equal(baseline.PointsAt(AllocationScope.CreatureType, apt.Id), reverted.PointsAt(AllocationScope.CreatureType, apt.Id));
    }

    [Fact]
    public void EffectiveSpeciesAllocation_isPerPlayer_twoPlayersSameSpeciesSameLevel_oneOverridden()
    {
        var alice = _store.CreatePlayer("SpeciesAllocAlice");
        var bob = _store.CreatePlayer("SpeciesAllocBob");
        SeedSpeciesLevel(alice.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");
        SeedSpeciesLevel(bob.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");

        var budget = _store.EffectiveSpeciesAllocation(alice.Id, "fumeshroom", RealTuning).TotalForScope(AllocationScope.CreatureType);
        _store.SaveAllocation(AllocationScope.CreatureType, SpeciesAllocation.ScopeKey(alice.Id, FusionRpg.Core.Commanders.EmpireId.Dave, "fumeshroom"),
            AptitudeAllocation.Single(AllocationScope.CreatureType, "Ferocity", budget));

        var aliceEffective = _store.EffectiveSpeciesAllocation(alice.Id, "fumeshroom", RealTuning);
        var bobEffective = _store.EffectiveSpeciesAllocation(bob.Id, "fumeshroom", RealTuning);

        Assert.Equal(budget, aliceEffective.PointsAt(AllocationScope.CreatureType, "Ferocity"));
        Assert.Equal(0, bobEffective.PointsAt(AllocationScope.CreatureType, "Ferocity")); // Bob still reads his own baseline
        Assert.True(bobEffective.PointsAt(AllocationScope.CreatureType, "Might") > 0);
    }

    [Fact]
    public void ScopesSum_inTheAllocationObject_butEachScopesOwnShareIsWhatAResolveReadsSince61()
    {
        // Rewritten for species-progression step 6.1 (spec-species-layer-delivery.md, "Rewrite,
        // never re-bless, the tests whose subject is the merge"): `commander + species` still builds
        // ONE AptitudeAllocation whose Total/GrandTotal/Share sum both scopes exactly as before --
        // that ALLOCATION-OBJECT contract is unchanged and still proven here. What changed is that a
        // REAL resolve (AptitudeResolver.Resolve, since step 6.1) never reads this merged .Share() at
        // all: it reads .ShareWithinScope(scope, aptitudeId) for each scope alone, so "share taken on
        // the sum" no longer describes what a composed channel value sees. That per-layer contract is
        // proven directly against the resolver in
        // AptitudeResolverTests.AddingPointsToASecondScope_leavesTheFirstScopesOwnContributionUnchanged
        // (`tests/FusionRpg.Core.Tests/ClassSystem/AptitudeResolverTests.cs`).
        var player = _store.CreatePlayer("SpeciesAllocScopeSum");
        SeedSpeciesLevel(player.Id, FumeshroomCreatureTypeId, level: 21, "fumeshroom");
        _store.SaveAllocation(AllocationScope.Commander, "player:" + player.Id,
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 40));

        var commander = _store.LoadAllocation(AllocationScope.Commander, "player:" + player.Id);
        var species = _store.EffectiveSpeciesAllocation(player.Id, "fumeshroom", RealTuning);
        var combined = commander + species;

        var mightTotal = combined.Total("Might");
        Assert.Equal(40 + species.PointsAt(AllocationScope.CreatureType, "Might"), mightTotal);
        // .Share() itself is still "on the sum" -- an allocation-object fact (AptitudeAllocation's own
        // Total/GrandTotal contract), not a claim about what AptitudeResolver.Resolve reads since 6.1.
        Assert.Equal((double)mightTotal / combined.GrandTotal(), combined.Share("Might"));
    }
}
