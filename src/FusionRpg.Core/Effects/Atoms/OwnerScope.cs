using System.Text.RegularExpressions;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The eight owner scopes a binding may attach to (definitions.md §6).
///
/// <para><b><see cref="Slot"/> is a world-map construction slot</b>, unrelated to an item's `slot`
/// column. Two different concepts, one word — deliberately not the same type.</para>
///
/// <para><b><see cref="UniqueActor"/> is a persistent `rpg_unique_actor`</b>, keyed on the actor's own
/// stable `instance_id` — durable like <see cref="Player"/>, just scoped to one actor rather than the
/// whole account. Added 2026-09-01 (`tasks/seed-to-concrete-open-decisions.md` decision 1, owner-
/// approved) specifically because <see cref="Entity"/> cannot be reused for this: its pointer is
/// session-scoped and reused, so an equipped-item binding written as `entity:` would silently vanish
/// on the next session boundary (`ClearSessionScopedBindings()` deletes every `entity:` binding on
/// purpose). A unique actor's `instance_id` is never reused, so its bindings are never session-scoped.
/// </para>
/// </summary>
public enum OwnerKind
{
    Match = 0,
    Plant,
    Zombie,
    Entity,
    Player,
    Sector,
    Slot,
    UniqueActor,
}

/// <summary>
/// A parsed owner key. The canonical string form is <c>{kind}:{key}</c>, and <c>match</c> renders as
/// bare <c>match</c> with no colon.
///
/// <para><c>entity:0xABC</c> and <c>entity:abc</c> were both in circulation. <b>Only the second
/// parses</b> — anything else is <see cref="AtomRejectionReason.BadOwnerKey"/>, because two spellings
/// of one pointer means two bindings the withdraw path cannot match.</para>
/// </summary>
public readonly record struct OwnerScope(OwnerKind Kind, string Key)
{
    static readonly Regex HexRe = new("^[0-9a-f]+$", RegexOptions.Compiled);
    static readonly Regex DecimalRe = new("^(0|[1-9][0-9]*)$", RegexOptions.Compiled);
    static readonly Regex IdRe = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static OwnerScope Match => new(OwnerKind.Match, "");

    /// <summary><c>entity:</c> bindings are session-scoped and never durable — the pointer is reused.</summary>
    public bool IsSessionScoped => Kind == OwnerKind.Entity;

    public override string ToString() => Kind == OwnerKind.Match ? "match" : $"{Name(Kind)}:{Key}";

    public static string Name(OwnerKind kind) => kind switch
    {
        OwnerKind.Match => "match",
        OwnerKind.Plant => "plant",
        OwnerKind.Zombie => "zombie",
        OwnerKind.Entity => "entity",
        OwnerKind.Player => "player",
        OwnerKind.Sector => "sector",
        OwnerKind.Slot => "slot",
        OwnerKind.UniqueActor => "unique-actor",
        _ => "",
    };

    /// <summary>Parse the canonical string form. Every failure is `BadOwnerKey`, never a default.</summary>
    public static AtomRejection TryParse(string? text, out OwnerScope scope)
    {
        scope = default;
        if (string.IsNullOrWhiteSpace(text))
            return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey, "owner key is empty");

        if (string.Equals(text, "match", StringComparison.Ordinal))
        {
            scope = Match;
            return AtomRejection.Ok;
        }

        var colon = text!.IndexOf(':');
        if (colon <= 0)
            return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey,
                $"'{text}' is not '{{kind}}:{{key}}' (only `match` has no colon)");

        var kindName = text[..colon];
        var key = text[(colon + 1)..];

        OwnerKind kind = default;
        var known = false;
        foreach (OwnerKind candidate in Enum.GetValues(typeof(OwnerKind)))
        {
            if (!string.Equals(Name(candidate), kindName, StringComparison.Ordinal)) continue;
            kind = candidate;
            known = true;
            break;
        }

        if (!known)
            return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey, $"unknown owner kind '{kindName}'");

        if (kind == OwnerKind.Match)
            return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey,
                "match takes no key; write it as bare `match`");

        return Validate(kind, key, out scope);
    }

    /// <summary>Validate a kind/key pair directly, for callers holding them separately.</summary>
    public static AtomRejection Validate(OwnerKind kind, string key, out OwnerScope scope)
    {
        scope = default;
        key ??= "";

        switch (kind)
        {
            case OwnerKind.Match:
                if (key.Length != 0)
                    return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey, "match takes an empty key");
                break;

            case OwnerKind.Plant:
            case OwnerKind.Zombie:
                // typeIds for types that do not exist are ACCEPTED: type catalogs are game data we
                // do not own, and refusing them would make us the authority on someone else's list.
                if (!DecimalRe.IsMatch(key))
                    return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey,
                        $"{Name(kind)} takes a decimal typeId >= 0, got '{key}'");
                break;

            case OwnerKind.Entity:
                if (!HexRe.IsMatch(key))
                    return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey,
                        $"entity takes lowercase hex with no 0x prefix, got '{key}'");
                break;

            case OwnerKind.Player:
                if (!DecimalRe.IsMatch(key) || key == "0")
                    return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey,
                        $"player takes a decimal id > 0, got '{key}'");
                break;

            case OwnerKind.Sector:
            case OwnerKind.Slot:
                // Existence is a bind-time check against the world host, not a grammar check.
                if (!IdRe.IsMatch(key))
                    return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey,
                        $"{Name(kind)} takes a kebab-case id, got '{key}'");
                break;

            case OwnerKind.UniqueActor:
                // Same grammar as Sector/Slot (kebab/alnum id) — durability differs (this scope is
                // durable, never session-scoped), the key shape does not. Existence is a bind-time
                // check against rpg_unique_actor, not a grammar check.
                if (!IdRe.IsMatch(key))
                    return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey,
                        $"{Name(kind)} takes a kebab-case instance id, got '{key}'");
                break;

            default:
                return AtomRejection.Fail(AtomRejectionReason.BadOwnerKey, $"unknown owner kind {kind}");
        }

        scope = new OwnerScope(kind, key);
        return AtomRejection.Ok;
    }
}
