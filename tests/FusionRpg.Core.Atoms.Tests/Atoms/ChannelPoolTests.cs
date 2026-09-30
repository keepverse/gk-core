using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E30 (spec-channel-pool.md): layer 2 — "element power of Y, Y is a pool of [6 type of element]."
/// This module's own scope, verified against the real code rather than assumed: the pool artifact,
/// the schema addition, validation (§3.3's five refusals), and pricing (§3.4) — all provable Core-side.
///
/// <para><b>NOT covered here (a real, stated gap, not an oversight):</b> effect-pipeline module 2's own
/// resolve step — turning a pool reference into a concrete channel at roll time — does not exist yet.
/// Reading <c>InjectorEffectActionSink.cs:93</c> confirms the executor reads <c>channel</c> as a single
/// string; nothing anywhere reads an array-valued channel, so <c>count &gt; 1</c>'s execution semantics
/// (spec §3.2: "+15% to all resistances becomes one atom") have no owner yet. Tests 1-3 of the spec's
/// own §5 (which need a real resolve step) and acceptance criterion 5 (byte-identical reroll) are
/// therefore not built — a genuine cross-module dependency the spec itself declares
/// ("E30 declares a dependency on effect-pipeline module 2"), not silently skipped.
/// </summary>
public class ChannelPoolTests
{
    /// <summary>The <c>data</c> directory: the pack holds gk-data/packs/fusion/data/seed and gk-data/packs/fusion/data/generated, gk-core
    /// holds gk-core/data/tuning. Callers join "seed" or "tuning" onto this with no "data" segment,
    /// so the segment has to survive the change of repository - an earlier version returned
    /// the pack root and every read became &lt;pack&gt;/seed/... and missed.
    /// FamilyExpansionTests is the one caller that reads TUNING through this helper, and it
    /// must not: gk-core/data/tuning is in gk-core. That single site is why this helper could not
    /// simply be collapsed.</summary>
    static string FindDataDir() => Path.Combine(KeepverseRoots.Content(), "data");

    static IReadOnlyList<ChannelPoolRow> ShippedPools()
    {
        var path = Path.Combine(FindDataDir(), "seed", "channel-pools", "pools.v1.json");
        var read = ChannelPoolFile.TryParse(File.ReadAllText(path), out var pools);
        Assert.True(read.IsOk, read.Detail);
        return pools;
    }

    // ---- test 4: pricing -------------------------------------------------------------------------

    [Fact]
    public void Price_of_a_pooled_atom_equals_the_weighted_mean_times_count_hand_worked()
    {
        // Hand-worked fixture: a 3-member pool, unequal weights, unequal coefficients (via distinct
        // channels sharing the SAME stat.derived coefficient row today — spec §3.4's own note that
        // every declared pool's spread is exactly 0 until E44 fits per-channel coefficients — so this
        // fixture asserts the ARITHMETIC using the one shipped coefficient, not a hypothetical spread).
        var pool = new ChannelPoolRow("pool.test.three", null, new[]
        {
            new ChannelPoolMember("combat.power.fire", 1000),
            new ChannelPoolMember("combat.power.ice", 2000),
            new ChannelPoolMember("combat.power.air", 1000),
        });
        ChannelPoolRow? Lookup(string id) => id == pool.PoolId ? pool : null;

        var atom = new AtomRow
        {
            AtomId = "atom.pooled-test.t1", KindId = "stat.derived", FamilyId = "atom.pooled-test", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.three","count":2},"op":"flat","amount":100}""",
        };

        var pooledPrice = CostFunction.Price(atom, PowerTables.Authored(), lookupPool: Lookup);
        Assert.True(pooledPrice.Ok, pooledPrice.Verdict.Reason);

        // Every member here shares stat.derived's one channel-less coefficient row (§3.4's own note),
        // so the weighted mean equals any single member's price, and count=2 doubles it exactly.
        var single = new AtomRow
        {
            AtomId = "atom.pooled-test-single.t1", KindId = "stat.derived", FamilyId = "atom.pooled-test-single", Variant = "", Tier = 1,
            ParamsJson = """{"channel":"combat.power.fire","op":"flat","amount":100}""",
        };
        var singlePrice = CostFunction.Price(single, PowerTables.Authored());
        Assert.True(singlePrice.Ok, singlePrice.Verdict.Reason);

        Assert.Equal(singlePrice.Power.Offense * 2, pooledPrice.Power.Offense);
    }

    [Fact]
    public void Price_of_a_pooled_atom_with_no_lookupPool_supplied_is_unpriced_never_a_crash()
    {
        var atom = new AtomRow
        {
            AtomId = "atom.pooled-nolookup.t1", KindId = "stat.derived", FamilyId = "atom.pooled-nolookup", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.element-power","count":1},"op":"flat","amount":100}""",
        };

        var priced = CostFunction.Price(atom, PowerTables.Authored()); // lookupPool omitted

        Assert.False(priced.Ok);
        Assert.Equal(PowerVector.Zero, priced.Power);
    }

    [Fact]
    public void Price_of_an_unknown_pool_reference_is_unpriced_never_a_crash()
    {
        var atom = new AtomRow
        {
            AtomId = "atom.pooled-unknown.t1", KindId = "stat.derived", FamilyId = "atom.pooled-unknown", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.does-not-exist","count":1},"op":"flat","amount":100}""",
        };

        var priced = CostFunction.Price(atom, PowerTables.Authored(), lookupPool: _ => null);

        Assert.False(priced.Ok);
        Assert.Contains("unknown pool", priced.Verdict.Reason, StringComparison.Ordinal);
    }

    // ---- test 5: planted violation — unregistered channel member ------------------------------------

    [Fact]
    public void PlantedViolation_a_pool_member_naming_an_unregistered_channel_is_refused_at_load()
    {
        var pool = new ChannelPoolRow("pool.test.bad-member", null, new[]
        {
            new ChannelPoolMember("combat.power.fire", 1000),
            new ChannelPoolMember("crit.rat", 1000), // "crit.rate" was meant — the exact E29 planted typo
        });
        ChannelPoolRow? Lookup(string id) => id == pool.PoolId ? pool : null;

        var atom = new AtomRow
        {
            AtomId = "atom.bad-member.t1", KindId = "stat.derived", FamilyId = "atom.bad-member", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.test.bad-member","count":1},"op":"flat","amount":100}""",
        };

        var r = AtomRowValidator.Validate(atom, composeKindOf: DerivedComposeKindOf, lookupPool: Lookup);

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("crit.rat", r.Detail, StringComparison.Ordinal);
    }

    // ---- test 6: planted violation — count exceeds members, no allowRepeat -------------------------

    [Fact]
    public void PlantedViolation_count_seven_on_a_six_member_pool_without_allowRepeat_is_refused_not_clamped()
    {
        var atom = new AtomRow
        {
            AtomId = "atom.overcount.t1", KindId = "stat.derived", FamilyId = "atom.overcount", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.element-power","count":7},"op":"flat","amount":100}""",
        };
        var pools = ShippedPools().ToDictionary(p => p.PoolId, StringComparer.Ordinal);

        var r = AtomRowValidator.Validate(atom, composeKindOf: DerivedComposeKindOf,
            lookupPool: id => pools.TryGetValue(id, out var p) ? p : null);

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("exceeds", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Count_seven_WITH_allowRepeat_on_a_six_member_pool_validates()
    {
        var atom = new AtomRow
        {
            AtomId = "atom.overcount-ok.t1", KindId = "stat.derived", FamilyId = "atom.overcount-ok", Variant = "", Tier = 1,
            Name = "Overcount OK", ParamsJson = """{"channel":{"pool":"pool.element-power","count":7,"allowRepeat":true},"op":"flat","amount":100}""",
        };
        var pools = ShippedPools().ToDictionary(p => p.PoolId, StringComparer.Ordinal);

        var r = AtomRowValidator.Validate(atom, composeKindOf: DerivedComposeKindOf,
            lookupPool: id => pools.TryGetValue(id, out var p) ? p : null);

        Assert.True(r.IsOk, r.ToString());
    }

    [Fact]
    public void PlantedViolation_count_zero_is_refused()
    {
        var atom = new AtomRow
        {
            AtomId = "atom.zerocount.t1", KindId = "stat.derived", FamilyId = "atom.zerocount", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.element-power","count":0},"op":"flat","amount":100}""",
        };
        var pools = ShippedPools().ToDictionary(p => p.PoolId, StringComparer.Ordinal);

        var r = AtomRowValidator.Validate(atom, composeKindOf: DerivedComposeKindOf,
            lookupPool: id => pools.TryGetValue(id, out var p) ? p : null);

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void PlantedViolation_an_unknown_pool_id_is_refused_naming_it()
    {
        var atom = new AtomRow
        {
            AtomId = "atom.unknownpool.t1", KindId = "stat.derived", FamilyId = "atom.unknownpool", Variant = "", Tier = 1,
            ParamsJson = """{"channel":{"pool":"pool.nonexistent","count":1},"op":"flat","amount":100}""",
        };

        var r = AtomRowValidator.Validate(atom, composeKindOf: DerivedComposeKindOf, lookupPool: _ => null);

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("pool.nonexistent", r.Detail, StringComparison.Ordinal);
    }

    // ---- test 7: the concrete form is unchanged -----------------------------------------------------

    [Fact]
    public void A_concrete_channel_atom_validates_prices_and_hashes_exactly_as_before_this_module()
    {
        var atom = new AtomRow
        {
            AtomId = "atom.concrete-unchanged.t1", KindId = "stat.derived", FamilyId = "atom.concrete-unchanged", Variant = "", Tier = 1,
            Name = "Concrete Unchanged", ParamsJson = """{"channel":"combat.power.fire","op":"flat","amount":100}""",
        };
        var pools = ShippedPools().ToDictionary(p => p.PoolId, StringComparer.Ordinal);

        var r = AtomRowValidator.Validate(atom, composeKindOf: DerivedComposeKindOf,
            lookupPool: id => pools.TryGetValue(id, out var p) ? p : null);
        Assert.True(r.IsOk, r.ToString());

        var priced = CostFunction.Price(atom, PowerTables.Authored(), lookupPool: id => pools.TryGetValue(id, out var p) ? p : null);
        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.NotEqual(PowerVector.Zero, priced.Power);
    }

    // ---- test 8: overflow throws --------------------------------------------------------------------

    [Fact]
    public void PricePooled_widens_before_multiplying_and_throws_on_overflow_rather_than_wrapping()
    {
        // A pool whose single member's price, multiplied by an astronomically large weight, would
        // overflow `long` inside the weighted-sum accumulation — proves the arithmetic is `checked`,
        // per AGENTS.md's numeric rule, rather than silently wrapping to a nonsense negative price.
        var pool = new ChannelPoolRow("pool.test.overflow", null, new[]
        {
            new ChannelPoolMember("combat.power.fire", int.MaxValue),
        });
        ChannelPoolRow? Lookup(string id) => id == pool.PoolId ? pool : null;

        var atom = new AtomRow
        {
            AtomId = "atom.overflow-price.t1", KindId = "stat.derived", FamilyId = "atom.overflow-price", Variant = "", Tier = 1,
            ParamsJson = $$"""{"channel":{"pool":"pool.test.overflow","count":{{int.MaxValue}}},"op":"flat","amount":100}""",
        };

        Assert.Throws<OverflowException>(() => CostFunction.Price(atom, PowerTables.Authored(), lookupPool: Lookup));
    }

    // ---- test 9: the declared pool set reconciles against the real 98 families ----------------------

    [Fact]
    public void The_twelve_declared_pools_equal_the_element_expanded_stems_of_the_98_families_minus_the_two_unregistered_ones()
    {
        var dir = Path.Combine(FindDataDir(), "seed", "items", "affix-families");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.NotEmpty(files);

        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var kindId = entry.GetProperty("kindId").GetString();
                if (kindId is not ("stat.derived" or "stat.modify")) continue;
                if (!entry.TryGetProperty("variants", out var variants) ||
                    !variants.TryGetProperty("generate", out var genEl)) continue;
                var gen = genEl.GetString() ?? "";
                if (!gen.Contains("element", StringComparison.Ordinal)) continue;
                if (!entry.TryGetProperty("params", out var p) || !p.TryGetProperty("channel", out var chEl)) continue;
                var channel = chEl.GetString() ?? "";
                if (!channel.Contains("{variant}", StringComparison.Ordinal)) continue;

                var stem = channel.Replace(".{variant}", "", StringComparison.Ordinal)
                                   .Replace("{variant}", "", StringComparison.Ordinal);
                stems.Add(stem);
            }
        }

        // The stem count (14 today) is a growing content population, never pinned (population-pin
        // SE3.3, 2026-09-19). Two of them — combat.power.pierce and combat.power.overflow — are NOT
        // registered channel families (E29's guard refuses the three families naming them before a
        // pool is ever consulted, per §6.1) and correctly get no pool; that membership is the real
        // contract, checked below.
        Assert.NotEmpty(stems);
        var unregistered = new[] { "combat.power.pierce", "combat.power.overflow" };
        foreach (var u in unregistered) Assert.Contains(u, stems);

        var expectedPoolIds = stems.Except(unregistered)
            .Select(stem => "pool.element-" + stem.Replace("combat.", "", StringComparison.Ordinal).Replace('.', '-'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var actualPoolIds = ShippedPools().Select(p => p.PoolId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // The count is fully implied by the element-wise equality below -- never pinned separately.
        Assert.Equal(expectedPoolIds, actualPoolIds);
    }

    // ---- test 10: pool.element-power is 6 members, omni absent ---------------------------------------

    [Fact]
    public void Pool_element_power_has_six_members_and_omni_is_absent()
    {
        var pool = ShippedPools().Single(p => p.PoolId == "pool.element-power");

        Assert.Equal(6, pool.Members.Count);
        Assert.DoesNotContain(pool.Members, m => m.Channel.EndsWith(".omni", StringComparison.Ordinal));
        Assert.Contains(pool.Members, m => m.Channel == "combat.power.fire");
        Assert.Contains(pool.Members, m => m.Channel == "combat.power.ice");
        Assert.Contains(pool.Members, m => m.Channel == "combat.power.air");
        Assert.Contains(pool.Members, m => m.Channel == "combat.power.earth");
        Assert.Contains(pool.Members, m => m.Channel == "combat.power.light");
        Assert.Contains(pool.Members, m => m.Channel == "combat.power.dark");
    }

    [Fact]
    public void Every_one_of_the_twelve_shipped_pools_has_exactly_six_members_no_omni()
    {
        foreach (var pool in ShippedPools())
        {
            Assert.Equal(6, pool.Members.Count);
            Assert.DoesNotContain(pool.Members, m => m.Channel.EndsWith(".omni", StringComparison.Ordinal));
        }
    }

    // ---- every shipped pool member is a real registered channel --------------------------------------

    [Fact]
    public void Every_member_of_every_shipped_pool_is_a_registered_derived_channel()
    {
        var registry = DerivedStatRegistry.CreateDefault();
        foreach (var pool in ShippedPools())
        {
            foreach (var member in pool.Members)
            {
                Assert.True(registry.TryResolveChannel(member.Channel, out _),
                    $"{pool.PoolId}: member '{member.Channel}' is not a registered derived channel");
            }
        }
    }

    // ---- ChannelRefJson ------------------------------------------------------------------------------

    [Fact]
    public void ChannelRefJson_reads_a_plain_string_as_concrete()
    {
        using var doc = JsonDocument.Parse("""{"channel":"combat.power.fire"}""");
        var read = ChannelRefJson.TryRead(doc.RootElement.GetProperty("channel"), out var channelRef);

        Assert.True(read.IsOk);
        Assert.False(channelRef.IsPool);
        Assert.Equal("combat.power.fire", channelRef.Concrete);
    }

    [Fact]
    public void ChannelRefJson_reads_a_pool_object_with_default_count_one_and_allowRepeat_false()
    {
        using var doc = JsonDocument.Parse("""{"channel":{"pool":"pool.element-power"}}""");
        var read = ChannelRefJson.TryRead(doc.RootElement.GetProperty("channel"), out var channelRef);

        Assert.True(read.IsOk);
        Assert.True(channelRef.IsPool);
        Assert.Equal("pool.element-power", channelRef.PoolId);
        Assert.Equal(1, channelRef.Count);
        Assert.False(channelRef.AllowRepeat);
    }

    [Fact]
    public void ChannelRefJson_reads_an_explicit_count_and_allowRepeat()
    {
        using var doc = JsonDocument.Parse("""{"channel":{"pool":"pool.element-power","count":6,"allowRepeat":true}}""");
        var read = ChannelRefJson.TryRead(doc.RootElement.GetProperty("channel"), out var channelRef);

        Assert.True(read.IsOk);
        Assert.Equal(6, channelRef.Count);
        Assert.True(channelRef.AllowRepeat);
    }

    static DerivedComposeKind? DerivedComposeKindOf(string channel)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        return registry.TryResolveChannel(channel, out var def) ? def.Compose : null;
    }
}
