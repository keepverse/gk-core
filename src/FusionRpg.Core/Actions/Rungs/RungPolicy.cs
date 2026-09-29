namespace FusionRpg.Core.Actions.Rungs;

/// <summary>
/// The loaded rung ladder, host-configured at startup exactly like every other tunable surface
/// (`ShieldPolicy`, `CombatPolicy`, ...). Two readers — `A11` and `A3` — both resolve through this,
/// which is the whole argument for the table existing once.
/// </summary>
public static class RungPolicy
{
    static RungTable? _table;

    public static void Configure(RungTable table) =>
        _table = table ?? throw new ArgumentNullException(nameof(table));

    public static RungTable Table => _table ?? throw new InvalidOperationException(
        "RungPolicy.Configure(...) has not run. Every rung read comes from " +
        "data/tuning/action-rungs.v{n}.json (spec-rung-table.md) — there is no built-in default.");
}
