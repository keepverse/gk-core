namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>class-system-map.md §2aa — the four scopes an actor's allocation is the SUM of, commander
/// smallest through unique largest (a commander allocation replicates across the whole roster, so a
/// dominant one is the worst case). The relative BUDGET each scope gets is `point-economy`'s call
/// (P6.1); this type only needs the four buckets to sum into, in the order the decision states them.
/// Append-only, like every other ordinal roster in this codebase — never reorder.</summary>
public enum AllocationScope { Commander, CreatureType, Aspect, UniqueCreature }

/// <summary>One JSON-canonical allocation row. Sorted by (scope, aptitude) on the way out so the
/// serialized bytes are stable regardless of insertion order (golden hashes depend on it).</summary>
public sealed record AllocationEntry(AllocationScope Scope, string AptitudeId, long Points);

/// <summary>
/// An actor's aptitude allocation — immutable, `long`-valued points per (scope, aptitude id).
///
/// <para><b>Rewritten for species-progression step 6.1</b> (spec-species-layer-delivery.md, R2/R16):
/// the class comment used to read "Scopes sum before share, never the reverse", describing how
/// <see cref="AptitudeResolver.Resolve"/> USED to read this allocation — through <see cref="Share"/>,
/// which divides one aptitude's SUM across all four scopes by the GRAND total across all twelve. That
/// merge-then-share read is retired: since step 6.1 the resolver reads <see cref="ShareWithinScope"/>
/// instead, resolving each scope PRESENT alone. <b>The per-layer contract this type now carries is:
/// one scope's own contribution is unchanged by adding points to another scope</b> — the opposite of
/// what "scopes sum before share" implied for a real resolve.</para>
///
/// <para><see cref="Total"/>/<see cref="GrandTotal"/>/<see cref="Share"/> are UNCHANGED by step 6.1 —
/// they still describe the allocation OBJECT's own sum-then-divide arithmetic (point-budget
/// accounting still reads <see cref="TotalForScope"/> against each scope's own rate, an orthogonal use
/// this rewrite does not touch), and remain correct, tested facts about this type. They are simply no
/// longer what a real channel resolve reads — a per-scope share, once wrongly assumed to distort a
/// large scope's broad spread, is exactly what the resolver now takes on purpose (R2: "each species
/// container resolves only its own points; the commander is its own layer").</para>
///
/// <para><b>Empty means all-zero shares, never <c>1/12</c> each.</b> An actor with no allocation has
/// no build; treating "nothing chosen" as "chose evenly" would silently invent a default nobody set
/// (tunables-ssot.md's own rule against exactly that, applied here to a runtime value rather than a
/// config key).</para>
///
/// <para><b>No aptitude cap and no respec cap</b> (PS-8, AGENTS.md) — nothing here bounds
/// <see cref="Total"/> or <see cref="GrandTotal"/>; overflow throws via checked arithmetic rather than
/// clamping, so a build that would silently overflow fails loudly instead of quietly capping.</para>
/// </summary>
public sealed class AptitudeAllocation
{
    readonly IReadOnlyDictionary<(AllocationScope Scope, string AptitudeId), long> _points;

    public static readonly AptitudeAllocation Empty = new(new Dictionary<(AllocationScope, string), long>());

    AptitudeAllocation(IReadOnlyDictionary<(AllocationScope, string), long> points) => _points = points;

    /// <summary>
    /// JSON round-trip for web-match log setupJson re-resolve: a logged match carries the squad's
    /// <c>BattleHubInputs</c>, and the boot sweep deserializes them back. Without this, any row
    /// with a non-null aptitude crashed the sweep with <c>NotSupportedException</c> instead of
    /// healing. Validation matches <see cref="Single"/>; a missing/empty entry list reads as
    /// <see cref="Empty"/> so rows written before this existed (serialized as <c>{}</c>) still read.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public AptitudeAllocation(IReadOnlyList<AllocationEntry>? entries)
    {
        var merged = new Dictionary<(AllocationScope, string), long>();
        if (entries is not null)
            foreach (var e in entries)
            {
                if (!AptitudeCatalog.IsAptitudeId(e.AptitudeId))
                    throw new ArgumentException($"unknown aptitude id '{e.AptitudeId}'", nameof(entries));
                if (e.Points < 0)
                    throw new ArgumentOutOfRangeException(nameof(entries), "allocation points cannot be negative");
                if (e.Points == 0)
                    continue;
                var key = (e.Scope, e.AptitudeId);
                checked { merged[key] = merged.GetValueOrDefault(key) + e.Points; }
            }
        _points = merged;
    }

    /// <summary>Canonical serialization form, sorted by (scope, aptitude id) for stable bytes.</summary>
    public IReadOnlyList<AllocationEntry> Entries => _points
        .OrderBy(kv => kv.Key.Item1)
        .ThenBy(kv => kv.Key.Item2, StringComparer.Ordinal)
        .Select(kv => new AllocationEntry(kv.Key.Item1, kv.Key.Item2, kv.Value))
        .ToList();

    public static AptitudeAllocation Single(AllocationScope scope, string aptitudeId, long points)
    {
        if (!AptitudeCatalog.IsAptitudeId(aptitudeId))
            throw new ArgumentException($"unknown aptitude id '{aptitudeId}'", nameof(aptitudeId));
        if (points < 0)
            throw new ArgumentOutOfRangeException(nameof(points), "allocation points cannot be negative");
        return points == 0
            ? Empty
            : new AptitudeAllocation(new Dictionary<(AllocationScope, string), long> { [(scope, aptitudeId)] = points });
    }

    public long PointsAt(AllocationScope scope, string aptitudeId) => _points.GetValueOrDefault((scope, aptitudeId));

    /// <summary>The sum across all four scopes for one aptitude — the quantity <see cref="Share"/>
    /// is taken over, per the decision that scopes sum before share.</summary>
    public long Total(string aptitudeId)
    {
        long sum = 0;
        foreach (var scope in AllScopes)
            checked { sum += PointsAt(scope, aptitudeId); }
        return sum;
    }

    public long GrandTotal()
    {
        long sum = 0;
        foreach (var apt in AptitudeCatalog.All)
            checked { sum += Total(apt.Id); }
        return sum;
    }

    /// <summary>The orthogonal sum to <see cref="Total"/>: one scope, across all twelve aptitudes —
    /// what `point-economy` (P6.1) checks against that scope's own budget. "Each scope draws from its
    /// own budget" (spec-point-economy.md §7 test 2) starts here: this is scope-local spend, never
    /// combined with another scope's before the comparison.</summary>
    public long TotalForScope(AllocationScope scope)
    {
        long sum = 0;
        foreach (var apt in AptitudeCatalog.All)
            checked { sum += PointsAt(scope, apt.Id); }
        return sum;
    }

    /// <summary>Bounded [0,1] ratio — exempt from the long-magnitude rule (docs/architecture/numeric-types.md: "bounded ratios
    /// are exempt"). Empty allocation reads 0 for every aptitude, never a uniform 1/12.</summary>
    public double Share(string aptitudeId)
    {
        var grand = GrandTotal();
        return grand == 0 ? 0.0 : (double)Total(aptitudeId) / grand;
    }

    public IReadOnlyDictionary<string, double> Shares() =>
        AptitudeCatalog.All.ToDictionary(a => a.Id, a => Share(a.Id), StringComparer.Ordinal);

    /// <summary>
    /// `species-progression` SP1.6 (`species-layer-delivery` step 6.1's own prerequisite) — the
    /// per-scope counterpart to <see cref="Share"/>: divides one aptitude's points by that SAME
    /// scope's own total (<see cref="TotalForScope"/>), never the grand total across all four scopes.
    /// This is the read the 2026-09-18 amendment to decisions.md "Class system" (R2/R16/R21) names:
    /// once step 6.1 splits the layers, each scope resolves ALONE, its share taken over that scope's
    /// own total. An empty scope reads 0 for every aptitude, never a uniform 1/12 (the same "nothing
    /// chosen is not evenly chosen" rule <see cref="Share"/> already follows). For an allocation that
    /// holds points in exactly one scope, this equals <see cref="Share"/> by construction: that scope's
    /// total IS the grand total, and that scope's points ARE each aptitude's total. Bounded [0,1]
    /// ratio, exempt from the long-magnitude rule (docs/architecture/numeric-types.md: "bounded ratios are exempt").
    /// </summary>
    public double ShareWithinScope(AllocationScope scope, string aptitudeId)
    {
        var scopeTotal = TotalForScope(scope);
        return scopeTotal == 0 ? 0.0 : (double)PointsAt(scope, aptitudeId) / scopeTotal;
    }

    /// <summary>Commutative and associative — <c>a + b == b + a</c> and grouping never matters, so
    /// merging allocations from independent scopes can happen in any order.</summary>
    public static AptitudeAllocation operator +(AptitudeAllocation a, AptitudeAllocation b)
    {
        var merged = new Dictionary<(AllocationScope, string), long>(a._points);
        foreach (var (key, points) in b._points)
        {
            var existing = merged.GetValueOrDefault(key);
            checked { merged[key] = existing + points; }
        }
        return merged.Count == 0 ? Empty : new AptitudeAllocation(merged);
    }

    static readonly AllocationScope[] AllScopes = Enum.GetValues<AllocationScope>();
}
