namespace FusionRpg.Core.World.Turn;

/// <summary>
/// What decides when a turn resolves — and therefore what kind of game this is.
///
/// Wave 1 ships exactly one implementation, <see cref="WaitForAllCommitted"/>: the turn lasts as
/// long as thinking takes. The interface exists so <c>TurnEngine.Step</c> can never learn *why* the
/// barrier released; a policy that also fires on a deadline would make this real-time, and one that
/// fires on a wall-clock period would make it idle. No such policy is built here.
/// </summary>
public interface ITurnBarrier
{
    bool ShouldFire(IReadOnlyCollection<string> commanders, IReadOnlyCollection<string> committed);
}

/// <summary>Turn-based: every commander must have committed. No deadline, ever.</summary>
public sealed class WaitForAllCommitted : ITurnBarrier
{
    public bool ShouldFire(IReadOnlyCollection<string> commanders, IReadOnlyCollection<string> committed)
    {
        if (commanders.Count == 0) return false;

        // Counted as a set of *known* commanders: committing twice is not two commanders, and a
        // stranger's commit cannot release someone else's turn.
        var known = new HashSet<string>(commanders, StringComparer.Ordinal);
        var ready = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in committed)
            if (known.Contains(id))
                ready.Add(id);

        return ready.Count == known.Count;
    }
}
