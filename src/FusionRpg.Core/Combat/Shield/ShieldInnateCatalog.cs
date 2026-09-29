using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Shield;

/// <summary>Innate shield content row — validated at registration (spec §2.1). Priority has no
/// default: ShieldPolicy.PriorityInnate is config-loaded now (tunables-ssot.md T1), and a default
/// parameter value must be a compile-time constant — callers pass it explicitly instead.</summary>
public sealed record ShieldInnateDef(
    long BaseHp,
    ElementTypeId? Element,
    int Priority);

/// <summary>
/// Content rows for innate shields, keyed by (side, typeId) — code-first in-memory registry
/// like the status catalog; empty by default so shipping this system changes nothing until
/// content registers rows. Innate shields auto-grant once per actor registration via the
/// queue-then-first-tick barrier (spec §2.6).
/// </summary>
public static class ShieldInnateCatalog
{
    static readonly Dictionary<(string Side, int TypeId), ShieldInnateDef> Rows = new();

    public static bool IsEmpty => Rows.Count == 0;

    public static void Register(string side, int typeId, ShieldInnateDef def)
    {
        if (string.IsNullOrWhiteSpace(side)) throw new ArgumentException("side required", nameof(side));
        if (def == null) throw new ArgumentNullException(nameof(def));
        if (def.BaseHp <= 0) throw new ArgumentOutOfRangeException(nameof(def), "innate BaseHp must be > 0");
        Rows[(side.ToLowerInvariant(), typeId)] = def;
    }

    public static bool TryGet(string side, int typeId, out ShieldInnateDef def)
    {
        if (Rows.TryGetValue((side.ToLowerInvariant(), typeId), out var found))
        {
            def = found;
            return true;
        }

        def = null!;
        return false;
    }

    /// <summary>Test hook.</summary>
    public static void Clear() => Rows.Clear();
}
