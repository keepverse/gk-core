using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FusionRpg.Core.Narrative;
using FusionRpg.TestSupport;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Narrative;

/// <summary>
/// identity-rename T1: the lead names registry
/// (<c>gk-data/packs/fusion/data/seed/narrative/_registry/names.en.v1.json</c>) and its Core parser. The registry makes a
/// lead's display string a parameter, so the tests pin the <b>tokens</b> (a closed vocabulary, §6.5b)
/// and the closed tag enums — never a display string (a rename is one row edit) and never the size of
/// <c>names</c> (narrative-seed unions character rows into the same set later).
/// </summary>
[Trait("VerificationId", "core.lead-names")]
public class LeadNamesTests
{
    static string ShippedPath() => Path.Combine(
        KeepverseRoots.Content(), "data", "seed", "narrative", "_registry", "names.en.v1.json");

    static LeadNames Shipped() => LeadNames.Parse(File.ReadAllText(ShippedPath()));

    // ---- fixtures: one row, three leads, a whole file. Invented names only. ----

    static string Row(
        string display = "Garden Keeper",
        string article = "definite",
        string gender = "male",
        string number = "singular",
        string ruling = "R11",
        string? omit = null)
    {
        var parts = new List<string>();
        if (omit != "display") parts.Add($"\"display\":\"{display}\"");
        if (omit != "article") parts.Add($"\"article\":\"{article}\"");
        if (omit != "gender") parts.Add($"\"gender\":\"{gender}\"");
        if (omit != "number") parts.Add($"\"number\":\"{number}\"");
        if (omit != "ruling") parts.Add($"\"ruling\":\"{ruling}\"");
        return "{" + string.Join(",", parts) + "}";
    }

    static string ThreeLeadNames(string? summonerRow = null, string? companionRow = null,
        string? antagonistRow = null, string? omitToken = null)
    {
        var parts = new List<string>();
        if (omitToken != LeadTokens.Summoner)
            parts.Add("\"lead_summoner\":" + (summonerRow ?? Row()));
        if (omitToken != LeadTokens.Companion)
            parts.Add("\"lead_companion\":" + (companionRow ?? Row("Hourbloom", article: "none", gender: "neuter")));
        if (omitToken != LeadTokens.Antagonist)
            parts.Add("\"lead_antagonist\":" + (antagonistRow ?? Row("Rotwright")));
        return string.Join(",", parts);
    }

    static string Registry(string names, string schemaVersion = "1", string locale = "\"en\"") =>
        "{\"schemaVersion\":" + schemaVersion + ",\"locale\":" + locale + ",\"names\":{" + names + "}}";

    // ---- the closed vocabularies ----

    [Fact]
    public void The_lead_token_vocabulary_is_closed()
    {
        // Pinned literally, with its reason: §6.5b's lead family is closed and every consumer of the
        // registry (the C# hub, the server boot, the web reader) keys off these three strings. A
        // fourth lead is a token-grammar change (narrative-seed module 8 token-grammar), so adding one
        // has to fail this test first.
        Assert.Equal(new[] { "lead_summoner", "lead_companion", "lead_antagonist" }, LeadTokens.All);
    }

    [Fact]
    public void The_grammar_tags_are_closed_enums()
    {
        // A closed vocabulary the code owns and a human changes: a new tag must be a deliberate edit
        // to this test, the parser's mapping and every message's ICU select arm together.
        Assert.Equal(new[] { "Definite", "None" }, Enum.GetNames<NameArticle>());
        Assert.Equal(new[] { "Male", "Female", "Neuter", "None" }, Enum.GetNames<NameGender>());
        Assert.Equal(new[] { "Singular", "Plural", "None" }, Enum.GetNames<NameNumber>());
    }

    // ---- the shipped file ----

    [Fact]
    public void Shipped_registry_carries_the_three_lead_rows_and_their_rulings()
    {
        var names = Shipped();

        Assert.Equal("en", names.Locale);
        Assert.Equal(LeadNames.SupportedSchemaVersion, names.SchemaVersion);
        Assert.True(File.Exists(ShippedPath()));

        foreach (var token in LeadTokens.All)
        {
            var row = names.Row(token); // throws when the row is absent
            Assert.Equal(token, row.Token);
            Assert.False(string.IsNullOrWhiteSpace(row.Display));
            Assert.False(string.IsNullOrWhiteSpace(row.Ruling));
        }
        // Deliberately no count: the file is required to hold the three leads, not to hold only them.
    }

    [Fact]
    public void A_second_name_set_renders_different_display_strings_for_the_same_tokens()
    {
        var shipped = Shipped();
        var synthetic = LeadNames.Parse(Registry(ThreeLeadNames(
            summonerRow: Row("Examplar Vane"),
            companionRow: Row("Lumen Reed", article: "none", gender: "neuter"),
            antagonistRow: Row("Ashwright"))));

        foreach (var token in LeadTokens.All)
        {
            Assert.Equal(token, synthetic.Row(token).Token);
            Assert.NotEqual(shipped.Display(token), synthetic.Display(token));
        }
        // The tokens are the contract; the display strings are the parameters. The shipped file's own
        // tags for the three leads are pinned by narrative-seed's registry suite, not here, so that a
        // rename stays a one-file edit.
    }

    // ---- refusals ----

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("locale")]
    [InlineData("names")]
    public void A_missing_root_field_is_refused(string omitted)
    {
        var parts = new List<string>();
        if (omitted != "schemaVersion") parts.Add("\"schemaVersion\":1");
        if (omitted != "locale") parts.Add("\"locale\":\"en\"");
        if (omitted != "names") parts.Add("\"names\":{" + ThreeLeadNames() + "}");

        var ex = Assert.Throws<FormatException>(
            () => LeadNames.Parse("{" + string.Join(",", parts) + "}"));
        Assert.Contains(omitted, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("article")]
    [InlineData("gender")]
    [InlineData("number")]
    [InlineData("ruling")]
    public void A_missing_row_field_is_refused(string omitted)
    {
        var json = Registry(ThreeLeadNames(summonerRow: Row(omit: omitted)));

        var ex = Assert.Throws<FormatException>(() => LeadNames.Parse(json));
        Assert.Contains(omitted, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("article", "indefinite")]
    [InlineData("gender", "fem")]
    [InlineData("number", "many")]
    public void An_unknown_tag_value_is_refused(string field, string value)
    {
        var summonerRow = field switch
        {
            "article" => Row(article: value),
            "gender" => Row(gender: value),
            _ => Row(number: value),
        };

        var ex = Assert.Throws<FormatException>(
            () => LeadNames.Parse(Registry(ThreeLeadNames(summonerRow: summonerRow))));
        Assert.Contains($"unknown {field} '{value}'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LeadTokens.Summoner)]
    [InlineData(LeadTokens.Companion)]
    [InlineData(LeadTokens.Antagonist)]
    public void A_missing_lead_token_is_refused(string omitted)
    {
        var json = Registry(ThreeLeadNames(omitToken: omitted));

        var ex = Assert.Throws<FormatException>(() => LeadNames.Parse(json));
        Assert.Contains(omitted, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("the Garden Keeper")]
    [InlineData("a Keeper")]
    [InlineData("Garden 3")]
    [InlineData("{Garden}")]
    [InlineData("Garden <em>Keeper</em>")]
    public void A_display_that_is_not_a_bare_name_is_refused(string display)
    {
        var json = Registry(ThreeLeadNames(summonerRow: Row(display)));

        Assert.Throws<FormatException>(() => LeadNames.Parse(json));
    }

    [Fact]
    public void A_row_key_that_is_not_a_token_is_refused()
    {
        var ex = Assert.Throws<FormatException>(
            () => LeadNames.Parse(Registry("\"Lead Summoner\":" + Row())));
        Assert.Contains("Lead Summoner", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_schema_version_is_refused()
    {
        var json = Registry(ThreeLeadNames(), schemaVersion: "2");

        var ex = Assert.Throws<FormatException>(() => LeadNames.Parse(json));
        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_token_throws_and_names_the_token()
    {
        var ex = Assert.Throws<UnknownLeadNameException>(() => Shipped().Display("lead_sidekick"));
        Assert.Equal("lead_sidekick", ex.Token);
    }

    // ---- the hub the server configures at boot ----

    [Fact]
    public void The_hub_refuses_before_a_registry_is_configured_and_serves_it_afterwards()
    {
        LeadNamesHub.Reset();
        try
        {
            // A guessed name would reach player-facing text (and, at T13, a persisted faction row).
            Assert.False(LeadNamesHub.IsConfigured);
            Assert.Throws<InvalidOperationException>(() => LeadNamesHub.Current);

            var shipped = Shipped();
            LeadNamesHub.Configure(shipped);

            Assert.True(LeadNamesHub.IsConfigured);
            Assert.Same(shipped, LeadNamesHub.Current);
            Assert.Equal(shipped.Display(LeadTokens.Summoner),
                LeadNamesHub.Current.Display(LeadTokens.Summoner));
        }
        finally
        {
            // The hub is PROCESS-WIDE, so this test must put the process back the way it found it: the
            // assembly's module initializer (`ContractTuningTestBootstrap`) configures it, and leaving it
            // unconfigured after this test broke every later test that builds a world template or an
            // empty save — 157 failures in a full-project run, none in a run that selects only this class
            // (found by identity-rename T13's module-level verification).
            LeadNamesHub.Configure(Shipped());
        }
    }
}
