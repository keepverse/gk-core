using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.13 — the run/match → save mapping is total where it can be and **null** where it
/// cannot: a fact whose run resolves to no save awards nothing, and is never credited to the save that
/// happens to be current.
/// </summary>
[Trait("VerificationId", "data.save-empires")]
public class SaveOfRunTests
{
    static RpgStore Fresh() => DataTestStore.Create().Store;

    [Fact]
    public void An_unknown_run_resolves_to_no_save_never_the_current_one()
    {
        var store = Fresh();
        Assert.True(store.IsLiveSave(1));           // a current save exists ...
        Assert.Null(store.SaveOfRun(999_999));      // ... and an unknown run still resolves to nothing
    }

    [Fact]
    public void An_unknown_or_blank_match_resolves_to_no_save_never_the_current_one()
    {
        var store = Fresh();
        Assert.True(store.IsLiveSave(1));
        Assert.Null(store.SaveOfMatch("no-such-match"));
        Assert.Null(store.SaveOfMatch(null));
        Assert.Null(store.SaveOfMatch(""));
    }
}
