using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Consumables;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>
/// D3.19 (spec-loot-pack.md §2) — `PackFootprintTable.Build` at load. Reads the REAL, shipped
/// `dungeon.v1.json` and the real `gk-data/packs/fusion/data/seed/items/base-types/` corpus (`DungeonTestFiles`'s own
/// established reason: "the balance surface is the file", not a fixture that can drift from it) —
/// this is also the regression that proves `dungeon.v1.json`'s `pack.footprint.role`/`.massStep`/
/// `pack.stack.materialClass` blocks actually use real `ItemRole`/mass-class/`MaterialClass`
/// vocabulary, a real mismatch this task found and fixed (see the todo's own D3.19 entry).
/// </summary>
public class PackFootprintTableTests
{
    static DungeonTuning RealTuning()
    {
        var registries = DungeonRegistryLoader.LoadAll(DungeonTestFiles.RegistryDir());
        return DungeonTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.DungeonTuningPath()), registries);
    }

    static string BaseTypesDir() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types");

    static PackTuning FixtureTuning() => new(
        RoleCells: new Dictionary<ItemRole, int> { [ItemRole.Sense] = 1, [ItemRole.CoreGuard] = 4 },
        MassSteps: new Dictionary<string, int> { ["light"] = -1, ["medium"] = 0, ["heavy"] = 1 });

    // ---- ResolveTuning: the real dungeon.v1.json cross-checked against real item vocabulary ----

    [Fact]
    public void ResolveTuning_accepts_the_real_shipped_dungeon_v1_json()
    {
        var tuning = PackFootprintTable.ResolveTuning(RealTuning());
        // Formula, not a literal (population-pin SE3.3, 2026-09-19): every ItemRole except Standard --
        // same shape SlotRolesTests/SocketGeometryTests prove for the same closed enum.
        Assert.Equal(Enum.GetValues<ItemRole>().Length - 1, tuning.RoleCells.Count);
        Assert.Equal(5, tuning.MassSteps.Count);   // light, medium-light, medium, medium-heavy, heavy
    }

    [Fact]
    public void ResolveTuning_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => PackFootprintTable.ResolveTuning(null!));
    }

    [Fact]
    public void ResolveTuning_refuses_an_unparseable_role_key_by_name()
    {
        var bad = RealTuning() with { PackFootprintRole = new Dictionary<string, int> { ["not-a-real-role"] = 3 } };
        var ex = Assert.Throws<PackRejection>(() => PackFootprintTable.ResolveTuning(bad));
        Assert.Contains("not-a-real-role", ex.Message);
    }

    [Fact]
    public void ResolveTuning_refuses_an_unknown_mass_step_key_by_name()
    {
        var bad = RealTuning() with { PackFootprintMassStep = new Dictionary<string, int> { ["ultralight"] = -1 } };
        var ex = Assert.Throws<PackRejection>(() => PackFootprintTable.ResolveTuning(bad));
        Assert.Contains("ultralight", ex.Message);
    }

    // ---- Build: named refusals ----

    [Fact]
    public void Build_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackFootprintTable.Build(null!, FixtureTuning()));
        Assert.Throws<ArgumentNullException>(() => PackFootprintTable.Build(Array.Empty<BaseTypeEntry>(), null!));
    }

    [Fact]
    public void Build_derives_a_real_footprint_for_a_well_formed_entry()
    {
        var entries = new[] { new BaseTypeEntry("item.test-sense", "sense", new[] { "medium" }) };
        var result = PackFootprintTable.Build(entries, FixtureTuning());
        Assert.Equal((1, 1), result["item.test-sense"]);
    }

    [Fact]
    public void Build_skips_disabled_entries()
    {
        var entries = new[] { new BaseTypeEntry("item.retired", "sense", new[] { "medium" }, Enabled: false) };
        var result = PackFootprintTable.Build(entries, FixtureTuning());
        Assert.Empty(result);
    }

    [Fact]
    public void Build_refuses_an_unknown_role_naming_the_id()
    {
        var entries = new[] { new BaseTypeEntry("item.bad-role", "not-a-role", new[] { "medium" }) };
        var ex = Assert.Throws<PackRejection>(() => PackFootprintTable.Build(entries, FixtureTuning()));
        Assert.Contains("item.bad-role", ex.Message);
    }

    [Fact]
    public void Build_refuses_zero_mass_class_tags_naming_the_id()
    {
        var entries = new[] { new BaseTypeEntry("item.no-mass", "sense", new[] { "flavor:fire" }) };
        var ex = Assert.Throws<PackRejection>(() => PackFootprintTable.Build(entries, FixtureTuning()));
        Assert.Contains("item.no-mass", ex.Message);
    }

    [Fact]
    public void Build_refuses_two_mass_class_tags_naming_the_id()
    {
        var entries = new[] { new BaseTypeEntry("item.two-mass", "sense", new[] { "light", "heavy" }) };
        var ex = Assert.Throws<PackRejection>(() => PackFootprintTable.Build(entries, FixtureTuning()));
        Assert.Contains("item.two-mass", ex.Message);
    }

    // ---- ForConsumable / ForMaterial: the real dungeon.v1.json values ----

    [Theory]
    [InlineData(ConsumableClass.Restore)]
    [InlineData(ConsumableClass.Draught)]
    [InlineData(ConsumableClass.Ward)]
    [InlineData(ConsumableClass.Board)]
    [InlineData(ConsumableClass.Revive)]
    [InlineData(ConsumableClass.Utility)]
    public void ForConsumable_resolves_every_real_class_against_the_shipped_file(ConsumableClass classId)
    {
        var (cells, stackCap) = PackFootprintTable.ForConsumable(classId, RealTuning());
        Assert.True(cells >= 1);
        Assert.True(stackCap >= 1);
    }

    [Theory]
    [InlineData(MaterialClass.Shard)]
    [InlineData(MaterialClass.Substrate)]
    [InlineData(MaterialClass.Essence)]
    [InlineData(MaterialClass.Catalyst)]
    public void ForMaterial_resolves_every_real_class_against_the_shipped_file(MaterialClass materialClass)
    {
        Assert.True(PackFootprintTable.ForMaterial(materialClass, RealTuning()) >= 1);
    }

    [Fact]
    public void ForMaterial_refuses_Souls_a_ledger_balance_not_a_stack()
    {
        Assert.Throws<PackRejection>(() => PackFootprintTable.ForMaterial(MaterialClass.Souls, RealTuning()));
    }

    // ---- LoadBaseTypeEntries + the full end-to-end pipeline against the real corpus ----

    [Fact]
    public void LoadBaseTypeEntries_reads_the_real_corpus_and_finds_hundreds_of_rows()
    {
        var entries = PackFootprintTable.LoadBaseTypeEntries(BaseTypesDir());
        Assert.True(entries.Count > 500, $"expected hundreds of base-type entries, found {entries.Count}");
        Assert.Contains(entries, e => !string.IsNullOrEmpty(e.Role));
    }

    /// <summary>
    /// The five mass-class tag ids §9's footprint formula reads, mirroring `PackFootprintTable`'s own
    /// set. Kept here so the test computes the gap from the corpus rather than quoting a stale list.
    /// </summary>
    static readonly IReadOnlySet<string> MassClassTagIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "light", "medium-light", "medium", "medium-heavy", "heavy",
    };

    /// <summary>
    /// CONTRACT, not a hardcoded gap list: an authored base type must carry EXACTLY ONE mass-class tag,
    /// and the set of entries that do not is COMPUTED from the shipped corpus on every run. The item
    /// corpus is generator-authored and grows every generation (720 → 1178 entries and climbing), so a
    /// pinned list of offender ids is stale the moment content ships — the previous 12-id list named
    /// only the first batch of offenders and hid the 100+ the next batch added. Computing the set keeps
    /// the assertion meaningful at any corpus size.
    /// </summary>
    static IReadOnlyDictionary<string, BaseTypeEntry> RealEntriesById() =>
        PackFootprintTable.LoadBaseTypeEntries(BaseTypesDir()).ToDictionary(e => e.Id, StringComparer.Ordinal);

    static bool HasExactlyOneMassClass(BaseTypeEntry e) =>
        e.Tags.Count(MassClassTagIds.Contains) == 1;

    [Fact]
    public void Every_base_type_without_exactly_one_mass_class_refuses_by_name()
    {
        // The fail-fast contract: a load-time content defect halts loudly naming the id rather than
        // silently dropping or guessing a size for real, shippable content. Driven off the computed
        // gap, so a corpus that fixes some entries and breaks others still gets the right verdict.
        var tuning = PackFootprintTable.ResolveTuning(RealTuning());
        var entries = RealEntriesById();
        var gap = entries.Values.Where(e => e.Enabled && !HasExactlyOneMassClass(e)).ToList();

        Assert.NotEmpty(gap); // the gap is real today; a corpus that closes it outright may revisit this
        foreach (var entry in gap)
        {
            var ex = Assert.Throws<PackRejection>(() => PackFootprintTable.Build(new[] { entry }, tuning));
            Assert.Contains("mass-class-count", ex.Message);
        }
    }

    [Fact]
    public void The_real_corpus_with_a_single_mass_class_builds_end_to_end_with_no_refusal()
    {
        // The strongest proof available short of the content fix: every enabled real base type that
        // DOES carry exactly one mass-class tag derives a real footprint against the shipped
        // dungeon.v1.json, once ResolveTuning's own cross-check passes. This is also what proves the
        // dungeon.v1.json fix (real ItemRole/mass-class vocabulary, not placeholder keys) actually
        // unblocks the real content, not just a fixture. The eligible set is computed, not "all minus
        // a pinned list".
        var tuning = PackFootprintTable.ResolveTuning(RealTuning());
        var entries = PackFootprintTable.LoadBaseTypeEntries(BaseTypesDir())
            .Where(e => e.Enabled && HasExactlyOneMassClass(e))
            .ToList();
        Assert.True(entries.Count > 500, $"expected hundreds of real entries, found {entries.Count}");

        var result = PackFootprintTable.Build(entries, tuning);

        Assert.Equal(entries.Count, result.Count);
        Assert.All(result.Values, wh => Assert.True(wh.W >= 1 && wh.H >= 1 && wh.W <= 4 && wh.H <= 4));
    }
}
