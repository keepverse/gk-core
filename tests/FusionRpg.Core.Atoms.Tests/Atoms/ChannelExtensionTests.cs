using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E16: <c>attackInterval</c>, <c>produceInterval</c> and <c>zombieSpeed</c> become real composed
/// channels (spec-channel-extension.md).
///
/// <para>"Shoots faster" is the tower-defense genre's single most wanted affix, and it was
/// impossible to author: the three were written straight to the Unity field from cheat keys,
/// bypassing the modifier bag entirely. The documented channel enum listed them anyway, which is how
/// the gap survived — the docs promised a capability the code never had.</para>
///
/// <para>The trap this module has to hold is that <b>lower is better</b> on an interval, which
/// inverts the grammar for the author, the cost function and the UI at once.</para>
/// </summary>
public class ChannelExtensionTests
{
    static readonly EntityBaseline Plant = new()
    {
        Hp = 100, MaxHp = 100, Atk = 10,
        AttackInterval = 2.0, ProduceInterval = 24.0,
    };

    static EntityFinal Compose(params StatModifier[] mods) =>
        new StatComposer().Compose(Plant, new Bag(mods), applyStats: true);

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

    // ---- the channels compose --------------------------------------------------------------------

    [Fact]
    public void There_are_twentythree_primary_channels()
    {
        // 11 since E16; E38 (spec-entity-fields-12plus.md) took it to 23 — see
        // EntityFieldsTwelvePlusTests for that module's own channel-list assertions. The count is
        // pinned once, AtomCatalogSsotDriftTests (population-pin SE3.3, 2026-09-19); this test's own
        // job is the membership check below, not a second count literal.
        Assert.Contains(StatChannels.AttackInterval, StatChannels.All);
        Assert.Contains(StatChannels.ProduceInterval, StatChannels.All);
        Assert.Contains(StatChannels.ZombieSpeed, StatChannels.All);
    }

    [Fact]
    public void A_flat_modifier_reaches_the_attack_interval()
    {
        // The whole point: an effect can now touch it at all.
        var final = Compose(Mod(StatChannels.AttackInterval, ModifierOp.Flat, -0.5));

        Assert.Equal(1.5, final.AttackInterval, 3);
    }

    [Fact]
    public void An_untouched_interval_composes_to_its_baseline()
    {
        Assert.Equal(2.0, Compose().AttackInterval, 3);
    }

    [Fact]
    public void An_entity_with_no_such_stat_stays_at_zero()
    {
        // A zombie has no produce interval. Composing modifiers onto a zero baseline would invent
        // one out of nothing, and the writer reads zero as "leave the field alone".
        var zombie = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10, ZombieSpeed = 1.5 };

        var final = new StatComposer().Compose(
            zombie, new Bag(new[] { Mod(StatChannels.ProduceInterval, ModifierOp.Flat, -5) }),
            applyStats: true);

        Assert.Equal(0, final.ProduceInterval);
        Assert.Equal(1.5, final.ZombieSpeed, 3);
    }

    [Fact]
    public void An_interval_can_never_reach_zero()
    {
        // `More` −100% is ordinary content, and the result is a divide-by-zero or an infinite fire
        // rate depending on which call site reads it. Neither is shippable.
        var final = Compose(Mod(StatChannels.AttackInterval, ModifierOp.More, -1.0));

        Assert.Equal(StatChannels.MinimumInterval, final.AttackInterval, 5);
        Assert.True(final.AttackInterval > 0);
    }

    [Fact]
    public void Even_a_deeply_negative_stack_stays_above_the_floor()
    {
        var final = Compose(
            Mod(StatChannels.AttackInterval, ModifierOp.Flat, -100),
            Mod(StatChannels.AttackInterval, ModifierOp.More, -5.0));

        Assert.True(final.AttackInterval >= StatChannels.MinimumInterval);
    }

    [Fact]
    public void Scales_off_leaves_the_intervals_at_baseline()
    {
        var final = new StatComposer().Compose(
            Plant, new Bag(new[] { Mod(StatChannels.AttackInterval, ModifierOp.Flat, -1) }),
            applyStats: false);

        Assert.Equal(2.0, final.AttackInterval, 3);
    }

    // ---- direction -------------------------------------------------------------------------------

    [Theory]
    [InlineData("attackInterval", true)]
    [InlineData("produceInterval", true)]
    [InlineData("zombieSpeed", false)]
    [InlineData("atk", false)]
    [InlineData("maxHp", false)]
    public void Direction_is_declared_once_and_read_everywhere(string channel, bool lowerIsBetter)
    {
        Assert.Equal(lowerIsBetter, StatChannels.IsLowerBetter(channel));
    }

    [Fact]
    public void Reducing_an_interval_prices_as_a_buff_not_a_penalty()
    {
        // `quickening` reduces an attack interval. Pricing the raw magnitude would file the genre's
        // most wanted affix as negative power — failing no budget, sorting last in every UI.
        var quicken = Atom(StatChannels.AttackInterval, -1);

        var priced = CostFunction.Price(quicken);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0, "a shorter interval is a buff");
    }

    [Fact]
    public void Lengthening_an_interval_prices_as_a_drawback()
    {
        // The other half. Without it the flip could be applying to everything.
        var slow = Atom(StatChannels.AttackInterval, 1);

        Assert.True(CostFunction.Price(slow).Power.Total < 0);
    }

    [Fact]
    public void A_higher_is_better_channel_is_unaffected_by_the_flip()
    {
        Assert.True(CostFunction.Price(Atom(StatChannels.Atk, 10)).Power.Total > 0);
        Assert.True(CostFunction.Price(Atom(StatChannels.ZombieSpeed, 1)).Power.Total > 0);
    }

    // ---- the lint ---------------------------------------------------------------------------------

    [Fact]
    public void A_positive_magnitude_on_an_interval_warns()
    {
        // Almost always an author meaning "faster" and getting "slower".
        var report = ContentValidation.Lint(
            new[] { Atom(StatChannels.AttackInterval, 1) }, Array.Empty<ContainerRow>());

        var warning = Assert.Single(report.Warnings.Where(w => w.Rule == "backwards-interval"));
        Assert.Contains("SLOWER", warning.Detail, StringComparison.Ordinal);
        Assert.False(warning.Blocking); // a deliberate drawback is legitimate content
    }

    [Fact]
    public void A_negative_magnitude_on_an_interval_does_not_warn()
    {
        var report = ContentValidation.Lint(
            new[] { Atom(StatChannels.AttackInterval, -1) }, Array.Empty<ContainerRow>());

        Assert.DoesNotContain(report.Warnings, w => w.Rule == "backwards-interval");
    }

    [Fact]
    public void A_positive_magnitude_on_a_normal_channel_does_not_warn()
    {
        var report = ContentValidation.Lint(
            new[] { Atom(StatChannels.Atk, 10) }, Array.Empty<ContainerRow>());

        Assert.DoesNotContain(report.Warnings, w => w.Rule == "backwards-interval");
    }

    // ---- authorability ------------------------------------------------------------------------------

    [Fact]
    public void The_three_new_channels_pass_atom_validation()
    {
        // The end of the story: an author can now write the affix. Before E16 the registry rejected
        // the channel with a message explaining that E16 would one day promote it.
        foreach (var channel in new[]
                 { StatChannels.AttackInterval, StatChannels.ProduceInterval, StatChannels.ZombieSpeed })
        {
            var verdict = AtomRowValidator.Validate(Atom(channel, -1));
            Assert.True(verdict.IsOk, $"{channel}: {verdict}");
        }
    }

    [Fact]
    public void An_invented_channel_is_still_rejected()
    {
        // Promoting three must not have opened the gate. The list is a rule, not documentation.
        Assert.Equal(
            AtomRejectionReason.BadParamValue,
            AtomRowValidator.Validate(Atom("fireRate", -1)).Reason);
    }

    static AtomRow Atom(string channel, int amount) => new()
    {
        AtomId = "atom.quickening.t1",
        KindId = "stat.modify",
        FamilyId = "atom.quickening",
        Tier = 1,
        Name = "Quickening",
        ParamsJson = $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""",
    };
}
