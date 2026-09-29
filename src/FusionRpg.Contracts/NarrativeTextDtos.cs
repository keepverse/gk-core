using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>
/// Story text on the wire: a KEY and a map of token REFERENCES — never a display string
/// (spec-narrative-text.md §2, R8–R11). The server cannot leak a name this way, and renaming a lead is
/// one names-registry edit with no server change.
///
/// <para>This is the only DTO in this assembly that carries a text key together with a token map; a
/// reflection test in the Core tests asserts exactly that, so a second one cannot appear quietly.</para>
/// </summary>
public sealed class NarrativeTextDto
{
    /// <summary>The seed text key, which is also the lingui id.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>One entry per token the text uses, keyed by the token name as authored
    /// (<c>lead_summoner</c>, <c>c_rotwright</c>, <c>role_warden</c>, <c>reward</c>).</summary>
    [JsonPropertyName("tokens")] public Dictionary<string, TokenRefDto> Tokens { get; set; } = new();
}

/// <summary>
/// One token's reference: what kind of thing it is and which one, resolved on the client. There is
/// deliberately no <c>display</c>/<c>name</c> member — a display string on this type is the defect the
/// DTO exists to prevent.
/// </summary>
public sealed class TokenRefDto
{
    /// <summary>lead | character | character-epithet | sector-slot | delve-domain | homeworld | supply | magnitude.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>The entity id, slug or wire id the client resolves.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>
    /// A magnitude's resolved <c>P(Θ)</c> amount — <c>long</c>, because integer magnitudes outgrow
    /// <c>int</c> at reachable Θ (docs/architecture/numeric-types.md's range table). Null for every other kind.
    /// </summary>
    [JsonPropertyName("amount")] public long? Amount { get; set; }

    /// <summary>A magnitude's stock id (souls, essence, ...). Null for every other kind.</summary>
    [JsonPropertyName("unit")] public string? Unit { get; set; }
}
