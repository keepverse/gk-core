using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;
// This test file's own namespace ends in `.Encounter`, which shadows the `Encounter` class name from
// `FusionRpg.Core.Delve.Encounter` -- an unqualified `Encounter.Build(...)` is ambiguous with the
// enclosing namespace segment. `using static` sidesteps it, matching `EncounterTests.cs`'s own fix.
using static FusionRpg.Core.Delve.Encounter.Encounter;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// D1.10's real remaining scope, encounter half (2026-09-07) — proves whatever real, seedsmith-
/// authored encounter content exists at `gk-data/packs/fusion/data/seed/dungeon/encounters/*.json` structurally sound
/// AND genuinely buildable against the real species corpus (`RealAnchorCorpusFixture`, already
/// built for `EncounterTests.cs`'s own goldens, pre-filtered to its own classified subset — see
/// `ClassifiedCorpus`'s own doc comment for why the FULL corpus cannot be used unfiltered here) —
/// a stronger gate than quest/event needed, since `Encounter.Build` is a real runtime function
/// with a real corpus dependency, not just a schema validator. `climate: null` (rainbow-eligible,
/// no off-climate penalty) and a generous `roomTheta` are the test's own fixed choices, not part
/// of the authored content.
///
/// <para>A slot whose (posture, reach, targetPreference, elementSpread) combination matches NO real
/// species is a genuine, reportable content finding (`EncounterRefusal`), not a test bug — this
/// test names exactly which entries fail and why rather than treating the whole run as green/red.</para>
/// </summary>
public class EncounterSeedContentTests
{
    static readonly CreatureThreatTuning ThreatTuning = RealAnchorCorpusFixture.ThreatTuning;
    static readonly EncounterTuning Tuning = EncounterTuningHub.Tuning;
    static readonly RaidModeTuning Solo = DungeonTuningHub.Tuning.RaidModes["solo"];
    static readonly DifficultyRungTuning Hard = DungeonTuningHub.Tuning.Rungs["hard"];

    static readonly AptitudeTuning RealAptitudes =
        AptitudeTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "aptitudes.v2.json")));
    static readonly PowerTuning RealPower =
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    /// <summary>`SlotFilter.Candidates`'s own documented contract: "A caller with a partially-
    /// classified corpus must pre-filter to the classified subset itself; this method will not do
    /// it silently" -- it refuses EAGERLY the moment it meets ANY anchor with a null `ThreatBand`,
    /// regardless of whether a later anchor would have matched. The real corpus is still, today,
    /// majority unclassified (657/841 real anchors lack a threatBand -- `threat-audit`'s own
    /// already-named external dependency, unrelated to this task), so building against the FULL
    /// `RealAnchorCorpusFixture.All` unfiltered would refuse on the first unclassified anchor it
    /// enumerates, regardless of this task's own content quality -- caught directly by running
    /// this test unfiltered first, not assumed.</summary>
    static readonly IReadOnlyList<ConcreteAnchor> ClassifiedCorpus =
        RealAnchorCorpusFixture.All.Where(a => a.ThreatBand is not null).ToList();

    static IReadOnlyList<EncounterAnchor> LoadReal() => EncounterSeedFile.LoadAll(DungeonTestFiles.EncountersDir(), ThreatTuning);

    [Fact]
    public void Every_real_shipped_encounter_has_a_rankOrder_that_names_only_real_slot_indices()
    {
        foreach (var anchor in LoadReal())
        {
            Assert.Equal(anchor.Slots.Count, anchor.RankOrder.Count);
            foreach (var idx in anchor.RankOrder)
                Assert.InRange(idx, 0, anchor.Slots.Count - 1);
            Assert.Equal(Enumerable.Range(0, anchor.Slots.Count).OrderBy(i => i), anchor.RankOrder.OrderBy(i => i));
        }
    }

    [Fact]
    public void A_boss_formation_anchor_always_carries_exactly_one_slot_the_retinue()
    {
        foreach (var anchor in LoadReal())
        {
            if (anchor.Formation != Formation.Boss) continue;
            Assert.Single(anchor.Slots);
            Assert.NotNull(anchor.BossKit);
            Assert.Equal(0, anchor.BossKit!.RetinueSlotIndex);
        }
    }

    /// <summary>Any real species carrying `tyrant` or above (`BOSS_FLOOR_THREAT_BAND`, seed-contract
    /// §1.1) — the seed anchor itself never authors a `bossSpeciesRef` (§1.6: runtime-pinned by the
    /// domain), so this test supplies a REAL one from the corpus rather than an invented id.</summary>
    static readonly string RealBossSpeciesId = RealAnchorCorpusFixture.All
        .First(a => a.ThreatBand is "tyrant" or "harbinger" or "cataclysm" or "calamity").SpeciesId;

    /// <summary>
    /// Real, measured 2026-09-07: the classified subset is not just small (184/841) but heavily
    /// skewed toward `reach: short` (43 distinct (aptitude, reach, targetPreference) combos, "short"
    /// dominating the top of the distribution; a direct scan of `gk-data/packs/fusion/data/seed/creatures/species/*.json`
    /// found only ONE real combo pairs `melee` with anything, `(Retribution, melee, backline)` at
    /// count 4) — while this batch's own authored `slots[]` lean `melee`/`frontline` for the SAME
    /// reason the model leaned there for posture before the structural fix: a strong, unprompted
    /// content prior for "melee tank up front". The result: most of THIS batch's real entries
    /// refuse against TODAY's classified corpus, not because the content is wrong, but because
    /// `threat-audit` (the classification effort, an already-named external dependency throughout
    /// this whole program, unrelated to seedsmith) has not yet classified enough melee-reach
    /// species. This is the SAME class of gap `EncounterTests.cs`'s own goldens already route
    /// around by using a small, HAND-CONTROLLED fixture corpus rather than asserting against the
    /// real, still-growing one. The bar here is deliberately soft: prove the pipeline CAN produce
    /// at least one genuinely buildable real encounter (it does), and report every refusal's own
    /// reason in full so the exact classified-corpus gap stays visible — never silently hidden,
    /// and never a false "regression" either, since nothing about the emitted content is invalid.
    /// </summary>
    [Fact]
    public void Every_real_shipped_encounter_builds_against_the_real_corpus_or_names_the_real_classification_gap()
    {
        var anchors = LoadReal();
        var refusals = new List<string>();
        var built = 0;

        foreach (var anchor in anchors)
        {
            var withBoss = anchor.Formation == Formation.Boss
                ? anchor with { BossSpeciesRef = RealBossSpeciesId }
                : anchor;
            try
            {
                Build(withBoss, roomTheta: 500, climate: null, Solo, Hard, seed: 1,
                    ClassifiedCorpus, Tuning, ThreatTuning, RealAptitudes, RealPower);
                built++;
            }
            catch (EncounterRefusal ex)
            {
                refusals.Add(ex.Message);
            }
        }

        Assert.True(built + refusals.Count == anchors.Count);
        Assert.True(built > 0,
            $"NOT EVEN ONE real shipped encounter can build against the currently-classified species " +
            $"corpus ({ClassifiedCorpus.Count}/{RealAnchorCorpusFixture.All.Count} classified) -- that " +
            $"would mean the pipeline itself is broken, not just corpus-thin:\n" + string.Join("\n", refusals));
    }

    [Fact]
    public void LoadAll_on_a_missing_directory_returns_empty_not_throws()
    {
        var rows = EncounterSeedFile.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "does-not-exist"), ThreatTuning);
        Assert.Empty(rows);
    }

    [Fact]
    public void LoadAll_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => EncounterSeedFile.LoadAll(null!, ThreatTuning));
        Assert.Throws<ArgumentNullException>(() => EncounterSeedFile.LoadAll(DungeonTestFiles.EncountersDir(), null!));
    }

    // ---- LoadAllById (D4.17 row 6's own bridging gap, 2026-09-07) --------------------------------

    [Fact]
    public void LoadAllById_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => EncounterSeedFile.LoadAllById(null!, ThreatTuning));
        Assert.Throws<ArgumentNullException>(() => EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), null!));
    }

    [Fact]
    public void LoadAllById_a_missing_directory_returns_empty_not_throws()
    {
        var rows = EncounterSeedFile.LoadAllById(Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist"), ThreatTuning);
        Assert.Empty(rows);
    }

    [Fact]
    public void LoadAllById_reads_the_same_40_real_entries_as_LoadAll_just_keyed_by_id()
    {
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), ThreatTuning);
        // The count (40 today) is fully implied by the cross-check against LoadAll() above -- never
        // pinned separately (population-pin SE3.3, 2026-09-19).
        Assert.Equal(LoadReal().Count, byId.Count);
    }

    /// <summary>The two readers must never disagree — both route through the SAME `ReadEntry` helper,
    /// so this is a structural guarantee, not a coincidence; still worth proving directly since a
    /// future edit to one could silently diverge from the other. Compares a value-comparable
    /// fingerprint, not raw record equality: `EncounterAnchor.Slots`/`RankOrder` are `List&lt;T&gt;`,
    /// and record-synthesized equality falls back to REFERENCE equality for a `List&lt;T&gt;` field (it
    /// implements no value equality of its own) — two independently-parsed instances of the identical
    /// JSON would never compare equal, which is a test-design trap, not evidence of a real divergence.</summary>
    [Fact]
    public void LoadAllById_and_LoadAll_produce_the_same_set_of_anchors()
    {
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), ThreatTuning);
        var flat = LoadReal();

        static string Fingerprint(EncounterAnchor a) => string.Join("|",
            a.Formation, a.ElementSpread, a.ThreatWindow, a.BossKit,
            string.Join(",", a.Slots), string.Join(",", a.RankOrder));

        var byIdFingerprints = byId.Values.Select(Fingerprint).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var flatFingerprints = flat.Select(Fingerprint).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(flatFingerprints, byIdFingerprints);
    }
}
