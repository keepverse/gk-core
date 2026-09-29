namespace FusionRpg.Core.Actions.Cost;

/// <summary>
/// One resource's runtime state (spec-action-costs.md §2): the only persisted fields are
/// <see cref="Stored"/> and <see cref="LastTick"/> — the current value is resolved lazily on read,
/// never advanced by a scheduled event. With six pools across 200 actors that would be 1,200 timers
/// doing nothing but arithmetic; compute-on-read gives an identical answer for free.
///
/// <para><b>S10.1 sub-tick unit.</b> The rate arrives in per-mille of a unit per tick
/// (<see cref="Stats.Derived.ResourceChannelReader.RegenPerMilleTick"/>) and <see cref="Carry"/>
/// holds the sub-unit remainder, in per-mille, that has accrued but not yet become a whole unit.
/// Nothing is rounded per tick: the accumulation is exact and the single division by 1000 happens
/// last, once, on the total.</para>
///
/// <para>This is carry-correction for <i>how much</i>, and it is the same discipline
/// <c>KernelDriveHost</c> already applies to <i>when</i>: it re-arms a repeating effect off the
/// event's own <c>DueTick</c> rather than off "now" (<c>KernelDriveHost.cs:186-192</c>), so a
/// stuttering frame delays a DoT tick instead of permanently slowing its cadence. Rounding regen
/// per tick is the quantity-side version of re-arming off "now": each tick's lost fraction is gone
/// forever, and the error compounds without bound. Carrying the remainder makes the error zero.</para>
/// </summary>
/// <param name="Stored">Whole units banked as of <paramref name="LastTick"/>.</param>
/// <param name="LastTick">The tick <paramref name="Stored"/> and <paramref name="Carry"/> are anchored at.</param>
/// <param name="Carry">Sub-unit remainder in per-mille, always in <c>[0, 1000)</c>. Zero for a
/// freshly-seeded or freshly-clamped pool — a pool that hit a rail banks no windfall.</param>
public readonly record struct ResourcePoolState(long Stored, long LastTick, long Carry = 0)
{
    /// <summary>The per-mille denominator. Structural, not tunable: it is the unit
    /// <see cref="Stats.Derived.ResourceChannelReader.RegenPerMilleTick"/> emits in, and the two
    /// must agree or the arithmetic is simply wrong. Changing it is an "ask first" per
    /// spec-resource-subtick.md.</summary>
    public const long Milli = 1000L;

    /// <summary><c>value(now) = clamp(stored + floor((carry + ratePerMille * (now - lastTick)) / 1000), 0, max)</c>.
    /// <c>rate</c> and <c>max</c> are read fresh by the caller on every resolve (buffs/debuffs move
    /// them), so this struct itself never caches either. Pure: a read never consumes the carry.</summary>
    public long Resolve(long nowTick, long ratePerMilleTick, long max) =>
        Settle(nowTick, ratePerMilleTick, max).Stored;

    /// <summary>
    /// Materializes <see cref="Resolve"/> as the new <see cref="Stored"/>, carries the sub-unit
    /// remainder forward in <see cref="Carry"/>, and anchors <see cref="LastTick"/> at
    /// <paramref name="nowTick"/> — used at battle end, where the pool resolves to a concrete value
    /// and the clock it was ticking against is dropped (spec §2): the SAVED representation is a bare
    /// value with no <c>lastTick</c> attached, since a persisted tick count would make a reloaded
    /// actor's pool depend on wall-clock time between sessions.
    ///
    /// <para>Hitting either rail discards the carry as well as the overflow: a pool sitting full is
    /// not quietly banking 999‰ of a windfall to hand over the instant something spends from it.</para>
    ///
    /// <para>⚠️ Overflow discipline (docs/architecture/numeric-types.md "Numeric overflow"): every operand is already
    /// <c>long</c> (widen before multiply), the single <c>/1000</c> is last, and the whole
    /// accumulation is <c>checked</c> so an absurd rate × elapsed throws rather than wrapping.</para>
    /// </summary>
    public ResourcePoolState Settle(long nowTick, long ratePerMilleTick, long max)
    {
        if (nowTick < LastTick)
            throw new ArgumentOutOfRangeException(nameof(nowTick), nowTick, "nowTick precedes LastTick");

        var elapsed = nowTick - LastTick;

        // Widen-before-multiply: both operands are already long. Divide by 1000 LAST, exactly once.
        var accruedMilli = checked(Carry + ratePerMilleTick * elapsed);

        // Floor division, not C#'s truncate-toward-zero: a negative rate must lose its fraction in
        // the same direction it is moving, or the remainder leaves [0, 1000) and the carry drifts.
        var whole = accruedMilli / Milli;
        var remainder = accruedMilli - whole * Milli;
        if (remainder < 0)
        {
            whole -= 1;
            remainder += Milli;
        }

        var raw = checked(Stored + whole);

        if (raw < 0) return new ResourcePoolState(0, nowTick, 0);
        if (raw > max) return new ResourcePoolState(max, nowTick, 0);
        return new ResourcePoolState(raw, nowTick, remainder);
    }
}
