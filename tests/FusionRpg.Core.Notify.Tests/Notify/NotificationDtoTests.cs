using System.Text.Json;
using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.Core.Tests.Notify;

/// <summary>notify-vocabulary §3 — the closed wire enums and DTOs. Member counts are a closed
/// vocabulary the code owns (a category, by contrast, is an open population and is never pinned
/// here): NotifySeverity=3, NotifyArgKind=5, NotifyRefKind=6, NotifyDelivery=2. A new member is a
/// reviewed change.</summary>
public class NotificationDtoTests
{
    [Fact]
    public void NotifySeverity_has_exactly_3_members()
        => Assert.Equal(3, Enum.GetValues<NotifySeverity>().Length);

    [Fact]
    public void NotifyArgKind_has_exactly_5_members()
        => Assert.Equal(5, Enum.GetValues<NotifyArgKind>().Length);

    [Fact]
    public void NotifyRefKind_has_exactly_6_members()
        => Assert.Equal(6, Enum.GetValues<NotifyRefKind>().Length);

    [Fact]
    public void NotifyDelivery_has_exactly_2_members()
        => Assert.Equal(2, Enum.GetValues<NotifyDelivery>().Length);

    [Fact]
    public void NotifySeverity_is_ordered_so_a_larger_value_outranks_a_smaller_one()
    {
        Assert.True(NotifySeverity.Critical > NotifySeverity.Important);
        Assert.True(NotifySeverity.Important > NotifySeverity.Routine);
    }

    [Theory]
    [InlineData(NotifySeverity.Routine, "\"routine\"")]
    [InlineData(NotifySeverity.Important, "\"important\"")]
    [InlineData(NotifySeverity.Critical, "\"critical\"")]
    public void NotifySeverity_serializes_as_a_camelCase_string(NotifySeverity value, string expectedJson)
        => Assert.Equal(expectedJson, JsonSerializer.Serialize(value));

    [Theory]
    [InlineData(NotifyArgKind.Magnitude, "\"magnitude\"")]
    [InlineData(NotifyArgKind.Count, "\"count\"")]
    [InlineData(NotifyArgKind.WorldTurn, "\"worldTurn\"")]
    [InlineData(NotifyArgKind.Ref, "\"ref\"")]
    [InlineData(NotifyArgKind.DomainToken, "\"domainToken\"")]
    public void NotifyArgKind_serializes_as_a_camelCase_string(NotifyArgKind value, string expectedJson)
        => Assert.Equal(expectedJson, JsonSerializer.Serialize(value));

    [Theory]
    [InlineData(NotifyDelivery.Live, "\"live\"")]
    [InlineData(NotifyDelivery.CatchUp, "\"catchUp\"")]
    public void NotifyDelivery_serializes_as_a_camelCase_string(NotifyDelivery value, string expectedJson)
        => Assert.Equal(expectedJson, JsonSerializer.Serialize(value));

    [Theory]
    [InlineData(NotifyRefKind.Sector, "\"sector\"")]
    [InlineData(NotifyRefKind.Lane, "\"lane\"")]
    [InlineData(NotifyRefKind.Faction, "\"faction\"")]
    [InlineData(NotifyRefKind.Legion, "\"legion\"")]
    [InlineData(NotifyRefKind.Structure, "\"structure\"")]
    [InlineData(NotifyRefKind.Cache, "\"cache\"")]
    public void NotifyRefKind_serializes_as_a_camelCase_string(NotifyRefKind value, string expectedJson)
        => Assert.Equal(expectedJson, JsonSerializer.Serialize(value));

    [Fact]
    public void Every_enum_round_trips_through_its_camelCase_string()
    {
        foreach (var value in Enum.GetValues<NotifySeverity>())
        {
            var json = JsonSerializer.Serialize(value);
            Assert.Equal(value, JsonSerializer.Deserialize<NotifySeverity>(json));
        }
        foreach (var value in Enum.GetValues<NotifyArgKind>())
        {
            var json = JsonSerializer.Serialize(value);
            Assert.Equal(value, JsonSerializer.Deserialize<NotifyArgKind>(json));
        }
        foreach (var value in Enum.GetValues<NotifyDelivery>())
        {
            var json = JsonSerializer.Serialize(value);
            Assert.Equal(value, JsonSerializer.Deserialize<NotifyDelivery>(json));
        }
        foreach (var value in Enum.GetValues<NotifyRefKind>())
        {
            var json = JsonSerializer.Serialize(value);
            Assert.Equal(value, JsonSerializer.Deserialize<NotifyRefKind>(json));
        }
    }

    [Fact]
    public void NotificationDto_round_trips_with_a_typed_arg()
    {
        var dto = new NotificationDto
        {
            Seq = 42,
            Rev = 7,
            DedupKey = "world:1:t3:e0",
            Category = "loam.shortfall",
            Severity = NotifySeverity.Important,
            SourceId = "world-turn",
            MessageKey = "world.turn-entry",
            Args = new List<NotifyArgDto>
            {
                new()
                {
                    Name = "sector",
                    Kind = NotifyArgKind.Ref,
                    Value = JsonSerializer.SerializeToElement(new { refKind = "sector", id = "s-1" }),
                },
            },
            SubjectKey = "sector:s-1",
            WorldId = "w-1",
            WorldTurn = 3,
            State = "unread",
            CreatedUtc = "2026-09-19T00:00:00Z",
        };

        var json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<NotificationDto>(json)!;

        Assert.Equal(dto.Seq, back.Seq);
        Assert.Equal(dto.Rev, back.Rev);
        Assert.Equal(dto.DedupKey, back.DedupKey);
        Assert.Equal(dto.Category, back.Category);
        Assert.Equal(dto.Severity, back.Severity);
        Assert.Equal(dto.MessageKey, back.MessageKey);
        Assert.Equal(dto.WorldTurn, back.WorldTurn);
        var arg = Assert.Single(back.Args);
        Assert.Equal(NotifyArgKind.Ref, arg.Kind);
        Assert.Equal("sector", arg.Value.GetProperty("refKind").GetString());
        Assert.Contains("\"severity\":\"important\"", json);
        Assert.Contains("\"kind\":\"ref\"", json);
    }

    [Fact]
    public void NotificationBatchDto_carries_delivery_and_items()
    {
        var batch = new NotificationBatchDto
        {
            PlayerId = 9,
            Delivery = NotifyDelivery.CatchUp,
            Items = new List<NotificationDto> { new() { Seq = 1, Rev = 1 } },
        };

        var json = JsonSerializer.Serialize(batch);
        Assert.Contains("\"delivery\":\"catchUp\"", json);
        var back = JsonSerializer.Deserialize<NotificationBatchDto>(json)!;
        Assert.Equal(9, back.PlayerId);
        Assert.Equal(NotifyDelivery.CatchUp, back.Delivery);
        Assert.Single(back.Items);
    }

    [Fact]
    public void NotificationStateChangedDto_round_trips_its_changes()
    {
        var dto = new NotificationStateChangedDto
        {
            PlayerId = 3,
            State = "read",
            Changes = new List<NotificationStateChangeDto> { new() { Seq = 5, Rev = 2 } },
        };

        var back = JsonSerializer.Deserialize<NotificationStateChangedDto>(JsonSerializer.Serialize(dto))!;
        Assert.Equal(3, back.PlayerId);
        Assert.Equal("read", back.State);
        Assert.Equal(5, Assert.Single(back.Changes).Seq);
    }

    [Fact]
    public void NotificationEvents_names_the_two_SignalR_events()
    {
        Assert.Equal("NotificationBatch", NotificationEvents.Batch);
        Assert.Equal("NotificationStateChanged", NotificationEvents.StateChanged);
    }
}
