using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using Xunit;

namespace FusionRpg.Core.Tests.Saves;

/// <summary>
/// `save-identity` SE4.11 — the save/empire value types and the authored new-save registry. Each
/// validator rule has its own falsifier; nothing asserts a row count.
/// </summary>
public class NewSaveEmpiresTests
{
    static NewSaveEmpires Shipped() =>
        NewSaveEmpires.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "saves", "_registry", "new-save-empires.v1.json")));

    static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
    }

    static string Registry(string empires) =>
        $$"""{ "schemaVersion": 1, "empires": [ {{empires}} ] }""";

    [Fact]
    public void The_shipped_registry_has_exactly_one_human_and_the_two_well_known_empires()
    {
        var registry = Shipped();
        Assert.Equal(1, registry.Rows.Count(r => r.Controller == EmpireController.Human));
        Assert.Contains(registry.Rows, r => r.EmpireId == "dave" && r.Controller == EmpireController.Human);
        Assert.Contains(registry.Rows, r => r.EmpireId == "zomboss" && r.Controller == EmpireController.Ai);
    }

    [Fact]
    public void A_second_human_row_is_refused() =>
        Assert.Throws<FormatException>(() => NewSaveEmpires.Parse(Registry(
            """{ "empireId": "dave", "controller": "human" }, { "empireId": "penny", "controller": "human" }""")));

    [Fact]
    public void No_human_row_is_refused() =>
        Assert.Throws<FormatException>(() => NewSaveEmpires.Parse(Registry(
            """{ "empireId": "zomboss", "controller": "ai" }""")));

    [Theory]
    [InlineData("robot")]
    [InlineData("")]
    [InlineData("HUMAN")]
    public void An_unknown_or_miscased_controller_is_refused(string controller) =>
        Assert.Throws<FormatException>(() => NewSaveEmpires.Parse(Registry(
            $$"""{ "empireId": "dave", "controller": "{{controller}}" }""")));

    [Theory]
    [InlineData("commander:dave")]
    [InlineData("dave smith")]
    public void An_empire_id_that_is_not_a_bare_faction_token_is_refused(string empireId) =>
        Assert.Throws<FormatException>(() => NewSaveEmpires.Parse(Registry(
            $$"""{ "empireId": "{{empireId}}", "controller": "human" }""")));

    [Fact]
    public void A_wrong_schema_version_is_refused() =>
        Assert.Throws<FormatException>(() => NewSaveEmpires.Parse(
            """{ "schemaVersion": 2, "empires": [ { "empireId": "dave", "controller": "human" } ] }"""));

    [Fact]
    public void The_controller_vocabulary_is_the_two_the_spec_names()
    {
        // Closed on purpose: a third decider is a reviewed design change, not a new value.
        Assert.Equal(EmpireController.Human, NewSaveEmpires.Controller("human"));
        Assert.Equal(EmpireController.Ai, NewSaveEmpires.Controller("ai"));
        Assert.Equal(2, Enum.GetValues(typeof(EmpireController)).Length);
    }

    [Fact]
    public void Save_id_and_empire_ref_are_value_types_over_the_save_and_the_faction()
    {
        var save = new SaveId(7);
        var daveOf7 = new EmpireRef(save, EmpireId.Dave);
        var zombossOf7 = new EmpireRef(save, EmpireId.Zomboss);

        Assert.Equal(save, daveOf7.Save);
        Assert.Equal(EmpireId.Dave, daveOf7.Empire);
        Assert.NotEqual(daveOf7, zombossOf7);           // one save, two empires: different owners
        Assert.NotEqual(daveOf7, new EmpireRef(new SaveId(8), EmpireId.Dave)); // one empire, two saves
    }
}
