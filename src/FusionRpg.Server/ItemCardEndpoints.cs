using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Items.Gems;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// N2's string catalog (`gk-content/content/display/en.json`) as a file the Server opens once at boot — Core
/// parses the text (<see cref="DisplayStringCatalog"/>) and never touches the disk, the same split
/// every other corpus loader in this file uses.
///
/// <para><b>Absence degrades, it never blocks.</b> No file, or an unreadable one, returns a lookup
/// that answers <c>null</c> for every key. Block 10 then renders its key with no sentence, which is
/// honest and is exactly what <c>DisplayRules.MissingDisplayKey</c> exists to report — the one thing
/// it must never do is invent a sentence.</para>
/// </summary>
public static class DisplayStringCatalogFile
{
    public static Func<string, string?> Load(string path)
    {
        try
        {
            if (File.Exists(path)) return DisplayStringCatalog.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to the empty catalog: a malformed string file must not stop the server.
        }

        return _ => null;
    }
}

/// <summary>
/// ⏸ <b>The second stopgap over module 6's missing <c>item_base_type</c> table</b>, and it says so
/// for the same reason <see cref="BaseTypeSocketMaxCorpus"/> does. Module 6 shipped the 740-entry
/// corpus as seed JSON and the Core readers, but no table and no loader — and a card with no base
/// type has no header, which is why <see cref="RpgStore.GetItemCardInput"/> throws by name rather
/// than rendering a nameless item.
///
/// <para>Every field is a display KEY or a frame/role id, never the base type's own
/// <c>container_id</c>, which spec-item-card.md §2.4 forbids showing. The day module 6 lands the
/// table, this class is deleted and the delegate reads the table.</para>
/// </summary>
public static class ItemBaseTypeCorpus
{
    /// <summary>
    /// Recursive: the corpus is partitioned into subdirectories (<c>footing/</c>, <c>girdle/</c>, …)
    /// as well as files at the root, and a non-recursive walk silently loses those partitions.
    /// Returns <c>null</c> for an id the corpus does not carry, which the card route refuses by name.
    /// </summary>
    public static Func<string, CardBaseType?> Load(string baseTypesDir)
    {
        var byId = new Dictionary<string, CardBaseType>(StringComparer.Ordinal);
        if (!Directory.Exists(baseTypesDir)) return _ => null;

        foreach (var file in Directory
                     .EnumerateFiles(baseTypesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (Str(entry, "id") is not { Length: > 0 } id) continue;
                    if (Str(entry, "nameKey") is not { Length: > 0 } nameKey) continue;

                    byId[id] = new CardBaseType(
                        nameKey,
                        // Class and role reach the card as KEYS derived from the corpus's own ids —
                        // the same `{kind}.{id}` derivation `RpgStore.GetItemCardInput` already uses
                        // for `rarity.*` / `set.*` / `combo.*`, and whether `gk-content/content/display/en.json`
                        // carries a row for one is `DisplayRules.MissingDisplayKey`'s question.
                        ClassNounKey: Prefixed("class.", Str(entry, "class")),
                        Frame: Str(entry, "frame") ?? "",
                        RoleNameKey: Prefixed("role.", Str(entry, "role")),
                        FlavourKey: Str(entry, "flavorKey"),
                        // item-content T2: the authored English name, read and no longer dropped. All
                        // 740 entries carry one; `gk-content/content/display/en.json` carries no `base.*` row for
                        // any of them, so the key alone left the card showing `base.quilted-sock` in
                        // its own name slot. Module 8's grammar also needs a real noun to glue onto.
                        Name: Str(entry, "name") ?? "");
                }
            }
        }

        return baseTypeId => byId.TryGetValue(baseTypeId, out var value) ? value : null;
    }

    /// <summary>An explicit lookup for tests and for a host with no corpus on disk.</summary>
    public static Func<string, CardBaseType?> From(IReadOnlyDictionary<string, CardBaseType> byId) =>
        baseTypeId => byId.TryGetValue(baseTypeId, out var value) ? value : (CardBaseType?)null;

    static string? Str(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Prefixed(string prefix, string? id) => id is { Length: > 0 } ? prefix + id : "";
}

/// <summary>species-gear-chain T8: a gem entry whose tier cannot be resolved. Thrown at load,
/// naming the gem — a silently under-reported tier is the bug with no symptom this module exists
/// to remove (spec-gem-tier.md §Design 2).</summary>
public sealed class GemCorpusRejection : Exception
{
    public GemCorpusRejection(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The gem catalog the DAL cannot reconstruct — <c>item_socket.insert_container_id</c> names a
/// <c>gem.*</c> container and <see cref="ContainerRow"/> carries no element, tier or family, which is
/// exactly the gap <see cref="ItemCardCorpus.LookupInsert"/> documents.
///
/// <para>⭐ species-gear-chain T8: the corpus <b>carries</b> a tier, in <c>powerBand</c> — an
/// authored `tier` field would be an <c>OwnershipViolation</c> (entry-shapes.md:81), so every
/// insert reports <c>TierOfPowerBand(entry.powerBand)</c> through the one shared mirror two
/// consumers already use. An unknown band is a <c>GemCorpusRejection</c> naming the gem, never a
/// silent fall back to tier 1: an under-reported tier makes a satisfiable combination look
/// unsatisfiable, which is a bonus that vanishes with no symptom.</para>
/// </summary>
public static class GemInsertCorpus
{
    /// <summary>Structural, not a tunable: it is the identity of "this container is not in the gem
    /// corpus at all", and a balance pass has nothing to change here. Narrowed by species-gear-chain
    /// T8 from "every insert" to the lookup-miss fallback only — the corpus path resolves through
    /// <c>UniqueBudget.TierOfPowerBand</c>.</summary>
    public const int UnauthoredInsertTier = 1;

    public static Func<string, CardInsertLookup?> Load(string gemsDir)
    {
        var byId = new Dictionary<string, CardInsertLookup>(StringComparer.Ordinal);
        if (!Directory.Exists(gemsDir)) return _ => null;

        foreach (var file in Directory
                     .EnumerateFiles(gemsDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (Str(entry, "id") is not { Length: > 0 } id) continue;

                    // species-gear-chain T22: the band is read once and feeds BOTH the derived tier
                    // (which throws on an unknown band, unchanged) and the mint seed below, so the
                    // two can never disagree about which band an entry authors.
                    var band = Str(entry, "powerBand");
                    var tier = TierOf(id, band);
                    var family = Str(entry, "family") ?? "";
                    var element = Str(entry, "element");
                    var nameKey = Str(entry, "nameKey") ?? id;
                    var name = Str(entry, "name") ?? "";

                    var tags = new List<string>();
                    if (entry.TryGetProperty("tags", out var tagsEl) &&
                        tagsEl.ValueKind == JsonValueKind.Array)
                        foreach (var tag in tagsEl.EnumerateArray())
                            if (tag.ValueKind == JsonValueKind.String) tags.Add(tag.GetString()!);

                    byId[id] = new CardInsertLookup(
                        new InsertDef(id, family, element ?? "", tier),
                        // §2.4: the CELL shows a display key, never the insert's container id.
                        nameKey,
                        // item-content T4: the authored English name beside the key, never instead of
                        // it. All 40 shipped gems carry one and the catalog has no `gem.*` row, so the
                        // key alone is what a socket picker would have had to offer.
                        name,
                        // T22: the mint seed for socket-insert's real instance. `band!` is safe —
                        // TierOf threw above on a missing or unknown band, so reaching here means the
                        // corpus carries one of bands.v1.json's five.
                        new GemSeed(id, nameKey, name, family, element, band!, Str(entry, "affinityElement"), tags));
                }
            }
        }

        return containerId => byId.TryGetValue(containerId, out var value) ? value : (CardInsertLookup?)null;
    }

    /// <summary>The tier is DERIVED from the authored powerBand, never authored on the entry
    /// (entry-shapes.md:81 makes a `tier` field on a gem an OwnershipViolation; consumables set the
    /// precedent with `grade` from the band's tier). <c>UniqueBudget.TierOfPowerBand</c> is the ONE
    /// mirror of <c>bands.v1.json powerBand.tierMap</c> — a fourth private five-band switch is the
    /// duplication that rule exists to stop. An unknown band THROWS naming the gem: it must never
    /// fall back to <see cref="UnauthoredInsertTier"/>.</summary>
    static int TierOf(string id, string? powerBand)
    {
        try
        {
            return FusionRpg.Core.Items.Uniques.UniqueBudget.TierOfPowerBand(
                powerBand ?? throw new ArgumentOutOfRangeException(nameof(powerBand), powerBand,
                    "missing powerBand"));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new GemCorpusRejection(
                $"gem '{id}' names powerBand '{powerBand}', which is not one of bands.v1.json's five — " +
                "an insert with no resolvable tier would silently under-report every tier-gated combination " +
                "it could have satisfied, which is a bonus that vanishes with no symptom", ex);
        }
    }

    public static Func<string, CardInsertLookup?> From(IReadOnlyDictionary<string, CardInsertLookup> byId) =>
        containerId => byId.TryGetValue(containerId, out var value) ? value : (CardInsertLookup?)null;

    static string? Str(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// ⏸ <b>The third stopgap of the same shape</b> as <see cref="ItemBaseTypeCorpus"/> and
/// <see cref="GemInsertCorpus"/>, and it exists for the same reason: <c>AffixNameTable</c>'s own
/// doc calls <c>item_affix_name</c> a PROJECTION built at import from each family's <c>nameWords</c>,
/// and that import never shipped — no table, no loader, and therefore no production caller for
/// <c>ItemNameComposer</c> (item-content T1, 2026-09-06).
///
/// <para>This walks <c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c> once at boot and keys each family's
/// authored rows by <c>family_id</c>. Core still opens no file: <see cref="AffixNameTable.ParseSlot"/>
/// takes the already-loaded JSON, exactly as it was written to. The day the projection table lands,
/// this class is deleted and the delegate reads the table.</para>
///
/// <para>⛔ <b>A family authors exactly one slot</b> — 58 prefix, 51 suffix across the 109 shipped
/// families — and that slot IS the family's naming side (<see cref="AffixNameSlot.Slot"/> documents
/// why the atom-derived class cannot be used for this today). A file authoring both is a load-time
/// rejection rather than a silent pick, because picking would decide half a grammar by file order.</para>
/// </summary>
public static class AffixNameWordCorpus
{
    public static Func<string, AffixNameSlot?> Load(string affixFamiliesDir)
    {
        var byFamily = new Dictionary<string, AffixNameSlot>(StringComparer.Ordinal);
        if (!Directory.Exists(affixFamiliesDir)) return _ => null;

        foreach (var file in Directory
                     .EnumerateFiles(affixFamiliesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            // `_`-prefixed files are the corpus's own scratch/registry partitions, skipped the same
            // way the real FamilyExpansion readers already skip them.
            if (Path.GetFileName(file).StartsWith('_')) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (Str(entry, "id") is not { Length: > 0 } familyId) continue;
                    if (!entry.TryGetProperty("nameWords", out var nameWords) ||
                        nameWords.ValueKind != JsonValueKind.Object) continue;

                    var hasPrefix = nameWords.TryGetProperty("prefix", out var prefixRows);
                    var hasSuffix = nameWords.TryGetProperty("suffix", out var suffixRows);

                    if (hasPrefix == hasSuffix)
                        throw new AffixNameRejection(
                            $"family '{familyId}' authors "
                            + (hasPrefix ? "both a prefix and a suffix word list" : "an empty nameWords object")
                            + " — a family's single authored slot is what says which half of the naming "
                            + "grammar it fills (ssot-affixes.md §4.12)");

                    byFamily[familyId] = new AffixNameSlot(
                        hasPrefix ? AffixClass.Prefix : AffixClass.Suffix,
                        AffixNameTable.ParseSlot(hasPrefix ? prefixRows : suffixRows));
                }
            }
        }

        return familyId => byFamily.TryGetValue(familyId, out var value) ? value : (AffixNameSlot?)null;
    }

    /// <summary>An explicit lookup for tests and for a host with no corpus on disk.</summary>
    public static Func<string, AffixNameSlot?> From(IReadOnlyDictionary<string, AffixNameSlot> byFamily) =>
        familyId => byFamily.TryGetValue(familyId, out var value) ? value : (AffixNameSlot?)null;

    static string? Str(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// The head/tail table a RARE item's two-word name is drawn from
/// (<c>gk-data/packs/fusion/data/seed/items/rare-names/rare-names.json</c>), and the seeded draw over it.
///
/// <para>⏸ Nothing in the seed tree carried these words before 2026-09-06 — the corpus was searched
/// for <c>rareName</c>/<c>rarePrefix</c>/<c>rareSuffix</c> and had none — which is the third and last
/// reason <c>ItemNameComposer</c> had no production caller. The list is decorative by design: with 3+
/// affixes there is no honest way to name the item after two of them (ssot-affixes.md §4.12).</para>
///
/// <para>⛔ <b>The draw is the shipped <see cref="SeededRng"/>, not a local hash.</b> That class is
/// spec-fixed and version-pinned precisely so a replayable draw stays byte-identical across .NET
/// versions, which is what SC5 asks of a name derived from <c>roll_seed</c>.</para>
/// </summary>
public static class RareNameCorpus
{
    /// <summary>The stream name the head/tail draw runs under. Structural, not a tunable: changing it
    /// renames every rare item in every existing save.</summary>
    public const string RngStream = "item.rare-name";

    /// <summary>Returns <c>null</c> when the file is absent or carries no words — the card then falls
    /// back to the base type's own name rather than inventing one, the same
    /// absence-degrades-never-guesses rule the other two corpora follow.</summary>
    public static Func<long, (string Head, string Tail)>? Load(string rareNamesPath)
    {
        if (!File.Exists(rareNamesPath)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(rareNamesPath)); }
        catch (JsonException) { return null; }

        List<string> heads = new(), tails = new();
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array) return null;

            foreach (var entry in entries.EnumerateArray())
            {
                var slot = entry.TryGetProperty("slot", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
                if (!entry.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
                    continue;

                var target = slot switch { "head" => heads, "tail" => tails, _ => null };
                if (target is null) continue;

                foreach (var w in words.EnumerateArray())
                    if (w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } word)
                        target.Add(word);
            }
        }

        if (heads.Count == 0 || tails.Count == 0) return null;
        return Draw(heads, tails);
    }

    /// <summary>The pure draw, over two already-read lists — a test names its own words without a
    /// file, and the file loader above has no second copy of the seeding rule.</summary>
    public static Func<long, (string Head, string Tail)> Draw(
        IReadOnlyList<string> heads, IReadOnlyList<string> tails)
    {
        if (heads is null || heads.Count == 0) throw new ArgumentException("no head words", nameof(heads));
        if (tails is null || tails.Count == 0) throw new ArgumentException("no tail words", nameof(tails));

        return rollSeed =>
        {
            // A reinterpretation of the seed's bits, not arithmetic on a magnitude: `roll_seed` is an
            // identity and `SeededRng` takes the same 64 bits as unsigned. `checked` would reject a
            // perfectly ordinary negative seed for no reason.
            var rng = SeededRng.DeriveStream(unchecked((ulong)rollSeed), RngStream);
            return (heads[rng.NextInt(heads.Count)], tails[rng.NextInt(tails.Count)]);
        };
    }
}

// ---- wire shapes ----------------------------------------------------------------------------------

/// <summary>
/// One <see cref="DisplayLine"/>, on the wire.
///
/// <para><b>A thin DTO, not a redesign, and it exists for exactly one reason:</b> the default
/// serializer writes a C# enum as a NUMBER, and <c>UnitClass</c>/<c>SourceKind</c> are two closed
/// vocabularies whose members are the contract. A route that emitted <c>"unit": 7</c> would pin the
/// web client to a declaration ORDER, which is the one thing an append-only enum is allowed to
/// change. So both go out as their enum names, the same
/// <c>.ToString()</c> convention <see cref="ItemSurfaceEndpoints"/> already uses for surface state
/// and combination state, and both stay NULLABLE because their null is load-bearing: a structural
/// line (a header, a footer, a requirement clause) carries no magnitude and names no unit.</para>
/// </summary>
public sealed record DisplayLineDto(
    string Key,
    IReadOnlyDictionary<string, string> Args,
    string? Unit,
    string? SourceKind,
    int GroupOrder,
    int? RollBarSegments,
    string? ContextRead,
    int? RollQualityPerMille);

public sealed record DisplayBlockDto(string BlockKey, IReadOnlyList<DisplayLineDto> Lines);

/// <summary>
/// One rendered card — module 10's <see cref="DisplayModel"/>, unchanged.
/// </summary>
/// <param name="Fingerprint"><see cref="DisplayModel.Fingerprint"/>, carried rather than recomputed:
/// it is the byte-identity claim spec-item-card.md makes over one
/// <c>(container_id, catalog_revision, roll_seed)</c>, and a client that caches a card wants the same
/// key the determinism test asserts on.</param>
public sealed record ItemCardDto(
    string InstanceId, IReadOnlyList<DisplayBlockDto> Blocks, string Fingerprint);

/// <summary>
/// One channel's delta, on the wire.
///
/// <para><c>Unit</c> is the <c>UnitClass</c> enum NAME and is nullable, exactly like
/// <see cref="UnitClassGroupDto.Unit"/> — the two are the same lookup's answer (see
/// <c>ChannelDelta</c>'s own note), so a client that reads one vocabulary reads both. Before
/// 2026-09-06 this carried a second vocabulary of its own (<c>"per-mille"</c> / <c>"game-units"</c>,
/// derived from the atom's op) and disagreed with the group it sat in.</para>
///
/// <para><c>IncumbentMax</c> / <c>CandidateMax</c> are non-null only for an <c>OnApply</c> BAND, and
/// then the honest reading is <c>Incumbent … IncumbentMax</c> rather than a point. Additive fields:
/// a client that ignores them reads the band's lower bound, which is what the scalar columns
/// carry.</para>
/// </summary>
public sealed record ChannelDeltaDto(
    string Channel, string? Unit, long Incumbent, long Candidate, long Delta,
    long? IncumbentMax, long? CandidateMax);

public sealed record VerdictBadgeDto(string LabelKey, string Shape);

public sealed record SidegradeTradeDto(
    IReadOnlyList<ChannelDeltaDto> YouGain, IReadOnlyList<ChannelDeltaDto> YouGiveUp);

/// <summary><c>Unit</c> is nullable and its own group — an unresolvable unit is never folded into
/// <c>GameUnits</c>, which is SC4 as a wire shape rather than as a component's good intention.</summary>
public sealed record UnitClassGroupDto(string? Unit, IReadOnlyList<ChannelDeltaDto> Deltas);

/// <summary>
/// <see cref="CompareModel"/>, on the wire. <c>Incumbent</c>/<c>Candidate</c> are
/// <see cref="CompareModel.Left"/>/<see cref="CompareModel.Right"/> under the names
/// <see cref="ItemCardCompare.Compare"/>'s own parameter documentation gives them, so nothing here
/// renames a Core concept.
/// </summary>
public sealed record ItemCompareDto(
    ItemCardDto Incumbent,
    ItemCardDto Candidate,
    IReadOnlyList<int> DifferingLineIndexes,
    IReadOnlyList<ChannelDeltaDto> Deltas,
    string Dominance,
    VerdictBadgeDto Badge,
    SidegradeTradeDto Trade,
    IReadOnlyList<UnitClassGroupDto> UnitGroups,
    int MeanRollQualityMilliIncumbent,
    int MeanRollQualityMilliCandidate,
    string FootnoteKey,
    string? IncomparableReasonKey);

/// <summary>Why a card could not be rendered, in the item program's own refusal vocabulary. Same
/// shape as the workbench's and the equip route's, so <c>httpErrorMessage</c> lifts <c>reason</c> out
/// of any of the three without a special case.</summary>
public sealed record ItemCardRefusalDto(bool Ok, string Reason, string InstanceId);

// ---- the service ----------------------------------------------------------------------------------

/// <summary>
/// ⭐ <b>The production caller module 10 and module 20 both named as their last blocker.</b>
///
/// <para><c>ItemCardRenderer.Render</c>, <c>ItemCardCompare.Compare</c> and
/// <c>DominancePresentation</c> all shipped tested, and <b>nothing outside <c>tests/</c> called any
/// of them</b> — so the web card rendered an honest "pending" for every block. This class calls them
/// against a real stored item, through <see cref="RpgStore.GetItemCardInput"/>.</para>
///
/// <para>⛔ <b>It computes nothing.</b> Every line, every delta, every verdict and the footnote come
/// from the module that owns them, called once. The only thing this file decides is which HTTP
/// status a named refusal gets.</para>
///
/// <para><b>Named gaps, carried forward rather than papered over.</b> <c>SalvageYield</c> and
/// <c>Power</c> stay null (the DAL's own documented omission — both are computed reads over tuning,
/// not stored rows), <c>ItemCardCorpus.ItemName</c> stays empty so the header falls back to the base
/// type's own <c>nameKey</c> (module 8's <c>ItemNameComposer</c> needs the <c>nameWords</c> corpus and
/// the rare two-word draw, neither of which the Server loads), and per-item attribute
/// <c>Requirements</c> stay empty because no shipped table carries one.</para>
/// </summary>
public sealed class ItemCardService
{
    readonly RpgStore _store;
    readonly ItemCardCorpus _corpus;
    readonly EquipGate _gate;
    readonly Func<string, int?> _socketMaxFor;

    /// <param name="socketMaxFor">
    /// The base type's own <c>socketMax</c> — the SAME boot-time lookup the workbench reads, and the
    /// one input that makes the card's combination reachability capacity-aware. Omitted (or null) it
    /// falls back to <c>_ =&gt; null</c>, which makes <c>SocketHost.Capacity</c> unbounded and renders a
    /// combination this chassis can never hold as reachable rather than <c>undiscovered</c> — the
    /// default exists only so a caller with no corpus on hand keeps compiling, and every production
    /// caller passes the real one.
    /// </param>
    public ItemCardService(RpgStore store, ItemCardCorpus corpus, EquipGate? gate = null,
        Func<string, int?>? socketMaxFor = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _corpus = corpus ?? throw new ArgumentNullException(nameof(corpus));
        _gate = gate ?? new EquipGate();
        _socketMaxFor = socketMaxFor ?? (_ => null);
    }

    /// <summary>A rendered card, or a named refusal with the status it deserves. <c>Card</c> and
    /// <c>Reason</c> are mutually exclusive by construction.</summary>
    public sealed record CardResult(ItemCardDto? Card, string Reason, int Status);

    public sealed record CompareResultDto(ItemCompareDto? Compare, string Reason, int Status);

    // ---- one card ----------------------------------------------------------------------------------

    public CardResult Card(string instanceId, string? specimenId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return new CardResult(null, "item.instance-required: name the item to render", StatusCodes.Status400BadRequest);

        if (!TryWearer(specimenId, out var wearer, out var wearerRefusal))
            return new CardResult(null, wearerRefusal, StatusCodes.Status409Conflict);

        return Render(instanceId, wearer, out var card, out var reason, out var status)
            ? new CardResult(card, "", StatusCodes.Status200OK)
            : new CardResult(null, reason, status);
    }

    // ---- two cards ---------------------------------------------------------------------------------

    /// <summary>
    /// The comparison, over two real stored items.
    ///
    /// <para>Both sides are rendered by the SAME <c>ItemCardRenderer.Render</c> the single-card route
    /// calls, and the diff is taken over those rendered lines — spec-item-card.md's own wording:
    /// <i>"comparison diffs rendered lines, so the server must be able to call the same function the
    /// tooltip calls"</i>. A second delta pass here would be the two-implementations defect this
    /// level exists to make impossible.</para>
    /// </summary>
    public CompareResultDto Compare(string candidateId, string incumbentId, string? specimenId)
    {
        if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(incumbentId))
            return new CompareResultDto(null,
                "item.instance-required: a comparison needs both an incumbent and a candidate",
                StatusCodes.Status400BadRequest);

        if (string.Equals(candidateId, incumbentId, StringComparison.Ordinal))
            return new CompareResultDto(null,
                $"item.compare-same-instance: '{candidateId}' cannot be weighed against itself",
                StatusCodes.Status409Conflict);

        if (!TryWearer(specimenId, out var wearer, out var wearerRefusal))
            return new CompareResultDto(null, wearerRefusal, StatusCodes.Status409Conflict);

        // The INPUTS are needed as well as the cards: the delta table reads the instances' own frozen
        // atom rows, which is what keeps the numbers under the verdict identical to the numbers on
        // the lines above it.
        if (!TryInput(incumbentId, wearer, out var incumbentInput, out var reason, out var status))
            return new CompareResultDto(null, reason, status);
        if (!TryInput(candidateId, wearer, out var candidateInput, out reason, out status))
            return new CompareResultDto(null, reason, status);

        DisplayModel left, right;
        try
        {
            left = ItemCardRenderer.Render(incumbentInput!);
            right = ItemCardRenderer.Render(candidateInput!);
        }
        catch (DisplayTemplateRejection ex)
        {
            return new CompareResultDto(null, $"item.display-template-missing: {ex.Message}",
                StatusCodes.Status409Conflict);
        }

        var model = ItemCardCompare.Compare(
            left, right,
            ItemCardCompare.AtomsOf(incumbentInput!.Instance, incumbentInput.LookupAtom),
            ItemCardCompare.AtomsOf(candidateInput!.Instance, candidateInput.LookupAtom),
            _corpus.Registry);

        return new CompareResultDto(
            new ItemCompareDto(
                Incumbent: ToDto(incumbentId, model.Left),
                Candidate: ToDto(candidateId, model.Right),
                DifferingLineIndexes: model.DifferingLineIndexes,
                Deltas: model.Deltas.Select(ToDto).ToList(),
                Dominance: model.Dominance.ToString(),
                Badge: new VerdictBadgeDto(model.Badge.LabelKey, model.Badge.Shape),
                Trade: new SidegradeTradeDto(
                    model.Trade.YouGain.Select(ToDto).ToList(),
                    model.Trade.YouGiveUp.Select(ToDto).ToList()),
                UnitGroups: model.UnitGroups
                    .Select(g => new UnitClassGroupDto(g.Unit?.ToString(), g.Deltas.Select(ToDto).ToList()))
                    .ToList(),
                MeanRollQualityMilliIncumbent: model.MeanRollQualityMilliLeft,
                MeanRollQualityMilliCandidate: model.MeanRollQualityMilliRight,
                FootnoteKey: model.FootnoteKey,
                IncomparableReasonKey: model.IncomparableReasonKey),
            "", StatusCodes.Status200OK);
    }

    // ---- shared ------------------------------------------------------------------------------------

    bool Render(string instanceId, ItemCardWearer? wearer, out ItemCardDto? card, out string reason, out int status)
    {
        card = null;
        if (!TryInput(instanceId, wearer, out var input, out reason, out status)) return false;

        try
        {
            card = ToDto(instanceId, ItemCardRenderer.Render(input!));
        }
        catch (DisplayTemplateRejection ex)
        {
            // §2.4's rule, surfaced rather than swallowed: a family with no template would put a raw
            // id on a tooltip, so the renderer refuses and so does this route.
            reason = $"item.display-template-missing: {ex.Message}";
            status = StatusCodes.Status409Conflict;
            return false;
        }

        reason = "";
        status = StatusCodes.Status200OK;
        return true;
    }

    bool TryInput(string instanceId, ItemCardWearer? wearer, out ItemCardInput? input, out string reason, out int status)
    {
        input = null;
        try
        {
            input = _store.GetItemCardInput(instanceId, _corpus, wearer, _socketMaxFor);
        }
        catch (InvalidOperationException ex)
        {
            // The DAL's hard failures, each already named there: a container the base-type corpus does
            // not carry, a filled socket the gem catalog does not carry, a rarity off the stored
            // ladder. All three are "this card cannot be assembled honestly", never a 500.
            reason = $"item.card-unrenderable: {ex.Message}";
            status = StatusCodes.Status409Conflict;
            return false;
        }

        if (input is null)
        {
            // The three genuinely absent rows the DAL distinguishes by returning null: an unowned
            // instance, a missing `effect_instance`, a container the catalog does not have.
            reason = $"item.unknown: no owned item '{instanceId}'";
            status = StatusCodes.Status404NotFound;
            return false;
        }

        reason = "";
        status = StatusCodes.Status200OK;
        return true;
    }

    /// <summary>
    /// Who is asking. Absent, the card still renders — it just carries no requirement block, no
    /// refusal and no set progress, because all three are facts about a WEARER. A specimen id that
    /// names nothing is refused rather than silently treated as absent: the caller asked a question
    /// about a creature, and answering about no creature at all would be a different answer.
    /// </summary>
    bool TryWearer(string? specimenId, out ItemCardWearer? wearer, out string reason)
    {
        wearer = null;
        reason = "";
        if (specimenId is not { Length: > 0 }) return true;

        var row = _store.GetUniqueActor(specimenId);
        if (row is null)
        {
            reason = $"equip.specimen-unknown: no bound creature '{specimenId}'";
            return false;
        }

        // ⚠ `UniqueActorDto.Level` is a `long` and `SpecimenActor.Level` is an `int` — a level is a
        // ladder INDEX, not a magnitude, so `int` is the repo's shape for it. The narrowing is
        // `checked` for the same reason `ItemEquipService` checks it: a clamp would silently admit an
        // item the level gate should refuse.
        wearer = new ItemCardWearer(
            new SpecimenActor(row.InstanceId, Frame: null, checked((int)row.Level), Faction: null), _gate);
        return true;
    }

    /// <summary><b>Public since 2026-09-06</b> (item-content module <c>atom-preview</c>): the preview
    /// route returns the same <see cref="ItemCardDto"/> shape this route does, and it must be the same
    /// projection rather than a second one — a preview that reshaped a line would stop being a preview
    /// of what ships.</summary>
    public static ItemCardDto ToDto(string instanceId, DisplayModel model) => new(
        instanceId,
        model.Blocks
            .Select(b => new DisplayBlockDto(b.BlockKey, b.Lines.Select(ToDto).ToList()))
            .ToList(),
        model.Fingerprint());

    static DisplayLineDto ToDto(DisplayLine line) => new(
        line.Key, line.Args, line.Unit?.ToString(), line.SourceKind?.ToString(), line.GroupOrder,
        line.RollBar?.Segments, line.ContextRead, line.RollQualityPerMille);

    static ChannelDeltaDto ToDto(ChannelDelta d) =>
        new(d.Channel, d.Unit?.ToString(), d.Incumbent, d.Candidate, d.Delta, d.IncumbentMax, d.CandidateMax);
}

/// <summary>
/// item module 20 (<c>item-surfaces</c>) — the <b>see</b> and <b>compare</b> surfaces' own read-only
/// routes, and the last two of the six that had no server half.
///
/// <para>Its own file rather than more lines in <see cref="ItemSurfaceEndpoints"/> for the same
/// file-per-concern reason <c>WorkbenchEndpoints.cs</c> and <c>ItemEquipEndpoints.cs</c> are separate:
/// these two need a corpus and a gate that the three armoury routes do not. Both are
/// <c>MapGet</c> — module 20 carries no write path, deliberately.</para>
/// </summary>
public static class ItemCardEndpoints
{
    public static void MapItemCard(this WebApplication app, ItemCardService cards)
    {
        if (cards is null) throw new ArgumentNullException(nameof(cards));

        // The eleven blocks of one item, rendered. `specimenId` is optional and adds the three
        // wearer-shaped blocks (requirements, refusal, set progress) — an item in the bag advances no
        // set and refuses nothing, so its absence is a different card, not a poorer one.
        app.MapGet("/api/items/{instanceId}/card", (string instanceId, string? specimenId) =>
            Render(instanceId, cards.Card(instanceId, specimenId)));

        // Incumbent versus candidate. The path reads "compare THIS item against THAT one", so
        // `{instanceId}` is the candidate the player is deciding about and `{incumbentId}` is what
        // they are already wearing — the same direction `ItemCardCompare.Compare(left, right)` takes.
        app.MapGet("/api/items/{instanceId}/compare/{incumbentId}",
            (string instanceId, string incumbentId, string? specimenId) =>
                Render(instanceId, cards.Compare(instanceId, incumbentId, specimenId)));
    }

    /// <summary>
    /// A refused render is the named rule at the status it deserves — <b>404 when the row genuinely
    /// is not there</b>, 409 when it is there and cannot be rendered honestly, 400 when the request
    /// itself is malformed. Never a 500, and never a 200 carrying an empty card: an empty card and an
    /// unrenderable one look identical to a player and must not look identical on the wire.
    /// </summary>
    static IResult Render(string instanceId, ItemCardService.CardResult result) =>
        result.Card is { } card
            ? Results.Ok(card)
            : Results.Json(new ItemCardRefusalDto(false, result.Reason, instanceId), statusCode: result.Status);

    static IResult Render(string instanceId, ItemCardService.CompareResultDto result) =>
        result.Compare is { } compare
            ? Results.Ok(compare)
            : Results.Json(new ItemCardRefusalDto(false, result.Reason, instanceId), statusCode: result.Status);
}
