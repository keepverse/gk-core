using System.Text.Json;
using System.Text.Json.Nodes;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Power;
using FusionRpg.Data;

namespace FusionRpg.Server;

// ---- wire shapes ------------------------------------------------------------------------------------

/// <summary>One fixed-core entry, field-for-field against <see cref="ContainerAtomRow"/>.</summary>
public sealed record PreviewAtomDto(int Seq, string AtomId, string? OverridesJson = null);

/// <summary>One weighted pool entry, field-for-field against <see cref="ContainerPoolRow"/>.</summary>
public sealed record PreviewPoolDto(string AffixId, int Weight, string? Group = null);

/// <summary>
/// An <b>unsaved</b> container, posted for preview.
///
/// <para>Every field down to <see cref="Pool"/> is <see cref="ContainerRow"/>'s own, camelCased and
/// nothing else — spec-atom-preview.md §5's rule, so a container an author is about to write can be
/// posted with no translation step in between. Nothing on this record is stored: the route mints an
/// instance in memory, renders it, and drops both.</para>
///
/// <para>The four fields below <see cref="Pool"/> are the preview's OWN inputs, and none of them is a
/// property of a container:</para>
/// <list type="bullet">
/// <item><see cref="BaseTypeId"/> — which module 6 base type supplies the header. Defaults to
/// <see cref="ContainerId"/>, which is what a real minted item's container id already is.</item>
/// <item><see cref="RollSeed"/> — the pool draw. Defaults to 0, so a preview is reproducible from the
/// posted body alone; an author who wants a different draw sends a different seed.</item>
/// <item><see cref="ThetaContent"/> — the drop depth. Defaults to the LOADED power tuning's own pin
/// index, where <c>contentScale</c> is exactly ×1.000 — not a literal, and not the silent 1.0
/// spec-content-scale.md §2.4 forbids: the pin is a real number read off the tuning the server
/// booted with.</item>
/// <item><see cref="ItemLevel"/> — the header's <c>ilvl</c> token. An author's own number, rendered
/// as posted.</item>
/// </list>
/// </summary>
public sealed record ItemPreviewRequestDto
{
    public string ContainerId { get; init; } = "";
    public string? Kind { get; init; }
    public string? Slot { get; init; }
    public string? Rarity { get; init; }
    public int? MinTier { get; init; }
    public int? MaxTier { get; init; }
    public int? LevelReq { get; init; }
    public int PrefixRolls { get; init; }
    public int SuffixRolls { get; init; }
    public string? TagsJson { get; init; }
    public IReadOnlyList<PreviewAtomDto>? Atoms { get; init; }
    public IReadOnlyList<PreviewPoolDto>? Pool { get; init; }

    public string? BaseTypeId { get; init; }
    public long? RollSeed { get; init; }
    public int? ThetaContent { get; init; }
    public int ItemLevel { get; init; }
}

/// <summary>
/// One rendered variant of the posted container.
/// </summary>
/// <param name="Mode">
/// <c>rolled</c>, <c>min</c> or <c>max</c> — see <see cref="ItemPreviewService.RollModes"/>.
/// </param>
/// <param name="Card">Module 10's <see cref="DisplayModel"/> on the wire, the SAME
/// <see cref="ItemCardDto"/> shape <c>GET /api/items/{instanceId}/card</c> returns, so a client
/// already adapting one adapts the other with no second reader.</param>
public sealed record ItemPreviewCardDto(string Mode, ItemCardDto Card);

/// <param name="ContainerId">Echoed back so a client rendering several previews can tell them apart.</param>
/// <param name="ThetaContent">The depth actually used — the request's, or the tuning pin it defaulted to.</param>
/// <param name="ContentScaleMilli">What that depth multiplied every scaled magnitude by. 1000 = ×1.000.</param>
public sealed record ItemPreviewDto(
    string ContainerId,
    long RollSeed,
    int ThetaContent,
    long ContentScaleMilli,
    IReadOnlyList<ItemPreviewCardDto> Cards);

/// <summary>Why a preview could not be rendered. <c>ok</c>/<c>reason</c> match the card, workbench and
/// equip refusals, so <c>httpErrorMessage</c> lifts <c>reason</c> out of this one too.</summary>
public sealed record ItemPreviewRefusalDto(bool Ok, string Reason, string ContainerId);

// ---- the service ------------------------------------------------------------------------------------

/// <summary>
/// ⭐ <b>item-content module <c>atom-preview</c></b> — the surface an author uses to see what a
/// container renders as <i>before</i> it is saved or shipped.
///
/// <para>⛔ <b>It renders nothing itself.</b> The card comes out of <c>ItemCardRenderer.Render</c>,
/// unmodified and uncopied, and the DTO projection is <see cref="ItemCardService.ToDto"/> — the same
/// one the real card route uses. spec-atom-preview.md §7's first rule, and ssot-presentation.md §8.6's
/// named failure mode ("a second renderer appears and the two drift") is the reason it is a rule.</para>
///
/// <para>⛔ <b>No write path.</b> The posted container is never upserted, the minted instance is never
/// saved, and no ownership row is created — the preview exists entirely inside one request. This is
/// the same stance <c>ItemCardEndpoints</c> takes for the same reason, not a limitation to lift later.</para>
///
/// <para><b>What it reads from the store, and why that is not a contradiction:</b> the ATOM catalog,
/// the affix library and the display templates. The container is the thing being authored; the atoms
/// it references already exist, and resolving them against anything other than the shipped catalog
/// would preview a card the game cannot produce.</para>
///
/// <para><b>Named gaps, carried rather than invented.</b> Blocks 7/8/9/10 (sockets, set, granted
/// actions, flavour) render EMPTY: sockets and set progress are facts about a stored instance and a
/// wearer, and <c>item_granted_action</c>/<c>item_unique</c> are keyed on a container that, by
/// definition, is not saved yet. Block 6 (enhancement) is empty for the same reason — an unsaved
/// container has no mutation ledger. An empty block is an empty block, never a guessed one.</para>
/// </summary>
public sealed class ItemPreviewService
{
    /// <summary>
    /// The three variants every preview returns, in this order.
    ///
    /// <para><c>rolled</c> is the instance exactly as <c>Instantiator.TryInstantiate</c> produced it.
    /// <c>min</c> and <c>max</c> pin each atom's magnitude to the bottom and the top of its own
    /// authored value spec, which is spec-atom-preview.md §2's criterion 4: an author sees a family's
    /// display template render at both ends of the range they authored, without waiting for a drop to
    /// land there.</para>
    /// </summary>
    public const string ModeRolled = "rolled";
    public const string ModeMin = "min";
    public const string ModeMax = "max";

    public static readonly IReadOnlyList<string> RollModes = new[] { ModeRolled, ModeMin, ModeMax };

    readonly RpgStore _store;
    readonly ItemCardCorpus _corpus;
    readonly PowerTuning _power;

    public ItemPreviewService(RpgStore store, ItemCardCorpus corpus, PowerTuning power)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _corpus = corpus ?? throw new ArgumentNullException(nameof(corpus));
        _power = power ?? throw new ArgumentNullException(nameof(power));
    }

    /// <summary>A rendered preview, or a named refusal with the status it deserves. <c>Preview</c> and
    /// <c>Reason</c> are mutually exclusive by construction.</summary>
    public sealed record PreviewResult(ItemPreviewDto? Preview, string Reason, int Status);

    public PreviewResult Preview(ItemPreviewRequestDto? request)
    {
        if (request is null)
            return Refuse("item.preview.body-required: post a container definition", StatusCodes.Status400BadRequest);

        if (string.IsNullOrWhiteSpace(request.ContainerId))
            return Refuse("item.preview.container-id-required: a container needs an id to preview",
                StatusCodes.Status400BadRequest);

        var kindText = string.IsNullOrWhiteSpace(request.Kind) ? nameof(ContainerKind.Item) : request.Kind!;
        if (!Enum.TryParse<ContainerKind>(kindText, ignoreCase: true, out var kind))
            return Refuse(
                $"item.preview.kind-unknown: '{kindText}' is not one of {string.Join('/', Enum.GetNames<ContainerKind>())}",
                StatusCodes.Status400BadRequest);

        var atoms = request.Atoms ?? Array.Empty<PreviewAtomDto>();
        if (atoms.Count == 0)
            return Refuse(
                "item.preview.atoms-required: a container with no fixed core has no base-stat line to render",
                StatusCodes.Status400BadRequest);

        var container = new ContainerRow
        {
            ContainerId = request.ContainerId,
            Kind = kind,
            Slot = request.Slot,
            Rarity = request.Rarity,
            MinTier = request.MinTier,
            MaxTier = request.MaxTier,
            LevelReq = request.LevelReq,
            PrefixRolls = request.PrefixRolls,
            SuffixRolls = request.SuffixRolls,
            TagsJson = string.IsNullOrWhiteSpace(request.TagsJson) ? "{}" : request.TagsJson!,
            Atoms = atoms.Select(a => new ContainerAtomRow(a.Seq, a.AtomId, a.OverridesJson)).ToList(),
            Pool = (request.Pool ?? Array.Empty<PreviewPoolDto>())
                .Select(p => new ContainerPoolRow(p.AffixId, p.Weight, p.Group)).ToList(),
        };

        // Memoised exactly as `RpgStore.GetItemCardInput` memoises them, and for the same reason: a
        // card with twelve atoms across four families does four template queries, not twelve.
        var atomCache = new Dictionary<string, AtomRow?>(StringComparer.Ordinal);
        var templateCache = new Dictionary<string, DisplayTemplateRow?>(StringComparer.Ordinal);
        var affixCache = new Dictionary<string, AffixRow?>(StringComparer.Ordinal);

        AtomRow? LookupAtom(string id) =>
            atomCache.TryGetValue(id, out var cached) ? cached : atomCache[id] = _store.GetAtom(id);

        DisplayTemplateRow? LookupTemplate(string family) =>
            templateCache.TryGetValue(family, out var cached)
                ? cached
                : templateCache[family] = _store.GetDisplayTemplate(family);

        AffixRow? LookupAffix(string id) =>
            affixCache.TryGetValue(id, out var cached) ? cached : affixCache[id] = _store.GetAffix(id);

        var baseTypeId = string.IsNullOrWhiteSpace(request.BaseTypeId) ? container.ContainerId : request.BaseTypeId!;
        var baseType = _corpus.LookupBaseType(baseTypeId);
        if (baseType is null)
            return Refuse(
                $"item.preview.base-type-unknown: module 6's corpus has no base type '{baseTypeId}' — " +
                "a card with no base type has no header, and inventing one would preview an item that " +
                "cannot drop",
                StatusCodes.Status409Conflict);

        CardRarity rarity;
        try
        {
            // The SAME read the real card route makes, off the SAME stored ladder — never a second
            // pip count and never a second palette index.
            rarity = _store.ReadRarity(container.Rarity, _corpus.Palette);
        }
        catch (InvalidOperationException ex)
        {
            return Refuse($"item.preview.rarity-unknown: {ex.Message}",
                StatusCodes.Status409Conflict);
        }

        var theta = request.ThetaContent ?? _power.Curve.PinIndex;
        var rollSeed = request.RollSeed ?? 0L;

        InstanceRow? instance;
        AtomRejection mint;
        try
        {
            mint = Instantiator.TryInstantiate(
                container, LookupAtom, LookupAffix, rollSeed, theta, _power, out instance,
                InstanceOrigin.Drop, catalogRevision: _store.GetCatalogRevision());
        }
        catch (OverflowException ex)
        {
            // AGENTS.md's rule: overflow THROWS rather than wrapping. A Θ an author typed can reach
            // one, and the honest answer is the named refusal, not a 500.
            return Refuse($"item.preview.magnitude-overflow: Θ_content {theta} overflows a magnitude — {ex.Message}",
                StatusCodes.Status409Conflict);
        }

        if (!mint.IsOk || instance is null)
            // `ContainerValidator`'s own closed rejection vocabulary — an atom id the catalog does not
            // have, a duplicate family, a pool budget the pool cannot fill. Named, never a 500.
            return Refuse($"item.preview.container-rejected: {mint}",
                StatusCodes.Status409Conflict);

        var cards = new List<ItemPreviewCardDto>(RollModes.Count);
        foreach (var mode in RollModes)
        {
            var shown = mode switch
            {
                ModeMin => PinnedToBound(instance, container, LookupAtom, takeMax: false),
                ModeMax => PinnedToBound(instance, container, LookupAtom, takeMax: true),
                _ => instance,
            };

            var input = new ItemCardInput(
                shown, container, LookupAtom, LookupTemplate, baseType.Value, rarity,
                ItemName: _corpus.ItemName.Length > 0 ? _corpus.ItemName : baseType.Value.NameKey)
            {
                ItemLevel = request.ItemLevel,
                LevelReq = container.LevelReq,
                Registry = _corpus.Registry,
            };

            try
            {
                cards.Add(new ItemPreviewCardDto(mode, ItemCardService.ToDto($"preview:{mode}", ItemCardRenderer.Render(input))));
            }
            catch (DisplayTemplateRejection ex)
            {
                // §2.4's rule, surfaced rather than swallowed — and this is the refusal the preview
                // surface exists to show an author EARLY: a family with no live template would put a
                // raw id on a tooltip, so the renderer refuses and so does this route.
                return Refuse($"item.display-template-missing: {ex.Message}",
                    StatusCodes.Status409Conflict);
            }
        }

        return new PreviewResult(
            new ItemPreviewDto(container.ContainerId, rollSeed, theta, instance.ContentScaleMilli, cards),
            "", StatusCodes.Status200OK);
    }

    // ---- the Min / Max ends of the authored range ------------------------------------------------------

    /// <summary>
    /// The same instance with every atom's magnitude pinned to the bottom (or the top) of its OWN
    /// authored value spec.
    ///
    /// <para>⛔ <b>Not a second roll and not a second renderer.</b> It rewrites exactly one key —
    /// <c>values_json.amount</c>, which is the only magnitude the card reads
    /// (<c>ItemCard.Magnitude</c> and <c>ArmouryCompare.RollQualityMilli</c> both read that key and no
    /// other) — and it picks the bound through the SAME <see cref="AtomJson.TryReadValueSpec"/> reader
    /// and applies the SAME <see cref="ContentScale.Apply"/> scaler <c>Instantiator.Freeze</c> uses.
    /// The three policy arms below are Freeze's own three arms, with <c>spec.Resolve(rng)</c> replaced
    /// by the authored bound:</para>
    /// <list type="bullet">
    /// <item><c>OnInstantiate</c> — content-scaled, because the item carries it.</item>
    /// <item><c>OnApply</c> — left UNSCALED, because it belongs to the hit and Freeze exempts it. What
    /// changes is that the band collapses to the bound, so the template renders one end of it rather
    /// than <c>12–30%</c>.</item>
    /// <item><c>Fixed</c> — untouched. <c>min == max</c> by definition, so there is no other end.</item>
    /// </list>
    ///
    /// <para>The container's own <c>OverridesJson</c> is merged over the atom's params first, exactly
    /// as Freeze merges it: an override that narrows a range is the range this preview must show.</para>
    /// </summary>
    static InstanceRow PinnedToBound(
        InstanceRow instance, ContainerRow container, Func<string, AtomRow?> lookupAtom, bool takeMax)
    {
        var overrideBySeq = container.Atoms.ToDictionary(a => a.Seq, a => a.OverridesJson);
        var rows = new List<InstanceAtomRow>(instance.Atoms.Count);

        foreach (var row in instance.Atoms)
        {
            var atom = lookupAtom(row.AtomId);
            overrideBySeq.TryGetValue(row.Seq, out var overridesJson);

            if (atom is null || !TryBound(atom, overridesJson, instance.ContentScaleMilli, takeMax, out var pinned))
            {
                rows.Add(row);
                continue;
            }

            rows.Add(row with { ValuesJson = WithAmount(row.ValuesJson, pinned) });
        }

        return instance with { Atoms = rows };
    }

    /// <summary>The authored bound for one atom, or <c>false</c> when there is no other end to show.</summary>
    static bool TryBound(AtomRow atom, string? overridesJson, long contentScaleMilli, bool takeMax, out long value)
    {
        value = 0;

        var raw = AmountOf(overridesJson) ?? AmountOf(atom.ParamsJson);
        if (raw is not { } amount) return false;
        if (!AtomJson.TryReadValueSpec(amount, out var spec).IsOk) return false;
        if (spec.Min == spec.Max) return false;

        var bound = takeMax ? spec.Max : spec.Min;
        switch (spec.Roll)
        {
            case RollPolicy.OnInstantiate:
                value = ContentScale.Apply(bound, contentScaleMilli);
                return true;
            case RollPolicy.OnApply:
                value = bound;
                return true;
            default:
                // `Fixed` is unreachable here (min == max already returned false) and any policy added
                // later has no proven bound semantics — leaving the row alone is the safe answer.
                return false;
        }
    }

    static JsonElement? AmountOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("amount", out var a) ? a.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Replaces <c>amount</c> and nothing else — the resolved <c>channel</c> a pooled affix
    /// froze, and every other param Freeze passed through, survive untouched.</summary>
    static string WithAmount(string valuesJson, long amount)
    {
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(string.IsNullOrWhiteSpace(valuesJson) ? "{}" : valuesJson) as JsonObject
                  ?? new JsonObject();
        }
        catch (JsonException) { obj = new JsonObject(); }

        obj["amount"] = JsonValue.Create(amount);
        return obj.ToJsonString();
    }

    static PreviewResult Refuse(string reason, int status) => new(null, reason, status);
}

// ---- the route --------------------------------------------------------------------------------------

/// <summary>
/// item-content module <c>atom-preview</c> — one POST, and its own file for the same
/// file-per-concern reason <c>ItemCardEndpoints.cs</c> and <c>WorkbenchEndpoints.cs</c> are separate.
///
/// <para>It is a <c>MapPost</c> only because the payload is a whole container definition, not because
/// it writes anything. Nothing this route touches is persisted.</para>
/// </summary>
public static class ItemPreviewEndpoints
{
    public static void MapItemPreview(this WebApplication app, ItemPreviewService previews)
    {
        if (previews is null) throw new ArgumentNullException(nameof(previews));

        app.MapPost("/api/items/preview/card", (ItemPreviewRequestDto? body) =>
        {
            var result = previews.Preview(body);
            return result.Preview is { } preview
                ? Results.Ok(preview)
                : Results.Json(
                    new ItemPreviewRefusalDto(false, result.Reason, body?.ContainerId ?? ""),
                    statusCode: result.Status);
        });
    }
}
