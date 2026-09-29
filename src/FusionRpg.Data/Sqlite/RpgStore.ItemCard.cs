using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Items.Gems;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Core.Items.Uniques;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Data;

/// <summary>
/// One socketed insert, joined to the display key its cell renders. <see cref="Def"/> is module 16's
/// own evaluator input; <see cref="NameKey"/> is what §2.4 lets the cell show instead of the insert's
/// container id.
/// </summary>
/// <param name="Name">
/// item-content `item-naming` (T4): the gem corpus's own AUTHORED English name (<c>"Ember Shard"</c>),
/// read and no longer dropped — the same class of defect T2 fixed in <c>ItemBaseTypeCorpus</c>.
/// <c>gk-content/content/display/en.json</c> carries no <c>gem.*</c> row, so the key alone left every insert
/// showing a key fragment where a name belongs. Additive with a default, so no existing construction
/// site changes.
/// </param>
/// <param name="Seed">
/// species-gear-chain T22: the same entry's <see cref="GemSeed"/> — family, element and powerBand
/// <see cref="GemContainerBuild"/> mints the real insert instance from. Rides this record (rather
/// than a second load of <c>gems/*.json</c>) so the mint resolves the same entry the card names:
/// two loads are two chances to disagree about what a gem is. <c>null</c> on hand-built lookups
/// (tests); <c>socket-insert</c> refuses mint-unavailable rather than guessing from the
/// <see cref="Def"/> alone, because the tier in the def is DERIVED from the band and the
/// inversion is not unique.
/// </param>
public readonly record struct CardInsertLookup(InsertDef Def, string NameKey, string Name = "", GemSeed? Seed = null);

/// <summary>
/// The facts an item card needs that <b>no shipped table carries</b>, supplied by the caller.
///
/// <para>This record exists because the honest answer to "can the DAL assemble a whole card from
/// SQL?" is <i>almost</i>. Three things genuinely are not in the database and saying so is cheaper
/// than inventing them:</para>
///
/// <list type="number">
/// <item><b>The base type.</b> <c>item_base_type</c> now EXISTS
/// (<see cref="EnsureBaseTypeSchemaUnlocked"/>, populated at boot by <c>Program.cs</c>'s
/// <c>ImportBaseTypes</c> and read back by <c>BuildLiveLootContentView</c>'s <c>BaseTypesFor</c> —
/// corrected 2026-09-23, this line used to say there was no table), but it carries only
/// <c>(id, frame, role)</c>. <c>nameKey</c>, the class noun and the flavour key are not in it, so they
/// still have to arrive from the corpus.</item>
/// <item><b>The gem catalog.</b> <c>item_socket.insert_container_id</c> names a <c>gem.*</c> container,
/// and <see cref="ContainerRow"/> carries no element, tier or family — so an <see cref="InsertDef"/>
/// cannot be reconstructed from <c>effect_container</c> alone.</item>
/// <item><b>Two tuning objects.</b> <see cref="SocketTuning"/>/<see cref="ItemSurfaceTuning"/> come
/// from <c>gk-core/data/tuning/*.json</c>, which the Server owns loading.</item>
/// </list>
///
/// <para>⛔ <b>Nothing here is optional-with-a-guess.</b> Where a piece is absent the card renders
/// that block EMPTY and the reason is documented on the field, never filled in with a plausible
/// default — an invented gem element would put a resonance on a card the evaluator would not fire.</para>
/// </summary>
/// <param name="LookupBaseType">
/// <c>container_id</c> → module 6's base-type display facts. <b>Required</b>: a card with no base type
/// has no header. Return <c>null</c> and <see cref="RpgStore.GetItemCardInput"/> throws by name rather
/// than rendering a nameless item.
/// </param>
/// <param name="LookupInsert">
/// A <c>gem.*</c> container id → its evaluator def and display key. <c>null</c> is legal only while
/// every socket on the item is empty; a FILLED socket with no lookup throws, because rendering it as
/// empty would be a lie about the item's own state.
/// </param>
/// <param name="Palette">
/// The ten rarity hexes in ordinal order. Defaults to <see cref="RarityPalette.Dark"/> — the shipped
/// dark-theme ladder, not a preference: a caller rendering the light theme passes
/// <see cref="RarityPalette.Light"/>.
/// </param>
/// <param name="ItemName">
/// An EXPLICIT name that overrides composition — module 17's authored unique name, or a caller that
/// has already decided what this item is called. Empty is the normal case and means "compose one"
/// (see <paramref name="LookupNameWords"/>).
///
/// <para>⛔ Until 2026-09-06 this was the ONLY way a name could reach the card, and nothing set it —
/// which is why every rendered card fell back to <c>baseType.NameKey</c> and the header read
/// <c>base.card-proof-blade</c>. It could not have worked: <see cref="ItemCardCorpus"/> is built once
/// at host start, so a per-instance composed name has no way in through a flat string. The two
/// delegates below are the fix, and they follow the same "needs per-request resolution" shape
/// <paramref name="LookupBaseType"/> and <paramref name="LookupInsert"/> already use.</para>
/// </param>
/// <param name="LookupNameWords">
/// <c>family_id</c> → its authored <c>nameWords</c> rows and the slot they fill
/// (<c>AffixNameWordCorpus.Load</c>). With <paramref name="RareNameDraw"/>, this turns naming on:
/// <see cref="RpgStore.GetItemCardInput"/> then calls <c>ItemNameAssembly.Compose</c> — module 8's own
/// function, once — over the instance's real rolled affixes.
///
/// <para><c>null</c> (either delegate) leaves naming off and the card falls back to the base type's
/// authored name, which is honest rather than half-composed.</para>
/// </param>
/// <param name="RareNameDraw">
/// <c>roll_seed</c> → the two-word name a RARE item (3+ affixes) gets instead of the affix grammar
/// (<c>RareNameCorpus.Load</c>). Required alongside <paramref name="LookupNameWords"/> because the
/// threshold is 3 and real rarity rungs roll 3+ routinely — a naming path that throws on the common
/// case is not wired.
/// </param>
public sealed record ItemCardCorpus(
    Func<string, CardBaseType?> LookupBaseType,
    Func<string, CardInsertLookup?>? LookupInsert = null,
    SocketTuning? SocketTuning = null,
    ItemSurfaceTuning? SurfaceTuning = null,
    EnhancementTuning? Enhancement = null,
    IReadOnlyList<string>? Palette = null,
    string ItemName = "",
    DerivedStatRegistry? Registry = null,
    Func<string, string?>? LookupString = null,
    Func<string, AffixNameSlot?>? LookupNameWords = null,
    Func<long, (string Head, string Tail)>? RareNameDraw = null);

/// <summary>
/// Who is asking. Absent, the card still renders — it just carries no requirement block, no refusal
/// and no set progress, because all three are facts about a <i>wearer</i> and not about the item.
/// </summary>
/// <param name="Actor">Module 4's own gate input. Its <c>SpecimenId</c> is the
/// <c>rpg_item_assignment.specimen_id</c> this method reads assignments and set membership under.</param>
/// <param name="Gate">Module 4's <c>EquipGate</c>. Defaults to a fresh one over the default
/// <c>SlotUnlock</c>, which is what <c>EquipGate</c>'s own parameterless path already does.</param>
/// <param name="Requirements">
/// I11's attribute clauses. Supplied rather than read: <b>no shipped table carries a per-item
/// attribute requirement</b> — <c>EquipGate</c> refuses on role/frame/level/faction and on nothing
/// else — so reading one out of SQL would mean inventing the column first.
/// </param>
/// <param name="FactionReq">Content-restricted faction clause (I11 §2.3), inert until modules 13/17
/// ship content that sets it — passed through rather than assumed absent.</param>
public sealed record ItemCardWearer(
    SpecimenActor Actor,
    EquipGate? Gate = null,
    IReadOnlyList<CardRequirement>? Requirements = null,
    string? FactionReq = null);

public sealed partial class RpgStore
{
    /// <summary>
    /// ⭐ <b>The DAL read path P2.5 deferred</b>: an <c>instance_id</c> in, a real
    /// <see cref="ItemCardInput"/> out, assembled from the rows the shipped stores already hold — so a
    /// caller goes from "an item id" to a rendered card without hand-building the input.
    ///
    /// <para>⛔ <b>It owns no logic.</b> Every number and every state on the result is produced by the
    /// module that owns it, called once:</para>
    ///
    /// <list type="table">
    /// <item><term>module 1 <c>durable-ownership</c></term><description><c>rpg_item</c> —
    /// <see cref="GetItem"/> for <c>locked</c>/<c>stale</c>. Its absence is what makes this method
    /// return <c>null</c>: an <c>effect_instance</c> nobody owns is not an item card.</description></item>
    /// <item><term>the instance itself</term><description><see cref="GetInstance"/> +
    /// <see cref="GetContainer"/> — the frozen atoms and the fixed-core <c>seq</c> set the card splits
    /// base / implicit / affix by.</description></item>
    /// <item><term>module 10</term><description><see cref="GetAtom"/> and
    /// <see cref="GetDisplayTemplate"/>, handed over as the two memoised lookups
    /// <see cref="ItemCardInput"/> already takes — the <c>item_display_template</c> rows
    /// <c>Program.cs</c> seeds at boot finally have a production reader.</description></item>
    /// <item><term>module 15 <c>enhance-reroll</c></term><description>
    /// <see cref="GetInstanceMutationHead"/>'s <c>enhance_level</c>, and
    /// <c>EnhancePolicy.GainMilli</c> over the rung's own stored <c>enhance_cap</c>
    /// (<see cref="GetRarityBudget"/>) — never a second curve.</description></item>
    /// <item><term>module 16 <c>sockets</c></term><description><see cref="GetSockets"/> for the cells
    /// and <see cref="GetComboRecipes"/> + <c>CombinationDistance.Evaluate</c> for the combinations —
    /// one evaluation, the same one the bench previews with.</description></item>
    /// <item><term>module 12 <c>threshold-grants</c></term><description><see cref="ListSets"/> +
    /// <see cref="ListAssignments"/> → <c>SetEvaluator.Progress</c> and <c>SetDisclosure.ForWearer</c>,
    /// so the "3 / 4" and the redundancy note come off the same wearer read.</description></item>
    /// <item><term>module 4 <c>equip-assign</c></term><description><c>EquipGate.Explain</c> against the
    /// role the item is actually assigned to.</description></item>
    /// <item><term>modules 17 / 19</term><description><see cref="GetItemUnique"/> and
    /// <see cref="ListItemGrantedActions"/>.</description></item>
    /// </list>
    ///
    /// <para><b>Named gaps, not silent ones.</b> <c>SalvageYield</c> and <c>Power</c> are left null:
    /// module 14's yield needs the materials tuning and module 9's <c>CardPower</c> needs the power
    /// tuning, and both are computed reads rather than stored rows — a caller that has the tuning adds
    /// them with a <c>with</c> expression. <c>NoReassign</c> is always <c>false</c> because the
    /// <c>no_reassign</c> content flag is <b>reserved and deliberately not added</b>
    /// (<c>ssot-inventory.md:208</c>), so there is no column to read and inventing one would be worse
    /// than the false.</para>
    ///
    /// <para>⚠ <b>The display KEYS this derives are structural, not content.</b> <c>rarity.{rung}</c>,
    /// <c>set.{setId}</c>, <c>combo.{shape}</c>, <c>role.{role}</c> and friends are id→key derivations;
    /// whether <c>gk-content/content/display/en.json</c> actually has a row for one is
    /// <c>DisplayRules.MissingDisplayKey</c>'s question, and today it has none of them — a named
    /// wiring gap in the string corpus, reported by the existing rule rather than papered over here.</para>
    /// </summary>
    /// <returns><c>null</c> when the instance is not an owned item, its <c>effect_instance</c> row is
    /// gone, or its container is not in the catalog — three genuinely absent rows, distinguished from
    /// a mis-assembled card by never throwing for them.</returns>
    public ItemCardInput? GetItemCardInput(
        string instanceId, ItemCardCorpus corpus, ItemCardWearer? wearer = null,
        Func<string, int?>? socketMaxFor = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("instance id", nameof(instanceId));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (corpus.LookupBaseType is null) throw new ArgumentNullException(nameof(corpus.LookupBaseType));

        var item = GetItem(instanceId);
        if (item is null) return null;

        var instance = GetInstance(instanceId);
        if (instance is null) return null;

        var container = GetContainer(instance.ContainerId);
        if (container is null) return null;

        var baseType = corpus.LookupBaseType(container.ContainerId)
            ?? throw new InvalidOperationException(
                $"no base type for container '{container.ContainerId}' — module 6's corpus is the only " +
                "source for a base type's nameKey/class/flavour; item_base_type carries only " +
                "(id, frame, role)");

        // Memoised so a card with twelve atoms across four families does four template queries, not
        // twelve. Both delegates outlive this call by design: `ItemCardInput` holds them and the
        // renderer calls them, which is the shape the record already documents.
        var atoms = new Dictionary<string, AtomRow?>(StringComparer.Ordinal);
        var templates = new Dictionary<string, DisplayTemplateRow?>(StringComparer.Ordinal);

        AtomRow? LookupAtom(string id)
        {
            if (atoms.TryGetValue(id, out var cached)) return cached;
            return atoms[id] = GetAtom(id);
        }

        DisplayTemplateRow? LookupTemplate(string family)
        {
            if (templates.TryGetValue(family, out var cached)) return cached;
            return templates[family] = GetDisplayTemplate(family);
        }

        var generation = GetItemGeneration(instanceId);
        var head = GetInstanceMutationHead(instanceId);
        var enhanceLevel = head?.EnhanceLevel ?? 0;

        // ONE set-corpus read for the whole card: the socket block needs it for D21's set-piece
        // exclusivity and the set block needs it for the ladder, and two reads of the same table
        // inside one card is two chances for them to disagree as well as twice the query.
        var sets = ListSets();

        // strain-splice-host SSH1.3 (host-gate §2): `socketMaxFor` is the base type's own socketMax,
        // and it MUST be threaded here or `SocketHost.Capacity` reads `int.MaxValue` for every item.
        // That is not a cosmetic difference: `CombinationDistance.Reachable` tests Capacity five
        // times (MinSockets, a Strain/Splice's summed ingredient quantity, a Pure/Diversity
        // threshold, Ring/Eclipse's two-socket floor, and `CanEverHold`'s own MinSockets test), so an
        // unbounded capacity makes every one of them pass and renders a combination the chassis can
        // NEVER hold as reachable instead of `undiscovered` — the exact "tooltip lie" that method's
        // own doc forbids. Corrected 2026-09-23: this line used to pass `_ => null` and claim
        // "nothing consumes it yet", which stopped being true when SSH1.3 shipped.
        var (sockets, combinations) = ReadSocketBlock(instanceId, corpus, socketMaxFor ?? (_ => null));
        var (set, wornRole) = ReadSetBlock(instanceId, wearer, sets);
        var unique = GetItemUnique(container.ContainerId);

        // species-gear-chain T26: an instance's OWN rung wins once a promotion has moved it. The
        // container's rarity is where the item started and is shared by every item minted from it, so
        // reading only the container would leave a promoted item showing its original rung forever.
        var rarities = ListRarities();
        var instanceRungId = generation is { } g
            ? rarities.FirstOrDefault(r => r.Ordinal == g.RarityOrdinal)?.RarityId
            : null;

        // The mark, resolved to a rung KEY beside the current rung's own key (T26's card rule): the
        // ordinal is the persisted fact, the key is what a card can render against the shared ladder.
        var promotedFromKey = generation?.PromotedFromOrdinal is { } promoted
            ? rarities.FirstOrDefault(r => r.Ordinal == promoted)?.RarityId is { Length: > 0 } id
                ? "rarity." + id
                : null
            : null;

        return new ItemCardInput(
            instance, container, LookupAtom, LookupTemplate, baseType,
            ReadRarity(instanceRungId ?? container.Rarity, corpus.Palette),
            ComposeItemName(corpus, instance, container, baseType, unique, LookupAtom))
        {
            ItemLevel = generation?.ItemLevel ?? 0,
            EnhanceLevel = enhanceLevel,
            PromotedFromRarityKey = promotedFromKey,
            EnhanceGainMilli = ReadEnhanceGain(container.Rarity, enhanceLevel, corpus.Enhancement),
            LevelReq = container.LevelReq,
            SpecimenLevel = wearer?.Actor.Level ?? 0,
            Refusal = ReadRefusal(wearer, wornRole, generation?.Frame ?? baseType.Frame, container.LevelReq),
            Requirements = wearer?.Requirements ?? Array.Empty<CardRequirement>(),
            Sockets = sockets,
            Combinations = combinations,
            Set = set,
            GrantedActions = ReadGrantedActions(container.ContainerId),
            Unique = unique,
            Locked = item.Locked,
            Stale = item.Stale,
            // `no_reassign` is reserved and deliberately unadded (ssot-inventory.md:208). No column,
            // no read, no guess.
            NoReassign = false,
            Registry = corpus.Registry,
            // N2's string catalog, for block 10's authored sentence. Null when the host has no
            // catalog on disk: the flavour line then carries its key alone and nothing is invented.
            LookupString = corpus.LookupString,
        };
    }

    // ---- the name (module 8) ----------------------------------------------------------------------------

    /// <summary>
    /// ⭐ <b>The production caller module 8 named as its own last blocker</b> (item-content T1,
    /// 2026-09-06). <c>ItemNameComposer.Compose</c> shipped correct and tested with no caller outside
    /// <c>tests/</c>, so every card in the game rendered its base type's display KEY where its name
    /// belongs.
    ///
    /// <para>⛔ <b>It derives nothing.</b> The grammar, the (tier DESC, seq ASC) selection, the
    /// rare-name threshold and the word resolution all belong to modules 8's own functions, called
    /// once through <see cref="ItemNameAssembly"/>. What happens here is the four-way precedence, and
    /// nothing else:</para>
    ///
    /// <list type="number">
    /// <item>An EXPLICIT <see cref="ItemCardCorpus.ItemName"/> wins — a caller that already knows.</item>
    /// <item>A UNIQUE takes its base type's authored name: module 17 hand-authors a unique's name and
    /// <c>ItemNameComposer</c>'s own doc says it is <i>"never called for one"</i>. (⏸ Named gap:
    /// <c>UniqueRow</c> carries no name column of its own, so the base type's name is what there is —
    /// module 17's to close, not this method's to invent.)</item>
    /// <item>Both naming delegates present ⇒ the composed name.</item>
    /// <item>Otherwise the base type's authored name, or its key when even that is empty — the same
    /// fallback this method has always had, just one rung better.</item>
    /// </list>
    /// </summary>
    static string ComposeItemName(
        ItemCardCorpus corpus, InstanceRow instance, ContainerRow container, CardBaseType baseType,
        UniqueRow? unique, Func<string, AtomRow?> lookupAtom)
    {
        if (corpus.ItemName.Length > 0) return corpus.ItemName;

        var baseName = baseType.Name.Length > 0 ? baseType.Name : baseType.NameKey;
        if (unique is not null) return baseName;
        if (corpus.LookupNameWords is not { } lookupNameWords) return baseName;
        if (corpus.RareNameDraw is not { } rareNameDraw) return baseName;

        return ItemNameAssembly.Compose(
            baseName, baseType.Frame, instance.RollSeed,
            ItemNameAssembly.RolledAffixes(instance, container, lookupAtom, lookupNameWords),
            lookupNameWords, rareNameDraw);
    }

    // ---- rarity ---------------------------------------------------------------------------------------

    /// <summary>
    /// I1's three redundant channels, all three read off the STORED ladder: the pip count is the rung's
    /// 1-based position in <c>rarity</c>'s own append-only ordinal order (never <c>ordinal / 10</c>,
    /// which would bake the spacing in), and the hex is that same position into the palette.
    /// </summary>
    /// <remarks><b>Public since 2026-09-06</b> (item-content module <c>atom-preview</c>): the preview
    /// route renders an UNSAVED container, so it never reaches <see cref="GetItemCardInput"/> — but it
    /// still needs the rung's three redundant channels off the same stored ladder. Widening the
    /// visibility is the anti-duplication move: the alternative was a second pip count and a second
    /// palette index in the Server, which is exactly how two surfaces come to disagree about a rung.</remarks>
    public CardRarity ReadRarity(string? rarityId, IReadOnlyList<string>? palette)
    {
        var hexes = palette ?? RarityPalette.Dark;
        if (string.IsNullOrEmpty(rarityId)) return new CardRarity("", 0, "");

        var ladder = ListRarities().OrderBy(r => r.Ordinal).ToList();
        var index = ladder.FindIndex(r => string.Equals(r.RarityId, rarityId, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException(
                $"rarity '{rarityId}' is not on the stored ladder — the container references a rung the " +
                "`rarity` table does not have, which UpsertContainer should already have refused");

        if (index >= hexes.Count)
            throw new InvalidOperationException(
                $"rarity '{rarityId}' is rung {index + 1} but the palette has {hexes.Count} entries — " +
                "the palette must cover the whole ladder or the top rungs render colourless");

        return new CardRarity("rarity." + rarityId, index + 1, hexes[index]);
    }

    /// <summary>Module 15's own curve at the stored level, over the rung's own stored cap. Returns 0
    /// when either the tuning or the <c>enhance_cap</c> row is absent — a missing gain is honest; a
    /// gain computed against a guessed cap is not.</summary>
    long ReadEnhanceGain(string? rarityId, int enhanceLevel, EnhancementTuning? tuning)
    {
        if (enhanceLevel <= 0 || tuning is null || string.IsNullOrEmpty(rarityId)) return 0;
        var cap = GetRarityBudget(rarityId, "enhance_cap");
        return cap is null ? 0 : EnhancePolicy.GainMilli(enhanceLevel, cap.Value, tuning);
    }

    // ---- sockets (module 16) --------------------------------------------------------------------------

    /// <summary>
    /// strain-splice-host SSH1.2 (host-gate F5) — the ONE place a real <see cref="SocketHost"/> is
    /// built for a real instance. Before this, <c>ItemSurfaceEndpoints.cs</c>'s <c>/combinations</c>
    /// route hard-coded <c>ArmamentPrimary</c>, an empty frame and never-a-set-piece, while this file
    /// built the real thing separately — two builders that could (and did) disagree about the same
    /// item. Self-contained: <see cref="GetItemGeneration"/> gives the mint-time-fixed role and frame
    /// (the same base type a corpus's <c>LookupBaseType</c> would resolve, but off SQL alone, so this
    /// needs no corpus delegate), <see cref="GetSockets"/> gives the real socket count, and
    /// <see cref="ListSets"/> gives D21's set-piece flag. Returns <c>null</c> when the instance or its
    /// generation row is missing, or its stored role string does not parse — the caller's own choice
    /// whether that means "render cells only" (the item card) or "no combinations" (the surface route).
    /// </summary>
    /// <param name="socketMaxFor">
    /// SSH1.3 wires this to <c>BaseTypeSocketMaxCorpus</c>'s lookup to fill <c>SocketHost.Capacity</c>;
    /// unused until that field exists, so a caller with no real lookup on hand may pass <c>_ =&gt;
    /// null</c> today without changing any observable behaviour.
    /// </param>
    public SocketHost? SocketHostFor(string instanceId, Func<string, int?> socketMaxFor)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("instance id", nameof(instanceId));
        if (socketMaxFor is null) throw new ArgumentNullException(nameof(socketMaxFor));

        var instance = GetInstance(instanceId);
        if (instance is null) return null;

        var generation = GetItemGeneration(instanceId);
        if (generation is null) return null;

        if (!ItemRoles.TryParse(generation.Role, out var role)) return null;

        var slots = GetSockets(instanceId);
        var sets = ListSets();

        // strain-splice-host SSH1.3 (host-gate §2): Capacity is the base type's own socketMax, off
        // the SAME lookup the workbench already uses. `null` (an id the lookup does not carry) falls
        // back to SocketHost's own default (unbounded) rather than refusing the whole host -- a
        // missing socket-max row is a display-time gap, not a reason to hide a real item's cells.
        var capacity = socketMaxFor(generation.BaseTypeId) ?? int.MaxValue;

        return new SocketHost(
            instance.ContainerId, role, generation.Frame, slots.Count,
            // D21's own question -- is this base type a member of any shipped set -- answered off
            // `item_set_member` rather than re-derived from the corpus.
            IsSetPiece: sets.Any(s => s.Members.Any(
                m => string.Equals(m.ContainerId, instance.ContainerId, StringComparison.Ordinal))),
            Capacity: capacity);
    }

    (IReadOnlyList<CardSocketCell> Cells, IReadOnlyList<CardCombination> Combos) ReadSocketBlock(
        string instanceId, ItemCardCorpus corpus, Func<string, int?> socketMaxFor)
    {
        var slots = GetSockets(instanceId);
        if (slots.Count == 0)
            return (Array.Empty<CardSocketCell>(), Array.Empty<CardCombination>());

        var cells = new List<CardSocketCell>(slots.Count);
        var fill = new List<SocketFill>(slots.Count);

        foreach (var slot in slots)
        {
            CardInsertLookup? insert = null;
            if (!slot.IsEmpty)
            {
                if (corpus.LookupInsert is null)
                    throw new InvalidOperationException(
                        $"socket {slot.Index} on '{instanceId}' holds '{slot.InsertContainerId}' but no insert " +
                        "lookup was supplied — no shipped table carries a gem's element/tier/family, and " +
                        "rendering a filled socket as empty would be a lie about the item");

                insert = corpus.LookupInsert(slot.InsertContainerId!)
                    ?? throw new InvalidOperationException(
                        $"socket {slot.Index} on '{instanceId}' holds '{slot.InsertContainerId}', which the " +
                        "gem catalog does not have");
            }

            cells.Add(new CardSocketCell(
                slot.Index,
                slot.Affinity.Length == 0 ? "" : "element." + slot.Affinity,
                slot.Crafted,
                insert?.NameKey,
                // §9.4: an omni insert that counts only toward Diversity says so on its own line.
                OmniCountsDiversityOnly: insert is { } i
                    && string.Equals(i.Def.Element, ElementRoster.OmniId, StringComparison.Ordinal)));

            if (insert is { } filled) fill.Add(new SocketFill(slot.Index, slot.Affinity, filled.Def));
        }

        // Both tunings or neither: `CombinationDistance.Evaluate` refuses a null, and half an
        // evaluation is not a partial answer, it is a wrong one.
        if (corpus.SocketTuning is null || corpus.SurfaceTuning is null)
            return (cells, Array.Empty<CardCombination>());

        var catalog = GetComboRecipes();
        if (catalog.Count == 0) return (cells, Array.Empty<CardCombination>());

        // A container with no parseable item role is not an equippable item, and every combination
        // recipe is gated on the host's role — so there is nothing to evaluate rather than something
        // to guess. The CELLS still render: `item_socket` is the SSOT and its rows are true either way.
        if (SocketHostFor(instanceId, socketMaxFor) is not { } host)
            return (cells, Array.Empty<CardCombination>());

        var rows = CombinationDistance.Evaluate(
            host, fill, catalog, corpus.SocketTuning, corpus.SurfaceTuning, out _);

        // §4.3: `undiscovered` is never rendered at all, so it never reaches the card.
        var combos = rows
            .Where(r => r.State != CombinationDisplayState.Undiscovered)
            .Select(r => new CardCombination(
                "combo." + r.Shape.ToString().ToLowerInvariant(),
                r.Shape, r.State, r.Distance, r.GrantedTier, r.AllAttuned,
                r.MissingElements.Select(e => "element." + e).ToList()))
            .ToList();

        return (cells, combos);
    }

    // ---- set (module 12) ------------------------------------------------------------------------------

    /// <summary>
    /// The wearer's whole equipped list, once, through module 12's own evaluator — never a per-item
    /// count. Returns the set THIS item advances (or is redundant in), plus the role it occupies, which
    /// the equip gate then re-uses.
    /// </summary>
    (CardSet? Set, ItemRole? WornRole) ReadSetBlock(
        string instanceId, ItemCardWearer? wearer, IReadOnlyList<SetDef> sets)
    {
        if (wearer is null) return (null, null);

        var assignments = ListAssignments(wearer.Actor.SpecimenId);
        if (assignments.Count == 0) return (null, null);

        ItemRole? wornRole = null;
        var worn = new List<EquippedPiece>(assignments.Count);

        foreach (var a in assignments)
        {
            // ⛔ Fixed 2026-09-06 (defect R2). Both arms tested `"item"`, which is
            // `rpg_item_loadout_entry`'s spelling (`LoadoutReport.InstanceRefKind`) — a DIFFERENT
            // table. `rpg_item_assignment` only ever carries `rolled` or `stock`, so a real equipped
            // item fell down the else arm, had its instance id read as a container id, and could
            // never set `wornRole` — which silently emptied the set block AND `ReadRefusal`. Latent
            // while nothing wrote a rolled row; live from the day `POST /api/items/equip` shipped.
            //
            // `rolled` pins one copy, so the container comes from the instance; `stock` already IS a
            // container id. An unrecognised kind is skipped rather than guessed — the same
            // "unreadable entry is a visible hole, not a silent pass" rule
            // `GetLoadoutEntriesValidated` applies to the preset table.
            string? containerId;
            if (string.Equals(a.RefKind, EquipRefKinds.Rolled, StringComparison.Ordinal))
                containerId = GetInstance(a.RefId)?.ContainerId;
            else if (string.Equals(a.RefKind, EquipRefKinds.Stock, StringComparison.Ordinal))
                containerId = a.RefId;
            else
                continue;
            if (containerId is null) continue;

            worn.Add(new EquippedPiece(a.Role, containerId));
            if (string.Equals(a.RefKind, EquipRefKinds.Rolled, StringComparison.Ordinal)
                && string.Equals(a.RefId, instanceId, StringComparison.Ordinal))
                wornRole = a.Role;
        }

        if (wornRole is null) return (null, null);
        if (sets.Count == 0) return (null, wornRole);

        var progress = SetEvaluator.Progress(worn, sets);
        if (progress.Count == 0) return (null, wornRole);

        // Which set THIS piece touches. Order is the caller's (SetDisclosure's own rule), and `worn`
        // was built in assignment order, so the disclosure below and the count above agree by
        // construction rather than by coincidence.
        var disclosure = SetDisclosure.ForWearer(worn, sets);
        PieceSetDisclosure? mineOrNull = null;
        foreach (var d in disclosure)
            if (d.Piece.Role == wornRole && (d.AdvancesSetIds.Count > 0 || d.RedundantSetIds.Count > 0))
            {
                mineOrNull = d;
                break;
            }

        if (mineOrNull is not { } mine) return (null, wornRole);

        var setId = mine.AdvancesSetIds.FirstOrDefault() ?? mine.RedundantSetIds.FirstOrDefault();
        if (setId is null) return (null, wornRole);

        var def = sets.FirstOrDefault(s => string.Equals(s.SetId, setId, StringComparison.Ordinal));
        if (def is null) return (null, wornRole);

        var p = progress.First(x => string.Equals(x.SetId, setId, StringComparison.Ordinal));

        // P8.4 — the multi-set per-piece disclosure. `SetDisclosure.ForWearer` already returned EVERY
        // set this piece touches; the primary row below is only its first. Building the peers here —
        // off the same `disclosure`, `progress` and `sets` reads, never a second pass — is what keeps
        // the card's "3 / 4" and the other two it was hiding from disagreeing.
        var others = new List<CardSetPeer>();
        foreach (var otherId in mine.AdvancesSetIds.Concat(mine.RedundantSetIds))
        {
            if (string.Equals(otherId, setId, StringComparison.Ordinal)) continue;
            if (others.Any(o => string.Equals(o.SetId, otherId, StringComparison.Ordinal))) continue;
            var otherDef = sets.FirstOrDefault(s => string.Equals(s.SetId, otherId, StringComparison.Ordinal));
            if (otherDef is null) continue;
            var otherProgress = progress.FirstOrDefault(x => string.Equals(x.SetId, otherId, StringComparison.Ordinal));
            others.Add(new CardSetPeer(
                otherDef.SetId, otherDef.DisplayName, otherProgress.Count, otherProgress.Total,
                Redundant: mine.RedundantSetIds.Contains(otherId)));
        }

        return (new CardSet(
            "set." + def.SetId, p.Count, p.Total,
            // The WHOLE ladder, always — an inactive threshold is the goal (§4.3).
            def.Tiers.OrderBy(t => t.PiecesRequired)
                .Select(t => new CardSetTier(t.PiecesRequired, t.PiecesRequired <= p.Count, t.IsCapability))
                .ToList(),
            Redundant: mine.RedundantSetIds.Count > 0,
            // item-lore T7: the set's own authored lore key, straight off `item_set.flavour_key`.
            // Null for the 24 sets that authored none, which renders no Flavour block at all.
            FlavourKey: def.FlavourKey,
            // item-content T2: `item_set.display_name`. SetCorpus.Parse has read it into
            // SetDef.DisplayName since it shipped and the column round-trips it; the card derived
            // `set.{setId}` and dropped it, and the string catalog has no `set.*` row to resolve that
            // key with — so the set block showed an id-shaped string where a name belongs.
            Name: def.DisplayName,
            Others: others), wornRole);
    }

    // ---- requirements (module 4) ----------------------------------------------------------------------

    /// <summary>
    /// <c>EquipGate.Explain</c> against the role the item is <b>actually assigned to</b>. No assignment
    /// means no refusal to explain: a card for an item sitting in the bag is not refusing anything, and
    /// picking a plausible role to test it against would invent a rejection.
    /// </summary>
    static EquipRefusal? ReadRefusal(
        ItemCardWearer? wearer, ItemRole? wornRole, string itemFrame, int? levelReq)
    {
        if (wearer is null || wornRole is null) return null;
        var gate = wearer.Gate ?? new EquipGate();
        return gate.Explain(wornRole.Value, wearer.Actor, itemFrame, levelReq, wearer.FactionReq);
    }

    // ---- granted actions (module 19 / G4) --------------------------------------------------------------

    /// <summary>
    /// <c>AlreadyKnown</c> is always <c>false</c>: whether a specimen already knows an action is the
    /// action layer's own state, not the item store's, and a card that guessed it would tell a player
    /// they already have something they do not.
    ///
    /// <para>⛔ <b>Fixed 2026-09-06 (item-content `granted-action-text`, T15) — a real defect, named
    /// rather than quietly patched.</b> Both keys used to be built as <c>"action." + g.ActionId</c>,
    /// and <c>rpg_action.action_id</c> ALREADY carries the <c>action.</c> stem
    /// (<c>action.family.cactus.001</c>) — so block 9 asked the string catalog for
    /// <c>action.action.family.cactus.001.desc</c>, a key nothing could ever author. The id IS the
    /// key stem, so the name key is the id and the description key is now READ from
    /// <c>rpg_action.description_key</c> rather than guessed from it.</para>
    ///
    /// <para>The <c>.desc</c> suffix survives only as the fallback for an action whose row is gone or
    /// whose <c>description_key</c> is unauthored — a key that resolves to nothing, which is
    /// <c>DisplayRules.MissingDisplayKey</c>'s visible absence rather than a blank line.</para>
    /// </summary>
    IReadOnlyList<CardGrantedAction> ReadGrantedActions(string containerId) =>
        ListItemGrantedActions(containerId)
            .Where(g => g.Enabled)
            .OrderBy(g => g.Seq)
            .Select(g => new CardGrantedAction(
                g.ActionId,
                GetAction(g.ActionId)?.DescriptionKey is { Length: > 0 } key ? key : g.ActionId + ".desc",
                // A DefaultAttack grant replaces the species' basic attack, which only exists in a
                // battle; a plain Granted entry is an extra selectable and is not battle-only.
                BattleOnly: g.Role == ItemGrantRole.DefaultAttack,
                AlreadyKnown: false))
            .ToList();

    /// <summary>
    /// The compact armoury line's half of card block 9 — `ssot-presentation.md` §9.14's own explicit
    /// ask: <i>"the battle-only tag needs to be visible in the compact list line too, not only the
    /// card — a player scanning an armoury should not have to open each item to learn that half of
    /// them are inert on the lawn."</i>
    ///
    /// <para>Same rule as <see cref="ReadGrantedActions"/>'s own <c>BattleOnly</c>, read from the same
    /// rows — a second derivation is how a list and a card come to disagree.</para>
    /// </summary>
    public bool GrantsBattleOnlyAction(string containerId) =>
        ListItemGrantedActions(containerId)
            .Any(g => g.Enabled && g.Role == ItemGrantRole.DefaultAttack);
}
