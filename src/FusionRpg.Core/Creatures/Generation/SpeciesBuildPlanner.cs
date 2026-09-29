using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// `redistribution-plan` (module 4) — turns each species' classified build favour
/// (<see cref="AnchorRow.AptitudePrimary"/>/<see cref="AnchorRow.AptitudeSecondary"/>) into a full
/// twelve-aptitude share vector, deterministically, in one closed-form pass (spec-redistribution-plan.md
/// "The algorithm — closed-form, not a search"). Pure and Core-only: no file IO, no `RpgStore`, no
/// model call ever — the tool (`gk-forge/tools/CreatureBuildPlanGen`) reads anchors and writes the plan; this type
/// only computes.
/// </summary>
public static class SpeciesBuildPlanner
{
    /// <summary>`per-species-lean` (module 6): the lean is keyed to the SPECIES, not to its primary,
    /// and the signal penalty below is why. Phase 1 runs before the Phase 2 loop and reads only
    /// values that do not depend on processing order, so the whole plan stays input-order independent.
    /// <paramref name="signals"/> is the measured population
    /// (<see cref="BuildFavourMeasurer.RosterPopulation"/> → <see cref="LeanSignals.Compute"/>); a row
    /// it does not carry reads neutral, see <see cref="NeutralFor"/>.</summary>
    public static SpeciesBuildResult Plan(
        IReadOnlyList<AnchorRow> species, SpeciesBuildTuning tuning, LeanSignalInputs signals)
    {
        if (species is null) throw new ArgumentNullException(nameof(species));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (signals is null) throw new ArgumentNullException(nameof(signals));
        if (species.Count == 0)
            throw new ArgumentException("species must not be empty", nameof(species));

        foreach (var s in species)
        {
            if (!AptitudeCatalog.IsAptitudeId(s.AptitudePrimary))
                throw new ArgumentException($"'{s.SpeciesId}': unknown primary aptitude '{s.AptitudePrimary}'");
            if (s.AptitudeSecondary is { } sec && !AptitudeCatalog.IsAptitudeId(sec))
                throw new ArgumentException($"'{s.SpeciesId}': unknown secondary aptitude '{sec}'");
        }

        var aptitudeIds = AptitudeCatalog.All
            .Select(a => a.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var speciesCount = species.Count;

        // Phase 1 — one lean per SPECIES, from that primary's own corpus-wide crowding plus the
        // signal penalty (spec-per-species-lean.md "The formula — byte-identical at zero weights").
        // With every weight at 0 the penalty is exactly 0 and this is today's formula, which is the
        // refactor's proof; a specialist (signal 1000) loses nothing and a generalist loses up to its
        // weight, so sharp builds belong to sharp creatures.
        var primaryCounts = species
            .GroupBy(s => s.AptitudePrimary, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);

        var crowdingPermilleByPrimary = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (primary, count) in primaryCounts)
        {
            checked { crowdingPermilleByPrimary[primary] = count * 1000 / speciesCount; }
        }

        var signalById = signals.Sets.ToDictionary(s => s.SpeciesId, StringComparer.Ordinal);
        var leanBySpecies = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in species)
        {
            var set = signalById.TryGetValue(s.SpeciesId, out var measured) ? measured : NeutralFor(s);

            // Every weight × signal product is a checked multiply (spec: "checked on every w × signal
            // product"); `1000 - signal` is the penalty shape, so a signal at 1000 costs nothing.
            long penalty = 0;
            foreach (var name in LeanSignals.Names)
                checked { penalty += tuning.LeanSignalWeights.For(name) * (1000 - set.For(name)) / 1000; }

            long crowding;
            checked { crowding = tuning.CrowdingFactor * crowdingPermilleByPrimary[s.AptitudePrimary] / 1000; }

            checked
            {
                leanBySpecies[s.SpeciesId] = Math.Clamp(
                    tuning.LeanMaxPermille - crowding - penalty, tuning.LeanMinPermille, tuning.LeanMaxPermille);
            }
        }

        // Phase 2 — ordinal speciesId order; the running corpus total per aptitude is what "the
        // running corpus deficit" (spec §"Phase 2") is measured against, recomputed as the pass
        // proceeds so early species fill the rarest aptitudes and later ones spread wider.
        var runningTotal = aptitudeIds.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);
        var vectors = new List<SpeciesBuildVector>(speciesCount);
        long processed = 0;

        foreach (var s in species.OrderBy(s => s.SpeciesId, StringComparer.Ordinal))
        {
            processed++;
            var vector = new Dictionary<string, long>(StringComparer.Ordinal);
            var usedSlots = new HashSet<string>(StringComparer.Ordinal);

            var lean = leanBySpecies[s.SpeciesId];
            vector[s.AptitudePrimary] = lean;
            usedSlots.Add(s.AptitudePrimary);

            var remainder = 1000 - lean;
            var leftover = remainder;

            // `Pure` is the anchor's own authority on "no distinct secondary" — matching
            // `SpeciesExpander.Expand`'s identical `!anchor.Pure && anchor.AptitudeSecondary is not
            // null` check. Some real classified anchors set `pure: true` yet still echo the primary
            // back as `aptitudeSecondary` (verified: `HypnoCattailGirl`/`ObsidianWallNut`, both
            // primary==secondary=="Focus"/"Retribution") rather than the "none" sentinel — trusting
            // `AptitudeSecondary is not null` alone would silently overwrite `vector[primary]` with a
            // SMALLER secondary share (same dictionary key), corrupting the vector's sum below 1000.
            // The `!= s.AptitudePrimary` guard is defense in depth against the same corruption from
            // any other bad-data shape, not just this one observed on the real corpus.
            if (!s.Pure && s.AptitudeSecondary is { } secondary && secondary != s.AptitudePrimary)
            {
                long secondaryShare;
                checked { secondaryShare = remainder * tuning.SecondarySharePermille / 1000; }
                vector[secondary] = secondaryShare;
                usedSlots.Add(secondary);
                leftover = remainder - secondaryShare;
            }

            if (leftover > 0)
            {
                var remainingAptitudes = aptitudeIds.Length - usedSlots.Count;
                var wantSlots = Math.Max(1, tuning.MaxAptitudesPerSpecies - usedSlots.Count);
                var slots = Math.Min(wantSlots, remainingAptitudes);

                // Largest current deficit against the band floor, ordinal tiebreak (spec §"Phase 2").
                // A negative "deficit" (already past the floor for this many species processed) sorts
                // last, so an over-represented aptitude naturally stops absorbing further remainder.
                var candidates = aptitudeIds
                    .Where(id => !usedSlots.Contains(id))
                    .Select(id => (Id: id, Deficit: tuning.ParityFloorPermille * processed - runningTotal[id]))
                    .OrderByDescending(x => x.Deficit)
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .Take(slots)
                    .Select(x => x.Id)
                    .ToArray();

                // Largest-remainder split of `leftover` across the chosen slots: base share to every
                // slot, then one extra permille each to the first `extra` (already deficit-ordered) so
                // the sum is exactly `leftover` — never lost to integer-division truncation.
                var baseShare = leftover / candidates.Length;
                var extra = leftover % candidates.Length;
                for (var i = 0; i < candidates.Length; i++)
                {
                    var share = baseShare + (i < extra ? 1 : 0);
                    vector[candidates[i]] = share;
                    usedSlots.Add(candidates[i]);
                }
            }

            foreach (var (id, share) in vector)
                checked { runningTotal[id] += share; }

            vectors.Add(new SpeciesBuildVector(s.SpeciesId, vector));
        }

        // Phase 3 — verify, and refuse. Corpus share is the mean vector value per aptitude across the
        // corpus (decision 11: parity over TOTAL ALLOCATED POINTS — every vector already sums to 1000,
        // so this mean is exactly "this aptitude's share of the corpus' total points").
        var corpusShare = aptitudeIds.ToDictionary(
            id => id, id => runningTotal[id] / speciesCount, StringComparer.Ordinal);

        var offending = corpusShare
            .Where(kv => kv.Value < tuning.ParityFloorPermille || kv.Value > tuning.ParityCeilingPermille)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        if (offending.Count > 0)
        {
            var detail = string.Join(", ", offending.Select(kv =>
                $"{kv.Key}={kv.Value}‰ (band [{tuning.ParityFloorPermille},{tuning.ParityCeilingPermille}]‰)"));
            throw new SpeciesBuildRefusal(
                $"species-build plan out of band for {offending.Count} aptitude(s): {detail}", offending);
        }

        // Phase 4 — the lead and shape caps (`lead-relabel-pass`, R-Q6). Phase 3 gates the POINTS; this
        // gates the DISTRIBUTION: no aptitude may lead more than `leadCapPermille` + its tolerance of
        // the measured roster, and no single build profile may hold more than `shapeCapPermille`.
        // The measure is the same one `favour-detector` writes, computed from the vectors this planner
        // just built through the shared measurer — so the gate and the committed artifact cannot
        // disagree, and there is no second histogram. A document that publishes no caps (v4 and earlier)
        // has nothing to gate on: absence is stated, never defaulted. **No floor on any aptitude**: only
        // the maxima are checked (R-Q6 rejects a floor outright), so a lead held by zero species passes.
        var leadThreshold = tuning.LeadRefusalPermille;
        var shapeCap = tuning.ShapeCapPermille;
        if (leadThreshold is not null || shapeCap is not null)
        {
            var caps = BuildFavourMeasurer.Measure(vectors, species, signals.Missing);
            if (leadThreshold is { } leadCap && caps.MaxLeadPermille > leadCap)
            {
                throw new SpeciesBuildRefusal(
                    $"species-build plan breaks the lead cap: '{caps.MaxLeadAptitude}' leads " +
                    $"{caps.MaxLeadPermille}‰ of {caps.SpeciesCount} measured species, over " +
                    $"leadCapPermille {tuning.LeadCapPermille}‰ + leadCapTolerancePermille " +
                    $"{tuning.LeadCapTolerancePermille}‰ = {leadCap}‰",
                    new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        [caps.MaxLeadAptitude] = caps.MaxLeadPermille,
                    });
            }

            if (shapeCap is { } shapeThreshold && caps.LargestShapePermille > shapeThreshold)
            {
                var largest = caps.ShapeCount
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .First();
                throw new SpeciesBuildRefusal(
                    $"species-build plan breaks the shape cap: profile '{largest.Key}' holds " +
                    $"{caps.LargestShapePermille}‰ of {caps.SpeciesCount} measured species, over " +
                    $"shapeCapPermille {shapeThreshold}‰",
                    new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        [largest.Key] = caps.LargestShapePermille,
                    });
            }
        }

        return new SpeciesBuildResult(vectors, corpusShare);
    }

    /// <summary>The signals for a row the measured population did not carry — a row outside the
    /// roster the measure filtered, or one the caller could not rank at all. Both ranked signals read
    /// <see cref="LeanSignals.NeutralPermille"/> and `pure` still comes from the anchor, which always
    /// carries it; the reason is already recorded in the signal input's own `Missing` list, so this
    /// is a value, never a second report.</summary>
    static LeanSignalSet NeutralFor(AnchorRow s) => new(
        SpeciesId: s.SpeciesId,
        Specialisation: LeanSignals.NeutralPermille,
        Pure: s.Pure ? 1000 : 0,
        ThreatRung: LeanSignals.NeutralPermille);
}
