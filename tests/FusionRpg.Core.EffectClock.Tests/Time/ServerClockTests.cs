using FusionRpg.Core.Time;
using Xunit;

namespace FusionRpg.Core.Tests.Time;

/// <summary>
/// The clock seam (rpg-simulator RS3, <c>docs/architecture/rpg-simulator-spec-clock-seam.md</c>).
///
/// <para><b>Shape B, and why it is not shape A</b> (spec §0, todo RS-F13). Owner ruling B1 (a) asked for
/// a full <c>System.TimeProvider</c> migration; <c>System.TimeProvider</c> ships in .NET 8 and
/// <c>FusionRpg.Core</c> targets <c>net6.0</c> (the Injector is a <c>net6.0</c> Unity host that
/// references Core), so the ruling's mechanism cannot be the seam's <i>stored</i> type. Its intent —
/// every ambient clock read becomes one injectable read — is executed in full, and a
/// <c>TimeProvider</c> is an <b>input</b> a net8.0 host adapts into the seam. That last point is what
/// the third test proves, because it is the one part of shape B that could quietly stop being true.</para>
///
/// <para>The class is one collection so the process-global seam is never configured by two tests at
/// once; every test restores the machine clock in <c>finally</c>, because a leaked configuration would
/// make an unrelated suite's timestamps wrong rather than fail it.</para>
/// </summary>
[Collection("server-clock")]
[Trait("VerificationId", "core.server-clock")]
public class ServerClockTests
{
    static readonly DateTimeOffset Fixed = DateTimeOffset.Parse("2026-03-04T05:06:07.1234567Z");

    [Fact]
    public void Configure_replaces_the_one_read()
    {
        try
        {
            ServerClock.Configure(() => Fixed);
            Assert.Equal(Fixed, ServerClock.UtcNow);
            Assert.Equal(Fixed.UtcDateTime, ServerClock.UtcNowDateTime);
        }
        finally { ServerClock.Reset(); }

        // Reset restores the machine clock: the read is close to real UTC again.
        Assert.True((ServerClock.UtcNow - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void The_two_accessors_keep_the_round_trip_form_each_type_emitted_before()
    {
        try
        {
            ServerClock.Configure(() => Fixed);

            // 165 migrated sites emitted DateTime.UtcNow.ToString("o"), whose round-trip form ends in Z.
            Assert.EndsWith("Z", ServerClock.UtcNowDateTime.ToString("o"), StringComparison.Ordinal);
            Assert.Equal(Fixed.UtcDateTime.ToString("o"), ServerClock.UtcNowDateTime.ToString("o"));

            // The DateTimeOffset form ends in +00:00 — the reason the second accessor exists: swapping
            // the type would have changed an emitted STRING, which is a behaviour change, not a migration.
            Assert.EndsWith("+00:00", ServerClock.UtcNow.ToString("o"), StringComparison.Ordinal);
            Assert.NotEqual(ServerClock.UtcNow.ToString("o"), ServerClock.UtcNowDateTime.ToString("o"));
        }
        finally { ServerClock.Reset(); }
    }

    /// <summary>A net8.0 <c>TimeProvider</c> is an INPUT to the seam, never a stored type (shape B).</summary>
    sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Fixed;
    }

    [Fact]
    public void A_net8_TimeProvider_is_adapted_into_the_seam_at_the_composition_root()
    {
        var provider = new FixedTimeProvider();
        try
        {
            ServerClock.Configure(provider.GetUtcNow);
            Assert.Equal(Fixed, ServerClock.UtcNow);
            Assert.Equal(Fixed.UtcDateTime, ServerClock.UtcNowDateTime);
        }
        finally { ServerClock.Reset(); }
    }

    [Fact]
    public void The_configured_offset_is_reported()
    {
        try
        {
            ServerClock.Configure(() => Fixed, offsetSeconds: -3600);
            Assert.Equal(-3600, ServerClock.OffsetSeconds);
        }
        finally { ServerClock.Reset(); }

        Assert.Equal(0, ServerClock.OffsetSeconds);
    }
}
