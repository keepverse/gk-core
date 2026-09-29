using System.Reflection;
using FusionRpg.Core.Progression;
using Xunit;

namespace FusionRpg.Core.Tests.Progression;

/// <summary>
/// `empire-level` EP4.1 — the Core half: the `empire` progression kind, the `empire_species_level_up`
/// reason, the curve/award parse, and the pure grant rule. The store-side credit, the ledger and the
/// REST read are later rows of the same spec.
/// </summary>
public sealed class EmpireLevelGrantsTests
{
    // A document from BEFORE the empire pair existed — v1's and v2's shape. EP4.2 made both keys
    // required, so this document is no longer loadable at all; the two tests below assert what the
    // refusal names.
    const string OlderDocument = """
        { "schemaVersion": 1, "version": 2,
          "xpCurve": {
            "plant": { "first": 80, "step": 32 },
            "zombie": { "first": 70, "step": 28 },
            "player": { "first": 100, "step": 45 },
            "specimen": { "first": 100, "step": 45 } },
          "awards": { "kill": 12, "defeat": -100, "mower": -30,
            "plantPlace": 8, "zombieSpawn": 9 } }
        """;

    /// <summary>The empire curve present, the award absent — so the refusal it produces is about the
    /// AWARD and not about the curve, which is what makes the second refusal test specific.</summary>
    const string DocumentWithoutSpeciesLevelUp = """
        { "schemaVersion": 1, "version": 3,
          "xpCurve": {
            "plant": { "first": 80, "step": 32 },
            "zombie": { "first": 70, "step": 28 },
            "player": { "first": 100, "step": 45 },
            "specimen": { "first": 100, "step": 45 },
            "empire": { "first": 10, "step": 5 } },
          "awards": { "kill": 12, "defeat": -100, "mower": -30,
            "plantPlace": 8, "zombieSpawn": 9 } }
        """;

    // The same document plus the two keys this row adds, at the spec's working values
    // (`spec-empire-level.md` §Tunables: level 2 at 10 species level-ups, then 15, 20 ...).
    const string NewDocument = """
        { "schemaVersion": 1, "version": 3,
          "xpCurve": {
            "plant": { "first": 80, "step": 32 },
            "zombie": { "first": 70, "step": 28 },
            "player": { "first": 100, "step": 45 },
            "specimen": { "first": 100, "step": 45 },
            "empire": { "first": 10, "step": 5 } },
          "awards": { "kill": 12, "defeat": -100, "mower": -30,
            "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1 } }
        """;

    [Fact]
    public void ActorKind_and_grant_kind_vocabularies_are_closed_and_pinned()
    {
        // A PINNED LITERAL, which AGENTS.md allows for exactly one thing: a closed vocabulary the code
        // owns and a human edits. `RpgActorKinds` is that (its own doc comment says so), and
        // `EmpireLevelGrantKind` is this row's new one -- growth is a reviewed change that moves this
        // number in the same commit. Nothing here counts a content population.
        var kinds = typeof(RpgActorKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(6, kinds.Length);
        Assert.Equal(new[] { "empire", "plant", "player", "species", "specimen", "zombie" }, kinds);
        Assert.True(RpgActorKinds.IsKnown(RpgActorKinds.Empire));

        var grants = Enum.GetValues<EmpireLevelGrantKind>();
        Assert.Single(grants);
        Assert.Equal(EmpireLevelGrantKind.FreeEmpireRespec, grants[0]);
    }

    [Fact]
    public void For_grants_nothing_when_the_published_stock_is_zero()
    {
        Assert.Empty(EmpireLevelGrants.For(2, new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 0)));
    }

    [Fact]
    public void For_grants_the_published_free_respec_stock()
    {
        foreach (var level in new long[] { 2, 7, 41 })
        {
            var grant = Assert.Single(EmpireLevelGrants.For(level, new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 1)));
            Assert.Equal(EmpireLevelGrantKind.FreeEmpireRespec, grant.Kind);
            Assert.Equal(1, grant.Amount);
        }

        var bigger = Assert.Single(EmpireLevelGrants.For(9, new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 3)));
        Assert.Equal(3, bigger.Amount);
    }

    [Fact]
    public void The_grant_rule_declares_no_level_shaped_curve()
    {
        // The reflection half of "one power ladder": this class may READ a level, never DERIVE one.
        // Mirrors guard-power.py's G2 heuristic (a numeric-returning method with a level-named
        // parameter) instead of trusting a comment -- and it is deliberately STRICTER than G2, which
        // also requires arithmetic on the parameter in the body.
        var numeric = new[] { typeof(int), typeof(long), typeof(double), typeof(float) };
        var offenders = typeof(EmpireLevelGrants)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => numeric.Contains(m.ReturnType))
            .Where(m => m.GetParameters().Any(p => p.Name is "level" or "lvl" or "index"))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Loader_reads_the_empire_curve_and_the_species_level_up_award()
    {
        var tuning = ProgressionTuningLoader.Parse(NewDocument);

        Assert.NotNull(tuning.EmpireCurve);
        Assert.Equal(10, tuning.EmpireCurve!.First);
        Assert.Equal(5, tuning.EmpireCurve.Step);
        Assert.Equal(1, tuning.Awards.SpeciesLevelUp);
    }

    /// <summary>The award present, the CURVE absent — so the refusal it produces is about the curve and
    /// not about the award, which is what makes the first refusal test specific.</summary>
    const string DocumentWithoutEmpireCurve = """
        { "schemaVersion": 1, "version": 3,
          "xpCurve": {
            "plant": { "first": 80, "step": 32 },
            "zombie": { "first": 70, "step": 28 },
            "player": { "first": 100, "step": 45 },
            "specimen": { "first": 100, "step": 45 } },
          "awards": { "kill": 12, "defeat": -100, "mower": -30,
            "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1 } }
        """;

    [Fact]
    public void A_document_without_the_empire_curve_is_refused_by_name()
    {
        // EP4.2 made both keys REQUIRED at parse (tunables-ssot.md T5): the publish that first carried
        // them moved every reader of the previous version in the same commit (H7), so the tolerant
        // window EP4.1 opened is closed and a v1/v2-shaped document no longer loads. EP4.1 deferred the
        // requirement precisely because a parse rejection BEFORE that publish would have made the live
        // server and ~30 fixtures unloadable;
        // `An_in_code_tuning_without_the_empire_pair_refuses_at_use_by_name` below pins the one case
        // that still reaches the deferred refusal.
        var rejection = Assert.Throws<ProgressionTuningRejection>(
            () => ProgressionTuningLoader.Parse(DocumentWithoutEmpireCurve));
        Assert.Contains("empire", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_v1_shaped_document_no_longer_loads_at_all()
    {
        // `progression.v1.json`'s own shape: both keys absent. The first required one evaluated is the
        // AWARD (a constructor argument, so it runs before the curve initializer), so that is the key
        // this document's refusal names -- the point is that it is refused, not which of the two missing
        // keys gets named first.
        var rejection = Assert.Throws<ProgressionTuningRejection>(
            () => ProgressionTuningLoader.Parse(OlderDocument));
        Assert.Contains("speciesLevelUp", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_without_the_species_level_up_award_is_refused_by_name()
    {
        // The award is the empire level's ONLY faucet, so absence is a misconfiguration rather than a
        // small value -- refused by name, never read as 0, which would freeze every empire level.
        var rejection = Assert.Throws<ProgressionTuningRejection>(
            () => ProgressionTuningLoader.Parse(DocumentWithoutSpeciesLevelUp));
        Assert.Contains("speciesLevelUp", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_in_code_tuning_without_the_empire_pair_refuses_at_use_by_name()
    {
        // The published-document path can no longer produce a tuning without these keys, but an in-code
        // `ProgressionTuning` built by a test bootstrap that predates them can -- and that case must
        // refuse by name too rather than read a zero or an absent curve. This is the deferral EP4.1
        // shipped, kept as the defensive case now that the loader requires the keys.
        var withoutEmpire = new ProgressionTuning(
            SchemaVersion: 1, Version: 3,
            PlantCurve: new XpCurveParams(80, 32), ZombieCurve: new XpCurveParams(70, 28),
            PlayerCurve: new XpCurveParams(100, 45), SpecimenCurve: new XpCurveParams(100, 45),
            Awards: new XpAwardsTuning(Kill: 12, Defeat: -100, Mower: -30, PlantPlace: 8, ZombieSpawn: 9));

        try
        {
            ProgressionTuningHub.Configure(withoutEmpire);

            var curveRejection = Assert.Throws<ProgressionTuningRejection>(
                () => RpgXpCurve.ParamsFor(RpgActorKinds.Empire));
            Assert.Contains("xpCurve.empire", curveRejection.Message, StringComparison.Ordinal);

            var awardRejection = Assert.Throws<ProgressionTuningRejection>(
                () => RpgXpAwards.SpeciesLevelUp);
            Assert.Contains("awards.speciesLevelUp", awardRejection.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Never leave the assembly's ambient tuning on a document this test chose: every other
            // class here reads the same process-wide hub.
            ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression);
        }
    }

    [Fact]
    public void The_empire_level_reads_the_shared_arithmetic_ladder()
    {
        try
        {
            ProgressionTuningHub.Configure(ProgressionTuningLoader.Parse(NewDocument));

            // `first + (L-1)*step`, row 6's shape with the empire's own pair -- asserted through the
            // shared curve function, so a private f(level) appearing later would fail here.
            Assert.Equal(10, RpgXpCurve.XpToNext(RpgActorKinds.Empire, 1));
            Assert.Equal(15, RpgXpCurve.XpToNext(RpgActorKinds.Empire, 2));
            Assert.Equal(10 * 3 + 5 * 3, RpgXpCurve.TotalToReach(RpgActorKinds.Empire, 4));
            Assert.Equal(1, RpgXpAwards.SpeciesLevelUp);
        }
        finally
        {
            ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Species_level_up_award_rejects_a_non_positive_value(long value)
    {
        var json = $$"""
        { "xpCurve": { "player": { "first": 60, "step": 30 }, "plant": { "first": 60, "step": 30 }, "zombie": { "first": 60, "step": 30 }, "specimen": { "first": 60, "step": 30 }, "empire": { "first": 10, "step": 5 } },
          "awards": { "kill": 20, "defeat": -100, "mower": -30, "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": {{value}} } }
        """;

        Assert.Throws<ProgressionTuningRejection>(() => ProgressionTuningLoader.Parse(json));
    }

    [Fact]
    public void A_present_but_malformed_empire_curve_is_still_a_parse_rejection()
    {
        const string json = """
        { "xpCurve": { "player": { "first": 60, "step": 30 }, "plant": { "first": 60, "step": 30 }, "zombie": { "first": 60, "step": 30 }, "specimen": { "first": 60, "step": 30 }, "empire": { "first": 10 } },
          "awards": { "kill": 20, "defeat": -100, "mower": -30, "plantPlace": 8, "zombieSpawn": 9, "speciesLevelUp": 1 } }
        """;

        Assert.Throws<ProgressionTuningRejection>(() => ProgressionTuningLoader.Parse(json));
    }
}
