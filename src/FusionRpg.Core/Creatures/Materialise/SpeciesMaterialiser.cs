using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Creatures.Materialise;

/// <summary>One species' rolled effect instance — the per-player half of the two-layer split
/// (spec-player-materialise.md §1: shared definitions, per-player materialisation).</summary>
public sealed record MaterialisedRoll(string SpeciesId, InstanceRow Instance);

/// <summary>
/// `player-materialise` (T5.5, `spec-player-materialise.md`, creature-seed module 16, build order 16 of
/// 16, ⭐ the closing module): rolls every species' `species-passive.{speciesId}` container against
/// one player's world seed. <b>Model calls: none, ever</b> — this runs on a player's machine.
///
/// <para><b>Pure — seed and catalog in, rows out, no I/O.</b> The roster is a CACHE of a derivation,
/// not the only copy of a fact: <c>(worldSeed, catalogRevision)</c> reproduces it exactly. That
/// property dies to a single impure input, so nothing here reads a clock, an unseeded
/// <see cref="Random"/>, or a dictionary/hash-set's own iteration order — every ordering-sensitive
/// step sorts explicitly, by content, never by insertion.</para>
/// </summary>
public static class SpeciesMaterialiser
{
    /// <summary>
    /// Roll one instance per species that already has a `species-passive.{speciesId}` container in
    /// the catalog. A species with none yet is skipped, not an error — `species-effects` (T5.3) has
    /// not shipped real content for every species yet, and that is a valid, current state, not a
    /// defect this module should surface as one.
    /// </summary>
    /// <param name="speciesIds">The roster to materialise. Order does not affect the result — each
    /// species' own roll seed is independently derived (<see cref="WorldSeed.DeriveRollSeed"/>), and
    /// this method sorts internally before iterating, so a caller handing in a shuffled list (or one
    /// built by enumerating a `Dictionary`/`HashSet`) still reproduces byte-identically.</param>
    /// <param name="lookupSpeciesPassiveContainer">Resolves `species-passive.{speciesId}` — null
    /// means no content exists for this species yet.</param>
    public static AtomRejection Materialise(
        IReadOnlyList<string> speciesIds,
        Func<string, ContainerRow?> lookupSpeciesPassiveContainer,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        Func<string, IReadOnlyList<string>> domainMembers,
        long worldSeed,
        long catalogRevision,
        int thetaContent,
        PowerTuning tuning,
        out IReadOnlyList<MaterialisedRoll> rolls)
    {
        var results = new List<MaterialisedRoll>();

        foreach (var speciesId in speciesIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            var container = lookupSpeciesPassiveContainer($"species-passive.{speciesId}");
            if (container is null) continue;

            var rollSeed = WorldSeed.DeriveRollSeed(worldSeed, "species", speciesId);
            var compose = InstanceProducer.Compose(
                container, lookupAtom, lookupAffix, domainMembers, rollSeed, thetaContent, tuning,
                out var instance, variant: null, InstanceOrigin.Drop, catalogRevision);

            if (!compose.IsOk)
            {
                rolls = Array.Empty<MaterialisedRoll>();
                return AtomRejection.Fail(compose.Reason, $"'{speciesId}': {compose.Detail}");
            }

            results.Add(new MaterialisedRoll(speciesId, instance!));
        }

        rolls = results;
        return AtomRejection.Ok;
    }
}
