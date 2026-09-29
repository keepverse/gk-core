using FusionRpg.Core.Combat;

namespace FusionRpg.Core.Stats;

/// <summary>
/// Per-ptr max-HP pin for a debug-spawned entity (`debug.spawn-plant` / `debug.spawn-zombie` with an
/// absolute <c>hp</c>/<c>maxHp</c>), fed to <c>ActorHub.Resolve</c> as an input — lawn-combat-wire L-N20.
///
/// <para>Why an input and not a post-write: the pin used to be re-asserted AFTER the Hub write
/// (<c>EntityStatWriter.Force*MaxHpPreserveRatio</c>), which replaced the composed max HP and so erased
/// every Hub max-HP bonus (aptitude, loadout, equipment) on that ptr. As an Override on the
/// <see cref="StatChannels.MaxHp"/> primary channel it sets the base, and
/// <c>ActorHub.MergeAppliedCombat</c> still adds <c>progression.bonus.maxHp</c> on top. Current HP
/// then follows the ordinary writer rule (<c>StatSystem.CurrentHpForWrite</c>): a reapply keeps the
/// live ratio, a spawn write takes the composed value.</para>
///
/// <para>The pin is applied on every resolve for its ptr regardless of <c>includeAbsolute</c>: the
/// global <c>P-MAXHP</c>/<c>Z-MAXHP</c> channel is dropped by an <c>includeAbsolute: false</c> reapply
/// (e.g. <c>cheat.pushScales</c>), which is the defect the pin exists for. For its own ptr the pin wins
/// over that global channel. Keys are <see cref="CombatPtr.Normalize"/>d; main-thread callers, but the
/// store locks because server-relayed commands and the frame loop share it.</para>
/// </summary>
public sealed class SpawnHpPin
{
    readonly object _gate = new();
    readonly Dictionary<string, int> _pins = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_gate) return _pins.Count; }
    }

    /// <summary>Non-positive values and empty ptrs are ignored — a pin is an explicit positive max.</summary>
    public void Pin(string? ptr, int maxHp)  // overflow-bounded: a debug spawn pin feeding the int cheat-absolute map, which is written into PvZ's int max-health field (host limit, docs/architecture/numeric-types.md numeric rule 4)
    {
        var key = CombatPtr.Normalize(ptr);
        if (key.Length == 0 || maxHp <= 0) return;
        lock (_gate) _pins[key] = maxHp;
    }

    public bool TryGet(string? ptr, out int maxHp)  // overflow-bounded: a debug spawn pin feeding the int cheat-absolute map, which is written into PvZ's int max-health field (host limit, docs/architecture/numeric-types.md numeric rule 4)
    {
        var key = CombatPtr.Normalize(ptr);
        lock (_gate) return _pins.TryGetValue(key, out maxHp);
    }

    /// <summary>Call when the entity leaves the board: IL2CPP reuses addresses inside a match, and a
    /// new entity at this ptr must not inherit the pin.</summary>
    public void Remove(string? ptr)
    {
        var key = CombatPtr.Normalize(ptr);
        if (key.Length == 0) return;
        lock (_gate) _pins.Remove(key);
    }

    public void Clear()
    {
        lock (_gate) _pins.Clear();
    }

    /// <summary>The Hub absolute input for <paramref name="ptr"/>: <paramref name="cheatAbsolute"/>
    /// unchanged when the ptr has no pin, otherwise a copy whose <see cref="StatChannels.MaxHp"/> is
    /// the pin. Never mutates the caller's map.</summary>
    public IReadOnlyDictionary<string, int>? ApplyTo(string? ptr, IReadOnlyDictionary<string, int>? cheatAbsolute)
    {
        if (!TryGet(ptr, out var pinned)) return cheatAbsolute;
        var merged = cheatAbsolute == null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(cheatAbsolute, StringComparer.Ordinal);
        merged[StatChannels.MaxHp] = pinned;
        return merged;
    }
}
