using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures;

/// <summary>One creature species — generated from captured game data, output checked in (spec-creature-core.md).</summary>
public sealed record CreatureSpeciesDef
{
    // Core is net6 / C# 10 — no `required`; Validate() rejects unset/invalid fields instead.
    public string SpeciesId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Linked capture side ("plant" | "zombie") — portrait/body source.</summary>
    public string Side { get; init; } = "";
    /// <summary>The PvZ type id whose art/dumps this species wears.</summary>
    public int GameTypeId { get; init; }
    /// <summary>Creature type id in the disjoint id space (≥ 10000) — used by web-battle events.</summary>
    public int CreatureTypeId { get; init; }
    public ElementTypeId ElementPrimary { get; init; }
    public ElementTypeId? ElementSecondary { get; init; }
    public CreatureRarity BaseRarity { get; init; }
    public CreatureDeployMode DeployMode { get; init; }
    public CreatureAcquisition Acquisition { get; init; }

    /// <summary>
    /// R-CS3/R-CS4's mark — <c>creature</c> (the default) or <c>excluded</c>, which
    /// <see cref="CreatureAdmission"/> refuses in every context. Carried from the seed anchor through
    /// the generator (<c>CreatureSpeciesGen</c>), the import and
    /// <c>ConcreteSpeciesMapper.ToCreatureSpeciesDef</c> — one field, one declaring site for the rule
    /// (species-gear-chain CS13). An empty string reads as the default, never as exclusion.
    /// </summary>
    public string SpeciesKind { get; init; } = "creature";

    /// <summary>
    /// The species' rank (spec-species-rank.md §1) — <c>creature-rank.v1.json</c>'s own cell for the
    /// species' resolved <c>(threatBand, rarity)</c> pair, carried from
    /// <c>ConcreteSpecies.Rank</c> through <c>ConcreteSpeciesMapper.ToCreatureSpeciesDef</c> (the one
    /// funnel both hosts share). Identity only: it gates and displays, never a magnitude. Null means
    /// the pipeline SKIPPED it (an unresolved threatBand or rarity) — never a fabricated bottom rung,
    /// so a consumer must map null to its own pass-through floor rather than to <c>Chaff</c> by
    /// accident.
    /// </summary>
    public CreatureRank? Rank { get; init; }
    public IReadOnlyList<string> Variants { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TraitPool { get; init; } = Array.Empty<string>();

    /// <summary>
    /// creature-lawn-deploy T1.5 — the species' own base-stat magnitudes (`ConcreteSpecies.Magnitudes`,
    /// already `long`-typed, `PTheta`-derived channel values), carried forward so a deploy can bind
    /// them the same way `TraitPool` already rides this record. Empty for any species with no
    /// magnitude data imported yet (a compiled-default/fixture species, or one pending its own
    /// generation pass) — a reconciler reading this must treat empty as "no magnitude bindings," never
    /// a startup error, matching this catalog's own species-level "honest incompleteness" precedent.
    /// </summary>
    public IReadOnlyDictionary<string, long> Magnitudes { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>
    /// `battle-tempo` `tempo-content` (spec-tempo-content.md §1.1) — the species' own attack tempo,
    /// carried from `ConcreteSpecies.AttackIntervalMs` (already authored, already persisted; this
    /// field is a PROJECTION into the compiled roster, not a new column on the corpus). `0` (the
    /// default) means "no tempo carried" — a fixture or a pre-tempo snapshot — and
    /// <see cref="Battle.SpeciesTempoProjection.SpeedFor"/> floors it to the default `turn.speed`
    /// rather than throwing, so every existing `CreatureSpeciesDef` literal in the tree stays valid
    /// without being touched.
    /// </summary>
    public long AttackIntervalMs { get; init; }
}

public static partial class CreatureSpeciesCatalog
{
    /// <summary>Disjoint id-space floor: web-battle events must never collide with PvZ type ids.</summary>
    public const int CreatureTypeIdFloor = 10_000;

    /// <summary>Structural partition of the id space (tunables-ssot: stays `const`, with this comment
    /// saying why) — plant and zombie `gameTypeId`s overlap (102 shared values across the shipped
    /// catalog), so without it a plant and a zombie sharing one compute the SAME id.</summary>
    public const int PlantOffset = 50_000;

    /// <summary>The disjoint creature id space. The plant offset is NOT optional: without it a plant
    /// and a zombie sharing a gameTypeId compute the SAME id — measured, 102 colliding gameTypeIds
    /// across the shipped catalog. Three sites computed this independently and one omitted the offset;
    /// this is the one place it lives now (species-gear-chain T19).</summary>
    public static int CreatureTypeIdFor(string side, int gameTypeId) =>
        CreatureTypeIdFloor + (string.Equals(side, "plant", StringComparison.Ordinal) ? PlantOffset : 0) + gameTypeId;

    public static readonly IReadOnlyList<string> KnownVariants = new[]
    {
        "normal", "ancient", "mutated", "corrupted", "blessed", "cursed", "shiny"
    };

    static Dictionary<string, CreatureSpeciesDef>? _byId;

    /// <summary>
    /// The store-backed roster (T4.8, `catalog-runtime`) — <see cref="SpeciesSnapshot.Configure"/>
    /// must have run first, the same "no built-in default" discipline `DerivedStatPolicy.Tuning`
    /// already established. `species-import`'s own committed output supersedes the compiled
    /// <c>GeneratedSpecies</c> array, which stays in the tree only until T4.8's own diff-test-gated
    /// deletion step.
    /// </summary>
    public static IReadOnlyList<CreatureSpeciesDef> All => Scoped.Value ?? _configured ?? throw new InvalidOperationException(
        "CreatureSpeciesCatalog.Configure(...) has not run. Every host reads the roster " +
        "species-import wrote via RpgStore.BuildCreatureSpeciesSnapshot() and calls Configure at " +
        "startup — there is no built-in default to fall back to.");

    public static bool IsKnown(string? speciesId) =>
        speciesId != null && ByIdMap().ContainsKey(speciesId);

    public static CreatureSpeciesDef Get(string speciesId) =>
        ByIdMap().TryGetValue(speciesId, out var def)
            ? def
            : throw new ArgumentException($"Unknown creature species id '{speciesId}'.");

    static Dictionary<string, CreatureSpeciesDef> ByIdMap()
    {
        // A scoped (test-only) roster is never cached in the process-global _byId — caching it would
        // leak one test's roster into the next call from a DIFFERENT async context that happens to
        // reuse the same thread. The real production path (no Scoped.Value) still caches normally.
        if (Scoped.Value is { } scoped)
            return scoped.ToDictionary(s => s.SpeciesId, StringComparer.Ordinal);

        if (_byId == null)
        {
            _ = All;
            _byId = All.ToDictionary(s => s.SpeciesId, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>Catalog discipline — a bad species is a startup error, never a runtime surprise.</summary>
    public static IReadOnlyList<CreatureSpeciesDef> Validate(IReadOnlyList<CreatureSpeciesDef> species)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTypeIds = new HashSet<int>();
        foreach (var s in species)
        {
            if (string.IsNullOrWhiteSpace(s.SpeciesId) || s.SpeciesId != s.SpeciesId.Trim().ToLowerInvariant())
                throw new InvalidOperationException($"Species id '{s.SpeciesId}' must be non-empty lower-kebab.");
            if (!seenIds.Add(s.SpeciesId))
                throw new InvalidOperationException($"Duplicate species id '{s.SpeciesId}'.");
            if (s.CreatureTypeId < CreatureTypeIdFloor)
                throw new InvalidOperationException($"Species '{s.SpeciesId}' creatureTypeId {s.CreatureTypeId} below floor {CreatureTypeIdFloor}.");
            if (!seenTypeIds.Add(s.CreatureTypeId))
                throw new InvalidOperationException($"Duplicate creatureTypeId {s.CreatureTypeId} ('{s.SpeciesId}').");
            if (s.Side is not ("plant" or "zombie"))
                throw new InvalidOperationException($"Species '{s.SpeciesId}' side must be plant|zombie.");
            if (s.Acquisition == CreatureAcquisition.None)
                throw new InvalidOperationException($"Species '{s.SpeciesId}' has no acquisition flags.");
            if (s.ElementSecondary is { } sec && sec == s.ElementPrimary)
                throw new InvalidOperationException($"Species '{s.SpeciesId}' primary == secondary element.");
            foreach (var v in s.Variants)
                if (!KnownVariants.Contains(v, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Species '{s.SpeciesId}' unknown variant '{v}'.");
            foreach (var t in s.TraitPool)
                if (!CreatureTraitCatalog.IsKnown(t))
                    throw new InvalidOperationException($"Species '{s.SpeciesId}' unknown trait '{t}'.");

            // rank (spec-species-rank.md §4): nothing to refuse here. It is a CLOSED enum, already
            // parsed at every boundary that produces one (the DAL read-back, the committed-tree
            // SeedReader, the Mapper), and null — a rank the pipeline skipped — is legal. Defaulting
            // it here would fabricate the bottom rung, the one thing Assumption 4 forbids.
        }

        return species;
    }
}
