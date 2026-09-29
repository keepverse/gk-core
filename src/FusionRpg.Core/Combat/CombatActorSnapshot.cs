using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat;

/// <summary>Resolved derived stats + element types for one actor at overlay combat time.</summary>
public sealed record CombatActorSnapshot(
    ActorDerivedSnapshot Derived,
    ActorElementTypes ElementTypes)
{
    public static CombatActorSnapshot AttackerLess() =>
        new(ActorDerivedSnapshot.AttackerLess(), ActorElementTypes.Neutral);
}

public delegate CombatActorSnapshot CombatActorResolve(string? ptr, bool attackerLess);

/// <summary>Ptr-keyed element type lookup for offline harness / scenario runner.</summary>
public sealed class ActorElementLookup
{
    readonly Dictionary<string, ActorElementTypes> _byPtr = new(StringComparer.Ordinal);

    public void Pin(string? ptr, ActorElementTypes types)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key) || types == null) return;
        _byPtr[key] = types;
    }

    public void Clear() => _byPtr.Clear();

    public ActorElementTypes Resolve(string? ptr)
    {
        var key = CombatPtr.Normalize(ptr);
        if (!string.IsNullOrEmpty(key) && _byPtr.TryGetValue(key, out var types))
            return types;
        return ActorElementTypes.Neutral;
    }
}
