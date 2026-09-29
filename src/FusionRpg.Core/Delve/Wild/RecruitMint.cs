using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Wild;

/// <summary>
/// D4.5 (spec-wild-room.md §4, "Recruit minting and teleport-home") — assembles the
/// <see cref="CreatureMintSpec"/> a wild join (or a §5 capture, which spec §5 says mints "the same
/// way") hands to the already-shipped `MintCreatureUnlocked` — the one mint every acquisition path uses
/// (summons `RpgStore.Summons.cs:104`, expeditions `RpgStore.Expeditions.cs:344`, fusion
/// `RpgStore.Fusion.cs:218`). This file only DECIDES what to mint ("Core decides, Data writes") —
/// actually calling `MintCreatureUnlocked` with the spec this returns is D4.8's own, still-unbuilt,
/// wiring job.
///
/// <para><b>The named gap</b> (spec, verbatim: <c>Level = θ_enemy = Θ_room + thetaOffset</c>):
/// <see cref="CreatureMintSpec"/> (`FusionRpg.Contracts`, `CreatureDtos.cs:58-70`) has **no `Level` field
/// today** — confirmed by reading the type directly; every existing mint caller
/// (`RpgStore.Fusion.cs:218`, `RpgStore.Summons.cs:104`) hard-codes level 1 through
/// `MintCreatureUnlocked`'s own INSERT, and this file is no different — <paramref name="thetaEnemy"/>
/// is accepted (not silently dropped) so this function's own callers already pass the value a future
/// `Level` field will need; once `CreatureMintSpec.Level` lands (filed, additive, on
/// `creature-system-map.md`: `long? Level`, `$level = spec.Level ?? 1`), only this function's BODY
/// changes — never its signature, never a caller.</para>
/// </summary>
public static class RecruitMint
{
    /// <summary>
    /// Identity copies straight from the pack's own <see cref="ConcreteSpecies"/> row (spec, verbatim:
    /// "identity from the pack's `ConcreteSpecies` row, `Rarity = BaseRarity`" — `ConcreteSpecies.Rarity`
    /// already IS the species' base rarity, no separate roll). `traitIds` is the caller's own
    /// already-rolled `SummonRoller.RollTraits(...)` result (a fresh roll, not a copy, so it is not
    /// read off <paramref name="species"/>); `origin` is `"delve"` for a wild join or `"capture"` for
    /// §5 — this function stays reusable by both rather than hardcoding one. `Variant` is left at
    /// `CreatureMintSpec`'s own default (`"normal"`) — the spec's own "recruit's spec" list never names a
    /// variant roll for this path, unlike summons/fusion, so nothing is invented here.
    /// </summary>
    public static CreatureMintSpec Build(ConcreteSpecies species, IReadOnlyList<string> traitIds, string origin, int thetaEnemy)
    {
        if (species is null) throw new ArgumentNullException(nameof(species));
        if (traitIds is null) throw new ArgumentNullException(nameof(traitIds));
        if (string.IsNullOrWhiteSpace(origin)) throw new ArgumentException("origin required", nameof(origin));

        _ = thetaEnemy; // GAP: CreatureMintSpec has no Level field yet -- see this file's own doc comment.

        return new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = species.Rarity.ToId(),
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = traitIds.ToList(),
            Origin = origin,
        };
    }
}
