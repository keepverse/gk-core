using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>`item_role`/`item_role_frame` — the SQL-joinable mirror of `core.v1.json`'s own roles,
/// reseeded from the registry JSON, never hand-populated.</summary>
public class SlotRolesTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SlotRolesTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    const string RegistryJson = """
        {
          "roles": {
            "budgetWeightMilliTotal": 1000,
            "list": [
              { "roleId": "armament-primary", "humanoidName": "main-hand", "plantName": "muzzle",
                "hybridEligible": true, "budgetWeightMilli": 940 },
              { "roleId": "head-guard", "humanoidName": "head", "plantName": "crown",
                "hybridEligible": false, "budgetWeightMilli": 60 }
            ],
            "commanderOnly": [
              { "roleId": "standard", "humanoidName": "banner", "plantName": "root-totem" }
            ]
          }
        }
        """;

    [Fact]
    public void Seeding_replaces_prior_rows_rather_than_accumulating_them()
    {
        _store.SeedRoles(RegistryJson);
        _store.SeedRoles(RegistryJson);

        var roles = _store.ListRoles();
        Assert.Equal(3, roles.Count); // armament-primary, head-guard, standard -- not 6
    }

    [Fact]
    public void Seeding_reads_hybrid_eligibility_and_weight_from_the_registry()
    {
        _store.SeedRoles(RegistryJson);

        var roles = _store.ListRoles();
        var armament = Assert.Single(roles, r => r.RoleId == "armament-primary");
        Assert.True(armament.HybridEligible);
        Assert.Equal(940, armament.BudgetWeightMilli);

        var headGuard = Assert.Single(roles, r => r.RoleId == "head-guard");
        Assert.False(headGuard.HybridEligible);
    }

    [Fact]
    public void Humanoid_and_plant_may_always_host_every_role()
    {
        _store.SeedRoles(RegistryJson);

        Assert.True(_store.IsRoleLegalForFrame("head-guard", "humanoid"));
        Assert.True(_store.IsRoleLegalForFrame("head-guard", "plant"));
    }

    [Fact]
    public void Hybrid_legality_follows_hybrid_eligible_exactly()
    {
        _store.SeedRoles(RegistryJson);

        Assert.True(_store.IsRoleLegalForFrame("armament-primary", "hybrid"));
        Assert.False(_store.IsRoleLegalForFrame("head-guard", "hybrid"));
    }
}
