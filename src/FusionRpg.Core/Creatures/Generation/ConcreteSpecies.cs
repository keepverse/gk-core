namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// One species, expanded from its anchor into every concrete magnitude — `species-generator`'s own
/// output row (T4.4, `spec-species-generator.md`). Committed, diffable, and regenerable: adding a new
/// derived field is a new formula reading the SAME anchor, never a rewritten seed file
/// (`seed-contract.md` §1 consequence 3).
///
/// <para><see cref="Magnitudes"/> is a map, not one named property per channel, on purpose — the same
/// "adding a column costs zero seed files" property extends to this row's own shape: a new aptitude
/// edge widens the map without widening this record.</para>
/// </summary>
public sealed record ConcreteSpecies
{
    public string SpeciesId { get; init; } = "";
    public FusionRpg.Core.Creatures.CreatureRarity Rarity { get; init; }

    /// <summary>The species' rank — <c>creature-rank.v1.json</c>'s own cell for its resolved
    /// <c>(threatBand, rarity)</c> pair, resolved at expand time from the anchor's DERIVED
    /// <c>rank</c> (spec-species-rank.md §1). Identity only: it gates and displays, it never prices,
    /// scales, or contests, and it is resolved after <see cref="Theta"/>/<see cref="PTheta"/> so it
    /// cannot enter either by construction rather than by a flag. Null means SKIPPED — the anchor's
    /// threatBand or rarity was unresolved, so no rung was fabricated (Assumption 4).</summary>
    public FusionRpg.Core.Creatures.CreatureRank? Rank { get; init; }

    /// <summary>The species' own base (`creature-shape.v1.json`'s `speciesBaseTheta`) plus the threat
    /// rung's `thetaOffset` — lives inside Θ itself, additive, before `P(Θ)` runs.</summary>
    public int Theta { get; init; }

    /// <summary><c>P(Θ)</c> — the one ladder, read once, shared by every magnitude below (Q21:
    /// no channel gets a second growth rate).</summary>
    public long PTheta { get; init; }

    public long AttackIntervalMs { get; init; }

    /// <summary><c>"stated"</c> when a real interval came from `power-parse`'s text extraction;
    /// <c>"classified"</c> when it came from the `attackTempo` label table — spec §4: a stated
    /// interval always wins, and the row records which so a later observation can correct it without
    /// guesswork.</summary>
    public string AttackIntervalSource { get; init; } = "";

    public long RangeCells { get; init; }

    /// <summary>The anchor's own `variants` count — validated against `ssot-rarity.md` §3.3's count
    /// band for this species' rarity, never generated from nothing.</summary>
    public int VariantCount { get; init; }

    // ---- catalog-runtime pass-through (T4.8's own real precondition, resolved 2026-09-02) -----------
    // Every field below is copied from the anchor, not derived — `species-generator`'s own job is
    // numbers, but `CreatureSpeciesDef` (the live production catalog nine call sites read) needs these
    // too, and the anchor already carries all of them (verified against the real anchor schema and
    // the two real classified anchors on disk). Carrying them here, alongside the numeric fields
    // above, means `catalog-runtime`'s store-backed read is one table, not a join at load time.

    /// <summary>The anchor's own `side` — `"plant"` or `"zombie"`.</summary>
    public string Side { get; init; } = "";

    /// <summary>The anchor's own captured `gameTypeId` — the PvZ type id whose art/dumps this species
    /// wears. `CreatureTypeId` (the disjoint id-space value `CreatureSpeciesCatalog.CreatureTypeIdFloor` plus
    /// this) is deliberately NOT stored here — it is one addition, computed wherever it is read,
    /// never duplicated as a second source of truth for the same fact.</summary>
    public int GameTypeId { get; init; }

    public Core.Stats.Derived.ElementTypeId ElementPrimary { get; init; }
    public Core.Stats.Derived.ElementTypeId? ElementSecondary { get; init; }
    public CreatureDeployMode DeployMode { get; init; }
    public CreatureAcquisition Acquisition { get; init; }

    /// <summary>
    /// R-CS3/R-CS4's mark, carried verbatim from the seed anchor (<c>creature</c> | <c>excluded</c>).
    /// Null means "the anchor did not say", which every consumer reads as <c>creature</c> — the mark
    /// exists to REMOVE species from play, so an absent mark must never imply exclusion.
    /// </summary>
    public string? SpeciesKind { get; init; }

    /// <summary>The anchor's own `variants` list — <see cref="VariantCount"/> stays `Variants.Count`,
    /// kept as its own field since it already has real callers reading just the count.</summary>
    public IReadOnlyList<string> Variants { get; init; } = Array.Empty<string>();

    /// <summary>The anchor's own `traits` — validated against `CreatureTraitCatalog.IsKnown` wherever
    /// this feeds `CreatureSpeciesCatalog.Validate`, never re-validated here (this row is a carrier, not
    /// a second validator for a rule `CreatureSpeciesCatalog` already owns).</summary>
    public IReadOnlyList<string> TraitPool { get; init; } = Array.Empty<string>();

    /// <summary>Looked up from `almanac_seed` by the caller (species-import has `RpgStore`; this
    /// module deliberately does not — `spec-species-generator.md`'s own "opens no database" scope),
    /// mirroring the pre-atom-layer generator's own fallback chain (`DisplayName ?? TypeName ??
    /// "Creature {gameTypeId}"`). Null here means the caller has not resolved it yet — never a silent
    /// empty string standing in for "no name found."</summary>
    public string? Name { get; init; }

    /// <summary>Every Magnitude-mode aptitude edge this species' primary (and secondary, if impure)
    /// aptitude reaches, each computed by <see cref="Core.Stats.Aptitudes.AptitudeReadFunctions.Magnitude"/>
    /// reading the SAME <see cref="PTheta"/> — never a private <c>f(level)</c>. Contest-mode edges are
    /// deliberately excluded: their result is a bounded contest point value, not a game magnitude
    /// (<c>AptitudeReadFunctions</c>'s own class doc), so this module — whose whole job is magnitudes —
    /// does not emit them.</summary>
    public IReadOnlyDictionary<string, long> Magnitudes { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
}
