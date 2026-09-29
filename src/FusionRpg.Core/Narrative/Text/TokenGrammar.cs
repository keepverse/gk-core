using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FusionRpg.Core.Narrative.Text;

/// <summary>
/// One authored text field: <paramref name="Key"/> is the lingui id (the seed text key) and
/// <paramref name="Text"/> is the literal string, tokens and markup included
/// (spec-narrative-text.md §1, §2). Nothing here resolves a name — this is the reference, not the value.
/// </summary>
public sealed record TextRef(string Key, string Text);

/// <summary>What kind of thing a `{...}` token names — the grammar's own closed classification.</summary>
public enum TokenForm
{
    /// <summary>`{lead_summoner}` and its siblings — a lead's name, from the names registry.</summary>
    Lead,

    /// <summary>`{c_&lt;slug&gt;}` — a character's keyed name from the character corpus.</summary>
    Character,

    /// <summary>`{c_&lt;slug&gt;_epithet}` — the character's keyed epithet rather than its name.</summary>
    CharacterEpithet,

    /// <summary>`{role_&lt;roleId&gt;}` — whatever the storylet's cast bound that role to.</summary>
    Role,

    /// <summary>`{place}` `{supply}` `{reward}` `{cost}` — the cast's own entity references.</summary>
    Placeholder
}

/// <summary>One token as authored: its literal spelling, its form, the id it names and the form or
/// pronoun suffix it carries (<c>c_rotwright_epithet_poss</c> → name <c>rotwright</c>, suffix
/// <c>_poss</c>).</summary>
public sealed record GrammarToken(string Raw, TokenForm Form, string Name, string? Suffix);

/// <summary>One refusal, with the rule id the loaders report and a detail naming the offending text.</summary>
public sealed record GrammarIssue(string RuleId, string Detail);

/// <summary>
/// The closed narrative token and markup grammar (spec-narrative-text.md §1, whose table is the closed
/// list: the three leads, `c_&lt;slug&gt;` and `c_&lt;slug&gt;_epithet`, the form and pronoun suffixes, `role_&lt;id&gt;`,
/// the four bound placeholders, and `<em>`/`<whisper>`/`<shout>`/`<pause/>`).
///
/// <para>Pure and read-only: the grammar owns what a token MAY be; whether a given role or character
/// exists is the corpus's answer, which <see cref="Validate"/> takes as parameters rather than reading.
/// The names-registry literal-name check and the round-trip render are narrative-seed's validators
/// (spec §3); the runtime repeats token closure only, because an unresolvable token is a runtime failure
/// while a literal name is a generation-time content defect.</para>
/// </summary>
public static class TokenGrammar
{
    /// <summary>The closed markup set (spec §1: "at first ship").</summary>
    public static readonly IReadOnlyList<string> MarkupTags = new[] { "em", "whisper", "shout", "pause" };

    /// <summary>The self-closing half of the set.</summary>
    public static readonly IReadOnlyList<string> SelfClosingMarkupTags = new[] { "pause" };

    /// <summary>The three leads (R11) — the only `lead_*` names that exist.</summary>
    public static readonly IReadOnlyList<string> LeadNames = new[] { "lead_summoner", "lead_companion", "lead_antagonist" };

    /// <summary>The four bound placeholders (spec §1).</summary>
    public static readonly IReadOnlyList<string> Placeholders = new[] { "place", "supply", "reward", "cost" };

    /// <summary>The form and pronoun suffixes a lead or character token may carry (spec §1).</summary>
    public static readonly IReadOnlyList<string> Suffixes = new[] { "_start", "_bare", "_subj", "_obj", "_poss" };

    /// <summary>Rule ids, one per refusal. <c>role-undeclared</c> and <c>digit-in-text</c> are the two
    /// spec §3 names; the rest follow the same <c>storylet.&lt;rule&gt;</c> spelling.</summary>
    public const string StrayBrace = "storylet.stray-brace";
    public const string UnknownToken = "storylet.unknown-token";
    public const string RoleUndeclared = "storylet.role-undeclared";
    public const string CharacterUnknown = "storylet.character-unknown";
    public const string MarkupUnknown = "storylet.markup-unknown";
    public const string MarkupUnbalanced = "storylet.markup-unbalanced";
    public const string DigitInText = "storylet.digit-in-text";

    static readonly Regex TokenPattern = new(@"\{[^{}]*\}", RegexOptions.Compiled);
    static readonly Regex MarkupPattern = new(@"<(?<close>/?)(?<name>[a-zA-Z]+)(?<self>/?)>", RegexOptions.Compiled);
    static readonly Regex CharacterPattern = new(@"^c_(?<slug>[a-z0-9_]+?)(?<epithet>_epithet)?$", RegexOptions.Compiled);
    static readonly Regex RoleSlugPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>The text's tokens, in order, with any suffix split off. Malformed spellings are not
    /// refused here — <see cref="Validate"/> is the one place that refuses.</summary>
    public static IReadOnlyList<GrammarToken> Tokens(string? text)
    {
        var found = new List<GrammarToken>();
        if (string.IsNullOrEmpty(text)) return found;

        foreach (System.Text.RegularExpressions.Match match in TokenPattern.Matches(text))
            found.Add(Classify(match.Value[1..^1]));

        return found;
    }

    /// <summary>Every refusal in one text field. Empty means the text is well formed and its roles and
    /// characters are known.</summary>
    public static IReadOnlyList<GrammarIssue> Validate(
        TextRef text, IEnumerable<string> declaredRoleIds, IEnumerable<string> knownCharacterSlugs)
    {
        ArgumentNullException.ThrowIfNull(text);
        var roles = new HashSet<string>(declaredRoleIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var characters = new HashSet<string>(knownCharacterSlugs ?? Array.Empty<string>(), StringComparer.Ordinal);
        var issues = new List<GrammarIssue>();

        if (text.Text is null)
        {
            issues.Add(new GrammarIssue(UnknownToken, $"{text.Key}: the text is null"));
            return issues;
        }

        // Braces: every brace must belong to a token. Removing the well-formed spans leaves only strays.
        if (TokenPattern.Replace(text.Text, "").IndexOfAny(new[] { '{', '}' }) >= 0)
            issues.Add(new GrammarIssue(StrayBrace, $"{text.Key}: a stray '{{' or '}}' — braces only open and close a token"));

        CheckMarkup(text, issues);

        if (Regex.IsMatch(text.Text, "[0-9]"))
            issues.Add(new GrammarIssue(DigitInText, $"{text.Key}: an ASCII digit in story text — a magnitude is a token, never a typed number"));

        foreach (System.Text.RegularExpressions.Match match in TokenPattern.Matches(text.Text))
        {
            var raw = match.Value[1..^1];
            if (raw.Length == 0)
            {
                issues.Add(new GrammarIssue(UnknownToken, $"{text.Key}: an empty token '{{}}'"));
                continue;
            }

            var token = Classify(raw);
            if (token.Form == TokenForm.Role)
            {
                if (!roles.Contains(token.Name))
                    issues.Add(new GrammarIssue(RoleUndeclared, $"{text.Key}: token '{{{raw}}}' names role '{token.Name}', which the storylet's roles[] does not declare"));
            }
            else if (token.Form is TokenForm.Character or TokenForm.CharacterEpithet)
            {
                if (!characters.Contains(token.Name))
                    issues.Add(new GrammarIssue(CharacterUnknown, $"{text.Key}: token '{{{raw}}}' names character '{token.Name}', which the corpus does not know"));
            }
            else if (!IsKnown(raw, token))
            {
                issues.Add(new GrammarIssue(UnknownToken, $"{text.Key}: token '{{{raw}}}' is not in the closed grammar"));
            }
        }

        return issues;
    }

    /// <summary>The token a raw `{...}` body names. Unknown spellings classify as
    /// <see cref="TokenForm.Placeholder"/> with the raw body as the name, and
    /// <see cref="IsKnown"/> is what refuses them.</summary>
    static bool Has(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
        return false;
    }

    static string? FindSuffix(string raw)
    {
        for (var i = 0; i < Suffixes.Count; i++)
            if (raw.EndsWith(Suffixes[i], StringComparison.Ordinal)) return Suffixes[i];
        return null;
    }

    static GrammarToken Classify(string raw)
    {
        var suffix = FindSuffix(raw);
        var core = suffix is null ? raw : raw[..^suffix.Length];

        if (suffix is null && Has(Placeholders, core))
            return new GrammarToken(raw, TokenForm.Placeholder, core, null);

        if (Has(LeadNames, core))
            return new GrammarToken(raw, TokenForm.Lead, core, suffix);

        var character = CharacterPattern.Match(core);
        if (character.Success)
            return new GrammarToken(
                raw,
                character.Groups["epithet"].Success ? TokenForm.CharacterEpithet : TokenForm.Character,
                character.Groups["slug"].Value,
                suffix);

        if (suffix is null && core.StartsWith("role_", StringComparison.Ordinal) && core.Length > "role_".Length
            && RoleSlugPattern.IsMatch(core["role_".Length..]))
            return new GrammarToken(raw, TokenForm.Role, core["role_".Length..], null);

        return new GrammarToken(raw, TokenForm.Placeholder, raw, null);
    }

    /// <summary>A placeholder is known by its exact spelling; every other form was already validated by
    /// <see cref="Classify"/> (which only produces a real form on an exact match).</summary>
    static bool IsKnown(string raw, GrammarToken token) =>
        token.Form != TokenForm.Placeholder || Has(Placeholders, raw);

    static void CheckMarkup(TextRef text, List<GrammarIssue> issues)
    {
        var open = new Stack<string>();
        foreach (System.Text.RegularExpressions.Match match in MarkupPattern.Matches(text.Text))
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            if (!Has(MarkupTags, name))
            {
                issues.Add(new GrammarIssue(MarkupUnknown, $"{text.Key}: <{name}> is not one of the closed tags ({string.Join(", ", MarkupTags)})"));
                continue;
            }

            // `Groups["close"].Success` is TRUE for `<em>` too — an optional group that matched the
            // EMPTY string still reports Success — so the marker must be tested by its value.
            var isClosing = match.Groups["close"].Value.Length > 0;
            var isSelfClosing = match.Groups["self"].Value.Length > 0 || Has(SelfClosingMarkupTags, name);

            if (isSelfClosing)
                continue;

            if (!isClosing)
            {
                open.Push(name);
                continue;
            }

            if (open.Count == 0 || open.Peek() != name)
            {
                issues.Add(new GrammarIssue(MarkupUnbalanced, $"{text.Key}: </{name}> closes nothing open (open: {(open.Count == 0 ? "none" : string.Join(">", open))})"));
                continue;
            }

            open.Pop();
        }

        foreach (var unclosed in open)
            issues.Add(new GrammarIssue(MarkupUnbalanced, $"{text.Key}: <{unclosed}> is never closed"));
    }
}
