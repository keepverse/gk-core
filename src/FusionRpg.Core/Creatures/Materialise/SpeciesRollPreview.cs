using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Creatures.Materialise;

/// <summary>One preview's outcome: the rolled instance, or the refusal code the caller reports.</summary>
public sealed record SpeciesRollPreviewResult(InstanceRow? Instance, string? RefusalCode, string? Detail)
{
    public bool IsOk => Instance is not null;

    public static SpeciesRollPreviewResult Ok(InstanceRow instance) => new(instance, null, null);

    public static SpeciesRollPreviewResult Refused(string code, string detail) => new(null, code, detail);
}

/// <summary>
/// `species-progression` SP0.2 — the delayed, deterministic, **never-stored** roll
/// (spec-species-mod-ledger.md, behaviour 2). One species, one save's world seed: the same
/// <see cref="WorldSeed.DeriveRollSeed"/>(worldSeed, "species", speciesId) seed and the same
/// <see cref="InstanceProducer.Compose"/> call <see cref="SpeciesMaterialiser"/> performs, so the fusion
/// preview endpoint and the fusion transaction can never disagree about what a species would roll.
///
/// <para><b>Pure and stateless</b>, like the materialiser: seed and catalog in, one instance out, no I/O
/// and no persistence. An empire that has never fused has no ledger row, and this is what stands in for
/// one — computed, never written.</para>
/// </summary>
public static class SpeciesRollPreview
{
    /// <summary>
    /// The preview for one species. A refusal means there is nothing to preview: no
    /// <c>species-passive.{speciesId}</c> container exists yet (`picks.source-not-materialised` — the code
    /// the fusion endpoint already reports for exactly this case), or the container itself refuses to
    /// compose, in which case its own reason is carried through.
    /// </summary>
    public static SpeciesRollPreviewResult For(
        string speciesId,
        Func<string, ContainerRow?> lookupSpeciesPassiveContainer,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        Func<string, IReadOnlyList<string>> domainMembers,
        long worldSeed,
        long catalogRevision,
        int contentTheta,
        PowerTuning tuning)
    {
        if (string.IsNullOrWhiteSpace(speciesId))
            return SpeciesRollPreviewResult.Refused("picks.source-not-materialised", "speciesId is required");

        var container = lookupSpeciesPassiveContainer($"species-passive.{speciesId}");
        if (container is null)
            return SpeciesRollPreviewResult.Refused(
                "picks.source-not-materialised", $"no species-passive.{speciesId} container exists yet");

        var rollSeed = WorldSeed.DeriveRollSeed(worldSeed, "species", speciesId);
        var compose = InstanceProducer.Compose(
            container, lookupAtom, lookupAffix, domainMembers, rollSeed, contentTheta, tuning,
            out var instance, variant: null, InstanceOrigin.Drop, catalogRevision);
        if (!compose.IsOk)
            return SpeciesRollPreviewResult.Refused(compose.Reason.ToString(), $"'{speciesId}': {compose.Detail}");

        return SpeciesRollPreviewResult.Ok(instance!);
    }
}
