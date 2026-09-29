using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// D4.17 row 6's own real bridging finding (party-dungeon-todo.md, 2026-09-07): `ConcreteAnchor.From`
/// (the real join `SlotFilter.Candidates`/`EncounterPreflight.Run` both consume) has **zero production
/// callers anywhere** — the only place the real ~800-species corpus was ever assembled into
/// `ConcreteAnchor` rows was a TEST-ONLY fixture (`tests/.../RealAnchorCorpusFixture.cs`), confirmed by
/// `grep`. This is the production equivalent: the identical join (anchor -&gt;
/// <see cref="SpeciesExpander.Expand"/> -&gt; <see cref="ConcreteAnchor.From"/>), reading real species
/// anchor JSON directly, taking every tuning as a caller-supplied parameter (never re-read from disk
/// here) so this stays a pure function over caller-supplied data, matching
/// <see cref="Domains.DomainAnchorBuilder"/>'s own established shape.
///
/// <para>Reused, not duplicated, from the test fixture's own already-proven logic — the ordinal
/// species-id order and "skip a species `SpeciesExpander.UnresolvedFields` cannot generate" rule are
/// identical, because they are the same real correctness rules, not a coincidence.</para>
/// </summary>
public static class EncounterCorpusBuilder
{
    public static IReadOnlyList<ConcreteAnchor> Build(
        string speciesDir, AptitudeTuning aptitudeTuning, PowerTuning powerTuning,
        CreatureShapeTuning shapeTuning, CreatureThreatTuning threatTuning)
    {
        if (speciesDir is null) throw new ArgumentNullException(nameof(speciesDir));
        if (aptitudeTuning is null) throw new ArgumentNullException(nameof(aptitudeTuning));
        if (powerTuning is null) throw new ArgumentNullException(nameof(powerTuning));
        if (shapeTuning is null) throw new ArgumentNullException(nameof(shapeTuning));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));
        if (!Directory.Exists(speciesDir)) return Array.Empty<ConcreteAnchor>();

        var anchors = new List<AnchorRow>();
        foreach (var file in Directory.GetFiles(speciesDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            anchors.AddRange(AnchorRowReader.ReadAll(File.ReadAllText(file)));
        }

        var rows = new List<ConcreteAnchor>();
        foreach (var anchor in anchors.OrderBy(a => a.SpeciesId, StringComparer.Ordinal))
        {
            if (SpeciesExpander.UnresolvedFields(anchor).Count > 0) continue;
            var species = SpeciesExpander.Expand(anchor, aptitudeTuning, powerTuning, shapeTuning, threatTuning);
            rows.Add(ConcreteAnchor.From(anchor, species, threatTuning));
        }
        return rows;
    }
}
