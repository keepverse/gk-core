using FusionRpg.Core.Activity;
using Xunit;

namespace FusionRpg.Core.Tests.Activity;

/// <summary>`commander-roster` EP3.4 — the MatchStarted payload's commander field as the server reads it
/// (spec-lawn-commander-seat.md "The seat wire"). Absence MEANS "no seat": a pre-EP3.4 payload must read
/// as seatless, never as today's default commander.</summary>
public class MatchStartedSeatPayloadTests
{
    [Fact]
    public void A_leading_commander_id_is_read_from_the_payload()
    {
        Assert.Equal(
            "commander:unique:abc",
            PvzActivityKinds.LeadingCommanderId(
                """{"matchKey":"m1","leadingCommanderId":"commander:unique:abc"}"""));
    }

    [Theory]
    [InlineData("""{"matchKey":"m1"}""")]              // a pre-EP3.4 payload: no field at all
    [InlineData("""{"leadingCommanderId":""}""")]      // an empty id is not a commander
    [InlineData("""{"leadingCommanderId":null}""")]    // explicitly no seat
    [InlineData("""{"leadingCommanderId":42}""")]      // a number is not an id
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void An_absent_or_blank_field_means_no_seat(string? payloadJson)
    {
        Assert.Null(PvzActivityKinds.LeadingCommanderId(payloadJson));
    }
}
