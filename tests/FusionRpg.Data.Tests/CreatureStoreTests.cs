using FusionRpg.Contracts;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

public class CreatureStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CreatureStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // Mint enforces catalog discipline — tests must use a real generated species.
    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s => s.Side == "zombie");

    static CreatureMintSpec Spec(string? species = null) => new()
    {
        SpeciesId = species ?? CatalogSpecies.SpeciesId,
        Side = "zombie",
        GameTypeId = CatalogSpecies.GameTypeId,
        Rarity = "chaff",
        Variant = "normal",
        ElementPrimary = "fire",
        TraitIds = new List<string> { "swift" },
        Origin = "summon"
    };

    [Fact]
    public void Mint_creates_actor_profile_and_codex_atomically()
    {
        var (specimen, newlyDiscovered) = _store.MintCreature(1, Spec());
        Assert.True(newlyDiscovered);
        Assert.Equal("Roster", specimen.Actor.Phase);
        Assert.Equal(CatalogSpecies.SpeciesId, specimen.Profile.SpeciesId);
        Assert.Equal(specimen.Actor.InstanceId, specimen.Profile.InstanceId);

        var roster = _store.ListCreatureRoster(1);
        Assert.Single(roster.Items);
        var codex = _store.ListCreatureCodex(1);
        Assert.Single(codex.Entries);
        Assert.Equal(CreatureCodexStates.Discovered, codex.Entries[0].State);
    }

    [Fact]
    public void Mint_for_unknown_player_leaves_nothing()
    {
        Assert.Throws<InvalidOperationException>(() => _store.MintCreature(999, Spec()));
        Assert.Empty(_store.ListCreatureRoster(999).Items);
        Assert.Empty(_store.ListCreatureCodex(999).Entries);
    }

    [Fact]
    public void Second_mint_of_same_species_is_not_newly_discovered()
    {
        var (_, first) = _store.MintCreature(1, Spec());
        var (_, second) = _store.MintCreature(1, Spec());
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(2, _store.ListCreatureRoster(1).Items.Count);
        Assert.Single(_store.ListCreatureCodex(1).Entries);
    }

    [Fact]
    public void Codex_lattice_never_downgrades()
    {
        _store.UpsertCreatureCodex(1, "hell-hound", CreatureCodexStates.Discovered);
        _store.UpsertCreatureCodex(1, "hell-hound", CreatureCodexStates.Seen); // must be a no-op downgrade
        var codex = _store.ListCreatureCodex(1);
        Assert.Equal(CreatureCodexStates.Discovered, Assert.Single(codex.Entries).State);

        _store.UpsertCreatureCodex(1, "wisp", CreatureCodexStates.Seen);
        Assert.Equal(CreatureCodexStates.Seen, _store.ListCreatureCodex(1).Entries.Single(e => e.SpeciesId == "wisp").State);
        var newly = _store.UpsertCreatureCodex(1, "wisp", CreatureCodexStates.Discovered);
        Assert.True(newly);
        Assert.Equal(CreatureCodexStates.Discovered, _store.ListCreatureCodex(1).Entries.Single(e => e.SpeciesId == "wisp").State);
    }

    [Fact]
    public void Nickname_and_lock_bump_revision()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        var id = specimen.Profile.InstanceId;
        var (ok1, p1) = _store.SetCreatureNickname(id, "Ragnar");
        Assert.True(ok1);
        Assert.Equal("Ragnar", p1!.Nickname);
        Assert.Equal(1, p1.Revision);
        var (ok2, p2) = _store.SetCreatureLocked(id, true);
        Assert.True(ok2);
        Assert.True(p2!.Locked);
        Assert.Equal(2, p2.Revision);
    }

    [Fact]
    public void Mint_rejects_bad_args()
    {
        Assert.Throws<ArgumentException>(() =>
            _store.MintCreature(1, new CreatureMintSpec { SpeciesId = "", Side = "zombie" }));
        Assert.Throws<ArgumentException>(() =>
            _store.MintCreature(1, new CreatureMintSpec { SpeciesId = "x", Side = "mower" }));
    }

    [Fact]
    public void Mint_enforces_catalog_discipline()
    {
        // Review I3: unknown ids reject at the write gate; whitespace species trims to ONE codex key.
        Assert.Throws<ArgumentException>(() => _store.MintCreature(1, Spec("no-such-species")));
        var badTrait = Spec();
        badTrait.TraitIds = new List<string> { "nonsense-trait" };
        Assert.Throws<ArgumentException>(() => _store.MintCreature(1, badTrait));
        var badVariant = Spec();
        badVariant.Variant = "holographic";
        Assert.Throws<ArgumentException>(() => _store.MintCreature(1, badVariant));
        var badRarity = Spec();
        badRarity.Rarity = "mythic";
        Assert.Throws<ArgumentException>(() => _store.MintCreature(1, badRarity));
        Assert.Empty(_store.ListCreatureRoster(1).Items); // nothing leaked through

        _store.MintCreature(1, Spec("  " + CatalogSpecies.SpeciesId + "  "));
        _store.MintCreature(1, Spec());
        Assert.Single(_store.ListCreatureCodex(1).Entries); // trimmed — no split codex keys
    }

    [Fact]
    public void Roster_and_codex_carry_the_catalog_rank_id_and_display_name()
    {
        // spec-species-rank.md §5 (Task 9 payloads): roster/codex payloads copy the rank off the
        // runtime catalog — never re-derived, never defaulted. The compiled-default roster this
        // project boots from carries no ranks, so the ranked species is scoped in (UseScoped), the
        // same seam every catalog-swap test in this repo already uses. Heirloom (not bottom)
        // proves the copy is the catalog's own value rather than a fabricated floor.
        var species = new FusionRpg.Core.Creatures.CreatureSpeciesDef
        {
            SpeciesId = "test-ranked-weed", Name = "Test Ranked Weed", Side = "zombie", GameTypeId = 42,
            CreatureTypeId = FusionRpg.Core.Creatures.CreatureSpeciesCatalog.CreatureTypeIdFor("zombie", 42),
            ElementPrimary = FusionRpg.Core.Stats.Derived.ElementTypeId.Fire,
            BaseRarity = FusionRpg.Core.Creatures.CreatureRarity.Chaff,
            DeployMode = FusionRpg.Core.Creatures.CreatureDeployMode.PlantAvatar,
            Acquisition = FusionRpg.Core.Creatures.CreatureAcquisition.Summonable,
            Rank = FusionRpg.Core.Creatures.CreatureRank.Heirloom,
            Variants = new[] { "normal" }, TraitPool = new[] { "swift" },
        };
        using (FusionRpg.Core.Creatures.CreatureSpeciesCatalog.UseScoped(new[] { species }))
        {
            var (specimen, _) = _store.MintCreature(1, Spec(species.SpeciesId));
            Assert.Equal("heirloom", specimen.Profile.Rank);
            Assert.Equal("Heirloom", specimen.Profile.RankDisplayName);

            var roster = _store.ListCreatureRoster(1);
            Assert.Equal("heirloom", roster.Items.Single().Profile.Rank);
            Assert.Equal("Heirloom", roster.Items.Single().Profile.RankDisplayName);

            var codex = _store.ListCreatureCodex(1);
            Assert.Equal("heirloom", codex.Entries.Single().Rank);
            Assert.Equal("Heirloom", codex.Entries.Single().RankDisplayName);
        }
    }
}
