using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Delve.Attrition;

/// <summary>
/// `delve-attrition` D2.17 (spec-delve-attrition.md §1) — one creature between rooms. <see cref="Pools"/>
/// carries all six resource ids — `hp` included — so the battle's `Hp` and the pool have one owner
/// (never a second, drift-prone source of truth for the same seat). <see cref="NerveStacks"/> is the
/// counter `StatusRuntime` has no field for (P3, `decisions.md:134`) — the staged `nerve` status is
/// its projection, never the count itself.
/// </summary>
/// <param name="Pools">Keys == <see cref="FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds"/>,
/// checked on every read/write by <see cref="PartyPoolsCarry"/> — never a hand-listed subset.</param>
/// <param name="Downed">Stays true for the rest of the room's fight and the delve unless revived
/// (§6) — never cleared by anything in this record's own construction.</param>
/// <param name="DownedOnce">Set once, first transition only, never cleared in-run (§6/§7 — a revive
/// lets the run continue, it does not erase the fact for permadeath purposes, R3).</param>
public sealed record DelveMemberState(
    string InstanceId,
    IReadOnlyDictionary<string, long> Pools,
    IReadOnlyList<BattleStatusSpec> Statuses,
    BattleInnateShield? Shield,
    int NerveStacks,
    bool Downed,
    bool DownedOnce);
