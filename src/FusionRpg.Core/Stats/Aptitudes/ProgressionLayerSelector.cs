using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// `species-progression` module 1, `layer-source-selector` (map C1) — which progression layers an
/// actor carries. **2a and 2b are mutually exclusive by construction**: the record has one slot for
/// "whose points", never two — the exact shape the map C1 defect violated (a unique specimen composing
/// both its own allocation AND its empire's species allocation).
/// </summary>
public sealed record ProgressionLayers(
    EmpireId Empire,          // the army it fights for -- the commander term's owner
    bool CarriesCommander,    // every empire carries a commander term (R23/R4 mirrored); the VALUE says if it is empty
    ProgressionOwner Owner);  // exactly one of: Specimen(instanceId) | Species(speciesId) | None

/// <summary>Which layer 2 an actor's points come from -- a specimen's own roll (2a), an empire's
/// species-wide allocation (2b), or neither. A closed vocabulary: one owner shape per actor, never
/// both at once.</summary>
public abstract record ProgressionOwner
{
    public sealed record Specimen(string InstanceId) : ProgressionOwner;   // layer 2a
    public sealed record Species(string SpeciesId) : ProgressionOwner;     // layer 2b, read against Empire
    public sealed record None : ProgressionOwner;                          // no species resolved
}

/// <summary>
/// The one function every compose path asks instead of re-deriving the rule (map C1's fix: a copied
/// rule drifts, three copies existed, one of them was wrong). Decides WHICH layer applies, never HOW
/// MUCH -- values still come from the existing loaders (`LoadAllocationUnlocked`,
/// `EffectiveSpeciesAllocationUnlocked`, the injector caches); this type touches none of them.
/// </summary>
public static class ProgressionLayerSelector
{
    /// <summary>
    /// The mechanism declares the source (decisions.md "Creature progression source"): never inferred
    /// from a typeId. <paramref name="empire"/> is the actor's empire -- for a general, from the ONE
    /// side mapping (`SpeciesAllocation.EmpireForSide` / `KillAttribution.EmpireOf`); for a specimen,
    /// its OWNER empire (`rpg_unique_actors.empire_id`, save-identity G5) -- never inferred from its
    /// side.
    ///
    /// <para><b><paramref name="humanEmpire"/> no longer discriminates</b> (R23, `ai-empire-species`
    /// EP4.18): `CarriesCommander` used to be `empire == humanEmpire`, which made the commander term
    /// human-only by construction. R23 mirrors the player's pool onto Zomboss's empire (R4's side-wide
    /// rule, mirrored), so EVERY empire now carries the term and WHICH empire's pool it is already rides
    /// on <see cref="ProgressionLayers.Empire"/>; whether that pool is empty is the pool read's answer, not
    /// this rule's. The parameter stays in the signature because every call site already names the save's
    /// human empire and a future per-empire policy will need the same input.</para>
    /// </summary>
    public static ProgressionLayers Select(
        CreatureProgressionSource source, EmpireId empire, EmpireId humanEmpire) =>
        source switch
        {
            CreatureProgressionSource.UniqueSpecimenSource u =>
                new(empire, true, new ProgressionOwner.Specimen(u.InstanceId)),
            CreatureProgressionSource.EmpireGeneralSource g =>
                new(empire, true, new ProgressionOwner.Species(g.SpeciesId)),
            CreatureProgressionSource.CommanderSource =>
                new(empire, true, new ProgressionOwner.None()),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "unknown progression source"),
        };
}
