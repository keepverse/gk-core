using System;

namespace FusionRpg.Core.Time;

/// <summary>
/// The server's wall clock is an <b>input</b>, not an ambient fact
/// (<c>docs/architecture/rpg-simulator-spec-clock-seam.md</c> §1). One value is read from one place;
/// nothing else in <c>src/</c> reads the machine clock; and the value is reported, so a pasted
/// artifact says what time the server thought it was.
///
/// <para><b>Product surface, not a test seam</b> (owner ruling B2 (b)). The two product behaviours are
/// both wall-clock reads a player can already reach: a player can move their machine clock, and a
/// hibernating world catches up lazily on read. So this type is <b>not</b> behind <c>SimFlags</c> — a
/// release build compiles and runs it, the composition root configures it once, and a verdict prints
/// what it was.</para>
///
/// <para><b>The erratum this shape answers</b> (spec §0, todo RS-F13). Owner ruling <b>B1 (a)</b> asked
/// for a full <c>System.TimeProvider</c> migration, but <c>System.TimeProvider</c> ships in .NET 8 and
/// <c>FusionRpg.Core</c> targets <c>net6.0</c> (the Injector is a Unity/BepInEx <c>net6.0</c> host and
/// references Core). The ruling's <i>intent</i> — every ambient clock read becomes one injectable
/// read — is executed here in full; its <i>mechanism</i> is <b>shape B</b>: the stored type is one
/// <see cref="Func{DateTimeOffset}"/>, and a <c>TimeProvider</c> is an <b>input</b> a net8.0
/// composition root adapts into it (<c>ServerClock.Configure(timeProvider.GetUtcNow)</c>), never a
/// stored type.</para>
///
/// <para><b>Two accessors, one read.</b> <see cref="UtcNow"/> is the seam's own type
/// (<see cref="DateTimeOffset"/>, the type the configured delegate returns).
/// <see cref="UtcNowDateTime"/> is the same read as a UTC <see cref="DateTime"/>, and exists because
/// 165 of the migrated sites emitted <c>DateTime.UtcNow.ToString("o")</c>, whose round-trip form ends
/// in <c>Z</c>; a <see cref="DateTimeOffset"/> round-trip ends in <c>+00:00</c>. Both come from the one
/// configured delegate, so the value is still single-sourced — the second accessor preserves an
/// emitted STRING, which is a migration, not a behavioural change.</para>
/// </summary>
public static class ServerClock
{
    static volatile Func<DateTimeOffset> _utcNow = static () => DateTimeOffset.UtcNow;

    /// <summary>
    /// The signed offset in seconds the composition root applied (from <c>FUSIONRPG_CLOCK_OFFSET</c>),
    /// so a host can report it. <c>0</c> means the machine clock. Never accepted from a route
    /// (spec §2): a player-reachable clock setter would make every deadline in the game lie.
    /// </summary>
    public static long OffsetSeconds { get; private set; }

    /// <summary>The one configured UTC read. Nothing else in <c>src/</c> reads the machine clock.</summary>
    public static DateTimeOffset UtcNow => _utcNow();

    /// <summary>
    /// The same configured read as a UTC <see cref="DateTime"/> (Kind <see cref="DateTimeKind.Utc"/>),
    /// for the ISO-emission sites that must keep their <c>DateTime</c> round-trip form.
    /// </summary>
    public static DateTime UtcNowDateTime => _utcNow().UtcDateTime;

    /// <summary>
    /// Configure the seam. Called exactly once, from the composition root. A net8.0 host hands the
    /// seam a <c>TimeProvider</c> by adapting its own read:
    /// <c>ServerClock.Configure(timeProvider.GetUtcNow)</c>.
    /// </summary>
    public static void Configure(Func<DateTimeOffset> utcNow, long offsetSeconds = 0)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        OffsetSeconds = offsetSeconds;
    }

    /// <summary>Restore the machine clock. Host teardown and tests only — never a product path.</summary>
    public static void Reset()
    {
        _utcNow = static () => DateTimeOffset.UtcNow;
        OffsetSeconds = 0;
    }
}
