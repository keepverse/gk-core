using FusionRpg.Contracts;
using FusionRpg.Core.Combat;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// Turns one board event into the flat facts a compiled predicate reads.
///
/// <para>The reduction happens <b>once per event</b>, not once per leaf — that is the whole reason
/// <see cref="FactReader"/> is a narrow readonly struct instead of something that reaches into the
/// board or the status runtime on demand.</para>
///
/// <para><b>What is not on the board snapshot.</b> <c>BoardEntitySnap</c> carries side, type, row,
/// column and charm — no HP, no element, no statuses. Those arrive through the optional providers,
/// and the defaults are chosen so a missing provider makes a predicate <i>fail</i> rather than pass:
/// full HP, no element, no statuses. A condition that cannot be evaluated must not fire an effect.</para>
/// </summary>
public static class RunnerEventMapper
{
    /// <summary>Full health in per-mille — the default when nothing supplies HP.</summary>
    public const int FullHpMilli = 1000;

    public static RunnerEvent From(
        EffectEventDto ev,
        BoardSnapshot? board = null,
        Func<string, int>? hpMilli = null,
        Func<string, int>? elementId = null,
        Func<string, ulong>? statusMask = null)
    {
        if (ev is null) throw new ArgumentNullException(nameof(ev));

        var actor = ev.ActorPtr ?? "";
        var target = ev.TargetPtr ?? "";
        var killer = ev.KillerPtr;

        return new RunnerEvent(
            TriggerIndex.Ordinal(ev.Trigger),
            actor,
            target,
            Facts(actor, ev.Side, ev.TypeId, killer, board, hpMilli, elementId, statusMask),
            Facts(target, OtherSide(ev.Side), ev.TargetTypeId, killer, board, hpMilli, elementId, statusMask));
    }

    static EntityFacts Facts(
        string ptr, string? side, int? typeId, string? killerPtr,
        BoardSnapshot? board,
        Func<string, int>? hpMilli, Func<string, int>? elementId, Func<string, ulong>? statusMask)
    {
        var snap = board?.FindPtr(ptr);

        return new EntityFacts(
            Side: SideOrdinal(snap?.Side ?? side),
            TypeId: snap?.TypeId ?? typeId ?? -1,
            HpMilli: hpMilli?.Invoke(ptr) ?? FullHpMilli,
            ElementId: elementId?.Invoke(ptr) ?? -1,
            Row: snap?.Row ?? -1,
            Col: snap?.Col ?? -1,
            IsMindControlled: snap?.MindControlled ?? false,
            IsKiller: !string.IsNullOrEmpty(ptr) && CombatPtr.EqualsPtr(ptr, killerPtr),
            StatusMask: statusMask?.Invoke(ptr) ?? 0UL);
    }

    /// <summary>0 plant · 1 zombie · 2 bullet · -1 unknown, matching <see cref="EntityFacts.Side"/>.</summary>
    public static int SideOrdinal(string? side) => side?.ToLowerInvariant() switch
    {
        "plant" => 0,
        "zombie" => 1,
        "bullet" => 2,
        _ => -1,
    };

    /// <summary>
    /// The event carries one side — the actor's. The other entity is the opposing side, which is
    /// also what the shipped overlay's filters mean on <c>OnDamageDealt</c>; the compiler's subject
    /// trap exists because those two readings look identical and are not.
    /// </summary>
    static string? OtherSide(string? side) => side?.ToLowerInvariant() switch
    {
        "plant" => "zombie",
        "zombie" => "plant",
        _ => null,
    };
}
