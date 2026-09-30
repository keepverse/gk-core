using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T7 (basic-attack-seed, spec-basic-attack-seed.md): `kindHint` parsing. Before this task the parser
/// read the field into nothing — real shipped briefs already author `"kindHint": "innate"`
/// (`gk-data/packs/fusion/data/seed/actions/committed-round-2.json`'s `action.species.cabbagepult.002`, among 84 others
/// across the full corpus) and it was silently discarded. That was the defect; this is the fix.
/// </summary>
public class ActionCorpusBriefJsonTests
{
    const string BriefTemplate = """
    {
      "schemaVersion": 1,
      "kind": "action-seed",
      "entries": [
        {
          "id": "action.test.001",
          "name": "Test Action",
          "category": "attack",
          "scope": "general",
          "targetMode": "single",
          "relation": "enemy",
          "descriptionKey": "action.test.desc",
          "rungBand": [1, 1],
          "atomFamilies": ["atom.test"]
          __KIND_HINT__
        }
      ]
    }
    """;

    static string WithKindHint(string? rawJsonValue) =>
        BriefTemplate.Replace("__KIND_HINT__", rawJsonValue is null ? "" : $", \"kindHint\": {rawJsonValue}");

    [Fact]
    public void AnAbsentKindHintParsesToNull()
    {
        var briefs = ActionCorpusBriefJson.Parse(WithKindHint(null));

        Assert.Single(briefs);
        Assert.Null(briefs[0].KindHint);
    }

    [Theory]
    [InlineData("\"basic\"", ActionKind.Basic)]
    [InlineData("\"innate\"", ActionKind.Innate)]
    [InlineData("\"skill\"", ActionKind.Skill)]
    public void AKnownKindHintParsesToTheMatchingActionKind(string rawValue, ActionKind expected)
    {
        var briefs = ActionCorpusBriefJson.Parse(WithKindHint(rawValue));

        Assert.Single(briefs);
        Assert.Equal(expected, briefs[0].KindHint);
    }

    /// <summary>An explicit JSON `null` reads the same as the property being absent entirely — both
    /// mean "no hint", never a rejection.</summary>
    [Fact]
    public void AnExplicitJsonNullKindHintParsesToNull()
    {
        var briefs = ActionCorpusBriefJson.Parse(WithKindHint("null"));

        Assert.Single(briefs);
        Assert.Null(briefs[0].KindHint);
    }

    /// <summary>The acceptance bar, verbatim: "an unrecognized value is REJECTED, and the rejection
    /// names the offending brief's id" — never a silent default to Skill, never coerced.</summary>
    [Fact]
    public void AnUnknownKindHintRejectsNamingTheBriefId()
    {
        var ex = Assert.Throws<ActionCorpusBriefRejection>(() => ActionCorpusBriefJson.Parse(WithKindHint("\"legendary\"")));

        Assert.Contains("action.test.001", ex.Message, StringComparison.Ordinal);
        Assert.Contains("legendary", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A non-string `kindHint` (a number here) is exactly as invalid as an unknown string —
    /// rejected naming the brief, never silently treated as absent.</summary>
    [Fact]
    public void ANonStringKindHintRejectsNamingTheBriefId()
    {
        var ex = Assert.Throws<ActionCorpusBriefRejection>(() => ActionCorpusBriefJson.Parse(WithKindHint("7")));

        Assert.Contains("action.test.001", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Real, measured content (not a hypothetical): `committed-round-2.json`'s
    /// `action.species.cabbagepult.002` really does carry `"kindHint": "innate"` today.</summary>
    [Fact]
    public void ARealShippedBriefsKindHintParses()
    {
        var repoRoot = RepoRoot();
        var path = Path.Combine(repoRoot, "data", "seed", "actions", "committed-round-2.json");
        var briefs = ActionCorpusBriefJson.Parse(File.ReadAllText(path));

        var cabbagepult2 = briefs.Single(b => b.Id == "action.species.cabbagepult.002");
        Assert.Equal(ActionKind.Innate, cabbagepult2.KindHint);

        // Sibling brief in the same file with no kindHint authored -- still parses to null (absent),
        // proving this isn't just "everything defaults to Innate".
        var cabbagepult1 = briefs.Single(b => b.Id == "action.species.cabbagepult.001");
        Assert.Null(cabbagepult1.KindHint);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }
}
