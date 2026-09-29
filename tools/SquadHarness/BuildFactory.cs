using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §1: "An actor build is constructed with the SAME corner-shape helper the duel
/// sweep uses -- spike `k` aptitudes, floor the other `12 - k` at `4167` per-mille
/// (`HybridViability/Program.cs:79-95`)." This is that helper, copied verbatim (same roster order, same
/// <see cref="Floor"/>/<see cref="Total"/> constants) rather than re-derived, so both the duel roster and
/// the squad roster are provably built from ONE shape -- the acceptance bar F1 must clear: "the duel
/// roster is proven to be gk-core/tools/HybridViability's same 91 builds by CONSTRUCTING them, never by
/// asserting the number 91."
/// </summary>
public static class BuildFactory
{
    /// <summary>Same twelve, same order, as gk-core/tools/HybridViability, gk-forge/tools/DominanceBaseline and
    /// gk-core/tools/ResidualFitLoop -- kept identical so a drift in one is a diff against the others.</summary>
    public static readonly IReadOnlyList<string> Roster = new[]
    {
        "Might", "Fortitude", "Vigor", "Onslaught", "Agility", "Composure",
        "Pierce", "Focus", "Bulwark", "Retribution", "Precision", "Ferocity",
    };

    /// <summary>BestResponse.DominanceMatrix's fixed corner-shape constant (100/roster.Length/2,
    /// per-mille). A bounded ratio (0..100_000), not a magnitude -- exempt from the long-ceiling rule,
    /// and structural (it defines what "a corner" means, never a balance dial a pass would retune).</summary>
    public const long Floor = 4167;

    /// <summary>The whole allocation budget this shape spends, per-mille of itself (structural, same
    /// reason as <see cref="Floor"/>).</summary>
    public const long Total = 100_000;

    /// <summary>
    /// Spread <see cref="Total"/> over <paramref name="spikeIds"/> evenly, flooring every other
    /// aptitude at <see cref="Floor"/> -- the corner shape generalised from one spike to k. k=1
    /// reproduces DominanceBaseline's Corner() exactly; k=2/3 are the hybrid2/hybrid3 shapes.
    /// </summary>
    public static AptitudeAllocation Build(params string[] spikeIds)
    {
        if (spikeIds.Length == 0) throw new ArgumentException("must spike at least one aptitude", nameof(spikeIds));
        var spikeBudget = Total - Floor * (Roster.Count - spikeIds.Length);
        var each = spikeBudget / spikeIds.Length;
        var remainder = spikeBudget - each * spikeIds.Length; // integer split: give the rest to the first
        return Roster.Aggregate(AptitudeAllocation.Empty, (acc, id) =>
        {
            var idx = Array.IndexOf(spikeIds, id);
            var pts = idx < 0 ? Floor : each + (idx == 0 ? remainder : 0);
            return acc + AptitudeAllocation.Single(AllocationScope.Commander, id, pts);
        });
    }

    /// <summary>The "even12" / "spread" build -- every aptitude gets an equal share of
    /// <see cref="Total"/>. Its own method (not a 12-argument <see cref="Build"/> call) because the
    /// even split is exact division with no remainder-to-first-spike rule, matching
    /// <c>HybridViability/Program.cs</c>'s own separate construction line for it.</summary>
    public static AptitudeAllocation EvenSpread() =>
        Roster.Aggregate(AptitudeAllocation.Empty,
            (acc, id) => acc + AptitudeAllocation.Single(AllocationScope.Commander, id, Total / Roster.Count));
}
