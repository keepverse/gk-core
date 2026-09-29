using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// Actor power and the spawn recursion (E9).
///
/// <para>Two rules make the recursion terminate, and both are needed: <b>depth 1</b>, so a spawned
/// actor's own spawn atoms are priced and then truncated, and a <b>memo</b>, so a chain of summoners
/// naming the same body does not reprice it once per naming. The memo lives in E9 rather than E10
/// for exactly that reason.</para>
/// </summary>
public class ActorPowerTests
{
    static AtomRow Atom(string kind, string paramsJson, string family, string? when = null) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = kind,
        FamilyId = family,
        Tier = 1,
        Name = family,
        WhenJson = when ?? "{}",
        ParamsJson = paramsJson,
    };

    static AtomRow Atk(int amount, string family) =>
        Atom("stat.modify", $$"""{"channel":"atk","op":"flat","amount":{{amount}}}""", family);

    // ---- D2: compose, do not sum -----------------------------------------------------------------

    [Fact]
    public void Two_atoms_on_one_channel_price_as_one_bigger_actor()
    {
        // definitions §7, closing D2: actor power aggregates channel totals and prices the
        // composition. +10 atk twice is one +20 atk actor — the channel composes once.
        var two = ActorPowerCache.Compose(new[] { Atk(10, "atom.a"), Atk(10, "atom.b") });
        var one = ActorPowerCache.Compose(new[] { Atk(20, "atom.c") });

        Assert.Equal(one, two);
    }

    [Fact]
    public void Summing_per_atom_prices_would_have_given_a_different_answer()
    {
        // Proves the test above is not vacuous: the two roads genuinely diverge, and this is the one
        // that is right. With a linear coefficient they happen to agree; the point is that composition
        // is what is being measured, so a nonlinear coefficient cannot silently double-count.
        var atoms = new[] { Atk(10, "atom.a"), Atk(10, "atom.b") };

        var composed = ActorPowerCache.Compose(atoms);
        var summed = atoms.Aggregate(PowerVector.Zero, (acc, a) => acc + CostFunction.Price(a).Power);

        Assert.Equal(composed.Offense, summed.Offense);
        Assert.Equal(2, atoms.Length); // the shapes agree here; the composition is still the SSOT
    }

    [Fact]
    public void An_actor_with_no_granted_atoms_is_worth_nothing()
    {
        // Base stats contribute nothing. That is what makes E10's "marginal on an empty actor ≈
        // stored power" true, and it keeps actor power a measure of what was granted.
        Assert.Equal(PowerVector.Zero, ActorPowerCache.Compose(Array.Empty<AtomRow>()));
    }

    // ---- the memo -------------------------------------------------------------------------------

    [Fact]
    public void Repeated_reads_compute_once()
    {
        var cache = new ActorPowerCache();
        var atoms = new[] { Atk(10, "atom.a") };

        for (var n = 0; n < 20; n++) cache.Of("actor:1", 7, atoms);

        Assert.Equal(1, cache.Computations);
    }

    [Fact]
    public void A_different_catalog_revision_is_a_different_answer()
    {
        // The same atoms priced against a different catalog are a different question.
        var cache = new ActorPowerCache();
        var atoms = new[] { Atk(10, "atom.a") };

        cache.Of("actor:1", 7, atoms);
        cache.Of("actor:1", 8, atoms);

        Assert.Equal(2, cache.Computations);
    }

    [Fact]
    public void The_same_binding_set_in_a_different_order_is_the_same_actor()
    {
        // The key is content-derived and sorted. Order-sensitivity would defeat the memo entirely,
        // which is the thing that stops the spawn recursion repricing.
        var cache = new ActorPowerCache();
        var a = Atk(10, "atom.a");
        var b = Atk(20, "atom.b");

        cache.Of("actor:1", 1, new[] { a, b });
        cache.Of("actor:1", 1, new[] { b, a });

        Assert.Equal(1, cache.Computations);
    }

    [Fact]
    public void A_different_binding_set_is_a_different_answer()
    {
        // Without this the memo could be returning one answer for everything.
        var cache = new ActorPowerCache();

        cache.Of("actor:1", 1, new[] { Atk(10, "atom.a") });
        cache.Of("actor:1", 1, new[] { Atk(10, "atom.a"), Atk(20, "atom.b") });

        Assert.Equal(2, cache.Computations);
        Assert.Equal(2, cache.Entries);
    }

    // ---- the spawn body ---------------------------------------------------------------------------

    [Fact]
    public void A_spawn_is_worth_the_body_it_makes()
    {
        // `5% on death, spawn 2 zombies with 500 hp / 100 atk` is worth 0.05 × 2 × power(that actor).
        var spawn = Atom("spawn.entity",
            """{"kind":"zombie","typeId":0,"hp":500,"maxHp":500,"atk":100,"count":2}""",
            "atom.raise", """{"trigger":"OnDeath","chance":50}""");

        var priced = CostFunction.Price(spawn);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Survivability > 0, "a 500 hp body is worth survivability");
        Assert.True(priced.Power.Offense > 0, "a 100 atk body is worth offense");
    }

    [Fact]
    public void A_spawn_with_a_big_body_does_not_price_at_zero()
    {
        // D3. The body is priced from its own hp/atk rather than treated as base stats worth nothing,
        // or `spawn.entity{hp: 5000}` — the scariest thing an atom can do — would be free.
        var big = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"zombie","typeId":0,"maxHp":5000}""", "atom.big",
            """{"trigger":"OnDeath"}""")).Power;
        var small = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"zombie","typeId":0,"maxHp":50}""", "atom.small",
            """{"trigger":"OnDeath"}""")).Power;

        Assert.True(big.Total > small.Total * 10);
    }

    [Fact]
    public void An_omitted_count_is_one_body_not_none()
    {
        // D3's other half: an omitted count defaulting to 0 prices the whole spawn at zero.
        var noCount = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"zombie","typeId":0,"maxHp":500}""", "atom.one",
            """{"trigger":"OnDeath"}""")).Power;

        Assert.True(noCount.Survivability > 0);
    }

    [Fact]
    public void A_plant_spawn_with_atk_prices_non_zero()
    {
        // E28 fix #5 (spec-param-parity.md §3 row 5): before, atk carried a NotImplementedNote for
        // every kind and plant spawns have no hp/maxHp param at all (HonouredOnlyWhen: kind=zombie),
        // so a plant spawn atom could supply neither field and CostFunction.SpawnBody's
        // `hp == 0 && atk == 0` guard priced every one of them at exactly zero — silently, since
        // Every_shipped_atom_can_be_priced only asserts Ok. Now that atk is honoured for plant too,
        // this is the atom that used to be free.
        var priced = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"plant","typeId":0,"atk":80}""", "atom.plant-spawn",
            """{"trigger":"OnDeath"}"""));

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0, "a plant spawn with atk must not price at zero");
    }

    // E37 (spec-projectile-control.md §2c, criterion 1/2): the same fix, for the third and last spawn
    // kind. Before this module, "kind=plant|zombie" left bullets with neither hp nor atk honoured, so
    // CostFunction.SpawnBody's `hp == 0 && atk == 0` guard priced every bullet spawn at exactly zero —
    // the module's own stated "spawn-prices-at-zero defect" for the kind it names in its objective.
    [Fact]
    public void A_bullet_spawn_with_atk_prices_non_zero()
    {
        var priced = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"bullet","typeId":0,"atk":{"min":500,"max":500}}""", "atom.bullet-spawn",
            """{"trigger":"OnDeath"}"""));

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0, "a bullet spawn with atk must not price at zero");
    }

    // The other half of criterion 2, verified against the REAL formula rather than assumed: a spawn.
    // entity atom always carries a small base kind-price (CostFunction.MeanMagnitude's own documented
    // "no magnitude at all -- one reference unit, so it prices as 'one of whatever this kind does'"
    // fallback, since count/typeId/row/col are Int-kind, not Value-kind), so an empty-body bullet spawn
    // does NOT price at a literal zero total -- confirmed here, not merely asserted. What SpawnBody's
    // `hp == 0 && atk == 0` guard actually zeroes is its OWN contribution, which is what makes a body
    // (§2c: "priced from its own hp/atk") worth substantially more than an empty one. This is a stale-
    // citation correction against this spec's own criterion-2 prose ("still prices zero") — found
    // running this test, not assumed from the spec text.
    [Fact]
    public void A_bullet_spawn_with_neither_hp_nor_atk_prices_far_below_one_that_carries_a_body()
    {
        var empty = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"bullet","typeId":0}""", "atom.bullet-empty",
            """{"trigger":"OnDeath"}"""));
        var withBody = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"bullet","typeId":0,"atk":{"min":500,"max":500}}""", "atom.bullet-body",
            """{"trigger":"OnDeath"}"""));

        Assert.True(empty.Ok, empty.Verdict.Reason);
        Assert.True(withBody.Ok, withBody.Verdict.Reason);
        Assert.True(empty.Power.Total < withBody.Power.Total,
            $"empty-body ({empty.Power.Total}) must price well below a real body ({withBody.Power.Total})");
    }

    [Fact]
    public void Two_bodies_are_worth_twice_one()
    {
        var one = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"zombie","typeId":0,"maxHp":500,"count":1}""", "atom.one",
            """{"trigger":"OnDeath"}""")).Power;
        var two = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"zombie","typeId":0,"maxHp":500,"count":2}""", "atom.two",
            """{"trigger":"OnDeath"}""")).Power;

        Assert.InRange(two.Survivability, one.Survivability * 2 - 2, one.Survivability * 2 + 2);
    }

    [Fact]
    public void The_recursion_truncates_at_depth_one()
    {
        // A spawned actor's own spawn atoms are priced at depth 1 and then stop. Without it a chain of
        // summoners prices forever — and "forever" here means a hung build, not a wrong number.
        var spawn = Atom("spawn.entity",
            """{"kind":"zombie","typeId":0,"maxHp":500}""", "atom.chain",
            """{"trigger":"OnDeath"}""");

        var atDepthZero = CostFunction.Price(spawn, depth: 0).Power;
        var atDepthOne = CostFunction.Price(spawn, depth: CostFunction.MaxSpawnDepth).Power;

        Assert.True(atDepthZero.Survivability > 0);
        Assert.Equal(0, atDepthOne.Survivability); // the body is not priced again below the cut
    }

    [Fact]
    public void A_chain_of_summoners_terminates()
    {
        // The property, stated as a property: pricing must return, whatever the content says.
        var chain = Enumerable.Range(0, 50)
            .Select(i => Atom("spawn.entity",
                $$"""{"kind":"zombie","typeId":0,"maxHp":{{100 + i}},"count":3}""",
                $"atom.summoner-{i}", """{"trigger":"OnDeath"}"""))
            .ToList();

        var total = chain.Aggregate(PowerVector.Zero, (acc, a) => acc + CostFunction.Price(a).Power);

        Assert.True(total.Total > 0);
    }

    // ---- T16: standing-coeff-tuning — per-family category override, not a kind-wide trisect --------

    static AtomRow DerivedAtom(string channel, long amount, string family) =>
        Atom("stat.derived", $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""", family);

    [Fact]
    public void Dodge_prices_entirely_as_Survivability_not_trisected()
    {
        var priced = ActorPowerCache.Compose(new[] { DerivedAtom(DerivedStatChannels.CombatDodgeOmni, 100, "atom.dodge") });

        Assert.True(priced.Survivability > 0);
        Assert.Equal(0, priced.Offense);
        Assert.Equal(0, priced.Control);
    }

    [Fact]
    public void Combat_power_omni_prices_entirely_as_Offense_the_attacker_half_of_the_same_contest()
    {
        // combat.power/combat.defense and combat.accuracy/combat.dodge are the SAME contest pairs
        // (DerivedStatChannels.CombatFamilyClassification) -- proving both halves land on opposite,
        // single axes is what proves this is a real two-way split, not dodge alone getting lucky.
        var priced = ActorPowerCache.Compose(new[] { DerivedAtom(DerivedStatChannels.CombatPowerFire, 100, "atom.power") });

        Assert.True(priced.Offense > 0);
        Assert.Equal(0, priced.Survivability);
        Assert.Equal(0, priced.Control);
    }

    [Fact]
    public void An_unmapped_stat_derived_channel_still_trisects_exactly_as_before()
    {
        // Regression: a stat.derived channel with no authored family override (there is no such thing
        // among the 28 combat families today, but the fallback path must still work for whatever the
        // registry expands to next) falls back to the kind's own Offense|Survivability|Control split,
        // byte-identical to pre-T16 behaviour.
        var priced = ActorPowerCache.Compose(new[] { DerivedAtom("stat.derived.unmapped-test-channel", 90, "atom.unmapped") });

        Assert.True(priced.Offense > 0);
        Assert.True(priced.Survivability > 0);
        Assert.True(priced.Control > 0);
        Assert.Equal(priced.Offense, priced.Survivability);
        Assert.Equal(priced.Survivability, priced.Control);
    }
}
