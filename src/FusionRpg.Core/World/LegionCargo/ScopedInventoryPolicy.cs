namespace FusionRpg.Core.World.LegionCargo;

/// <summary>
/// Every tunable legion-cargo constant, in one place — the <c>LoamPolicy</c> precedent.
/// Values live in <c>data/tuning/scoped-inventory.v{n}.json</c> (tunables-ssot.md T1);
/// <see cref="Configure"/> must run before any rule below is read: there is no built-in default.
/// </summary>
public static class ScopedInventoryPolicy
{
    static ScopedInventoryTuning? _tuning;

    /// <summary>Host-only (Server startup, or a test's inline construction).</summary>
    public static void Configure(ScopedInventoryTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static ScopedInventoryTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ScopedInventoryPolicy.Configure(...) has not run. Every legion-cargo rule reads " +
        "data/tuning/scoped-inventory.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>
    /// What one member contributes to a legion's cargo weight capacity — a <c>long</c> magnitude
    /// (AGENTS.md: <c>long</c> for any magnitude). Flat across roles by owner decision
    /// (spec-legion-cargo.md Locked anchors): every member counts, not just <c>Bearer</c>s.
    /// </summary>
    public static long CargoWeightPerUnit => Tuning.CargoWeightPerUnit;

    /// <summary>
    /// What one member contributes to a legion's cargo slot capacity — an <c>int</c>: a legion's
    /// realistic member count is small, a structural bound commented as such, never a
    /// progression-scaled magnitude (spec-legion-cargo.md §Numeric types).
    /// </summary>
    public static int CargoSlotsPerUnit => Tuning.CargoSlotsPerUnit;

    /// <summary>
    /// A legion's cargo weight capacity for a live member count — computed fresh from the count,
    /// never stored (spec-legion-cargo.md §Design 1). Widen-before-multiply, checked: overflow
    /// throws, never wraps.
    /// </summary>
    public static long WeightCapacityFor(int memberCount) =>
        checked((long)memberCount * CargoWeightPerUnit);

    /// <summary>
    /// A legion's cargo slot capacity for a live member count — same fresh-computation discipline
    /// as <see cref="WeightCapacityFor"/>. Checked: overflow throws, never wraps.
    /// </summary>
    public static int SlotCapacityFor(int memberCount) =>
        checked(memberCount * CargoSlotsPerUnit);
}
