using FusionRpg.Contracts;

namespace FusionRpg.Core.Notify;

/// <summary>Fluent builder for a draft's `Args` list (notify-service spec §1's own code style) —
/// implicitly converts to `IReadOnlyList&lt;NotifyArg&gt;` so `Args.Count(...).Count(...)` can be
/// assigned directly to `NotificationDraft.Args` with no terminal `.Build()` call. First used by
/// `CacheNotificationSource` (cache-notify-source, NS6.2); any later source with typed args reuses
/// this instead of hand-building a `List&lt;NotifyArg&gt;`.</summary>
public sealed class NotifyArgsBuilder
{
    readonly List<NotifyArg> _args = new();
    internal NotifyArgsBuilder() { }

    public NotifyArgsBuilder Count(string name, long value)
    {
        _args.Add(new NotifyArg(name, NotifyArgKind.Count, value));
        return this;
    }

    public NotifyArgsBuilder WorldTurnArg(string name, int turn)
    {
        _args.Add(new NotifyArg(name, NotifyArgKind.WorldTurn, turn));
        return this;
    }

    /// <summary>The wire's `{refKind, id}` shape (`notify-format` `kit.ts`'s own `ref()` primitive)
    /// — an anonymous type with pre-lowercased field names, NOT a named PascalCase record:
    /// `NotificationPublisher.SerializeArgs` calls `JsonSerializer.SerializeToElement(a.Value)`
    /// with no camelCase naming policy, so a record's `RefKind`/`Id` properties would serialize
    /// PascalCase and never match the web's lowercase `refKind`/`id` read. An anonymous type's
    /// property names serialize verbatim (no naming transform either way), so writing them
    /// lowercase here is what makes the wire shape correct — `refKind`'s own enum value still
    /// renders lowercase through `NotifyRefKind`'s `[JsonConverter]` attribute, which applies
    /// regardless of the caller's options.</summary>
    public NotifyArgsBuilder Ref(string name, NotifyRefKind refKind, string id)
    {
        _args.Add(new NotifyArg(name, NotifyArgKind.Ref, new { refKind, id }));
        return this;
    }

    public NotifyArgsBuilder DomainToken(string name, object value)
    {
        _args.Add(new NotifyArg(name, NotifyArgKind.DomainToken, value));
        return this;
    }

    /// <summary>Terminal call — kept explicit (unlike the spec's own illustrative pseudocode)
    /// because C# refuses a user-defined implicit conversion to an interface type, and
    /// `NotificationDraft.Args` is typed `IReadOnlyList&lt;NotifyArg&gt;`.</summary>
    public IReadOnlyList<NotifyArg> Build() => _args;
}

/// <summary>Entry point matching notify-service spec §1's own code style (`Args.Count(...)`).</summary>
public static class Args
{
    public static NotifyArgsBuilder Count(string name, long value) => new NotifyArgsBuilder().Count(name, value);
    public static NotifyArgsBuilder WorldTurnArg(string name, int turn) => new NotifyArgsBuilder().WorldTurnArg(name, turn);
    public static NotifyArgsBuilder Ref(string name, NotifyRefKind refKind, string id) => new NotifyArgsBuilder().Ref(name, refKind, id);
    public static NotifyArgsBuilder DomainToken(string name, object value) => new NotifyArgsBuilder().DomainToken(name, value);
}
