using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T3.6 (`instance-producer`, ⭐ the payoff): <see cref="InstanceProducer.Compose"/>'s Core-only half —
/// the fixed core frozen exactly as <see cref="Instantiator.TryInstantiate"/> already does, the pool
/// half now drawn through <see cref="Resolver.Resolve"/> (module 2) instead of
/// <see cref="Instantiator.Draw"/>. The Data-layer half (<c>RpgStore.ProduceAndBind</c> — real
/// persistence, <c>ResolveBindings</c> reachability, the transactional atomicity guarantee) has its
/// own tests in <c>FusionRpg.Data.Tests</c>, the same split <c>AffixValidatorTests</c>/
/// <c>AffixStoreTests</c> already established for T3.1.
/// </summary>
public class InstanceProducerTests
{
    const int PinTheta = 20;
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);
    static readonly Dictionary<string, AffixRow> Affixes = new(StringComparer.Ordinal);

    static InstanceProducerTests()
    {
        void AddAtom(string family, string variant, int tier, string paramsJson)
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            Catalog[id] = new AtomRow
            {
                AtomId = id, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
                ParamsJson = paramsJson,
            };
        }

        AddAtom("atom.vitality", "", 1, "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}");
        AddAtom("atom.roll", "", 1,
            "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onInstantiate\"}}");
        foreach (var v in new[] { "fire", "ice", "air" })
            AddAtom("atom.ember-power", v, 1, "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":5}");
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

    static InstanceRow Compose(ContainerRow c, long seed, int theta = PinTheta, VariantShift? variant = null)
    {
        var r = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, seed, theta, Tuning,
            out var instance, variant);
        Assert.True(r.IsOk, r.ToString());
        return instance!;
    }

    [Fact]
    public void The_fixed_core_comes_first_and_the_drawn_pool_continues_the_numbering()
    {
        Seed(new AffixRow("affix.ember", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.ember-power.fire.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.core-then-pool", Kind = ContainerKind.Item, PrefixRolls = 1,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
            Pool = new[] { new ContainerPoolRow("affix.ember", 100) },
        };

        var instance = Compose(c, 1);

        Assert.Equal(new[] { 1, 2 }, instance.Atoms.Select(a => a.Seq));
        Assert.Equal("atom.vitality.t1", instance.Atoms[0].AtomId);
        Assert.Equal("atom.ember-power.fire.t1", instance.Atoms[1].AtomId);
    }

    [Fact]
    public void PowerJson_stays_null_on_every_produced_atom()
    {
        // A3's own guard — power is backfilled later (E9), never computed on this path.
        Seed(new AffixRow("affix.ember", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.ember-power.fire.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.no-power", Kind = ContainerKind.Item, PrefixRolls = 1,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
            Pool = new[] { new ContainerPoolRow("affix.ember", 100) },
        };

        var instance = Compose(c, 1);

        Assert.All(instance.Atoms, a => Assert.Null(a.PowerJson));
    }

    [Fact]
    public void A_slot_bearing_affix_the_old_Draw_could_never_expand_resolves_through_Compose()
    {
        // The whole point of T3.6: an affix Instantiator.Draw would THROW NotSupportedException for
        // (a slot ref) resolves cleanly here, because Compose calls Resolver.Resolve, not Draw.
        Seed(new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.ember-power.$E1"),
        }));
        var c = new ContainerRow
        {
            ContainerId = "item.slot", Kind = ContainerKind.Item, PrefixRolls = 1,
            MinTier = 1, MaxTier = 1,
            Pool = new[] { new ContainerPoolRow("affix.elemental", 100, "g.elemental") },
        };

        var instance = Compose(c, 5);

        var atomId = Assert.Single(instance.Atoms).AtomId;
        Assert.StartsWith("atom.ember-power.", atomId, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_scale_applies_to_both_the_core_and_the_drawn_pool()
    {
        var c = new ContainerRow
        {
            ContainerId = "item.scaled", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
        };

        // PinTheta (20) is the pin: contentScale(20) == 1.000 exactly, so a non-pin theta must move
        // the frozen value away from the raw authored 45.
        var pinned = Compose(c, 1, theta: PinTheta);
        var scaled = Compose(c, 1, theta: PinTheta + 50);

        var pinnedAmount = System.Text.Json.JsonDocument.Parse(pinned.Atoms[0].ValuesJson)
            .RootElement.GetProperty("amount").GetInt32();
        var scaledAmount = System.Text.Json.JsonDocument.Parse(scaled.Atoms[0].ValuesJson)
            .RootElement.GetProperty("amount").GetInt32();

        Assert.Equal(45, pinnedAmount);
        Assert.NotEqual(pinnedAmount, scaledAmount);
    }

    [Fact]
    public void Same_container_revision_seed_and_variant_reproduces_identically()
    {
        Seed(new AffixRow("affix.roll", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.roll.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.repro", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.roll", 100) },
        };
        var variant = new VariantShift("blessed", 0, 1, 0, false);

        var a = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 77, PinTheta, Tuning,
            out var instA, variant, catalogRevision: 3);
        var b = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 77, PinTheta, Tuning,
            out var instB, variant, catalogRevision: 3);

        Assert.True(a.IsOk); Assert.True(b.IsOk);
        Assert.Equal(instA!.ContentFingerprint(), instB!.ContentFingerprint());
    }

    /// <summary>E30 (spec-channel-pool.md §5 test 2), the literal wording — "the same seed replays
    /// byte-identically, asserted over ContentFingerprint()" — proven at the INSTANCE layer this time
    /// (`ResolverTests.cs` proves the same fact at the resolver layer directly), now that
    /// <see cref="InstanceProducer.Compose"/>'s own signature threads <c>lookupPool</c> through to
    /// <see cref="Resolver.Resolve"/>.</summary>
    [Fact]
    public void A_pooled_channel_atom_reproduces_identically_over_ContentFingerprint()
    {
        var pool = new ChannelPoolRow("pool.test.repro", null, new[]
        {
            new ChannelPoolMember("chan.a", 1000),
            new ChannelPoolMember("chan.b", 1000),
            new ChannelPoolMember("chan.c", 1000),
        });
        ChannelPoolRow? LookupPool(string id) => id == pool.PoolId ? pool : null;

        Seed(new AffixRow("affix.pooled-repro", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.pooled-repro.t1") }));
        var pooledAtom = new AtomRow
        {
            AtomId = "atom.pooled-repro.t1", KindId = "stat.derived", FamilyId = "atom.pooled-repro", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.repro","count":1},"op":"flat","amount":{"min":10,"max":200,"roll":"onInstantiate"}}""",
        };
        Catalog[pooledAtom.AtomId] = pooledAtom;
        var c = new ContainerRow
        {
            ContainerId = "item.pooled-instance-repro", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.pooled-repro", 100) },
        };

        var a = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 55, PinTheta, Tuning,
            out var instA, lookupPool: LookupPool);
        var b = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 55, PinTheta, Tuning,
            out var instB, lookupPool: LookupPool);

        Assert.True(a.IsOk, a.ToString()); Assert.True(b.IsOk, b.ToString());
        Assert.Equal(instA!.ContentFingerprint(), instB!.ContentFingerprint());

        // And the pool actually resolved — a concrete string channel, not the raw pool-object JSON.
        var resolvedChannel = System.Text.Json.JsonDocument.Parse(instA.Atoms.Last().ValuesJson)
            .RootElement.GetProperty("channel");
        Assert.Equal(System.Text.Json.JsonValueKind.String, resolvedChannel.ValueKind);
    }

    [Fact]
    public void A_bad_container_is_rejected_and_composes_no_instance()
    {
        var c = new ContainerRow { ContainerId = "item.bad", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.nope.t1") } };

        var r = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instance);

        Assert.False(r.IsOk);
        Assert.Null(instance);
    }

    // ---- WAVE F2.1 (creature-standalone, 2026-09-07): forced pool picks -------------------------------

    [Fact]
    public void A_legal_forced_pick_appears_verbatim_and_shrinks_SuffixRolls_first()
    {
        // A single real (weight 100) pool row, SuffixRolls = 1: if the shrink did NOT happen,
        // Resolver.Resolve would draw this exact row again with 100% certainty (it is the pool's
        // only member), producing 2 atoms. Exactly 1 atom proves SuffixRolls shrank to 0.
        Seed(new AffixRow("affix.forced", AffixClass.Suffix, new[] { new AffixRefRow(1, "atom.ember-power.fire.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.forced-shrink", Kind = ContainerKind.Item, SuffixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.forced", 100, "g.a") },
        };
        var picks = new[] { new ForcedPoolPick("affix.forced",
            new[] { new InstanceAtomRow(1, "atom.ember-power.fire.t1", "{\"amount\":5}") }) };

        var r = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instance, forcedPicks: picks);

        Assert.True(r.IsOk, r.ToString());
        var atom = Assert.Single(instance!.Atoms);
        Assert.Equal("atom.ember-power.fire.t1", atom.AtomId);
        Assert.Equal("{\"amount\":5}", atom.ValuesJson);
    }

    [Fact]
    public void Two_forced_picks_shrink_SuffixRolls_then_PrefixRolls()
    {
        Seed(
            new AffixRow("affix.forced-a", AffixClass.Suffix, new[] { new AffixRefRow(1, "atom.ember-power.fire.t1") }),
            new AffixRow("affix.forced-b", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.ember-power.ice.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.forced-both", Kind = ContainerKind.Item, PrefixRolls = 1, SuffixRolls = 1,
            Pool = new[]
            {
                new ContainerPoolRow("affix.forced-a", 100, "g.a"),
                new ContainerPoolRow("affix.forced-b", 100, "g.b"),
            },
        };
        var picks = new[]
        {
            new ForcedPoolPick("affix.forced-a", new[] { new InstanceAtomRow(1, "atom.ember-power.fire.t1", "{\"amount\":5}") }),
            new ForcedPoolPick("affix.forced-b", new[] { new InstanceAtomRow(1, "atom.ember-power.ice.t1", "{\"amount\":5}") }),
        };

        var r = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instance, forcedPicks: picks);

        // Both budgets exhausted by the two forced picks — Resolver.Resolve draws nothing further
        // (both real, weight-100 rows would otherwise be drawn with certainty), so exactly the two
        // forced atoms appear, nothing more.
        Assert.True(r.IsOk, r.ToString());
        Assert.Equal(2, instance!.Atoms.Count);
        Assert.Contains(instance.Atoms, a => a.AtomId == "atom.ember-power.fire.t1");
        Assert.Contains(instance.Atoms, a => a.AtomId == "atom.ember-power.ice.t1");
    }

    [Fact]
    public void A_forced_pick_naming_an_affix_that_is_not_in_this_containers_own_pool_still_succeeds()
    {
        // Correction, 2026-09-07: inheritance is cross-species by design (a sacrifice's own species
        // pool feeding a DIFFERENT output species' container) — real content confirms species pools
        // never share affix ids, so requiring pool membership on the TARGET would refuse nearly every
        // real inheritance pick. Legitimacy is the caller's job (F2.4: source only from a real
        // specimen's real roll), never Compose's own to judge.
        // A valid, real pool member of its OWN — required so ContainerValidator.Validate accepts the
        // container at all — that has nothing to do with the forced pick's own affix id.
        Seed(new AffixRow("affix.own-species-thing", AffixClass.Suffix, new[] { new AffixRefRow(1, "atom.ember-power.ice.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.forced-cross-species", Kind = ContainerKind.Item, SuffixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.own-species-thing", 100, "g.a") },
        };
        var picks = new[] { new ForcedPoolPick("affix.from-a-different-species",
            new[] { new InstanceAtomRow(1, "atom.ember-power.fire.t1", "{\"amount\":5}") }) };

        var r = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instance, forcedPicks: picks);

        Assert.True(r.IsOk, r.ToString());
        var atom = Assert.Single(instance!.Atoms);
        Assert.Equal("atom.ember-power.fire.t1", atom.AtomId);
    }

    [Fact]
    public void A_forced_pick_count_exceeding_the_total_roll_budget_is_refused_before_any_roll()
    {
        Seed(new AffixRow("affix.forced", AffixClass.Suffix, new[] { new AffixRefRow(1, "atom.ember-power.fire.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.forced-over-budget", Kind = ContainerKind.Item, SuffixRolls = 1, // total budget 1
            Pool = new[] { new ContainerPoolRow("affix.forced", 100, "g.a") },
        };
        var picks = new[]
        {
            new ForcedPoolPick("affix.forced", new[] { new InstanceAtomRow(1, "atom.ember-power.fire.t1", "{\"amount\":5}") }),
            new ForcedPoolPick("affix.forced", new[] { new InstanceAtomRow(1, "atom.ember-power.fire.t1", "{\"amount\":5}") }),
        };

        var r = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instance, forcedPicks: picks);

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, r.Reason);
        Assert.Contains("fusion-inherit.exceeds-roll-budget", r.Detail);
        Assert.Null(instance);
    }

    [Fact]
    public void Remaining_slots_still_roll_normally_after_a_forced_pick_on_an_otherwise_unchanged_container()
    {
        // Two real (weight 100), different-group pool rows, SuffixRolls = 2 (validator requires
        // rolls <= drawable groups, so both must be real). Forcing one pick shrinks the budget to 1
        // — Resolver.Resolve still draws exactly one MORE atom from the unmodified pool (either row
        // is a legal outcome; which one depends on the seed and is not what this test asserts). A
        // total of 2 proves the shrink neither over- nor under-shrunk: 1 would mean Resolver drew
        // nothing (over-shrunk), 3 would mean it still saw the original, unshrunk budget.
        Seed(
            new AffixRow("affix.forced", AffixClass.Suffix, new[] { new AffixRefRow(1, "atom.ember-power.fire.t1") }),
            new AffixRow("affix.other", AffixClass.Suffix, new[] { new AffixRefRow(1, "atom.ember-power.ice.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.forced-plus-remaining", Kind = ContainerKind.Item, SuffixRolls = 2,
            Pool = new[]
            {
                new ContainerPoolRow("affix.forced", 100, "g.a"),
                new ContainerPoolRow("affix.other", 100, "g.b"),
            },
        };
        var picks = new[] { new ForcedPoolPick("affix.forced",
            new[] { new InstanceAtomRow(1, "atom.ember-power.fire.t1", "{\"amount\":5}") }) };

        var r = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instance, forcedPicks: picks);

        Assert.True(r.IsOk, r.ToString());
        Assert.Equal(2, instance!.Atoms.Count);
        Assert.Contains(instance.Atoms, a => a.AtomId == "atom.ember-power.fire.t1"); // the forced pick
    }

    [Fact]
    public void Zero_forced_picks_reproduces_todays_exact_output_byte_for_byte()
    {
        Seed(new AffixRow("affix.ember", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.ember-power.fire.t1") }));
        var c = new ContainerRow
        {
            ContainerId = "item.no-forced", Kind = ContainerKind.Item, PrefixRolls = 1,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
            Pool = new[] { new ContainerPoolRow("affix.ember", 100) },
        };

        var withNull = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instA, forcedPicks: null);
        var withEmpty = InstanceProducer.Compose(c, LookupAtom, LookupAffix, DomainMembers, 1, PinTheta, Tuning,
            out var instB, forcedPicks: Array.Empty<ForcedPoolPick>());

        Assert.True(withNull.IsOk); Assert.True(withEmpty.IsOk);
        Assert.Equal(instA!.ContentFingerprint(), instB!.ContentFingerprint());
    }
}
