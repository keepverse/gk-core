using System.Reflection;
using FusionRpg.Contracts;
using FusionRpg.Core.Narrative.Text;
using Xunit;

namespace FusionRpg.Core.Tests.Narrative.Text;

/// <summary>
/// npc-story-events NR2.15 (spec-narrative-text.md §1–§3, §SOLID): the closed token and markup grammar,
/// the one binder every server read model uses, and the wire shape that carries references instead of
/// names. The rule ids are the loaders' contract, so each refusal is asserted BY ID, not by message.
/// </summary>
[Trait("VerificationId", "core.narrative")]
[Trait("Guard", "narrative")]
public sealed class NarrativeTextTests
{
    static readonly string[] DeclaredRoles = { "warden", "guest" };
    static readonly string[] KnownCharacters = { "rotwright", "hourbloom" };

    static TextRef Text(string text) => new("narr.test.line", text);

    static IReadOnlyList<GrammarIssue> Validate(string text) =>
        TokenGrammar.Validate(Text(text), DeclaredRoles, KnownCharacters);

    // ---- the grammar: one rule id per refusal ------------------------------------------------------

    [Theory]
    [InlineData("the {lead_summoner whispers", TokenGrammar.StrayBrace)]
    [InlineData("a bare } brace", TokenGrammar.StrayBrace)]
    [InlineData("{lead_villain} arrives", TokenGrammar.UnknownToken)]
    [InlineData("{}", TokenGrammar.UnknownToken)]
    [InlineData("{lead_summoner_sideways} arrives", TokenGrammar.UnknownToken)]
    [InlineData("{role_stranger} arrives", TokenGrammar.RoleUndeclared)]
    [InlineData("{c_nobody} arrives", TokenGrammar.CharacterUnknown)]
    [InlineData("a <blink>moment</blink>", TokenGrammar.MarkupUnknown)]
    [InlineData("<em>never closed", TokenGrammar.MarkupUnbalanced)]
    [InlineData("</em> closes nothing", TokenGrammar.MarkupUnbalanced)]
    [InlineData("<em><whisper>crossed</em></whisper>", TokenGrammar.MarkupUnbalanced)]
    [InlineData("3 souls", TokenGrammar.DigitInText)]
    public void A_rule_id_each_refusal(string text, string expectedRuleId)
    {
        var issues = Validate(text);

        Assert.Contains(issues, issue => issue.RuleId == expectedRuleId);
    }

    [Fact]
    public void The_whole_closed_grammar_validates_clean()
    {
        var text = "{lead_summoner_start} and {lead_companion} meet {c_rotwright_epithet}, "
            + "who owes {role_warden} a debt: <em>{place}</em>, <whisper>{supply}</whisper>, "
            + "{reward} souls and {cost} loam.<pause/>";

        var issues = Validate(text);

        Assert.Empty(issues);
    }

    [Fact]
    public void A_suffix_is_split_off_the_name_it_is_carried_by()
    {
        var token = Assert.Single(TokenGrammar.Tokens("{c_rotwright_epithet_poss}"));

        Assert.Equal(TokenForm.CharacterEpithet, token.Form);
        Assert.Equal("rotwright", token.Name);
        Assert.Equal("_poss", token.Suffix);
    }

    [Fact]
    public void A_role_id_is_known_by_its_slug_not_its_prefix()
    {
        var token = Assert.Single(TokenGrammar.Tokens("{role_warden}"));

        Assert.Equal(TokenForm.Role, token.Form);
        Assert.Equal("warden", token.Name);
    }

    [Fact]
    public void A_placeholder_takes_no_suffix()
    {
        // `{reward_poss}` is not the pronoun of a reward — it is not in the grammar at all.
        Assert.Contains(Validate("{reward_poss}"), issue => issue.RuleId == TokenGrammar.UnknownToken);
    }

    // ---- the binder --------------------------------------------------------------------------------

    static StoryletCast Cast() => new()
    {
        Roles = new Dictionary<string, CastBinding>(StringComparer.Ordinal)
        {
            // The cast resolver stores what the role WAS CAST TO — the resolved reference, not the
            // role id and not a slug the client would have to resolve a second time.
            ["warden"] = new CastBinding("character", "creature.rotwright")
        },
        Leads = new Dictionary<string, CastBinding>(StringComparer.Ordinal)
        {
            ["lead_summoner"] = new CastBinding("lead", "lead_summoner"),
            ["lead_antagonist"] = new CastBinding("lead", "lead_antagonist")
        },
        Characters = new Dictionary<string, CastBinding>(StringComparer.Ordinal)
        {
            ["rotwright"] = new CastBinding("character", "creature.rotwright"),
            ["hourbloom"] = new CastBinding("character-epithet", "creature.hourbloom")
        },
        Placeholders = new Dictionary<string, CastBinding>(StringComparer.Ordinal)
        {
            ["place"] = new CastBinding("delve-domain", "domain.sunken-vault"),
            ["supply"] = new CastBinding("supply", "supply.warden-torch"),
            ["reward"] = new CastBinding("magnitude", "souls", Amount: 5_000_000_000L, Unit: "souls"),
            ["cost"] = new CastBinding("magnitude", "loam", Amount: 250L, Unit: "loam")
        }
    };

    [Fact]
    public void Every_token_in_a_fixture_storylet_binds_from_a_fixture_cast()
    {
        var text = Text("{lead_summoner} sends {c_hourbloom_epithet} to {role_warden}, "
            + "who takes {place} for {reward}.");

        var bound = StoryTextBinder.Bind(text, Cast());

        Assert.Equal(
            new[] { "lead_summoner", "c_hourbloom_epithet", "role_warden", "place", "reward" }.OrderBy(k => k, StringComparer.Ordinal),
            bound.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("lead", bound["lead_summoner"].Kind);
        Assert.Equal("character-epithet", bound["c_hourbloom_epithet"].Kind);
        Assert.Equal("delve-domain", bound["place"].Kind);
    }

    [Fact]
    public void A_role_token_binds_to_what_the_role_was_cast_to_not_to_the_role_id()
    {
        var bound = StoryTextBinder.Bind(Text("{role_warden} speaks"), Cast());

        Assert.Equal("character", bound["role_warden"].Kind);
        Assert.Equal("creature.rotwright", bound["role_warden"].Id);
    }

    [Fact]
    public void An_unbound_token_fails_naming_it()
    {
        var ex = Assert.Throws<StoryTextBindingFailure>(() =>
            StoryTextBinder.Bind(Text("{lead_summoner} and {lead_antagonist} and {c_nightshade}"), Cast()));

        // The two leads ARE bound by this cast; `c_nightshade` is not, so the failure must name it
        // rather than the field alone.
        Assert.Equal("c_nightshade", ex.Token);
        Assert.Equal("narr.test.line", ex.Key);
        Assert.Contains("c_nightshade", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_magnitude_carries_long()
    {
        var bound = StoryTextBinder.Bind(Text("{reward}"), Cast());

        Assert.True(bound["reward"].Amount > int.MaxValue, "a P(Θ) magnitude outgrows int at reachable Θ");
        Assert.Equal(5_000_000_000L, bound["reward"].Amount);
        Assert.Equal("souls", bound["reward"].Unit);
        Assert.Equal(typeof(long?), typeof(TokenRefDto).GetProperty(nameof(TokenRefDto.Amount))!.PropertyType);
    }

    [Fact]
    public void Binding_wraps_into_the_wire_dto()
    {
        var dto = StoryTextBinder.BindDto(Text("{place}"), Cast());

        Assert.Equal("narr.test.line", dto.Key);
        Assert.Equal("domain.sunken-vault", dto.Tokens["place"].Id);
    }

    // ---- references, never names -------------------------------------------------------------------

    [Fact]
    public void The_wire_types_carry_no_display_string()
    {
        // A `Display`/`Name`/`Label` member on either type is the defect the DTO exists to prevent, so
        // the absence is asserted rather than promised.
        var suspicious = new[] { typeof(NarrativeTextDto), typeof(TokenRefDto) }
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(Dictionary<string, string>))
                .Select(p => $"{type.Name}.{p.Name}"))
            .Where(name => name.Contains("display", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".Name", StringComparison.Ordinal)
                || name.Contains("label", StringComparison.OrdinalIgnoreCase)
                || name.Contains("text", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".Key", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void Only_one_contracts_type_carries_a_text_key_and_a_token_map()
    {
        var carrying = typeof(NarrativeTextDto).Assembly.GetTypes()
            .Where(type => type.IsPublic && !type.IsAbstract)
            .Where(type => type.GetProperty("Key")?.PropertyType == typeof(string))
            .Where(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p =>
                p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericArguments().Last() == typeof(TokenRefDto)))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { nameof(NarrativeTextDto) }, carrying);
    }
}
