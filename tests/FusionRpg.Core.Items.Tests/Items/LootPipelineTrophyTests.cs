using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T34c (`species-materials` d, R22 family roll) — the drop-time resolver that
/// turns a drawn trophy SCOPE TOKEN into the ONE concrete id a kill grants, mirroring T34b's own
/// resolve-time (spend) mechanism for the drop-time (grant) side. Every fixture here is synthetic (a
/// two-group table: one species-trophy leg, one family-trophy leg, each entry vs `Nothing` in its own
/// group per § Design 5's own "independent draw unit" framing) — real trophy content in
/// `gk-data/packs/fusion/data/seed/loot/tables-creature.json` is a separate, content-authoring concern this test file does
/// not depend on.
/// </summary>
public class LootPipelineTrophyTests
{
    const string TableId = "drop.test.trophy-kill";
    const string SpeciesGroup = "species-trophy";
    const string FamilyGroup = "family-trophy";
    const string SourceId = "test-kill";

    static DropTableRow Table(long speciesWeight = 1_000_000, long familyWeight = 1_000_000) => new(
        TableId, new[] { "web" }, null, null, true, 1, new[]
        {
            // Weighted so the trophy entry ALWAYS wins in these tests (never Nothing) -- these tests
            // exercise the resolver, not the draw odds, which are a real content/balance decision the
            // real corpus makes separately.
            new DropTableGroupRow(SpeciesGroup, 0, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Nothing, "", 0),
                new DropTableEntryRow(1, DropEntryKind.Material, "", (int)speciesWeight,
                    TrophyScope: "species", TrophySlot: 1),
            }),
            new DropTableGroupRow(FamilyGroup, 1, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Nothing, "", 0),
                new DropTableEntryRow(1, DropEntryKind.Material, "", (int)familyWeight,
                    TrophyScope: "family", TrophySlot: 1),
            }),
        });

    static LootContentView View(DropTableRow table, Func<string, IReadOnlyList<string>>? familiesForSpecies = null) => new(
        new Dictionary<string, LootSourceRow>(StringComparer.Ordinal)
        {
            [$"creature-kill:{SourceId}"] = new LootSourceRow("creature-kill", SourceId, table.TableId, 10),
        },
        new Dictionary<string, DropTableRow>(StringComparer.Ordinal) { [table.TableId] = table },
        DropVolumeCorpusTests.Ladder(),
        (_, _) => Array.Empty<string>(),
        FamiliesForSpecies: familiesForSpecies);

    static LootRequest Request(string? speciesId, ulong seed = 0xA11CE) =>
        new("player-1", "creature-kill", SourceId, seed, 20, KilledSpeciesId: speciesId);

    static DropVolumeTuning Tuning() => DropVolumeTests.Tuning();

    // A real species, perSpecies = 2 (T34's own real run) -- slot 1 exists in the bootstrap-injected
    // real trophy registry.
    const string RealSpecies = "abyssswordstar";
    const string RealFamily = "flora"; // one of RealSpecies's own real families, per family-map.json

    [Fact]
    public void A_species_trophy_group_resolves_to_the_killed_species_own_concrete_id()
    {
        var table = Table(familyWeight: 0); // isolate the species leg for this assertion
        var view = View(table);
        var request = Request(RealSpecies);

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var m).IsOk);
        var grant = Assert.Single(m!.Grants, g => g.Kind == DropEntryKind.Material);
        Assert.Equal(MaterialCatalog.ComposeTrophyId("species", RealSpecies, 1), grant.RefId);
    }

    [Fact]
    public void A_species_trophy_group_without_a_killed_species_refuses_by_name()
    {
        var table = Table(familyWeight: 0);
        var view = View(table);
        var request = Request(speciesId: null);

        var result = LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out _);
        Assert.False(result.IsOk);
        Assert.Contains("drop.trophy-no-killed-species", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_species_trophy_id_the_registry_does_not_hold_refuses_by_name()
    {
        var table = Table(familyWeight: 0);
        var view = View(table);
        var request = Request("not-a-real-species");

        var result = LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out _);
        Assert.False(result.IsOk);
        Assert.Contains("drop.trophy-slot-unknown", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_family_species_resolves_directly_and_consumes_no_family_index_draw()
    {
        var table = Table(speciesWeight: 0); // isolate the family leg
        var view = View(table, speciesId => speciesId == RealSpecies
            ? new[] { RealFamily } : Array.Empty<string>());
        var request = Request(RealSpecies);

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var m).IsOk);
        var grant = Assert.Single(m!.Grants, g => g.Kind == DropEntryKind.Material);
        Assert.Equal(MaterialCatalog.ComposeTrophyId("family", RealFamily, 1), grant.RefId);
    }

    [Fact]
    public void A_multi_family_species_rolls_the_named_stream_and_replays_deterministically()
    {
        var table = Table(speciesWeight: 0);
        var view = View(table, _ => new[] { "flora", "gourd" }); // both real families, real slot-1 rows exist
        var request = Request(RealSpecies);

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var a).IsOk);
        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var b).IsOk);

        var grantA = Assert.Single(a!.Grants, g => g.Kind == DropEntryKind.Material);
        var grantB = Assert.Single(b!.Grants, g => g.Kind == DropEntryKind.Material);
        // Same (SourceSeed, correlationId) -> same family, every time (determinism, not just re-use of
        // the same manifest object).
        Assert.Equal(grantA.RefId, grantB.RefId);
        Assert.True(grantA.RefId is "trophy.family.flora.1" or "trophy.family.gourd.1");
    }

    [Fact]
    public void A_no_family_species_skips_the_family_group_entirely()
    {
        var table = Table(speciesWeight: 0); // family-only table
        var view = View(table, _ => Array.Empty<string>());
        var request = Request(RealSpecies);

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var m).IsOk);
        Assert.Empty(m!.Grants);
    }

    [Fact]
    public void A_no_family_species_in_a_mixed_group_refuses_rather_than_silently_drops()
    {
        // Defense in depth: the group-skip rule only fires for a group whose non-Nothing entries are
        // ALL family-scope. A group mixing an ordinary entry with a family-trophy entry still draws,
        // and if the family-trophy entry wins for a no-family species, that is refused by name.
        var mixed = new DropTableRow(TableId, new[] { "web" }, null, null, true, 1, new[]
        {
            new DropTableGroupRow(FamilyGroup, 0, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Material, "", 0), // an ordinary material, never drawn (weight 0)
                new DropTableEntryRow(1, DropEntryKind.Material, "", 1_000_000,
                    TrophyScope: "family", TrophySlot: 1),
            }),
        });
        var view = View(mixed, _ => Array.Empty<string>());
        var request = Request(RealSpecies);

        var result = LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out _);
        Assert.False(result.IsOk);
        Assert.Contains("drop.trophy-species-has-no-family", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void No_family_map_delegate_supplied_treats_every_species_as_having_no_family()
    {
        var table = Table(speciesWeight: 0);
        var view = View(table, familiesForSpecies: null);
        var request = Request(RealSpecies);

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var m).IsOk);
        Assert.Empty(m!.Grants);
    }

    [Fact]
    public void Adding_the_trophy_groups_leaves_every_other_groups_own_draws_byte_identical()
    {
        // Stream isolation (§ Design's own named-stream discipline): an ordinary "mats" group in the
        // SAME table draws identically whether or not the trophy groups are present, because every
        // named stream is keyed per (tableId, groupKey) -- adding one group's own stream never
        // perturbs another's.
        DropTableRow WithMats(bool includeTrophyGroups)
        {
            var groups = new List<DropTableGroupRow>
            {
                new("mats", 0, 1, new[]
                {
                    new DropTableEntryRow(0, DropEntryKind.Material, "shard.chaff", 1),
                }),
            };
            if (includeTrophyGroups)
            {
                groups.Add(new DropTableGroupRow(SpeciesGroup, 1, 1, new[]
                {
                    new DropTableEntryRow(0, DropEntryKind.Nothing, "", 0),
                    new DropTableEntryRow(1, DropEntryKind.Material, "", 1_000_000,
                        TrophyScope: "species", TrophySlot: 1),
                }));
            }
            return new DropTableRow(TableId, new[] { "web" }, null, null, true, 1, groups);
        }

        var withoutTrophy = View(WithMats(false));
        var withTrophy = View(WithMats(true));
        var request = Request(RealSpecies);

        Assert.True(LootPipeline.Resolve(request, withoutTrophy, Tuning(), LootPityState.Empty, out var a).IsOk);
        Assert.True(LootPipeline.Resolve(request, withTrophy, Tuning(), LootPityState.Empty, out var b).IsOk);

        var matsA = Assert.Single(a!.Grants, g => g.RefId == "shard.chaff");
        var matsB = Assert.Single(b!.Grants, g => g.RefId == "shard.chaff");
        Assert.Equal(matsA.Count, matsB.Count);
    }

    [Fact]
    public void A_replayed_correlation_id_credits_once()
    {
        // Inherited idempotency (§ Design 5, Strengthen pass item 5): this module adds no write path
        // of its own -- RecordedManifestFor's own gate is exercised generically by LootPipelineTests
        // already; this proves a trophy-bearing manifest is no exception.
        var table = Table(familyWeight: 0);
        string? recordedJson = null;
        var view = View(table) with
        {
            RecordedManifestFor = (_, _) => recordedJson,
        };
        var request = Request(RealSpecies);

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var first).IsOk);
        recordedJson = "{\"replayed\":true}";
        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var second).IsOk);
        Assert.True(second!.Replayed);
        Assert.Equal(recordedJson, second.ReplayedResultJson);
    }
}
