using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;

namespace FusionRpg.Core.Actions;

/// <summary>
/// Compile output → shipped resolver → range gate (spec-targeting.md §5). The shipped
/// <see cref="TargetResolver"/> is called, never modified.
///
/// <para><b>The range gate runs BEFORE the resolver's random pick, not after.</b> Narrowing the
/// snapshot first means an out-of-range candidate never consumes a draw from the RNG stream at all —
/// filtering the already-picked result would shift every subsequent draw and desync a replay
/// (spec-targeting.md §6c). With no board (the caster itself is not in the snapshot), every range
/// check passes — not an error, not empty; the pure Core battle kernel never constructs a non-empty
/// <see cref="BoardSnapshot"/> until `A10` lands, so this is the correct proxy for "no board yet".</para>
/// </summary>
public static class ActionTargetResolver
{
    /// <summary>
    /// The seeded stream new `RolledTarget` actions draw from (spec-targeting.md §6a). The battle's
    /// other named streams are `initiative`, `crit`, `essence`, `status`, `proc` — none of them is
    /// `target`, so this action would otherwise either desync an existing stream or draw from an
    /// unnamed, non-replayable source.
    /// </summary>
    public const string RngStreamName = "target";

    public static ICombatRng DeriveRng(ulong runSeed) =>
        new SeededRngCombatAdapter(SeededRng.DeriveStream(runSeed, RngStreamName));

    public static IReadOnlyList<string> Resolve(
        CompiledTargetSpec compiled,
        CasterSide casterSide,
        string casterPtr,
        int minRange,
        int maxRange,
        BoardSnapshot snapshot,
        EffectEventDto? ev,
        CombatPolicy? policy,
        ICombatRng? rng)
    {
        if (compiled.IsSelf) return new[] { casterPtr };

        var wire = compiled.PerSide[(int)casterSide];
        var casterEntity = snapshot.FindPtr(casterPtr);

        if (casterEntity is null)
            return TargetResolver.Resolve(wire, snapshot, ev, policy, rng);

        var casterPos = new GridPos(casterEntity.Row, casterEntity.Col);

        var inRange = new List<BoardEntitySnap>(snapshot.Entities.Count);
        foreach (var e in snapshot.Entities)
        {
            if (ReferenceEquals(e, casterEntity) || e.Ptr == casterPtr) { inRange.Add(e); continue; }
            if (GridDistance.InRange(casterPos, new GridPos(e.Row, e.Col), minRange, maxRange))
                inRange.Add(e);
        }

        var narrowed = new BoardSnapshot(inRange);
        return TargetResolver.Resolve(wire, narrowed, ev, policy, rng);
    }
}
