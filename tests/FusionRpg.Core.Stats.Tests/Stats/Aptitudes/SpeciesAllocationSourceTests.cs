using FusionRpg.Core.Commanders;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

/// <summary>`species-build` T3.2 (module 6, `allocation-transport`) — `SpeciesAllocationSource`'s own
/// `ctx → allocation` resolution, fully provable with fake resolvers (mirrors
/// `SpecimenOwnershipOracle`'s established shape) — no `LawnElementIndex`, no running game.</summary>
public class SpeciesAllocationSourceTests
{
    static StatContext Ctx(StatSide side, int typeId, long? playerId = 1, string? entityKey = null) => new()
    {
        Side = side, TypeId = typeId, EntityKey = entityKey ?? $"{side}:{typeId}", PlayerId = playerId
    };

    [Fact]
    public void Commander_and_species_carry_into_one_allocation_object_each_scope_kept_apart()
    {
        // Rewritten for species-progression step 6.1 (spec-species-layer-delivery.md, "Rewrite,
        // never re-bless, the tests whose subject is the merge"): the ALLOCATION OBJECT this method
        // returns is still one merged AptitudeAllocation carrying both scopes' own points side by
        // side -- that part of R-S1 is unchanged, and this test still proves it. What changed is what
        // happens NEXT: since step 6.1, AptitudeResolver.Resolve no longer reads this object's merged
        // .Share() at all -- it resolves EACH scope alone via .ShareWithinScope(scope, aptitudeId), so
        // "merged into one object" no longer implies "shares combine at resolve time". That per-layer
        // contract (one scope's contribution is unchanged by adding points to another scope) is
        // proven directly against the resolver in AptitudeResolverTests.cs's own
        // AddingPointsToASecondScope_leavesTheFirstScopesOwnContributionUnchanged.
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => SpeciesLookupResult.Hit("fumeshroom"),
            resolveSpeciesAllocation: (_, id) => AptitudeAllocation.Single(AllocationScope.CreatureType, "Vigor", 40),
            resolveCommanderAllocation: pid => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 30),
            reportUnconfigured: _ => Assert.Fail("should not report when the index resolves a hit"));

        var result = source.Resolve(Ctx(StatSide.Plant, 7));

        // One AptitudeAllocation object, both scopes' own points kept distinctly addressable.
        Assert.Equal(30, result.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(40, result.PointsAt(AllocationScope.CreatureType, "Vigor"));
        Assert.Equal(70, result.Total("Might") + result.Total("Vigor")); // Total still sums across scopes
    }

    [Fact]
    public void PolevaulterZombie_and_WallNut_share_a_GameTypeId_but_resolve_differently()
    {
        // The named test the spec calls for: side stays part of the key, always.
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => side == StatSide.Zombie
                ? SpeciesLookupResult.Hit("polevaulterzombie")
                : SpeciesLookupResult.Hit("wallnut"),
            resolveSpeciesAllocation: (_, id) => id == "polevaulterzombie"
                ? AptitudeAllocation.Single(AllocationScope.CreatureType, "Agility", 50)
                : AptitudeAllocation.Single(AllocationScope.CreatureType, "Fortitude", 50),
            resolveCommanderAllocation: _ => AptitudeAllocation.Empty,
            reportUnconfigured: _ => Assert.Fail("index is always configured in this test"));

        var zombie = source.Resolve(Ctx(StatSide.Zombie, 3));
        var plant = source.Resolve(Ctx(StatSide.Plant, 3));

        Assert.Equal(50, zombie.PointsAt(AllocationScope.CreatureType, "Agility"));
        Assert.Equal(0, zombie.PointsAt(AllocationScope.CreatureType, "Fortitude"));
        Assert.Equal(50, plant.PointsAt(AllocationScope.CreatureType, "Fortitude"));
        Assert.Equal(0, plant.PointsAt(AllocationScope.CreatureType, "Agility"));
    }

    [Fact]
    public void The_species_term_is_asked_for_an_empire_and_the_two_sides_ask_for_different_ones()
    {
        // solid-remediation T4.1 (S1/S3). The key-set half of the defect: the commander term was the
        // visible symptom, but a lawn zombie ALSO read the player's species row, because the species
        // delegate was keyed by speciesId alone while the persisted scope key had gained an empire.
        // This asserts the seam carries the empire, so a cache behind it can tell the two apart.
        var asked = new List<(FusionRpg.Core.Commanders.EmpireId Empire, string SpeciesId)>();
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => SpeciesLookupResult.Hit(
                side == StatSide.Zombie ? "polevaulterzombie" : "wallnut"),
            resolveSpeciesAllocation: (empire, id) =>
            {
                asked.Add((empire, id));
                return AptitudeAllocation.Empty;
            },
            resolveCommanderAllocation: _ => AptitudeAllocation.Empty,
            reportUnconfigured: _ => Assert.Fail("index is always configured in this test"));

        source.Resolve(Ctx(StatSide.Plant, 3));
        source.Resolve(Ctx(StatSide.Zombie, 3));

        Assert.Equal(
            new[]
            {
                (FusionRpg.Core.Commanders.EmpireId.Dave, "wallnut"),
                (FusionRpg.Core.Commanders.EmpireId.Zomboss, "polevaulterzombie"),
            },
            asked);
    }

    [Fact]
    public void A_lawn_zombie_carries_its_owners_commander_pool_never_the_players()
    {
        // ai-empire-species EP4.18 (R23, R4 mirrored): the commander delegate is EMPIRE-keyed, so a
        // supplier answering different rows per empire is honoured on BOTH sides — a zombie general
        // carries Zomboss's pool, a plant general carries Dave's. What must never happen (the S1 defect)
        // is a zombie receiving the HUMAN's rows; the species half already refused that before R23 and is
        // unchanged here (Zomboss has no species rows in this fixture).
        var playersOwnRows = AptitudeAllocation.Single(AllocationScope.CreatureType, "Vigor", 500);
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => SpeciesLookupResult.Hit("shared-species-id"),
            resolveSpeciesAllocation: (empire, _) =>
                empire == FusionRpg.Core.Commanders.EmpireId.Dave ? playersOwnRows : AptitudeAllocation.Empty,
            resolveCommanderAllocation: empire => empire == FusionRpg.Core.Commanders.EmpireId.Zomboss
                ? AptitudeAllocation.Single(AllocationScope.Commander, "Might", 77)
                : AptitudeAllocation.Single(AllocationScope.Commander, "Might", 300),
            reportUnconfigured: _ => Assert.Fail("index is always configured in this test"));

        var plant = source.Resolve(Ctx(StatSide.Plant, 3));
        var zombie = source.Resolve(Ctx(StatSide.Zombie, 3));

        Assert.Equal(300, plant.PointsAt(AllocationScope.Commander, "Might"));    // Dave's own pool
        Assert.Equal(500, plant.PointsAt(AllocationScope.CreatureType, "Vigor"));
        Assert.Equal(77, zombie.PointsAt(AllocationScope.Commander, "Might"));    // ZOMBOSS's, never 300
        Assert.Equal(0, zombie.PointsAt(AllocationScope.CreatureType, "Vigor"));  // species half unchanged
    }

    [Fact]
    public void Unconfigured_index_reports_and_falls_back_to_commander_only_never_a_silent_zero()
    {
        var reports = new List<string>();
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => SpeciesLookupResult.NotConfigured,
            resolveSpeciesAllocation: (_, _) => throw new InvalidOperationException("must not be called when unconfigured"),
            resolveCommanderAllocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 15),
            reportUnconfigured: msg => reports.Add(msg));

        var result = source.Resolve(Ctx(StatSide.Plant, 7));

        Assert.Single(reports);
        Assert.Contains("not configured", reports[0], StringComparison.OrdinalIgnoreCase);
        // A real, if incomplete, answer (commander alone) -- not AptitudeAllocation.Empty and not a
        // fabricated species contribution.
        Assert.Equal(15, result.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(0, result.TotalForScope(AllocationScope.CreatureType));
    }

    [Fact]
    public void Configured_index_with_genuinely_no_species_is_commander_only_and_does_not_report()
    {
        var reported = false;
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => SpeciesLookupResult.NoSpecies,
            resolveSpeciesAllocation: (_, _) => throw new InvalidOperationException("must not be called when there's no species"),
            resolveCommanderAllocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 10),
            reportUnconfigured: _ => reported = true);

        var result = source.Resolve(Ctx(StatSide.Plant, 999));

        Assert.False(reported, "a genuinely-configured 'no species here' answer must not report as unconfigured");
        Assert.Equal(10, result.PointsAt(AllocationScope.Commander, "Might"));
    }

    [Fact]
    public void Constructor_rejects_null_collaborators()
    {
        AptitudeAllocation Commander(FusionRpg.Core.Commanders.EmpireId _) => AptitudeAllocation.Empty;
        AptitudeAllocation Species(FusionRpg.Core.Commanders.EmpireId _, string __) => AptitudeAllocation.Empty;
        SpeciesLookupResult Lookup(StatSide _, int __) => SpeciesLookupResult.NoSpecies;

        Assert.Throws<ArgumentNullException>(() => new SpeciesAllocationSource(null!, Species, Commander, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new SpeciesAllocationSource(Lookup, null!, Commander, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new SpeciesAllocationSource(Lookup, Species, null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new SpeciesAllocationSource(Lookup, Species, Commander, null!));
    }

    [Fact]
    public void SourceFile_performsNoIO_everyCollaboratorIsAnInjectedDelegate()
    {
        // "No I/O on the Hot path" (T3.2's own verify step) -- a text-scan guard, matching this
        // repo's established rigor for this class of assertion (DalGuardTests etc.): the type itself
        // must never reference File/Http/Sqlite/async I/O; every real read happens behind a delegate
        // the CALLER supplies, which is what makes this fully fake-able in a test with no game or DB.
        var path = FindSourceFile();
        var text = File.ReadAllText(path);
        foreach (var forbidden in new[] { "File.", "System.IO", "HttpClient", "Sqlite", "async ", "await " })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
    }

    static string FindSourceFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Core", "Stats", "Aptitudes", "SpeciesAllocationSource.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not locate SpeciesAllocationSource.cs above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Resolve_rejects_a_null_context()
    {
        AptitudeAllocation Commander(FusionRpg.Core.Commanders.EmpireId _) => AptitudeAllocation.Empty;
        AptitudeAllocation Species(FusionRpg.Core.Commanders.EmpireId _, string __) => AptitudeAllocation.Empty;
        SpeciesLookupResult Lookup(StatSide _, int __) => SpeciesLookupResult.NoSpecies;
        var source = new SpeciesAllocationSource(Lookup, Species, Commander, _ => { });

        Assert.Throws<ArgumentNullException>(() => source.Resolve(null!));
    }

    // ---- unique-lawn-wire (aptitude-sheet AS-1.1) ------------------------------------------

    [Fact]
    public void Bound_entity_resolves_commander_plus_unique_never_the_species_lookup()
    {
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveSpeciesAllocation: (_, _) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveCommanderAllocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 20),
            reportUnconfigured: _ => Assert.Fail("must not report for a Bound ctx"),
            resolveBoundInstanceId: entityKey => entityKey == "PTR1" ? "unique-1" : null,
            resolveUniqueAllocation: id => id == "unique-1"
                ? AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 45)
                : AptitudeAllocation.Empty);

        var result = source.Resolve(Ctx(StatSide.Plant, 7, entityKey: "PTR1"));

        Assert.Equal(20, result.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(45, result.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    [Fact]
    public void Bound_unique_sharing_a_species_id_with_a_general_never_inherits_empire_shares()
    {
        // G6 regression (spec-unique-lawn-wire.md): same (Side, TypeId) as a real general, but this
        // ctx's EntityKey IS Bound -- the species branch must never run, so it can never contribute.
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => SpeciesLookupResult.Hit("fumeshroom"),
            resolveSpeciesAllocation: (_, id) => AptitudeAllocation.Single(AllocationScope.CreatureType, "Vigor", 999),
            resolveCommanderAllocation: _ => AptitudeAllocation.Empty,
            reportUnconfigured: _ => Assert.Fail("must not report for a Bound ctx"),
            resolveBoundInstanceId: entityKey => entityKey == "PTR1" ? "unique-1" : null,
            resolveUniqueAllocation: _ => AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 10));

        var bound = source.Resolve(Ctx(StatSide.Plant, 7, entityKey: "PTR1"));
        var general = source.Resolve(Ctx(StatSide.Plant, 7, entityKey: "PTR2"));

        Assert.Equal(10, bound.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
        Assert.Equal(0, bound.PointsAt(AllocationScope.CreatureType, "Vigor"));
        Assert.Equal(999, general.PointsAt(AllocationScope.CreatureType, "Vigor"));
        Assert.Equal(0, general.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    [Fact]
    public void A_zombie_side_human_owned_unique_carries_the_human_commander_term_on_the_lawn()
    {
        // species-progression SP1.3 (layer-source-selector, rule 3 / C10) -- the one named behaviour
        // change. Before this fix, a Bound ctx's commander term was decided by ctx.Side (Zombie ->
        // Zomboss -> Empty) regardless of who actually owns the specimen; a human player's unique
        // fighting on the zombie side must carry the human commander term, as the sheet already does.
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveSpeciesAllocation: (_, _) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveCommanderAllocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 20),
            reportUnconfigured: _ => Assert.Fail("must not report for a Bound ctx"),
            resolveBoundInstanceId: entityKey => entityKey == "PTR-Z1" ? "unique-z1" : null,
            resolveUniqueAllocation: id => id == "unique-z1"
                ? AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 15)
                : AptitudeAllocation.Empty,
            resolveSpecimenOwnerEmpire: entityKey => entityKey == "PTR-Z1" ? EmpireId.Dave : null);

        var result = source.Resolve(Ctx(StatSide.Zombie, 7, entityKey: "PTR-Z1"));

        Assert.Equal(20, result.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(15, result.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    [Fact]
    public void A_zombie_owned_unique_carries_his_pool_regardless_of_which_side_it_occupies()
    {
        // ai-empire-species EP4.18 (R23/R4 mirrored), the ownership half: a Zomboss-owned specimen
        // fighting on the PLANT side (a hypno'd/converted unique) carries ZOMBOSS's commander pool —
        // ownership decides the pool's OWNER, never the side the specimen currently occupies, and never
        // the human's. Under the pre-R23 human-only gate this term resolved Empty; R23 gives his members
        // the term the player's members already had.
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveSpeciesAllocation: (_, _) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveCommanderAllocation: empire => empire == FusionRpg.Core.Commanders.EmpireId.Zomboss
                ? AptitudeAllocation.Single(AllocationScope.Commander, "Might", 999)
                : AptitudeAllocation.Single(AllocationScope.Commander, "Might", 1),
            reportUnconfigured: _ => Assert.Fail("must not report for a Bound ctx"),
            resolveBoundInstanceId: entityKey => entityKey == "PTR-P1" ? "unique-p1" : null,
            resolveUniqueAllocation: id => id == "unique-p1"
                ? AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 5)
                : AptitudeAllocation.Empty,
            resolveSpecimenOwnerEmpire: entityKey => entityKey == "PTR-P1" ? EmpireId.Zomboss : null);

        var result = source.Resolve(Ctx(StatSide.Plant, 7, entityKey: "PTR-P1"));

        Assert.Equal(999, result.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(5, result.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    [Fact]
    public void An_unregistered_bound_specimen_falls_back_to_the_human_empire()
    {
        // resolveSpecimenOwnerEmpire omitted entirely -- today's equivalent (pre-registration, or a
        // caller that never wires it), matching this class's own established "unregistered reads as
        // the human empire" fallback used throughout the rest of this file.
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveSpeciesAllocation: (_, _) => throw new InvalidOperationException("must not be called for a Bound ctx"),
            resolveCommanderAllocation: _ => AptitudeAllocation.Single(AllocationScope.Commander, "Might", 20),
            reportUnconfigured: _ => Assert.Fail("must not report for a Bound ctx"),
            resolveBoundInstanceId: entityKey => entityKey == "PTR1" ? "unique-1" : null,
            resolveUniqueAllocation: id => id == "unique-1"
                ? AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Vigor", 45)
                : AptitudeAllocation.Empty);

        var result = source.Resolve(Ctx(StatSide.Zombie, 7, entityKey: "PTR1"));

        Assert.Equal(20, result.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(45, result.PointsAt(AllocationScope.UniqueCreature, "Vigor"));
    }

    [Fact]
    public void Not_Bound_falls_through_to_the_species_path_even_when_the_hook_is_wired()
    {
        var source = new SpeciesAllocationSource(
            resolveSpeciesId: (side, typeId) => SpeciesLookupResult.Hit("wallnut"),
            resolveSpeciesAllocation: (_, _) => AptitudeAllocation.Single(AllocationScope.CreatureType, "Fortitude", 30),
            resolveCommanderAllocation: _ => AptitudeAllocation.Empty,
            reportUnconfigured: _ => Assert.Fail("index resolves a hit, should not report"),
            resolveBoundInstanceId: _ => null, // no Bound match for this entity
            resolveUniqueAllocation: _ => throw new InvalidOperationException("must not be called when not Bound"));

        var result = source.Resolve(Ctx(StatSide.Plant, 7));

        Assert.Equal(30, result.PointsAt(AllocationScope.CreatureType, "Fortitude"));
    }

    [Fact]
    public void Constructor_requires_resolveUniqueAllocation_when_resolveBoundInstanceId_is_supplied()
    {
        AptitudeAllocation Commander(FusionRpg.Core.Commanders.EmpireId _) => AptitudeAllocation.Empty;
        AptitudeAllocation Species(FusionRpg.Core.Commanders.EmpireId _, string __) => AptitudeAllocation.Empty;
        SpeciesLookupResult Lookup(StatSide _, int __) => SpeciesLookupResult.NoSpecies;

        Assert.Throws<ArgumentNullException>(() => new SpeciesAllocationSource(
            Lookup, Species, Commander, _ => { }, resolveBoundInstanceId: _ => null));
    }
}
