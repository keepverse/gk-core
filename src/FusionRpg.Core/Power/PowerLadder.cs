namespace FusionRpg.Core.Power;

/// <summary>Thrown by <see cref="PowerLadder.Value"/> when Θ exceeds <see cref="PowerLadder.MaxIndex"/> — never wraps.</summary>
public sealed class PowerIndexOverflow : Exception
{
    public int Index { get; }
    public long MaxIndex { get; }

    public PowerIndexOverflow(int index, long maxIndex)
        : base($"power ladder: Θ={index} exceeds maxIndex={maxIndex} for the loaded curve — refuses to wrap")
    {
        Index = index;
        MaxIndex = maxIndex;
    }
}

/// <summary>
/// The Θ ladder (ssot-power-scale.md §4). Arithmetic progression on the increment: the step
/// A + B·(Θ−1) grows linearly, so the total is triangular — local exponent 1.1 → 1.9 across the
/// playable band. Θ(Θ−1) is always even, so the triangular term divides exactly and no rounding
/// happens inside the sum; the single rounding is milli → whole, at the end (§2.1).
///
/// Pure and stateless over its loaded <see cref="PowerTuning"/> — no numeric literal outside
/// <see cref="PowerTuning"/>'s loader (power-guard, wave 4, enforces this permanently).
/// </summary>
public sealed class PowerLadder
{
    readonly PowerTuning _t;
    long? _maxIndex;

    public PowerLadder(PowerTuning tuning) => _t = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>P(Θ) in per-mille, before the single end rounding. Exact — no float anywhere.</summary>
    public long ValueMilli(int index)
    {
        Guard(index);
        var c = _t.Curve;
        return checked(c.CMilli + c.AMilli * index + TriangularMilli(c, index));
    }

    /// <summary>
    /// B·index·(index−1)/2, computed without ever forming the un-halved product. index·(index−1) is
    /// a product of two consecutive integers and therefore always even — halving whichever factor is
    /// even BEFORE multiplying by BMilli keeps the intermediate magnitude at the true final size.
    /// Multiplying first and dividing by 2 last can overflow `checked` even when the actual (halved)
    /// result comfortably fits — found while deriving <see cref="MaxIndex"/>, which understated the
    /// true ceiling by ~30% at the decided B=400 dial before this fix.
    /// </summary>
    static long TriangularMilli(PowerCurveTuning c, long index)
    {
        long half, other;
        if ((index & 1) == 0) { half = index / 2; other = index - 1; }
        else { half = (index - 1) / 2; other = index; }
        return checked(c.BMilli * half * other);
    }

    /// <summary>P(Θ) in whole units — the single rounding, half away from zero, at the end.</summary>
    public long Value(int index) => RoundHalfAwayFromZero(ValueMilli(index), 1000);

    /// <summary>
    /// The largest Θ for which <see cref="ValueMilli"/> stays representable in <c>long</c>, given the
    /// loaded curve — a computed property of <c>B</c>, not a constant (§2.5). Reporting it as a
    /// function of the dial is how a balance owner learns that a steeper curve costs headroom.
    /// </summary>
    public long MaxIndex
    {
        get
        {
            if (_maxIndex is { } cached) return cached;

            // Binary search over the int range (Value's own index parameter is int, so no caller can
            // ever exceed it) for the boundary where ValueMilli would overflow long. Assumes
            // ValueMilli is non-decreasing for Θ≥0, which holds for AMilli≥0 — true across the whole
            // documented dial band (§4.5); no legal shipped tuning sets B large enough to violate it.
            long lo = 0, hi = int.MaxValue;
            while (lo < hi)
            {
                long mid = lo + (hi - lo + 1) / 2;
                if (FitsInLong(mid)) lo = mid; else hi = mid - 1;
            }
            _maxIndex = lo;
            return lo;
        }
    }

    bool FitsInLong(long index)
    {
        try
        {
            var c = _t.Curve;
            checked { _ = c.CMilli + c.AMilli * index + TriangularMilli(c, index); }
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    void Guard(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), $"power ladder: Θ must not be negative — got {index}");
        if (index > MaxIndex)
            throw new PowerIndexOverflow(index, MaxIndex);
    }

    static long RoundHalfAwayFromZero(long milli, long scale)
    {
        long q = milli / scale;
        long r = milli % scale;
        if (r == 0) return q;
        long twiceR = checked(r * 2);
        return milli >= 0
            ? (twiceR >= scale ? q + 1 : q)
            : (-twiceR >= scale ? q - 1 : q);
    }
}
