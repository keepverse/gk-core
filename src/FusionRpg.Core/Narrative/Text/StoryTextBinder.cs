using FusionRpg.Contracts;

namespace FusionRpg.Core.Narrative.Text;

/// <summary>
/// One thing a token can be bound to: the kind the client resolves and the id it resolves it by. A
/// magnitude carries its amount and unit as well (<see cref="TokenRefDto.Amount"/> is <c>long</c>).
/// </summary>
public sealed record CastBinding(string Kind, string Id, long? Amount = null, string? Unit = null)
{
    /// <summary>The token reference this binding becomes on the wire.</summary>
    public TokenRefDto ToDto() => new() { Kind = Kind, Id = Id, Amount = Amount, Unit = Unit };
}

/// <summary>
/// The cast a storylet's text binds against (spec-narrative-text.md §2): what each declared role was
/// cast to, the leads and characters the text may name, and the four bound placeholders. The cast
/// RESOLVER (which fills this from the corpus and the player's state) is `cast-resolver`'s; this type is
/// its input contract, and every member is a reference — never a display string.
/// </summary>
public sealed record StoryletCast
{
    /// <summary>Role id → what the role was cast to, for `{role_&lt;id&gt;}`.</summary>
    public IReadOnlyDictionary<string, CastBinding> Roles { get; init; } =
        new Dictionary<string, CastBinding>(StringComparer.Ordinal);

    /// <summary>Lead token name (<c>lead_summoner</c>, ...) → the lead's names-registry reference.</summary>
    public IReadOnlyDictionary<string, CastBinding> Leads { get; init; } =
        new Dictionary<string, CastBinding>(StringComparer.Ordinal);

    /// <summary>Character slug → the character's reference; `c_&lt;slug&gt;_epithet` binds the same entry.</summary>
    public IReadOnlyDictionary<string, CastBinding> Characters { get; init; } =
        new Dictionary<string, CastBinding>(StringComparer.Ordinal);

    /// <summary>The four bound placeholders: <c>place</c>, <c>supply</c>, <c>reward</c>, <c>cost</c>.</summary>
    public IReadOnlyDictionary<string, CastBinding> Placeholders { get; init; } =
        new Dictionary<string, CastBinding>(StringComparer.Ordinal);
}

/// <summary>Thrown when a token the text uses has no binding — the failure names the token and the field,
/// so a content defect is one line away from its cause.</summary>
public sealed class StoryTextBindingFailure : Exception
{
    public string Key { get; }
    public string Token { get; }

    public StoryTextBindingFailure(string key, string token)
        : base($"story text '{key}': token '{{{token}}}' is not bound by the cast — every token the text uses must bind")
    {
        Key = key;
        Token = token;
    }
}

/// <summary>
/// The one binder for every narrative text field (spec-narrative-text.md §2, §SOLID: "one binder for
/// every text field"). It resolves each authored token through the cast and produces the wire map; it
/// never reads a registry, a name or a display string, and it never composes markup — the client renders
/// that from the same closed table the grammar validates against.
/// </summary>
public static class StoryTextBinder
{
    /// <summary>
    /// Every token in <paramref name="text"/> bound by <paramref name="cast"/>, keyed by the token name as
    /// authored. Throws <see cref="StoryTextBindingFailure"/> naming the first token with no binding.
    /// </summary>
    public static Dictionary<string, TokenRefDto> Bind(TextRef text, StoryletCast cast)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(cast);

        var bound = new Dictionary<string, TokenRefDto>(StringComparer.Ordinal);
        foreach (var token in TokenGrammar.Tokens(text.Text))
        {
            if (bound.ContainsKey(token.Raw)) continue;
            bound[token.Raw] = Resolve(token, cast, text.Key).ToDto();
        }

        return bound;
    }

    /// <summary>The same map wrapped as the wire DTO.</summary>
    public static NarrativeTextDto BindDto(TextRef text, StoryletCast cast) =>
        new() { Key = text.Key, Tokens = Bind(text, cast) };

    static CastBinding Resolve(GrammarToken token, StoryletCast cast, string key)
    {
        // role_<id> is replaced by what the role was cast to (spec §2) — the indirection is the point.
        if (token.Form == TokenForm.Role)
            return cast.Roles.TryGetValue(token.Name, out var role) ? role : throw new StoryTextBindingFailure(key, token.Raw);

        if (token.Form == TokenForm.Lead)
            return cast.Leads.TryGetValue(token.Name, out var lead) ? lead : throw new StoryTextBindingFailure(key, token.Raw);

        if (token.Form is TokenForm.Character or TokenForm.CharacterEpithet)
            return cast.Characters.TryGetValue(token.Name, out var character) ? character : throw new StoryTextBindingFailure(key, token.Raw);

        return cast.Placeholders.TryGetValue(token.Name, out var placeholder)
            ? placeholder
            : throw new StoryTextBindingFailure(key, token.Raw);
    }
}
