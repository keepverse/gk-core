using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>One `(Side, GameTypeId) → speciesId` lookup outcome, distinguishing THREE states rather
/// than collapsing to a bool (spec-allocation-transport.md's own ⛔ hazard): the underlying index not
/// having been configured yet (the bootstrap window `LawnElementResolverHost` documents — a throwaway
/// empty index would otherwise look identical to "this type genuinely has no species"), versus a real,
/// configured "no species at this (Side, TypeId)" answer, versus a real hit.</summary>
public readonly record struct SpeciesLookupResult(bool IndexConfigured, bool Found, string SpeciesId)
{
    public static readonly SpeciesLookupResult NotConfigured = new(false, false, "");
    public static readonly SpeciesLookupResult NoSpecies = new(true, false, "");
    public static SpeciesLookupResult Hit(string speciesId) => new(true, true, speciesId);
}

/// <summary>
/// `allocation-transport` (module 6) — the `ctx → allocation` resolution logic behind an injected
/// lookup, mirroring <see cref="Battle.SpecimenOwnershipOracle"/>'s own established shape: fully
/// provable in Core with fake resolvers, no running game, no I/O of its own (the Hot-path ban this
/// type must never violate — every parameter is a plain, already-resolved delegate call).
///
/// <para><b>Commander and species points merge into ONE <see cref="AptitudeAllocation"/></b>
/// (`operator+`), resolved once by the caller — never resolved per scope and concatenated
/// (`AptitudeAllocation`'s own "scopes sum before share, never the reverse").</para>
///
/// <para><b>An un-configured index reports, never returns a silent zero</b> — the identical shape to
/// the documented defect where a 222-point allocation resolved and wrote nothing. When
/// <paramref name="resolveSpeciesId"/> reports <see cref="SpeciesLookupResult.IndexConfigured"/> false,
/// <see cref="Resolve"/> calls <paramref name="reportUnconfigured"/> and falls back to the commander
/// allocation ALONE — a real, if incomplete, answer, not a made-up one.</para>
/// </summary>
public sealed class SpeciesAllocationSource
{
    readonly Func<StatSide, int, SpeciesLookupResult> _resolveSpeciesId;
    readonly Func<Commanders.EmpireId, string, AptitudeAllocation> _resolveSpeciesAllocation;
    readonly Func<Commanders.EmpireId, AptitudeAllocation> _resolveCommanderAllocation;
    readonly Action<string> _reportUnconfigured;
    readonly Func<string, string?>? _resolveBoundInstanceId;
    readonly Func<string, AptitudeAllocation>? _resolveUniqueAllocation;
    readonly Func<string, Commanders.EmpireId?>? _resolveSpecimenOwnerEmpire;

    /// <param name="resolveSpeciesId">`(Side, GameTypeId) → SpeciesLookupResult` — in production,
    /// `LawnElementIndex.TryGet` wrapped to also report whether the index itself has been configured
    /// (`LawnElementResolverHost`'s own state). Injected, never a hard dependency — a test supplies a
    /// fake covering all three outcomes with no `LawnElementIndex` involved.</param>
    /// <param name="resolveSpeciesAllocation">`(empire, speciesId) → effective CreatureType
    /// allocation` — in production, the injector's own cached-by-speciesId dictionary
    /// (`allocation-transport`'s own cache, refreshed at the existing commander-cache cadence), never a
    /// server round trip.
    /// <para><b>solid-remediation T4.1 (S3): the empire is part of the key, not a caller's assumption.</b>
    /// The persisted scope key gained an empire dimension (`SpeciesAllocation.ScopeKey`), so a species
    /// row is `(empire, speciesId)` and no longer `speciesId` alone. Passing the empire HERE is what
    /// keeps the two key sets the same shape: a cache behind this delegate that ignores the empire is
    /// answering a question it was not asked, and would hand a lawn zombie the human player's species
    /// row — the exact defect S1 names, one seam further down.</para></param>
    /// <param name="resolveCommanderAllocation">`empire → that empire's Commander allocation` — in
    /// production, the injector's per-empire commander cache (the human empire's from the player fetch,
    /// Zomboss's from the SAME response's `commanderByEmpire` map: R23, EP4.18). It takes the EMPIRE, not
    /// a player id, because a commander pool belongs to an empire and the two are no longer the same
    /// question — the human-only delegate this replaced could only ever answer the human's.</param>
    /// <param name="reportUnconfigured">Called with a diagnostic message when the species index has
    /// not been configured yet — required, never silently swallowed (this repo's own "no silent
    /// default" discipline, applied here to a runtime reporting hook rather than a config key).</param>
    /// <param name="resolveBoundInstanceId">`unique-lawn-wire` (aptitude-sheet AS-1.1) — `EntityKey`
    /// (the live ptr hex) → the UniqueCreature `instanceId` it is currently Bound to, or null when this
    /// entity is not a Bound specimen. Optional: omitted, every ctx resolves the species path exactly
    /// as before (existing callers/tests are unaffected). When supplied, a Bound hit takes ABSOLUTE
    /// priority over the species lookup below — never merged with it — which is what keeps a Bound
    /// unique that happens to share a species id with a general from inheriting empire shares
    /// (`spec-unique-lawn-wire.md`'s own G6 regression).</param>
    /// <param name="resolveUniqueAllocation">`instanceId → effective UniqueCreature allocation` — in
    /// production, the injector's own cached-by-instanceId dictionary, fetched via
    /// `GET /api/aptitudes/unique/{instanceId}` at the same cadence as the commander/species caches.
    /// Required whenever <paramref name="resolveBoundInstanceId"/> is supplied.</param>
    /// <param name="resolveSpecimenOwnerEmpire">`species-progression` SP1.3 (`layer-source-selector`,
    /// rule 3 / C10) — `EntityKey` (the SAME live ptr hex <paramref name="resolveBoundInstanceId"/>
    /// takes) → the specimen's OWNER empire, or null when unregistered. In production, the injector's
    /// specimen-owner map (`CheatState.TryGetSpecimenEmpire`). Optional: omitted, a Bound ctx resolves
    /// as the human empire (today's equivalent — existing callers/tests are unaffected). Read ONLY for
    /// a Bound ctx — a Bound unique's commander term comes from its OWNER, never <c>ctx.Side</c>, which
    /// is what makes a zombie-side, human-owned unique carry the human commander term on the lawn, as
    /// the sheet already does.</param>
    public SpeciesAllocationSource(
        Func<StatSide, int, SpeciesLookupResult> resolveSpeciesId,
        Func<Commanders.EmpireId, string, AptitudeAllocation> resolveSpeciesAllocation,
        Func<Commanders.EmpireId, AptitudeAllocation> resolveCommanderAllocation,
        Action<string> reportUnconfigured,
        Func<string, string?>? resolveBoundInstanceId = null,
        Func<string, AptitudeAllocation>? resolveUniqueAllocation = null,
        Func<string, Commanders.EmpireId?>? resolveSpecimenOwnerEmpire = null)
    {
        _resolveSpeciesId = resolveSpeciesId ?? throw new ArgumentNullException(nameof(resolveSpeciesId));
        _resolveSpeciesAllocation = resolveSpeciesAllocation ?? throw new ArgumentNullException(nameof(resolveSpeciesAllocation));
        _resolveCommanderAllocation = resolveCommanderAllocation ?? throw new ArgumentNullException(nameof(resolveCommanderAllocation));
        _reportUnconfigured = reportUnconfigured ?? throw new ArgumentNullException(nameof(reportUnconfigured));
        if (resolveBoundInstanceId is not null && resolveUniqueAllocation is null)
            throw new ArgumentNullException(nameof(resolveUniqueAllocation),
                "resolveUniqueAllocation is required whenever resolveBoundInstanceId is supplied");
        _resolveBoundInstanceId = resolveBoundInstanceId;
        _resolveUniqueAllocation = resolveUniqueAllocation;
        _resolveSpecimenOwnerEmpire = resolveSpecimenOwnerEmpire;
    }

    /// <summary>The one resolve entry point. A Bound UniqueCreature (when the caller wired
    /// <c>resolveBoundInstanceId</c>) resolves commander merged with ITS OWN allocation and returns
    /// immediately — the species lookup never runs for that ctx, so a Bound specimen sharing a species
    /// id with a general can never inherit empire shares. Otherwise: commander alone when there is no
    /// species to merge (genuinely no species at this `(Side, TypeId)`, OR the index isn't configured
    /// yet — reported in the latter case), commander merged with the species' effective allocation
    /// otherwise. `Side` stays part of every lookup key, always — `polevaulterzombie`/`wallnut` share a
    /// `GameTypeId` but never a `Side`, so they never collide here.</summary>
    public AptitudeAllocation Resolve(StatContext ctx)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        // species-progression SP1.3 (layer-source-selector, rule 3 / C10): a Bound unique's empire is
        // its OWNER's, read from the injector's specimen-owner map, checked BEFORE any side-derived
        // empire is computed — a unique never inherits the empire species fallback, and its commander
        // term must not be decided by which SIDE it happens to occupy on the board either. This is
        // what makes a zombie-side, human-owned unique carry the human commander term on the lawn, as
        // the sheet already does (the one named behaviour change this task pins).
        if (_resolveBoundInstanceId is not null)
        {
            var instanceId = _resolveBoundInstanceId(ctx.EntityKey);
            if (!string.IsNullOrEmpty(instanceId))
            {
                var owner = _resolveSpecimenOwnerEmpire?.Invoke(ctx.EntityKey) ?? Commanders.EmpireId.Dave;
                var layers = ProgressionLayerSelector.Select(
                    CreatureProgressionSource.UniqueSpecimen(instanceId, occurrenceId: "lawn"),
                    owner, Commanders.EmpireId.Dave);
                var boundCommander = layers.CarriesCommander
                    ? _resolveCommanderAllocation(owner)
                    : AptitudeAllocation.Empty;
                return boundCommander + _resolveUniqueAllocation!(instanceId);
            }
        }

        // solid-remediation T4.1 (S1): WHICH empire is asking, for the general/species path below —
        // plants are the player's (Dave), zombies are Zomboss's.
        var empire = SpeciesAllocation.EmpireForSide(ctx.Side);

        // S1's shape, now typed by ownership instead of side: the commander term is the OWNER EMPIRE's
        // pool (R23, EP4.18) — Zomboss's is computed at read exactly as Dave's is explicit — so a zombie
        // no longer resolves empty-for-someone-else's-sake and no longer reads the human's. A commander
        // allocation belongs to an empire; an actor inherits its OWN.
        var commander = _resolveCommanderAllocation(empire);

        var lookup = _resolveSpeciesId(ctx.Side, ctx.TypeId);

        if (!lookup.IndexConfigured)
        {
            _reportUnconfigured(
                $"SpeciesAllocationSource.Resolve: species index not configured yet (side={ctx.Side}, " +
                $"typeId={ctx.TypeId}, entity='{ctx.EntityKey}') -- resolving commander-only for this call.");
            return commander;
        }

        if (!lookup.Found) return commander;

        return commander + _resolveSpeciesAllocation(empire, lookup.SpeciesId);
    }
}
