using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>
    /// zomboss-deploy-ai T3.4, widened by save-identity SE4.22 ("Zomboss stops being a player row") —
    /// mints a fresh specimen of <paramref name="speciesId"/> owned by <paramref name="owner"/> (a save's
    /// empire), reusing the EXACT same mint mapping the original `MintForZomboss` established (and
    /// `ExecuteSummon` shares, `RpgStore.Summons.cs`): `Side`/`GameTypeId`/`ElementPrimary`/
    /// `ElementSecondary` straight off the catalog def, `Rarity` = the species' own `BaseRarity`,
    /// `TraitIds` via `SummonRoller.RollTraits` — the same shared trait-roll summons and wild joins
    /// already use, not a third trait-rolling scheme invented here. `origin: "zomboss"` stays: it names
    /// where the specimen came from (provenance), not who owns it now.
    ///
    /// <para>Replaces <c>MintForZomboss</c>: no player row represents Zomboss any more (<c>
    /// EnsureZombossPlayer</c>/<c>ZombossPlayerName</c> are deleted). The specimen mints straight onto
    /// <paramref name="owner"/>'s own save — the caller (<c>ZombossDeployEndpoints</c>) resolves that
    /// save from the match BEFORE calling this, so a deploy whose match resolves to no run never reaches
    /// here at all.</para>
    /// </summary>
    public CreatureSpecimenDto MintForEmpire(EmpireRef owner, string speciesId, ulong seed)
    {
        var species = Core.Creatures.CreatureSpeciesCatalog.Get(speciesId);
        var traits = SummonRoller.RollTraits(species, species.BaseRarity, SeededRng.DeriveStream(seed, "zomboss-deploy-ai:mint"));
        var (specimen, _) = MintCreatureForEmpire(owner, new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = traits.ToList(),
            Origin = "zomboss",
        });
        return specimen;
    }
}
