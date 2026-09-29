using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

public class StatusContagionTests
{
    [Fact]
    public void Blight_pulse_spreads_to_row_neighbor()
    {
        var h = new FoundationHarness();
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "P1", Side = "plant", TypeId = 0, Col = 2, Row = 2 },
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 5, Row = 2 },
            new BoardEntitySnap { Ptr = "Z2", Side = "zombie", TypeId = 0, Col = 6, Row = 2 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "blight",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["statusId"] = "blight",
                ["amount"] = -12L,
                ["icd_ms"] = 0,
                ["periodMs"] = 1000,
                ["durationMs"] = 5000,
                ["spread"] = new Dictionary<string, object?>
                {
                    ["chance"] = 1.0,
                    ["maxHops"] = 2,
                    ["icd_ms"] = 0,
                    ["statusId"] = "blight",
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["mode"] = TargetModes.Area,
                        ["shape"] = AreaShapes.Row,
                        ["anchor"] = "EventTarget",
                        ["filters"] = new Dictionary<string, object?> { ["side"] = "zombie" }
                    }
                }
            }
        });

        h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant"
        });
        Assert.NotEmpty(h.Bag.Status!.ForHost("Z1"));
        Assert.Empty(h.Bag.Status.ForHost("Z2"));

        h.AdvanceTime(1000);
        Assert.NotEmpty(h.Bag.Status.ForHost("Z2"));
    }
}
