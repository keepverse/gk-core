using System.Globalization;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Power;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Core.Items.Uniques;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Display;

/// <summary>
/// G3 §4.1's eleven blocks, in order. <b>Closed</b> — §9's own failure-mode note says adding a block
/// is a reviewed change, and the order being a static list rather than a configuration table is
/// deliberate (§5.3: "a reorderable card is a table nobody reorders and a bug surface everybody
/// inherits").
/// </summary>
public static class CardBlocks
{
    public const string Header = "item.card.header";
    public const string Requirements = "item.card.requirements";
    public const string BaseStats = "item.card.base-stats";
    public const string Implicit = "item.card.implicit";
    public const string Affixes = "item.card.affixes";
    public const string Enhancement = "item.card.enhancement";
    public const string Sockets = "item.card.sockets";
    public const string Set = "item.card.set";
    public const string GrantedAction = "item.card.granted-action";
    public const string Flavour = "item.card.flavour";
    public const string Footer = "item.card.footer";

    /// <summary>The eleven, in render order. Every card emits all eleven — an empty block is an empty
    /// block, not an absent one, so a consumer never has to ask "was there no set, or did the renderer
    /// forget?".</summary>
    public static readonly IReadOnlyList<string> Order = new[]
    {
        Header, Requirements, BaseStats, Implicit, Affixes, Enhancement,
        Sockets, Set, GrantedAction, Flavour, Footer,
    };

    /// <summary>The four blocks §4.1 marks as never collapsible are actually eight; these two are the
    /// only ones that may. Carried as data so module 20 does not re-derive the table.</summary>
    public static readonly IReadOnlySet<string> MayCollapse =
        new HashSet<string>(StringComparer.Ordinal) { Flavour, Footer };
}

/// <summary>Module 6's base type, as the card reads it. Every field is a KEY, an authored display
/// string, or a frame/role id — never the base type's <c>container_id</c>, which §2.4 forbids showing.</summary>
/// <param name="Name">
/// The base type's AUTHORED English name (<c>"Quilted Sock"</c>), carried alongside
/// <paramref name="NameKey"/> rather than instead of it (item-content T2, 2026-09-06).
///
/// <para>Both are real and neither replaces the other. <paramref name="NameKey"/> stays the
/// localisation path ssot-presentation.md §5.3 N2 fixes — the string catalog is the SSOT for what a
/// player reads once a key has a row. But the catalog has <b>no <c>base.*</c> row at all</b> today
/// (108 keys, all <c>disptpl.*</c>), so a card that carried only the key showed literally
/// <c>base.card-proof-blade</c> in its own name slot. All 740 shipped base types author a real
/// <c>name</c>, and this is it: the renderer's fallback when the catalog cannot answer, and the string
/// <see cref="ItemNameComposer"/> glues its words onto.</para>
///
/// <para>Empty for a caller with no corpus behind it, in which case the name key is all there is.</para>
/// </param>
public readonly record struct CardBaseType(
    string NameKey, string ClassNounKey, string Frame, string RoleNameKey, string? FlavourKey,
    string Name = "");

/// <summary>Module 7's rung, as the card reads it — I1's three redundant channels, all three
/// (pips, the rung name in text, and the colour), never colour alone.</summary>
public readonly record struct CardRarity(string DisplayKey, int PipCount, string ColorHex);

/// <summary>
/// One requirement clause. I11's own shape: the card must name WHICH number gates
/// (<c>Sinew 32 (29 + 3) — 29 gates</c>), so the unassisted half and the granted half are separate
/// fields rather than one total.
/// </summary>
/// <param name="Unassisted">What the specimen brings on its own — never anything an equippable
/// container grants (I11 §2.7's cycle rule, which <c>UnassistedAttributes</c> already enforces).</param>
public readonly record struct CardRequirement(
    string AttributeKey, long Needed, long Unassisted, long Bonus, bool Gates);

/// <summary>One socket cell. <see cref="InsertNameKey"/> is a display key, never the insert's
/// container or family id, and there is deliberately no tier field: §2.4 never renders one.</summary>
public readonly record struct CardSocketCell(
    int Index, string AffinityKey, bool Crafted, string? InsertNameKey, bool OmniCountsDiversityOnly = false)
{
    public bool IsEmpty => InsertNameKey is null;
}

/// <summary>
/// One combination row, joined to its display key. The state, distance and granted tier all come from
/// <see cref="CombinationDistance.Evaluate"/> — module 16's own single evaluator, called once — so the
/// card cannot promise a resonance the evaluator would not fire.
/// </summary>
/// <param name="IsWord">
/// §4.1 block 7 orders socket content as <i>cells → active resonances → <b>the word</b> → near-misses</i>,
/// and §4.4 keeps <c>word</c> and <c>resonance</c> as separate source kinds. It is not a
/// <see cref="ComboShape"/>: the shape is the item's IDENTITY (module 21's authored output), so
/// inferring the word from it would put the wrong label on the shipped combinations.
/// <para>⛔ <b>Nothing sets this, and it is no longer a wiring gap.</b> The separate authored
/// socket-word corpus was <b>retired</b> — owner ruling "regenerate, do not retain", corpus deleted by
/// SSH2.6 (<c>e79c0fde8</c>), which renamed the kind to <c>combination</c> in both kind tables. The
/// mechanic is alive as sockets → circuits → Strain/Splice, produced by <c>ResonanceGenerator</c> and
/// evaluated by <c>CombinationDistance</c>; this flag and its <c>SourceKind.Word</c> arm survive only
/// so a word-shaped source can be labelled without a second render path. It is never inferred from
/// the shape, and no corpus is expected to set it.</para>
/// </param>
public readonly record struct CardCombination(
    string NameKey, ComboShape Shape, CombinationDisplayState State, int? Distance,
    int GrantedTier, bool AllAttuned, IReadOnlyList<string> MissingKeys, bool IsWord = false);

/// <summary>One rung of a set's threshold ladder. The WHOLE ladder always renders (§4.3) — an
/// inactive threshold is the goal, and hiding it removes the goal.</summary>
public readonly record struct CardSetTier(int PiecesRequired, bool Active, bool IsCapability);

/// <summary>
/// The set block. <paramref name="Redundant"/> is module 12's disclosure requirement, not a rejection:
/// equipping a second copy in another role stays legal, so the card must show "3 / 4" AND say why the
/// fourth did not count.
/// </summary>
/// <param name="FlavourKey">
/// The set's authored lore key, or <c>null</c> for the 24 of 30 shipped sets that have none. Owner
/// decision 2026-09-06: block 10 renders for a set as well as a unique. Absent means the block renders
/// nothing — never an empty section and never a placeholder sentence.
/// </param>
/// <param name="Name">
/// The set's AUTHORED display name (<c>"Sunwoven Almanac"</c>), carried alongside
/// <paramref name="NameKey"/> for the same reason <see cref="CardBaseType.Name"/> is (item-content T2,
/// 2026-09-06): <c>SetCorpus.Parse</c> has always read <c>name</c> into <c>SetDef.DisplayName</c> and
/// <c>item_set.display_name</c> has always round-tripped it, but the card threw it away and derived
/// <c>set.{setId}</c> instead — and the string catalog carries no <c>set.*</c> row, so the set block
/// showed a key. Empty when the set row carries no name.
/// </param>
/// <summary>One OTHER set this same equipped piece touches, beside the primary one block 8 already
/// renders. Module 12's own measured corpus fact is the reason it exists: one shipped item can advance
/// three different "3 / 4"s at once, so a card that renders only the first has rendered a third of the
/// truth. <paramref name="Redundant"/> carries the same disclosure meaning it has on the primary row.</summary>
public readonly record struct CardSetPeer(string SetId, string Name, int Count, int Total, bool Redundant);

public readonly record struct CardSet(
    string NameKey, int Count, int Total, IReadOnlyList<CardSetTier> Ladder, bool Redundant,
    string? FlavourKey = null, string Name = "", IReadOnlyList<CardSetPeer>? Others = null)
{
    /// <summary>The other sets this piece touches, never <c>null</c> — an absent list is an empty one,
    /// so a consumer never has to ask "was there no second set, or did the renderer forget?"</summary>
    public IReadOnlyList<CardSetPeer> OtherSets => Others ?? Array.Empty<CardSetPeer>();
}

/// <summary>G4's granted action, as §4.1 block 9 names it.</summary>
public readonly record struct CardGrantedAction(
    string NameKey, string DescriptionKey, bool BattleOnly, bool AlreadyKnown);

/// <summary>
/// Everything the eleven blocks read, and nothing else. Each field is produced by an
/// already-shipped read path — named per field — so this is an assembly point, never a second
/// mechanism for reading an item.
/// </summary>
/// <param name="Instance">The rolled instance, exactly as <c>Instantiator.TryInstantiate</c> produced
/// it and <c>RpgStore</c> round-trips it. Its <c>values_json</c> is the authoritative magnitude
/// (Rule 3); nothing here re-rolls or re-curves.</param>
/// <param name="Container">The container the instance was minted from — the fixed core's <c>seq</c>
/// set is what tells a core atom from a drawn one, with no extra column and no second bookkeeping.</param>
/// <param name="LookupTemplate">N1's row for a family. <c>null</c> is
/// <see cref="DisplayRules.MissingDisplayTemplate"/> and throws — never a blank line, never a raw id.</param>
/// <param name="ItemName">Module 8's <c>ItemNameComposer.Compose</c> output, or module 17's authored
/// unique name. <b>Module 10 renders the name; it does not derive it</b>
/// (spec-affix-legality.md's own note), which is also why a reroll leaves the name alone for free.</param>
public sealed record ItemCardInput(
    InstanceRow Instance,
    ContainerRow Container,
    Func<string, AtomRow?> LookupAtom,
    Func<string, DisplayTemplateRow?> LookupTemplate,
    CardBaseType BaseType,
    CardRarity Rarity,
    string ItemName)
{
    public int ItemLevel { get; init; }

    /// <summary>Module 15's mutation ledger value (<c>effect_instance.enhance_level</c>). 0 when never
    /// enhanced, and at 0 the header emits no <c>+N</c> token at all.</summary>
    public int EnhanceLevel { get; init; }

    /// <summary>
    /// species-gear-chain T26 — the display key of the FIRST rung this instance was promoted from
    /// (<c>"rarity.cultivated"</c>), or null when it was never promoted. The card shows how far the
    /// item has climbed rather than only where it stands: <see cref="Rarity"/> already carries the
    /// CURRENT rung, which promotion moves. A key, never the ordinal, for the same reason
    /// <see cref="CardRarity.DisplayKey"/> is one — the client resolves it against the same ladder.
    /// </summary>
    public string? PromotedFromRarityKey { get; init; }

    /// <summary>Module 15's <c>EnhancePolicy.GainMilli</c> at <see cref="EnhanceLevel"/>. Rendered as
    /// ONE block (I6 §5.5's suppress-and-append rule), never as stacked per-level lines.</summary>
    public long EnhanceGainMilli { get; init; }

    // ---- requirements (module 4 `equip-assign`) ----------------------------------------------------

    public int? LevelReq { get; init; }
    public int SpecimenLevel { get; init; }

    /// <summary><c>EquipGate.Explain</c>'s answer. Non-null turns the block red and names the axis.</summary>
    public EquipRefusal? Refusal { get; init; }

    public IReadOnlyList<CardRequirement> Requirements { get; init; } = Array.Empty<CardRequirement>();

    // ---- sockets (module 16) -----------------------------------------------------------------------

    public IReadOnlyList<CardSocketCell> Sockets { get; init; } = Array.Empty<CardSocketCell>();
    public IReadOnlyList<CardCombination> Combinations { get; init; } = Array.Empty<CardCombination>();

    // ---- set (module 12 `threshold-grants`) --------------------------------------------------------

    public CardSet? Set { get; init; }

    // ---- granted actions (G4) ----------------------------------------------------------------------

    public IReadOnlyList<CardGrantedAction> GrantedActions { get; init; } = Array.Empty<CardGrantedAction>();

    // ---- uniques (module 17) -----------------------------------------------------------------------

    /// <summary>Non-null makes the fixed core render as <c>unique-identity</c>/<c>unique-variance</c>
    /// (§4.4's exact rule) and unlocks the flavour block. Since the owner's 2026-09-06 decision a set
    /// unlocks it too — see <see cref="CardSet.FlavourKey"/>.</summary>
    public UniqueRow? Unique { get; init; }

    // ---- the string catalog (N2) --------------------------------------------------------------------

    /// <summary>
    /// N2's `gk-content/content/display/en.json`, as a key → sentence lookup
    /// (<see cref="DisplayStringCatalog.Parse"/>). Only block 10 reads it today: every other line's
    /// text comes from a display TEMPLATE row, which the renderer already substitutes into.
    ///
    /// <para><c>null</c>, or a key the catalog does not carry, is <b>not</b> an error and is
    /// <b>never</b> filled in — the line still carries its <c>flavourKey</c>, so the absence is
    /// visible and <see cref="DisplayRules.MissingDisplayKey"/> is the rule that reports it. A
    /// synthesised sentence here is the exact L3/L4 violation this field exists to avoid.</para>
    /// </summary>
    public Func<string, string?>? LookupString { get; init; }

    // ---- footer -------------------------------------------------------------------------------------

    public bool Locked { get; init; }
    public bool Stale { get; init; }
    public bool NoReassign { get; init; }

    /// <summary>Module 19's <c>SalvagePolicy.Yield</c> output. One number reaches the face; the
    /// breakdown is the expandable half.</summary>
    public IReadOnlyList<MaterialCostLine>? SalvageYield { get; init; }

    /// <summary>Module 9's <c>ItemPowerReads.CardPower</c>. Suppressed by its own tuning flag, in which
    /// case no power line is emitted at all — §10 Q7's "ask first" is answered by the tuning, not
    /// here.</summary>
    public CardPowerDisplay? Power { get; init; }

    public DerivedStatRegistry? Registry { get; init; }
}

/// <summary>
/// The <b>Card</b> level of spec-item-card.md's three (Line → Card → Compare): one real rolled
/// instance plus its container, sockets, set, enhancement and requirements, projected onto G3 §4.1's
/// eleven ordered blocks.
///
/// <para>⛔ <b>Every magnitude line goes through <see cref="ItemDisplayRenderer.Line"/>.</b> There is
/// no second path to a line here — that is the rule that stops "the tooltip says X and the list says
/// Y", and it is why this class contains no formatting of its own for any atom's number.</para>
///
/// <para>⛔ <b>§2.4's never-shown list is enforced structurally, not by convention.</b> No atom id,
/// family id, container id, instance id, tier number, name band, group id, or raw per-mille reaches an
/// arg: the group ORDER is an ordinal derived from the template's group id and the id itself is
/// dropped; every name that could have been an id is taken as a display key from the input instead.
/// <c>Nothing_the_spec_forbids_reaches_a_rendered_arg</c> asserts it over a real instance rather than
/// trusting this paragraph.</para>
/// </summary>
public static class ItemCardRenderer
{
    /// <summary>The one place the eleven blocks are assembled. Total over
    /// <see cref="CardBlocks.Order"/> by construction: the result is built by walking that list, so a
    /// block cannot be silently dropped by an early return.</summary>
    public static DisplayModel Render(ItemCardInput input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        var registry = input.Registry ?? DerivedStatRegistry.CreateDefault();
        var lines = Classify(input);
        var groupOrder = GroupOrdinals(input, lines);

        var blocks = new List<DisplayBlock>(CardBlocks.Order.Count);
        foreach (var key in CardBlocks.Order)
        {
            blocks.Add(new DisplayBlock(key, key switch
            {
                CardBlocks.Header => HeaderLines(input),
                CardBlocks.Requirements => RequirementLines(input),
                CardBlocks.BaseStats => AtomLines(input, registry, groupOrder, lines.Base),
                CardBlocks.Implicit => AtomLines(input, registry, groupOrder, lines.Implicit),
                CardBlocks.Affixes => AtomLines(input, registry, groupOrder, lines.Affixes),
                CardBlocks.Enhancement => EnhancementLines(input),
                CardBlocks.Sockets => SocketLines(input),
                CardBlocks.Set => SetLines(input),
                CardBlocks.GrantedAction => GrantedActionLines(input),
                CardBlocks.Flavour => FlavourLines(input),
                CardBlocks.Footer => FooterLines(input, lines),
                _ => throw new InvalidOperationException($"unhandled card block '{key}'"),
            }));
        }

        return new DisplayModel(blocks);
    }

    // ---- which atom belongs to which block -----------------------------------------------------------

    /// <summary>One instance atom, already joined to its catalog row and its authored value spec.</summary>
    readonly record struct Placed(InstanceAtomRow Row, AtomRow Atom, SourceKind Kind);

    readonly record struct Placement(
        IReadOnlyList<Placed> Base, IReadOnlyList<Placed> Implicit, IReadOnlyList<Placed> Affixes);

    /// <summary>
    /// I3 §5.2's split, read off content and nothing else: the container's fixed-core <c>seq</c> set
    /// says which atoms were authored rather than drawn; <c>seq 0</c> is the base stat block and
    /// <c>seq 1</c> is the single implicit. Anything the pool drew is an affix, and its prefix/suffix
    /// side is <c>AffixValidator.AffixClassOfAtom</c>'s own derivation (trigger present ⇒ suffix) —
    /// never a second rule.
    ///
    /// <para>On a UNIQUE the fixed core past <c>seq 1</c> renders in the affix block as
    /// <c>unique-identity</c>, except the one <c>OnInstantiate</c> atom, which is
    /// <c>unique-variance</c> and is the only line on that card with a bar (§4.4).</para>
    /// </summary>
    static Placement Classify(ItemCardInput input)
    {
        var coreSeqs = input.Container.Atoms.Select(a => a.Seq).ToHashSet();
        var baseRows = new List<Placed>();
        var implicitRows = new List<Placed>();
        var affixRows = new List<Placed>();

        foreach (var row in input.Instance.Atoms.OrderBy(a => a.Seq))
        {
            var atom = input.LookupAtom(row.AtomId)
                ?? throw new DisplayTemplateRejection(
                    $"instance references atom '{row.AtomId}', which the catalog does not have");

            if (coreSeqs.Contains(row.Seq))
            {
                if (row.Seq == 0) { baseRows.Add(new Placed(row, atom, SourceKind.Base)); continue; }
                if (row.Seq == 1) { implicitRows.Add(new Placed(row, atom, SourceKind.Implicit)); continue; }

                // Fixed core past the implicit. On an ORDINARY item that is more base stat, and it
                // belongs in block 3 -- a `base` line in the affix block would say a rolled affix is
                // what the base type guarantees. On a UNIQUE it is the item's identity, which §4.4
                // splits by roll policy: the Fixed ones are `unique-identity`, and the one
                // OnInstantiate atom is `unique-variance` and is the only line on that card with a bar.
                if (input.Unique is null)
                {
                    baseRows.Add(new Placed(row, atom, SourceKind.Base));
                    continue;
                }

                affixRows.Add(new Placed(row, atom, RollOf(atom) == RollPolicy.OnInstantiate
                    ? SourceKind.UniqueVariance
                    : SourceKind.UniqueIdentity));
                continue;
            }

            affixRows.Add(new Placed(row, atom,
                AffixValidator.AffixClassOfAtom(atom) == AffixClass.Suffix
                    ? SourceKind.AffixSuffix
                    : SourceKind.AffixPrefix));
        }

        return new Placement(baseRows, implicitRows, affixRows);
    }

    /// <summary>
    /// §4.1's affix ordering key: <b>group order</b>, then tier DESC, then seq ASC. The group order is
    /// the ORDINAL of the template's <c>groupId</c> within this card's own sorted group set —
    /// content-derived and ordinal, the tiebreak discipline definitions §5 already forced on the
    /// effect list. The group id itself never reaches an arg (§2.4).
    /// </summary>
    static IReadOnlyDictionary<string, int> GroupOrdinals(ItemCardInput input, Placement placement)
    {
        var groups = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in placement.Base.Concat(placement.Implicit).Concat(placement.Affixes))
            if (input.LookupTemplate(p.Atom.FamilyId) is { } t) groups.Add(t.GroupId);

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var g in groups) map[g] = i++;
        return map;
    }

    // ---- blocks 3/4/5: the atom lines ------------------------------------------------------------------

    static IReadOnlyList<DisplayLine> AtomLines(
        ItemCardInput input, DerivedStatRegistry registry,
        IReadOnlyDictionary<string, int> groupOrder, IReadOnlyList<Placed> placed)
    {
        var lines = new List<(DisplayLine Line, int Group, int Tier, int Seq, int Side)>(placed.Count);

        foreach (var p in placed)
        {
            var template = input.LookupTemplate(p.Atom.FamilyId)
                ?? throw new DisplayTemplateRejection(
                    $"'{p.Atom.FamilyId}' has no display template row ({DisplayRules.MissingDisplayTemplate}) "
                    + "-- rendering it would put a raw id on a tooltip");

            var group = groupOrder.TryGetValue(template.GroupId, out var g) ? g : int.MaxValue;
            var roll = RollOf(p.Atom);
            var quality = ArmouryCompare.RollQualityMilli(p.Atom, p.Row.ValuesJson);
            var channel = ChannelOf(p.Row.ValuesJson) ?? ChannelOf(p.Atom.ParamsJson);
            var unit = channel is null ? null : ChannelUnits.ForAuthoredChannel(channel, registry);
            var (frozen, bandMax) = Magnitude(p.Row.ValuesJson);

            lines.Add((
                ItemDisplayRenderer.Line(
                    template, p.Atom, input.BaseType.Frame, frozen, p.Kind, group,
                    unit, ElementOf(p.Atom, p.Row.ValuesJson), roll, quality, contextRead: null, bandMax: bandMax),
                group,
                p.Atom.Tier,
                p.Row.Seq,
                // Prefixes then suffixes -- §4.1's first ordering key, before the group.
                p.Kind == SourceKind.AffixSuffix ? 1 : 0));
        }

        return lines
            .OrderBy(x => x.Side)
            .ThenBy(x => x.Group)
            .ThenByDescending(x => x.Tier)
            .ThenBy(x => x.Seq)
            .Select(x => x.Line)
            .ToList();
    }

    // ---- block 1: header ---------------------------------------------------------------------------------

    /// <summary>
    /// One line, because the header is one thing a player reads left to right. §10 Q2's collision is
    /// resolved in the ARG ORDER the key documents — <b>pips → <c>+12</c> → name</b> — because the pips
    /// are the rarity ladder's accessibility channel and must not be displaced by an optional token.
    /// Reversible by moving one entry; the owner's call.
    /// </summary>
    static IReadOnlyList<DisplayLine> HeaderLines(ItemCardInput input)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pips"] = input.Rarity.PipCount.ToString(CultureInfo.InvariantCulture),
            ["rungKey"] = input.Rarity.DisplayKey,
            ["colorHex"] = input.Rarity.ColorHex,
            ["name"] = input.ItemName,
            ["baseNameKey"] = input.BaseType.NameKey,
            // item-content T2: the base type's authored name, beside its key rather than instead of
            // it. The key stays the localisation path; this is what a client renders while the string
            // catalog has no `base.*` row, and it is the noun module 8's grammar was composed onto.
            ["baseName"] = input.BaseType.Name,
            ["classNounKey"] = input.BaseType.ClassNounKey,
            ["roleNameKey"] = input.BaseType.RoleNameKey,
            ["frame"] = input.BaseType.Frame,
            ["ilvl"] = input.ItemLevel.ToString(CultureInfo.InvariantCulture),
        };

        // Absent at +0 rather than present as "+0": a zero enhancement is not a thing the item has.
        if (input.EnhanceLevel > 0)
            args["enhance"] = "+" + input.EnhanceLevel.ToString(CultureInfo.InvariantCulture);

        // species-gear-chain T26: the promotion mark, absent entirely when the item was never
        // promoted. The rung KEY only — the client resolves it against the same ladder it already
        // uses for `rungKey`, so the card never carries an ordinal a client would have to map.
        if (input.PromotedFromRarityKey is { Length: > 0 } promotedFrom)
            args["promotedFromKey"] = promotedFrom;

        // Module 9's read, rendered under Rule P (two significant figures with its band). Suppressed
        // entirely when the tuning says so -- no row, rather than an empty one.
        if (input.Power is { Shown: true } power) args["power"] = power.Render();

        return new[] { new DisplayLine(CardBlocks.Header, args, null, null, 0) };
    }

    // ---- block 2: requirements ---------------------------------------------------------------------------

    static IReadOnlyList<DisplayLine> RequirementLines(ItemCardInput input)
    {
        var lines = new List<DisplayLine>();

        if (input.LevelReq is { } req)
        {
            var met = input.SpecimenLevel >= req;
            lines.Add(new DisplayLine("item.card.requirement.level", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["need"] = req.ToString(CultureInfo.InvariantCulture),
                ["have"] = input.SpecimenLevel.ToString(CultureInfo.InvariantCulture),
                ["met"] = met ? "1" : "0",
            }, UnitClass.Count, null, 0));
        }

        foreach (var r in input.Requirements)
            lines.Add(new DisplayLine("item.card.requirement.attribute", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attrKey"] = r.AttributeKey,
                ["need"] = r.Needed.ToString(CultureInfo.InvariantCulture),
                ["unassisted"] = r.Unassisted.ToString(CultureInfo.InvariantCulture),
                ["bonus"] = r.Bonus.ToString(CultureInfo.InvariantCulture),
                // I11's own wording: the card names WHICH number gates, not just that something does.
                ["gates"] = r.Gates ? "1" : "0",
            }, UnitClass.Count, null, 0));

        if (input.Refusal is { } refusal)
            lines.Add(new DisplayLine("item.card.requirement.refused", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reasonKey"] = "item.equip.refusal." + RefusalSlug(refusal.Reason),
                ["remedy"] = refusal.Remedy,
            }, null, null, 0));

        return lines;
    }

    static string RefusalSlug(EquipRefusalReason reason) => reason switch
    {
        EquipRefusalReason.RoleLocked => "role-locked",
        EquipRefusalReason.RoleNotOnFrame => "role-not-on-frame",
        EquipRefusalReason.LevelTooLow => "level-too-low",
        EquipRefusalReason.FactionMismatch => "faction-mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    // ---- block 6: enhancement -----------------------------------------------------------------------------

    /// <summary>I6 §5.5's suppress-and-append rule, taken at its word: ONE line at the current level,
    /// never one per level. Absent entirely at +0.</summary>
    static IReadOnlyList<DisplayLine> EnhancementLines(ItemCardInput input)
    {
        if (input.EnhanceLevel <= 0) return Array.Empty<DisplayLine>();

        return new[]
        {
            new DisplayLine("item.card.enhancement.level", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["level"] = input.EnhanceLevel.ToString(CultureInfo.InvariantCulture),
                // Per-mille NEVER renders as per-mille (§2.4) -- the shared conversion, not a second one.
                ["gain"] = ItemDisplayRenderer.FormatPerMille(input.EnhanceGainMilli),
            }, UnitClass.PerMilleRatio, SourceKind.Enhancement, 0),
        };
    }

    // ---- block 7: sockets ---------------------------------------------------------------------------------

    /// <summary>
    /// Cells first (empty ones shown as empty), then <c>active</c> resonances, then the word, then
    /// <c>one-away</c>. <c>undiscovered</c> is <b>never rendered at all</b> and <c>known-inactive</c>
    /// renders name-only — §4.3's four closed states, applied to
    /// <see cref="CombinationDistance"/>'s own output rather than re-decided here.
    /// </summary>
    static IReadOnlyList<DisplayLine> SocketLines(ItemCardInput input)
    {
        var lines = new List<DisplayLine>();

        foreach (var cell in input.Sockets.OrderBy(c => c.Index))
            lines.Add(new DisplayLine("item.card.socket.cell", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["index"] = cell.Index.ToString(CultureInfo.InvariantCulture),
                ["affinityKey"] = cell.AffinityKey,
                ["crafted"] = cell.Crafted ? "1" : "0",
                ["empty"] = cell.IsEmpty ? "1" : "0",
                ["insertKey"] = cell.InsertNameKey ?? "",
                // §9.4: an omni insert sitting in a three-fire fill that is not firing Pure looks
                // broken unless its own line says why. So the line says why.
                ["omniDiversityOnly"] = cell.OmniCountsDiversityOnly ? "1" : "0",
            }, null, SourceKind.SocketInsert, cell.Index));

        // §4.1 block 7's order: active resonances, then THE WORD, then near-misses, then the rest.
        // Within a state, the catalog's own order; within `active`, resonances before the word.
        foreach (var state in new[]
                 {
                     CombinationDisplayState.Active,
                     CombinationDisplayState.OneAway,
                     CombinationDisplayState.KnownInactive,
                 })
            foreach (var combo in input.Combinations.Where(c => c.State == state).OrderBy(c => c.IsWord ? 1 : 0))
            {
                var args = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["nameKey"] = combo.NameKey,
                    ["state"] = StateSlug(combo.State),
                    ["attuned"] = combo.AllAttuned ? "1" : "0",
                };
                // A one-away line names the EXACT missing ingredient -- "needs something" is not a hint.
                if (combo.State == CombinationDisplayState.OneAway)
                {
                    args["distance"] = (combo.Distance ?? 0).ToString(CultureInfo.InvariantCulture);
                    args["missingKeys"] = string.Join(",", combo.MissingKeys);
                }

                lines.Add(new DisplayLine("item.card.socket.combination", args, null,
                    combo.IsWord ? SourceKind.Word : SourceKind.Resonance, 0));
            }

        return lines;
    }

    static string StateSlug(CombinationDisplayState state) => state switch
    {
        CombinationDisplayState.Active => "active",
        CombinationDisplayState.OneAway => "one-away",
        CombinationDisplayState.KnownInactive => "known-inactive",
        CombinationDisplayState.Undiscovered => throw new InvalidOperationException(
            "an undiscovered combination is never rendered at all (ssot-presentation.md §4.3)"),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    // ---- block 8: set --------------------------------------------------------------------------------------

    /// <summary>
    /// The whole ladder, always. A set has at most four thresholds, and an inactive one is the GOAL —
    /// hiding it removes the goal. The wording rule that prevents a real lie is enforced by the shape:
    /// a tier line carries its <c>pieces</c> count and no "next" flag, so a screenshot taken an hour ago
    /// is still true.
    /// </summary>
    static IReadOnlyList<DisplayLine> SetLines(ItemCardInput input)
    {
        if (input.Set is not { } set) return Array.Empty<DisplayLine>();

        var lines = new List<DisplayLine>
        {
            new("item.card.set.header", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nameKey"] = set.NameKey,
                // item-content T2: `item_set.display_name`, which SetCorpus has always parsed and the
                // card has always discarded. Beside the key, never instead of it.
                ["name"] = set.Name,
                ["count"] = set.Count.ToString(CultureInfo.InvariantCulture),
                ["total"] = set.Total.ToString(CultureInfo.InvariantCulture),
                // Module 12's disclosure requirement: legal, uncounted, and said out loud.
                ["redundant"] = set.Redundant ? "1" : "0",
            }, UnitClass.Count, SourceKind.SetThreshold, 0),
        };

        foreach (var tier in set.Ladder.OrderBy(t => t.PiecesRequired))
            lines.Add(new DisplayLine("item.card.set.threshold", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pieces"] = tier.PiecesRequired.ToString(CultureInfo.InvariantCulture),
                ["active"] = tier.Active ? "1" : "0",
                ["capability"] = tier.IsCapability ? "1" : "0",
            }, UnitClass.Count, SourceKind.SetThreshold, tier.PiecesRequired));

        // P8.4 — the multi-set per-piece disclosure list, on the SAME block and in the SAME line
        // vocabulary rather than a second route's worth of parallel rendering. Module 12's own corpus
        // fact is why this is not a nicety: 25 of the 154 shipped (role, base type) member pairs belong
        // to more than one set, one to three, so "which other sets does THIS piece advance, and in
        // which is it the redundant copy" is a question the primary row alone cannot answer. One line
        // per peer, ordinally ordered by set id so the wire bytes are stable.
        foreach (var peer in set.OtherSets.OrderBy(p => p.SetId, StringComparer.Ordinal))
            lines.Add(new DisplayLine("item.card.set.other", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nameKey"] = "set." + peer.SetId,
                ["name"] = peer.Name,
                ["count"] = peer.Count.ToString(CultureInfo.InvariantCulture),
                ["total"] = peer.Total.ToString(CultureInfo.InvariantCulture),
                ["redundant"] = peer.Redundant ? "1" : "0",
            }, UnitClass.Count, SourceKind.SetThreshold, 0));

        return lines;
    }

    // ---- block 9: granted action ------------------------------------------------------------------------------

    /// <summary>
    /// Name AND description (<c>ssot-presentation.md</c> §9.14), plus the battle-only tag and the
    /// already-known state.
    ///
    /// <para>item-content <c>granted-action-text</c> (T15): both keys resolve through the string
    /// catalog here, for the same reason block 10 does — an action's description is a CATALOG row,
    /// not a template substitution, so no <c>ItemDisplayRenderer</c> pass will ever reach it. Same
    /// consumer rule as block 10: show <c>__rendered</c>, never the key.</para>
    ///
    /// <para>⛔ <b>An unresolved key emits no <c>__rendered</c> at all</b> — never the key's own tail
    /// and never a sentence synthesised from the action's parameters, which is §8.1's named failure
    /// mode. The absence is <see cref="DisplayRules.MissingDisplayKey"/>'s to report.</para>
    /// </summary>
    static IReadOnlyList<DisplayLine> GrantedActionLines(ItemCardInput input) =>
        input.GrantedActions.Select(a =>
        {
            var args = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nameKey"] = a.NameKey,
                ["descriptionKey"] = a.DescriptionKey,
                ["battleOnly"] = a.BattleOnly ? "1" : "0",
                ["alreadyKnown"] = a.AlreadyKnown ? "1" : "0",
            };

            if (input.LookupString?.Invoke(a.NameKey) is { Length: > 0 } name) args["__renderedName"] = name;
            if (input.LookupString?.Invoke(a.DescriptionKey) is { Length: > 0 } desc) args["__rendered"] = desc;

            return new DisplayLine("item.card.granted-action", args, null, SourceKind.GrantedAction, 0);
        }).ToList();

    // ---- block 10: flavour -------------------------------------------------------------------------------------

    /// <summary>
    /// Uniques <b>and sets</b> (§4.1, widened by the owner's 2026-09-06 decision — spec-item-lore.md
    /// §2.4). A base type's own <c>flavorKey</c> is still not carried: G1's <c>flavour_key</c> is an
    /// item's identity line, and rendering all 740 base types' flavour would make block 10 the tallest
    /// thing on a common drop.
    ///
    /// <para>⛔ <b>Absent is absent.</b> Nothing authored ⇒ no line, never an empty one and never a
    /// placeholder — <c>ssot-presentation.md</c>'s L3/L4 rule that display text is authored, never
    /// synthesised at render time. The same applies one level down: a key whose catalog row is missing
    /// still renders its line with the key and no <c>__rendered</c>, so the gap is reported by
    /// <see cref="DisplayRules.MissingDisplayKey"/> rather than papered over with the key's own tail.</para>
    ///
    /// <para>A unique may not be a set member (<c>UniqueRules.SetMembership</c> refuses it at import),
    /// so in practice at most one of these two ever fires. Both are emitted rather than one being
    /// picked, because "the corpus rule holds" is not something a renderer should assume.</para>
    /// </summary>
    static IReadOnlyList<DisplayLine> FlavourLines(ItemCardInput input)
    {
        var lines = new List<DisplayLine>(2);

        if (input.Unique?.FlavourKey is { Length: > 0 } uniqueKey)
            lines.Add(FlavourLine(input, uniqueKey, SourceKind.UniqueIdentity));

        if (input.Set?.FlavourKey is { Length: > 0 } setKey)
            lines.Add(FlavourLine(input, setKey, SourceKind.SetThreshold));

        return lines;
    }

    static DisplayLine FlavourLine(ItemCardInput input, string key, SourceKind kind)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["flavourKey"] = key };

        // The one place block 10 differs from every other block: its sentence is a CATALOG row, not a
        // template substitution, so the resolution happens here rather than in ItemDisplayRenderer.
        // Same arg name either way, so a consumer has one rule: show `__rendered`, never the key.
        if (input.LookupString?.Invoke(key) is { Length: > 0 } text) args["__rendered"] = text;

        return new DisplayLine("item.card.flavour", args, null, kind, 0);
    }

    // ---- block 11: footer ---------------------------------------------------------------------------------------

    /// <summary>
    /// The mean roll quality is COMPUTED from the same per-atom read the bars above it use
    /// (<c>ArmouryCompare.RollQualityMilli</c>), never passed in — a footer that could disagree with
    /// the bars on the same card is the two-implementations defect in miniature.
    /// </summary>
    static IReadOnlyList<DisplayLine> FooterLines(ItemCardInput input, Placement placement)
    {
        var rolled = placement.Affixes
            .Where(p => RollOf(p.Atom) == RollPolicy.OnInstantiate)
            .Select(p => ArmouryCompare.RollQualityMilli(p.Atom, p.Row.ValuesJson))
            .ToList();

        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["meanRollQuality"] = rolled.Count == 0
                ? ""
                // Integer mean, rounded away from zero exactly once, through the engine's own divide.
                : ItemDisplayRenderer.FormatPerMille(
                    ItemDisplayRenderer.RoundAwayFromZero(rolled.Sum(r => (long)r), rolled.Count)),
            ["stale"] = input.Stale ? "1" : "0",
            ["locked"] = input.Locked ? "1" : "0",
            ["noReassign"] = input.NoReassign ? "1" : "0",
        };

        if (input.SalvageYield is { Count: > 0 } yield)
        {
            long total = 0;
            foreach (var line in yield) total = checked(total + line.Qty);
            args["salvageUnits"] = total.ToString(CultureInfo.InvariantCulture);
            args["salvageLines"] = yield.Count.ToString(CultureInfo.InvariantCulture);
        }

        return new[] { new DisplayLine(CardBlocks.Footer, args, null, null, 0) };
    }

    // ---- reads off the frozen instance / the authored atom ----------------------------------------------------------

    /// <summary>The atom's own authored roll policy, read from its <c>params.amount</c> value spec —
    /// the same reader <c>Instantiator.Freeze</c> used to freeze it, so the bar rule and the freeze rule
    /// can never disagree about what rolled.</summary>
    static RollPolicy RollOf(AtomRow atom)
    {
        if (!TryAmount(atom.ParamsJson, out var raw)) return RollPolicy.Fixed;
        return AtomJson.TryReadValueSpec(raw, out var spec).IsOk ? spec.Roll : RollPolicy.Fixed;
    }

    /// <summary>
    /// What the line has to show: a frozen integer, or an <c>OnApply</c> BAND.
    ///
    /// <para><c>Instantiator.Freeze</c> writes a plain number for a <c>Fixed</c> or
    /// <c>OnInstantiate</c> spec and deliberately copies an <c>OnApply</c> spec through <b>as
    /// authored</b> — the hit rolls it, not the item — so an <c>OnApply</c> row's <c>values_json</c>
    /// still carries <c>{min, max, roll}</c>. That is not a missing magnitude: it is the band §3.4
    /// requires the card to show instead of a value, and it is what the shipped corpus authors for
    /// every generated affix (<c>ssot-affixes.md:422</c>). Returning both bounds is how the renderer
    /// tells the two apart without a second parse.</para>
    /// </summary>
    static (long? Value, long? BandMax) Magnitude(string valuesJson)
    {
        if (!TryAmount(valuesJson, out var raw)) return (null, null);
        if (raw.ValueKind == JsonValueKind.Number) return (raw.GetInt64(), null);
        if (raw.ValueKind != JsonValueKind.Object) return (null, null);

        return AtomJson.TryReadValueSpec(raw, out var spec).IsOk
            ? (spec.Min, spec.Max)
            : (null, null);
    }

    static bool TryAmount(string? json, out JsonElement amount)
    {
        amount = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("amount", out var a)) return false;
            amount = a.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    static string? ChannelOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The concrete element a template's <c>{element}</c> names — from the atom's own variant if it
    /// materialised there, otherwise from the <b>resolved channel the instance froze</b>.
    ///
    /// <para><b>Both arms are real, and they are the two entry points, not a fallback.</b> An affix
    /// whose ref is a SLOT (<c>$element</c>) resolves through <c>Resolver</c>'s step 1/4 into a
    /// concrete atom id, so the element genuinely is in <c>AtomRow.Variant</c>. A family whose channel
    /// is <c>{variant}</c>-templated does the opposite: <c>FamilyExpansion</c> emits ONE row per tier
    /// carrying an E30 pool reference and leaves the variant deliberately empty (W7.9, "element does
    /// not materialise" in the atom id), and the concrete element arrives later, as the pool draw
    /// <c>Resolver.RollValues</c> stamps into <c>values_json.channel</c>. Reading only the variant
    /// meant every pooled-channel affix refused to render, which is the entry-point split P2.5 named
    /// and this closes.</para>
    ///
    /// <para>⛔ <b>Gated on the atom's own channel actually being a pool reference</b>, not on the
    /// resolved string looking element-shaped. A trailing segment is only read as an element when
    /// <c>params.channel</c> is an E30 pool object — otherwise an unrelated channel whose last segment
    /// happens to collide with an element name would silently acquire one.</para>
    ///
    /// <para><c>omni</c> resolves to <c>null</c>, deliberately: <c>ElementRoster.TryParse</c> refuses
    /// it (it is the untyped case, and <c>pools.v1.json</c>'s own note says omni is never a pool
    /// member), so there is no concrete element to name and the renderer refuses rather than writing
    /// "omni" into a sentence.</para>
    /// </summary>
    static string? ElementOf(AtomRow atom, string valuesJson)
    {
        // `AtomRow.Variant` is "" and never NULL by design; passing "" would render an empty word.
        if (atom.Variant.Length > 0) return atom.Variant;
        if (!IsPooledChannel(atom.ParamsJson)) return null;

        var resolved = ChannelOf(valuesJson);
        if (resolved is null) return null;

        var dot = resolved.LastIndexOf('.');
        if (dot < 0 || dot == resolved.Length - 1) return null;

        return ElementRoster.TryParse(resolved[(dot + 1)..], out var id) ? id.ToElementId() : null;
    }

    /// <summary>Whether the atom AUTHORS an E30 pool reference — read through the shipped
    /// <see cref="ChannelRefJson"/>, never by shape-matching the JSON here, so "is this a pool" has one
    /// answer across the resolver and the card.</summary>
    static bool IsPooledChannel(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(paramsJson!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("channel", out var c)) return false;
            return ChannelRefJson.TryRead(c, out var channelRef).IsOk && channelRef.IsPool;
        }
        catch (JsonException) { return false; }
    }
}
