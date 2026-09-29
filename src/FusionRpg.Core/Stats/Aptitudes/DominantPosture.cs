namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>class-system-todo.md P1.3 — a READ over an allocation, never a stored field: a posture is
/// never persisted, never an actor property, and no resolve path (contest or magnitude) may reference
/// it — the twelve aptitudes are what channels actually read; posture is display/grouping only
/// (Aptitude.cs's own doc comment on <see cref="Posture"/>). Ties resolve to <c>null</c> ("None"),
/// never to an arbitrary tie-break, because inventing a winner out of a tie would assert a build
/// identity nobody chose.</summary>
public static class DominantPosture
{
    public static Posture? Of(AptitudeAllocation allocation)
    {
        var byPosture = Enum.GetValues<Posture>()
            .Select(p => (Posture: p, Points: AptitudeCatalog.InPosture(p).Sum(a => allocation.Total(a.Id))))
            .ToList();

        var max = byPosture.Max(x => x.Points);
        if (max == 0) return null; // empty allocation: no posture dominates nothing
        var leaders = byPosture.Where(x => x.Points == max).ToList();
        return leaders.Count == 1 ? leaders[0].Posture : null;
    }
}
