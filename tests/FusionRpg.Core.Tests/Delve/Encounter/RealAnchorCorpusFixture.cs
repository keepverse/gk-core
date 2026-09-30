using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// The real anchor corpus, joined into <see cref="ConcreteAnchor"/> rows through the exact production
/// pipeline (anchor -&gt; `SpeciesExpander` -&gt; `ConcreteAnchor.From`), built ONCE for every
/// `Delve.Encounter` test that needs it. Deliberately does NOT reuse
/// `Creatures.Fusion.RealCorpusFixture` — that one round-trips through a temp `RpgStore` and returns
/// `CreatureSpeciesDef`, which carries neither `ThreatBand`, `AptitudePrimary`, `Reach` nor
/// `TargetPreference` (verified 2026-09-06) — exactly the fields this module's own join exists to
/// recover, so building from the plain `AnchorRow`/`ConcreteSpecies` pair directly is both simpler and
/// the only shape that actually has what `SlotFilter.Candidates` needs.
/// </summary>
internal static class RealAnchorCorpusFixture
{
    static string RepoRoot()
    {
        // This is the ENGINE root, not the content root, and the difference is not cosmetic:
        // every read below is ReadTuning("data", "tuning", ...), and gk-core/data/tuning lives in gk-core.
        // The gk-data pack has no gk-core/data/tuning at all, so pointing this at the content root made
        // every one of them look for gk-data/packs/fusion/data/tuning/... and miss. 109 tests in
        // FusionRpg.Core.Tests failed through this one helper, because it runs from a
        // [ModuleInitializer] and a static initializer that throws takes its whole fixture with it.
        //
        // It previously returned ContentRoot.Path, with a comment arguing that the content root was
        // correct "so every gk-core/data/tuning / gk-data/packs/fusion/data/seed read here stays valid once content moves to the
        // gk-data pack". That reasoning bundled two trees into one helper: gk-data/packs/fusion/data/seed does move to the
        // pack, gk-core/data/tuning does not, and this helper has no gk-data/packs/fusion/data/seed read to justify the content
        // root. Naming the root you actually mean is the whole fix; a comment rationalising the
        // wrong one is how it survived review.
        return KeepverseRoots.Core();
    }

    static string ReadTuning(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

    static readonly AptitudeTuning RealAptitudes = AptitudeTuningLoader.Parse(ReadTuning("data", "tuning", "aptitudes.v2.json"));
    static readonly CreatureShapeTuning RealShape = CreatureShapeTuningLoader.Parse(ReadTuning("data", "tuning", "creature-shape.v1.json"));
    static readonly CreatureThreatTuning RealThreat = CreatureThreatTuningLoader.Parse(ReadTuning("data", "tuning", "creature-threat.v2.json"));
    static readonly PowerTuning RealPower = PowerTuningLoader.Parse(ReadTuning("data", "tuning", "power-scale.v2.json"));

    public static CreatureThreatTuning ThreatTuning => RealThreat;

    /// <summary>Ordinal `SpeciesId` order (spec-encounter-generator.md §9 — "no dictionary enumeration
    /// reaches an output"), matching `WaveCatalog.cs`'s own `OrderBy(..., Ordinal)` convention.
    /// Anchors that fail `SpeciesExpander`'s own unresolved-field check are skipped, the same skip
    /// `RealCorpusFixture`/species-import both apply — a species that cannot be generated correctly
    /// never reaches the corpus at all, banded or not.</summary>
    public static readonly IReadOnlyList<ConcreteAnchor> All = Build();

    static IReadOnlyList<ConcreteAnchor> Build()
    {
        var seedRoot = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species");
        var anchors = new List<AnchorRow>();
        foreach (var file in Directory.GetFiles(seedRoot, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            anchors.AddRange(AnchorRowReader.ReadAll(File.ReadAllText(file)));
        }

        var rows = new List<ConcreteAnchor>();
        foreach (var anchor in anchors.OrderBy(a => a.SpeciesId, StringComparer.Ordinal))
        {
            if (SpeciesExpander.UnresolvedFields(anchor).Count > 0) continue;
            var species = SpeciesExpander.Expand(anchor, RealAptitudes, RealPower, RealShape, RealThreat);
            rows.Add(ConcreteAnchor.From(anchor, species, RealThreat));
        }

        Assert.True(rows.Count > 700, $"expected the real corpus to join well over 700 anchors, got {rows.Count} — seed data may have moved");
        return rows;
    }
}
