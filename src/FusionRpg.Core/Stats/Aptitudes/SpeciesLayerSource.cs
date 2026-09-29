using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// species-progression module 6 (`species-layer-delivery`) step 6.2 (SP6.4) — the `ctx -> rows`
/// resolution behind the injector's `speciesLayers` delegate (`ActorHubBootstrap.CreateDefault`'s
/// `speciesLayers` parameter, read by `SpeciesLayerSubsystem`, SP6.2). Mirrors
/// <see cref="SpeciesAllocationSource"/>'s own established shape — fully provable in Core with fake
/// resolvers, no running game, no I/O of its own (the Hot-path ban this type must never violate: every
/// parameter is a plain, already-resolved delegate call) — but answers 1a/1b
/// <see cref="ProjectedLayerRow"/>s instead of an <see cref="AptitudeAllocation"/>, and routes the
/// general/specimen split through <see cref="ProgressionLayerSelector"/>, the ONE function every
/// compose path asks instead of re-deriving the rule.
///
/// <para><b>Acceptance (spec-species-layer-delivery.md step 6.2):</b> a general <c>Species</c> answer
/// gets 1a + 1b of ITS SIDE'S empire; a <c>Specimen</c> answer gets 1a + 1b of its OWNER empire (G5,
/// never inferred from its side); a <c>None</c> answer (no species at this <c>(Side, TypeId)</c>) gets
/// nothing. 2b is never read here — the transport's own <c>empire</c> field stays <c>{}</c> until
/// SP6.10, and this type carries no delegate that could even reach it; the <c>Species</c> arm is the
/// documented future extension point for it, the <c>Specimen</c> arm structurally has none.</para>
/// </summary>
public sealed class SpeciesLayerSource
{
    readonly Func<StatSide, int, SpeciesLookupResult> _resolveSpeciesId;
    readonly Func<string, IReadOnlyList<ProjectedLayerRow>> _resolveBaseRows;
    readonly Func<EmpireId, string, IReadOnlyList<ProjectedLayerRow>> _resolveModRows;
    readonly Func<string, string?>? _resolveBoundInstanceId;
    readonly Func<string, EmpireId?>? _resolveSpecimenOwnerEmpire;

    /// <param name="resolveSpeciesId">`(Side, GameTypeId) -> SpeciesLookupResult` — in production, the
    /// SAME `LawnElementIndex`-backed lookup `SpeciesAllocationSource` already uses
    /// (`CheatState.ResolveSpeciesLookup`), reused rather than re-derived: a Bound specimen's species
    /// is looked up through its OWN live `(Side, TypeId)` exactly like a general's, since a specimen's
    /// `GameTypeId` identifies its species the same way regardless of ownership.</param>
    /// <param name="resolveBaseRows">`speciesId -> 1a rows` — in production, the injector's
    /// `speciesLayers.base` cache (SP6.3's own wire shape, parsed by `RpgClient`).</param>
    /// <param name="resolveModRows">`(empire, speciesId) -> 1b rows` — in production, the injector's
    /// `speciesLayers.mod` cache, keyed by the SAME real `EmpireId` the transport already uses.</param>
    /// <param name="resolveBoundInstanceId">Same delegate shape as
    /// <see cref="SpeciesAllocationSource"/>'s own — `EntityKey -> Bound instanceId`, or null when this
    /// entity is not a Bound specimen. Optional: omitted, every ctx resolves the general path.</param>
    /// <param name="resolveSpecimenOwnerEmpire">`EntityKey -> the specimen's OWNER empire`. Read ONLY
    /// for a Bound ctx — never <c>ctx.Side</c> — matching `layer-source-selector` rule 3 / C10.</param>
    public SpeciesLayerSource(
        Func<StatSide, int, SpeciesLookupResult> resolveSpeciesId,
        Func<string, IReadOnlyList<ProjectedLayerRow>> resolveBaseRows,
        Func<EmpireId, string, IReadOnlyList<ProjectedLayerRow>> resolveModRows,
        Func<string, string?>? resolveBoundInstanceId = null,
        Func<string, EmpireId?>? resolveSpecimenOwnerEmpire = null)
    {
        _resolveSpeciesId = resolveSpeciesId ?? throw new ArgumentNullException(nameof(resolveSpeciesId));
        _resolveBaseRows = resolveBaseRows ?? throw new ArgumentNullException(nameof(resolveBaseRows));
        _resolveModRows = resolveModRows ?? throw new ArgumentNullException(nameof(resolveModRows));
        _resolveBoundInstanceId = resolveBoundInstanceId;
        _resolveSpecimenOwnerEmpire = resolveSpecimenOwnerEmpire;
    }

    /// <summary>The one resolve entry point — the exact shape `ActorHubBootstrap.CreateDefault`'s
    /// `speciesLayers` parameter expects.</summary>
    public IReadOnlyList<ProjectedLayerRow> Resolve(StatContext ctx)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        // The species lookup is IDENTICAL for a general and a Bound specimen: both read their species
        // off the SAME live (Side, TypeId) via the one LawnElementIndex-backed resolver. What differs
        // is only WHICH EMPIRE the 1b term comes from, decided below.
        var lookup = _resolveSpeciesId(ctx.Side, ctx.TypeId);
        if (!lookup.Found) return Array.Empty<ProjectedLayerRow>(); // unconfigured or genuinely none: a None answer

        var speciesId = lookup.SpeciesId;
        var instanceId = _resolveBoundInstanceId?.Invoke(ctx.EntityKey);

        ProgressionLayers layers;
        if (!string.IsNullOrEmpty(instanceId))
        {
            // A Bound specimen's empire is its OWNER's (layer-source-selector rule 3 / C10) — never
            // ctx.Side. This is the "Specimen" answer: 1a + 1b of the OWNER empire, never 2b.
            var owner = _resolveSpecimenOwnerEmpire?.Invoke(ctx.EntityKey) ?? EmpireId.Dave;
            layers = ProgressionLayerSelector.Select(
                CreatureProgressionSource.UniqueSpecimen(instanceId, occurrenceId: "lawn"), owner, EmpireId.Dave);
        }
        else
        {
            // A general's empire is its SIDE's (solid-remediation T4.1 S1). This is the "Species"
            // answer: 1a + 1b of its side's empire — the point where a future 2b term (SP6.10) would
            // be concatenated too, once the transport's `empire` field carries real rows.
            var empire = SpeciesAllocation.EmpireForSide(ctx.Side);
            layers = ProgressionLayerSelector.Select(
                CreatureProgressionSource.EmpireGeneral(speciesId), empire, EmpireId.Dave);
        }

        return layers.Owner switch
        {
            ProgressionOwner.None => Array.Empty<ProjectedLayerRow>(),
            _ => Concat(_resolveBaseRows(speciesId), _resolveModRows(layers.Empire, speciesId)),
        };
    }

    static IReadOnlyList<ProjectedLayerRow> Concat(
        IReadOnlyList<ProjectedLayerRow> baseRows, IReadOnlyList<ProjectedLayerRow> modRows)
    {
        if (baseRows.Count == 0) return modRows;
        if (modRows.Count == 0) return baseRows;
        var list = new List<ProjectedLayerRow>(baseRows.Count + modRows.Count);
        list.AddRange(baseRows);
        list.AddRange(modRows);
        return list;
    }
}
