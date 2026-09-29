using FusionRpg.Contracts;

namespace FusionRpg.Core.Vfx;

/// <summary>Bridges the legacy Funnel present shape to cues — vfx-ssot.md §5, §13 phase 1.</summary>
public static class VfxCueMapper
{
    public static VfxCueDto FromDamageFx(DamageFxDto fx)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        return new VfxCueDto
        {
            CueId = fx.Tag == DamageFxTag.Heal ? VfxCueIds.CombatHeal : VfxCueIds.CombatHit,
            TargetPtr = fx.TargetPtr,
            Amount = fx.Amount,
            Tag = fx.Tag,
            Elements = fx.Elements
        };
    }
}
