namespace FusionRpg.Core.Effects.Atoms;

/// <summary>Shape of a param value. `Value` means an E2 value spec, not a bare number.</summary>
public enum ParamKind
{
    String,
    Int,
    Bool,
    /// <summary>An E2 ValueSpec — { min, max, roll, scale }. Any number an atom carries is one of these.</summary>
    Value,
    Object,
    Array,
}

/// <summary>
/// One declared param. <paramref name="HonouredOnlyWhen"/> is the G1 guard: the legacy overlay
/// allowlist accepts keys the executor then silently drops, so a key is only legal in the
/// configurations where something actually reads it.
/// </summary>
/// <param name="HonouredOnlyWhen">
/// `null` = honoured always. Otherwise `"discriminator=a|b"` — legal only when that param holds
/// one of those values. Present-but-unhonoured is a rejection, never a shrug.
/// </param>
/// <param name="NotImplementedNote">
/// Non-null means the key exists in the legacy allowlist but nothing implements it anywhere.
/// Declared so the rejection can explain itself instead of reading as an unknown key.
/// </param>
/// <param name="OverlayOrParam">
/// The executor cannot do anything without this key, but the shipped content supplies it through the
/// grant overlay rather than the row — <c>fx.shield_grant</c> and <c>fx.overlay_damage</c> both ship
/// with the magnitude on the overlay and nothing in params. Marking it <see cref="Required"/> would
/// make that content unauthorable; leaving it plainly optional would let a binding that names it
/// nowhere bind and then do nothing. So it is checked at <b>bind</b>, against params and overlay
/// together (D10).
/// </param>
/// <param name="Vocabulary">
/// E29 (spec-kind-value-guard.md): non-null means this param's value must be a member of an
/// enumerable SSOT — a status id, a currency, a board verb. Resolved <b>fresh on every call</b>, never
/// cached, so a vocabulary that grows (a new status registered, a new derived channel) widens what
/// validates with no guard edit — the exact mistake G6's own comment names: "the registry declared
/// PrimaryChannels and never read it, which made the list documentation rather than a rule." Checked
/// by <see cref="AtomKindRegistry.Validate"/>, not here, because the refusal message names the kind
/// and this schema does not know its own kind id.
/// </param>
public sealed record ParamDef(
    string Name,
    ParamKind Kind,
    bool Required = false,
    string? HonouredOnlyWhen = null,
    string? NotImplementedNote = null,
    bool OverlayOrParam = false,
    Func<IReadOnlyCollection<string>>? Vocabulary = null);

/// <summary>The closed key set for one atom kind. Unknown keys reject; unhonoured keys reject.</summary>
public sealed class ParamSchema
{
    readonly Dictionary<string, ParamDef> _defs;

    public ParamSchema(params ParamDef[] defs)
    {
        _defs = new Dictionary<string, ParamDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs) _defs[d.Name] = d;
    }

    public IReadOnlyCollection<ParamDef> Defs => _defs.Values;

    public bool TryGet(string name, out ParamDef def) => _defs.TryGetValue(name, out def!);

    /// <summary>
    /// Validate a param bag against this schema. Returns the first refusal — content authors fix
    /// one thing at a time, and a row is rejected whole regardless of how many rules it broke.
    /// </summary>
    public AtomRejection Validate(IReadOnlyDictionary<string, object?> supplied)
    {
        foreach (var kv in supplied)
        {
            if (!_defs.TryGetValue(kv.Key, out var def))
                return AtomRejection.Fail(AtomRejectionReason.UnknownParam, kv.Key);

            if (def.NotImplementedNote is { } note)
                return AtomRejection.Fail(AtomRejectionReason.ParamNotImplemented, $"{kv.Key}: {note}");

            if (def.HonouredOnlyWhen is { } cond && !ConditionHolds(cond, supplied))
                return AtomRejection.Fail(AtomRejectionReason.ParamNotHonoured,
                    $"{kv.Key} is only honoured when {cond}");
        }

        foreach (var def in _defs.Values)
        {
            if (def.Required && !supplied.ContainsKey(def.Name))
                return AtomRejection.Fail(AtomRejectionReason.MissingParam, def.Name);
        }

        return AtomRejection.Ok;
    }

    /// <summary>`"kind=zombie|bullet"` against the supplied bag. Absent discriminator never holds.</summary>
    static bool ConditionHolds(string condition, IReadOnlyDictionary<string, object?> supplied)
    {
        var eq = condition.IndexOf('=');
        if (eq <= 0) return false;

        var key = condition[..eq].Trim();
        if (!supplied.TryGetValue(key, out var raw) || raw is null) return false;

        var actual = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(actual)) return false;

        foreach (var allowed in condition[(eq + 1)..].Split('|'))
        {
            if (string.Equals(allowed.Trim(), actual, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
