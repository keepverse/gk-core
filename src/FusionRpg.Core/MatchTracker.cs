namespace FusionRpg.Core;

public sealed class MatchTracker
{
    public HashSet<string> Applied { get; } = new(StringComparer.Ordinal);
    public HashSet<string> DeadZombies { get; } = new(StringComparer.Ordinal);

    public void Clear()
    {
        Applied.Clear();
        DeadZombies.Clear();
    }

    public bool TryApply(string ptr) => Applied.Add(ptr);

    public bool TryNoteZombieDead(string ptr) => DeadZombies.Add(ptr);
}
