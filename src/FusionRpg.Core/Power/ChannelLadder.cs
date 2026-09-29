namespace FusionRpg.Core.Power;

/// <summary>
/// One channel's own anchor at Θ=20 — hp keeps <see cref="PowerTuning.FixedCMilli"/> /
/// <see cref="PowerTuning.FixedPinValue"/> directly and needs no entry here; atk/defense are loaded
/// (spec-battle-magnitude.md §2.1, §4).
/// </summary>
public sealed record PowerChannelTuning(long CMilli, long PinValue);

/// <summary>
/// battle-magnitude (T2.1, spec-battle-magnitude.md §2.1): one shared dial `B`, applied
/// <b>proportionally</b> to each channel's own pin — `B_ch = B · pinCh / pinHp` — so hp/atk/defense
/// always move together in the same ratio; a single ratio on `Value(Θ)` cannot reproduce all three
/// pins at once (§2.1's disproof), and a shared *absolute* `B` drives defense's derived `A` negative
/// (audit F1 — defense's whole pin is 22, smaller than the quadratic term alone at `B=0.4`).
///
/// <para><b>Unlike <see cref="PowerLadder"/>, this never materializes a rounded per-mille `A_ch`
/// or `B_ch`.</b> The spec's own worked example has `A_ch = 3.4859` for atk — not an exact per-mille
/// value — so rounding it before combining with the triangular term would compound error and could
/// miss the pin. The whole expression is carried as one exact `long` numerator over a fixed `long`
/// denominator and rounded exactly once, at the very end — same principle as PowerLadder's single
/// end-rounding, just with a denominator wider than 1000.</para>
/// </summary>
public sealed class ChannelLadder
{
    readonly long _bMilli;
    readonly long _pinHp;
    readonly long _cCh;
    readonly long _pinCh;

    public ChannelLadder(long bMilli, long pinHp, PowerChannelTuning channel)
    {
        if (pinHp <= 0) throw new ArgumentOutOfRangeException(nameof(pinHp), "channel ladder: pinHp must be positive");
        if (channel is null) throw new ArgumentNullException(nameof(channel));
        if (channel.PinValue <= 0) throw new ArgumentOutOfRangeException(nameof(channel), "channel ladder: pinValue must be positive");

        _bMilli = bMilli;
        _pinHp = pinHp;
        _cCh = channel.CMilli / 1000;
        _pinCh = channel.PinValue;
    }

    /// <summary>
    /// <c>A_ch</c>'s exact value as a rational: <c>AMilliNumerator / AMilliDenominator</c>. Exposed so
    /// F1's "every channel's derived A is &gt; 0" check can test the sign without ever forming a
    /// float — the denominator is always positive by construction, so numerator sign == value sign.
    /// </summary>
    public long AMilliNumerator => checked(
        (_pinCh - _cCh) * _pinHp * 1_000_000 - _bMilli * _pinCh * 190_000);

    public long AMilliDenominator => checked(_pinHp * 20_000);

    public long Value(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), $"channel ladder: Θ must not be negative — got {index}");

        long denominator = checked(_pinHp * 20_000);
        long numerator = checked(
            _cCh * _pinHp * 20_000
            + (long)index * ((_pinCh - _cCh) * _pinHp * 1_000 - _bMilli * _pinCh * 190)
            + 10L * index * (index - 1) * _bMilli * _pinCh);

        return RoundHalfAwayFromZero(numerator, denominator);
    }

    static long RoundHalfAwayFromZero(long numerator, long denominator)
    {
        long q = numerator / denominator;
        long r = numerator % denominator;
        if (r == 0) return q;
        long twiceR = checked(Math.Abs(r) * 2);
        bool roundsUp = twiceR >= Math.Abs(denominator);
        if (!roundsUp) return q;
        bool negative = (numerator < 0) != (denominator < 0);
        return negative ? q - 1 : q + 1;
    }
}
