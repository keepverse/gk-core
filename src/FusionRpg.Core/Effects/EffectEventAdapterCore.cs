using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects;

/// <summary>Capture kind → FT* mapping without Unity. Injector and SimEffectHost share this.</summary>
public static class EffectEventAdapterCore
{
    /// <summary>
    /// E34 (spec-trigger-vocabulary.md §2.2): <c>wave.change</c> (the polled <c>board.theWave</c>
    /// transition) and <c>wave.spawn</c>/<c>wave.huge</c> all describe the same real wave — mapping
    /// every one of them to OnWave would double- or triple-fire an atom per wave, a real balance bug
    /// (a doubled resource-economy payout), not a tidiness one, and one the goldens cannot catch since
    /// no shipped content uses this trigger yet. One canonical edge per wave, per match: keyed by
    /// matchKey (never collapsed to "") so two matches sharing a process never share a wave counter,
    /// the same discipline <c>GameHooks.LastWave</c> already applies for <c>wave.change</c> alone —
    /// this extends it across all three host kinds. Capped like <see cref="EffectEventDedupe"/> so a
    /// long-lived process cannot grow this unbounded.
    /// </summary>
    static readonly Dictionary<string, int> LastMappedWave = new(StringComparer.Ordinal);

    public static EffectEventDto? TryMap(
        string kind,
        Dictionary<string, object> p,
        long tick,
        string? matchKey = null)
    {
        if (string.Equals(kind, "combat.hit", StringComparison.OrdinalIgnoreCase))
            return MapDealtFromCombatHit(p, tick, matchKey);

        if (string.Equals(kind, "zombie.damage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "plant.damage", StringComparison.OrdinalIgnoreCase))
            return MapTaken(kind, p, tick, matchKey);

        if (string.Equals(kind, "zombie.die", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "plant.die", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "debug.kill.signal", StringComparison.OrdinalIgnoreCase))
            return MapDeath(kind, p, tick, matchKey);

        if (string.Equals(kind, "plant.place", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "zombie.place", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "bullet.init", StringComparison.OrdinalIgnoreCase))
            return MapSpawn(kind, p, tick, matchKey);

        if (string.Equals(kind, "actor.activate", StringComparison.OrdinalIgnoreCase))
            return MapActivate(p, tick, matchKey);

        if (string.Equals(kind, "effect.timer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "combat.timer", StringComparison.OrdinalIgnoreCase))
        {
            return new EffectEventDto
            {
                Trigger = EffectTriggers.OnTimer,
                MatchKey = matchKey,
                Tick = tick,
                ActorPtr = p.TryGetValue("actorPtr", out var a) ? a?.ToString() : null,
                TargetPtr = p.TryGetValue("targetPtr", out var t) ? t?.ToString() : null
            };
        }

        // E34 (spec-trigger-vocabulary.md §2.2): wave.change is the canonical OnWave edge;
        // wave.spawn/wave.huge map to the same trigger only when their wave number differs from the
        // last one this mapper produced for this match — see LastMappedWave's own doc comment.
        if (string.Equals(kind, "wave.change", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "wave.spawn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "wave.huge", StringComparison.OrdinalIgnoreCase))
            return MapWave(p, tick, matchKey);

        if (string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase))
            return MapMatchStart(p, tick, matchKey);

        if (string.Equals(kind, "board.end", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "match.win", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "match.lose", StringComparison.OrdinalIgnoreCase))
            return MapMatchEnd(p, tick, matchKey);

        if (string.Equals(kind, "sun.gain", StringComparison.OrdinalIgnoreCase))
            return MapSunCollect(p, tick, matchKey);

        if (string.Equals(kind, "grid.place", StringComparison.OrdinalIgnoreCase))
            return MapGridPlace(p, tick, matchKey);

        return null;
    }

    /// <summary>
    /// E34 §2.2: match-scoped — no ActorPtr, no Side. A wave with no number carries nothing to dedupe
    /// against or to report, so it maps to no event rather than an OnWave with a null Wave.
    /// </summary>
    static EffectEventDto? MapWave(Dictionary<string, object> p, long tick, string? matchKey)
    {
        var wave = IntOrNull(p, "wave");
        if (wave is null) return null;

        var key = matchKey ?? "";
        if (LastMappedWave.TryGetValue(key, out var last) && last == wave.Value)
            return null; // already mapped this wave — wave.change vs wave.spawn/huge double-fire guard

        LastMappedWave[key] = wave.Value;
        if (LastMappedWave.Count > 4096) LastMappedWave.Clear();

        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnWave,
            MatchKey = matchKey,
            Wave = wave,
            Tick = tick
        };
    }

    /// <summary>E34 §2.2: MatchKey, Tick only — board.start. Also drops any stale wave counter for
    /// this matchKey, matching GameHooks.LastWave's own reset-on-board-start discipline (belt and
    /// braces: a matchKey is a fresh GUID per match in practice, so this is normally a no-op).</summary>
    static EffectEventDto MapMatchStart(Dictionary<string, object> p, long tick, string? matchKey)
    {
        LastMappedWave.Remove(matchKey ?? "");
        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnMatchStart,
            MatchKey = matchKey,
            Tick = tick
        };
    }

    /// <summary>E34 §2.2: MatchKey, Tick only — board.end / match.win / match.lose all mean the same
    /// thing to the atom layer. Drops the wave counter for this matchKey so a long-lived process does
    /// not accumulate one entry per match forever.</summary>
    static EffectEventDto MapMatchEnd(Dictionary<string, object> p, long tick, string? matchKey)
    {
        LastMappedWave.Remove(matchKey ?? "");
        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnMatchEnd,
            MatchKey = matchKey,
            Tick = tick
        };
    }

    /// <summary>E34 §2.2: MatchKey, Tick only. The sun count deliberately gets no field — a predicate
    /// over collected sun is E3's closed leaf list, out of scope here.</summary>
    static EffectEventDto MapSunCollect(Dictionary<string, object> p, long tick, string? matchKey) =>
        new()
        {
            Trigger = EffectTriggers.OnSunCollect,
            MatchKey = matchKey,
            Tick = tick
        };

    /// <summary>E34 §2.2: MatchKey, Tick, TypeId (the grid item type, from the host's own "type" key —
    /// GameCaptureHooks.cs's SetGridItemHook), ActorPtr only when the payload carries "ptr".</summary>
    static EffectEventDto MapGridPlace(Dictionary<string, object> p, long tick, string? matchKey) =>
        new()
        {
            Trigger = EffectTriggers.OnGridPlace,
            MatchKey = matchKey,
            Tick = tick,
            TypeId = IntOrNull(p, "type") ?? IntOrNull(p, "typeId"),
            ActorPtr = Str(p, "ptr")
        };

    /// <summary>
    /// Maps injector <c>combat.hit</c> → <see cref="EffectTriggers.OnDamageDealt"/>.
    /// W0-D SSOT payloads: <c>source=takeDamage</c> (Bullet via TakeDamage) and
    /// <c>source=attackPlant</c> (melee). Actor = <c>attackerPtr</c> else <c>bulletPtr</c>.
    /// Base Bullet.Hit* Harmony is not the primary surface.
    /// </summary>
    static EffectEventDto MapDealtFromCombatHit(Dictionary<string, object> p, long tick, string? matchKey)
    {
        var side = Str(p, "side") ?? "zombie";
        var actorSide = string.Equals(side, "zombie", StringComparison.OrdinalIgnoreCase) ? "plant" : "zombie";
        // Prefer melee attackerPtr; pea TakeDamage stamps bulletPtr only.
        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            MatchKey = matchKey,
            Side = actorSide,
            ActorPtr = Str(p, "attackerPtr") ?? Str(p, "bulletPtr"),
            TargetPtr = Str(p, "targetPtr"),
            TypeId = IntOrNull(p, "fromType"),
            TargetTypeId = IntOrNull(p, "targetType"),
            Damage = LongOrNull(p, "damage"),
            Tick = tick,
            ScenarioId = Str(p, "scenarioId")
        };
    }

    static EffectEventDto MapTaken(string kind, Dictionary<string, object> p, long tick, string? matchKey)
    {
        var side = kind.StartsWith("plant", StringComparison.OrdinalIgnoreCase) ? "plant" : "zombie";
        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageTaken,
            MatchKey = matchKey,
            Side = side,
            ActorPtr = Str(p, "damageFrom"),
            TargetPtr = Str(p, "ptr"),
            TypeId = IntOrNull(p, "type") ?? IntOrNull(p, "typeId"),
            TargetTypeId = IntOrNull(p, "type") ?? IntOrNull(p, "typeId"),
            Damage = LongOrNull(p, "damage"),
            Tick = tick,
            ScenarioId = Str(p, "scenarioId")
        };
    }

    static EffectEventDto MapDeath(string kind, Dictionary<string, object> p, long tick, string? matchKey)
    {
        var side = kind.Contains("plant", StringComparison.OrdinalIgnoreCase) ? "plant" : "zombie";
        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnDeath,
            MatchKey = matchKey,
            Side = side,
            ActorPtr = Str(p, "ptr") ?? Str(p, "deadPtr"),
            TargetPtr = Str(p, "ptr") ?? Str(p, "deadPtr"),
            TypeId = IntOrNull(p, "type") ?? IntOrNull(p, "typeId"),
            KillerPtr = Str(p, "killerPtr") ?? Str(p, "damageFrom"),
            Tick = tick,
            ScenarioId = Str(p, "scenarioId")
        };
    }

    static EffectEventDto MapSpawn(string kind, Dictionary<string, object> p, long tick, string? matchKey)
    {
        string side;
        if (kind.Contains("bullet", StringComparison.OrdinalIgnoreCase)) side = "bullet";
        else if (kind.Contains("plant", StringComparison.OrdinalIgnoreCase)) side = "plant";
        else side = "zombie";

        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnSpawn,
            MatchKey = matchKey,
            Side = side,
            ActorPtr = Str(p, "ptr"),
            TypeId = IntOrNull(p, "type") ?? IntOrNull(p, "typeId")
                     ?? IntOrNull(p, "plantType") ?? IntOrNull(p, "bulletType"),
            Tick = tick,
            ScenarioId = Str(p, "scenarioId")
        };
    }

    /// <summary>
    /// E33 (spec-activation-edge.md §2.2): the activation edge — "this actor decided to act" — reaches
    /// the atom layer the same way every other lawn event does. `actorPtr` is required, never
    /// defaulted; a payload with none maps to no event, never a board-wide fan-out (the inverse of
    /// G5's <c>FindObjectsOfType&lt;Zombie&gt;()</c> hole). `actionId`, if present, is telemetry only
    /// — the atom layer has no action vocabulary and gains none here.
    /// </summary>
    static EffectEventDto? MapActivate(Dictionary<string, object> p, long tick, string? matchKey)
    {
        var actorPtr = Str(p, "actorPtr");
        if (string.IsNullOrEmpty(actorPtr)) return null;

        return new EffectEventDto
        {
            Trigger = EffectTriggers.OnActivate,
            MatchKey = matchKey,
            Side = Str(p, "side"),
            ActorPtr = actorPtr,
            TargetPtr = Str(p, "targetPtr"),
            TypeId = IntOrNull(p, "typeId"),
            TargetTypeId = IntOrNull(p, "targetTypeId"),
            Tick = tick,
            ScenarioId = Str(p, "scenarioId")
        };
    }

    static string? Str(Dictionary<string, object> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v == null) return null;
        return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    static int? IntOrNull(Dictionary<string, object> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v == null) return null;
        try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    /// <summary>Damage is a magnitude, not an id — parsed to <c>long</c> so a power-scaled hit
    /// doesn't silently truncate through the same path <see cref="IntOrNull"/> uses for type ids.</summary>
    static long? LongOrNull(Dictionary<string, object> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v == null) return null;
        try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}
