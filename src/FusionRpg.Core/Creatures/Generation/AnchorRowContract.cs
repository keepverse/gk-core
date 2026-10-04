using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>Everything <see cref="SpeciesExpander.Expand"/> reads that is not the anchor itself: the
/// four tuning surfaces, loaded by the caller from wherever it already loads them.</summary>
public sealed record AnchorContractTunings(
    AptitudeTuning Aptitudes,
    PowerTuning Power,
    CreatureShapeTuning Shape,
    CreatureThreatTuning Threat);

/// <summary>
/// The C# anchor consumer's own raise set, as ONE predicate — and, unlike a list written beside it,
/// built almost entirely OUT OF that consumer's own methods.
///
/// <para><b>Why it is not a second list of field names.</b> A guard list kept beside
/// <see cref="AnchorRowReader"/> and <see cref="SpeciesExpander"/> is a second implementation free
/// to drift from the code it claims to describe, and the drift is silent in the one direction that
/// matters: the consumer grows a guard, the list does not, and the list goes on certifying entries
/// the consumer refuses. So every stage here is reached by CALLING something:</para>
///
/// <list type="bullet">
/// <item><description>the two file guards — <see cref="AnchorRowReader.RequireArrayDocument"/>,
/// which <see cref="AnchorRowReader.ReadAll"/> itself calls;</description></item>
/// <item><description>presence and shape — <see cref="AnchorRowReader.TryStr"/> and
/// <see cref="AnchorRowReader.TryGameTypeId"/> over <see cref="AnchorRowReader.StrFields"/>, the
/// list the reader itself iterates. Not one of the eleven names appears in this file;</description></item>
/// <item><description>the classification skip — <see cref="SpeciesExpander.UnresolvedFields"/>,
/// called, never restated;</description></item>
/// <item><description>every closed vocabulary — the very helpers
/// <see cref="SpeciesExpander.Expand"/> uses: <see cref="CreatureRarityIds.TryParse"/>,
/// <see cref="ElementRoster.TryParse"/>, <see cref="SpeciesExpander.IsKnownAptitudeFamily"/>,
/// <see cref="SpeciesExpander.LookupOrThrow"/>, <see cref="SpeciesExpander.ResolveRank"/>, and
/// <c>Enum.TryParse</c> with the same <c>ignoreCase</c> the expander passes;</description></item>
/// <item><description>the catalog stage — <see cref="CreatureSpeciesCatalog.Validate"/>, called on
/// the same <see cref="ConcreteSpecies"/> → <see cref="CreatureSpeciesDef"/> projection the real
/// import path projects (<see cref="ConcreteSpeciesMapper.ToCreatureSpeciesDef"/>).</description></item>
/// </list>
///
/// <para><b>What it is not, stated rather than assumed.</b> It is NOT a merge with the Python
/// live-seed loader's contract: that is a different consumer with a different raise set, and
/// seedsmith's <c>adapters/creatures/anchor/schema.py</c> deliberately owns that sibling
/// separately. And it is NOT a claim about any corpus — this type reads no anchor document. It is
/// handed one <see cref="JsonElement"/> and answers what the C# consumer would do with it. That
/// separation is what lets it run in a standalone gk-core clone: the code it describes is gk-core's
/// own <c>src/</c>, and the vocabularies it reads are gk-core's own <c>data/tuning/</c>, so nothing
/// here requires a private sibling to exist.</para>
///
/// <para><b>Every guard is evaluated; none short-circuits ACROSS guards.</b> The consumer is
/// fail-fast by design — <c>CreatureRecipeReconcileInput</c> prints the first rejection and returns
/// 1 — so it reports <i>where it stopped</i> and never <i>how much is affected</i>. That difference
/// is what turns one repair round into four, each round surfacing the next guard in the same file.
/// WITHIN one field the consumer's own shadowing is preserved (see the stage-3 notes below);
/// across fields nothing is.</para>
/// </summary>
public static class AnchorRowContract
{
    /// <summary>The classification pipeline's own literal. <see cref="SpeciesExpander.UnresolvedFields"/>
    /// reports it and a batch caller SKIPS the species — a skip is not a refusal, so this predicate
    /// refuses nothing on its account. <see cref="SkippedFields"/> answers it separately, because
    /// "no violation" and "will reconcile" are two different true sentences about two different
    /// species, and only one of them is true about any given one.</summary>
    public const string UnresolvedSentinel = "unresolved";

    /// <summary>The keys whose presence and shape this contract checks, read FROM the reader.</summary>
    public static IReadOnlyList<string> StrFields => AnchorRowReader.StrFields;

    /// <summary>The five fields <see cref="SpeciesExpander.UnresolvedFields"/> inspects, named here so
    /// a reader can tell a deliberate omission from an oversight.</summary>
    public static readonly IReadOnlyList<string> SkipFields =
        new[] { "rarity", "aptitudePrimary", "elementPrimary", "attackTempo", "deployMode" };

    /// <summary>
    /// Every guard this predicate can report, BY NAME, in stage order.
    ///
    /// <para>Named, never cited by line: a line citation rots inside the edit that introduces it,
    /// and a citation that rots on an unrelated change is not evidence of coverage.</para>
    ///
    /// <para>This list is the SUBJECT of the coverage assertion, not the assertion. What proves a
    /// guard added to the reader without being added here fails the build is
    /// <c>AnchorRowContractTests</c>, which pins this list AND requires a behavioural case per
    /// entry. A list nobody checks is not a contract. Seventeen guards: three in stage 2, ten in
    /// stage 3, four per-entry in stage 4.</para>
    ///
    /// <para><b>CONSIDERED AND DELIBERATELY NOT GUARDS.</b> Named so a reader can tell an omission
    /// from an oversight — and the trait line is a MEASURED exclusion, not an assumption:</para>
    /// <list type="bullet">
    /// <item><description><c>traits</c> — <c>CreatureSpeciesCatalog.Validate</c> does refuse an
    /// unknown <c>CreatureSpeciesDef.TraitPool</c> member, but the anchor's <c>traits</c> NEVER
    /// reaches it: <see cref="ConcreteSpeciesMapper.ToCreatureSpeciesDef"/> substitutes
    /// <c>CreatureTraitPoolCuration.PickFor(...)</c>, and <c>RpgStore.Species</c>'s own round-trip
    /// carries the curated pool. Measured by the differential test: an anchor carrying
    /// <c>"vampiric"</c>, which is in no catalog, is ACCEPTED by the tool. An open flavour-text
    /// array and a closed gameplay vocabulary happen to share a field name, and a guard here would
    /// refuse 904 real anchors to police a value nothing reads.</description></item>
    /// <item><description><c>variants</c> — unlike <c>traits</c> this one IS a straight pass-through,
    /// which is why it is a guard and the sibling is not.</description></item>
    /// <item><description><c>side</c> and <c>targetPreference</c> are presence-guarded at stage 2
    /// and vocabulary-checked nowhere in stage 3; <c>side</c> is refused one stage later, by the
    /// catalog.</description></item>
    /// <item><description><c>resourceProfile</c>, <c>family</c>, <c>basis</c> and <c>speciesKind</c>
    /// are read by no stage of this path at all.</description></item>
    /// <item><description><c>StrArray</c>'s <c>variants</c> and <c>traits</c> both make absent and
    /// empty the SAME empty list, and nothing refuses an empty one.</description></item>
    /// <item><description><c>Enum.TryParse</c> also accepts a numeric string and a comma-separated
    /// flag list, and <c>CreatureRarityIds.TryParse</c> trims and lower-cases its argument. This
    /// predicate calls those methods rather than re-implementing them, so it inherits their exact
    /// tolerance instead of guessing at it.</description></item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<string> GuardNames = new[]
    {
        // Stage 2 - AnchorRowReader, called over StrFields (eleven names, one entry each).
        "reader.str-field",
        "reader.game-type-id",
        "reader.record-is-an-object",

        // Stage 3 - SpeciesExpander, each through the expander's own helper.
        "expand.rarity",
        "expand.aptitude-primary",
        "expand.aptitude-secondary",
        "expand.attack-tempo",
        "expand.reach",
        "expand.element-primary",
        "expand.element-secondary",
        "expand.deploy-mode",
        "expand.acquisition-flag",
        "expand.rank",

        // Stage 4 - CreatureSpeciesCatalog.Validate, called. Per-entry guards; the two duplicate
        // guards are document-level and are named in `CorpusViolations` instead, with the reason.
        // `catalog.trait` is deliberately absent - see the "NOT GUARDS" note above.
        "catalog.acquisition-none",
        "catalog.side",
        "catalog.variant",
        "catalog.creature-type-id-floor",
    };

    /// <summary>
    /// <see cref="AnchorRowReader.ReadAll"/>'s two file-level guards, then every row's.
    ///
    /// <para>File-level guards live here rather than in prose because a shape that can never reach a
    /// per-entry predicate is otherwise unchecked, and a whole document is only in hand at a scan.
    /// <paramref name="path"/> is quoted in every message so a scan over 464 files names the file the
    /// way the consumer's own CLI does before it returns 1.</para>
    /// </summary>
    public static IReadOnlyList<string> FileViolations(string json, string path)
    {
        var out_ = new List<string>();
        JsonDocument doc;
        try { doc = AnchorRowReader.RequireArrayDocument(json); }
        catch (AnchorRowRejection ex) { return new[] { $"{path}: {ex.Message}" }; }

        using (doc)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                foreach (var v in Violations(el))
                    out_.Add($"{path}: {Where(el)}: {v}");
        }
        return out_;
    }

    /// <summary>
    /// The per-entry contract. Empty means the C# consumer accepts this anchor; non-empty means it
    /// refuses it, and every entry says why in the consumer's own words.
    ///
    /// <para><paramref name="tunings"/> is required, and omitting it is a refusal rather than a
    /// lenient mode: a caller that forgot the tuning files gets told the vocabulary guards could
    /// not be evaluated, instead of a green light it did not earn. A predicate that cannot say "I
    /// did not check" can only ever be confidently wrong.</para>
    /// </summary>
    public static IReadOnlyList<string> Violations(JsonElement el, AnchorContractTunings? tunings = null)
    {
        var out_ = new List<string>();

        // ---- STAGE 2: the reader's own presence and shape guards, CALLED ------------------------
        // Not `ReadOne` in a try/catch: the consumer raises at the FIRST bad field, so a predicate
        // that did the same would report where it stopped and never how much is affected. These
        // non-raising twins are the reader's own tests, so the set cannot drift from its order.
        //
        // The row's SHAPE comes first, and it is its own guard rather than an ordering detail:
        // `JsonElement.TryGetProperty` throws `InvalidOperationException` on a non-object element
        // rather than returning false, so calling `TryStr` first would let a malformed row escape
        // this predicate as an exception — the one thing a refusal list must never do.
        if (el.ValueKind != JsonValueKind.Object)
        {
            // The consumer cannot refuse this shape either: `ReadAll` hands a non-object straight to
            // `ReadOne`, whose `TryGetProperty` throws out of the tool uncaught rather than
            // returning 1 with a message. Reported here anyway, because a bad row must become a
            // refusal and because "the consumer crashes here" is a finding about the consumer, not a
            // licence for this predicate to crash too.
            out_.Add($"anchor record must be an object, not {el.ValueKind}");
            return out_;
        }

        foreach (var key in AnchorRowReader.StrFields)
            if (!AnchorRowReader.TryStr(el, key, out _))
                out_.Add($"anchor: missing or non-string '{key}'");
        if (!AnchorRowReader.TryGameTypeId(el, out _))
            out_.Add("anchor: missing or non-integer 'gameTypeId'");

        if (out_.Count > 0) return out_;

        var row = AnchorRowReader.ReadOne(el);

        // ---- THE SKIP IS NOT A VIOLATION -------------------------------------------------------
        if (SpeciesExpander.UnresolvedFields(row).Count > 0) return out_;

        if (tunings is null)
        {
            out_.Add("anchor contract: no tunings supplied, so the stage 3 vocabulary guards and the " +
                     "stage 4 catalog guards could not be evaluated");
            return out_;
        }

        // ---- STAGE 3: every vocabulary, through the expander's own helper ------------------------
        // Two shadowing rules keep this faithful, and both are the consumer's control flow rather
        // than a judgement call:
        //   * a presence defect shadows its OWN field's membership guard. `attackTempo: ""` is
        //     present-and-wrong and fails only at the table lookup, while an absent `reach` failed
        //     at presence and never reached one. Two different defects with two different repairs,
        //     so they must not collapse into one word.
        //   * `Expand` gates the secondary-apptitude check on `hasSecondary`, because a pure
        //     species carries zero secondary share by construction and a garbage secondary on a
        //     pure anchor is inert. Checking it unconditionally would invent refusals.
        var present = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in AnchorRowReader.StrFields)
            if (AnchorRowReader.TryStr(el, key, out var v)) present[key] = v;

        if (!CreatureRarityIds.TryParse(row.Rarity, out _))
            out_.Add($"'{row.SpeciesId}': rarity '{row.Rarity}' is not a known CreatureRarity");

        if (present.ContainsKey("aptitudePrimary") &&
            !SpeciesExpander.IsKnownAptitudeFamily(tunings.Aptitudes, row.AptitudePrimary))
            out_.Add(
                $"'{row.SpeciesId}': aptitudePrimary '{row.AptitudePrimary}' has no edge in " +
                "aptitudes.v2.json — either an unresolved classification vote or an unknown family");

        if (row.AptitudeSecondary is { } secondary && row.Pure is not true &&
            !SpeciesExpander.IsKnownAptitudeFamily(tunings.Aptitudes, secondary))
            out_.Add(
                $"'{row.SpeciesId}': aptitudeSecondary '{secondary}' has no edge in " +
                "aptitudes.v2.json — either an unresolved classification vote or an unknown family");

        // Tempo and reach: the expander's OWN lookup, caught, so the reported message is the table's
        // message rather than a paraphrase that could say something the table does not.
        try
        {
            SpeciesExpander.LookupOrThrow(
                tunings.Shape.AttackTempoIntervalMs, row.AttackTempo, row.SpeciesId, "attackTempo");
        }
        catch (InvalidOperationException ex) { out_.Add(ex.Message); }

        try
        {
            SpeciesExpander.LookupOrThrow(
                tunings.Shape.ReachRangeCells, row.Reach, row.SpeciesId, "reach");
        }
        catch (InvalidOperationException ex) { out_.Add(ex.Message); }

        if (!ElementRoster.TryParse(row.ElementPrimary, out _))
            out_.Add(
                $"'{row.SpeciesId}': elementPrimary '{row.ElementPrimary}' is not a known element");

        if (row.ElementSecondary is { } secEl && !ElementRoster.TryParse(secEl, out _))
            out_.Add($"'{row.SpeciesId}': elementSecondary '{secEl}' is not a known element");

        if (present.ContainsKey("deployMode") &&
            !Enum.TryParse<CreatureDeployMode>(present["deployMode"], ignoreCase: false, out _))
            out_.Add(
                $"'{row.SpeciesId}': deployMode '{row.DeployMode}' is not a known CreatureDeployMode");

        foreach (var flag in row.Acquisition)
            if (!Enum.TryParse<CreatureAcquisition>(flag, ignoreCase: false, out _))
                out_.Add(
                    $"'{row.SpeciesId}': acquisition '{flag}' is not a known CreatureAcquisition");

        try { SpeciesExpander.ResolveRank(row); }
        catch (InvalidOperationException ex) { out_.Add(ex.Message); }

        // ---- STAGE 4: the catalog's own Validate, CALLED ------------------------------------------
        // Reachable only when stage 3 passed, because that is what `Expand` needs — the consumer's
        // own short-circuit, kept. Calling `Validate` on the single-species roster the real import
        // path projects is what makes this stage non-transcribed: an unknown variant, an unknown
        // trait, a side that is neither plant nor zombie, an acquisition that resolved to `None`
        // and a creatureTypeId under the floor are all the catalog's rules, read from the catalog,
        // in the catalog's order, with the catalog's messages.
        if (out_.Count > 0) return out_;

        var def = ConcreteSpeciesMapper.ToCreatureSpeciesDef(SpeciesExpander.Expand(
            row, tunings.Aptitudes, tunings.Power, tunings.Shape, tunings.Threat));
        try { CreatureSpeciesCatalog.Validate(new[] { def }); }
        catch (InvalidOperationException ex) { out_.Add(ex.Message); }

        return out_;
    }

    /// <summary>
    /// The two document-level guards <see cref="CreatureSpeciesCatalog.Validate"/> applies across a
    /// roster rather than within one entry: a repeated species id and a repeated creatureTypeId.
    ///
    /// <para>They are here, and they are the ONE place this file re-derives instead of calling,
    /// for a reason worth stating: the consumer raises at the FIRST duplicate, so calling
    /// <see cref="CreatureSpeciesCatalog.Validate"/> on a roster reports one and stops. A roster
    /// scan that has to count cannot delegate. Both are computed from the catalog's own
    /// <see cref="CreatureSpeciesCatalog.CreatureTypeIdFor"/> and from the same normalisation
    /// <see cref="ConcreteSpeciesMapper.ToCreatureSpeciesDef"/> applies, so neither the id space
    /// nor the case-folding is restated here.</para>
    /// </summary>
    public static IReadOnlyList<string> CorpusViolations(
        IEnumerable<(string Path, string SpeciesId, int CreatureTypeId)> roster)
    {
        var out_ = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var typeIds = new HashSet<int>();
        foreach (var (path, speciesId, creatureTypeId) in roster)
        {
            if (!ids.Add(speciesId))
                out_.Add($"{path}: Duplicate species id '{speciesId}'.");
            if (!typeIds.Add(creatureTypeId))
                out_.Add($"{path}: Duplicate creatureTypeId {creatureTypeId} ('{speciesId}').");
        }
        return out_;
    }

    /// <summary>The fields <see cref="SpeciesExpander.UnresolvedFields"/> reports for this anchor —
    /// the ones carrying the classification pipeline's <see cref="UnresolvedSentinel"/>, which a batch
    /// caller skips rather than refuses. Empty is the common case; non-empty is a real, ongoing
    /// corpus state (a genuine 3-way vote that never converged), never a corrupt anchor.</summary>
    public static IReadOnlyList<string> SkippedFields(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        AnchorRow row;
        try { row = AnchorRowReader.ReadOne(el); }
        catch (AnchorRowRejection) { return Array.Empty<string>(); }
        return SpeciesExpander.UnresolvedFields(row);
    }

    static string Where(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty("speciesId", out var id) &&
        id.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(id.GetString())
            ? id.GetString()!
            : "<no speciesId>";
}