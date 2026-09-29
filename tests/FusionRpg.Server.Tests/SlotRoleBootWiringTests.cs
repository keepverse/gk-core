using FusionRpg.Core.Items;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// The boot wire for module 3's slot-role tables. <c>RpgStore.SeedRoles</c> was built and proven but
/// had **no production caller**, so <c>item_role</c>/<c>item_role_frame</c> were empty in a real
/// deployed database (item-todo.md P1.3's own 2026-09-06 bullet). <c>Program.cs</c> now calls it from
/// the shipped registry, which is what this file proves from the DEPLOYED path rather than from a
/// test-local JSON: the registry the wire reads is the one the Server project copies to
/// <c>&lt;base&gt;/data/seed/items/_registry/</c>, so a missing or unparsable file is caught here
/// rather than on a player's first boot.
/// </summary>
public class SlotRoleBootWiringTests
{
    readonly DataTestStore _testStore = DataTestStore.Create();

    RpgStore Store => _testStore.Store;

    /// <summary>The exact path <c>Program.cs</c> reads, relative to the deployed server — the same
    /// <c>AppContext.BaseDirectory</c> + <c>gk-data/packs/fusion/data/seed/items/_registry</c> shape the workbench block
    /// beside it uses, and the reason that block's own named blocker ("where does this file live at
    /// runtime") no longer applies.</summary>
    static string DeployedRegistryPath() => Path.Combine(
        AppContext.BaseDirectory, "data", "seed", "items", "_registry", "core.v1.json");

    [Fact]
    public void The_slot_role_registry_ships_to_the_deployed_server()
    {
        var path = DeployedRegistryPath();
        Assert.True(File.Exists(path), $"the shipped role registry is not deployed at {path}");
        Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(path)));
    }

    [Fact]
    public void Seeding_the_deployed_registry_populates_the_whole_closed_role_vocabulary()
    {
        Store.SeedRoles(File.ReadAllText(DeployedRegistryPath()));

        var roles = Store.ListRoles();

        // CONTRACT, not a count: the closed vocabulary is `ItemRole`, which the code owns and a human
        // changes — so every member must be seeded, and a registry that drops one fails here. A
        // literal row total would be a population pin and is deliberately absent.
        var seeded = roles.Select(r => r.RoleId).ToHashSet(StringComparer.Ordinal);
        foreach (ItemRole role in Enum.GetValues(typeof(ItemRole)))
            Assert.Contains(ItemRoles.Id(role), seeded);
        Assert.Equal(seeded.Count, roles.Count);   // no duplicate rows
    }

    [Fact]
    public void The_seeded_frame_legality_reproduces_the_registrys_own_hybrid_rule()
    {
        var json = File.ReadAllText(DeployedRegistryPath());
        Store.SeedRoles(json);

        // Read the SHIPPED registry's own answer and assert the table reproduces it — a faithful
        // transcription, never a guessed role/frame pair. `SeedRoles` writes humanoid and plant as
        // always-legal and hybrid from the row's own `hybridEligible`, so hybrid is the one frame
        // axis the registry actually varies and the one this can test.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("roles").GetProperty("list").EnumerateArray().ToList();
        Assert.NotEmpty(rows);

        var ineligible = 0;
        foreach (var row in rows)
        {
            var roleId = row.GetProperty("roleId").GetString()!;
            var hybridEligible = row.GetProperty("hybridEligible").GetBoolean();
            if (!hybridEligible) ineligible++;

            Assert.True(Store.IsRoleLegalForFrame(roleId, "humanoid"), $"{roleId} must be legal on humanoid");
            Assert.True(Store.IsRoleLegalForFrame(roleId, "plant"), $"{roleId} must be legal on plant");
            Assert.Equal(hybridEligible, Store.IsRoleLegalForFrame(roleId, "hybrid"));
        }

        // The rule has to bite somewhere, or the loop above is vacuous: `core.v1.json`'s own `frames`
        // note says hybrid drops three of the fifteen roles, and this is the reading that proves the
        // seeded table carries that rather than a uniform `true`.
        Assert.True(ineligible > 0, "the shipped registry marks no role hybrid-ineligible — the assertion would be vacuous");
    }
}
