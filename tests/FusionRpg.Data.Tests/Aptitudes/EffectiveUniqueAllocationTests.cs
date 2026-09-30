using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Aptitudes;

/// <summary>EP1.13 (spec-default-build.md, read in full this session) —
/// <see cref="RpgStore.EffectiveUniqueAllocation"/>: explicit wins wholesale, else the assign ladder's
/// suggestion at the specimen's own <see cref="AllocationScope.UniqueCreature"/> budget. Covers the
/// module's own testing-strategy slice: the default exists (1), explicit replaces wholesale (2), level
/// 1 is empty (3), and the ownership row (6). Cache-trigger tests (4) and the Guard/golden tests (5, 8)
/// belong to EP1.14/EP1.15, which wire production readers through this resolver.
///
/// <para><b>Shared-static-catalog collection.</b> <see cref="SpeciesBuildPlanCatalog"/> is a global,
/// unscoped singleton with no test-scoping mechanism (its own doc comment). <c>AllocationStoreTests.cs</c>
/// already configures it for the identical "fumeshroom" species; this file joins its
/// "SpeciesBuildPlanCatalogGlobal" xUnit collection so the two classes never run in parallel with each
/// other (Extra Rigor: a test touching process-wide static state stays out of it or shares one
/// serialized collection) — and configures the IDENTICAL dictionary, so whichever static ctor runs
/// last is never observably different from the first.</para>
/// </summary>
[Trait("VerificationId", "data.effective-unique-allocation")]
[Collection("SpeciesBuildPlanCatalogGlobal")]
public class EffectiveUniqueAllocationTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    const long PlayerId = 1;
    const string FumeshroomSpeciesId = "fumeshroom";

    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        CreatureSpeciesCatalog.All.First(s => s.DeployMode != CreatureDeployMode.HypnoAlly);

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    // Same shipped tuning file every other Data.Tests file in this assembly reads (AllocationRespecTests.cs,
    // AllocationStoreTests.cs) -- the real point economy, not a hand-typed stand-in.
    static readonly AptitudeTuning RealTuning = AptitudeTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitudes.v10.json")));

    static EffectiveUniqueAllocationTests()
    {
        // Identical dictionary to AllocationStoreTests.cs's own static ctor -- see this file's own doc
        // comment on why that matters for the shared "SpeciesBuildPlanCatalogGlobal" collection.
        SpeciesBuildPlanCatalog.Configure(new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            [FumeshroomSpeciesId] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["Might"] = 500, ["Vigor"] = 300, ["Fortitude"] = 200
            }
        });
    }

    public EffectiveUniqueAllocationTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        // The active-preset rung's own math (MaterializePresetRowsAtUniqueBudget) reads this hub only
        // when a preset is active; no test here activates one, but the ladder walk itself always reads
        // AptitudePresetTuningHub.Tuning.AssignLadder to know its own order.
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(
            1, 1, SoftMaxPresets: 32, DefaultRowAbsMax: 1000,
            AssignLadder: new AssignLadderTuning(new[]
            {
                AptitudeAutoAssignRules.ActivePreset, AptitudeAutoAssignRules.SpeciesFavour,
                AssignLadder.PostureRung, AptitudeAutoAssignRules.Even
            })));
    }

    public void Dispose() => _testStore.Dispose();

    EmpireRef HumanOwner => new(new SaveId(PlayerId), _store.HumanEmpireOf(PlayerId));

    string MintAtLevel(string speciesId, long level, ulong seed)
    {
        var instanceId = _store.MintForEmpire(HumanOwner, speciesId, seed).Actor.InstanceId;
        if (level != 1)
        {
            using var db = SqliteConnectionFactory.Open(_store.HotPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE rpg_unique_actors SET level = $lvl WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$lvl", level);
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.ExecuteNonQuery();
        }
        return instanceId;
    }

    // ---- Test 1: the default exists --------------------------------------------------------------

    [Fact]
    public void A_levelled_specimen_with_no_explicit_allocation_resolves_the_species_favour_distribution_notZero()
    {
        var instanceId = MintAtLevel(FumeshroomSpeciesId, level: 21, seed: 1);

        var effective = _store.EffectiveUniqueAllocation(instanceId, RealTuning);

        Assert.True(effective.IsDefault);
        Assert.Equal(AptitudeAutoAssignRules.SpeciesFavour, effective.DefaultRuleId);
        // The relation the spec asks for -- never a literal point value: fumeshroom's own plan row
        // favours Might (500‰) over Vigor (300‰) over Fortitude (200‰), so the resolved points must
        // hold the same ORDER, and their total must equal the specimen's own UniqueCreature budget.
        var might = effective.Allocation.PointsAt(AllocationScope.UniqueCreature, "Might");
        var vigor = effective.Allocation.PointsAt(AllocationScope.UniqueCreature, "Vigor");
        var fortitude = effective.Allocation.PointsAt(AllocationScope.UniqueCreature, "Fortitude");
        Assert.True(might > vigor);
        Assert.True(vigor > fortitude);

        var source = PointBudget.UniqueCreatureSourceFromLevel(21);
        var budget = PointBudget.PointsFor(AllocationScope.UniqueCreature, source, RealTuning);
        Assert.True(budget > 0, "expected a nonzero UniqueCreature budget at level 21 on the shipped tuning");
        Assert.Equal(budget, effective.Allocation.TotalForScope(AllocationScope.UniqueCreature));
    }

    // ---- Test 2: explicit replaces wholesale (D2) --------------------------------------------------

    [Fact]
    public void One_explicit_point_replaces_the_whole_default()
    {
        var instanceId = MintAtLevel(FumeshroomSpeciesId, level: 21, seed: 2);
        var beforeExplicit = _store.EffectiveUniqueAllocation(instanceId, RealTuning);
        Assert.True(beforeExplicit.IsDefault);
        Assert.True(beforeExplicit.Allocation.PointsAt(AllocationScope.UniqueCreature, "Vigor") > 0);

        // ONE explicit point (Might=1) -- D2 says this replaces the default WHOLESALE, never tops it up.
        _store.SaveAllocation(AllocationScope.UniqueCreature, instanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 1));

        var effective = _store.EffectiveUniqueAllocation(instanceId, RealTuning);
        Assert.False(effective.IsDefault);
        Assert.Null(effective.DefaultRuleId);
        Assert.Equal(1, effective.Allocation.TotalForScope(AllocationScope.UniqueCreature));
        Assert.Equal(1, effective.Allocation.PointsAt(AllocationScope.UniqueCreature, "Might"));
        // The default's own Vigor points are GONE, not left over from a top-up.
        Assert.Equal(0, effective.Allocation.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    // ---- Test 3: level 1 is empty -------------------------------------------------------------------

    [Fact]
    public void A_freshLevelOne_specimen_resolves_empty_withIsDefaultTrue()
    {
        var instanceId = MintAtLevel(FumeshroomSpeciesId, level: 1, seed: 3);

        var effective = _store.EffectiveUniqueAllocation(instanceId, RealTuning);

        Assert.True(effective.IsDefault);
        Assert.Equal(0, effective.Allocation.TotalForScope(AllocationScope.UniqueCreature));
    }

    // ---- Test 6: the ownership row -------------------------------------------------------------------

    [Fact]
    public void A_resolved_default_allocation_carries_no_creatureType_scope_points()
    {
        var instanceId = MintAtLevel(FumeshroomSpeciesId, level: 21, seed: 4);

        var effective = _store.EffectiveUniqueAllocation(instanceId, RealTuning);

        Assert.True(effective.Allocation.TotalForScope(AllocationScope.UniqueCreature) > 0);
        Assert.Equal(0, effective.Allocation.TotalForScope(AllocationScope.CreatureType));
    }

    [Fact]
    public void A_resolved_explicit_allocation_also_carries_no_creatureType_scope_points()
    {
        var instanceId = MintAtLevel(CatalogSpecies.SpeciesId, level: 10, seed: 5);
        _store.SaveAllocation(AllocationScope.UniqueCreature, instanceId,
            AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 3));

        var effective = _store.EffectiveUniqueAllocation(instanceId, RealTuning);

        Assert.Equal(0, effective.Allocation.TotalForScope(AllocationScope.CreatureType));
    }
}
