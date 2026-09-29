namespace FusionRpg.Core.Delve.Battle;

/// <summary>
/// D2.14 — the delve-level log's closed kind vocabulary (spec-delve-battle-profile.md §4a):
/// `rpg_delves.decisions_json`, appended through `RpgStore.AppendDecision` (already generic over
/// `object` — this module supplies what that object IS, not a store-side change). `object.{verb}` is
/// a template, not a literal: `supplies-and-objects` (unbuilt) owns the verb vocabulary, so this
/// module accepts any `"object."`-prefixed kind rather than hardcoding verbs it does not own.
/// </summary>
public static class DelveDecisionKinds
{
    public const string Enter = "enter";
    public const string Route = "route";
    public const string PackMove = "pack.move";
    public const string PackDrop = "pack.drop";
    public const string Talk = "talk";
    public const string SupplyUse = "supply.use";
    public const string Steer = "steer";
    public const string Retreat = "retreat";
    public const string Extract = "extract";
    public const string ObjectPrefix = "object.";

    static readonly IReadOnlySet<string> Fixed = new HashSet<string>(StringComparer.Ordinal)
        { Enter, Route, PackMove, PackDrop, Talk, SupplyUse, Steer, Retreat, Extract };

    /// <summary>All nine fixed kinds — `object.{verb}` is a pattern, not a member, so it is not in
    /// this list; use <see cref="IsKnown"/> to validate a candidate kind string.</summary>
    public static IReadOnlyList<string> FixedKinds { get; } = Fixed.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public static bool IsKnown(string kind) =>
        Fixed.Contains(kind) || (kind.StartsWith(ObjectPrefix, StringComparison.Ordinal) && kind.Length > ObjectPrefix.Length);
}

/// <summary>
/// One entry in the delve-level log — `{seq, kind, partyIndex, tick?, payload}` exactly
/// (spec-delve-battle-profile.md §4a). Ordered by `Seq` only; the delve has no clock of its own
/// (`Tick` is the OPTIONAL battle-tick a decision happened at, for kinds that occur mid-fight — e.g.
/// `retreat` — never the delve's own clock, which does not exist). `Payload` is kind-specific and
/// genuinely untyped here: `route`'s payload (a door/room id) and `talk`'s (`payload.surface`, per
/// the spec's own example) belong to their owning modules, none of which are built yet — this record
/// is the log's shape, not a union of every future payload.
/// </summary>
public sealed record DelveDecision(int Seq, string Kind, int? PartyIndex, long? Tick, object? Payload)
{
    /// <summary>Validates <paramref name="kind"/> against <see cref="DelveDecisionKinds.IsKnown"/>
    /// before constructing — the log has no reader that could recover from an unknown kind landing
    /// in `rpg_delves.decisions_json`, so refuse at the one point that can still throw usefully.</summary>
    public static DelveDecision Create(int seq, string kind, int? partyIndex = null, long? tick = null, object? payload = null)
    {
        if (!DelveDecisionKinds.IsKnown(kind))
            throw new ArgumentException($"Unknown delve decision kind '{kind}'.", nameof(kind));
        return new DelveDecision(seq, kind, partyIndex, tick, payload);
    }
}
