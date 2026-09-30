using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// species-gear-chain T5: the admission rule is one declaring site with a member per context.
/// The matrix below pins the per-flag reading — including the population-luck cases no shipped
/// species covers (Summonable|EventOnly must still refuse: a plain HasFlag(Summonable) would admit
/// it). Flags are a CLOSED VOCABULARY the code owns (validation-ssot.md §1).
/// </summary>
public class CreatureAdmissionTests
{
    static CreatureSpeciesDef Species(CreatureAcquisition acquisition) =>
        new() { SpeciesId = "test", Acquisition = acquisition };

    [Theory]
    //                     wave  wild  delve
    [InlineData(1,          true, true, true)]   // Summonable: the ordinary roster
    [InlineData(2,          false, true, true)]  // CaptureOnly: admitted only where it is caught
    [InlineData(4,          false, false, false)] // EventOnly: refused everywhere, first
    [InlineData(0,          false, false, false)] // None: admitted nowhere
    [InlineData(3,          true, true, true)]   // Summonable|CaptureOnly: admitted (2 shipped)
    [InlineData(5,          false, false, false)] // Summonable|EventOnly: refused BY RULE (0 shipped)
    [InlineData(6,          false, false, false)] // CaptureOnly|EventOnly: refused BY RULE (0 shipped)
    [InlineData(7,          false, false, false)] // all flags: EventOnly still refuses first
    public void Admission_matrix(int flags, bool wave, bool wild, bool delve)
    {
        var s = Species((CreatureAcquisition)flags);
        Assert.Equal(wave, CreatureAdmission.ForWave(s));
        Assert.Equal(wild, CreatureAdmission.ForWildMap(s));
        Assert.Equal(delve, CreatureAdmission.ForDelve(s));
    }

    [Fact]
    public void ForDelve_matches_ForWildMap_by_decision_not_coincidence()
    {
        // If ForDelve ever diverges from the map rule, it does so as its own reviewed member —
        // this pins the decided equality across the whole flag space, not just shipped combos.
        for (var flags = 0; flags < 8; flags++)
        {
            var s = Species((CreatureAcquisition)flags);
            Assert.Equal(CreatureAdmission.ForWildMap(s), CreatureAdmission.ForDelve(s));
        }
    }

    // ── CS13: R-CS3/R-CS4's `speciesKind: "excluded"` mark ───────────────────────────────────────

    static CreatureSpeciesDef Marked(CreatureAcquisition acquisition, string? speciesKind) =>
        new() { SpeciesId = "test", Acquisition = acquisition, SpeciesKind = speciesKind ?? "creature" };

    [Fact]
    public void A_planted_excluded_species_is_refused_in_every_context_whatever_its_flags_say()
    {
        // Planted, not population-luck: the twelve shipped rows are `Summonable`, so the FLAG admits
        // them and only the mark refuses them. This is the case the corpus cannot fail on.
        foreach (var flags in new[] { CreatureAcquisition.Summonable, CreatureAcquisition.CaptureOnly,
                                      CreatureAcquisition.Summonable | CreatureAcquisition.CaptureOnly })
        {
            var s = Marked(flags, CreatureAdmission.ExcludedKind);
            Assert.False(CreatureAdmission.ForWave(s));
            Assert.False(CreatureAdmission.ForWildMap(s));
            Assert.False(CreatureAdmission.ForDelve(s));
        }
    }

    [Fact]
    public void An_unmarked_species_reads_as_creature_and_is_admitted_by_its_flags()
    {
        // The mark REMOVES species from play, so an absent one must never imply exclusion.
        Assert.True(CreatureAdmission.ForWave(Marked(CreatureAcquisition.Summonable, null)));
        Assert.True(CreatureAdmission.ForWave(Marked(CreatureAcquisition.Summonable, "")));
        Assert.True(CreatureAdmission.ForWave(Marked(CreatureAcquisition.Summonable, "creature")));
        Assert.False(CreatureAdmission.IsExcluded(Marked(CreatureAcquisition.Summonable, null)));
    }

    [Fact]
    public void Every_generated_species_reads_back_with_its_own_mark_through_the_shared_path()
    {
        // The whole contract, count-free: read each committed generated species the way the hosts do
        // (seed reader → shared mapper) and assert the admission rule agrees with the file's own mark
        // in both directions. Deliberately no assertion on how MANY are excluded — that is a reading.
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "generated", "creatures");
        var excluded = 0;
        var admitted = 0;
        // `_`-prefixed siblings in this tree are artifacts, not species (the build plan) — the same
        // skip the corpus tools make.
        foreach (var file in Directory.EnumerateFiles(dir, "*.json")
                     .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal)))
        {
            var json = File.ReadAllText(file);
            var def = ConcreteSpeciesMapper.ToCreatureSpeciesDef(ConcreteSpeciesSeedReader.Parse(json));
            var markedInFile = json.Contains("\"speciesKind\":", StringComparison.Ordinal);

            Assert.Equal(markedInFile, CreatureAdmission.IsExcluded(def));
            if (markedInFile)
            {
                Assert.False(CreatureAdmission.ForWave(def));
                excluded++;
            }
            else
            {
                admitted++;
            }
        }

        // Envelope only: the corpus must contain both kinds for the assertion above to mean anything.
        Assert.True(excluded > 0 && admitted > 0);
    }

    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the content root, so the `gk-data/packs/fusion/data/generated` read above stays valid once content moves to the
        // gk-data pack. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.ContentRoot.Path;
    }
}
