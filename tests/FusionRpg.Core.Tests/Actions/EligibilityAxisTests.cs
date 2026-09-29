using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// A-E1 (spec-eligibility-axis.md): the founding gap this program named — "nothing in the code can
/// express who may hold an action." Tests 1-8 mirror the spec's own §5 numbering; test 3 is written
/// first per the spec's own instruction ("the difference between a working eligibility system and one
/// that silently grants every species action to everybody").
/// </summary>
public class EligibilityAxisTests
{
    static ActionRow Row(string id, EligibilityScope scope, string? scopeKey) => new()
    {
        ActionId = id,
        Name = id,
        Kind = ActionKind.Skill,
        ContainerId = "",
        Scope = scope,
        ScopeKey = scopeKey,
    };

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FamilyOf =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["cherrybomb"] = new[] { "cherry" },
            ["cactus"] = new[] { "cactus" },
        };

    /// <summary>Test 3, written first per the spec's own instruction: a species-scoped row with a
    /// null <see cref="ActionRow.ScopeKey"/> must never appear for an actor whose own key is ALSO
    /// null/empty — the exact "two nulls compare equal" accident that would make a mis-authored row
    /// universal.</summary>
    [Fact]
    public void PlantedViolation_a_species_row_with_a_null_scopeKey_never_matches_an_actor_with_no_key()
    {
        var rows = new[] { Row("action.species.broken.001", EligibilityScope.Species, scopeKey: null) };

        var candidatesForNullActor = ActionEligibility.Candidates(rows, actorSpeciesKey: null, FamilyOf);
        var candidatesForEmptyActor = ActionEligibility.Candidates(rows, actorSpeciesKey: "", FamilyOf);
        var candidatesForRealActor = ActionEligibility.Candidates(rows, actorSpeciesKey: "cherrybomb", FamilyOf);

        Assert.Empty(candidatesForNullActor);
        Assert.Empty(candidatesForEmptyActor);
        Assert.Empty(candidatesForRealActor);
    }

    [Fact]
    public void Candidates_returns_general_plus_the_actors_family_and_species_rows_and_nothing_else()
    {
        var rows = new[]
        {
            Row("action.general.0001", EligibilityScope.General, null),
            Row("action.general.0002", EligibilityScope.General, null),
            Row("action.family.cherry.001", EligibilityScope.Family, "cherry"),
            Row("action.family.cactus.001", EligibilityScope.Family, "cactus"), // a DIFFERENT family
            Row("action.species.cherrybomb.001", EligibilityScope.Species, "cherrybomb"),
            Row("action.species.other.001", EligibilityScope.Species, "somethingelse"), // a DIFFERENT species
        };

        var result = ActionEligibility.Candidates(rows, actorSpeciesKey: "cherrybomb", FamilyOf);

        Assert.Equal(
            new[]
            {
                "action.family.cherry.001",
                "action.general.0001",
                "action.general.0002",
                "action.species.cherrybomb.001",
            },
            result.Select(r => r.ActionId));
    }

    [Fact]
    public void An_actor_with_an_unknown_family_gets_general_tier_rows_only()
    {
        var rows = new[]
        {
            Row("action.general.0001", EligibilityScope.General, null),
            Row("action.family.cherry.001", EligibilityScope.Family, "cherry"),
        };

        // "unassigned-species" is not a key in FamilyOf at all (§7: the family tier reaches 53/84),
        // so familyOf(actor) misses and only the general tier is reachable — the species tier is a
        // separate, direct match this actor also has none of.
        var result = ActionEligibility.Candidates(rows, actorSpeciesKey: "unassigned-species", FamilyOf);

        Assert.Equal(new[] { "action.general.0001" }, result.Select(r => r.ActionId));
    }

    [Fact]
    public void The_candidate_set_is_ordinally_sorted_and_stable_across_two_calls()
    {
        var rows = new[]
        {
            Row("action.general.0003", EligibilityScope.General, null),
            Row("action.general.0001", EligibilityScope.General, null),
            Row("action.general.0002", EligibilityScope.General, null),
        };

        var first = ActionEligibility.Candidates(rows, actorSpeciesKey: null, FamilyOf);
        var second = ActionEligibility.Candidates(rows, actorSpeciesKey: null, FamilyOf);

        var expected = new[] { "action.general.0001", "action.general.0002", "action.general.0003" };
        Assert.Equal(expected, first.Select(r => r.ActionId));
        Assert.Equal(expected, second.Select(r => r.ActionId));
    }

    /// <summary>Test 5: <see cref="UnlockState.TryAccept"/> driven from a REAL candidate set, not a
    /// hardcoded string literal — the ladder stops being reachable only from a test fixture.</summary>
    [Fact]
    public void UnlockState_TryAccept_is_driven_from_a_real_candidate_set()
    {
        var rows = new[]
        {
            Row("action.family.cherry.001", EligibilityScope.Family, "cherry"),
            Row("action.species.other.001", EligibilityScope.Species, "somethingelse"),
        };
        var candidates = ActionEligibility.Candidates(rows, actorSpeciesKey: "cherrybomb", FamilyOf);
        Assert.Single(candidates); // only the family row — proves this is really filtered, not a fixture

        var tuning = new UnlockTuning(P1Milli: 1000, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);
        var state = UnlockState.Empty();
        var outcome = state.TryAccept(candidates[0].ActionId, tuning, new AlwaysHitRng());

        Assert.True(outcome.Accepted);
        Assert.Equal("action.family.cherry.001", state.Held[0].UnlockId);
    }

    /// <summary>Test 6's mirror case (the load-time refusal itself is A-C1's, per §6): a
    /// family-scoped row naming a family with no assignment appears in NO candidate set — inert
    /// rather than wrong, with no schema coupling introduced to check it here.</summary>
    [Fact]
    public void A_family_scoped_row_naming_an_unassigned_family_joins_no_candidate_set()
    {
        var rows = new[] { Row("action.family.ghost.001", EligibilityScope.Family, "no-such-family") };

        var result = ActionEligibility.Candidates(rows, actorSpeciesKey: "cherrybomb", FamilyOf);

        Assert.Empty(result);
    }

    /// <summary>Test 7: A1's closure held mechanically — exactly three scope values exist, and every
    /// string outside them is refused by the parser rather than silently accepted.</summary>
    [Fact]
    public void PlantedViolation_a_fourth_scope_value_is_refused_by_the_parser()
    {
        var all = Enum.GetValues<EligibilityScope>();
        Assert.Equal(3, all.Length);

        Assert.False(EligibilityScopes.TryParse("guild", out _));
        Assert.False(EligibilityScopes.TryParse("global", out _));
        Assert.False(EligibilityScopes.TryParse(null, out _));
    }

    /// <summary>Test 8: <see cref="ActionEffectScope"/> — a different concept sharing a similar word
    /// (§4) — is untouched by this module.</summary>
    [Fact]
    public void ActionEffectScope_is_unchanged()
    {
        Assert.Equal(4, Enum.GetValues<ActionEffectScope>().Length);
        Assert.Equal("caster", ActionEffectScopes.Name(ActionEffectScope.Caster));
        Assert.Equal("primaryTarget", ActionEffectScopes.Name(ActionEffectScope.PrimaryTarget));
        Assert.Equal("eachTarget", ActionEffectScopes.Name(ActionEffectScope.EachTarget));
        Assert.Equal("casterAllies", ActionEffectScopes.Name(ActionEffectScope.CasterAllies));
    }

    [Fact]
    public void RungBand_collapses_to_its_ceiling_not_its_floor()
    {
        var band = new RungBand(Floor: 1, Ceiling: 10);
        Assert.Equal(10, band.Collapse());

        var signature = new RungBand(Floor: 1, Ceiling: 10); // the corrected [1,10] window, not [5,10]
        Assert.Equal(10, signature.Collapse());
    }

    // ---- FamilyMap -----------------------------------------------------------------------------

    [Fact]
    public void FamilyMap_parses_a_speciesKey_to_familyId_list_object()
    {
        var map = FamilyMap.Parse("""{"cherrybomb":["cherry"],"cactus":["cactus"]}""");
        Assert.Equal(2, map.Count);
        Assert.Equal(new[] { "cherry" }, map["cherrybomb"]);
    }

    /// <summary>⛔ CORRECTED 2026-09-11: the live projection is a RELATION, not a function — a
    /// multi-element list is legal and reaches every family it names, never just the first.</summary>
    [Fact]
    public void FamilyMap_keeps_every_family_in_a_multi_element_list()
    {
        var map = FamilyMap.Parse("""{"allpeater":["organism","flora"]}""");
        Assert.Equal(new[] { "organism", "flora" }, map["allpeater"]);
    }

    [Fact]
    public void FamilyMap_parse_of_empty_string_yields_an_empty_map()
    {
        Assert.Empty(FamilyMap.Parse(""));
    }

    [Fact]
    public void FamilyMap_refuses_a_scalar_value_not_a_list()
    {
        Assert.Throws<InvalidOperationException>(() => FamilyMap.Parse("""{"cherrybomb":"cherry"}"""));
    }

    /// <summary>The real committed projection (§3.2, decided 2026-09-03; ⛔ re-measured 2026-09-11):
    /// the file is A-S0's live projection of the creature species corpus (`derive_live_family_assignments`
    /// over `gk-data/packs/fusion/data/seed/creatures/species/`), NOT the legacy 53-species `_generated/family-assignments.json`.
    /// Live shape: 904 species over 227 consolidated families, with multi-family membership (a species
    /// reaching more than one family is the reason the projection is a relation). Every key must be a
    /// real shipped species — read from disk, not asserted from memory.</summary>
    [Fact]
    public void The_real_family_map_json_is_the_live_species_relation()
    {
        var repoRoot = FindRepoRoot();
        var mapPath = Path.Combine(repoRoot, "data", "seed", "actions", "_generated", "family-map.json");
        var map = FamilyMap.Parse(File.ReadAllText(mapPath));

        // Liveness + the relation's defining property.
        Assert.True(map.Count > 53, $"expected the live roster past the legacy 53 keys; got {map.Count}");
        Assert.Contains(map.Values, v => v.Count > 1);
        Assert.All(map.Values, v => Assert.NotEmpty(v));

        // Every key is a shipped species id (the projection cannot invent one).
        var indexPath = Path.Combine(repoRoot, "data", "seed", "creatures", "species", "_index.json");
        var species = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(indexPath))!
            .Keys.Select(k => k.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var unknown = map.Keys.Where(k => !species.Contains(k)).ToList();
        Assert.Empty(unknown);
    }

    sealed class AlwaysHitRng : IAtomRandom
    {
        public int NextInclusive(int min, int max) => min;
        public int NextPerMille() => 0;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "actions"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
