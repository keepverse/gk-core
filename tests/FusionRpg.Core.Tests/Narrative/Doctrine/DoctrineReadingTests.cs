using System.Reflection;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Narrative.Doctrine;
using FusionRpg.Core.Narrative.Vocabulary;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.Narrative.Doctrine;

/// <summary>
/// npc-story-events NR6.2 (spec-counter-doctrine.md §1, Owner ruling R18): the Rotwright reads only what his
/// faction OBSERVED — his latest observation record and this turn's battles he took part in or saw — and
/// never a battle outcome, an enemy identity or a character. Keeping a lean out of his sight is counter-play,
/// so these cases are the mechanic, not decoration.
///
/// <para>The lean threshold is the shipped tuning's (400‰), so 60% is a lean and 30%-against-30% is a tie and
/// therefore no lean — the spec's own numbers, read from the tuning rather than pinned here.</para>
/// </summary>
[Trait("VerificationId", "core.narrative")]
[Trait("Guard", "narrative")]
public sealed class DoctrineReadingTests
{
    const string Rotwright = "rotwright";
    const string Dave = "dave";
    const int SeenTurn = 4;

    /// <summary>
    /// The reading's threshold is the shipped tuning's, so this class configures the hub from the COMMITTED
    /// file rather than a literal — the Core test bootstrap predates the narrative module and configures no
    /// narrative hub, and a literal here would let the reading's own numbers drift from the balance surface.
    /// </summary>
    static DoctrineReadingTests()
    {
        // The tuning parser JOINS the registries (it copies nothing), so both must be configured first — the
        // fixture registries, since the committed corpus is the shape/semantic mismatch NR1.5's read filed —
        // and the threshold itself is read from the committed tuning file, never a literal here.
        NarrativeRegistryHub.Configure(Path.Combine(RepoRoot(), "tests", "fixtures", "narrative", "_registry"));
        DispositionCatalog.Configure(DispositionCatalog.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "seed", "dungeon", "_registry", "disposition.v1.json"))));
        NarrativeTuningHub.Configure(NarrativeTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "narrative.v2.json"))));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    static string SpeciesOf(ElementTypeId element) =>
        CreatureSpeciesCatalog.All.First(s => s.ElementPrimary == element).SpeciesId;

    static WorldEntityMember Member(ElementTypeId element) =>
        new() { SpeciesId = SpeciesOf(element), Level = 1, Hp = 100 };

    static WorldEntity Legion(string id, string owner, string sector, ElementTypeId element, int members, string stance = "march") =>
        new()
        {
            EntityId = id, OwnerFactionId = owner, Kind = WorldEntityKind.Legion, AtSectorId = sector,
            Stance = stance, Members = Enumerable.Range(0, members).Select(_ => Member(element)).ToList()
        };

    static IntelSnapshot Snapshot(string sector, bool exact, bool owned, (string EntityId, int Members, ElementTypeId Element)[] forces)
    {
        return new()
        {
            SectorId = sector, LastSeenTurn = SeenTurn, Detail = SectorSight.Glimpse,
            // Only a sector whose snapshot names the player as owner contributes a ground share; a
            // non-owned sector carries no remembered slots either, which is what keeps an element-lean case
            // from being outvoted by ground it merely sits next to.
            OwnerFactionId = owned ? Dave : null,
            Slots = owned ? new[] { new RememberedSlot { SlotIndex = 0, SlotTypeId = "material-seam" } } : Array.Empty<RememberedSlot>(),
            Forces = forces.Select(f => new RememberedForce
            {
                EntityId = f.EntityId, OwnerFactionId = Dave, Kind = WorldEntityKind.Legion, Exact = exact
            }).ToList()
        };
    }

    /// <summary>A sector he observed but does not hold: forces count, ground does not.</summary>
    static IntelSnapshot Snapshot(string sector, bool exact, params (string EntityId, int Members, ElementTypeId Element)[] forces) =>
        Snapshot(sector, exact, owned: false, forces);

    /// <summary>A sector the snapshot names the player as owning — its remembered slots are the ground share.</summary>
    static IntelSnapshot OwnedSnapshot(string sector, bool exact, params (string EntityId, int Members, ElementTypeId Element)[] forces) =>
        Snapshot(sector, exact, owned: true, forces);

    static WorldState World(int turn, IEnumerable<WorldEntity> entities, params IntelSnapshot[] snapshots) => new()
    {
        WorldId = "w-reading", TemplateId = "layout.short-narrow-linear-001", Seed = 7UL, CurrentTurn = turn,
        Sectors = new[]
        {
            new WorldSector { SectorId = "s1", TypeId = "stable" },
            new WorldSector { SectorId = "s2", TypeId = "stable" }
        },
        Factions = new[]
        {
            new WorldFaction { FactionId = Dave, Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = Rotwright, Kind = WorldFactionKind.Wild, Name = "Rotwright", PolicyId = null }
        },
        Entities = entities.ToList(),
        Intel = new[] { new FactionIntel { FactionId = Rotwright, Sectors = snapshots } }
    };

    static DoctrineReadingResult Read(WorldState world, params TurnReportEntry[] battles) =>
        DoctrineReading.Of(world, battles, Rotwright, Dave);

    static string BattleId(int turn, string location, string attacker, string? defender) =>
        BattleKinds.IdFor(turn, BattleKinds.Sector, location, attacker, defender);

    static TurnReportEntry BattleEntry(int turn, string location, string attacker, string? defender, string? sector) =>
        new("events", TurnReportKinds.Battle, BattleId(turn, location, attacker, defender), "battle", sector);

    // ---- the shares --------------------------------------------------------------------------------

    [Fact]
    public void Sixty_percent_observed_fire_members_read_a_fire_lean()
    {
        var fire = Legion("dave-fire", Dave, "s1", ElementTypeId.Fire, 6);
        var ice = Legion("dave-ice", Dave, "s1", ElementTypeId.Ice, 4);
        var world = World(SeenTurn + 1, new[] { fire, ice },
            Snapshot("s1", exact: true, ("dave-fire", 6, ElementTypeId.Fire), ("dave-ice", 4, ElementTypeId.Ice)));

        var reading = Read(world);

        var share = reading.Shares.Single(s => s.Key == "element:fire");
        Assert.Equal(6, share.Count);
        Assert.Equal(10, share.Total);
        Assert.Equal(600, share.ShareMilli);
        Assert.Equal("element:fire", reading.LeanKey);
    }

    [Fact]
    public void Thirty_percent_fire_against_thirty_percent_ice_is_a_tie_and_reads_no_lean()
    {
        var world = World(SeenTurn + 1,
            new[]
            {
                Legion("dave-fire", Dave, "s1", ElementTypeId.Fire, 3),
                Legion("dave-ice", Dave, "s1", ElementTypeId.Ice, 3),
                Legion("dave-earth", Dave, "s1", ElementTypeId.Earth, 2),
                Legion("dave-air", Dave, "s1", ElementTypeId.Air, 2)
            },
            Snapshot("s1", exact: true, ("dave-fire", 3, ElementTypeId.Fire), ("dave-ice", 3, ElementTypeId.Ice),
                ("dave-earth", 2, ElementTypeId.Earth), ("dave-air", 2, ElementTypeId.Air)));

        var reading = Read(world);

        Assert.Equal(300, reading.Shares.Single(s => s.Key == "element:fire").ShareMilli);
        Assert.Equal(300, reading.Shares.Single(s => s.Key == "element:ice").ShareMilli);
        // Two keys tied at the top is no lean: the winner must be STRICTLY above every other share.
        Assert.Null(reading.LeanKey);
    }

    [Fact]
    public void A_fire_lean_he_never_saw_reads_no_lean_the_same_lean_on_observed_ground_does()
    {
        var hidden = World(SeenTurn + 1, new[] { Legion("dave-fire", Dave, "s2", ElementTypeId.Fire, 8) },
            Snapshot("s1", exact: true, ("some-other", 1, ElementTypeId.Ice)));

        Assert.Null(Read(hidden).LeanKey);

        var seen = World(SeenTurn + 1, new[] { Legion("dave-fire", Dave, "s1", ElementTypeId.Fire, 8) },
            Snapshot("s1", exact: true, ("dave-fire", 8, ElementTypeId.Fire)));

        Assert.Equal("element:fire", Read(seen).LeanKey);
    }

    [Fact]
    public void A_glimpsed_force_adds_nothing_to_a_share()
    {
        var world = World(SeenTurn + 1, new[] { Legion("dave-fire", Dave, "s1", ElementTypeId.Fire, 8) },
            Snapshot("s1", exact: false, ("dave-fire", 8, ElementTypeId.Fire)));

        var reading = Read(world);

        Assert.Empty(reading.Shares);
        Assert.Null(reading.LeanKey);
    }

    [Fact]
    public void A_battle_he_fought_out_of_sight_counts()
    {
        // The fire force was never in his observation, but his own warband fought it this turn — a battle is
        // a sighting, so it joins the observed set.
        var world = World(6,
            new[]
            {
                Legion("dave-fire", Dave, "s2", ElementTypeId.Fire, 8),
                Legion("rot-1", Rotwright, "s2", ElementTypeId.Dark, 4)
            },
            Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice)));

        var reading = Read(world, BattleEntry(6, "s2", "rot-1", "dave-fire", "s2"));

        Assert.Equal("element:fire", reading.LeanKey);
    }

    [Fact]
    public void A_battle_he_neither_fought_nor_saw_counts_for_nothing()
    {
        var world = World(6,
            new[]
            {
                Legion("dave-fire", Dave, "s2", ElementTypeId.Fire, 8),
                Legion("wild-1", "wild", "s2", ElementTypeId.Dark, 4)
            },
            Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice)));

        var reading = Read(world, BattleEntry(6, "s2", "wild-1", "dave-fire", "s2"));

        Assert.DoesNotContain(reading.Shares, s => s.Key == "element:fire");
    }

    [Fact]
    public void Nothing_observed_is_no_lean()
    {
        var world = World(6, new[] { Legion("dave-fire", Dave, "s2", ElementTypeId.Fire, 8) });

        var reading = Read(world);

        Assert.Same(DoctrineReadingResult.NothingSeen.Shares, reading.Shares);
        Assert.Null(reading.LeanKey);
        Assert.Equal(0, reading.LeanShareMilli);
    }

    [Fact]
    public void Ground_and_posture_are_read_from_the_same_observation()
    {
        var holding = Legion("dave-hold", Dave, "s1", ElementTypeId.Fire, 4, stance: "hold");
        var marching = Legion("dave-march", Dave, "s1", ElementTypeId.Fire, 4);
        var world = World(SeenTurn + 1, new[] { holding, marching },
            OwnedSnapshot("s1", exact: true,
                ("dave-hold", 4, ElementTypeId.Fire), ("dave-march", 4, ElementTypeId.Fire)));

        var reading = Read(world);

        Assert.Equal(500, reading.Shares.Single(s => s.Key == DoctrineReading.PostureHold).ShareMilli);
        Assert.Equal(1000, reading.Shares.Single(s => s.Key == "ground:material-seam").ShareMilli);
    }

    // ---- never how strong, never an outcome, never an identity --------------------------------------

    [Fact]
    public void Two_worlds_differing_only_in_battle_winners_read_identically()
    {
        var entities = new[]
        {
            Legion("dave-fire", Dave, "s2", ElementTypeId.Fire, 8),
            Legion("rot-1", Rotwright, "s2", ElementTypeId.Dark, 4)
        };
        var won = World(6, entities, Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice)));
        var lost = World(6, entities, Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice)));

        // The winner is carried in the entry's Detail, which the reading never opens.
        var wonEntry = BattleEntry(6, "s2", "rot-1", "dave-fire", "s2") with { Detail = "sector:s2:rot-1" };
        var lostEntry = BattleEntry(6, "s2", "rot-1", "dave-fire", "s2") with { Detail = "sector:s2:dave-fire" };

        Assert.Equal(Read(won, wonEntry).Shares, Read(lost, lostEntry).Shares);
        Assert.Equal(Read(won, wonEntry).LeanKey, Read(lost, lostEntry).LeanKey);
    }

    [Fact]
    public void Which_antagonist_entity_fought_does_not_change_the_reading()
    {
        var entities = new[]
        {
            Legion("dave-fire", Dave, "s2", ElementTypeId.Fire, 8),
            Legion("rot-1", Rotwright, "s2", ElementTypeId.Dark, 4),
            Legion("rot-2", Rotwright, "s1", ElementTypeId.Dark, 4)
        };
        var worldA = World(6, entities, Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice)));
        var worldB = World(6, entities, Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice)));

        var readingA = Read(worldA, BattleEntry(6, "s2", "rot-1", "dave-fire", "s2"));
        var readingB = Read(worldB, BattleEntry(6, "s1", "rot-2", "dave-fire", "s1"));

        Assert.Equal(readingA.Shares, readingB.Shares);
    }

    [Fact]
    public void A_counting_battle_reads_the_same_as_seeing_the_same_forces_on_the_ground()
    {
        var entities = new[] { Legion("dave-fire", Dave, "s2", ElementTypeId.Fire, 8), Legion("rot-1", Rotwright, "s2", ElementTypeId.Dark, 4) };

        var byBattle = Read(
            World(6, entities, Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice))),
            BattleEntry(6, "s2", "rot-1", "dave-fire", "s2"));

        var bySight = Read(
            World(SeenTurn + 1, entities,
                Snapshot("s1", exact: true, ("unrelated", 1, ElementTypeId.Ice)),
                Snapshot("s2", exact: true, ("dave-fire", 8, ElementTypeId.Fire))));

        Assert.Equal("element:fire", byBattle.LeanKey);
        Assert.Equal("element:fire", bySight.LeanKey);
        Assert.Equal(
            byBattle.Shares.Single(s => s.Key == "element:fire").ShareMilli,
            bySight.Shares.Single(s => s.Key == "element:fire").ShareMilli);
    }

    // ---- the input contract ------------------------------------------------------------------------

    [Fact]
    public void The_read_takes_world_state_this_turns_battles_and_two_faction_ids()
    {
        var parameters = typeof(DoctrineReading).GetMethod(nameof(DoctrineReading.Of))!
            .GetParameters()
            .Select(p => (p.Name, Type: p.ParameterType))
            .ToArray();

        Assert.Equal(
            new[]
            {
                ("world", typeof(WorldState)),
                ("turnBattles", typeof(IReadOnlyList<TurnReportEntry>)),
                ("antagonistFactionId", typeof(string)),
                ("playerFactionId", typeof(string))
            },
            parameters);
    }
}
