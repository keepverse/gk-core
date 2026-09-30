using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Generation;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Items.Power;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Core.Items.Uniques;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// The <b>Card</b> and <b>Compare</b> levels of `item-card` (item module 10), driven over a REAL
/// rolled instance: real affix families → the real <see cref="FamilyExpansion"/> generator → the real
/// <see cref="AffixLibraryGenerator"/> → the real <see cref="Instantiator"/>, with real display
/// templates, a real base type, a real rarity rung, real sockets, a real set and a real enhancement
/// level on top.
///
/// <para>⛔ <b>Nothing here hand-builds an atom.</b> Every <c>AtomRow</c> in every test comes out of
/// <c>FamilyExpansion.Expand</c> over <c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c>, and every frozen
/// value comes out of <c>Instantiator.TryInstantiate</c>. A fixture atom would let the card render
/// something no drop can produce, which is exactly the gap P2.5 said the Card level had to close.</para>
/// </summary>
public class ItemCardTests
{
    // ---- real content, loaded once -----------------------------------------------------------------

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string Seed(params string[] parts) =>
        Path.Combine(new[] { RepoRoot(), "data", "seed" }.Concat(parts).ToArray());

    /// <summary>The same tuning shape every other instantiation test in this suite uses.</summary>
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    /// <summary>Θ_content's pin — contentScale(20) == 1.000 exactly, so a frozen value is the atom's
    /// own rolled number and an assertion about it is about the roll, not about the depth.</summary>
    const int PinTheta = 20;

    /// <summary>
    /// The HIGHEST published <c>tier-bands.v{n}.json</c> — the one a real generation run reads, and
    /// the one `_meta.rebalance` means by <i>"writes tier-bands.v{n+1}.json; the old version stays for
    /// revert"</i>. Resolved, not hard-coded: this fixture pinned <c>v1</c> for as long as v1 was the
    /// only version, which quietly meant a published rebalance would not have reached the guard test
    /// that is supposed to prove the corpus renders (item-content T13).
    /// </summary>
    static string LatestTierBandsPath()
    {
        var dir = Path.Combine(Seed("items"), "_tuning");
        var latest = Directory.GetFiles(dir, "tier-bands.v*.json")
            .Select(f => (Path: f, Version: int.Parse(
                Path.GetFileNameWithoutExtension(f)["tier-bands.v".Length..],
                System.Globalization.CultureInfo.InvariantCulture)))
            .OrderByDescending(t => t.Version)
            .FirstOrDefault();
        Assert.False(latest.Path is null, $"no tier-bands.v*.json under {dir}");
        return latest.Path;
    }

    static readonly Lazy<IReadOnlyList<AtomRow>> RealAtoms = new(() =>
    {
        var tierBands = TierBandsFile.Read(File.ReadAllText(LatestTierBandsPath()));

        var families = new List<FamilyEntryInput>();
        foreach (var file in Directory.GetFiles(Path.Combine(Seed("items"), "affix-families"), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            families.AddRange(AffixFamilyFile.Read(Path.GetFileName(file), File.ReadAllText(file)));
        }

        return FamilyExpansion.Expand(families, tierBands, FlatReferenceBase).Rows;
    });

    static long? FlatReferenceBase(string channel) => channel switch
    {
        "maxHp" or "hp" => BattleRuleset.BaseHp(FamilyExpansion.ReferenceLevel),
        "atk" => BattleRuleset.BaseAtk(FamilyExpansion.ReferenceLevel),
        "defense" => BattleRuleset.BaseDefense(FamilyExpansion.ReferenceLevel),
        _ => null,
    };

    static readonly Lazy<IReadOnlyDictionary<string, AtomRow>> AtomsById = new(() =>
        RealAtoms.Value.ToDictionary(a => a.AtomId, StringComparer.Ordinal));

    static readonly Lazy<IReadOnlyDictionary<string, AffixRow>> AffixesById = new(() =>
        AffixLibraryGenerator.Generate(RealAtoms.Value).ToDictionary(a => a.AffixId, StringComparer.Ordinal));

    static readonly Lazy<IReadOnlyDictionary<string, DisplayTemplateRow>> Templates = new(() =>
    {
        var dir = Seed("items", "display-templates");
        return Directory.EnumerateFiles(dir, "*.json")
            .SelectMany(f => DisplayTemplates.Parse(File.ReadAllText(f)))
            .ToDictionary(r => r.RuntimeFamily, StringComparer.Ordinal);
    });

    static readonly Lazy<IReadOnlyList<SetDef>> Sets = new(() =>
        Directory.EnumerateFiles(Seed("items", "sets"), "*.json")
            .Where(f => !Path.GetFileName(f).EndsWith(".ledger.json", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => SetCorpus.Parse(File.ReadAllText(f)))
            .ToList());

    static AtomRow? LookupAtom(string id) => AtomsById.Value.TryGetValue(id, out var a) ? a : null;
    static AffixRow? LookupAffix(string id) => AffixesById.Value.TryGetValue(id, out var a) ? a : null;

    static DisplayTemplateRow? LookupTemplate(string familyId) =>
        Templates.Value.TryGetValue(familyId, out var t) ? t : null;

    /// <summary>One real base-type entry, read straight out of the shipped corpus.</summary>
    readonly record struct RealBaseType(
        string Id, string NameKey, string Frame, string Role, string ClassId,
        string? ImplicitFamily, int SocketMax, string? FlavourKey);

    static readonly Lazy<IReadOnlyList<RealBaseType>> BaseTypes = new(() =>
    {
        var rows = new List<RealBaseType>();
        foreach (var file in Directory.GetFiles(Seed("items", "base-types"), "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;
            foreach (var e in entries.EnumerateArray())
            {
                string? implicitFamily = null;
                if (e.TryGetProperty("implicit", out var imp) && imp.ValueKind == JsonValueKind.Object
                    && imp.TryGetProperty("family", out var fam) && fam.ValueKind == JsonValueKind.String)
                    implicitFamily = fam.GetString();

                rows.Add(new RealBaseType(
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("nameKey").GetString()!,
                    e.GetProperty("frame").GetString()!,
                    e.GetProperty("role").GetString()!,
                    e.TryGetProperty("class", out var c) ? c.GetString() ?? "" : "",
                    implicitFamily,
                    e.TryGetProperty("socketMax", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
                    e.TryGetProperty("flavorKey", out var f) ? f.GetString() : null));
            }
        }
        return rows;
    });

    // ---- the real mint: a real base type + real affixes -> a real instance ---------------------------

    /// <summary>
    /// A base type whose implicit family REALLY expands into a real atom and REALLY has a display
    /// template — chosen from the corpus rather than hardcoded, so a content edit moves the fixture
    /// instead of breaking it.
    /// </summary>
    static RealBaseType PickBaseType(string role = "armament-primary", string frame = "humanoid") =>
        BaseTypes.Value.First(b =>
            b.Role == role && b.Frame == frame
            && b.ImplicitFamily is { } fam
            && AtomsById.Value.ContainsKey(AtomRow.DeriveId(fam, "", 1))
            && LookupTemplate(fam) is { Status: "live" });

    /// <summary>
    /// The container a host mints for one dropped base type. This is the shape
    /// <c>LootContentView.Mint</c> hands out — a fixed core (base stat at <c>seq 0</c>, the base
    /// type's own implicit at <c>seq 1</c>) plus the rung's prefix/suffix budgets over the real
    /// generated affix library.
    /// </summary>
    /// <param name="extraCore">Fixed-core atoms past <c>seq 1</c> — what a hand-authored unique adds.
    /// Their families leave the pool with the other two, for the same reason.</param>
    static ContainerRow MintContainer(
        RealBaseType baseType, int prefixRolls, int suffixRolls, string rarityId,
        IReadOnlyList<AtomRow>? extraCore = null)
    {
        // A real base stat: the highest-tier `maxHp` flat atom the generator produced.
        var baseStat = RealAtoms.Value
            .Where(a => a.ParamsJson.Contains("\"maxHp\"", StringComparison.Ordinal))
            .OrderByDescending(a => a.Tier)
            .First();

        var implicitAtom = AtomsById.Value[AtomRow.DeriveId(baseType.ImplicitFamily!, "", 1)];
        var core = new List<AtomRow> { baseStat, implicitAtom };
        core.AddRange(extraCore ?? Array.Empty<AtomRow>());

        // The fixed core's own families are excluded from the pool -- ContainerValidator refuses
        // `DuplicateAtomInContainer` outright, and a real minter has the same obligation: an item
        // cannot roll the implicit it already carries.
        var coreFamilies = core.Select(a => a.FamilyId).ToHashSet(StringComparer.Ordinal);

        // Every affix whose family the display corpus can render, in id order -- the pool a real drop
        // draws from. Ordered so the draw is reproducible from the seed alone.
        //
        // ⛔ POOLED-CHANNEL atoms are excluded, and it is a property of THIS MINTER, not a dodge.
        // Everything below is minted through `Instantiator.TryInstantiate`, which copies an E30 pool
        // object into `values_json` untouched -- so the affix has no concrete channel, hence no unit
        // and no element, and `ItemCardRenderer` refuses it by name. That behaviour is already pinned
        // deliberately, from both sides, by `A_compose_minted_pooled_channel_affix_renders_its_
        // concrete_element` and its negative control below: resolving a pool is
        // `InstanceProducer.Compose`'s job. Before tier-bands v2 (item-content T10) the shipped
        // expansion contained no pooled atom at all, so this filter was invisible; seven element-typed
        // families are admitted now, and without it these fixtures draw an affix their own minter is
        // documented as unable to render.
        var pool = AffixesById.Value.Values
            .Where(a => a.Refs.Count == 1 && a.Refs[0].AtomId is { } id
                        && AtomsById.Value.TryGetValue(id, out var atom)
                        && !coreFamilies.Contains(atom.FamilyId)
                        && !atom.ParamsJson.Contains("\"pool\"", StringComparison.Ordinal)
                        && LookupTemplate(atom.FamilyId) is { Status: "live" })
            .OrderBy(a => a.AffixId, StringComparer.Ordinal)
            .Select(a => new ContainerPoolRow(a.AffixId, 100))
            .ToList();

        return new ContainerRow
        {
            ContainerId = baseType.Id,
            Kind = ContainerKind.Item,
            Slot = baseType.Role,
            Rarity = rarityId,
            LevelReq = 20,
            PrefixRolls = prefixRolls,
            SuffixRolls = suffixRolls,
            Atoms = core.Select((a, i) => new ContainerAtomRow(i, a.AtomId)).ToList(),
            Pool = pool,
        };
    }

    static InstanceRow Mint(ContainerRow container, long rollSeed, int theta = PinTheta)
    {
        var r = Instantiator.TryInstantiate(
            container, LookupAtom, LookupAffix, rollSeed, theta, Tuning, out var instance,
            InstanceOrigin.Drop, catalogRevision: 7);
        Assert.True(r.IsOk, r.ToString());
        return instance!;
    }

    /// <summary>A fully-dressed card over a real instance: real sockets, a real set, a real
    /// enhancement level and real requirements.</summary>
    static ItemCardInput FullCard(long rollSeed = 0xC0FFEE, int enhanceLevel = 12)
    {
        var baseType = PickBaseType();
        // suffixRolls is 0 on purpose, and it is a CONTENT fact, not a fixture convenience:
        // `FamilyExpansion` only expands channel-bearing families, none of which carries a trigger, so
        // the generated affix library is entirely prefix-class today and `ContainerValidator` refuses a
        // suffix budget it cannot fill. The suffix half of §4.1's ordering rule is exercised in
        // `A_triggered_affix_renders_as_a_suffix_after_every_prefix` against a real authored trigger.
        var container = MintContainer(baseType, prefixRolls: 4, suffixRolls: 0, rarityId: "heirloom");
        var instance = Mint(container, rollSeed);

        var socketTuning = SocketGeometryTests.Shipped();
        var surfaceTuning = ItemSurfaceTests.Shipped();
        var catalog = ResonanceGenerator.Generate(socketTuning);

        var host = new SocketHost(container.ContainerId, ItemRole.ArmamentPrimary, baseType.Frame, SocketCount: 3);
        var fill = new[]
        {
            new SocketFill(0, "fire", new InsertDef("gem.ember-shard-t3", "gem.ember", "fire", 3)),
            new SocketFill(1, "fire", new InsertDef("gem.ember-shard-t3", "gem.ember", "fire", 3)),
        };
        var distances = CombinationDistance.Evaluate(host, fill, catalog, socketTuning, surfaceTuning, out _);

        // A real set from the shipped corpus, evaluated by module 12's own evaluator over a real
        // wearer -- never a hand-counted "3 / 4".
        var set = Sets.Value.First(s => s.Tiers.Count > 0 && s.Members.Count >= 3);
        var worn = set.Members.Take(3).Select(m => new EquippedPiece(m.Role, m.ContainerId)).ToList();
        var progress = SetEvaluator.Progress(worn, Sets.Value).First(p => p.SetId == set.SetId);
        var disclosure = SetDisclosure.ForWearer(worn, Sets.Value);

        return new ItemCardInput(
            instance, container, LookupAtom, LookupTemplate,
            new CardBaseType(baseType.NameKey, "class." + baseType.ClassId, baseType.Frame,
                "role." + baseType.Role, baseType.FlavourKey),
            new CardRarity("rarity.heirloom", 7, "#8bd3c7"),
            ItemName: "Honed Hatchet of Vigour")
        {
            ItemLevel = 24,
            EnhanceLevel = enhanceLevel,
            EnhanceGainMilli = 240,
            LevelReq = container.LevelReq,
            SpecimenLevel = 30,
            Requirements = new[] { new CardRequirement("attr.sinew", 32, 29, 3, Gates: true) },
            Sockets = new[]
            {
                new CardSocketCell(0, "element.fire", Crafted: false, "gem.ember-shard"),
                new CardSocketCell(1, "element.fire", Crafted: false, "gem.ember-shard"),
                new CardSocketCell(2, "", Crafted: true, null),
            },
            Combinations = distances
                .Where(d => d.State != CombinationDisplayState.Undiscovered)
                .Select(d => new CardCombination(
                    "combo." + d.Shape.ToString().ToLowerInvariant(), d.Shape, d.State, d.Distance,
                    d.GrantedTier, d.AllAttuned,
                    d.MissingElements.Select(e => "element." + e).ToList()))
                .ToList(),
            Set = new CardSet(
                "set." + set.SetId, progress.Count, progress.Total,
                set.Tiers.Select(t => new CardSetTier(
                    t.PiecesRequired, t.PiecesRequired <= progress.Count, t.IsCapability)).ToList(),
                Redundant: disclosure.Any(d => d.RedundantSetIds.Count > 0)),
            GrantedActions = new[] { new CardGrantedAction("action.cleave", "action.cleave.desc", true, false) },
            Locked = true,
            Stale = false,
            NoReassign = false,
        };
    }

    // ================================================================================================
    //  The Card level
    // ================================================================================================

    [Fact]
    public void The_real_corpus_expands_into_real_atoms_and_a_real_affix_library()
    {
        // Guards the fixture itself: if the generator ever returns nothing, every assertion below
        // would pass vacuously.
        Assert.NotEmpty(RealAtoms.Value);
        Assert.NotEmpty(AffixesById.Value);
        // 98 → 107 on 2026-09-06: the phantom-closure pass authored nine families real content already
        // referenced, and each brought its own template row (7 into triggered.json, 2 into derived.json).
        // 107 → 109 the same day (item-content T11): `atom.chill-punisher` and `atom.rot-punisher` had
        // been authored into g-punisher.json with no template row, so every family now has one --
        // asserted against the family count itself rather than as a second literal. 109 -> 112
        // (2026-09-07): an affix-families-gen trial batch's three new families each got a real
        // template the same run this test caught their absence.
        Assert.Equal(RealFamilies.Value.Count, Templates.Value.Count);
        Assert.NotEmpty(BaseTypes.Value);
    }

    [Fact]
    public void A_real_rolled_instance_renders_all_eleven_blocks_in_order()
    {
        var model = ItemCardRenderer.Render(FullCard());

        // The count is fully implied by the element-wise equality below -- never pinned separately
        // (population-pin SE3.3, 2026-09-19).
        Assert.Equal(CardBlocks.Order, model.Blocks.Select(b => b.BlockKey).ToList());
    }

    [Fact]
    public void Every_block_the_fixture_populates_actually_has_lines()
    {
        var model = ItemCardRenderer.Render(FullCard());
        var byKey = model.Blocks.ToDictionary(b => b.BlockKey, StringComparer.Ordinal);

        // The eight this item genuinely has content for. Flavour is uniques-only (this is not one)
        // and the fixture leaves it empty on purpose -- asserted separately below.
        foreach (var key in new[]
                 {
                     CardBlocks.Header, CardBlocks.Requirements, CardBlocks.BaseStats, CardBlocks.Implicit,
                     CardBlocks.Affixes, CardBlocks.Enhancement, CardBlocks.Sockets, CardBlocks.Set,
                     CardBlocks.GrantedAction, CardBlocks.Footer,
                 })
            Assert.True(byKey[key].Lines.Count > 0, $"block '{key}' rendered no lines");
    }

    [Fact]
    public void Base_stats_come_from_seq_zero_and_the_implicit_from_seq_one()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);
        var byKey = model.Blocks.ToDictionary(b => b.BlockKey, StringComparer.Ordinal);

        Assert.Single(byKey[CardBlocks.BaseStats].Lines);
        Assert.Single(byKey[CardBlocks.Implicit].Lines);
        Assert.All(byKey[CardBlocks.BaseStats].Lines, l => Assert.Equal(SourceKind.Base, l.SourceKind));
        Assert.All(byKey[CardBlocks.Implicit].Lines, l => Assert.Equal(SourceKind.Implicit, l.SourceKind));

        // And the implicit really is the base type's own authored family, not whatever sorted first.
        var implicitAtomId = input.Instance.Atoms.Single(a => a.Seq == 1).AtomId;
        Assert.Equal(LookupTemplate(LookupAtom(implicitAtomId)!.FamilyId)!.Value.NameKey,
            byKey[CardBlocks.Implicit].Lines[0].Key);
    }

    [Fact]
    public void Every_drawn_affix_renders_one_line_prefixes_before_suffixes()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);
        var affixes = model.Blocks.Single(b => b.BlockKey == CardBlocks.Affixes).Lines;

        var coreSeqs = input.Container.Atoms.Select(a => a.Seq).ToHashSet();
        var drawn = input.Instance.Atoms.Count(a => !coreSeqs.Contains(a.Seq));
        Assert.Equal(drawn, affixes.Count);
        Assert.True(drawn > 0, "the real pool drew nothing -- the fixture would prove nothing");

        var lastPrefix = affixes.Select((l, i) => (l, i))
            .Where(x => x.l.SourceKind == SourceKind.AffixPrefix).Select(x => x.i).DefaultIfEmpty(-1).Max();
        var firstSuffix = affixes.Select((l, i) => (l, i))
            .Where(x => x.l.SourceKind == SourceKind.AffixSuffix).Select(x => x.i).DefaultIfEmpty(int.MaxValue).Min();
        Assert.True(lastPrefix < firstSuffix, "a suffix rendered before a prefix");
    }

    /// <summary>
    /// §4.1's <b>prefixes then suffixes</b>, with a real suffix.
    ///
    /// <para>⚠ The generated affix library is entirely prefix-class today — <c>FamilyExpansion</c>
    /// expands only channel-bearing families and none of those carries a trigger, so
    /// <c>ContainerValidator</c> refuses any suffix budget over it. That is a real content state, not
    /// a renderer limit, so this test supplies the one thing the corpus is missing: a real expanded
    /// atom carrying <c>g-on-hit.json</c>'s own authored trigger shape
    /// (<c>{"trigger":"OnDamageDealt"}</c>). The <b>class is still derived</b>, by
    /// <c>AffixValidator.AffixClassOfAtom</c> through the real <c>AffixLibraryGenerator</c> — nothing
    /// here declares "this is a suffix".</para>
    /// </summary>
    [Fact]
    public void A_triggered_affix_renders_as_a_suffix_after_every_prefix()
    {
        var baseType = PickBaseType();
        var plain = MintContainer(baseType, prefixRolls: 2, suffixRolls: 0, rarityId: "heirloom");
        var coreFamilies = plain.Atoms.Select(a => LookupAtom(a.AtomId)!.FamilyId).ToHashSet(StringComparer.Ordinal);

        var candidates = RealAtoms.Value
            .Where(a => LookupTemplate(a.FamilyId) is { Status: "live" })
            .Where(a => !coreFamilies.Contains(a.FamilyId))
            .GroupBy(a => a.FamilyId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(a => a.AtomId, StringComparer.Ordinal).First())
            .Take(3)
            .ToList();

        // The last one becomes triggered -- same row, same magnitude, plus a real `when` clause.
        var triggered = candidates[^1] with { WhenJson = "{\"trigger\":\"OnDamageDealt\"}" };
        var catalogRows = candidates.Take(candidates.Count - 1).Append(triggered).ToList();
        var catalog = plain.Atoms
            .Select(a => LookupAtom(a.AtomId)!)
            .Concat(catalogRows)
            .ToDictionary(a => a.AtomId, StringComparer.Ordinal);

        var affixes = AffixLibraryGenerator.Generate(catalogRows).ToDictionary(a => a.AffixId, StringComparer.Ordinal);
        Assert.Contains(affixes.Values, a => a.Class == AffixClass.Suffix);

        var container = plain with
        {
            PrefixRolls = 2,
            SuffixRolls = 1,
            Pool = affixes.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => new ContainerPoolRow(k, 100)).ToList(),
        };

        AtomRow? Atom(string id) => catalog.TryGetValue(id, out var a) ? a : null;
        AffixRow? Affix(string id) => affixes.TryGetValue(id, out var a) ? a : null;

        var r = Instantiator.TryInstantiate(container, Atom, Affix, 0x51DE, PinTheta, Tuning, out var instance);
        Assert.True(r.IsOk, r.ToString());

        var lines = ItemCardRenderer.Render(new ItemCardInput(
            instance!, container, Atom, LookupTemplate,
            new CardBaseType(baseType.NameKey, "class." + baseType.ClassId, baseType.Frame,
                "role." + baseType.Role, baseType.FlavourKey),
            new CardRarity("rarity.heirloom", 7, "#8bd3c7"), "Honed Hatchet"))
            .Blocks.Single(b => b.BlockKey == CardBlocks.Affixes).Lines;

        Assert.Contains(lines, l => l.SourceKind == SourceKind.AffixPrefix);
        Assert.Contains(lines, l => l.SourceKind == SourceKind.AffixSuffix);

        var lastPrefix = lines.Select((l, i) => (l, i))
            .Where(x => x.l.SourceKind == SourceKind.AffixPrefix).Max(x => x.i);
        var firstSuffix = lines.Select((l, i) => (l, i))
            .Where(x => x.l.SourceKind == SourceKind.AffixSuffix).Min(x => x.i);
        Assert.True(lastPrefix < firstSuffix, "a suffix rendered before a prefix");
    }

    [Fact]
    public void Rendered_equals_applied_every_magnitude_is_the_frozen_integer()
    {
        // Rule 4, the invariant: "if the card shows +45 hp, values_json holds 45." Stated over both
        // shapes `values_json` really carries -- a frozen number (Fixed / OnInstantiate) and an
        // untouched `{min,max,roll}` band (OnApply, which the shipped corpus authors for every
        // generated affix). A band is not a missing magnitude; it is the thing §3.4 says to show.
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);

        var expected = new List<string>();
        foreach (var row in input.Instance.Atoms.OrderBy(a => a.Seq))
        {
            using var doc = JsonDocument.Parse(row.ValuesJson);
            if (!doc.RootElement.TryGetProperty("amount", out var amt)) continue;

            expected.Add(amt.ValueKind == JsonValueKind.Number
                ? amt.GetInt64().ToString()
                : amt.GetProperty("min").GetInt64() + "–" + amt.GetProperty("max").GetInt64());
        }
        Assert.NotEmpty(expected);

        var shown = model.Lines
            .Where(l => l.Unit == UnitClass.GameUnits && l.Args.ContainsKey("value"))
            .Select(l => l.Args["value"])
            .ToList();

        Assert.NotEmpty(shown);
        foreach (var v in shown) Assert.Contains(v, expected);
    }

    [Fact]
    public void Only_an_on_instantiate_line_carries_a_roll_quality()
    {
        // A `Fixed` line never rolled and an `OnApply` line has a BAND -- "where in the band did this
        // land" is a question about a hit that has not happened yet. Reporting 1000‰ on either (which
        // is what an unreadable band degrades to) would put a full-luck number on a line with no luck.
        var model = ItemCardRenderer.Render(FullCard());

        foreach (var line in model.Lines.Where(l => l.SourceKind is SourceKind.Base or SourceKind.Implicit
                                                    or SourceKind.AffixPrefix or SourceKind.AffixSuffix))
            Assert.Equal(line.RollBar is not null, line.RollQualityPerMille is not null);
    }

    [Fact]
    public void An_on_apply_affix_renders_its_band_and_never_a_single_number()
    {
        // §3.4's OnApply arm: no bar, and the BAND -- "the hit rolled it, not the item".
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);

        var bands = model.Lines
            .Where(l => l.Args.TryGetValue("value", out var v) && v.Contains('–'))
            .ToList();

        Assert.NotEmpty(bands);
        foreach (var line in bands)
        {
            Assert.Null(line.RollBar);
            Assert.Contains('–', line.Args["__rendered"]);
        }
    }

    [Fact]
    public void A_roll_bar_appears_only_where_a_range_existed()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);

        foreach (var line in model.Lines.Where(l => l.SourceKind is SourceKind.Base or SourceKind.Implicit
                                                    or SourceKind.AffixPrefix or SourceKind.AffixSuffix))
        {
            if (line.RollBar is not { } bar) continue;
            Assert.InRange(bar.Segments, 1, RollBar.MaxSegments);
            Assert.NotNull(line.RollQualityPerMille);
        }
    }

    [Fact]
    public void The_enhancement_is_one_block_never_stacked_lines()
    {
        var model = ItemCardRenderer.Render(FullCard(enhanceLevel: 12));
        var block = model.Blocks.Single(b => b.BlockKey == CardBlocks.Enhancement);

        Assert.Single(block.Lines);
        Assert.Equal("12", block.Lines[0].Args["level"]);
        // Per-mille never renders as per-mille (§2.4) -- 240 goes out as 24%, through the shared helper.
        Assert.Equal("24%", block.Lines[0].Args["gain"]);
    }

    [Fact]
    public void A_plus_zero_item_emits_no_enhancement_block_and_no_header_token()
    {
        var model = ItemCardRenderer.Render(FullCard(enhanceLevel: 0));

        Assert.Empty(model.Blocks.Single(b => b.BlockKey == CardBlocks.Enhancement).Lines);
        Assert.DoesNotContain("enhance",
            model.Blocks.Single(b => b.BlockKey == CardBlocks.Header).Lines[0].Args.Keys);
    }

    [Fact]
    public void The_header_puts_pips_before_the_enhancement_prefix_before_the_name()
    {
        var header = ItemCardRenderer.Render(FullCard()).Blocks
            .Single(b => b.BlockKey == CardBlocks.Header).Lines[0];

        // All three of I1's redundant channels, never colour alone.
        Assert.Equal("7", header.Args["pips"]);
        Assert.Equal("rarity.heirloom", header.Args["rungKey"]);
        Assert.Equal("#8bd3c7", header.Args["colorHex"]);
        Assert.Equal("+12", header.Args["enhance"]);
        Assert.Equal("Honed Hatchet of Vigour", header.Args["name"]);
    }

    [Fact]
    public void The_promotion_mark_renders_the_source_rung_and_is_absent_when_never_promoted()
    {
        // Never promoted: no mark at all, rather than an empty one.
        var plain = ItemCardRenderer.Render(FullCard()).Blocks
            .Single(b => b.BlockKey == CardBlocks.Header).Lines[0];
        Assert.DoesNotContain("promotedFromKey", plain.Args.Keys);

        // Promoted: the mark names the rung the item CLIMBED FROM, as a key the client resolves
        // against the same ladder `rungKey` uses (T26, ssot-rarity.md §3.7 rule 6).
        var promoted = ItemCardRenderer.Render(FullCard() with { PromotedFromRarityKey = "rarity.cultivated" })
            .Blocks.Single(b => b.BlockKey == CardBlocks.Header).Lines[0];
        Assert.Equal("rarity.cultivated", promoted.Args["promotedFromKey"]);
        Assert.Equal("rarity.heirloom", promoted.Args["rungKey"]);   // the CURRENT rung is unchanged
    }

    [Fact]
    public void An_empty_socket_shows_as_empty_and_an_undiscovered_combination_is_never_rendered()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);
        var sockets = model.Blocks.Single(b => b.BlockKey == CardBlocks.Sockets).Lines;

        var cells = sockets.Where(l => l.Key == "item.card.socket.cell").ToList();
        Assert.Equal(3, cells.Count);
        Assert.Equal("1", cells[2].Args["empty"]);

        foreach (var combo in sockets.Where(l => l.Key == "item.card.socket.combination"))
            Assert.NotEqual("undiscovered", combo.Args["state"]);
    }

    [Fact]
    public void A_one_away_combination_names_the_exact_shortfall()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);
        var oneAway = model.Blocks.Single(b => b.BlockKey == CardBlocks.Sockets).Lines
            .Where(l => l.Key == "item.card.socket.combination" && l.Args["state"] == "one-away")
            .ToList();

        Assert.NotEmpty(oneAway);
        foreach (var line in oneAway)
        {
            Assert.True(line.Args.ContainsKey("distance"), "a one-away line with no distance");
            Assert.True(line.Args.ContainsKey("missingKeys"), "a one-away line that names nothing");
        }
    }

    [Fact]
    public void The_whole_set_ladder_renders_active_and_inactive_alike()
    {
        var input = FullCard();
        var set = input.Set!.Value;
        var block = ItemCardRenderer.Render(input).Blocks.Single(b => b.BlockKey == CardBlocks.Set);

        var thresholds = block.Lines.Where(l => l.Key == "item.card.set.threshold").ToList();
        Assert.Equal(set.Ladder.Count, thresholds.Count);
        Assert.Contains(thresholds, t => t.Args["active"] == "1");
        Assert.Contains(thresholds, t => t.Args["active"] == "0");

        // The wording rule that prevents a real lie: a threshold names its piece count, never "next".
        foreach (var t in thresholds) Assert.True(int.Parse(t.Args["pieces"]) >= 1);
        Assert.Equal(set.Count.ToString(), block.Lines[0].Args["count"]);
        Assert.Equal(set.Total.ToString(), block.Lines[0].Args["total"]);
    }

    /// <summary>
    /// P8.4 — the multi-set per-piece disclosure, on the SAME block. Module 12's own measured corpus
    /// fact is the reason this is not a nicety: 25 of the 154 shipped (role, base type) member pairs
    /// belong to more than one set, one to three, so the primary header alone can be a third of the
    /// truth. One line per peer, ordinally ordered by set id so the wire bytes are stable, and the
    /// disclosure flag travels with each peer exactly as it does on the primary row.
    /// </summary>
    [Fact]
    public void The_set_block_discloses_every_other_set_the_piece_touches()
    {
        var input = FullCard();
        var set = input.Set!.Value with
        {
            Others = new[]
            {
                new CardSetPeer("zeta", "Zeta Set", 2, 4, Redundant: false),
                new CardSetPeer("beta", "Beta Set", 1, 3, Redundant: true),
            },
        };
        var block = ItemCardRenderer.Render(input with { Set = set })
            .Blocks.Single(b => b.BlockKey == CardBlocks.Set);

        var peers = block.Lines.Where(l => l.Key == "item.card.set.other").ToList();
        Assert.Equal(2, peers.Count);
        // Ordinal by set id, never insertion order.
        Assert.Equal(new[] { "set.beta", "set.zeta" }, peers.Select(p => p.Args["nameKey"]).ToArray());
        Assert.Equal(new[] { "Beta Set", "Zeta Set" }, peers.Select(p => p.Args["name"]).ToArray());
        Assert.Equal(new[] { "1", "2" }, peers.Select(p => p.Args["count"]).ToArray());
        Assert.Equal(new[] { "3", "4" }, peers.Select(p => p.Args["total"]).ToArray());
        Assert.Equal(new[] { "1", "0" }, peers.Select(p => p.Args["redundant"]).ToArray());
        // The primary header and ladder are untouched by the peers.
        Assert.Equal(block.Lines[0].Key, "item.card.set.header");
        Assert.Single(block.Lines.Where(l => l.Key == "item.card.set.header"));
    }

    /// <summary>
    /// Was <c>Flavour_is_uniques_only</c> until the owner's 2026-09-06 decision gave sets a lore
    /// surface too (spec-item-lore.md §2.4). The invariant it asserts is the widened one: the block
    /// renders for a unique OR a set that has an authored key, and for neither when neither has one.
    /// </summary>
    [Fact]
    public void Flavour_renders_for_a_unique_or_a_set_and_for_neither_when_neither_authored_one()
    {
        var ordinary = ItemCardRenderer.Render(FullCard());
        Assert.Empty(ordinary.Blocks.Single(b => b.BlockKey == CardBlocks.Flavour).Lines);

        var unique = FullCard() with
        {
            Unique = new UniqueRow("item.kiln-nozzle", "item.humanoid-main-hand-a-001",
                UniqueCounterPressure.Narrow, 100, "offense", UniqueAcquisition.Drop,
                FlavourKey: "flavour.kiln-nozzle"),
        };
        var line = Assert.Single(ItemCardRenderer.Render(unique).Blocks
            .Single(b => b.BlockKey == CardBlocks.Flavour).Lines);
        Assert.Equal("flavour.kiln-nozzle", line.Args["flavourKey"]);
        Assert.Equal(SourceKind.UniqueIdentity, line.SourceKind);

        // The widened half: a SET with an authored flavour key renders the same block, with no
        // unique anywhere on the card.
        var baseCard = FullCard();
        Assert.NotNull(baseCard.Set);
        var setCard = baseCard with { Set = baseCard.Set!.Value with { FlavourKey = "flavor.set.copyhand" } };
        var setLine = Assert.Single(ItemCardRenderer.Render(setCard).Blocks
            .Single(b => b.BlockKey == CardBlocks.Flavour).Lines);
        Assert.Equal("flavor.set.copyhand", setLine.Args["flavourKey"]);
        Assert.Equal(SourceKind.SetThreshold, setLine.SourceKind);

        // And the negative that matters: a set with NO authored flavour renders no block at all --
        // not an empty line, not a placeholder.
        Assert.Empty(ItemCardRenderer.Render(baseCard).Blocks
            .Single(b => b.BlockKey == CardBlocks.Flavour).Lines);
    }

    /// <summary>
    /// T5 + T6: the authored sentence itself reaches the line, not the key and not a fragment of it.
    /// Driven off the real shipped corpus row and the real string catalog — a fixture sentence would
    /// prove the plumbing and not the wiring.
    /// </summary>
    [Fact]
    public void A_real_unique_with_authored_flavour_renders_its_real_sentence()
    {
        var seed = UniqueCorpus
            .Parse(File.ReadAllText(Path.Combine(Seed("items", "uniques"), "charnel-bloom-70.json")))
            .First(u => u.FlavourKey is { Length: > 0 });

        // T5: import carries the TEXT, not only the key.
        Assert.False(string.IsNullOrWhiteSpace(seed.FlavourText));

        var card = FullCard() with
        {
            Unique = new UniqueRow(seed.ContainerId, seed.BaseTypeId, UniqueCounterPressure.Narrow,
                100, seed.PowerAxis, UniqueAcquisition.Drop, FlavourKey: seed.FlavourKey),
            LookupString = ShippedStringCatalog.Value,
        };

        var line = Assert.Single(ItemCardRenderer.Render(card).Blocks
            .Single(b => b.BlockKey == CardBlocks.Flavour).Lines);

        Assert.Equal(seed.FlavourText, line.Args["__rendered"]);
        // Not the key, and not `keyTail`'s old fragment of it.
        Assert.NotEqual(seed.FlavourKey, line.Args["__rendered"]);
        Assert.Contains(" ", line.Args["__rendered"]);
    }

    /// <summary>T7's positive half, over the one shipped set family that authored flavour.</summary>
    [Fact]
    public void A_real_set_with_authored_flavour_renders_its_real_sentence()
    {
        var set = SetCorpus
            .Parse(File.ReadAllText(Path.Combine(Seed("items", "sets"), "sunwoven-almanac.json")))
            .First(s => s.FlavourKey is { Length: > 0 });

        Assert.False(string.IsNullOrWhiteSpace(set.FlavourText));

        var baseCard = FullCard();
        var card = baseCard with
        {
            Set = baseCard.Set!.Value with { FlavourKey = set.FlavourKey },
            LookupString = ShippedStringCatalog.Value,
        };

        var line = Assert.Single(ItemCardRenderer.Render(card).Blocks
            .Single(b => b.BlockKey == CardBlocks.Flavour).Lines);
        Assert.Equal(set.FlavourText, line.Args["__rendered"]);
    }

    /// <summary>
    /// The sync guard between the two halves of T6's acceptance: every authored `flavor` sentence in
    /// the unique and set corpora has a real row in `gk-content/content/display/en.json` carrying that exact
    /// sentence. Authoring a new flavour line without a catalog row turns this red instead of
    /// silently resolving to nothing on a player's card.
    /// </summary>
    [Fact]
    public void Every_authored_unique_and_set_flavour_sentence_has_its_string_catalog_row()
    {
        var catalog = ShippedStringCatalog.Value;
        var missing = new List<string>();

        foreach (var (key, text) in AuthoredFlavour())
            if (catalog(key) != text)
                missing.Add(key);

        Assert.Empty(missing);
    }

    /// <summary>Every authored (`flavorKey`, `flavor`) pair in the unique and set corpora.</summary>
    static IEnumerable<(string Key, string Text)> AuthoredFlavour()
    {
        foreach (var file in Directory.EnumerateFiles(Seed("items", "uniques"), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
            foreach (var u in UniqueCorpus.Parse(File.ReadAllText(file)))
                if (u.FlavourKey is { Length: > 0 } k && u.FlavourText is { Length: > 0 } t)
                    yield return (k, t);

        foreach (var file in Directory.EnumerateFiles(Seed("items", "sets"), "*.json")
                     .Where(f => !Path.GetFileName(f).EndsWith(".ledger.json", StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
            foreach (var s in SetCorpus.Parse(File.ReadAllText(file)))
                if (s.FlavourKey is { Length: > 0 } k && s.FlavourText is { Length: > 0 } t)
                    yield return (k, t);
    }

    /// <summary>`gk-content/content/display/en.json`, as the lookup the card takes — the same file the Server
    /// loads and the same one <c>DisplayCheck</c> validates keys against.</summary>
    static readonly Lazy<Func<string, string?>> ShippedStringCatalog = new(() =>
        DisplayStringCatalog.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "content", "display", "en.json"))));

    [Fact]
    public void A_unique_distinguishes_its_identity_lines_from_its_variance_line()
    {
        // §4.4's exact rule: on a unique, Fixed core atoms are `unique-identity`, the OnInstantiate one
        // is `unique-variance` and is the only line on that card with a bar.
        var baseType = PickBaseType();
        var seed = MintContainer(baseType, prefixRolls: 0, suffixRolls: 0, rarityId: "firstseed");
        var coreFamilies = seed.Atoms
            .Select(a => LookupAtom(a.AtomId)!.FamilyId)
            .ToHashSet(StringComparer.Ordinal);

        // Three fixed-core atoms past seq 1 -- what a hand-authored unique looks like. One per family,
        // so the group-exclusion rule the container validator enforces is honoured.
        var extra = RealAtoms.Value
            .Where(a => LookupTemplate(a.FamilyId) is { Status: "live" })
            .Where(a => !coreFamilies.Contains(a.FamilyId))
            .GroupBy(a => a.FamilyId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(g => g.OrderBy(a => a.AtomId, StringComparer.Ordinal).First())
            .ToList();

        var uniqueContainer = MintContainer(baseType, 0, 0, "firstseed", extra);
        var instance = Mint(uniqueContainer, 0xBEEF);

        var input = new ItemCardInput(
            instance, uniqueContainer, LookupAtom, LookupTemplate,
            new CardBaseType(baseType.NameKey, "class." + baseType.ClassId, baseType.Frame,
                "role." + baseType.Role, baseType.FlavourKey),
            new CardRarity("rarity.firstseed", 8, "#d9c27a"),
            "Kiln Nozzle")
        {
            Unique = new UniqueRow("item.kiln-nozzle", baseType.Id, UniqueCounterPressure.Narrow,
                100, "offense", UniqueAcquisition.Drop, FlavourKey: "flavour.kiln-nozzle"),
        };

        var affixes = ItemCardRenderer.Render(input).Blocks
            .Single(b => b.BlockKey == CardBlocks.Affixes).Lines;

        Assert.Equal(3, affixes.Count);
        Assert.All(affixes, l => Assert.True(
            l.SourceKind is SourceKind.UniqueIdentity or SourceKind.UniqueVariance,
            $"a unique's fixed core rendered as {l.SourceKind}"));
        Assert.All(affixes.Where(l => l.SourceKind == SourceKind.UniqueIdentity), l => Assert.Null(l.RollBar));
    }

    [Fact]
    public void The_footer_mean_roll_quality_is_computed_from_the_same_bars_above_it()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);
        var footer = model.Blocks.Single(b => b.BlockKey == CardBlocks.Footer).Lines[0];

        Assert.Equal("1", footer.Args["locked"]);
        Assert.Equal("0", footer.Args["stale"]);

        var bars = model.Blocks.Single(b => b.BlockKey == CardBlocks.Affixes).Lines
            .Where(l => l.RollBar is not null)
            .Select(l => (long)l.RollQualityPerMille!.Value)
            .ToList();

        if (bars.Count == 0) { Assert.Equal("", footer.Args["meanRollQuality"]); return; }

        var expected = ItemDisplayRenderer.FormatPerMille(
            checked((int)ItemDisplayRenderer.RoundAwayFromZero(bars.Sum(), bars.Count)));
        Assert.Equal(expected, footer.Args["meanRollQuality"]);
    }

    [Fact]
    public void The_requirement_block_names_which_number_gates()
    {
        var block = ItemCardRenderer.Render(FullCard()).Blocks
            .Single(b => b.BlockKey == CardBlocks.Requirements);

        var attr = block.Lines.Single(l => l.Key == "item.card.requirement.attribute");
        // I11's `Sinew 32 (29 + 3) -- 29 gates`: the unassisted half is its own field, so the card can
        // say WHICH number gates rather than only that something does.
        Assert.Equal("32", attr.Args["need"]);
        Assert.Equal("29", attr.Args["unassisted"]);
        Assert.Equal("3", attr.Args["bonus"]);
        Assert.Equal("1", attr.Args["gates"]);
    }

    [Fact]
    public void A_refused_equip_renders_its_reason_and_its_remedy()
    {
        var gate = new EquipGate();
        var refusal = gate.Explain(ItemRole.ArmamentPrimary,
            new SpecimenActor("spec-1", null, Level: 3, null), itemFrame: null, levelReq: 20, factionReq: null);
        Assert.NotNull(refusal);

        var block = ItemCardRenderer.Render(FullCard() with { Refusal = refusal, SpecimenLevel = 3 })
            .Blocks.Single(b => b.BlockKey == CardBlocks.Requirements);

        var line = block.Lines.Single(l => l.Key == "item.card.requirement.refused");
        Assert.StartsWith("item.equip.refusal.", line.Args["reasonKey"]);
        Assert.False(string.IsNullOrWhiteSpace(line.Args["remedy"]));
        Assert.Equal("0", block.Lines.Single(l => l.Key == "item.card.requirement.level").Args["met"]);
    }

    // ---- §2.4's never-shown list, as a test rather than a convention -----------------------------------

    [Fact]
    public void Nothing_the_spec_forbids_reaches_a_rendered_arg()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);

        var forbidden = new List<string> { input.Container.ContainerId, input.Instance.InstanceId };
        forbidden.AddRange(input.Instance.Atoms.Select(a => a.AtomId));
        forbidden.AddRange(input.Instance.Atoms
            .Select(a => LookupAtom(a.AtomId)!.FamilyId));
        forbidden.AddRange(input.Instance.Atoms
            .Select(a => LookupTemplate(LookupAtom(a.AtomId)!.FamilyId)!.Value.GroupId));
        forbidden.RemoveAll(string.IsNullOrEmpty);

        foreach (var line in model.Lines)
            foreach (var (key, value) in line.Args)
                foreach (var banned in forbidden)
                    Assert.False(value.Contains(banned, StringComparison.Ordinal),
                        $"line '{line.Key}' arg '{key}' leaks '{banned}'");
    }

    [Fact]
    public void Tier_numbers_never_appear_in_any_line()
    {
        var input = FullCard();
        var model = ItemCardRenderer.Render(input);

        // Every atom on this item carries a tier; none of them may be an arg, and no arg key may
        // even be called one. (A magnitude that happens to equal a tier number is not a tier -- the
        // check is structural, over the arg NAMES and over the rendered sentence's own tier tokens.)
        foreach (var line in model.Lines)
        {
            Assert.DoesNotContain(line.Args.Keys, k => k.Contains("tier", StringComparison.OrdinalIgnoreCase));
            if (line.Args.TryGetValue("__rendered", out var rendered))
                Assert.DoesNotContain("T" + input.Instance.Atoms[0].Seq, rendered, StringComparison.Ordinal);
        }
    }

    // ---- determinism: spec-item-card.md:302 ------------------------------------------------------------

    [Fact]
    public void Display_model_is_byte_identical_for_one_seed()
    {
        // Same (container_id, catalog_revision, roll_seed) => the same instance and the same card,
        // byte for byte, over two INDEPENDENT instantiations rather than one instance rendered twice.
        var baseType = PickBaseType();
        var container = MintContainer(baseType, 4, 0, "heirloom");

        var a = Render(Mint(container, 0x5EED));
        var b = Render(Mint(container, 0x5EED));

        Assert.Equal(a, b);

        // ...and a different seed really does produce a different card, or the assertion above would
        // pass for a renderer that ignored the instance entirely.
        Assert.NotEqual(a, Render(Mint(container, 0x5EEE)));

        string Render(InstanceRow instance) => ItemCardRenderer.Render(new ItemCardInput(
            instance, container, LookupAtom, LookupTemplate,
            new CardBaseType(baseType.NameKey, "class." + baseType.ClassId, baseType.Frame,
                "role." + baseType.Role, baseType.FlavourKey),
            new CardRarity("rarity.heirloom", 7, "#8bd3c7"),
            "Honed Hatchet")
        { ItemLevel = 24 }).Fingerprint();
    }

    [Fact]
    public void The_fingerprint_does_not_depend_on_arg_insertion_order()
    {
        // The determinism claim must survive a dictionary that enumerates differently -- otherwise it
        // is a claim about Dictionary<K,V>, not about the model.
        var forward = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" };
        var backward = new Dictionary<string, string>(StringComparer.Ordinal) { ["b"] = "2", ["a"] = "1" };

        var one = new DisplayModel(new[] { new DisplayBlock("k", new[] { new DisplayLine("l", forward, null, null, 0) }) });
        var two = new DisplayModel(new[] { new DisplayBlock("k", new[] { new DisplayLine("l", backward, null, null, 0) }) });

        Assert.Equal(one.Fingerprint(), two.Fingerprint());
    }

    // ================================================================================================
    //  The Compare level
    // ================================================================================================

    static (ItemCardInput Input, DisplayModel Model) Card(long seed, int prefixRolls = 4, int suffixRolls = 0)
    {
        var baseType = PickBaseType();
        var container = MintContainer(baseType, prefixRolls, suffixRolls, "heirloom");
        var instance = Mint(container, seed);
        var input = new ItemCardInput(
            instance, container, LookupAtom, LookupTemplate,
            new CardBaseType(baseType.NameKey, "class." + baseType.ClassId, baseType.Frame,
                "role." + baseType.Role, baseType.FlavourKey),
            new CardRarity("rarity.heirloom", 7, "#8bd3c7"),
            "Honed Hatchet")
        { ItemLevel = 24 };
        return (input, ItemCardRenderer.Render(input));
    }

    [Fact]
    public void Two_real_cards_diff_to_the_lines_that_actually_differ()
    {
        var left = Card(0xA1);
        var right = Card(0xB2);

        var model = ItemCardCompare.Compare(
            left.Model, right.Model,
            ItemCardCompare.AtomsOf(left.Input.Instance, LookupAtom),
            ItemCardCompare.AtomsOf(right.Input.Instance, LookupAtom));

        Assert.NotEmpty(model.DifferingLineIndexes);

        var a = left.Model.Lines.ToList();
        var b = right.Model.Lines.ToList();
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var same = a[i].Fingerprintable() == b[i].Fingerprintable();
            Assert.Equal(!same, model.DifferingLineIndexes.Contains(i));
        }
    }

    [Fact]
    public void An_item_compared_with_itself_differs_nowhere()
    {
        var card = Card(0xA1);
        var atoms = ItemCardCompare.AtomsOf(card.Input.Instance, LookupAtom);
        var model = ItemCardCompare.Compare(card.Model, card.Model, atoms, atoms);

        Assert.Empty(model.DifferingLineIndexes);
        Assert.Empty(model.Deltas.Where(d => d.Delta != 0));
    }

    [Fact]
    public void The_compare_model_carries_the_verdict_as_a_word_and_a_shape_never_a_colour()
    {
        var left = Card(0xA1);
        var right = Card(0xB2);
        var model = ItemCardCompare.Compare(
            left.Model, right.Model,
            ItemCardCompare.AtomsOf(left.Input.Instance, LookupAtom),
            ItemCardCompare.AtomsOf(right.Input.Instance, LookupAtom));

        Assert.Equal(DominancePresentation.Badge(model.Dominance), model.Badge);
        Assert.False(string.IsNullOrEmpty(model.Badge.LabelKey));
        Assert.False(string.IsNullOrEmpty(model.Badge.Shape));
        // The persistent footnote is always there, and there is no API to hide it.
        Assert.Equal(DominancePresentation.NoSingleScoreFootnoteKey, model.FootnoteKey);
    }

    [Fact]
    public void No_comparison_column_mixes_two_unit_classes()
    {
        var left = Card(0xA1);
        var right = Card(0xB2);
        var model = ItemCardCompare.Compare(
            left.Model, right.Model,
            ItemCardCompare.AtomsOf(left.Input.Instance, LookupAtom),
            ItemCardCompare.AtomsOf(right.Input.Instance, LookupAtom));

        // SC4 as a layout invariant: every group holds exactly one unit class, and the unit is on the
        // group, never in the column.
        Assert.Equal(model.UnitGroups.Select(g => g.Unit).Count(),
            model.UnitGroups.Select(g => g.Unit).Distinct().Count());
        Assert.Equal(model.Deltas.Count, model.UnitGroups.Sum(g => g.Deltas.Count));
    }

    [Fact]
    public void An_incomparable_verdict_always_carries_its_reason()
    {
        // Two items whose channel sets are disjoint -- the honest "nothing to weigh" case.
        var left = Card(0xA1);
        var right = Card(0xB2);
        var leftAtoms = ItemCardCompare.AtomsOf(left.Input.Instance, LookupAtom);

        var model = ItemCardCompare.Compare(left.Model, right.Model, leftAtoms, Array.Empty<CompareAtom>());
        Assert.Null(model.IncomparableReasonKey);   // one side empty is not "incomparable", it is a loss

        var forced = ItemCardCompare.Compare(left.Model, right.Model,
            leftAtoms.Where(a => Channel(a) == "maxHp").ToList(),
            leftAtoms.Where(a => Channel(a) == "atk").ToList());

        if (forced.Dominance == DominanceVerdict.Incomparable)
            Assert.Equal(DominancePresentation.IncomparableReasonKey, forced.IncomparableReasonKey);

        static string Channel(CompareAtom a)
        {
            using var doc = JsonDocument.Parse(a.ValuesJson);
            return doc.RootElement.TryGetProperty("channel", out var c) ? c.GetString() ?? "" : "";
        }
    }

    [Fact]
    public void A_longer_card_reports_its_extra_lines_as_differences()
    {
        var rich = Card(0xA1, prefixRolls: 4, suffixRolls: 0);
        var plain = Card(0xA1, prefixRolls: 0, suffixRolls: 0);

        var model = ItemCardCompare.Compare(
            plain.Model, rich.Model,
            ItemCardCompare.AtomsOf(plain.Input.Instance, LookupAtom),
            ItemCardCompare.AtomsOf(rich.Input.Instance, LookupAtom));

        var shorter = plain.Model.Lines.Count();
        var longer = rich.Model.Lines.Count();
        Assert.True(longer > shorter);
        for (var i = shorter; i < longer; i++)
            Assert.Contains(i, model.DifferingLineIndexes);
    }

    // ================================================================================================
    //  The whole-catalog guard — over LIVE AtomRows, not just the 98 template rows
    // ================================================================================================

    /// <summary>
    /// ⭐ **`every_atom_in_the_catalog_renders` — the spec's own "test that stops half the items
    /// reading as raw ids", now over real atoms rather than over templates alone.**
    ///
    /// <para>P2.5 deferred this because it *"cannot yet iterate live `AtomRow`s at Min/mid/Max drawn
    /// from a real container"*. It can now: `FamilyExpansion` produces them from the real corpus, and
    /// every one is rendered at its authored <c>Min</c>, its midpoint and its <c>Max</c> — no raw id,
    /// no unresolved <c>{placeholder}</c>, no empty string, on BOTH frames.</para>
    ///
    /// <para>An element-typed family's template names <c>{element}</c> while the generated atom's
    /// <c>Variant</c> is deliberately empty (W7.9: "element does not materialise" in the atom id). At
    /// runtime the concrete element arrives from the channel POOL draw, which is
    /// <c>Resolver</c>/<c>InstanceProducer.Compose</c>'s job rather than the atom row's — so the guard
    /// supplies one real element, exactly as the resolved instance would.</para>
    ///
    /// <para><b>Scope (widened, item-content T13).</b> <see cref="RealAtoms"/> now reads the HIGHEST
    /// published <c>tier-bands.v{n}.json</c> rather than a hard-coded <c>v1</c>, so the guard walks
    /// whatever the shipped tuning admits today instead of the nine families v1 admitted. The
    /// former quarantine skip for <c>atom.elpw-*</c> is gone: T12 gave both of their channel families
    /// a real <see cref="UnitClass"/>, so nothing here is excluded by name any more. The floor
    /// assertion below is DERIVED from the corpus, not a literal — a literal would go stale the next
    /// time a family is authored, and a stale floor is how a guard starts passing vacuously.</para>
    /// </summary>
    [Fact]
    public void Every_real_atom_renders_at_min_mid_and_max_with_no_raw_id()
    {
        // Nothing is skipped by name. A family reaches this guard iff the shipped tuning admits it
        // AND it has a live template — both facts read off the corpus, neither hand-listed.
        var expectedFamilies = RealAtoms.Value
            .Select(a => a.FamilyId)
            .Distinct(StringComparer.Ordinal)
            .Where(f => LookupTemplate(f) is { Status: "live" })
            .ToHashSet(StringComparer.Ordinal);
        var exercisedFamilies = new HashSet<string>(StringComparer.Ordinal);

        var rendered = 0;
        foreach (var atom in RealAtoms.Value)
        {
            if (LookupTemplate(atom.FamilyId) is not { Status: "live" } template) continue;
            exercisedFamilies.Add(atom.FamilyId);

            using var doc = JsonDocument.Parse(atom.ParamsJson);
            var hasChannel = doc.RootElement.TryGetProperty("channel", out var c);

            // A pooled channel is an OBJECT, not a string -- `FamilyExpansion` emits
            // `{pool, count, allowRepeat}` for every {variant}-templated family (W7.9). At runtime
            // `Resolver`/`InstanceProducer.Compose` draws one CONCRETE member out of
            // `pools.v1.json` and stamps it into values_json, and the card reads that; the atom row
            // alone never carries it. So the guard resolves the pool the same way Compose would --
            // off the real pool file, whose members are all one channel family, so which member is
            // read cannot change the unit (ChannelUnits' own probe rule). Reachable at all only
            // since tier-bands v2 (item-content T10) admitted the first element-typed families.
            string? channel = null;
            if (hasChannel && c.ValueKind == JsonValueKind.String)
                channel = c.GetString();
            else if (hasChannel && c.ValueKind == JsonValueKind.Object
                     && c.TryGetProperty("pool", out var poolIdEl)
                     && poolIdEl.GetString() is { } poolId)
            {
                var pool = LookupPool(poolId);
                Assert.True(pool is not null, $"{atom.AtomId} names pool '{poolId}', which pools.v1.json does not define");
                Assert.NotEmpty(pool!.Members);
                channel = pool.Members[0].Channel;
            }

            var unit = channel is null ? null : ChannelUnits.ForAuthoredChannel(channel);

            long min = 0, max = 0;
            if (doc.RootElement.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Object)
            {
                min = amt.GetProperty("min").GetInt64();
                max = amt.GetProperty("max").GetInt64();
            }

            foreach (var value in new[] { min, min + (max - min) / 2, max })
                foreach (var frame in new[] { "humanoid", "plant" })
                {
                    var line = ItemDisplayRenderer.Line(
                        template, atom, frame, value, SourceKind.AffixPrefix, 0, unit,
                        elementVariant: "fire", roll: RollPolicy.OnApply, bandMax: max);

                    var text = line.Args["__rendered"];
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{atom.AtomId} rendered nothing");
                    Assert.DoesNotContain('{', text);
                    Assert.DoesNotContain('}', text);
                    Assert.DoesNotContain(atom.AtomId, text, StringComparison.Ordinal);
                    Assert.DoesNotContain(atom.FamilyId, text, StringComparison.Ordinal);
                    rendered++;
                }
        }

        // Every family the corpus admits was actually walked -- not "most of them", and not a count
        // that a silently-skipped family could still satisfy.
        Assert.Equal(
            expectedFamilies.OrderBy(f => f, StringComparer.Ordinal).ToList(),
            exercisedFamilies.OrderBy(f => f, StringComparer.Ordinal).ToList());

        // A guard that iterated nothing would pass forever. Derived, not literal: five tiers per
        // family (FamilyExpansion.TierCount), three values (min/mid/max) on two frames.
        const int ValuesPerAtom = 3;
        const int Frames = 2;
        Assert.Equal(expectedFamilies.Count * FamilyExpansion.TierCount * ValuesPerAtom * Frames, rendered);

        // ...and the scope is the WIDENED one. tier-bands.v1 admitted 9 families through
        // FamilyExpansion's full gate chain (45 atoms); item-content T10 published v2 with a weight
        // for all 109 authored families. If this drops back to v1's nine, the guard has silently
        // renarrowed and its own doc comment is lying again.
        Assert.True(expectedFamilies.Count > 9,
            $"only {expectedFamilies.Count} families exercised -- the tuning file admits fewer than "
            + "tier-bands.v1 already did, so RealAtoms is reading the wrong version");
    }

    /// <summary>
    /// ⭐ <b>Proof that widening the guard above was not cosmetic (item-content T13).</b> A guard whose
    /// scope grew but whose reachable set did not would pass exactly as before, and its green would
    /// mean exactly as little — so the two scopes are built side by side from the two tunings on disk
    /// and the difference is asserted, family by family.
    ///
    /// <para>The <c>tier-bands.v1</c> scope is not simulated: it is the real v1 file, still shipped,
    /// through the real generator. Every family in the difference set is one that the widened guard
    /// renders at min/mid/max on both frames and the old scope never touched at all — so a broken
    /// template on any of them was, until now, invisible to the suite.</para>
    /// </summary>
    [Fact]
    public void The_widened_guard_reaches_families_the_v1_scoped_one_could_not()
    {
        var v1 = TierBandsFile.Read(
            File.ReadAllText(Path.Combine(Seed("items"), "_tuning", "tier-bands.v1.json")));

        HashSet<string> RenderableFamilies(IReadOnlyList<AtomRow> atoms) => atoms
            .Select(a => a.FamilyId)
            .Where(f => LookupTemplate(f) is { Status: "live" })
            .ToHashSet(StringComparer.Ordinal);

        var oldScope = RenderableFamilies(
            FamilyExpansion.Expand(RealFamilies.Value, v1, FlatReferenceBase).Rows);
        var newScope = RenderableFamilies(RealAtoms.Value);

        // Strictly larger, and strictly a superset -- nothing the old scope covered was lost.
        Assert.ProperSubset(newScope, oldScope);

        var gained = newScope.Except(oldScope, StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(gained);

        // The specific thing §6.3 said was impossible: no ELEMENT-typed family was reachable at all
        // before, because every one of them sat behind the tuning-file gate. Named by shape, read off
        // the atoms, so this cannot pass on a coincidence of counts.
        var pooledNow = RealAtoms.Value
            .Where(a => a.ParamsJson.Contains("\"pool\"", StringComparison.Ordinal))
            .Select(a => a.FamilyId).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(pooledNow);
        Assert.All(pooledNow, f => Assert.DoesNotContain(f, oldScope));
    }

    // ================================================================================================
    //  InstanceProducer.Compose — the OTHER minter, and the pooled channel only it resolves
    // ================================================================================================

    /// <summary>E30's shipped catalog, read from the real file. Without it a pooled-channel atom
    /// cannot resolve at all — <c>Resolver.RollValues</c> throws rather than freezing the pool object
    /// unread.</summary>
    static readonly Lazy<IReadOnlyDictionary<string, ChannelPoolRow>> Pools = new(() =>
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "channel-pools", "pools.v1.json"));
        var read = ChannelPoolFile.TryParse(json, out var rows);
        Assert.True(read.IsOk, read.ToString());
        return rows.ToDictionary(p => p.PoolId, StringComparer.Ordinal);
    });

    static ChannelPoolRow? LookupPool(string poolId) =>
        Pools.Value.TryGetValue(poolId, out var p) ? p : null;

    /// <summary>The real vocabulary <c>Resolver</c> step 1 substitutes a slot from — <c>ElementRoster</c>
    /// itself, "the one legal way to enumerate elements", never a hand-typed list. Throws on an
    /// unknown domain rather than returning empty, because an empty member list would make the
    /// resolver's own pick index out of range and hide the real cause.</summary>
    static IReadOnlyList<string> DomainMembers(string domain) => domain switch
    {
        "element" => ElementRoster.Concrete.Select(e => e.ToElementId()).ToList(),
        _ => throw new InvalidOperationException($"no member list for slot domain '{domain}'"),
    };

    /// <summary>
    /// ⛔ <b>The tuning-file half of this gap is CLOSED (item-content T10, 2026-09-06) — what remains
    /// is named, not papered over.</b>
    ///
    /// <para><b>What it used to say, and what actually happened.</b> <c>tier-bands.v1.json</c> authored
    /// a <c>channelWeightPermille</c> row for <b>14</b> of the corpus's <b>109</b> families, so
    /// <c>FamilyExpansion</c> refused <b>95</b> at its first gate (<c>FamilyExpansion.cs:121-125</c>,
    /// <i>"no authored sharePermille for family '…'"</i>) and every element-typed family was among
    /// them. <c>tier-bands.v2.json</c> (published through the file's own
    /// <c>python -m seedsmith numerics rebalance --publish</c>, never hand-edited) authors all 109 at
    /// the same <b>1000‰</b> the shipped 14 already carried, so that first gate now refuses
    /// <b>zero</b> and <see cref="RealAtoms"/> reads it.</para>
    ///
    /// <para><b>Why this fixture still exists.</b> Nine families were admitted through the FULL gate
    /// chain under v1; twenty-five are under v2. The other 84 are refused DOWNSTREAM of the tuning
    /// file, at three gates the tuning file cannot reach and this fixture cannot fake either:
    /// <b>40</b> author no <c>op</c> at all (trigger/status families — <c>FamilyExpansion</c>'s
    /// primaryChannel path has no tier-magnitude formula for them, <c>:258</c>); <b>22</b> name an
    /// <c>op</c> outside <c>opWeightPermille</c>'s three (<c>add</c>/<c>Replace</c>/<c>Flag</c>, plus
    /// four <c>board.action</c> verbs the parser reads into the same field — see the todo's own
    /// defect note); <b>19</b> name a channel with no shipped <c>BattleRuleset</c> curve
    /// (<c>arm1Max</c>, <c>status.power</c>, the <c>{variant}</c> Flat families); <b>3</b> are the
    /// <c>elpw-*</c> pool quarantine <c>FamilyExpansion.cs:44-47</c> names by id.</para>
    ///
    /// <para>So the fixture no longer needs to supply a weight — v2 supplies them all — but the
    /// <see cref="ExtendedAtoms"/> path is kept because it is what the pooled-channel tests below
    /// draw from, and because it reads the shipped tuning directly rather than through the
    /// admitted-set filter. <see cref="ShippedChannelWeightPermille"/> is now the weight it asserts
    /// the corpus already carries rather than one it invents.</para>
    /// </summary>
    const long ShippedChannelWeightPermille = 1000;

    static readonly Lazy<IReadOnlyList<FamilyEntryInput>> RealFamilies = new(() =>
    {
        var families = new List<FamilyEntryInput>();
        foreach (var file in Directory.GetFiles(Seed("items", "affix-families"), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            families.AddRange(AffixFamilyFile.Read(Path.GetFileName(file), File.ReadAllText(file)));
        }
        return families;
    });

    static readonly Lazy<IReadOnlyList<AtomRow>> ExtendedAtoms = new(() =>
    {
        var shipped = TierBandsFile.Read(File.ReadAllText(LatestTierBandsPath()));

        var weights = new Dictionary<string, long>(shipped.ChannelWeightPermille, StringComparer.Ordinal);
        foreach (var f in RealFamilies.Value)
        {
            var stem = f.Id.StartsWith("atom.", StringComparison.Ordinal) ? f.Id["atom.".Length..] : f.Id;
            // Since tier-bands v2 the shipped file already carries every one of these; the loop is
            // kept as the fixture's own safety net for a family authored ahead of its weight row.
            if (!weights.ContainsKey(stem)) weights[stem] = ShippedChannelWeightPermille;
        }

        return FamilyExpansion
            .Expand(RealFamilies.Value, new TierBandsInput(
                shipped.BaseSharePermille, weights, shipped.OpWeightPermille), FlatReferenceBase)
            .Rows;
    });

    static readonly Lazy<IReadOnlyDictionary<string, AtomRow>> ExtendedById = new(() =>
        ExtendedAtoms.Value.ToDictionary(a => a.AtomId, StringComparer.Ordinal));

    static readonly Lazy<IReadOnlyDictionary<string, AffixRow>> ExtendedAffixes = new(() =>
        AffixLibraryGenerator.Generate(ExtendedAtoms.Value).ToDictionary(a => a.AffixId, StringComparer.Ordinal));

    static AtomRow? LookupExtendedAtom(string id) =>
        ExtendedById.Value.TryGetValue(id, out var a) ? a : null;

    static AffixRow? LookupExtendedAffix(string id) =>
        ExtendedAffixes.Value.TryGetValue(id, out var a) ? a : null;

    /// <summary>A container whose whole pool is ONE pooled-channel affix, so the draw is not a
    /// coin toss: the seed decides the magnitude and the pool draw decides the element, and both are
    /// asserted.</summary>
    static (ContainerRow Container, AtomRow Pooled, RealBaseType BaseType) PooledContainer()
    {
        var baseType = PickBaseType();
        var baseStat = ExtendedAtoms.Value
            .Where(a => a.ParamsJson.Contains("\"maxHp\"", StringComparison.Ordinal))
            .OrderByDescending(a => a.Tier)
            .First();
        var implicitAtom = ExtendedById.Value[AtomRow.DeriveId(baseType.ImplicitFamily!, "", 1)];
        var core = new[] { baseStat, implicitAtom };
        var coreFamilies = core.Select(a => a.FamilyId).ToHashSet(StringComparer.Ordinal);

        // The first real expanded atom whose channel is an E30 POOL reference and whose family the
        // display corpus renders live -- chosen from content, so an authoring change moves the fixture
        // instead of breaking it.
        var pooled = ExtendedAtoms.Value
            .Where(a => !coreFamilies.Contains(a.FamilyId))
            .Where(a => a.ParamsJson.Contains("\"pool\"", StringComparison.Ordinal))
            .First(a => LookupTemplate(a.FamilyId) is { Status: "live" });

        var affix = ExtendedAffixes.Value.Values.First(a =>
            a.Refs.Count == 1 && string.Equals(a.Refs[0].AtomId, pooled.AtomId, StringComparison.Ordinal));

        return (new ContainerRow
        {
            ContainerId = baseType.Id,
            Kind = ContainerKind.Item,
            Slot = baseType.Role,
            Rarity = "heirloom",
            LevelReq = 20,
            PrefixRolls = 1,
            SuffixRolls = 0,
            Atoms = core.Select((a, i) => new ContainerAtomRow(i, a.AtomId)).ToList(),
            Pool = new[] { new ContainerPoolRow(affix.AffixId, 100) },
        }, pooled, baseType);
    }

    static ItemCardInput PooledCard(InstanceRow instance, ContainerRow container, RealBaseType baseType) =>
        new(instance, container, LookupExtendedAtom, LookupTemplate,
            new CardBaseType(baseType.NameKey, "class." + baseType.ClassId, baseType.Frame,
                "role." + baseType.Role, baseType.FlavourKey),
            new CardRarity("rarity.heirloom", 7, "#8bd3c7"),
            ItemName: "Pooled Fixture")
        {
            ItemLevel = 24,
            LevelReq = container.LevelReq,
            SpecimenLevel = 30,
        };

    /// <summary>
    /// ⭐ <b>item-content T10's before/after, pinned in the suite rather than measured once and
    /// written down.</b> Both tunings are read from disk and run through the real generator, so the
    /// claim "the tuning-file gate now refuses nothing" is re-proved on every run instead of trusted.
    ///
    /// <para><c>tier-bands.v1.json</c> — still on disk, kept for revert exactly as its own
    /// <c>_meta.rebalance</c> line promises — authors a share for 14 of the corpus's 109 families and
    /// refuses the other <b>95</b> at <c>FamilyExpansion.cs:121-125</c>. The latest published version
    /// refuses <b>zero</b> there. Asserted as a FAITHFULNESS check on v1 (the refused set must be
    /// exactly "the families v1 has no stem for") so the historical half cannot drift into a bare
    /// number, and as an emptiness check on the shipped half.</para>
    ///
    /// <para>The consequence, and the reason this mattered: <b>no element-typed family survived
    /// under v1</b>, so no drop could ever roll a pooled channel. Seven do under the shipped tuning
    /// (the <c>Increased</c> halves of dodge / crit-resist / crit-resist-damage / shield-capacity /
    /// shield-regen / shield-pen / elemental-defense), which is asserted below.</para>
    /// </summary>
    [Fact]
    public void The_shipped_tuning_authors_a_share_for_every_family_and_v1_did_not()
    {
        var v1 = TierBandsFile.Read(
            File.ReadAllText(Path.Combine(Seed("items"), "_tuning", "tier-bands.v1.json")));
        var shipped = TierBandsFile.Read(File.ReadAllText(LatestTierBandsPath()));

        static HashSet<string> UnsharedUnder(TierBandsInput tuning, IReadOnlyList<FamilyEntryInput> families) =>
            families
                .Where(f => !tuning.ChannelWeightPermille.ContainsKey(
                    f.Id.StartsWith("atom.", StringComparison.Ordinal) ? f.Id["atom.".Length..] : f.Id))
                .Select(f => f.Id)
                .ToHashSet(StringComparer.Ordinal);

        HashSet<string> RefusedForNoShare(TierBandsInput tuning) => FamilyExpansion
            .Expand(RealFamilies.Value, tuning, FlatReferenceBase).Refusals
            .Where(r => r.Reason.Contains("no authored sharePermille", StringComparison.Ordinal))
            .Select(r => r.FamilyId)
            .ToHashSet(StringComparer.Ordinal);

        // BEFORE — v1, faithfully: exactly the families it has no stem for. Both the family total and
        // the unshared total are corpus-derived and move every generation, so they are asserted
        // RELATIVELY (v1 shares fewer than the shipped tuning; the shipped tuning shares all) rather
        // than as literals. The v1 file is frozen history, so the property that matters is v1 leaves a
        // strict, non-empty subset unshared while the shipped tuning leaves none.
        var unsharedV1 = UnsharedUnder(v1, RealFamilies.Value);
        Assert.Equal(unsharedV1, RefusedForNoShare(v1));
        Assert.NotEmpty(unsharedV1);
        Assert.True(unsharedV1.Count < RealFamilies.Value.Count,
            "the frozen v1 tuning must leave at least one family unshared");

        // AFTER — the shipped tuning: nothing left at that gate at all.
        Assert.Empty(UnsharedUnder(shipped, RealFamilies.Value));
        Assert.Empty(RefusedForNoShare(shipped));

        // And the consequence: pooled-channel atoms exist in the SHIPPED expansion now, not only in
        // the fixture's own extended one.
        Assert.Contains(RealAtoms.Value, a => a.ParamsJson.Contains("\"pool\"", StringComparison.Ordinal));
        Assert.Contains(ExtendedAtoms.Value, a => a.ParamsJson.Contains("\"pool\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⭐ <b>The <c>InstanceProducer.Compose</c> half of the card, which P2.5 named as not done.</b>
    ///
    /// <para>A <c>{variant}</c>-templated family expands into ONE atom per tier carrying an E30 pool
    /// reference, with the variant deliberately empty (W7.9). <c>Instantiator.Freeze</c> copies that
    /// pool object into <c>values_json</c> untouched, so an item minted through
    /// <c>Instantiator.TryInstantiate</c> has no element to name — proved by the negative control
    /// below. <c>InstanceProducer.Compose</c> goes through <c>Resolver</c>, whose fifth named stream
    /// draws a CONCRETE channel from <c>pools.v1.json</c> and stamps it into <c>values_json</c>; the
    /// card reads that and renders <c>+12 fire penetration</c> rather than refusing.</para>
    ///
    /// <para>Every element assertion is checked against the pool's own member list, so a wrong element
    /// cannot pass by looking element-shaped.</para>
    /// </summary>
    [Fact]
    public void A_compose_minted_pooled_channel_affix_renders_its_concrete_element()
    {
        var (container, pooled, baseType) = PooledContainer();

        var ok = InstanceProducer.Compose(
            container, LookupExtendedAtom, LookupExtendedAffix, DomainMembers,
            rollSeed: 0xE1E3E7, thetaContent: PinTheta,
            tuning: Tuning, out var instance, catalogRevision: 7, lookupPool: LookupPool);
        Assert.True(ok.IsOk, ok.ToString());
        Assert.NotNull(instance);

        // The pool the ATOM names, and the concrete channel the INSTANCE froze -- the second must be a
        // member of the first, or the resolver invented one.
        var poolId = PoolIdOf(pooled.ParamsJson);
        Assert.NotNull(poolId);
        var members = Pools.Value[poolId!].Members.Select(m => m.Channel).ToHashSet(StringComparer.Ordinal);

        var drawn = instance!.Atoms.Single(a => string.Equals(a.AtomId, pooled.AtomId, StringComparison.Ordinal));
        var resolvedChannel = ChannelString(drawn.ValuesJson);
        Assert.NotNull(resolvedChannel);
        Assert.Contains(resolvedChannel!, members);

        // The card renders it, and the element in the sentence is the channel's own tail.
        var model = ItemCardRenderer.Render(PooledCard(instance, container, baseType));
        var affixes = model.Blocks.Single(b => b.BlockKey == CardBlocks.Affixes);
        var line = Assert.Single(affixes.Lines);

        var expectedElement = resolvedChannel![(resolvedChannel.LastIndexOf('.') + 1)..];
        Assert.Contains(expectedElement, ElementRoster.Concrete.Select(e => e.ToElementId()));
        Assert.Equal(expectedElement, line.Args["element"]);

        var text = line.Args["__rendered"];
        Assert.Contains(expectedElement, text, StringComparison.Ordinal);
        Assert.DoesNotContain('{', text);
        Assert.DoesNotContain('}', text);
        Assert.DoesNotContain(pooled.FamilyId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(pooled.AtomId, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control, and the reason the test above is not vacuous: the SAME container minted
    /// through <c>Instantiator.TryInstantiate</c> keeps the pool object in <c>values_json</c>, so the
    /// affix has neither a unit (its channel is not a string) nor an element, and the renderer refuses
    /// by name. That is a fact about which minter the caller picked, not a renderer limit — which is
    /// exactly what P2.5's bullet said and what this pins.
    /// </summary>
    [Fact]
    public void The_same_container_minted_by_try_instantiate_still_refuses_the_unresolved_pool()
    {
        var (container, pooled, baseType) = PooledContainer();

        var r = Instantiator.TryInstantiate(
            container, LookupExtendedAtom, LookupExtendedAffix, rollSeed: 0xE1E3E7, thetaContent: PinTheta,
            tuning: Tuning, out var minted, InstanceOrigin.Drop, catalogRevision: 7);
        Assert.True(r.IsOk, r.ToString());
        var instance = minted!;

        // The pool object really did survive the freeze -- otherwise this test proves nothing.
        var drawn = instance.Atoms.Single(a => string.Equals(a.AtomId, pooled.AtomId, StringComparison.Ordinal));
        Assert.Null(ChannelString(drawn.ValuesJson));
        Assert.Contains("\"pool\"", drawn.ValuesJson, StringComparison.Ordinal);

        Assert.Throws<DisplayTemplateRejection>(
            () => ItemCardRenderer.Render(PooledCard(instance, container, baseType)));
    }

    /// <summary>A non-pooled atom's element is still its own variant, and a non-pooled channel whose
    /// last segment merely LOOKS like an element never acquires one — the gate that keeps the new arm
    /// from over-reaching.</summary>
    [Fact]
    public void A_concrete_channel_never_acquires_an_element_from_its_last_segment()
    {
        var concrete = RealAtoms.Value.First(a =>
            !a.ParamsJson.Contains("\"pool\"", StringComparison.Ordinal)
            && LookupTemplate(a.FamilyId) is { Status: "live" });

        Assert.Equal("", concrete.Variant);

        // A values_json whose channel string ends in a real element name, on an atom that authored a
        // CONCRETE channel: the card must not read "fire" out of it.
        var faked = $"{{\"channel\":\"status.duration.fire\",\"amount\":10}}";
        var model = ItemCardRenderer.Render(new ItemCardInput(
            new InstanceRow
            {
                InstanceId = "i1", ContainerId = "item.probe", RollSeed = 1, CatalogRevision = 1,
                Origin = InstanceOrigin.Drop, ThetaContent = PinTheta, ContentScaleMilli = 1000,
                Atoms = new[] { new InstanceAtomRow(0, concrete.AtomId, faked) },
            },
            new ContainerRow
            {
                ContainerId = "item.probe", Kind = ContainerKind.Item, Slot = "armament-primary",
                Atoms = new[] { new ContainerAtomRow(0, concrete.AtomId) },
            },
            LookupAtom, LookupTemplate,
            new CardBaseType("bt.probe", "class.probe", "humanoid", "role.armament-primary", null),
            new CardRarity("rarity.chaff", 1, "#63645d"), "Probe"));

        var line = Assert.Single(model.Blocks.Single(b => b.BlockKey == CardBlocks.BaseStats).Lines);
        Assert.False(line.Args.ContainsKey("element"));
    }

    static string? PoolIdOf(string paramsJson)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        return doc.RootElement.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.Object
               && c.TryGetProperty("pool", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    static string? ChannelString(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
    }

    [Fact]
    public void The_power_row_renders_under_rule_p_two_significant_figures_with_its_band()
    {
        // Module 9's own CardPower render, carried onto the header -- never a second power computation.
        var power = new CardPowerDisplay(Shown: true, RoundedValue: 1300, BandPercent: 25);
        var header = ItemCardRenderer.Render(FullCard() with { Power = power }).Blocks
            .Single(b => b.BlockKey == CardBlocks.Header).Lines[0];

        Assert.Equal(power.Render(), header.Args["power"]);
        Assert.Contains("±25%", header.Args["power"]);

        // Suppressed by its own tuning flag emits NO row rather than an empty one (§10 Q7).
        var suppressed = ItemCardRenderer.Render(FullCard() with { Power = CardPowerDisplay.Suppressed })
            .Blocks.Single(b => b.BlockKey == CardBlocks.Header).Lines[0];
        Assert.DoesNotContain("power", suppressed.Args.Keys);
    }

    /// <summary>G3 §8.6 as a guard: `ItemDisplayRenderer.Line` is the only thing that produces a
    /// `DisplayLine` carrying a rendered SENTENCE. Everything else in the module emits structural
    /// lines (`{key, args}` with no template behind them), which is a different job.</summary>
    [Fact]
    public void No_second_line_producer_exists()
    {
        var dir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Display");
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            if (Path.GetFileName(file) is "ItemDisplayRenderer.cs" or "DisplayTemplates.cs") continue;
            var text = File.ReadAllText(file);
            if (text.Contains("DisplayTemplates.Render(", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.Empty(offenders);
    }

    // ================================================================================================
    //  The four reason codes, wired
    // ================================================================================================

    [Fact]
    public void The_display_namespace_is_registered_so_its_rule_ids_are_raisable()
    {
        DisplayRules.EnsureRegistered();
        foreach (var rule in new[]
                 {
                     DisplayRules.MissingUnitClass, DisplayRules.MissingDisplayTemplate,
                     DisplayRules.MissingDisplayKey, DisplayRules.UnrenderedMagnitude,
                 })
        {
            Assert.True(ContentRuleNamespaces.IsRegistered(rule), rule);
            var rejection = DisplayRules.Violated(rule, "x");
            Assert.Equal(AtomRejectionReason.ContentRuleViolated, rejection.Reason);
        }
    }

    [Fact]
    public void A_family_with_atoms_and_no_template_row_is_rejected()
    {
        var findings = DisplayContentRules.Check(
            new[] { new DisplayFamilyFact("atom.nobody-authored-me", "stat.modify", "maxHp", true) },
            Templates.Value.Values.ToList());

        var f = Assert.Single(findings);
        Assert.Equal(DisplayRules.MissingDisplayTemplate, f.RuleId);
    }

    [Fact]
    public void A_channel_with_a_reader_and_no_unit_class_is_rejected()
    {
        var findings = DisplayContentRules.Check(
            new[] { new DisplayFamilyFact("atom.vitality", "stat.modify", "not.a.real.channel", true) },
            Templates.Value.Values.ToList());

        Assert.Contains(findings, f => f.RuleId == DisplayRules.MissingUnitClass);
    }

    [Fact]
    public void A_kind_that_carries_no_channel_is_never_reported_as_missing_a_unit()
    {
        // `status.apply`/`board.action`/`resource.delta` families author no channel at all. Reporting
        // them would make the rule fire on 30+ perfectly good rows and teach everyone to ignore it.
        var findings = DisplayContentRules.Check(
            new[] { new DisplayFamilyFact("atom.venomous", "status.apply", "", false) },
            Templates.Value.Values.ToList());

        Assert.DoesNotContain(findings, f => f.RuleId == DisplayRules.MissingUnitClass);
    }

    [Fact]
    public void A_declared_magnitude_the_template_never_shows_is_rejected()
    {
        var wordless = new DisplayTemplateRow(
            "atom.silent", "disptpl.silent", "grants something", null, null, "g.test", "live");

        var findings = DisplayContentRules.Check(
            new[] { new DisplayFamilyFact("atom.silent", "stat.modify", "maxHp", DeclaresMagnitude: true) },
            new[] { wordless });

        var f = Assert.Single(findings);
        Assert.Equal(DisplayRules.UnrenderedMagnitude, f.RuleId);
    }

    [Fact]
    public void A_template_row_whose_string_key_is_undefined_is_rejected()
    {
        var findings = DisplayContentRules.Check(
            Array.Empty<DisplayFamilyFact>(),
            new[] { new DisplayTemplateRow("atom.x", "disptpl.absent", "+{value} x", null, null, "g", "live") },
            stringKeys: new HashSet<string>(StringComparer.Ordinal) { "disptpl.something-else" });

        var f = Assert.Single(findings);
        Assert.Equal(DisplayRules.MissingDisplayKey, f.RuleId);
    }

    [Fact]
    public void Every_shipped_display_template_row_has_its_string_and_shows_its_magnitude()
    {
        // The real corpus, against the real gk-content/content/display/en.json -- the two rules that are green
        // today, pinned so they stay green.
        var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "content", "display", "en.json")));
        var keys = en.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var findings = DisplayContentRules.Check(RealFamilyFacts(), Templates.Value.Values.ToList(), keys);

        Assert.DoesNotContain(findings, f => f.RuleId == DisplayRules.MissingDisplayKey);
        Assert.DoesNotContain(findings, f => f.RuleId == DisplayRules.UnrenderedMagnitude);
    }

    /// <summary>
    /// ⭐ <b>The quarantine this test used to pin is CLOSED (item-content T12).</b> It pinned the set
    /// <c>{ atom.elpw-focus, atom.elpw-overflow, atom.elpw-pierce }</c> and said, in its own words,
    /// that <i>"authoring E12's registry rows turns this test red and makes someone delete the pin"</i>.
    /// The rows are authored, so the pin is deleted and the assertion is inverted: no shipped family
    /// declares a channel with no resolvable unit.
    ///
    /// <para>Both channels the three families name — <c>combat.power.pierce.{variant}</c> and
    /// <c>combat.power.overflow.{variant}</c> — now resolve through
    /// <c>ChannelUnits.AuthoredChannelFamilies</c>, grounded in the term each one states it adds
    /// into: <c>OverlayCombatCalculator.cs:145</c>'s <c>(power - defense)</c> sum, whose two existing
    /// halves are both <see cref="UnitClass.GameUnits"/>. See that table's own comment for why this
    /// is not <see cref="UnitClass.ReciprocalPoints"/> despite the word "pierce".</para>
    ///
    /// <para>The <c>FamilyExpansion.cs:44-47</c> pool quarantine is a SEPARATE, still-open gap and is
    /// untouched: these three families still have no shipped E30 pool and are still refused by name
    /// at generation. A unit is what the display lane owed them; a pool is E30's.</para>
    /// </summary>
    [Fact]
    public void No_shipped_family_names_a_channel_with_no_resolvable_unit()
    {
        var findings = DisplayContentRules.Check(RealFamilyFacts(), Templates.Value.Values.ToList());

        var unresolved = findings
            .Where(f => f.RuleId == DisplayRules.MissingUnitClass)
            .Select(f => f.Subject)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unresolved);
    }

    /// <summary>
    /// The T12 registrations themselves, asserted per channel rather than only through the corpus
    /// rule — so a future change that deletes a row fails here with the channel's name on it.
    /// Probed with a concrete element, the way <c>ChannelUnits.ForAuthoredChannel</c> does.
    /// </summary>
    [Theory]
    [InlineData("combat.power.pierce.{variant}")]        // atom.elpw-pierce (Flat), atom.elpw-focus (Increased)
    [InlineData("combat.power.overflow.{variant}")]      // atom.elpw-overflow (Flat)
    public void The_minted_elemental_power_channels_resolve_to_the_unit_of_the_sum_they_join(string channel)
    {
        // The unit is not asserted as a literal picked here: it is read off the channel this one
        // states it adds into, so if `combat.power` is ever reclassified these move with it.
        var reference = ChannelUnits.ForAuthoredChannel("combat.power.{variant}");
        Assert.Equal(UnitClass.GameUnits, reference);
        Assert.Equal(reference, ChannelUnits.ForAuthoredChannel(channel));

        // ...and the bare family prefix is still NOT a channel. Only a concrete member resolves, so
        // the prefix match cannot swallow an id that merely starts the same way.
        Assert.Null(ChannelUnits.For(channel.Replace(".{variant}", "", StringComparison.Ordinal) + "."));
    }

    /// <summary>⛔ The name trap this registration had to avoid, pinned so it stays avoided:
    /// <c>combat.penetration.*</c> is the asymptotic <c>PierceFactor</c> family and is
    /// <see cref="UnitClass.ReciprocalPoints"/>; <c>combat.power.pierce.*</c> is an additive term in
    /// the power/defense sum and is not. Two channels with "pierce" in the name, two different
    /// units — which is exactly what §2.3 means by "a unit is inseparable from its reader".</summary>
    [Fact]
    public void The_two_pierce_shaped_channels_do_not_share_a_unit()
    {
        Assert.Equal(UnitClass.ReciprocalPoints, ChannelUnits.For("combat.penetration.fire"));
        Assert.Equal(UnitClass.GameUnits, ChannelUnits.For("combat.power.pierce.fire"));
    }

    [Fact]
    public void Every_shipped_channel_bearing_family_resolves_to_a_unit_class()
    {
        // The positive half of the rule, and the one that matters day to day: every family that
        // declares a channel has a unit, which is what makes `+10 fire power` and `+10 crit rate`
        // distinguishable rather than hopeful. The three-family quarantine this used to exclude by
        // name was closed by item-content T12 — nothing is skipped here any more.
        var facts = RealFamilyFacts().ToList();
        Assert.True(facts.Count > 80, "the corpus reader returned almost nothing -- the check would be vacuous");

        var findings = DisplayContentRules.Check(facts, Templates.Value.Values.ToList());
        Assert.DoesNotContain(findings, f => f.RuleId == DisplayRules.MissingUnitClass);
    }

    /// <summary>
    /// <c>MissingDisplayTemplate</c> over the real corpus, asserted for FAITHFULNESS rather than for a
    /// count: the rule must fire on exactly the families that have no template row, and on nothing
    /// else. A count would pin this test to whichever authoring wave is mid-flight.
    ///
    /// <para>The two families it used to catch — <c>atom.chill-punisher</c> and
    /// <c>atom.rot-punisher</c>, authored into <c>g-punisher.json</c> without a matching template —
    /// got their rows in item-content T11 (<c>disptpl.p3-050</c>/<c>-051</c>), so the expected set is
    /// empty today. The faithfulness shape is kept: the next family authored ahead of its template
    /// is named by this test rather than counted by it.</para>
    /// </summary>
    [Fact]
    public void Missing_display_template_fires_on_exactly_the_untemplated_families()
    {
        var facts = RealFamilyFacts();
        var templated = Templates.Value.Keys.ToHashSet(StringComparer.Ordinal);

        var expected = facts.Select(f => f.FamilyId).Where(id => !templated.Contains(id))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

        var actual = DisplayContentRules.Check(facts, Templates.Value.Values.ToList())
            .Where(f => f.RuleId == DisplayRules.MissingDisplayTemplate)
            .Select(f => f.Subject)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, actual);

        // T11's own acceptance: every authored family now has a template row. Asserted separately
        // from the faithfulness check above, which would pass just as happily on an empty corpus.
        Assert.Empty(expected);
        Assert.True(facts.Count > 80, "the corpus reader returned almost nothing");
    }

    /// <summary>
    /// T11's two new rows, rendered end to end at Min and Max through the real renderer — the
    /// per-family check `spec-affix-draw-coverage.md` §6 asks for. Deliberately NOT driven from
    /// <see cref="RealAtoms"/>: both families are still refused at generation (they author no
    /// <c>op</c>, so <c>FamilyExpansion</c> has no tier-magnitude formula for them — see
    /// <c>FamilyExpansion.cs:258</c>), which is a separate, named gap. The template must still
    /// render, because <c>InstanceProducer</c> can carry a hand-seeded row for one.
    /// </summary>
    [Theory]
    [InlineData("atom.chill-punisher")]
    [InlineData("atom.rot-punisher")]
    public void The_punisher_families_render_at_min_and_max_with_no_raw_id(string familyId)
    {
        var found = LookupTemplate(familyId);
        Assert.NotNull(found);
        var template = found!.Value;
        Assert.Equal("live", template.Status);
        Assert.Equal("g.punisher", template.GroupId);

        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId(familyId, "", 1),
            KindId = "resource.delta",
            FamilyId = familyId,
            Variant = "",
            Tier = 1,
            Name = familyId,
            WhenJson = "{}",
            ParamsJson = """{"op":"flat","amount":{"min":4,"max":9,"roll":"onApply"}}""",
            TagsJson = "{}",
            Enabled = true,
        };

        // ⛔ The unit is SUPPLIED here, and that is a named production gap, not a convenience.
        // `ItemCardRenderer` resolves a unit from `params.channel` only (`ItemCard.cs:389-390`), and
        // every trigger-shaped family -- resource.delta / status.apply / spawn.entity, 40 of the 109
        // -- authors no channel at all, so the card would pass `null` and `ItemDisplayRenderer` would
        // refuse any template of theirs that shows `{value}`. That axis (kind -> unit, as opposed to
        // channel -> unit) does not exist anywhere in the codebase today; it is filed in
        // tasks/item-content-todo.md rather than invented here, because building it is a design
        // change to ssot-presentation.md §2.3's ledger, not an authoring pass.
        // GameUnits is the right value regardless: the payload is signed HP through FA10's
        // resource.delta funnel, the same game-unit currency `atk` and `combat.power` carry.
        const UnitClass BonusDamageUnit = UnitClass.GameUnits;

        foreach (var value in new long[] { 4, 9 })
            foreach (var frame in new[] { "humanoid", "plant" })
            {
                var text = ItemDisplayRenderer.Line(
                    template, atom, frame, value, SourceKind.AffixSuffix, 0, BonusDamageUnit,
                    elementVariant: null, roll: RollPolicy.OnApply, bandMax: 9).Args["__rendered"];

                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain('{', text);
                Assert.DoesNotContain('}', text);
                Assert.DoesNotContain(familyId, text, StringComparison.Ordinal);
                Assert.Contains(value.ToString(System.Globalization.CultureInfo.InvariantCulture), text,
                    StringComparison.Ordinal);
            }
    }

    [Fact]
    public void The_generated_status_channel_families_have_a_unit_and_did_not_before()
    {
        // ⛔ The shipped facade read `TryGet`, so every GENERATED status family reported "no unit" for
        // a channel the engine reads perfectly well. Fixed in this pass; pinned so it stays fixed.
        var registry = DerivedStatRegistry.CreateDefault();

        Assert.Equal(UnitClass.StatusPotencyPoints, ChannelUnits.For("status.duration.burn", registry));
        Assert.Equal(UnitClass.StatusPotencyPoints, ChannelUnits.For("status.intensity.burn", registry));
        Assert.Equal(UnitClass.Flag, ChannelUnits.For("status.immune.freeze", registry));

        // ...and an unknown channel is still null, never a guess.
        Assert.Null(ChannelUnits.For("not.a.real.channel", registry));
        Assert.Null(ChannelUnits.ForAuthoredChannel("not.a.real.channel", registry));
    }

    [Fact]
    public void An_authored_element_template_and_a_bare_family_stem_both_resolve()
    {
        // The two shapes the affix corpus really writes, neither of which is a runtime channel id.
        Assert.NotNull(ChannelUnits.ForAuthoredChannel("combat.power.{variant}"));
        Assert.NotNull(ChannelUnits.ForAuthoredChannel("status.resist"));
        Assert.NotNull(ChannelUnits.ForAuthoredChannel("status.immune"));
    }

    /// <summary>Every shipped affix family, reduced to the four rules' inputs.</summary>
    static IReadOnlyList<DisplayFamilyFact> RealFamilyFacts()
    {
        var facts = new List<DisplayFamilyFact>();
        foreach (var file in Directory.GetFiles(Seed("items", "affix-families"), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var pars = e.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                    ? p : default;
                facts.Add(new DisplayFamilyFact(
                    e.GetProperty("id").GetString()!,
                    e.TryGetProperty("kindId", out var k) ? k.GetString() ?? "" : "",
                    pars.ValueKind == JsonValueKind.Object && pars.TryGetProperty("channel", out var c)
                        && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "",
                    pars.ValueKind == JsonValueKind.Object && pars.TryGetProperty("amount", out _)));
            }
        }
        return facts;
    }
}

/// <summary>Test-local helper matching <c>ItemCardCompare.SameLine</c>'s own notion of "the same
/// line" for compare purposes — NOT <c>DisplayModel.Fingerprint()</c>. Corrected 2026-09-07: this
/// used to wrap one line into a whole <c>DisplayModel</c> and call <c>Fingerprint()</c>, but that
/// function deliberately includes <see cref="DisplayLine.GroupOrder"/> (a per-card visual sort key
/// looked up from the family's own <c>groupId</c>, per <c>ItemCard.AtomLines</c> — never part of a
/// line's player-visible content) because ITS OWN job is proving one render is fully deterministic,
/// a stricter notion than "does this line show the player something different," which is what a
/// two-card DIFF needs and exactly what <c>SameLine</c>'s own doc comment enumerates (key, unit,
/// source kind, bar, roll quality, context, args — GroupOrder is deliberately not in that list).
/// Reusing the wrong identity function here made this test fail once the corpus grew enough for two
/// otherwise-identical lines to land in different visual groups on two different cards — a real
/// latent test bug, not a defect in `ItemCardCompare` or `DisplayModel`.</summary>
internal static class DisplayLineTestExtensions
{
    public static string Fingerprintable(this DisplayLine line) =>
        string.Join('|',
            line.Key, line.Unit?.ToString() ?? "-", line.SourceKind?.ToString() ?? "-",
            line.RollBar?.Segments.ToString() ?? "-", line.RollQualityPerMille?.ToString() ?? "-",
            line.ContextRead ?? "-",
            string.Join('|', line.Args.OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => $"{a.Key}={a.Value}")));
}
