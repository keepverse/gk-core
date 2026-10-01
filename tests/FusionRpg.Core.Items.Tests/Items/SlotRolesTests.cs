using FusionRpg.Core.Items;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `slot-roles` (item module 3) against the REAL, shipped `core.v1.json` — never a copy or a
/// fixture, because the whole point of <see cref="ItemRoleRegistry"/> is that weights are read, not
/// transcribed, and a test against a fixture could not catch a transcription drifting from the file
/// it claims to mirror.
/// </summary>
public class SlotRolesTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static IReadOnlyList<ItemRoleDef> LoadRegistry()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry", "core.v1.json");
        return ItemRoleRegistry.Parse(File.ReadAllText(path));
    }

    // The twelve, enumerated explicitly (D3, corrected by D30) -- a generator input, never a
    // recomputation, matching spec-slot-roles.md's own table.
    static readonly string[] ExpectedHybridCore =
    {
        "armament-primary", "core-guard", "armament-secondary", "jewel-major", "manipulator",
        "mantle", "girdle", "footing", "infusion", "retinue", "jewel-minor-a", "jewel-minor-b",
    };

    [Fact]
    public void No_second_roles_registry_exists()
    {
        // The two-sources-of-truth guard: a roles.v1.json would look just as authoritative as
        // core.v1.json's own roles.list, and this program refuses that pattern everywhere else.
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry", "roles.v1.json");
        Assert.False(File.Exists(path), "a second roles registry must never exist");
    }

    [Fact]
    public void The_fifteen_role_weights_sum_to_1000()
    {
        var defs = LoadRegistry();
        var bodyRoles = defs.Where(d => d.Role != ItemRole.Standard).ToList();

        // Formula, not a literal (population-pin SE3.3, 2026-09-19): every ItemRole except Standard --
        // same shape SocketGeometryTests proves for the same closed enum.
        Assert.Equal(Enum.GetValues<ItemRole>().Length - 1, bodyRoles.Count);
        Assert.Equal(1000, bodyRoles.Sum(d => d.BudgetWeightMilli));
    }

    [Fact]
    public void The_hybrid_core_is_twelve_roles_summing_to_800()
    {
        var defs = LoadRegistry();
        var core = ItemRoleRegistry.HybridCore(defs);

        // The count is fully implied by the sibling test The_hybrid_core_names_exactly_D3s_twelve_roles's
        // own element-wise equality -- never pinned separately (population-pin SE3.3, 2026-09-19).
        Assert.Equal(800, core.Sum(d => d.BudgetWeightMilli));
    }

    [Fact]
    public void The_hybrid_core_names_exactly_D3s_twelve_roles()
    {
        var defs = LoadRegistry();
        var coreIds = ItemRoleRegistry.HybridCore(defs).Select(d => ItemRoles.Id(d.Role)).OrderBy(s => s);

        Assert.Equal(ExpectedHybridCore.OrderBy(s => s), coreIds);
    }

    [Fact]
    public void The_hybrid_core_contains_all_three_jewel_roles()
    {
        // D3's own prose said "both jewels" where three jewel roles are kept -- the eleven-vs-twelve
        // defect, asserted directly.
        var coreIds = ItemRoleRegistry.HybridCore(LoadRegistry()).Select(d => ItemRoles.Id(d.Role)).ToHashSet();

        Assert.Contains("jewel-major", coreIds);
        Assert.Contains("jewel-minor-a", coreIds);
        Assert.Contains("jewel-minor-b", coreIds);
    }

    [Fact]
    public void Jewel_minor_b_is_in_the_hybrid_core()
    {
        // D3 wins over what the registry shipped before the v2 bump (895‰/13-role shape).
        Assert.Contains(ItemRole.JewelMinorB, ItemRoleRegistry.HybridCore(LoadRegistry()).Select(d => d.Role));
    }

    [Fact]
    public void Footing_is_in_the_hybrid_core()
    {
        // D3's CRITERION, not just its answer: footing is the frame-split showcase and is kept on
        // purpose, reversing an earlier same-day recommendation to drop it.
        Assert.Contains(ItemRole.Footing, ItemRoleRegistry.HybridCore(LoadRegistry()).Select(d => d.Role));
    }

    [Fact]
    public void Head_guard_and_sense_and_ward_array_are_excluded_from_the_hybrid_core()
    {
        var coreIds = ItemRoleRegistry.HybridCore(LoadRegistry()).Select(d => ItemRoles.Id(d.Role)).ToHashSet();

        Assert.DoesNotContain("head-guard", coreIds);
        Assert.DoesNotContain("sense", coreIds);
        Assert.DoesNotContain("ward-array", coreIds);
    }

    [Fact]
    public void The_registry_and_the_seedsmith_python_constants_name_the_same_twelve()
    {
        // §2g #0a's reconciliation, over all sources at once: the registry (read here), and
        // seedsmith's registries.py/linkage.py (read as text, since this is a C# test project).
        var registryCore = ItemRoleRegistry.HybridCore(LoadRegistry()).Select(d => ItemRoles.Id(d.Role)).OrderBy(s => s).ToList();

        var registriesPy = File.ReadAllText(Path.Combine(KeepverseRoots.Forge(), "tools", "seedsmith", "seedsmith",
            "adapters", "items", "registries.py"));
        var linkagePy = File.ReadAllText(Path.Combine(KeepverseRoots.Forge(), "tools", "seedsmith", "seedsmith",
            "metrics", "linkage.py"));

        // Both python sources name the THREE DROPPED roles (the complement), not the twelve kept --
        // assert the complement matches, which is equivalent and is what those files actually declare.
        var expectedDrops = new[] { "ward-array", "head-guard", "sense" };
        foreach (var dropped in expectedDrops)
        {
            Assert.Contains($"\"{dropped}\"", registriesPy);
            Assert.Contains($"\"{dropped}\"", linkagePy);
        }
        Assert.DoesNotContain("\"jewel-minor-b\"", ExtractExcludedRolesLine(registriesPy));

        Assert.Equal(ExpectedHybridCore.OrderBy(s => s), registryCore);
    }

    static string ExtractExcludedRolesLine(string pythonSource)
    {
        var idx = pythonSource.IndexOf("HYBRID_FRAME_EXCLUDED_ROLES", StringComparison.Ordinal);
        Assert.True(idx >= 0, "HYBRID_FRAME_EXCLUDED_ROLES not found");
        var end = pythonSource.IndexOf('\n', idx);
        return pythonSource[idx..(end < 0 ? pythonSource.Length : end)];
    }

    [Fact]
    public void Each_role_has_a_name_in_both_frame_vocabularies()
    {
        foreach (var d in LoadRegistry().Where(d => d.Role != ItemRole.Standard))
        {
            Assert.False(string.IsNullOrWhiteSpace(FrameVocabulary.NameOf(d, ItemFrame.Humanoid)));
            Assert.False(string.IsNullOrWhiteSpace(FrameVocabulary.NameOf(d, ItemFrame.Plant)));
        }
    }

    [Fact]
    public void Standard_is_declared_with_its_own_commander_budget()
    {
        var standard = LoadRegistry().Single(d => d.Role == ItemRole.Standard);
        Assert.False(string.IsNullOrWhiteSpace(standard.HumanoidName));
    }

    // ---- D2: the unlock predicate --------------------------------------------------------------

    [Fact]
    public void Every_slot_is_open_with_no_rule_configured()
    {
        var unlock = new SlotUnlock();
        Assert.True(unlock.IsUnlocked(ItemRole.ArmamentPrimary, new ActorContext("actor-1", Level: 1)));
    }

    sealed class LevelGate : ISlotUnlockRule
    {
        public bool Evaluate(ItemRole role, ActorContext actor) => actor.Level >= 10;
    }

    [Fact]
    public void A_configured_rule_can_close_a_slot_without_a_migration()
    {
        var unlock = new SlotUnlock(new LevelGate());

        Assert.False(unlock.IsUnlocked(ItemRole.JewelMajor, new ActorContext("actor-1", Level: 1)));
        Assert.True(unlock.IsUnlocked(ItemRole.JewelMajor, new ActorContext("actor-1", Level: 10)));
    }

    // ---- D14: standard is declared, never generated -------------------------------------------

    [Fact]
    public void Every_shipped_standard_base_type_is_retired()
    {
        foreach (var file in new[] { "humanoid-standard.json", "plant-standard.json" })
        {
            var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types", file);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                Assert.True(entry.TryGetProperty("enabled", out var enabled), $"{file}: entry missing 'enabled'");
                Assert.False(enabled.GetBoolean(), $"{file}: a standard base type must be retired (enabled: false)");

                // `seed-contract.md` §7.2 is "retire, don't delete" — and a retirement with no stated
                // reason is a deletion with the row left behind. The owner's ruling was both halves;
                // this assert is the half that was unguarded until 2026-09-06.
                Assert.True(entry.TryGetProperty("retiredReason", out var reason),
                    $"{file}: a retired entry must say why (retiredReason)");
                Assert.False(string.IsNullOrWhiteSpace(reason.GetString()),
                    $"{file}: retiredReason must not be blank");
            }
        }
    }
}
