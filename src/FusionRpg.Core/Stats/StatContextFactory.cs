using FusionRpg.Contracts;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Stats;

public sealed class StatContextFactory
{
    public StatContext ForPlant(
        string entityKey,
        EntityBaseline baseline,
        int typeId = 0,
        string? matchKey = null,
        long? playerId = null,
        StatsConfig? cheatScale = null,
        IReadOnlyDictionary<string, int>? cheatAbsolute = null,
        bool applyStats = true,
        IReadOnlyDictionary<string, double>? cheatAbsoluteReal = null,
        IReadOnlyList<StatModifier>? pvzStatsMods = null,
        ActorElementTypes? elementTypes = null) =>
        new()
        {
            Side = StatSide.Plant,
            EntityKey = entityKey,
            Baseline = baseline,
            TypeId = typeId,
            MatchKey = matchKey,
            PlayerId = playerId,
            CheatScale = cheatScale,
            CheatAbsolute = cheatAbsolute,
            CheatAbsoluteReal = cheatAbsoluteReal,
            ApplyStats = applyStats,
            PvzStatsMods = pvzStatsMods,
            ElementTypes = elementTypes ?? ActorElementTypes.Neutral
        };

    public StatContext ForZombie(
        string entityKey,
        EntityBaseline baseline,
        int typeId = 0,
        string? matchKey = null,
        long? playerId = null,
        StatsConfig? cheatScale = null,
        IReadOnlyDictionary<string, int>? cheatAbsolute = null,
        bool applyStats = true,
        IReadOnlyDictionary<string, double>? cheatAbsoluteReal = null,
        IReadOnlyList<StatModifier>? pvzStatsMods = null,
        ActorElementTypes? elementTypes = null) =>
        new()
        {
            Side = StatSide.Zombie,
            EntityKey = entityKey,
            Baseline = baseline,
            TypeId = typeId,
            MatchKey = matchKey,
            PlayerId = playerId,
            CheatScale = cheatScale,
            CheatAbsolute = cheatAbsolute,
            CheatAbsoluteReal = cheatAbsoluteReal,
            ApplyStats = applyStats,
            PvzStatsMods = pvzStatsMods,
            ElementTypes = elementTypes ?? ActorElementTypes.Neutral
        };

    /// <summary>
    /// battle-hub-fuse T5 — battle-resolve contexts. Battle sides ("squad"/"wave") are orthogonal
    /// to <see cref="StatSide"/> ("plant"/"zombie"), so the caller maps explicitly; <c>Side</c> only
    /// ever partitions subsystem memos here (no Hub contributor reads it for values — the
    /// allocation-reference check keeps even a shared slot correct), never actor fiction.
    /// Follow-up: a dedicated battle-side axis if memo pressure ever needs it.
    /// </summary>
    public StatContext ForBattle(
        string entityKey,
        EntityBaseline baseline,
        StatSide side,
        int typeId = 0,
        string? matchKey = null,
        long? playerId = null) =>
        new()
        {
            Side = side,
            EntityKey = entityKey,
            Baseline = baseline,
            TypeId = typeId,
            MatchKey = matchKey,
            PlayerId = playerId,
            ElementTypes = ActorElementTypes.Neutral
        };
}
