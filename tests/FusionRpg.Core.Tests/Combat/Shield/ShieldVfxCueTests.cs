using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Shield;

/// <summary>T14 — shield.broken cue id registered with a valid catalog recipe.</summary>
public class ShieldVfxCueTests
{
    [Fact]
    public void Shield_broken_cue_has_a_registered_recipe()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());
        Assert.True(catalog.TryGet(VfxCueIds.ShieldBroken, out var recipe));
        Assert.NotEmpty(recipe.Primitives);
    }

    [Fact]
    public void Shield_broken_cue_id_matches_event_kind()
    {
        // Cue rides the event kind so the emit site needs no mapping table.
        Assert.Equal(FusionRpg.Core.Combat.Shield.ShieldEventKinds.Broken, VfxCueIds.ShieldBroken);
    }
}
