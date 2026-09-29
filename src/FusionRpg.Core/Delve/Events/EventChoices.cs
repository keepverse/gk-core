namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.8 (spec-event-deck.md §6: "Choices, v1") — PARTIALLY BUILT: the fixed verb set,
/// presentation, eligibility and autopilot are pure and done; the answer's own persistence
/// (`decisions_json`'s `talk` row) and the grant bind/withdraw on `UniqueActor` are `RpgStore.Delve`'s
/// own job (D3.9), not this file's — see the honest gap below.
/// </summary>
public static class EventChoices
{
    public const string Use = "use";
    public const string Interact = "interact";
    public const string Leave = "leave";

    /// <summary>Story kind, the one gate on `leave` (spec §6, verbatim: "leave (only kind: story)").</summary>
    public const string StoryKind = "story";

    /// <summary>
    /// Which verbs this event PRESENTS at all (spec §6, verbatim): `use:{tag}` present iff
    /// `supplyOverride != none`; `interact` always; `leave` only on `kind: story`. Fixed order —
    /// autopilot's own tie-break depends on it never changing.
    /// </summary>
    public static IReadOnlyList<string> Presented(EventRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        var verbs = new List<string>(3);
        if (row.SupplyOverride is not null) verbs.Add(Use);
        verbs.Add(Interact);
        if (string.Equals(row.Kind, StoryKind, StringComparison.Ordinal)) verbs.Add(Leave);
        return verbs;
    }

    /// <summary>
    /// Eligible RIGHT NOW (spec §6): `use:{tag}` needs `HoldsStock(tag-bearing supply) >= 1` —
    /// <paramref name="holdsOverrideTag"/> is that already-resolved fact (the caller's own
    /// `FactReader.StockQty` read, kept as a plain bool here since "do I hold ANY" is the only
    /// threshold this verb ever checks, matching `HoldsStock`'s own framing); `interact` and `leave`
    /// carry no further gate once presented.
    /// </summary>
    public static bool IsEligible(string verb, bool holdsOverrideTag) => verb switch
    {
        Use => holdsOverrideTag,
        Interact => true,
        Leave => true,
        _ => throw new ArgumentException($"'{verb}' is not one of the fixed verbs (use/interact/leave)", nameof(verb)),
    };

    /// <summary>
    /// Autopilot picks the first eligible verb in the fixed order `use · interact · leave` (spec §6,
    /// verbatim). Always resolves: `interact` is always both presented and eligible, so this can never
    /// fall through empty-handed.
    /// </summary>
    public static string Autopilot(EventRow row, bool holdsOverrideTag)
    {
        foreach (var verb in Presented(row))
            if (IsEligible(verb, holdsOverrideTag))
                return verb;

        throw new InvalidOperationException(
            "no eligible verb -- interact is always both presented and eligible, this should be unreachable");
    }
}
