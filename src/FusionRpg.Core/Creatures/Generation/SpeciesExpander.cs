using System.Linq;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// `species-generator` (T4.4, `spec-species-generator.md`, creature-seed module 12, build order 12 of
/// 16). Expands one enum-only anchor into a concrete species — every magnitude, every interval,
/// derived from the SAME shared functions the player's own stat system reads, never a private
/// `f(level)`. <b>Model calls: none, ever</b> — this module is the entire reason no model ever picks
/// a number.
///
/// <para><b>Numeric rules, non-negotiable</b> (docs/architecture/numeric-types.md, binding harder here than anywhere else in
/// the program): <c>long</c> for every magnitude, never <c>float</c>; widen before multiplying;
/// divide by 1000 last, exactly once; overflow throws, never clamps.
/// <see cref="AptitudeReadFunctions.Magnitude"/> already honours all four — calling it is how this
/// module complies, reimplementing any part of it is how it stops complying.</para>
/// </summary>
public static class SpeciesExpander
{
    /// <summary>
    /// The voted anchor fields that can carry the classification pipeline's own literal
    /// `"unresolved"` sentinel (a genuine 3-way vote that never converged,
    /// spec-option-permutation.md §4) — a real, ongoing corpus state, not a corrupt anchor.
    /// <see cref="Expand"/> deliberately fails loud on any of these rather than silently guessing
    /// (see the comment inline below), so a batch caller over the WHOLE corpus needs to skip a
    /// still-unresolved species BEFORE calling <see cref="Expand"/>, not catch its exception —
    /// this is that check, shared so `CreatureSpeciesGen`/`CreatureSpeciesImport` never duplicate the
    /// field list. `threatBand` is excluded: a null/absent value there already has a real,
    /// sanctioned fallback (<see cref="CreatureThreatTuning.InferredDefaultRung"/>), read by
    /// <see cref="Expand"/> itself via <c>threatTuning.OffsetFor</c> — never a batch-level skip.
    // threatBand stays excluded DELIBERATELY (threat-band-fill): a batch-level skip here would
    // refuse every species relying on the sanctioned InferredDefaultRung fallback. The gap is
    // surfaced by the provenance field and the quality report instead — never by refusing the batch.
    /// </summary>
    public static IReadOnlyList<string> UnresolvedFields(AnchorRow anchor)
    {
        var fields = new List<string>();
        if (anchor.Rarity == "unresolved") fields.Add("rarity");
        if (anchor.AptitudePrimary == "unresolved") fields.Add("aptitudePrimary");
        if (anchor.ElementPrimary == "unresolved") fields.Add("elementPrimary");
        if (anchor.AttackTempo == "unresolved") fields.Add("attackTempo");
        if (anchor.DeployMode == "unresolved") fields.Add("deployMode");
        return fields;
    }

    /// <summary>
    /// Expand one anchor. <paramref name="statedIntervalMs"/> is `power-parse`'s own extracted
    /// interval when one exists (spec §4: a stated interval always wins over the classified
    /// `attackTempo` table) — null when none was extracted for this species.
    /// </summary>
    public static ConcreteSpecies Expand(
        AnchorRow anchor,
        AptitudeTuning aptitudeTuning,
        PowerTuning powerTuning,
        CreatureShapeTuning shapeTuning,
        CreatureThreatTuning threatTuning,
        long? statedIntervalMs = null)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));

        if (!CreatureRarityIds.TryParse(anchor.Rarity, out var rarity))
            throw new InvalidOperationException(
                $"'{anchor.SpeciesId}': rarity '{anchor.Rarity}' is not a known CreatureRarity");

        // ---- theta: species' own base + the threat rung's offset, additive, before P(Theta) -------
        var thetaOffset = threatTuning.OffsetFor(anchor.ThreatBand);
        var theta = checked(shapeTuning.SpeciesBaseTheta + thetaOffset);
        var pTheta = new PowerLadder(powerTuning).Value(theta); // the one ladder — PS-3

        // ---- rank: DERIVED identity, resolved AFTER theta/P(Theta) ---------------------------------
        // Ordering is the whole exclusion guarantee (spec-species-rank.md §1: rank is "excluded from
        // theta/pTheta by construction, never by a flag") — nothing below this line feeds back into
        // either value, and no magnitude channel reads it. Identity only: it gates and displays.
        var rank = ResolveRank(anchor);

        // ---- allocation share: pure = 100% primary; impure splits per creature-shape's own dial -------
        var hasSecondary = !anchor.Pure && anchor.AptitudeSecondary is not null;
        var primaryShareMilli = hasSecondary ? 1000L - shapeTuning.ImpureSecondaryShareMilli : 1000L;
        var secondaryShareMilli = hasSecondary ? shapeTuning.ImpureSecondaryShareMilli : 0L;

        // ---- every Magnitude-mode edge either aptitude reaches, read off the SAME pTheta ------------
        var shareExponentMilli = aptitudeTuning.Read.Magnitude.ShareExponentMilli;
        var magnitudes = new Dictionary<string, long>(StringComparer.Ordinal);

        // A family with zero edges of ANY mode is not a known aptitude — most commonly the
        // classification pipeline's own "unresolved" sentinel (a genuine 3-way vote disagreement
        // that never converged, spec-option-permutation.md §4). Unlike every other field this
        // function reads (rarity, elementPrimary, deployMode, acquisition, attackTempo, reach — all
        // throw on an unrecognised value), an unresolved aptitude used to fall through silently:
        // the loop below simply matched nothing, and the species was written to disk with an empty
        // `magnitudes` dict and no error anywhere — a real, found defect (audited 2026-09-03; see
        // SnorkleZombie/ThreePeater in the committed 28-species tree, both `aptitudePrimary:
        // "unresolved"`, both zero magnitude channels). Failing loud here is strictly safer: a
        // species that cannot be generated correctly must refuse generation, not ship silently
        // stat-less.
        bool IsKnownAptitude(string family) => IsKnownAptitudeFamily(aptitudeTuning, family);

        void ApplyAptitude(string family, long shareMilli)
        {
            if (shareMilli <= 0) return;
            var share = shareMilli / 1000.0;

            foreach (var edge in aptitudeTuning.Edges)
            {
                if (!string.Equals(edge.Source, family, StringComparison.Ordinal)) continue;
                if (edge.Mode != AptitudeReadMode.Magnitude) continue; // Contest is a bounded point value, not a magnitude

                var value = AptitudeReadFunctions.Magnitude(edge.KMilli, share, shareExponentMilli, pTheta);
                // A channel BOTH aptitudes reach sums their contributions — never overwrites — the
                // same reason two atoms in one container add rather than replace.
                magnitudes[edge.Channel] = checked(magnitudes.GetValueOrDefault(edge.Channel) + value);
            }
        }

        if (!IsKnownAptitude(anchor.AptitudePrimary))
            throw new InvalidOperationException(
                $"'{anchor.SpeciesId}': aptitudePrimary '{anchor.AptitudePrimary}' has no edge in " +
                "aptitudes.v2.json — either an unresolved classification vote or an unknown family");
        ApplyAptitude(anchor.AptitudePrimary, primaryShareMilli);

        // Gated on hasSecondary (secondaryShareMilli > 0), not merely "is the field non-null" — a
        // pure species carries zero secondary share by construction, so a garbage/unresolved
        // AptitudeSecondary on a pure anchor is inert (ApplyAptitude would no-op on it anyway) and
        // must not refuse generation for a value the math never reads.
        if (hasSecondary && anchor.AptitudeSecondary is { } secondary)
        {
            if (!IsKnownAptitude(secondary))
                throw new InvalidOperationException(
                    $"'{anchor.SpeciesId}': aptitudeSecondary '{secondary}' has no edge in " +
                    "aptitudes.v2.json — either an unresolved classification vote or an unknown family");
            ApplyAptitude(secondary, secondaryShareMilli);
        }

        // ---- tempo: a stated interval always wins (spec §4) -----------------------------------------
        var (intervalMs, intervalSource) = statedIntervalMs is { } stated
            ? (stated, "stated")
            : (LookupOrThrow(shapeTuning.AttackTempoIntervalMs, anchor.AttackTempo, anchor.SpeciesId, "attackTempo"), "classified");

        var rangeCells = LookupOrThrow(shapeTuning.ReachRangeCells, anchor.Reach, anchor.SpeciesId, "reach");

        // ---- catalog-runtime pass-through: copied from the anchor, never derived --------------------
        if (!ElementRoster.TryParse(anchor.ElementPrimary, out var elementPrimary))
            throw new InvalidOperationException(
                $"'{anchor.SpeciesId}': elementPrimary '{anchor.ElementPrimary}' is not a known element");
        ElementTypeId? elementSecondary = null;
        if (anchor.ElementSecondary is { } secEl)
        {
            if (!ElementRoster.TryParse(secEl, out var parsedSec))
                throw new InvalidOperationException(
                    $"'{anchor.SpeciesId}': elementSecondary '{secEl}' is not a known element");
            // Found running the real flip 2026-09-05: CreatureSpeciesCatalog.Validate refused
            // 'tower_peapuff' for "primary == secondary element" — its real anchor independently
            // voted "fire" for BOTH fields (two separate classification passes agreeing on the same
            // value rather than one of them saying "none"), a real LLM voting artifact, not a code
            // bug. SpeciesBuildPlanner.cs already carries the identical defense for the sibling
            // AptitudeSecondary field ("trusting AptitudeSecondary is not null alone would silently
            // corrupt the vector... defense in depth against the same corruption from any other
            // bad-data shape") — applying that same established principle here rather than inventing
            // a new one: a stated secondary that equals the primary is not a hybrid, it is "none".
            if (parsedSec != elementPrimary) elementSecondary = parsedSec;
        }
        if (!Enum.TryParse<CreatureDeployMode>(anchor.DeployMode, ignoreCase: false, out var deployMode))
            throw new InvalidOperationException(
                $"'{anchor.SpeciesId}': deployMode '{anchor.DeployMode}' is not a known CreatureDeployMode");
        var acquisition = CreatureAcquisition.None;
        foreach (var flag in anchor.Acquisition)
        {
            if (!Enum.TryParse<CreatureAcquisition>(flag, ignoreCase: false, out var parsedFlag))
                throw new InvalidOperationException(
                    $"'{anchor.SpeciesId}': acquisition '{flag}' is not a known CreatureAcquisition");
            acquisition |= parsedFlag;
        }

        return new ConcreteSpecies
        {
            SpeciesId = anchor.SpeciesId,
            Rarity = rarity,
            Rank = rank,
            Theta = theta,
            PTheta = pTheta,
            AttackIntervalMs = intervalMs,
            AttackIntervalSource = intervalSource,
            RangeCells = rangeCells,
            VariantCount = anchor.Variants.Count,
            Magnitudes = magnitudes,
            Side = anchor.Side,
            GameTypeId = anchor.GameTypeId,
            ElementPrimary = elementPrimary,
            ElementSecondary = elementSecondary,
            DeployMode = deployMode,
            Acquisition = acquisition,
            // R-CS3/R-CS4: the anchor's mark rides through verbatim; the admission policy is the one
            // place that interprets it (species-gear-chain CS13).
            SpeciesKind = anchor.SpeciesKind,
            Variants = anchor.Variants,
            TraitPool = anchor.Traits,
        };
    }

    /// <summary>Whether <paramref name="family"/> is a known aptitude, read off the tuning file's own
    /// <c>edges[].source</c> values with the SAME ordinal comparison <see cref="Expand"/> applies.
    ///
    /// <para>Public and static so there is exactly ONE such test in the tree. It was a local function
    /// inside <see cref="Expand"/> until the anchor-contract predicate needed the same answer, and
    /// two implementations of "is this a known aptitude" is precisely how a contract ends up true of
    /// one consumer and not the other.</para></summary>
    public static bool IsKnownAptitudeFamily(AptitudeTuning tuning, string family) =>
        tuning.Edges.Any(e => string.Equals(e.Source, family, StringComparison.Ordinal));

    /// <summary>Resolves the anchor's own DERIVED rank id into the closed enum, or null when the
    /// anchor recorded that rank was SKIPPED (an unresolved threatBand or rarity) — spec
    /// Assumption 4: skip, don't fabricate. The anchor is the one derivation site
    /// (seedsmith's `derive_rank`, over the SAME <c>creature-rank.v1.json</c> this program reads
    /// elsewhere); re-deriving here from `(threatBand, rarity)` would be a second source of truth for
    /// a value the tuning table already owns, which is the defect this program's own
    /// one-authority rule exists to prevent. An id outside the closed vocabulary is a real anchor
    /// defect and throws, exactly as an unknown <c>rarity</c>/<c>elementPrimary</c> does above —
    /// never a silent null, which would read as "rank was skipped".</summary>
    public static CreatureRank? ResolveRank(AnchorRow anchor)
    {
        if (anchor.Rank is null) return null;
        if (CreatureRankIds.TryParse(anchor.Rank, out var parsed)) return parsed;
        throw new InvalidOperationException(
            $"'{anchor.SpeciesId}': rank '{anchor.Rank}' is not a known CreatureRank");
    }

    /// <summary>The tempo/reach table lookup, raised. Public so the anchor-contract predicate reports
    /// this message verbatim instead of a paraphrase that could say something the table does not.
    /// The table is the one <c>creature-shape.v1.json</c> loaded into, compared with its own
    /// <see cref="StringComparer.Ordinal"/>: neither trims nor folds case.</summary>
    public static long LookupOrThrow(IReadOnlyDictionary<string, long> table, string key, string speciesId, string field)
    {
        if (table.TryGetValue(key, out var v)) return v;
        throw new InvalidOperationException($"'{speciesId}': {field} '{key}' has no entry in creature-shape.v1.json");
    }
}
