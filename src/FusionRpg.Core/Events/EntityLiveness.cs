using System.Globalization;

namespace FusionRpg.Core.Events;

/// <summary>
/// lawn-hit-entry T9 / lawn-combat-wire L-N16: which lawn ptrs are dead or dying, and the one rule
/// that reads it — <b>a dead or dying target absorbs no delta</b>.
///
/// <para>Two gates share this set. The record-time gate (<c>EventDrainHost.TryRecord*</c>) refuses a
/// hit that arrives after death. The apply-time gate (<see cref="AdmitsDelta"/>, called by the FA10
/// resource-delta sink) refuses a hit that was recorded <i>before</i> death and drains after it —
/// the second projectile already in the ring when the first one killed. Without the apply gate
/// that late record reaches <c>EntityStatWriter.AddZombieHp</c>, sees HP ≤ 0 and calls
/// <c>ForceKillZombie</c>, which runs <c>Die()</c> a second time.</para>
///
/// <para>A ptr leaves the set on a genuine spawn edge (<see cref="MarkSpawned"/>): IL2CPP reuses
/// native addresses inside one match, and a new entity at a recycled ptr must take hits and must
/// get its own death handled. Main-thread only, like every injector caller.</para>
/// </summary>
public sealed class EntityLiveness
{
    readonly HashSet<IntPtr> _dead = new();

    public int DeadCount => _dead.Count;

    public void MarkDead(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) _dead.Add(ptr);
    }

    /// <summary>A real spawn at <paramref name="ptr"/> — clears a stale mark left by an earlier entity
    /// that lived at the same native address.</summary>
    public void MarkSpawned(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) _dead.Remove(ptr);
    }

    public bool IsDead(IntPtr ptr) => ptr != IntPtr.Zero && _dead.Contains(ptr);

    /// <summary>False only for a parseable ptr that is marked dead. An unparseable or empty ptr is
    /// admitted: this gate refuses dead targets, it does not validate addressing.</summary>
    public bool AdmitsDelta(string? targetPtr) => !TryParsePtr(targetPtr, out var p) || !IsDead(p);

    public void Clear() => _dead.Clear();

    long _staleMarkHits;
    IntPtr _lastStaleMarkPtr;

    /// <summary>lawn-combat-wire L-N27: a vanilla hit landed on <paramref name="ptr"/> while its entity still has health. If the
    /// ptr is marked dead, that mark is stale — the entity was reactivated at a recycled address without a spawn hook —
    /// and every RPG hit on it is being refused. Counts such hits; returns whether this one was one.</summary>
    public bool NoteHitOnLivingEntity(IntPtr ptr)
    {
        if (!IsDead(ptr)) return false;
        _staleMarkHits++;
        _lastStaleMarkPtr = ptr;
        return true;
    }

    /// <summary>Hits counted by <see cref="NoteHitOnLivingEntity"/> since the last call, and the last such ptr; resets both.</summary>
    public (long Hits, IntPtr LastPtr) TakeStaleMarkHits()
    {
        var result = (_staleMarkHits, _lastStaleMarkPtr);
        _staleMarkHits = 0;
        _lastStaleMarkPtr = IntPtr.Zero;
        return result;
    }

    /// <summary>Accepts the spellings a drained DTO carries: bare hex, <c>0x</c>-prefixed, or
    /// <c>entity:</c>-prefixed, any case.</summary>
    public static bool TryParsePtr(string? ptr, out IntPtr value)
    {
        value = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(ptr)) return false;
        var s = ptr.Trim();
        if (s.StartsWith("entity:", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) || v == 0)
            return false;
        value = new IntPtr(v);
        return true;
    }
}
