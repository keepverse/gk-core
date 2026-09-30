using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Generation;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// ⭐ <b><c>RpgStore.GetItemCardInput</c> — the DAL read path P2.5 deferred.</b>
///
/// <para>The whole point is the join: an item id goes in, and every block of the card comes back
/// assembled from rows the shipped stores already hold — the owned <c>rpg_item</c>, the
/// <c>effect_instance</c> and its container, the <c>item_display_template</c> rows boot seeds, the
/// <c>item_socket</c> fill, the <c>effect_instance.enhance_level</c> the mutation ledger moved, the
/// <c>item_set_member</c> join under the wearer's assignments, and module 4's gate.</para>
///
/// <para>⛔ <b>Nothing here is a fixture atom.</b> The catalog is the REAL
/// <c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c> corpus through the REAL <c>FamilyExpansion</c> and
/// <c>AffixLibraryGenerator</c>, the item is minted by the REAL <c>Instantiator</c>, the sockets go
/// in through <c>SetSockets</c>, the enhancement through <c>AppendMutationOp</c>, and the set through
/// <c>ImportSetCorpus</c> + <c>SaveAssignment</c>. Against a real SQLite store, not a mock.</para>
///
/// <para><b>The acceptance test is byte equality against a hand-assembled input</b>
/// (<c>DisplayModel.Fingerprint</c>), so the DAL cannot pass by rendering something merely
/// plausible — it has to produce exactly the card a caller who assembled the input itself would get.</para>
/// </summary>
[Trait("VerificationId", "data.item-card")]
[Trait("VerificationId", "data.item-socket")]
public class ItemCardStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ItemCardStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // ---- the real corpus ------------------------------------------------------------------------------

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string Seed(params string[] parts) =>
        Path.Combine(new[] { KeepverseRoots.Content(), "data", "seed" }.Concat(parts).ToArray());

    /// <summary>The same tuning shape every other instantiation test uses, pinned at Θ_content 20 so
    /// <c>contentScale</c> is exactly ×1.000 and a frozen number is the atom's own roll.</summary>
    static readonly PowerTuning Power = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    const int PinTheta = 20;
    const string Rung = "heirloom";
    const int EnhanceCapMilli = 400;
    const int PlusThree = 3;

    static long? FlatReferenceBase(string channel) => channel switch
    {
        "maxHp" or "hp" => FusionRpg.Core.Battle.BattleRuleset.BaseHp(FamilyExpansion.ReferenceLevel),
        "atk" => FusionRpg.Core.Battle.BattleRuleset.BaseAtk(FamilyExpansion.ReferenceLevel),
        "defense" => FusionRpg.Core.Battle.BattleRuleset.BaseDefense(FamilyExpansion.ReferenceLevel),
        _ => null,
    };

    static readonly Lazy<IReadOnlyList<AtomRow>> RealAtoms = new(() =>
    {
        var itemsRoot = Seed("items");
        var tierBands = TierBandsFile.Read(
            File.ReadAllText(Path.Combine(itemsRoot, "_tuning", "tier-bands.v1.json")));

        var families = new List<FamilyEntryInput>();
        foreach (var file in Directory.GetFiles(Path.Combine(itemsRoot, "affix-families"), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            families.AddRange(AffixFamilyFile.Read(Path.GetFileName(file), File.ReadAllText(file)));
        }

        return FamilyExpansion.Expand(families, tierBands, FlatReferenceBase).Rows;
    });

    static readonly Lazy<IReadOnlyList<DisplayTemplateRow>> Templates = new(() =>
        Directory.EnumerateFiles(Seed("items", "display-templates"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => DisplayTemplates.Parse(File.ReadAllText(f)))
            .ToList());

    static readonly Lazy<IReadOnlyList<SetDef>> Sets = new(() =>
        Directory.EnumerateFiles(Seed("items", "sets"), "*.json")
            .Where(f => !Path.GetFileName(f).EndsWith(".ledger.json", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => SetCorpus.Parse(File.ReadAllText(f)))
            .ToList());

    static SocketTuning Sockets() =>
        SocketTuning.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    static ItemSurfaceTuning Surfaces() =>
        ItemSurfaceTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "item-surfaces.v1.json")));

    static EnhancementTuning Enhancement() =>
        EnhancementTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "enhancement.v1.json")));

    // ---- the seeded world -----------------------------------------------------------------------------

    /// <summary>What one seeded fixture produced, so a test asserts against real ids rather than
    /// re-deriving them.</summary>
    sealed record Fixture(
        string InstanceId, string ContainerId, string SpecimenId, ItemRole Role,
        SetDef Set, IReadOnlyList<AtomRow> Atoms);

    /// <summary>
    /// Seeds the whole world one card needs and returns the item's own ids. Everything goes in through
    /// a shipped write path — no INSERT is hand-written here, which is the same discipline the read
    /// side is being tested for.
    /// </summary>
    Fixture SeedWorld(bool withSockets = true, bool withEnhancement = true, bool withSet = true)
    {
        // 1 — the rarity ladder. Ordinals are append-only and load-bearing for the card's pip count,
        // so the whole ten-rung ladder goes in, not just the one rung the item uses.
        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            _store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5));

        _store.SetRarityBudget(Rung, "enhance_cap", EnhanceCapMilli);

        // 2 — the real atom catalog and the affix library generated from it.
        var atoms = RealAtoms.Value;
        Assert.NotEmpty(atoms);
        _store.UpsertAtoms(atoms);

        var affixes = AffixLibraryGenerator.Generate(atoms);
        var byId = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        AtomRow? LookupAtom(string id) => byId.TryGetValue(id, out var a) ? a : null;
        foreach (var affix in affixes) Assert.True(_store.UpsertAffix(affix, LookupAtom).IsOk);
        var affixById = affixes.ToDictionary(a => a.AffixId, StringComparer.Ordinal);
        AffixRow? LookupAffix(string id) => affixById.TryGetValue(id, out var a) ? a : null;

        // 3 — the display corpus, through the boot seeder the Server itself calls.
        _store.SeedItemDisplayTemplates(Templates.Value);

        // 4 — the set corpus, and the set this item belongs to.
        _store.ImportSetCorpus(Sets.Value);
        var set = Sets.Value.First(s => s.Tiers.Count > 0 && s.Members.Select(m => m.Role).Distinct().Count() >= 3);
        var member = set.Members[0];

        // 5 — the container. Its id IS the set member's base type: that is what item_set_member joins on.
        var templated = Templates.Value
            .Where(t => t.Status == "live")
            .Select(t => t.RuntimeFamily)
            .ToHashSet(StringComparer.Ordinal);

        var baseStat = atoms
            .Where(a => a.ParamsJson.Contains("\"maxHp\"", StringComparison.Ordinal))
            .OrderByDescending(a => a.Tier)
            .First();

        var pool = affixes
            .Where(a => a.Refs.Count == 1 && a.Refs[0].AtomId is { } id && byId.TryGetValue(id, out var at)
                        && !string.Equals(at.FamilyId, baseStat.FamilyId, StringComparison.Ordinal)
                        && templated.Contains(at.FamilyId))
            .OrderBy(a => a.AffixId, StringComparer.Ordinal)
            .Select(a => new ContainerPoolRow(a.AffixId, 100))
            .ToList();
        Assert.NotEmpty(pool);

        var container = new ContainerRow
        {
            ContainerId = member.ContainerId,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(member.Role),
            Rarity = Rung,
            LevelReq = 20,
            PrefixRolls = 3,
            SuffixRolls = 0,
            Atoms = new[] { new ContainerAtomRow(0, baseStat.AtomId) },
            Pool = pool,
        };
        Assert.True(_store.UpsertContainer(container).IsOk);

        // A granted action on the base type (module 19's own table).
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(container.ContainerId, 0, "action.cleave", ItemGrantRole.Granted));

        // 6 — the real mint, then the ownership row that makes it an ITEM rather than a bare instance.
        var mint = Instantiator.TryInstantiate(
            container, LookupAtom, LookupAffix, rollSeed: 0xC0FFEE, thetaContent: PinTheta,
            tuning: Power, out var instance, InstanceOrigin.Drop, catalogRevision: _store.GetCatalogRevision());
        Assert.True(mint.IsOk, mint.ToString());

        var instanceId = _store.SaveInstance(instance!);
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId,
            PlayerId = "p1",
            AcquiredUtc = "2026-09-06T00:00:00Z",
            OriginKind = "drop",
            Locked = true,
        }).IsOk);

        // 7 — the generation stamp, through the one transaction that writes it.
        _store.PersistLoot(
            "p1",
            new LootManifest("corr-1", "table.t1", 99UL, ItemLevel: 24, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "delve", "d1", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, container.ContainerId, 70, 24,
                    FrameMixPredicate.BucketOf(member.Frame), ItemRoles.Id(member.Role), "drop"),
            });

        // 8 — real sockets, through module 16's own writer, plus the generated resonance catalog.
        if (withSockets)
        {
            _store.SeedComboRecipes(ResonanceGenerator.Generate(Sockets()));
            _store.SetSockets(instanceId, new List<SocketSlot>
            {
                new(0, "fire", Crafted: false, "gem.ember-shard-t3", null),
                new(1, "fire", Crafted: false, "gem.ember-shard-t3", null),
                new(2, "", Crafted: true, null, null),
            });
        }

        // 9 — a real enhancement level, through module 15's ledger. No AtomValueSet: the level is the
        // thing under test, and moving the frozen magnitudes as well would confuse two questions.
        if (withEnhancement)
        {
            var appended = _store.AppendMutationOp(
                instanceId, MutationOpKind.Enhance, "corr-enh-1", opSeed: 7,
                new MutationResult("success", PlusThree, Array.Empty<AtomValueSet>(),
                    Array.Empty<int>(), Array.Empty<AtomAppend>()),
                newStateHash: null, originValuesJson: null, appliedUtc: "2026-09-06T00:01:00Z");
            Assert.True(appended.Ok, appended.Reason);
        }

        // 10 — the wearer. Three distinct member roles so SetEvaluator has a real 3 / N to report.
        const string specimen = "spec-1";
        if (withSet)
            foreach (var m in set.Members.GroupBy(m => m.Role).Select(g => g.First()).Take(3))
                // ⛔ Was `"item"` until 2026-09-06 (defect R2). `rpg_item_assignment` never carries
                // that kind — it is `rpg_item_loadout_entry`'s spelling, a different table — so this
                // fixture was seeding a row production cannot produce, and the card reader's own
                // matching `"item"` literal made the pair agree while both were wrong.
                _store.SaveAssignment(
                    specimen, m.Role,
                    string.Equals(m.ContainerId, container.ContainerId, StringComparison.Ordinal)
                        ? EquipRefKinds.Rolled
                        : EquipRefKinds.Stock,
                    string.Equals(m.ContainerId, container.ContainerId, StringComparison.Ordinal) ? instanceId : m.ContainerId);

        return new Fixture(instanceId, container.ContainerId, specimen, member.Role, set, atoms);
    }

    // ---- the corpus half the DAL cannot know ------------------------------------------------------------

    /// <summary>item-content T2: a real authored <c>Name</c> beside the key, the shape
    /// <c>ItemBaseTypeCorpus.Load</c> now produces for all 740 shipped entries.</summary>
    const string BaseName = "Proof Blade";

    static CardBaseType? BaseTypeOf(string containerId) => new CardBaseType(
        "bt." + containerId, "class.blade", "humanoid", "role.armament-primary", null, BaseName);

    static CardInsertLookup? InsertOf(string containerId) => new CardInsertLookup(
        new InsertDef(containerId, "gem.ember", "fire", 3), "gem.ember-shard");

    ItemCardCorpus Corpus() => new(
        BaseTypeOf, InsertOf, Sockets(), Surfaces(), Enhancement());

    ItemCardWearer Wearer(Fixture f) => new(new SpecimenActor(f.SpecimenId, Frame: null, Level: 30, Faction: null));

    // ====================================================================================================

    // ---- species-gear-chain T26: the promotion mark on the card ----------------------------------------

    [Fact]
    public void A_promoted_item_shows_its_new_rung_and_the_ordinal_it_climbed_from()
    {
        var f = SeedWorld();
        var rarities = _store.ListRarities();
        var fromOrdinal = _store.GetItemGeneration(f.InstanceId)!.RarityOrdinal;
        var fromRungId = rarities.First(r => r.Ordinal == fromOrdinal).RarityId;
        var nextRungId = RarityLadder.RungIds[RarityLadder.RungIndexOf(fromRungId) + 1];
        Promote(f.InstanceId, rarities.First(r => r.RarityId == nextRungId).Ordinal, fromOrdinal);

        // The rung follows the INSTANCE — the container's rarity is where the item started.
        var after = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;
        Assert.Equal("rarity." + nextRungId, after.Rarity.DisplayKey);
        Assert.Equal("rarity." + fromRungId, after.PromotedFromRarityKey);

        // ...and the mark reaches the rendered header, absent when never promoted.
        var header = ItemCardRenderer.Render(after).Blocks
            .Single(b => b.BlockKey == CardBlocks.Header).Lines[0];
        Assert.Equal("rarity." + fromRungId, header.Args["promotedFromKey"]);

        // A second promotion moves the rung again and leaves the mark where it began.
        var thirdRungId = RarityLadder.RungIds[RarityLadder.RungIndexOf(nextRungId) + 1];
        var thirdOrdinal = rarities.First(r => r.RarityId == thirdRungId).Ordinal;
        Promote(f.InstanceId, thirdOrdinal, rarities.First(r => r.RarityId == nextRungId).Ordinal);

        var third = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;
        Assert.Equal("rarity." + thirdRungId, third.Rarity.DisplayKey);
        Assert.Equal("rarity." + fromRungId, third.PromotedFromRarityKey);   // first source wins
    }

    void Promote(string instanceId, int toOrdinal, int fromOrdinal)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        _store.PromoteInstanceUnlocked(db, instanceId, toOrdinal, fromOrdinal);
        tx.Commit();
    }

    [Fact]
    public void An_item_id_becomes_a_rendered_card_with_no_hand_assembly()
    {
        var f = SeedWorld();

        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f));
        Assert.NotNull(input);

        var model = ItemCardRenderer.Render(input!);
        Assert.Equal(CardBlocks.Order, model.Blocks.Select(b => b.BlockKey).ToList());

        // The blocks that prove the join actually reached each module's own store.
        Assert.NotEmpty(model.Blocks.Single(b => b.BlockKey == CardBlocks.BaseStats).Lines);
        Assert.NotEmpty(model.Blocks.Single(b => b.BlockKey == CardBlocks.Affixes).Lines);
        Assert.NotEmpty(model.Blocks.Single(b => b.BlockKey == CardBlocks.Enhancement).Lines);
        Assert.NotEmpty(model.Blocks.Single(b => b.BlockKey == CardBlocks.Sockets).Lines);
        Assert.NotEmpty(model.Blocks.Single(b => b.BlockKey == CardBlocks.Set).Lines);
        Assert.NotEmpty(model.Blocks.Single(b => b.BlockKey == CardBlocks.GrantedAction).Lines);
    }

    /// <summary>
    /// ⭐ <b>The acceptance test.</b> The same stored state, assembled twice — once by the DAL and once
    /// by hand from the same public reads — must render to the same fingerprint. A DAL that produced a
    /// plausible-but-different card fails here, which is the only assertion that actually pins the
    /// join rather than the plumbing.
    /// </summary>
    [Fact]
    public void The_dal_assembled_card_is_byte_identical_to_a_hand_assembled_one()
    {
        var f = SeedWorld();

        var fromDal = ItemCardRenderer.Render(_store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!);
        var byHand = ItemCardRenderer.Render(HandAssemble(f));

        Assert.Equal(byHand.Fingerprint(), fromDal.Fingerprint());
    }

    /// <summary>
    /// The hand-assembled control: every field read through the same public store methods a caller
    /// had to string together before <c>GetItemCardInput</c> existed. This IS the code the DAL method
    /// replaces, kept here so the two can be compared rather than asserted equal by description.
    /// </summary>
    ItemCardInput HandAssemble(Fixture f)
    {
        var item = _store.GetItem(f.InstanceId)!;
        var instance = _store.GetInstance(f.InstanceId)!;
        var container = _store.GetContainer(instance.ContainerId)!;
        var generation = _store.GetItemGeneration(f.InstanceId)!;
        var head = _store.GetInstanceMutationHead(f.InstanceId)!;
        var baseType = BaseTypeOf(container.ContainerId)!.Value;

        var ladder = _store.ListRarities().OrderBy(r => r.Ordinal).ToList();
        var rung = ladder.FindIndex(r => r.RarityId == container.Rarity);

        var slots = _store.GetSockets(f.InstanceId);
        var fill = slots.Where(s => !s.IsEmpty)
            .Select(s => new SocketFill(s.Index, s.Affinity, InsertOf(s.InsertContainerId!)!.Value.Def))
            .ToList();

        var host = new SocketHost(container.ContainerId, f.Role, baseType.Frame, slots.Count,
            IsSetPiece: _store.ListSets().Any(s => s.Members.Any(m => m.ContainerId == container.ContainerId)));
        var distances = CombinationDistance.Evaluate(
            host, fill, _store.GetComboRecipes(), Sockets(), Surfaces(), out _);

        var worn = _store.ListAssignments(f.SpecimenId)
            .Select(a => new EquippedPiece(a.Role, a.RefKind == EquipRefKinds.Rolled
                ? _store.GetInstance(a.RefId)!.ContainerId
                : a.RefId))
            .ToList();
        SetProgress? progress = null;
        foreach (var row in SetEvaluator.Progress(worn, _store.ListSets()))
            if (string.Equals(row.SetId, f.Set.SetId, StringComparison.Ordinal)) { progress = row; break; }
        var disclosure = SetDisclosure.ForWearer(worn, _store.ListSets())
            .First(d => d.Piece.Role == f.Role);

        return new ItemCardInput(
            instance, container,
            id => _store.GetAtom(id),
            family => _store.GetDisplayTemplate(family),
            baseType,
            new CardRarity("rarity." + container.Rarity, rung + 1, RarityPalette.Dark[rung]),
            // item-content T1/T2: with no naming corpus in `Corpus()` the DAL falls back to the base
            // type's AUTHORED name, not to its display key. `Corpus()` deliberately supplies no naming
            // delegates so this mirror stays a mirror; the composed path is asserted separately below.
            baseType.Name)
        {
            ItemLevel = generation.ItemLevel,
            EnhanceLevel = head.EnhanceLevel,
            EnhanceGainMilli = EnhancePolicy.GainMilli(
                head.EnhanceLevel, _store.GetRarityBudget(container.Rarity!, "enhance_cap")!.Value, Enhancement()),
            LevelReq = container.LevelReq,
            SpecimenLevel = 30,
            Refusal = new EquipGate().Explain(f.Role, Wearer(f).Actor, generation.Frame, container.LevelReq, null),
            Sockets = slots.Select(s => new CardSocketCell(
                s.Index, s.Affinity.Length == 0 ? "" : "element." + s.Affinity, s.Crafted,
                s.IsEmpty ? null : InsertOf(s.InsertContainerId!)!.Value.NameKey)).ToList(),
            Combinations = distances
                .Where(d => d.State != CombinationDisplayState.Undiscovered)
                .Select(d => new CardCombination(
                    "combo." + d.Shape.ToString().ToLowerInvariant(), d.Shape, d.State, d.Distance,
                    d.GrantedTier, d.AllAttuned,
                    d.MissingElements.Select(e => "element." + e).ToList()))
                .ToList(),
            Set = progress is { } p
                ? new CardSet("set." + p.SetId, p.Count, p.Total,
                    f.Set.Tiers.OrderBy(t => t.PiecesRequired)
                        .Select(t => new CardSetTier(t.PiecesRequired, t.PiecesRequired <= p.Count, t.IsCapability))
                        .ToList(),
                    Redundant: disclosure.RedundantSetIds.Count > 0,
                    // item-content T2: `item_set.display_name`, which SetCorpus has always parsed and
                    // the card used to drop in favour of the derived `set.{setId}` key alone.
                    Name: f.Set.DisplayName,
                    // P8.4: the multi-set per-piece disclosure, mirrored from the SAME `disclosure`
                    // read the DAL uses — every other set this piece touches, each with its own
                    // progress and its own disclosure flag.
                    Others: disclosure.AdvancesSetIds.Concat(disclosure.RedundantSetIds)
                        .Where(id => !string.Equals(id, p.SetId, StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .Select(id =>
                        {
                            var peerDef = _store.ListSets().First(s => string.Equals(s.SetId, id, StringComparison.Ordinal));
                            var peerProgress = SetEvaluator.Progress(worn, _store.ListSets())
                                .First(x => string.Equals(x.SetId, id, StringComparison.Ordinal));
                            return new CardSetPeer(id, peerDef.DisplayName, peerProgress.Count, peerProgress.Total,
                                Redundant: disclosure.RedundantSetIds.Contains(id));
                        })
                        .ToList())
                : null,
            // item-content `granted-action-text` (T15): `action_id` already carries the `action.`
            // stem, so the name key IS the id — the old `"action." + g.ActionId` produced
            // `action.action.cleave`, a key nothing could author. The description key is READ from
            // `rpg_action.description_key`, with the `.desc` suffix left as the unauthored fallback.
            GrantedActions = _store.ListItemGrantedActions(container.ContainerId)
                .Select(g => new CardGrantedAction(
                    g.ActionId,
                    _store.GetAction(g.ActionId)?.DescriptionKey is { Length: > 0 } key ? key : g.ActionId + ".desc",
                    false, false))
                .ToList(),
            Unique = _store.GetItemUnique(container.ContainerId),
            Locked = item.Locked,
            Stale = item.Stale,
        };
    }

    // ---- item-content T1 / T2, the name ------------------------------------------------------------------

    /// <summary>Two inline naming rows, so this test proves the DAL's own wiring rather than the
    /// Server's file loader: one prefix family, one suffix family, both with words nothing else in the
    /// repo produces, so a name containing them can only have come through the composer.</summary>
    static Func<string, AffixNameSlot?> NameWordsFor(string prefixFamily, string suffixFamily)
    {
        var rows = new Dictionary<string, AffixNameSlot>(StringComparer.Ordinal)
        {
            [prefixFamily] = new(AffixClass.Prefix, new[]
            {
                new AffixNameRow("A", null, "Probe-A", null),
                new AffixNameRow("B", null, "Probe-B", null),
                new AffixNameRow("C", null, "Probe-C", null),
            }),
            [suffixFamily] = new(AffixClass.Suffix, new[]
            {
                new AffixNameRow("A", null, "of Probes", null),
                new AffixNameRow("B", null, "of Probes", null),
                new AffixNameRow("C", null, "of Probes", null),
            }),
        };

        return AffixNameWordCorpusStub(rows);
    }

    static Func<string, AffixNameSlot?> AffixNameWordCorpusStub(IReadOnlyDictionary<string, AffixNameSlot> rows) =>
        familyId => rows.TryGetValue(familyId, out var v) ? v : (AffixNameSlot?)null;

    /// <summary>
    /// ⭐ <b>The wiring T1 exists for.</b> The fixture container rolls three affixes, which is
    /// <c>ItemNameComposer.RareNameThreshold</c>, so the DAL must call the rare draw with the
    /// INSTANCE'S OWN <c>roll_seed</c> — asserted by drawing the same pair here.
    /// </summary>
    [Fact]
    public void The_card_name_is_composed_from_the_instances_own_rolled_affixes_and_seed()
    {
        var f = SeedWorld();
        var instance = _store.GetInstance(f.InstanceId)!;

        (string, string) Draw(long seed) => ("Rare" + seed, "Draw");
        var corpus = Corpus() with
        {
            LookupNameWords = AffixNameWordCorpusStub(new Dictionary<string, AffixNameSlot>(StringComparer.Ordinal)),
            RareNameDraw = Draw,
        };

        var input = _store.GetItemCardInput(f.InstanceId, corpus, Wearer(f))!;

        Assert.Equal($"Rare{instance.RollSeed} Draw", input.ItemName);
        Assert.NotEqual(BaseName, input.ItemName);
    }

    /// <summary>
    /// The rolled set the name is composed from is the DRAWN atoms only — the container's fixed core is
    /// what the base type guarantees, and counting it would push a two-affix item over the rare
    /// threshold and rename it. Same <c>seq</c>-set rule <c>ItemCardRenderer.Classify</c> reads.
    /// </summary>
    [Fact]
    public void The_named_affix_set_excludes_the_containers_fixed_core()
    {
        var f = SeedWorld();
        var instance = _store.GetInstance(f.InstanceId)!;
        var container = _store.GetContainer(f.ContainerId)!;

        var rolled = ItemNameAssembly.RolledAffixes(instance, container, id => _store.GetAtom(id));

        Assert.Equal(container.PrefixRolls + container.SuffixRolls, rolled.Count);
        Assert.Equal(instance.Atoms.Count - container.Atoms.Count, rolled.Count);
        Assert.DoesNotContain(rolled, a => container.Atoms.Any(c => c.Seq == a.Seq));
    }

    /// <summary>
    /// The affix grammar itself, end to end through the DAL: one drawn affix (under the threshold)
    /// composes <c>word + authored base name</c>. Uses its own single-family container so the drawn
    /// family — and therefore the word — is not left to the roll.
    /// </summary>
    [Fact]
    public void An_item_under_the_rare_threshold_is_named_by_the_affix_grammar()
    {
        var f = SeedWorld();

        // Raise the threshold above this item's affix count so the grammar path runs against a real,
        // already-minted instance rather than needing a second fixture.
        var previous = ItemsTuningHub.Tuning;
        ItemsTuningHub.Configure(previous with { RareNameThreshold = 99 });
        try
        {
            // The item rolls three prefixes, and `BestWord` picks the highest-tier one — NOT
            // necessarily the family `drawnFamily` names. Stubbing only one family made this test
            // depend on which affix won the roll, so it broke whenever the generated pool changed.
            // The contract under test is the grammar itself (word + authored base name), so the stub
            // supplies a Probe word for EVERY family the corpus asks about.
            var probe = new AffixNameSlot(AffixClass.Prefix, new[]
            {
                new AffixNameRow("A", null, "Probe-A", null),
                new AffixNameRow("B", null, "Probe-B", null),
                new AffixNameRow("C", null, "Probe-C", null),
            });
            var corpus = Corpus() with
            {
                LookupNameWords = _ => probe,
                RareNameDraw = _ => ("never", "used"),
            };

            var input = _store.GetItemCardInput(f.InstanceId, corpus, Wearer(f))!;

            Assert.StartsWith("Probe-", input.ItemName, StringComparison.Ordinal);
            Assert.EndsWith(" " + BaseName, input.ItemName, StringComparison.Ordinal);
        }
        finally { ItemsTuningHub.Configure(previous); }
    }

    /// <summary>item-content T2: the set block carries <c>item_set.display_name</c>, which
    /// <c>SetCorpus.Parse</c> has always read and the card used to drop for a derived
    /// <c>set.{setId}</c> key the string catalog has no row for.</summary>
    [Fact]
    public void The_set_block_carries_the_sets_authored_display_name()
    {
        var f = SeedWorld();
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.NotNull(input.Set);
        Assert.Equal(f.Set.DisplayName, input.Set!.Value.Name);
        Assert.NotEmpty(input.Set.Value.Name);
        Assert.NotEqual(input.Set.Value.NameKey, input.Set.Value.Name);
    }

    // ---- what each module contributes, asserted one at a time --------------------------------------------

    [Fact]
    public void The_enhancement_level_comes_from_the_mutation_ledger_and_its_gain_from_the_rungs_own_cap()
    {
        var f = SeedWorld();
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.Equal(PlusThree, input.EnhanceLevel);
        Assert.Equal(
            EnhancePolicy.GainMilli(PlusThree, EnhanceCapMilli, Enhancement()),
            input.EnhanceGainMilli);

        // And the header carries the `+N` token only because the ledger moved.
        var header = ItemCardRenderer.Render(input).Blocks
            .Single(b => b.BlockKey == CardBlocks.Header).Lines[0];
        Assert.Equal("+" + PlusThree, header.Args["enhance"]);
    }

    // ---- item-content `granted-action-text` (T14 / T15), card block 9 ------------------------------------

    /// <summary>
    /// ⭐ <b>The T15 acceptance test.</b> A real item grants a real action out of the REAL shipped
    /// corpus (<c>data/seed/actions/committed-round-*.json</c> through the real
    /// <see cref="ActionCorpusImporter"/>), and card block 9 carries that action's real authored
    /// description key — which resolves, through the real string catalog
    /// (<c>gk-content/content/display/en.json</c>), to the real authored sentence.
    ///
    /// <para>No fixture action and no fixture string: an invented pair would prove the plumbing and
    /// not the content, which is the exact gap `granted-action-text` exists to close.</para>
    /// </summary>
    [Fact]
    public void Block_9_renders_a_real_granted_actions_real_authored_description()
    {
        var f = SeedWorld();

        // The real corpus, imported through the real importer against the atom catalog SeedWorld
        // already loaded. Whichever rows resolve their atom families import; the rest refuse by name,
        // which is a content gap in a sibling pipeline and not this test's subject.
        var briefs = new[] { "committed-round-1.json", "committed-round-2.json" }
            .SelectMany(file => ActionCorpusBriefJson.Parse(
                File.ReadAllText(Path.Combine(KeepverseRoots.Content(), "data", "seed", "actions", file))))
            .ToList();
        var costTemplate = ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "action-corpus-cost-templates.v1.json")));

        var import = ActionCorpusImporter.Import(_store, briefs, costTemplate, RungPolicy.Table);
        var importedId = import.Outcomes.First(o => o.Imported).BriefId;
        var brief = briefs.First(b => string.Equals(b.Id, importedId, StringComparison.Ordinal));

        // The schema half (T14): the key survived the round trip through `rpg_action`.
        var stored = _store.GetAction(importedId);
        Assert.NotNull(stored);
        Assert.Equal(brief.DescriptionKey, stored!.DescriptionKey);

        // The render half (T15): grant it, then read the card block back.
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(
            f.ContainerId, 1, importedId, ItemGrantRole.DefaultAttack));

        // The REAL string catalog, the same file the Server loads at boot.
        var strings = DisplayStringCatalog.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.AuthoredContent(), "content", "display", "en.json")));
        var corpus = Corpus() with { LookupString = strings };

        var input = _store.GetItemCardInput(f.InstanceId, corpus, Wearer(f))!;
        var line = ItemCardRenderer.Render(input).Blocks
            .Single(b => b.BlockKey == CardBlocks.GrantedAction).Lines
            .Single(l => string.Equals(l.Args["nameKey"], importedId, StringComparison.Ordinal));

        Assert.Equal(brief.DescriptionKey, line.Args["descriptionKey"]);
        Assert.Equal("1", line.Args["battleOnly"]);        // a DefaultAttack grant is battle-only
        Assert.Equal("0", line.Args["alreadyKnown"]);

        // ⛔ The name key is the action id, NOT `"action." + id` — ids already carry the `action.`
        // stem, and the old derivation asked for `action.action.general.0003`.
        Assert.StartsWith("action.", line.Args["nameKey"], StringComparison.Ordinal);
        Assert.DoesNotContain("action.action.", line.Args["nameKey"], StringComparison.Ordinal);

        // ⭐ The finished text reaches the card, not just the key — the same `__rendered` contract
        // block 10 already uses, so a consumer has one rule for both.
        Assert.Equal(brief.Name, line.Args["__renderedName"]);
        var description = line.Args["__rendered"];
        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.Equal(strings(brief.DescriptionKey), description);
        Assert.DoesNotContain(importedId, description, StringComparison.Ordinal);
        Assert.DoesNotContain('{', description);
    }

    /// <summary>
    /// The compact armoury line's own half of §9.14: the store answers "is this item's granted action
    /// battle-only?" without assembling a card. <c>ItemSurfaceEndpoints</c>' armoury row reads exactly
    /// this, so the list and the card can never disagree.
    /// </summary>
    [Fact]
    public void The_compact_line_can_ask_for_the_battle_only_tag_without_opening_the_card()
    {
        var f = SeedWorld();

        // SeedWorld's own grant is a plain `Granted` entry — an extra selectable, not battle-only.
        Assert.False(_store.GrantsBattleOnlyAction(f.ContainerId));

        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(
            f.ContainerId, 1, "action.general.0003", ItemGrantRole.DefaultAttack));
        Assert.True(_store.GrantsBattleOnlyAction(f.ContainerId));

        // A disabled grant is not a promise about the lawn either.
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(
            f.ContainerId, 1, "action.general.0003", ItemGrantRole.DefaultAttack, Enabled: false));
        Assert.False(_store.GrantsBattleOnlyAction(f.ContainerId));
    }

    [Fact]
    public void A_plus_zero_item_reads_zero_out_of_sql_rather_than_omitting_the_read()
    {
        var f = SeedWorld(withEnhancement: false);
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.Equal(0, input.EnhanceLevel);
        Assert.Equal(0, input.EnhanceGainMilli);
        Assert.Empty(ItemCardRenderer.Render(input).Blocks.Single(b => b.BlockKey == CardBlocks.Enhancement).Lines);
    }

    [Fact]
    public void The_sockets_and_their_combinations_come_from_item_socket_and_the_recipe_table()
    {
        var f = SeedWorld();
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.Equal(3, input.Sockets.Count);
        Assert.Equal("element.fire", input.Sockets[0].AffinityKey);
        Assert.False(input.Sockets[0].IsEmpty);
        Assert.True(input.Sockets[2].Crafted);
        Assert.True(input.Sockets[2].IsEmpty);

        // Two attuned fire inserts really do fire a resonance, and it came off the stored catalog.
        Assert.NotEmpty(input.Combinations);
        Assert.Contains(input.Combinations, c => c.State == CombinationDisplayState.Active);
        // §4.3: an undiscovered combination never reaches the card at all.
        Assert.DoesNotContain(input.Combinations, c => c.State == CombinationDisplayState.Undiscovered);
    }

    [Fact]
    public void The_cards_combination_reachability_is_capacity_aware()
    {
        // strain-splice-host SSH1.3 (host-gate §2), corrected 2026-09-23: `CombinationDistance.Reachable`
        // reads `SocketHost.Capacity` — `CanEverHold`'s MinSockets, a Strain/Splice's summed ingredient
        // quantity, a Pure/Diversity threshold, and Ring/Eclipse's two-socket floor. A card that supplies
        // no socket-max lookup gives every host an unbounded capacity, so every one of those tests passes
        // and a combination the chassis can NEVER hold renders as a reachable goal — the "tooltip lie"
        // that method's own doc forbids.
        //
        // The observable difference on this fixture is the generated resonances' own thresholds: the
        // shipped tuning carries Pure 2/3/4 and Diversity 3/4, and this chassis opens 3 sockets, so a
        // capacity of 3 is the tightest value its own constructor accepts and the threshold-4 rows must
        // fall out. (A Strain/Splice cannot demonstrate this here: the fixture's container is a set
        // member, so D21 excludes both shapes on it whatever the capacity says.)
        var f = SeedWorld();

        var unbounded = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;
        Assert.Equal(4, unbounded.Combinations.Max(c => c.Distance ?? 0));

        var bounded = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f), _ => 3)!;
        Assert.Equal(3, bounded.Combinations.Max(c => c.Distance ?? 0));
        // The lookup narrows reachability; it does not empty the block.
        Assert.NotEmpty(bounded.Combinations);
    }

    [Fact]
    public void An_item_with_no_socket_rows_carries_no_cells_and_no_combinations()
    {
        var f = SeedWorld(withSockets: false);
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.Empty(input.Sockets);
        Assert.Empty(input.Combinations);
    }

    /// <summary>A filled socket with no gem catalog is REFUSED, never rendered as empty — the DAL's
    /// one hard failure, and the reason it is hard is that the alternative is a card that lies about
    /// what the item is wearing.</summary>
    [Fact]
    public void A_filled_socket_with_no_insert_lookup_is_refused_rather_than_shown_empty()
    {
        var f = SeedWorld();
        var blind = Corpus() with { LookupInsert = null };

        var ex = Assert.Throws<InvalidOperationException>(() => _store.GetItemCardInput(f.InstanceId, blind, Wearer(f)));
        Assert.Contains("gem", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_set_block_comes_from_item_set_member_joined_under_the_wearers_assignments()
    {
        var f = SeedWorld();
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.NotNull(input.Set);
        Assert.Equal("set." + f.Set.SetId, input.Set!.Value.NameKey);
        Assert.Equal(3, input.Set.Value.Count);
        Assert.Equal(f.Set.Members.Select(m => m.Role).Distinct().Count(), input.Set.Value.Total);
        // The WHOLE ladder renders, active and inactive alike.
        Assert.Equal(f.Set.Tiers.Count, input.Set.Value.Ladder.Count);
        Assert.Contains(input.Set.Value.Ladder, t => t.Active);
    }

    /// <summary>
    /// ⛔ <b>Defect R2, fixed 2026-09-06.</b> This read tested <c>a.RefKind == "item"</c>, which is
    /// <c>rpg_item_loadout_entry</c>'s spelling. <c>rpg_item_assignment</c> only ever carries
    /// <c>rolled</c> or <c>stock</c>, so the ONE kind <c>POST /api/items/equip</c> writes could never
    /// match: <c>wornRole</c> stayed null, and with it the whole set block and the requirement
    /// refusal. Latent while nothing wrote a rolled row, live from the day module 4 got a caller.
    ///
    /// <para>The row below is written exactly as <c>ItemEquipService.Equip</c> writes it —
    /// <c>SaveAssignment(specimen, role, "rolled", instanceId)</c>, the route's own last line — so
    /// this is the shipped write, not a fixture-shaped stand-in.</para>
    /// </summary>
    [Fact]
    public void A_rolled_assignment_is_what_the_card_reads_as_worn()
    {
        var f = SeedWorld(withSet: false);
        Assert.Null(_store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!.Refusal);

        // Exactly the row POST /api/items/equip lands.
        _store.SaveAssignment(f.SpecimenId, f.Role, EquipRefKinds.Rolled, f.InstanceId);

        // Worn now, so the gate has a role to judge — and it refuses this wearer's level, which it
        // can only do once `wornRole` is found. A silent null here IS the defect.
        var tooYoung = new ItemCardWearer(new SpecimenActor(f.SpecimenId, null, Level: 3, null));
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), tooYoung)!;

        Assert.NotNull(input.Refusal);
        Assert.Equal(EquipRefusalReason.LevelTooLow, input.Refusal!.Value.Reason);
    }

    /// <summary>The other half of R2: the retired literal must NOT be re-accepted as a synonym. A row
    /// spelled <c>"item"</c> in this table is unreadable, and an unreadable kind is a visible hole —
    /// the same rule <c>GetLoadoutEntriesValidated</c> applies to the preset table — never a silently
    /// plausible container id.</summary>
    [Fact]
    public void The_retired_item_ref_kind_is_not_quietly_treated_as_worn()
    {
        var f = SeedWorld(withSet: false);
        _store.SaveAssignment(f.SpecimenId, f.Role, "item", f.InstanceId);

        var tooYoung = new ItemCardWearer(new SpecimenActor(f.SpecimenId, null, Level: 3, null));

        Assert.Null(_store.GetItemCardInput(f.InstanceId, Corpus(), tooYoung)!.Refusal);
    }

    /// <summary>No wearer, no set — a set is a fact about a wearer, and an item in the bag advances
    /// nothing. Nor is there a requirement block or a refusal to explain.</summary>
    [Fact]
    public void An_unworn_item_carries_no_set_no_requirement_and_no_refusal()
    {
        var f = SeedWorld(withSet: false);
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.Null(input.Set);
        Assert.Null(input.Refusal);
        Assert.Empty(input.Requirements);
    }

    [Fact]
    public void The_equip_gate_refuses_a_level_it_actually_cannot_meet()
    {
        var f = SeedWorld();
        var tooYoung = new ItemCardWearer(new SpecimenActor(f.SpecimenId, null, Level: 3, null));

        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), tooYoung)!;

        Assert.NotNull(input.Refusal);
        Assert.Equal(EquipRefusalReason.LevelTooLow, input.Refusal!.Value.Reason);

        // And it reaches the card as a reason key plus a remedy, never as a bare boolean.
        var block = ItemCardRenderer.Render(input).Blocks.Single(b => b.BlockKey == CardBlocks.Requirements);
        Assert.Contains(block.Lines, l => l.Key == "item.card.requirement.refused");
    }

    [Fact]
    public void The_rarity_pips_and_colour_come_off_the_stored_ladder_in_ordinal_order()
    {
        var f = SeedWorld();
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        var expected = RarityLadder.RungIds.ToList().IndexOf(Rung);
        Assert.Equal("rarity." + Rung, input.Rarity.DisplayKey);
        Assert.Equal(expected + 1, input.Rarity.PipCount);
        Assert.Equal(RarityPalette.Dark[expected], input.Rarity.ColorHex);

        // The light theme is one argument, not a second code path.
        var light = _store.GetItemCardInput(f.InstanceId, Corpus() with { Palette = RarityPalette.Light }, Wearer(f))!;
        Assert.Equal(RarityPalette.Light[expected], light.Rarity.ColorHex);
    }

    [Fact]
    public void The_item_level_and_the_ownership_flags_come_from_their_own_tables()
    {
        var f = SeedWorld();
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;

        Assert.Equal(24, input.ItemLevel);
        Assert.True(input.Locked);
        Assert.False(input.Stale);
        // ssot-inventory.md:208 — `no_reassign` is reserved and deliberately not added, so there is no
        // column to read. False is the honest answer; a guess would not be.
        Assert.False(input.NoReassign);
    }

    /// <summary>The three genuinely absent rows return <c>null</c> rather than throwing — an
    /// unowned instance, a missing instance and a container the catalog does not have are all
    /// "no card", not "bad card".</summary>
    [Fact]
    public void A_missing_row_returns_null_rather_than_throwing()
    {
        SeedWorld();
        Assert.Null(_store.GetItemCardInput("no-such-instance", Corpus()));

        // A real effect_instance that nobody owns is not an item card: rpg_item is the ownership root.
        var orphan = _store.SaveInstance(new InstanceRow
        {
            ContainerId = "item.orphan",
            RollSeed = 1,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, RealAtoms.Value[0].AtomId, """{"amount":1}""") },
        });
        Assert.Null(_store.GetItemCardInput(orphan, Corpus()));
    }

    /// <summary>The display templates the card renders with are the ones boot seeded into
    /// <c>item_display_template</c> — not a file read behind the DAL's back. Dropping the table's
    /// rows makes the renderer refuse by name, which is what proves the read is real.</summary>
    [Fact]
    public void The_card_renders_from_item_display_template_rows_and_refuses_without_them()
    {
        var f = SeedWorld();
        Assert.NotEmpty(_store.ListDisplayTemplates());

        // Same store, same item, but ask for a family the table does not have: the lookup the DAL
        // handed over returns null and the renderer refuses rather than printing a raw id.
        var input = _store.GetItemCardInput(f.InstanceId, Corpus(), Wearer(f))!;
        Assert.Null(input.LookupTemplate("atom.not-a-family"));
        Assert.NotNull(input.LookupTemplate(
            f.Atoms.First(a => _store.GetDisplayTemplate(a.FamilyId) is not null).FamilyId));
    }
}
