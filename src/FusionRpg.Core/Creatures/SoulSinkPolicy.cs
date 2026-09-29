using FusionRpg.Core.Power;

namespace FusionRpg.Core.Creatures;

/// <summary>
/// The sink half of the soul economy, paired to <see cref="SoulEarnPolicy"/>'s faucet.
///
/// <para><b>Why this exists (effort-power reconciliation M2, 2026-09-05).</b> The faucet scales on
/// <c>P(Θ)</c> — <see cref="SoulEarnPolicy.KillEarn"/> and
/// <see cref="SoulEarnPolicy.MatchEndEarn"/> both multiply by <see cref="ContentScale"/>. Every sink
/// was a flat, Θ-free number: the slot price, the ritual price, upkeep, the pull cost, the fusion
/// recipe cost. Income therefore grew quadratically against costs that never moved, which is soul
/// inflation on a schedule and breaks PS-5's rule that a faucet and its sink share a scale.</para>
///
/// <para><b>Why it is inert today, and why it was still worth fixing now.</b> Every vanilla-PvZ
/// award reads at the pin (<c>Θ=20</c>, <c>contentScale = 1.000</c> exactly) because the capture
/// pipeline carries no per-kill depth signal yet — see <c>RpgStore.Souls.cs</c>'s
/// <c>VanillaPvzKillAndRunTheta</c> for the full reasoning. So this multiplication is
/// byte-identical at every shipped call site right now. It stops being byte-identical the moment a
/// real Θ signal lands, which is exactly when retrofitting it would be a balance change instead of
/// a no-op.</para>
///
/// <para><b>The pairing rule.</b> A sink reads the SAME Θ its faucet reads — content depth, not the
/// actor's own ladder index. Reading a different index would leave the ratio drifting, which is the
/// defect this closes rather than a different flavour of it.</para>
/// </summary>
public static class SoulSinkPolicy
{
    /// <summary>The documented placeholder depth every vanilla-PvZ soul flow reads, faucet and sink
    /// alike. <c>contentScale(20) = 1.000</c> exactly, so reading here is byte-identical to an
    /// unscaled number — but it is an EXPLICIT placeholder, not a silent default, and it is one
    /// constant rather than two so the faucet and the sink cannot drift apart. The reason a real
    /// signal does not exist yet lives on <c>RpgStore.Souls.cs</c>'s own reference to this.</summary>
    public const int VanillaPvzTheta = 20;

    /// <summary>Scales one authored soul price by the same content scale the faucet uses.
    /// <paramref name="thetaContent"/> is required, not defaulted — same discipline as
    /// <see cref="SoulEarnPolicy.KillEarn"/>: a caller with no real depth signal says so explicitly
    /// at the pin, never silently.</summary>
    public static long Price(long basePriceSouls, int thetaContent, PowerTuning tuning) =>
        ContentScale.Apply(basePriceSouls, ContentScale.Milli(thetaContent, tuning));
}
