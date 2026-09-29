namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// What a cooldown is charged against. A pair rather than a concatenated string, because building
/// <c>actor + ":" + key</c> on every commit is a string allocation per actor per turn on the Unity
/// main thread — the exact cost the kernel's zero-allocation contract exists to prevent.
/// </summary>
public readonly record struct CooldownSlot(string ActorKey, string Slot);

/// <summary>
/// Cooldown state. It has an owner — this module — because the alternative is what the engine does
/// today: each caller remembering its own timers, which cannot survive a mode where actions no
/// longer land on round boundaries.
///
/// <para><b>Cooldowns are absolute ticks on the simulation clock, so they keep running while their
/// owner is suspended.</b> This is the stated rule the spec asks for, and the reasoning is the same
/// one that kept crowd control out of the turn states: pausing a cooldown means storing a
/// <i>remainder</i>, and a remainder goes stale on every mutation the ledger cannot observe —
/// stacking, displacement, grant clearing, withdrawal, expiry. An absolute tick has nothing to go
/// stale. A mode that genuinely wants stun to pause cooldowns should reschedule the ready tick
/// explicitly, where the decision is visible.</para>
///
/// <para>Determinism note: the dictionary is keyed by a struct whose generated hash mixes
/// per-process string hashes, so bucket order varies run to run. That is safe here and only here,
/// because every access is an exact-key lookup — the ledger is never enumerated, and nothing about
/// its iteration order can reach a report. The kernel's purity guard bans <c>.Keys</c> and
/// <c>.Values</c> for precisely this reason.</para>
/// </summary>
public sealed class CooldownLedger
{
    readonly Dictionary<CooldownSlot, long> _readyAt;

    public CooldownLedger(int expectedEntries = 32) => _readyAt = new Dictionary<CooldownSlot, long>(expectedEntries);

    /// <summary>True when the action is off cooldown at <paramref name="nowTick"/>.</summary>
    public bool IsReady(string actorKey, ActionEnvelope envelope, long nowTick)
    {
        if (!TrySlot(actorKey, envelope, out var slot)) return true;
        return !_readyAt.TryGetValue(slot, out var readyAt) || readyAt <= nowTick;
    }

    /// <summary>
    /// Puts the action on cooldown from <paramref name="atTick"/>. The caller decides when that is
    /// — see <see cref="CooldownStart"/> — because commit, resolve, and recovery-end are three
    /// different games' answers and the envelope declares which one it means.
    /// </summary>
    /// <param name="reductionPm">
    /// combat-unification `species-skills` S2 — the acting actor's <c>skill.cooldown.{category}</c>
    /// value, per-mille. <b>0 is neutral</b> (the channel's registered default), and at 0
    /// <see cref="CooldownMath.ApplyReduction"/> returns the base ticks unchanged, so every existing
    /// caller that omits it behaves exactly as before.
    ///
    /// <para><b>Applied here, at the ARMING site, and deliberately not at the read site.</b> This
    /// ledger stores an absolute tick — see this type's own summary: "An absolute tick has nothing to
    /// go stale." Reducing at read time would let a mid-battle change retroactively shorten a cooldown
    /// that is already running.</para>
    /// </param>
    public void Start(string actorKey, ActionEnvelope envelope, long atTick, long reductionPm = 0)
    {
        if (!TrySlot(actorKey, envelope, out var slot)) return;
        if (envelope.CooldownTicks <= 0) return;
        _readyAt[slot] = atTick + CooldownMath.ApplyReduction(envelope.CooldownTicks, reductionPm);
    }

    /// <summary>The tick the action comes off cooldown; 0 when it is not on one.</summary>
    public long ReadyAt(string actorKey, ActionEnvelope envelope)
    {
        if (!TrySlot(actorKey, envelope, out var slot)) return 0;
        return _readyAt.TryGetValue(slot, out var readyAt) ? readyAt : 0;
    }

    /// <summary>Clears one action's cooldown — for a refund effect, or a battle reset.</summary>
    public bool Clear(string actorKey, ActionEnvelope envelope) =>
        TrySlot(actorKey, envelope, out var slot) && _readyAt.Remove(slot);

    /// <summary>
    /// Resolves the key an envelope charges against. <c>Specific</c> keys on the action id;
    /// <c>Category</c> keys on the declared group, and a <c>Category</c> with no group is refused
    /// loudly rather than silently falling back to the action id — that fallback would turn a
    /// content typo into a shared cooldown that quietly stopped being shared.
    /// </summary>
    static bool TrySlot(string actorKey, ActionEnvelope envelope, out CooldownSlot slot)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        if (string.IsNullOrEmpty(actorKey)) throw new ArgumentException("actorKey is required", nameof(actorKey));

        slot = default;
        switch (envelope.Class)
        {
            case CooldownClass.None:
                return false;
            case CooldownClass.Specific:
                slot = new CooldownSlot(actorKey, envelope.ActionId);
                return true;
            case CooldownClass.Category:
                if (string.IsNullOrEmpty(envelope.CooldownKey))
                    throw new ArgumentException(
                        $"Action '{envelope.ActionId}' declares a Category cooldown with no CooldownKey — " +
                        "a category with no discriminator cannot say which category.", nameof(envelope));
                slot = new CooldownSlot(actorKey, envelope.CooldownKey);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(envelope), envelope.Class, "unknown cooldown class");
        }
    }
}
