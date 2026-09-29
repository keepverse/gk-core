namespace FusionRpg.Core.Combat.Shield;

/// <summary>Observability event kinds on the string envelope stream (spec §2.6).</summary>
public static class ShieldEventKinds
{
    public const string Granted = "shield.granted";
    /// <summary>Aggregated per (owner, shieldId) per flush window; noise-suppressed on the live feed.</summary>
    public const string Absorbed = "shield.absorbed";
    public const string Broken = "shield.broken";
    public const string Expired = "shield.expired";
}

/// <summary>
/// One pending shield event. Absorbed records aggregate in place (Amount/HitCount sum);
/// a break flushes its shield's open aggregate first so ordering survives for VFX.
/// </summary>
public struct ShieldEventRec
{
    public string Kind;
    public string OwnerKey;
    public string ShieldId;
    public string Element;     // element id or "none"
    public long Amount;        // absorbed: total spent this window; others: 0
    public long HitCount;      // absorbed: merged hit count
    public long Hp;            // pool after the event (broken/expired: 0)
    public long MaxHp;
}
