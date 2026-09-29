namespace FusionRpg.Core.Scope;

/// <summary>Mirrors `effect-atom/definitions.md` §9's own four-state runtime-support matrix.</summary>
public enum ScopeSupportLevel
{
    Full = 0,
    Partial,
    None,
}

/// <summary>
/// How a legal `(kind, where, who, host)` combination actually reaches its population. Not derivable
/// from the other three fields — G8 is the proof: the same kind, same where, same who, resolves to a
/// different shape depending only on `host` (spec-scope-model.md Assumption 2).
/// </summary>
public enum ScopeDeliveryShape
{
    /// <summary>One `EffectGrant` per currently-qualifying entity — the normal case.</summary>
    PerEntityGrant,

    /// <summary>
    /// A single value read by the whole side, never granted per entity — the G8 shape. Live-PvZ-only
    /// today; nothing forces it to stay that way, but nothing currently needs the Sim host to have it.
    /// </summary>
    SideWideConstant,
}

public readonly record struct ScopeSupport(ScopeSupportLevel Level, ScopeDeliveryShape Shape);

/// <summary>
/// The lookup key. `Host` is null for <see cref="WhereScope.WorldMap"/> (one host, no split) and
/// required for <see cref="WhereScope.Battlefield"/>. `Channel` is null for kinds with no
/// channel-level distinction, and required for `stat.modify` specifically — G8 is about the
/// <c>defense</c> channel, not the kind as a whole, so collapsing this to kind-level granularity would
/// silently misrepresent the one case this table exists to get right.
/// </summary>
public readonly record struct ScopeCompatibilityKey(
    string AtomKindId, WhereScope Where, WhoKind Who, ScopeHost? Host, string? Channel = null);

/// <summary>
/// The compatibility contract (spec-scope-model.md Objective). A maintained table, audited against
/// code — not a general rule engine, not inferred from kind metadata (Assumption 3). Deliberately
/// small: only entries proven against real, verified behaviour are listed. Everything else rejects
/// `ScopeUnsupported` rather than guessing — an unlisted combination is not assumed safe.
/// </summary>
public static class ScopeCompatibility
{
    static readonly Dictionary<ScopeCompatibilityKey, ScopeSupport> Table = new()
    {
        // G8 (effect-atom/definitions.md): `stat.modify` on `defense` reads one side-wide cached
        // value on the live-PvZ TakeDamage-prefix path — proven by reading the injector's own
        // stat-write source and Harmony hooks this session, not assumed from the doc alone.
        [new ScopeCompatibilityKey("stat.modify", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Live, "defense")]
            = new(ScopeSupportLevel.Full, ScopeDeliveryShape.SideWideConstant),

        // The identical kind, same channel, on the SIM host: BattleEngine computes damage entirely in
        // C# and has no equivalent side-wide cache, so it takes the normal per-entity-grant shape —
        // the same kind, two hosts, two different answers, which is the whole point of this table
        // carrying a `host` dimension at all.
        [new ScopeCompatibilityKey("stat.modify", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Sim, "defense")]
            = new(ScopeSupportLevel.Full, ScopeDeliveryShape.PerEntityGrant),

        // The normal, unrestricted case — the shape most future content actually uses, complementing
        // G8's one named exception. `resource.delta` needs no channel-level distinction (unlike
        // stat.modify), so `Channel` stays null; supported per-entity on both hosts identically.
        [new ScopeCompatibilityKey("resource.delta", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Sim)]
            = new(ScopeSupportLevel.Full, ScopeDeliveryShape.PerEntityGrant),
        [new ScopeCompatibilityKey("resource.delta", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Live)]
            = new(ScopeSupportLevel.Full, ScopeDeliveryShape.PerEntityGrant),

        // `stat.derived` — the aura kind (decisions.md "Derived-write lawn executor", 2026-08-30).
        // Per-entity on BOTH hosts, and `Channel` stays null on purpose: G8's channel-level split
        // exists because `stat.modify` on `defense` hits live PvZ's ONE side-wide TakeDamage-prefix
        // cache. `stat.derived` never touches that path — it composes per actor through
        // `ActorHub.ResolveDerived`, which is exactly why definitions.md §6 names
        // `stat.derived` on `combat.defense.*` as the per-actor mitigation answer to G8's restriction.
        // So there is no channel for which this kind's shape differs, and inventing a channel
        // dimension it does not have would misrepresent it as precisely as collapsing G8's would.
        [new ScopeCompatibilityKey("stat.derived", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Live)]
            = new(ScopeSupportLevel.Full, ScopeDeliveryShape.PerEntityGrant),
        // The Sim host (BattleEngine) already had a consumer from E12 (`TraitAtomSource` at squad
        // build); this row records the shape that consumer already implements rather than granting a
        // new capability.
        [new ScopeCompatibilityKey("stat.derived", WhereScope.Battlefield, WhoKind.Relation, ScopeHost.Sim)]
            = new(ScopeSupportLevel.Full, ScopeDeliveryShape.PerEntityGrant),
    };

    /// <summary>Throws <see cref="ScopeUnsupportedException"/> for any combination not in the table.</summary>
    public static ScopeSupport Resolve(string atomKindId, WhereScope where, WhoKind who, ScopeHost? host, string? channel = null)
    {
        var key = new ScopeCompatibilityKey(atomKindId, where, who, host, channel);
        if (Table.TryGetValue(key, out var support))
            return support;

        throw new ScopeUnsupportedException(atomKindId, where, who, host);
    }

    public static bool TryResolve(string atomKindId, WhereScope where, WhoKind who, ScopeHost? host, string? channel, out ScopeSupport support) =>
        Table.TryGetValue(new ScopeCompatibilityKey(atomKindId, where, who, host, channel), out support);
}
