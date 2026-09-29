using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E38 (spec-entity-fields-12plus.md): twelve more Unity fields — plantShield, attackCountdown,
/// attackSpeedAdder, produceCountdown, plantSpeed, plantMoveSpeed, plantLevel, shootingLevel,
/// armorFlat, takeDmgMultiplier, zombieSpeedCurrent, zombieOriginSpeed — become real composed
/// channels. "E16 run a second time" (the spec's own words): same shape as
/// <see cref="ChannelExtensionTests"/>, twelve times over, plus the bearer-frame direction the
/// spec's own §2c decided for <c>takeDmgMultiplier</c>.
///
/// <para>The injector is not built by CI (spec §4) — compose/direction/pricing/validation all
/// assert here in Core.Tests; the writer half is covered by
/// <c>EntityFields12PlusGuardTests</c> (text-based, Guard.Tests) plus <c>guard-single-writer.py</c>.</para>
/// </summary>
public class EntityFieldsTwelvePlusTests
{
    static readonly string[] TwelveChannels =
    {
        StatChannels.PlantShield, StatChannels.AttackCountdown, StatChannels.AttackSpeedAdder,
        StatChannels.ProduceCountdown, StatChannels.PlantSpeed, StatChannels.PlantMoveSpeed,
        StatChannels.PlantLevel, StatChannels.ShootingLevel, StatChannels.ArmorFlat,
        StatChannels.TakeDmgMultiplier, StatChannels.ZombieSpeedCurrent, StatChannels.ZombieOriginSpeed,
    };

    static readonly EntityBaseline Plant = new()
    {
        Hp = 100, MaxHp = 100, Atk = 10,
        PlantShield = 0, AttackCountdown = 2.0, AttackSpeedAdder = 0, ProduceCountdown = 24.0,
        PlantSpeed = 0, PlantMoveSpeed = 0, PlantLevel = 1, ShootingLevel = 1,
    };

    static readonly EntityBaseline Zombie = new()
    {
        Hp = 100, MaxHp = 100, Atk = 10,
        ArmorFlat = 0, TakeDmgMultiplier = 1.0, ZombieSpeedCurrent = 1.0, ZombieOriginSpeed = 1.0,
    };

    static EntityFinal ComposePlant(params StatModifier[] mods) =>
        new StatComposer().Compose(Plant, new Bag(mods), applyStats: true);

    static EntityFinal ComposeZombie(params StatModifier[] mods) =>
        new StatComposer().Compose(Zombie, new Bag(mods), applyStats: true);

    sealed class Bag : IModifierBagReader
    {
        public Bag(IReadOnlyList<StatModifier> all) => All = all;
        public IReadOnlyList<StatModifier> All { get; }
    }

    static StatModifier Mod(string channel, ModifierOp op, double value) => new()
    {
        SourceKind = "test",
        SourceId = "src-" + channel + op,
        PluginId = "test",
        Channel = channel,
        Op = op,
        Value = value,
    };

    static AtomRow Atom(string channel, int amount) => new()
    {
        AtomId = "atom.e38test.t1",
        KindId = "stat.modify",
        FamilyId = "atom.e38test",
        Tier = 1,
        Name = "E38 test atom",
        ParamsJson = $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""",
    };

    // ---- 1. twenty-three primary channels, all twelve present -------------------------------------

    [Fact]
    public void There_are_twentythree_primary_channels_including_the_twelve()
    {
        // The count is pinned once, AtomCatalogSsotDriftTests (population-pin SE3.3, 2026-09-19);
        // this test's own job is the membership check below, not a second count literal.
        foreach (var channel in TwelveChannels)
            Assert.Contains(channel, StatChannels.All);
    }

    [Fact]
    public void Each_of_the_twelve_passes_atom_validation()
    {
        // Planted-violation coverage: leave one of the twelve out of StatChannels.All and this test
        // fails with BadParamValue instead of composing into nothing (spec §4's own planted case).
        foreach (var channel in TwelveChannels)
        {
            var verdict = AtomRowValidator.Validate(Atom(channel, 1));
            Assert.True(verdict.IsOk, $"{channel}: {verdict}");
        }
    }

    // ---- 2. composition -----------------------------------------------------------------------------

    [Fact]
    public void A_flat_modifier_reaches_plantLevel()
    {
        var final = ComposePlant(Mod(StatChannels.PlantLevel, ModifierOp.Flat, 1));
        Assert.Equal(2, final.PlantLevel);
    }

    [Fact]
    public void An_untouched_plantLevel_composes_to_its_baseline()
    {
        Assert.Equal(1, ComposePlant().PlantLevel);
    }

    [Fact]
    public void PlantLevel_never_goes_below_zero()
    {
        var final = ComposePlant(Mod(StatChannels.PlantLevel, ModifierOp.Flat, -100));
        Assert.Equal(0, final.PlantLevel);
    }

    [Fact]
    public void More_minus100_percent_on_attackCountdown_clamps_at_the_structural_floor()
    {
        // Same rule as E16's own interval: never zero or negative, a divide-by-zero / infinite-
        // fire-rate risk regardless of which call site reads it.
        var final = ComposePlant(Mod(StatChannels.AttackCountdown, ModifierOp.More, -1.0));

        Assert.Equal(StatChannels.MinimumInterval, final.AttackCountdown, 5);
        Assert.True(final.AttackCountdown > 0);
    }

    [Fact]
    public void AttackCountdown_composes_from_a_genuinely_zero_baseline()
    {
        // The whole reason E38 does NOT reuse E16's Real()/Interval() absent-baseline skip: a firing
        // plant's countdown legitimately reads 0 mid-cycle, and content must still be able to touch
        // it from there (unlike a zombie's produceInterval, which really is absent).
        var zeroCountdown = new EntityBaseline
        {
            Hp = Plant.Hp, MaxHp = Plant.MaxHp, Atk = Plant.Atk, AttackCountdown = 0,
        };
        var final = new StatComposer().Compose(
            zeroCountdown, new Bag(new[] { Mod(StatChannels.AttackCountdown, ModifierOp.Flat, 5) }),
            applyStats: true);

        Assert.Equal(5.0, final.AttackCountdown, 3);
    }

    [Fact]
    public void AttackSpeedAdder_composes_from_a_genuinely_zero_baseline_with_no_floor()
    {
        // Unguarded by design (§2b): a negative composed result must reach the field untouched.
        var final = ComposePlant(Mod(StatChannels.AttackSpeedAdder, ModifierOp.Flat, -50));
        Assert.Equal(-50.0, final.AttackSpeedAdder, 3);
    }

    [Fact]
    public void ArmorFlat_composes_from_a_genuinely_zero_baseline()
    {
        // Most zombies start at armorFlat 0 — if this channel reused Real()'s absent-baseline skip,
        // it would be uncomposable for the common case, not merely inert for an absent one.
        var final = ComposeZombie(Mod(StatChannels.ArmorFlat, ModifierOp.Flat, 25));
        Assert.Equal(25.0, final.ArmorFlat, 3);
    }

    [Fact]
    public void ArmorFlat_never_goes_negative()
    {
        var final = ComposeZombie(Mod(StatChannels.ArmorFlat, ModifierOp.Flat, -100));
        Assert.Equal(0.0, final.ArmorFlat, 3);
    }

    [Fact]
    public void PlantShield_composes_and_clamps_at_zero()
    {
        var raised = ComposePlant(Mod(StatChannels.PlantShield, ModifierOp.Flat, 500));
        Assert.Equal(500, raised.PlantShield);

        var floored = ComposePlant(Mod(StatChannels.PlantShield, ModifierOp.Flat, -500));
        Assert.Equal(0, floored.PlantShield);
    }

    [Fact]
    public void Scales_off_leaves_the_twelve_at_baseline()
    {
        var final = new StatComposer().Compose(
            Plant, new Bag(new[] { Mod(StatChannels.PlantShield, ModifierOp.Flat, 500) }),
            applyStats: false);

        Assert.Equal(0, final.PlantShield);
        Assert.Equal(2.0, final.AttackCountdown, 3);
    }

    // ---- 3. direction ---------------------------------------------------------------------------

    [Theory]
    [InlineData("plantShield", false)]
    [InlineData("attackCountdown", true)]
    [InlineData("attackSpeedAdder", false)]
    [InlineData("produceCountdown", true)]
    [InlineData("plantSpeed", false)]
    [InlineData("plantMoveSpeed", false)]
    [InlineData("plantLevel", false)]
    [InlineData("shootingLevel", false)]
    [InlineData("armorFlat", false)]
    [InlineData("takeDmgMultiplier", true)]
    [InlineData("zombieSpeedCurrent", false)]
    [InlineData("zombieOriginSpeed", false)]
    public void Direction_is_declared_for_each_of_the_twelve(string channel, bool lowerIsBetter)
    {
        Assert.Equal(lowerIsBetter, StatChannels.IsLowerBetter(channel));
    }

    [Fact]
    public void Increased_plus50_percent_on_takeDmgMultiplier_raises_the_value()
    {
        // The bearer takes MORE damage — the direct, non-obvious consequence of §2c's bearer frame.
        var final = ComposeZombie(Mod(StatChannels.TakeDmgMultiplier, ModifierOp.Increased, 0.5));
        Assert.True(final.TakeDmgMultiplier > Zombie.TakeDmgMultiplier);
    }

    [Fact]
    public void Increased_on_takeDmgMultiplier_warns_the_lower_is_better_lint()
    {
        // E14b's lint is generic over ANY LowerIsBetter channel (ContentValidation.BackwardsIntervals
        // reads StatChannels.IsLowerBetter, not a hardcoded interval list), so takeDmgMultiplier gets
        // the same "SLOWER"-worded warning attackInterval does with no lint-side change needed.
        var report = ContentValidation.Lint(
            new[] { Atom(StatChannels.TakeDmgMultiplier, 1) }, Array.Empty<ContainerRow>());

        var warning = Assert.Single(report.Warnings.Where(w => w.Rule == "backwards-interval"));
        Assert.False(warning.Blocking);
    }

    // ---- 4. the bearer-frame pricing decision (§2c, decided 2026-09-03) ---------------------------

    [Fact]
    public void Reducing_takeDmgMultiplier_prices_as_a_benefit()
    {
        var reduce = Atom(StatChannels.TakeDmgMultiplier, -100);
        var priced = CostFunction.Price(reduce);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0, "taking less damage is a buff");
    }

    [Fact]
    public void Raising_takeDmgMultiplier_prices_as_negative_power_under_the_bearer_frame()
    {
        // ⛔ DECIDED 2026-09-03: takeDmgMultiplier stays LowerIsBetter (the bearer frame, option 1 of
        // the spec's own two). A RAISE on your OWN takeDmgMultiplier is a real penalty to you — the
        // non-obvious direction a future author will file a bug against if it is not pinned here.
        var raise = Atom(StatChannels.TakeDmgMultiplier, 100);
        var priced = CostFunction.Price(raise);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total < 0, "raising your own takeDmgMultiplier is a penalty under the bearer frame");
    }

    [Fact]
    public void Enemies_take_more_damage_cannot_be_authored_as_a_stat_modify_on_this_channel()
    {
        // takeDmgMultiplier is NOT the authoring surface for "enemies take more damage" (§2b/§2c) —
        // that debuff is a status.apply payload, priced by its own coefficient/trigger/uptime.
        // Proven structurally, not just asserted in prose: stat.modify has no way to name a target
        // other than its own bearer (no "target" param on this kind at all), so an atom that means
        // "make THAT OTHER entity take more damage" cannot be expressed on stat.modify — an author
        // is forced to status.apply, which does carry a target (resolved from the triggering event).
        var statModify = AtomKindRegistry.Get("stat.modify")!;
        Assert.DoesNotContain(statModify.Params.Defs, d => d.Name == "target");

        var statusApply = AtomKindRegistry.Get("status.apply");
        Assert.NotNull(statusApply);
    }

    // ---- 5. E44 (power-sweep) coefficient rows, criterion 7 -----------------------------------------
    //
    // Checked live, not assumed, when this module was built (2026-09-04): E44's import infrastructure
    // had landed in the working tree (uncommitted) — SeedContent.Coefficients, AtomSeedFile's
    // "power-coefficient" TryKind case, ReadCoefficient, and RpgStore.Import.cs's own
    // ValidateCoefficients/WriteCoefficientsUnlocked wiring all exist — but the seed FILE itself
    // (gk-data/packs/fusion/data/seed/power/coefficients.v1.json) did not yet. §2c's instruction for exactly this state
    // ("if it exists and has a working import path... add your twelve rows there") applied, so this
    // module added the file rather than reporting the criterion blocked. Do not edit
    // CoefficientTable.Authored() (a coefficient added there would move every golden with no
    // content-hash change, forbidden by that method's own design note) — these tests read the seed
    // file directly, never that fallback table, and never a live SQLite import (Core.Tests has none).

    [Fact]
    public void Each_of_the_twelve_has_a_coefficient_row_in_the_seed_file()
    {
        var content = LoadCoefficientSeedFile(out var errors);
        Assert.Empty(errors);

        foreach (var channel in TwelveChannels)
        {
            var row = content.Coefficients.SingleOrDefault(c => c.KindId == "stat.modify" && c.Channel == channel);
            Assert.True(row is not null, $"{channel}: no coefficient row in coefficients.v1.json");
            Assert.True(row!.ReferenceScale > 0, $"{channel}: referenceScale must be positive");
            Assert.NotEqual(0, row.CoeffMilli);
        }
    }

    // E41 (spec-ui-attach-point.md §2c): landed a thirteenth row (ui.present, kind-wide, coeffMilli 0)
    // in this same seed file after this test was written, and E37 (spec-projectile-control.md §2c)
    // landed a fourteenth (bullet.modify, channel-less) alongside it without this guard being updated
    // for either — found stale here 2026-09-05 while re-verifying against the real file (it already
    // read 14 rows, not 13, before this session's own E44 additions). E44 (spec-power-sweep.md §4.1,
    // criteria 1/3) then added 20 more: 4 sweep-fitted (stat.modify/atk, defense, hp, maxHp — the only
    // channels the E43 family-expand corpus actually covers) plus 16 unfitted pass-throughs of
    // CoefficientTable.Authored()'s remaining original rows, added to close a real, independently
    // confirmed gap (RpgStore.GetPowerTables falls back to Authored() only when power_coefficient has
    // ZERO rows — once any row is imported, every Authored() channel this file does not also carry
    // becomes silently unpriced in a DB-backed context; confirmed live against the shipped dist DB,
    // which held exactly this session's pre-E44 14 rows and none of the missing 20). 14 + 20 = 34,
    // updated here rather than left stale the moment either module added its own row — matching this
    // session's own established precedent for a shared-file count guard (AtomKindRegistryTests.cs's
    // KindCount/AttachPointCount rows).
    [Fact]
    public void The_seed_file_carries_exactly_the_twelve_plus_ui_presents_own_row()
    {
        // A removed row makes the atom report unpriced once a real import runs — this is the sibling
        // check that a stray or duplicated row was not authored either.
        var content = LoadCoefficientSeedFile(out _);
        Assert.Equal(TwelveChannels.Length + 1 /* ui.present */ + 1 /* bullet.modify */
            + 20 /* E44: 4 sweep-fitted + 16 pass-through, 2026-09-05 */, content.Coefficients.Count);
        Assert.Contains(content.Coefficients, c => c.KindId == "ui.present" && c.Channel == "");
        Assert.Contains(content.Coefficients, c => c.KindId == "bullet.modify" && c.Channel == "");
        Assert.Contains(content.Coefficients, c => c.KindId == "stat.modify" && c.Channel == "atk");
    }

    static SeedContent LoadCoefficientSeedFile(out IReadOnlyList<SeedError> errors)
    {
        var path = Path.Combine(ContentRoot.Path, "data", "seed", "power", "coefficients.v1.json");
        var result = AtomSeedFile.Collect(new[] { (path, File.ReadAllText(path)) });
        errors = result.Errors;
        return result.Content;
    }
}
