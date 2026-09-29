namespace FusionRpg.Core.Progression;

/// <summary>
/// `empire-level` (module 13, `empire-progression` Wave D): the **closed vocabulary** of what reaching
/// an empire level pays out. One member today — ruling R18's earned free empire respec — and this enum
/// is the reserved world-stage hook: a later reward is a new member plus its applier, added by the
/// program that specs it, never a placeholder invented here.
///
/// <para>Growth is a reviewed change: this is a code-owned vocabulary a human edits, which is why
/// <c>EmpireLevelGrantsTests</c> pins the member count and says so (`validation-ssot.md` — a pinned
/// literal is legal for a closed vocabulary, never for a population). A grant that ever reaches a
/// magnitude does so through an existing actor layer and its own GG-49 SourceId, **never** through the
/// empire level itself: a level that fed `Theta` or `P(Theta)` would be a second power ladder
/// (`decisions.md`, *Empire level (2026-09-18)*; `ssot-power-scale.md` &sect;11).</para>
/// </summary>
public enum EmpireLevelGrantKind
{
    /// <summary>One grant of <see cref="EmpireLevelTuning.FreeRespecsPerEmpireLevel"/> earned free
    /// empire respecs, held by the empire `(SaveId, EmpireId)` and spent only by a species (empire)
    /// respec — never a unique creature's or the commander pool's (rulings R18/R19). Applied by
    /// `respec-free-counter`'s `rpg_empire_free_respec_ledger`, keyed `L{levelAfter}` inside the same
    /// transaction as the empire level change.</summary>
    FreeEmpireRespec,
}

/// <summary>One payout of one <see cref="EmpireLevelGrantKind"/>. `Amount` is `long` (a persisted
/// magnitude, docs/architecture/numeric-types.md's numeric rule) and is always positive: a grant is never taken back.</summary>
public readonly record struct EmpireLevelGrant(EmpireLevelGrantKind Kind, long Amount);

/// <summary>
/// The small pure record <see cref="EmpireLevelGrants.For"/> reads, so the grant rule is a function of
/// its own inputs rather than of a global hub (the shape `SpecimenRespecTuning` already uses).
/// </summary>
public sealed record EmpireLevelTuning(long FreeRespecsPerEmpireLevel);

/// <summary>
/// The host wiring for <see cref="EmpireLevelTuning"/> (`respec-free-counter` EP4.4). Follows this
/// repo's every-tuning-file-gets-a-hub convention: `Program.cs` builds the record from
/// `species-build.v{n}.json`'s `freeRespecsPerEmpireLevel` at start, and the Data credit reads it
/// there. It is deliberately NOT a store property: the tuning is parsed before any `RpgStore` exists
/// (the store is DI-constructed later), so a store slot could only be filled by a second read of the
/// same file — one number, read once, wired once.
///
/// <para><b>Unconfigured means no grants, and that is stated rather than hidden.</b> A caller that
/// does not configure it (a test bootstrap, a tool, a host that has not wired the key) gets an empty
/// grant list — the same behaviour the credit had before this key existed. It is not a silent
/// default for a MISSING KEY: the loader refuses a v6+ document without
/// `freeRespecsPerEmpireLevel` outright (tunables-ssot.md T5), so the only way to reach "no grants"
/// is a host that never asked.</para>
/// </summary>
public static class EmpireLevelTuningHub
{
    static EmpireLevelTuning? _tuning;

    public static void Configure(EmpireLevelTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static bool IsConfigured => _tuning is not null;

    /// <summary>The wired value, or the empty one — never a throw: an empire level is granted inside a
    /// transaction, and a host wiring gap must not roll back a player's XP.</summary>
    public static EmpireLevelTuning Tuning => _tuning ?? new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 0);
}

/// <summary>
/// What reaching an empire level grants (`spec-empire-level.md` §"Level-up grants"). Pure, no I/O, no
/// clock: the same `(levelAfter, tuning)` always yields the same list, which is what makes "each empire
/// level pays exactly once" provable from the stored row instead of from a ledger that compaction can
/// trim.
///
/// <para><b>No curve lives here.</b> The level's cost is <see cref="RpgXpCurve.XpToNext"/>'s shared
/// arithmetic ladder (§10.1 row 6) — a grant function may *read* a level, never derive one. A reflection
/// test asserts this class declares no level-shaped curve method, mirroring `guard-power.py`'s G2
/// heuristic rather than trusting a comment.</para>
/// </summary>
public static class EmpireLevelGrants
{
    /// <summary>
    /// What reaching <paramref name="levelAfter"/> pays. A non-positive
    /// <see cref="EmpireLevelTuning.FreeRespecsPerEmpireLevel"/> grants nothing — the working value is
    /// published, and a zero must produce an empty list rather than a zero-amount grant that would
    /// write a meaningless ledger row.
    ///
    /// <para><paramref name="levelAfter"/> is part of the contract rather than computed from: today
    /// every level pays the same, and a later reward may not (a milestone level is the obvious case).
    /// Reading it now keeps that a data change instead of a signature change at the one call site.</para>
    /// </summary>
    public static IReadOnlyList<EmpireLevelGrant> For(long levelAfter, EmpireLevelTuning tuning)
    {
        _ = levelAfter;
        if (tuning.FreeRespecsPerEmpireLevel <= 0) return Array.Empty<EmpireLevelGrant>();
        return new[]
        {
            new EmpireLevelGrant(EmpireLevelGrantKind.FreeEmpireRespec, tuning.FreeRespecsPerEmpireLevel),
        };
    }
}
