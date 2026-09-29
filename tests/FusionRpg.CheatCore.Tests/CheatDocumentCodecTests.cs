using FusionRpg.CheatCore;
using Xunit;

namespace FusionRpg.CheatCore.Tests;

public class CheatDocumentCodecTests
{
    [Fact]
    public void CheatDocumentCodec_roundTrip_skips_identity()
    {
        var entries = new (string id, bool enabled, double floatValue, string? kind)[]
        {
            ("A-P-HP%", true, 1d, "slider"),
            ("A-P-ATK%", true, 2d, "slider"),
            ("A-P-HP+", true, 0d, "number"),
            ("E-ZH", true, 1d, "slider"),
            ("P-HP", true, 900d, "number"),
            ("P-GOD", true, 0d, "toggle")
        };
        var doc = CheatDocumentCodec.FromEntries(7, "test", entries, "2026-01-01T00:00:00Z");
        Assert.Equal(7, doc.Revision);
        Assert.Equal("test", doc.Source);
        Assert.DoesNotContain(doc.Mods, m => m.Id == "A-P-HP%");
        Assert.DoesNotContain(doc.Mods, m => m.Id == "A-P-HP+");
        Assert.DoesNotContain(doc.Mods, m => m.Id == "E-ZH");
        Assert.Contains(doc.Mods, m => m.Id == "A-P-ATK%" && m.Value == 2d);
        Assert.Contains(doc.Mods, m => m.Id == "P-HP" && m.Value == 900d);
        Assert.Contains(doc.Mods, m => m.Id == "P-GOD" && m.Enabled);

        var back = CheatDocumentCodec.ToEntries(doc);
        Assert.DoesNotContain(back, e => e.id == "A-P-HP%");
        Assert.Contains(back, e => e.id == "A-P-ATK%" && e.floatValue == 2d);
        Assert.Contains(back, e => e.id == "P-HP" && e.floatValue == 900d);
        Assert.Contains(back, e => e.id == "P-GOD" && e.enabled);
    }

    [Fact]
    public void PresentRules_scale_absolute_board()
    {
        Assert.False(CheatPresentRules.HasNonIdentityScale(false, "A-P-HP%", 2));
        Assert.False(CheatPresentRules.HasNonIdentityScale(true, "A-P-HP%", 1));
        Assert.True(CheatPresentRules.HasNonIdentityScale(true, "A-P-HP%", 2));
        Assert.False(CheatPresentRules.HasNonIdentityScale(true, "A-P-HP+", 0));
        Assert.True(CheatPresentRules.HasNonIdentityScale(true, "A-P-HP+", 5));

        Assert.False(CheatPresentRules.ShouldApplyAbsolute(false, 100));
        Assert.False(CheatPresentRules.ShouldApplyAbsolute(true, 0));
        Assert.True(CheatPresentRules.ShouldApplyAbsolute(true, 50));

        Assert.False(CheatPresentRules.ShouldApplyBoardField(false));
        Assert.True(CheatPresentRules.ShouldApplyBoardField(true));
    }
}
