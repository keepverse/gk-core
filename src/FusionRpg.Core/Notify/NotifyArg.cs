using FusionRpg.Contracts;

namespace FusionRpg.Core.Notify;

/// <summary>notify-service spec §1 - Core's own arg shape, carrying a plain .NET value rather than
/// the wire's `JsonElement` (`NotifyArgDto`, Contracts). The publisher serializes this to the wire
/// shape at the edge (map "Core carries no transport type").</summary>
public sealed record NotifyArg(string Name, NotifyArgKind Kind, object Value);
