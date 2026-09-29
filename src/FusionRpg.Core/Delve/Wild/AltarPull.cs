using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Wild;

/// <summary>Named rule ids this module raises (§9's own pattern) — refused rather than a fallback.</summary>
public static class AltarRefusal
{
    /// <summary>Spec §9: "a band not in `disposition.v1.json`" has its own sibling — this is the
    /// altar's: `altar.bannerId` names no real row in `SummonBannerCatalog`.</summary>
    public const string BannerUnknown = "altar.banner-unknown";
}

/// <summary>
/// D4.7 (spec-wild-room.md §6, "Altar pulls as at-risk haul") — one pull on the already-shipped
/// `SummonRoller`, exactly as the spec's own words: "`SummonRoller.Roll(banner, focus, count: 1,
/// pity, rng)`… v1 sells single pulls, a ten-pull is ask-first." `count` is not a parameter of this
/// function at all — there is no way to call it with anything but 1, so the ask-first line for a
/// ten-pull needs no refusal check here; it simply cannot be reached through this API.
///
/// <para>This file touches none of `SummonRoller`'s rates or pity math (spec: "`SummonRoller`'s
/// rates and pity are untouched") — it is a thin pass-through, matching this program's own
/// `SupplyInstantiation.Concrete` shape (D3.25): resolve the banner, call the shipped roller, hand
/// back exactly what it returns. `altar.poolFromDomain` staying `false` (spec: "inert… a domain pool
/// is 'one filter argument, not a new roller'") means <see cref="SummonRoller.Roll"/> is called with
/// no pool filter at all — there is no optional trailing parameter to pass one through yet, so
/// nothing here can accidentally wire it early.</para>
///
/// <para><b>Out of scope, named rather than silently built:</b> pricing (`PullPrice`/`SpendUnbanked`,
/// already shipped — D3.13/D3.16), the haul-row shape written into `parties_json[p].haul[]`, the
/// pity read/write against `rpg_summon_pity`, and minting the pull at `CloseDelve(Extracted)` are ALL
/// explicitly D4.8's own job ("Cage, refusals, **store** and endpoints") — this file returns the
/// roller's own result and updated pity; it performs no SQL write and owns no store shape.</para>
/// </summary>
public static class AltarPull
{
    /// <summary>
    /// Resolves <paramref name="altarBannerId"/> through <see cref="SummonBannerCatalog"/> and rolls
    /// exactly one pull. <paramref name="rng"/> is the caller's own already-derived stream — spec:
    /// "the `rng` is `dungeon:altar:{r}:{c}:{n}` off the delve seed… replay-safe" — deriving that
    /// exact name is the caller's job (D4.8), not this file's.
    /// </summary>
    public static bool TryPull(
        string altarBannerId, ElementTypeId? focusElement, PityState pity, SeededRng rng,
        out SummonRollResult result, out PityState newPity, out string? refusalId)
    {
        if (pity is null) throw new ArgumentNullException(nameof(pity));

        var banner = SummonBannerCatalog.TryGet(altarBannerId);
        if (banner is null)
        {
            result = null!;
            newPity = pity;
            refusalId = AltarRefusal.BannerUnknown;
            return false;
        }

        var (results, pityAfter) = SummonRoller.Roll(banner, focusElement, count: 1, pity, rng);
        result = results[0];
        newPity = pityAfter;
        refusalId = null;
        return true;
    }
}
