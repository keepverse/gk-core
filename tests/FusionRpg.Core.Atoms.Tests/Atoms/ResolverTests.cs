using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T3.3 (`resolution-order`, `spec-resolution-order.md`): <see cref="Resolver.Resolve"/>'s five-step
/// order (slots → affixes → atoms → tiers → values), each on its own named RNG stream, replacing
/// <see cref="Instantiator.Draw"/>'s single-stream draw for anything an affix bundle or a slot needs.
/// </summary>
public class ResolverTests
{
    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);
    static readonly Dictionary<string, AffixRow> Affixes = new(StringComparer.Ordinal);

    static ResolverTests()
    {
        void AddAtom(string family, string variant, int tier, string paramsJson = "{}")
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            Catalog[id] = new AtomRow
            {
                AtomId = id, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
                ParamsJson = paramsJson,
            };
        }

        AddAtom("atom.vitality", "", 1, "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}");
        AddAtom("atom.vitality", "", 2, "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":90}");
        AddAtom("atom.might", "", 1, "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":10}");
        AddAtom("atom.roll", "", 1,
            "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onInstantiate\"}}");
        foreach (var v in new[] { "fire", "ice", "air" })
            for (var t = 1; t <= 5; t++)
                AddAtom("atom.ember-power", v, t, "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":5}");
        foreach (var v in new[] { "fire", "ice", "air" })
            for (var t = 1; t <= 5; t++)
                AddAtom("atom.frost-power", v, t, "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":7}");
    }

    static AtomRow? LookupAtom(string id) => Catalog.TryGetValue(id, out var a) ? a : null;
    static AffixRow? LookupAffix(string id) => Affixes.TryGetValue(id, out var a) ? a : null;
    static IReadOnlyList<string> DomainMembers(string domain) =>
        domain == "element" ? new[] { "fire", "ice", "air" } : Array.Empty<string>();

    static void Seed(params AffixRow[] affixes)
    {
        Affixes.Clear();
        foreach (var a in affixes) Affixes[a.AffixId] = a;
    }

    static ResolvedDraw Resolve(ContainerRow c, long seed, VariantShift? variant = null) =>
        Resolver.Resolve(c, LookupAtom, LookupAffix, DomainMembers, seed, variant);

    static readonly Dictionary<string, ChannelPoolRow> Pools = new(StringComparer.Ordinal);
    static ChannelPoolRow? LookupPool(string id) => Pools.TryGetValue(id, out var p) ? p : null;

    static ResolvedDraw ResolveWithPools(ContainerRow c, long seed, VariantShift? variant = null) =>
        Resolver.Resolve(c, LookupAtom, LookupAffix, DomainMembers, seed, variant, lookupPool: LookupPool);

    // ---- the five-step order, end to end ------------------------------------------------------------

    [Fact]
    public void A_single_ref_affix_resolves_to_its_own_concrete_atom()
    {
        Seed(new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.a", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.vitality", 100) },
        };

        var draw = Resolve(c, 1);

        Assert.Equal(new[] { "atom.vitality.t1" }, draw.Atoms.Select(a => a.AtomId));
    }

    [Fact]
    public void A_slot_ref_resolves_to_a_real_domain_member_and_a_tier_within_the_window()
    {
        Seed(new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.b", Kind = ContainerKind.Item, PrefixRolls = 1,
            MinTier = 2, MaxTier = 4,
            Pool = new[] { new ContainerPoolRow("affix.elemental", 100, "g.elemental") },
        };

        var draw = Resolve(c, 7);

        var atomId = Assert.Single(draw.Atoms).AtomId;
        Assert.StartsWith("atom.ember-power.", atomId, StringComparison.Ordinal);
        var atom = Catalog[atomId];
        Assert.Contains(atom.Variant, new[] { "fire", "ice", "air" });
        Assert.InRange(atom.Tier, 2, 4);
    }

    [Fact]
    public void Master_of_fire_and_ice_resolves_as_one_correlated_draw()
    {
        // Two refs, same slot name — one family's power atom AND another family's power atom, both
        // keyed on "$E1". Correlated means both resolve to the SAME element (definitions.md §4a's own
        // motivating example): "master of fire and ice" would be the WRONG name for two independent
        // element rolls landing on different elements by accident.
        Seed(new AffixRow("affix.dual-element", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
            new AffixRefRow(2, null, "E1", "element", 1, "atom.frost-power.$E1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.correlated", Kind = ContainerKind.Item, PrefixRolls = 1,
            MinTier = 1, MaxTier = 1,
            Pool = new[] { new ContainerPoolRow("affix.dual-element", 100, "g.dual-element") },
        };

        for (long seed = 0; seed < 30; seed++)
        {
            var draw = Resolve(c, seed);
            Assert.Equal(2, draw.Atoms.Count);
            var emberVariant = Catalog[draw.Atoms[0].AtomId].Variant;
            var frostVariant = Catalog[draw.Atoms[1].AtomId].Variant;
            Assert.Equal(emberVariant, frostVariant);
        }
    }

    /// <summary>A1 (spec-affix-schema.md, decided 2026-09-03): a `Mixed`-class affix consumes ONE
    /// prefix roll AND one suffix roll simultaneously, never twice — replaces the earlier
    /// "under today's two-independent-draws interim model" test, which only asserted `NotEmpty` and
    /// would have passed even with the double-draw defect this fix closes.</summary>
    [Fact]
    public void Mixed_class_affix_consumes_one_prefix_and_one_suffix_roll_never_twice()
    {
        Seed(
            new AffixRow("affix.mixed", AffixClass.Mixed, new[]
            {
                new AffixRefRow(1, "atom.vitality.t1"),
                new AffixRefRow(2, "atom.roll.t1"),
            }),
            new AffixRow("affix.filler", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.might.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.mixed", Kind = ContainerKind.Item, PrefixRolls = 1, SuffixRolls = 1,
            Pool = new[]
            {
                new ContainerPoolRow("affix.mixed", 100, "g.mixed"),
                new ContainerPoolRow("affix.filler", 1),
            },
        };

        for (long seed = 0; seed < 40; seed++)
        {
            var draw = Resolve(c, seed);
            var rollCount = draw.Atoms.Count(a => a.AtomId == "atom.roll.t1"); // unique to the mixed bundle
            Assert.True(rollCount <= 1, $"seed {seed}: the mixed bundle appeared {rollCount} times");
            // Whenever the mixed bundle was drawn at all, BOTH its refs are present together (one
            // correlated roll, never one ref without the other).
            var vitalityCount = draw.Atoms.Count(a => a.AtomId == "atom.vitality.t1");
            Assert.Equal(vitalityCount, rollCount);
        }
    }

    /// <summary>A1's over-draw guard: once the suffix budget is fully spent (zero to begin with, and
    /// nothing else in the pool is prefix-eligible), a `Mixed` affix must never be picked — picking it
    /// would spend a suffix roll the container never had.</summary>
    [Fact]
    public void Mixed_affix_is_not_drawable_when_the_suffix_budget_is_exhausted()
    {
        Seed(new AffixRow("affix.mixed", AffixClass.Mixed, new[]
        {
            new AffixRefRow(1, "atom.vitality.t1"),
            new AffixRefRow(2, "atom.roll.t1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.no-suffix-budget", Kind = ContainerKind.Item,
            PrefixRolls = 1, SuffixRolls = 0, // nothing to spend on a Mixed pick
            Pool = new[] { new ContainerPoolRow("affix.mixed", 100, "g.mixed") },
        };

        for (long seed = 0; seed < 20; seed++)
            Assert.Empty(Resolve(c, seed).Atoms);
    }

    /// <summary>The double-draw defect this fix closes: with only ONE eligible affix (a `Mixed` one)
    /// and both budgets nonzero, the old two-independent-passes code drew it twice — once per pass,
    /// with no memory between them. It must now resolve exactly once.</summary>
    [Fact]
    public void A_mixed_affix_is_never_drawn_twice_in_one_resolve()
    {
        Seed(new AffixRow("affix.mixed", AffixClass.Mixed, new[]
        {
            new AffixRefRow(1, "atom.vitality.t1"),
            new AffixRefRow(2, "atom.roll.t1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.only-mixed", Kind = ContainerKind.Item, PrefixRolls = 1, SuffixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.mixed", 100, "g.mixed") },
        };

        for (long seed = 0; seed < 20; seed++)
        {
            var draw = Resolve(c, seed);
            // The only pool row is Mixed-eligible for BOTH passes; the pre-fix code drew it in each,
            // yielding 4 atoms (the bundle's 2 refs, twice). The fix draws it once: 2 atoms.
            Assert.Equal(2, draw.Atoms.Count);
            Assert.Equal(new[] { "atom.vitality.t1", "atom.roll.t1" }, draw.Atoms.Select(a => a.AtomId));
        }
    }

    /// <summary>A pool with no `Mixed` affix at all rolls exactly as many affixes as its two budgets
    /// name — nothing in the two-pass rewrite changes behaviour when there is no `Mixed` bundle to
    /// carry state for, which is what makes every OTHER (non-`Mixed`) test in this file a byte-for-
    /// byte regression guard on its own.</summary>
    [Fact]
    public void A_pool_with_no_mixed_affix_rolls_exactly_the_two_budgets_worth()
    {
        Seed(
            new AffixRow("affix.p1", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }),
            new AffixRow("affix.p2", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.might.t1") }),
            new AffixRow("affix.s1", AffixClass.Suffix, new[] { new AffixRefRow(1, "atom.roll.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.no-mixed", Kind = ContainerKind.Item, PrefixRolls = 1, SuffixRolls = 1,
            Pool = new[]
            {
                new ContainerPoolRow("affix.p1", 50, "g.p1"),
                new ContainerPoolRow("affix.p2", 50, "g.p2"),
                new ContainerPoolRow("affix.s1", 100, "g.s1"),
            },
        };

        for (long seed = 0; seed < 20; seed++)
            Assert.Equal(2, Resolve(c, seed).Atoms.Count); // exactly PrefixRolls + SuffixRolls, always
    }

    // ---- variant shifts -------------------------------------------------------------------------

    [Fact]
    public void Variant_shifts_the_tier_window_and_authors_nothing()
    {
        Seed(new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.variant", Kind = ContainerKind.Item, PrefixRolls = 1,
            MinTier = 1, MaxTier = 1,
            Pool = new[] { new ContainerPoolRow("affix.elemental", 100, "g.elemental") },
        };
        var ancient = new VariantShift("ancient", TierWindowShift: 1, PrefixRollShift: 0, SuffixRollShift: 0, RerollsOneElementSlot: false);

        var before = Catalog.Count;
        var draw = Resolve(c, 9, ancient);

        var atom = Catalog[Assert.Single(draw.Atoms).AtomId];
        // window [1,1] shifted by +1 -> [2,2]; the resolver picked tier 2, not tier 1.
        Assert.Equal(2, atom.Tier);
        Assert.Equal(before, Catalog.Count); // nothing new entered the catalog
    }

    [Fact]
    public void Ancient_at_rung_10_saturates_at_t5_not_a_progression_cap()
    {
        Seed(new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.saturate", Kind = ContainerKind.Item, PrefixRolls = 1,
            MinTier = 4, MaxTier = 5,
            Pool = new[] { new ContainerPoolRow("affix.elemental", 100, "g.elemental") },
        };
        var ancient = new VariantShift("ancient", TierWindowShift: 1, PrefixRollShift: 0, SuffixRollShift: 0, RerollsOneElementSlot: false);

        for (long seed = 0; seed < 20; seed++)
        {
            var draw = Resolve(c, seed, ancient);
            var atom = Catalog[Assert.Single(draw.Atoms).AtomId];
            Assert.InRange(atom.Tier, 1, VariantShift.MaxTier); // never 6 — t6 does not exist
        }
    }

    [Fact]
    public void Corrupted_can_change_which_element_a_slot_resolves_to()
    {
        Seed(new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.corrupt", Kind = ContainerKind.Item, PrefixRolls = 1,
            MinTier = 1, MaxTier = 1,
            Pool = new[] { new ContainerPoolRow("affix.elemental", 100, "g.elemental") },
        };
        var corrupted = new VariantShift("corrupted", TierWindowShift: 0, PrefixRollShift: 0, SuffixRollShift: 0, RerollsOneElementSlot: true);

        // Not every seed necessarily differs (the reroll can land on the same member by chance), but
        // across enough seeds at least one must differ from the non-corrupted resolve of the same
        // seed — otherwise the reroll is not actually consuming a second draw.
        var anyDifferent = false;
        for (long seed = 0; seed < 40; seed++)
        {
            var normal = Catalog[Assert.Single(Resolve(c, seed).Atoms).AtomId].Variant;
            var corrupt = Catalog[Assert.Single(Resolve(c, seed, corrupted).Atoms).AtomId].Variant;
            if (normal != corrupt) anyDifferent = true;
        }
        Assert.True(anyDifferent, "corrupted never diverged from the non-corrupted resolve across 40 seeds");
    }

    // ---- stream independence -----------------------------------------------------------------------

    [Fact]
    public void An_extra_undrawn_slot_in_the_pool_does_not_shift_which_affixes_are_drawn()
    {
        // If "slots" and "affixes" shared one stream, adding a THIRD, weight-0, slot-bearing affix
        // (which step 1 still resolves — every slot in the pool resolves, drawn or not) would shift
        // step 2's own draw sequence. Separate streams mean it must not.
        Seed(
            new AffixRow("affix.a", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }),
            new AffixRow("affix.b", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.might.t1") }));
        var baseline = new ContainerRow
        {
            ContainerId = "item.stream", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.a", 50), new ContainerPoolRow("affix.b", 50) },
        };

        var results = new List<string>();
        for (long seed = 0; seed < 20; seed++) results.Add(Resolve(baseline, seed).Atoms[0].AtomId);

        // Now add a weight-0 slot-bearing affix. Step 1 resolves ITS slot too (every affix in the pool,
        // per Resolver.ResolveSlots' own doc comment) — a shared stream would burn an extra draw here
        // and shift every subsequent affix pick.
        Seed(
            new AffixRow("affix.a", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }),
            new AffixRow("affix.b", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.might.t1") }),
            new AffixRow("affix.phantom", AffixClass.Prefix, new[]
            {
                new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
            }));
        var withPhantom = baseline with
        {
            Pool = new[]
            {
                new ContainerPoolRow("affix.a", 50), new ContainerPoolRow("affix.b", 50),
                new ContainerPoolRow("affix.phantom", 0, "g.phantom"),
            },
        };

        var withPhantomResults = new List<string>();
        for (long seed = 0; seed < 20; seed++) withPhantomResults.Add(Resolve(withPhantom, seed).Atoms[0].AtomId);

        Assert.Equal(results, withPhantomResults);
    }

    [Fact]
    public void Each_named_stream_is_independent_of_how_many_times_the_others_were_drawn()
    {
        // Proven by construction, directly against SeededRng.DeriveStream: interleaving draws from a
        // fifth, hypothetical stream between the four real ones never changes any of the four's own
        // sequence — the independence property a future sixth resolution layer relies on.
        const long seed = 42;
        const string containerId = "item.independent";

        var slot1 = new AtomRandom(unchecked((ulong)seed), "affix.slot." + containerId).NextInclusive(0, 1_000_000);
        var affix1 = new AtomRandom(unchecked((ulong)seed), "affix.draw." + containerId).NextInclusive(0, 1_000_000);
        var tier1 = new AtomRandom(unchecked((ulong)seed), "affix.tier." + containerId).NextInclusive(0, 1_000_000);
        var value1 = new AtomRandom(unchecked((ulong)seed), "atom.value." + containerId).NextInclusive(0, 1_000_000);

        // A phantom fifth stream, drawn from repeatedly, interleaved with re-derivations of the four
        // real streams — none of it should move the numbers above.
        _ = new AtomRandom(unchecked((ulong)seed), "future.layer." + containerId).NextInclusive(0, 1_000_000);
        var slot2 = new AtomRandom(unchecked((ulong)seed), "affix.slot." + containerId).NextInclusive(0, 1_000_000);
        _ = new AtomRandom(unchecked((ulong)seed), "future.layer." + containerId).NextInclusive(0, 1_000_000);
        var affix2 = new AtomRandom(unchecked((ulong)seed), "affix.draw." + containerId).NextInclusive(0, 1_000_000);
        var tier2 = new AtomRandom(unchecked((ulong)seed), "affix.tier." + containerId).NextInclusive(0, 1_000_000);
        var value2 = new AtomRandom(unchecked((ulong)seed), "atom.value." + containerId).NextInclusive(0, 1_000_000);

        Assert.Equal(slot1, slot2);
        Assert.Equal(affix1, affix2);
        Assert.Equal(tier1, tier2);
        Assert.Equal(value1, value2);
    }

    // ---- reproducibility ------------------------------------------------------------------------

    [Fact]
    public void Same_seed_same_container_same_variant_reproduces_identically()
    {
        Seed(new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.repro", Kind = ContainerKind.Item, PrefixRolls = 1,
            MinTier = 1, MaxTier = 3,
            Pool = new[] { new ContainerPoolRow("affix.elemental", 100, "g.elemental") },
        };
        var variant = new VariantShift("ancient", 1, 0, 0, false);

        var a = Resolve(c, 123, variant);
        var b = Resolve(c, 123, variant);

        Assert.Equal(a.Atoms.Select(x => (x.AtomId, x.ValuesJson)), b.Atoms.Select(x => (x.AtomId, x.ValuesJson)));
    }

    [Fact]
    public void Different_seeds_produce_different_instances()
    {
        Seed(new AffixRow("affix.roll", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.roll.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.vary", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.roll", 100) },
        };

        var seen = new HashSet<string>();
        for (long seed = 0; seed < 40; seed++) seen.Add(Resolve(c, seed).Atoms[0].ValuesJson);

        Assert.True(seen.Count > 1, "every seed rolled the same value");
    }

    [Fact]
    public void An_empty_pool_resolves_to_nothing()
    {
        var c = new ContainerRow { ContainerId = "item.empty", Kind = ContainerKind.Item };

        Assert.Empty(Resolve(c, 1).Atoms);
    }

    // ---- values (step 5) ------------------------------------------------------------------------

    [Fact]
    public void An_OnInstantiate_value_is_frozen_inside_its_range()
    {
        Seed(new AffixRow("affix.roll", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.roll.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.frozen", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.roll", 100) },
        };

        for (long seed = 0; seed < 30; seed++)
        {
            var json = Resolve(c, seed).Atoms[0].ValuesJson;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.InRange(doc.RootElement.GetProperty("amount").GetInt32(), 10, 20);
        }
    }

    [Fact]
    public void A_fixed_value_is_copied_verbatim()
    {
        Seed(new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.fixed", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.vitality", 100) },
        };

        var json = Resolve(c, 1).Atoms[0].ValuesJson;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(45, doc.RootElement.GetProperty("amount").GetInt32());
    }

    // ---- E30 §3.2a: the channel.pool resolve step (effect-pipeline module 2's own responsibility,
    // decided 2026-09-03) — a pooled `channel` reference expands into `count` separate ResolvedAtoms,
    // same atom_id, one shared rolled magnitude, a different concrete channel each. -----------------

    [Fact]
    public void A_pooled_channel_atom_resolves_to_different_concrete_channels_across_two_roll_seeds()
    {
        // Test 1 (spec-channel-pool.md §5).
        Pools["pool.test.three"] = new ChannelPoolRow("pool.test.three", null, new[]
        {
            new ChannelPoolMember("chan.a", 1000),
            new ChannelPoolMember("chan.b", 1000),
            new ChannelPoolMember("chan.c", 1000),
        });
        Catalog["atom.pooled-single.t1"] = new AtomRow
        {
            AtomId = "atom.pooled-single.t1", KindId = "stat.derived", FamilyId = "atom.pooled-single", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.three","count":1},"op":"flat","amount":100}""",
        };
        Seed(new AffixRow("affix.pooled-single", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.pooled-single.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.pooled-single", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.pooled-single", 100) },
        };

        var seenChannels = new HashSet<string>(StringComparer.Ordinal);
        for (long seed = 0; seed < 30; seed++)
        {
            var atom = Assert.Single(ResolveWithPools(c, seed).Atoms);
            using var doc = System.Text.Json.JsonDocument.Parse(atom.ValuesJson);
            seenChannels.Add(doc.RootElement.GetProperty("channel").GetString()!);
        }

        Assert.True(seenChannels.Count > 1, "every seed drew the same channel — the pool is decorative, not live");
        Assert.Subset(new HashSet<string>(new[] { "chan.a", "chan.b", "chan.c" }), seenChannels);
    }

    [Fact]
    public void A_pooled_channel_atom_replays_byte_identically_for_the_same_seed()
    {
        // Test 2 — Law 1's reproducibility contract, extended through the channel.pool stream.
        Pools["pool.test.three"] = new ChannelPoolRow("pool.test.three", null, new[]
        {
            new ChannelPoolMember("chan.a", 1000),
            new ChannelPoolMember("chan.b", 1000),
            new ChannelPoolMember("chan.c", 1000),
        });
        Catalog["atom.pooled-repro.t1"] = new AtomRow
        {
            AtomId = "atom.pooled-repro.t1", KindId = "stat.derived", FamilyId = "atom.pooled-repro", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.three","count":1},"op":"flat","amount":{"min":10,"max":200,"roll":"onInstantiate"}}""",
        };
        Seed(new AffixRow("affix.pooled-repro", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.pooled-repro.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.pooled-repro", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.pooled-repro", 100) },
        };

        var a = ResolveWithPools(c, 123);
        var b = ResolveWithPools(c, 123);

        Assert.Equal(a.Atoms.Select(x => (x.AtomId, x.ValuesJson)), b.Atoms.Select(x => (x.AtomId, x.ValuesJson)));
    }

    [Fact]
    public void Count_six_over_a_six_member_pool_without_allowRepeat_draws_each_member_exactly_once_sharing_one_rolled_magnitude()
    {
        // Test 3 — draw-without-replacement, AND the §3.2a decision that the magnitude is rolled
        // once and shared across every expanded copy, never independently re-rolled per channel.
        Pools["pool.test.six"] = new ChannelPoolRow("pool.test.six", null, new[]
        {
            new ChannelPoolMember("chan.a", 1000), new ChannelPoolMember("chan.b", 1000),
            new ChannelPoolMember("chan.c", 1000), new ChannelPoolMember("chan.d", 1000),
            new ChannelPoolMember("chan.e", 1000), new ChannelPoolMember("chan.f", 1000),
        });
        Catalog["atom.pooled-six.t1"] = new AtomRow
        {
            AtomId = "atom.pooled-six.t1", KindId = "stat.derived", FamilyId = "atom.pooled-six", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.six","count":6,"allowRepeat":false},"op":"flat","amount":{"min":10,"max":200,"roll":"onInstantiate"}}""",
        };
        Seed(new AffixRow("affix.pooled-six", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.pooled-six.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.pooled-six", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.pooled-six", 100) },
        };

        var draw = ResolveWithPools(c, 7);

        Assert.Equal(6, draw.Atoms.Count); // each member exactly once — not 6 independent atoms with duplicates
        Assert.All(draw.Atoms, a => Assert.Equal("atom.pooled-six.t1", a.AtomId)); // same authored atom, every copy

        var channels = draw.Atoms.Select(a =>
            System.Text.Json.JsonDocument.Parse(a.ValuesJson).RootElement.GetProperty("channel").GetString())
            .ToList();
        Assert.Equal(new[] { "chan.a", "chan.b", "chan.c", "chan.d", "chan.e", "chan.f" },
            channels.OrderBy(x => x, StringComparer.Ordinal));

        var amounts = draw.Atoms.Select(a =>
            System.Text.Json.JsonDocument.Parse(a.ValuesJson).RootElement.GetProperty("amount").GetInt32())
            .Distinct()
            .ToList();
        Assert.Single(amounts); // ONE rolled magnitude, shared across all six — never six independent rolls
    }

    [Fact]
    public void A_pooled_channel_atom_compiles_to_the_same_shape_as_a_concrete_one_downstream()
    {
        // Criterion 2's "both compile to the same opcode" — a resolved pooled atom is indistinguishable
        // from an ordinary concrete-channel atom to anything downstream of the resolver.
        Pools["pool.test.one"] = new ChannelPoolRow("pool.test.one", null, new[] { new ChannelPoolMember("chan.only", 1000) });
        Catalog["atom.pooled-shape.t1"] = new AtomRow
        {
            AtomId = "atom.pooled-shape.t1", KindId = "stat.derived", FamilyId = "atom.pooled-shape", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.one","count":1},"op":"flat","amount":100}""",
        };
        Seed(new AffixRow("affix.pooled-shape", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.pooled-shape.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.pooled-shape", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.pooled-shape", 100) },
        };

        var resolved = Assert.Single(ResolveWithPools(c, 1).Atoms);
        using var doc = System.Text.Json.JsonDocument.Parse(resolved.ValuesJson);

        // A concrete-channel string, not an object — exactly what InjectorEffectActionSink.cs:93's
        // JsonOverlay.GetString(p, "channel") already reads for every other stat.derived atom.
        Assert.Equal(System.Text.Json.JsonValueKind.String, doc.RootElement.GetProperty("channel").ValueKind);
        Assert.Equal("chan.only", doc.RootElement.GetProperty("channel").GetString());
        Assert.Equal(100, doc.RootElement.GetProperty("amount").GetInt32());
    }

    [Fact]
    public void A_pooled_channel_atom_with_no_lookupPool_supplied_throws_rather_than_freezing_the_raw_pool_object()
    {
        // §3.2a's "should never happen" guard: validation is expected to refuse a pooled reference
        // reaching a caller with no pool catalog, so this proves the resolver does not silently freeze
        // the unread pool-object JSON (the pre-E30 behaviour) if that guard is ever bypassed.
        Catalog["atom.pooled-noguard.t1"] = new AtomRow
        {
            AtomId = "atom.pooled-noguard.t1", KindId = "stat.derived", FamilyId = "atom.pooled-noguard", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.whatever","count":1},"op":"flat","amount":100}""",
        };
        Seed(new AffixRow("affix.pooled-noguard", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.pooled-noguard.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.pooled-noguard", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.pooled-noguard", 100) },
        };

        Assert.Throws<InvalidOperationException>(() => Resolve(c, 1)); // Resolve(), not ResolveWithPools() — no lookupPool
    }
}
