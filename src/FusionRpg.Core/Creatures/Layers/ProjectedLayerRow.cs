using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures.Layers;

/// <summary>
/// species-progression SP3.4 (spec-species-layer-projector.md) — the projector's one output row
/// shape, shared by 1a, 1b and 2b. A value is either resolved now (a fixed atom amount or a Θ-free
/// contest read) or Θ-free until the reader supplies `P(Θ)` (a magnitude-read ladder coefficient) —
/// never a pre-resolved `double` for the magnitude case, since that would bake in a Θ nobody has read
/// yet.
/// </summary>
public abstract record LayerValue
{
    /// <summary>Resolved now: contest reads (bounded, Θ-free by PS-3) and 1a/1b's own fixed atom
    /// amounts (never rolled, never level-derived).</summary>
    public sealed record Fixed(double Amount) : LayerValue;

    /// <summary>Θ-free until <see cref="SpeciesLayerProjector.Resolve"/> supplies `P(Θ)` via
    /// <see cref="Core.Power.LadderScale.Micro"/> — the same function the live aptitude read and the
    /// projected-atom compile path both call (map C6).</summary>
    public sealed record LadderMicro(long KMicro) : LayerValue;
}

/// <summary>One projected row: a channel, how it composes, its value, and who produced it (the
/// GG-49 SourceId <see cref="Stats.Derived.ContributionSourceIds"/> minted it with).</summary>
public sealed record ProjectedLayerRow(string Channel, DerivedModifierOp Op, LayerValue Value, string SourceId);
