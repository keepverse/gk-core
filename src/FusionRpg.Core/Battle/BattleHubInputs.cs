using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Items.Consumables;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Battle;

/// <summary>
/// battle-hub-fuse T5 — the Hub inputs a setup carries into battle, resolved by the builders that
/// own the stores (squad / encounter / expedition) instead of pre-folded <c>ChannelMods</c>.
/// Every member is optional and null by default: a setup without inputs composes baseline +
/// affinity + traits + tempo only, exactly like a ChannelMods-free setup composes today.
/// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> on the setup property keeps
/// expedition golden hashes byte-identical (builders attach inputs after tier resolution).
/// </summary>
public sealed record BattleHubInputs
{
    /// <summary>Commanders + unique/specimen (squad) or pattern (zomboss/kit) allocation.</summary>
    public AptitudeAllocation? Aptitude { get; init; }

    /// <summary>Equip + tree bound <c>stat.derived</c> atoms, Server-resolved.</summary>
    public IReadOnlyList<BoundDerivedAtom>? BoundAtoms { get; init; }

    /// <summary>Durable star/loyalty for the actor.</summary>
    public StarLoyaltyContribution? StarLoyalty { get; init; }

    /// <summary>Run-start draught manifest (per-squad: every member receives every mod).</summary>
    public IReadOnlyList<DraughtMod>? Draughts { get; init; }

    /// <summary>Expedition injury counts by actor key (same keys <c>ApplyInjuries</c> reads).</summary>
    public IReadOnlyDictionary<string, int>? Injuries { get; init; }

    /// <summary>species-progression `species-layer-delivery` step 6.2 (SP6.7) — a unique's 1a
    /// (species-passive core) + 1b (a fusion pick) rows, of its OWNER empire — never 2b, and never
    /// merged with <see cref="Aptitude"/> (module 6's per-layer split, step 6.1). Builders (world-turn,
    /// web-squad) resolve which rows via `layer-source-selector`, the SAME rule <see cref="Aptitude"/>'s
    /// own owner resolution already uses.</summary>
    public IReadOnlyList<ProjectedLayerRow>? SpeciesLayers { get; init; }
}
