using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures.Fusion;

/// <summary>
/// Task 8 (spec-species-rank.md §6): the per-gate rank floors. Every gate ships at the BOTTOM rung —
/// that is what makes landing rank move zero goldens — so this file proves both halves: the shipped
/// tuning is a pass-through, and a floor tuned above bottom genuinely narrows the enforcing site.
/// The four other gates (`expeditionWildBand`, `waveBand`, `cageEligibility`) share one policy and
/// land with their own rows (Tasks 10-12); the two fusion gates are wired here.
/// </summary>
public class FusionRankFloorTests
{
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    static string ReadTuning(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

    static readonly IReadOnlyList<string> RankIds =
        CreatureRarityLadder.All.Select(r => r.ToId()).ToList();

    static CreatureRankTuning RealRankTuning() => CreatureRankTuningLoader.Parse(
        ReadTuning("data", "tuning", "creature-rank.v1.json"),
        CreatureThreatTuningLoader.Parse(ReadTuning("data", "tuning", "creature-threat.v2.json")).RungIds,
        RankIds);

    /// <summary>A tuning whose every declared gate sits at <paramref name="floor"/> — the shape a
    /// balance pass would publish once a gate is deliberately raised.</summary>
    static CreatureRankTuning FloorsAt(CreatureRank floor) => new(
        1, RankIds, Array.Empty<CreatureRankCell>(),
        CreatureRankFloors.DeclaredGates.ToDictionary(g => g, _ => floor.ToId(), StringComparer.Ordinal));

    sealed class FloorScope : IDisposable
    {
        public FloorScope(CreatureRankTuning tuning) => CreatureRankFloors.Configure(tuning);
        public void Dispose() => CreatureRankFloors.ResetToUnconfigured();
    }

    sealed class RosterScope : IDisposable
    {
        readonly IDisposable _scoped;
        public RosterScope(IReadOnlyList<CreatureSpeciesDef> roster) =>
            _scoped = CreatureSpeciesCatalog.UseScoped(roster);
        public void Dispose() => _scoped.Dispose();
    }

    [Fact]
    public void The_shipped_floors_are_the_bottom_rung_and_pass_every_rung_and_null()
    {
        using var floors = new FloorScope(RealRankTuning());

        foreach (var gate in CreatureRankFloors.DeclaredGates)
        {
            Assert.Equal(CreatureRank.Chaff, CreatureRankFloors.FloorFor(gate));
            Assert.True(CreatureRankFloors.Passes(gate, null)); // a skipped rank maps to bottom
            foreach (var rank in CreatureRankLadder.All)
                Assert.True(CreatureRankFloors.Passes(gate, rank));
        }
    }

    [Fact]
    public void An_unconfigured_policy_is_the_same_pass_through_as_a_bottom_floor()
    {
        // The two cases are the same behavior by construction — that is what makes the shipped
        // default a pass-through rather than an "optional seam" that silently does nothing.
        CreatureRankFloors.ResetToUnconfigured();

        foreach (var gate in CreatureRankFloors.DeclaredGates)
        {
            Assert.False(CreatureRankFloors.IsConfigured);
            Assert.Equal(CreatureRank.Chaff, CreatureRankFloors.FloorFor(gate));
            Assert.True(CreatureRankFloors.Passes(gate, null));
        }
    }

    [Fact]
    public void The_declared_gate_vocabulary_is_the_tuning_files_own_gate_ids()
    {
        // A gate whose id drifts from the file must fail LOUDLY rather than silently becoming a
        // bottom-rung pass-through, so the closed vocabulary is asserted against the declaring list.
        Assert.Equal(CreatureRankTuning.GateIds, CreatureRankFloors.DeclaredGates);
    }

    [Fact]
    public void A_floor_above_bottom_narrows_EligibleOutputs_and_null_maps_to_bottom()
    {
        var roster = new[]
        {
            SyntheticSpecies.Make("test.ranked-high", CreatureRarity.Cultivated,
                CreatureAcquisition.Summonable, CreatureSpeciesCatalog.CreatureTypeIdFloor + 1,
                rank: CreatureRank.Heirloom),
            SyntheticSpecies.Make("test.ranked-low", CreatureRarity.Cultivated,
                CreatureAcquisition.Summonable, CreatureSpeciesCatalog.CreatureTypeIdFloor + 2,
                rank: CreatureRank.Chaff),
            SyntheticSpecies.Make("test.rank-skipped", CreatureRarity.Cultivated,
                CreatureAcquisition.Summonable, CreatureSpeciesCatalog.CreatureTypeIdFloor + 3),
            // rank null — the pipeline skipped it
        };

        using var rosterScope = new RosterScope(roster);
        using var floors = new FloorScope(FloorsAt(CreatureRank.Heirloom));

        var eligible = CreatureRecipeCatalog.EligibleOutputs();

        Assert.Equal(new[] { "test.ranked-high" }, eligible.Select(s => s.SpeciesId));
        // The null case is decided by the same line, at the same gate: bottom < Heirloom, so a skipped
        // rank does not clear a raised floor.
        Assert.False(CreatureRankFloors.Passes(CreatureRankFloors.FusionRecipeEligibility, null));
        Assert.True(CreatureRankFloors.Passes(CreatureRankFloors.FusionRecipeEligibility, CreatureRank.Heirloom));
    }

    [Fact]
    public void A_bottom_floor_admits_the_same_roster_including_a_skipped_rank()
    {
        var roster = new[]
        {
            SyntheticSpecies.Make("test.b-ranked", CreatureRarity.Cultivated,
                CreatureAcquisition.Summonable, CreatureSpeciesCatalog.CreatureTypeIdFloor + 11,
                rank: CreatureRank.Chaff),
            SyntheticSpecies.Make("test.b-skipped", CreatureRarity.Cultivated,
                CreatureAcquisition.Summonable, CreatureSpeciesCatalog.CreatureTypeIdFloor + 12),
            SyntheticSpecies.Make("test.b-capture-only", CreatureRarity.Cultivated,
                CreatureAcquisition.CaptureOnly, CreatureSpeciesCatalog.CreatureTypeIdFloor + 13,
                rank: CreatureRank.Chaff),
        };

        using var rosterScope = new RosterScope(roster);
        using var floors = new FloorScope(RealRankTuning());

        var eligible = CreatureRecipeCatalog.EligibleOutputs();

        // The rarity floor and the capture-only rule still decide; rank adds nothing at bottom.
        Assert.Equal(new[] { "test.b-ranked", "test.b-skipped" }, eligible.Select(s => s.SpeciesId));
    }

    [Fact]
    public void Configure_refuses_a_tuning_that_does_not_answer_every_declared_gate()
    {
        var partial = new CreatureRankTuning(
            1, RankIds, Array.Empty<CreatureRankCell>(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CreatureRankFloors.FusionPromotion] = "chaff",
            });

        Assert.Throws<CreatureRankTuningRejection>(() => CreatureRankFloors.Configure(partial));
        CreatureRankFloors.ResetToUnconfigured();
    }
}
