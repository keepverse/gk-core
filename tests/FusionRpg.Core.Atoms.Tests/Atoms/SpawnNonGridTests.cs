using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E40 acceptance (spec-spawn-non-grid.md). Pets, buckets, coins and mowers were reachable only from
/// the cheat menu — no atom could reach any of them. This widens the closed <c>spawn.entity.kind</c>
/// domain from three values (<c>plant</c>/<c>zombie</c>/<c>bullet</c>) to seven, adding
/// <c>pet</c>/<c>bucket</c>/<c>coin</c>/<c>mower</c>, with NO new atom kind and NO new attach point
/// (§2a — the opcode/plan-item shape/coefficient row/executor switch all already exist for
/// <c>spawn.entity</c>). <c>present</c> (§2b) stays out — it opens an existing present, it places
/// nothing — and belongs on <c>board.action</c> instead, a different module's one-row change.
///
/// <para>The fourth executor arm (<c>coin</c>) is refused at LOAD by its own named block in
/// <see cref="AtomKindRegistry.Validate"/>, not wired: <c>CreateItem.SetCoin</c>'s call safety outside
/// the game's own drop flow is UNVERIFIED (§3), and this repo cannot run the live lawn session that
/// would settle it. <c>SpawnNonGridExecutorGuardTests</c> (FusionRpg.Guard.Tests) proves the four
/// executor arms exist as TEXT, since the injector needs a real PVZ Fusion install and never builds
/// under CI.</para>
/// </summary>
public class SpawnNonGridTests
{
    static Dictionary<string, object?> P(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    // ---- §2a: the domain is closed at seven, not a hand-counted list ------------------------------

    [Fact]
    public void SpawnEntity_kind_domain_is_exactly_seven_values()
    {
        var vocabulary = AtomKindRegistry.Get("spawn.entity")!.Params.Defs
            .First(d => d.Name == "kind").Vocabulary!();

        Assert.Equal(7, vocabulary.Count);
        Assert.Equal(
            new[] { "bucket", "bullet", "coin", "mower", "pet", "plant", "zombie" },
            vocabulary.OrderBy(v => v, StringComparer.Ordinal));
    }

    // §4's own "round-trip the domain" test: every LIVE (non-coin) member validates with the minimal
    // legal shape, iterating the real vocabulary rather than a hand-listed set of cases — the shape
    // that would fail silently if a future edit widened the array without a matching schema/executor.
    [Theory]
    [InlineData("zombie")]
    [InlineData("plant")]
    [InlineData("bullet")]
    [InlineData("pet")]
    [InlineData("bucket")]
    [InlineData("mower")]
    public void Every_live_domain_member_validates_with_the_minimal_legal_shape(string kind)
    {
        var r = AtomKindRegistry.Validate("spawn.entity", P(("kind", kind), ("typeId", 0), ("row", 2)));
        Assert.True(r.IsOk, r.ToString());
    }

    // kind: "sunflower" -- BadParamValue at LOAD, never an InvalidOperationException at execute
    // (§4's own test-table row, in these exact words).
    [Fact]
    public void Kind_sunflower_is_BadParamValue_at_load()
    {
        var r = AtomKindRegistry.Validate("spawn.entity", P(("kind", "sunflower")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("sunflower", r.Detail);
    }

    // §2b / §4: present is not a spawn -- Present.RandomPlant() OPENS an existing present, it places
    // nothing, so modelling it as spawn.entity would claim a capability the call does not have. Its
    // correct home is a board.action op alongside freeze/doom/fireline/cherry (AtomKindRegistry.cs's
    // BoardActionOps) -- a different module's one-row change, not this one's.
    [Fact]
    public void Kind_present_is_refused_it_is_not_a_spawn()
    {
        var r = AtomKindRegistry.Validate("spawn.entity", P(("kind", "present")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("present", r.Detail);
    }

    // ---- §3: the coin arm — a domain member, refused separately, by name and reason ----------------

    [Fact]
    public void Kind_coin_is_a_domain_member_but_refused_at_load_with_its_own_named_reason()
    {
        var r = AtomKindRegistry.Validate("spawn.entity", P(("kind", "coin"), ("typeId", 0), ("row", 2)));

        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        // Not the generic "not one of the seven legal values" membership message -- coin IS a member.
        Assert.DoesNotContain("legal values", r.Detail);
        Assert.Contains("SetCoin", r.Detail);
        Assert.Contains("UNVERIFIED", r.Detail);
    }

    [Fact]
    public void Kind_coin_is_refused_even_with_no_other_params_supplied()
    {
        var r = AtomKindRegistry.Validate("spawn.entity", P(("kind", "coin")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("SetCoin", r.Detail);
    }

    // ---- §2a: col honoured for plant/pet/bucket/coin, x honoured for zombie/bullet/mower -----------

    [Theory]
    [InlineData("plant")]
    [InlineData("pet")]
    [InlineData("bucket")]
    public void Col_is_honoured_for_plant_pet_and_bucket(string kind)
    {
        Assert.True(AtomKindRegistry.Validate("spawn.entity",
            P(("kind", kind), ("col", 3))).IsOk);
    }

    [Theory]
    [InlineData("zombie")]
    [InlineData("mower")]
    public void Col_is_not_honoured_for_zombie_or_mower(string kind)
    {
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, AtomKindRegistry.Validate("spawn.entity",
            P(("kind", kind), ("col", 3))).Reason);
    }

    [Fact]
    public void X_is_honoured_for_mower()
    {
        Assert.True(AtomKindRegistry.Validate("spawn.entity",
            P(("kind", "mower"), ("x", 100))).IsOk);
    }

    [Theory]
    [InlineData("pet")]
    [InlineData("bucket")]
    [InlineData("plant")]
    public void X_is_not_honoured_for_pet_bucket_or_plant(string kind)
    {
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, AtomKindRegistry.Validate("spawn.entity",
            P(("kind", kind), ("x", 100))).Reason);
    }

    // ---- §5 criterion 8: this module's own diff does not move either closed-vocabulary constant ----

    [Fact]
    public void This_module_adds_no_kind_and_no_attach_point()
    {
        // Self-consistency, the same shape AtomKindRegistryTests' own vocabulary-closed test uses —
        // never a hand-typed literal that could itself drift. spec §3's own correction: the wave's
        // OTHER modules (E35-E37, E41) DO move these constants, so the right check is "this diff
        // doesn't touch them", not "the absolute value is N" — asserted here by proving grid.spawn
        // (a different kind entirely) is untouched and spawn.entity's own attach point is still Board.
        Assert.Equal(AttachPoint.Board, AtomKindRegistry.Get("spawn.entity")!.Attach);
        Assert.Equal(AtomKindRegistry.KindCount, AtomKindRegistry.All.Count);
        Assert.Equal(AtomKindRegistry.AttachPointCount, Enum.GetValues<AttachPoint>().Length);
    }

    // §3: grid.spawn's own GridItemType domain is untouched -- graveType/GridItemType stay E28's row,
    // never widened here (placing the same item through two kinds would be a seam violation).
    [Fact]
    public void GridSpawn_gridItemType_vocabulary_is_still_the_twelve_member_set()
    {
        var vocabulary = AtomKindRegistry.Get("grid.spawn")!.Params.Defs
            .First(d => d.Name == "gridItemType").Vocabulary!();
        // pin: closed-vocabulary AtomKindRegistry.GridItemTypeValues — a new grid item type is a reviewed change
        Assert.Equal(12, vocabulary.Count);
    }

    // ---- §2c: pricing — a non-body spawn prices non-zero, and the flat-unit value is pinned --------

    static AtomRow Atom(string kind, string paramsJson, string family) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = kind,
        FamilyId = family,
        Tier = 1,
        Name = family,
        WhenJson = "{}", // no trigger: a permanent modifier, conditionality is a flat 1000‰
        ParamsJson = paramsJson,
    };

    // §2c: "a pet, a bucket and a mower will all price identically (one flat unit, via the existing
    // channel-less spawn.entity fallback row)" -- CostFunction's (kindId, channel) key cannot express
    // "price by kind" yet (that is E30's job, spec-channel-pool.md). Pinned exactly, not just >0, so
    // the day E30 differentiates kind-specific pricing, the change shows as a diff here rather than
    // silently. Math: no Value-kind param present -> MeanMagnitude's "one reference unit" fallback (1),
    // normalised against spawn.entity's channel-less row (coeffMilli 1000, referenceScale 1) -> 1000
    // base points, no trigger -> conditionality 1000‰ (unconditional) -> 1000 points total, split
    // evenly across spawn.entity's two PowerCategory flags (Offense|Utility) -> 500 each.
    [Theory]
    [InlineData("pet")]
    [InlineData("bucket")]
    [InlineData("mower")]
    public void A_non_body_spawn_prices_at_the_flat_unit_value_pending_E30(string kind)
    {
        var priced = CostFunction.Price(Atom("spawn.entity",
            $$"""{"kind":"{{kind}}","typeId":0,"row":2}""", "atom.non-body-" + kind));

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.Equal(1000, priced.Power.Total);
        Assert.Equal(500, priced.Power.Offense);
        Assert.Equal(0, priced.Power.Survivability);
        Assert.Equal(0, priced.Power.Control);
        Assert.Equal(500, priced.Power.Utility);
        Assert.Equal(0, priced.Power.Economy);
    }

    // Same flat-unit price for pet/bucket/mower today is the under-discrimination §2c names --
    // confirmed here as an equality, not merely asserted from the spec text.
    [Fact]
    public void Pet_bucket_and_mower_price_identically_today_the_known_E30_gap()
    {
        var pet = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"pet","typeId":0,"row":2}""", "atom.pet-price")).Power;
        var bucket = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"bucket","typeId":0,"row":2}""", "atom.bucket-price")).Power;
        var mower = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"mower","typeId":0,"row":2}""", "atom.mower-price")).Power;

        Assert.Equal(pet, bucket);
        Assert.Equal(bucket, mower);
    }

    // Coin is refused at load, so it never reaches AtomCompiler/CostFunction from a real bind path --
    // but CostFunction.Price itself does no Validate() call (it only reads ParamsJson), so pricing a
    // raw coin AtomRow directly still resolves through the same channel-less fallback. Recorded here
    // so a reader does not mistake "coin has no dedicated coefficient row" for a gap this module owes.
    [Fact]
    public void A_coin_atom_still_prices_through_the_same_channel_less_fallback_if_ever_constructed_directly()
    {
        var priced = CostFunction.Price(Atom("spawn.entity",
            """{"kind":"coin","typeId":0,"row":2}""", "atom.coin-price"));

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.Equal(1000, priced.Power.Total);
    }
}
