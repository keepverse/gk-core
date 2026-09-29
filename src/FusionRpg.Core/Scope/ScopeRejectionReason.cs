namespace FusionRpg.Core.Scope;

/// <summary>
/// A `(kind, where, who, host)` combination this table has no entry for. Reuses the atom layer's own
/// closed rejection-code list (`effect-atom/definitions.md` §10) rather than inventing a parallel
/// error surface — `ScopeUnsupported` already exists there for exactly this shape of refusal.
/// </summary>
public sealed class ScopeUnsupportedException : Exception
{
    public ScopeUnsupportedException(string atomKindId, WhereScope where, WhoKind who, ScopeHost? host)
        : base(FormatMessage(atomKindId, where, who, host))
    {
        AtomKindId = atomKindId;
        Where = where;
        Who = who;
        Host = host;
    }

    public string AtomKindId { get; }
    public WhereScope Where { get; }
    public WhoKind Who { get; }
    public ScopeHost? Host { get; }

    static string FormatMessage(string atomKindId, WhereScope where, WhoKind who, ScopeHost? host) =>
        $"ScopeUnsupported: {atomKindId} at ({WhereScopes.Name(where)}, {WhoKinds.Name(who)}" +
        (host.HasValue ? $", {ScopeHosts.Name(host.Value)}" : "") + ") has no compatibility entry.";
}
