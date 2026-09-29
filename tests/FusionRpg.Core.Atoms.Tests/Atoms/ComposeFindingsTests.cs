using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// ST4.5e (manager ruling on the ST4.5a diagnosis): <c>ComposeWithFindings</c> names the atoms the
/// composition could not price, instead of contributing a silent zero. The pooled-channel atom below is
/// the REAL corpus's case — three of the ten zero-priced actions in the ST4.5a reading were exactly
/// this, and nothing in the report could say why.
/// </summary>
public class ComposeFindingsTests
{
    static readonly PowerTables Tables = new(
        new[] { new PowerCoefficientRow("stat.derived", "", 1000, 1) },
        Array.Empty<TriggerFrequencyRow>());

    /// <summary>The shape the real corpus uses: a channel that is a POOL reference rather than a
    /// concrete channel, so nothing can price it without a pool catalog.</summary>
    static AtomRow PooledAtom() => new()
    {
        AtomId = "atom.st45e-pooled",
        KindId = "stat.derived",
        FamilyId = "atom.st45e-pooled",
        Tier = 1,
        Name = "pooled",
        ParamsJson = """
            {"amount":{"max":43,"min":21,"roll":"onApply"},
             "channel":{"allowRepeat":false,"count":1,"pool":"pool.element-dodge"},"op":"flat"}
            """,
    };

    [Fact]
    public void A_pooled_channel_atom_is_named_as_unpriced_instead_of_contributing_a_silent_zero()
    {
        var (power, unpriced) = ActorPowerCache.ComposeWithFindings(new[] { PooledAtom() }, Tables);

        Assert.True(power.IsZero);
        Assert.Equal(new[] { "atom.st45e-pooled" }, unpriced);
    }

    [Fact]
    public void The_named_composition_prices_identically_to_the_one_that_reports_nothing()
    {
        // One implementation, so the findings cannot change the price: `Compose` is this method's own
        // `Power` with the list discarded (contract 1 — the report must not price differently).
        var atoms = new[]
        {
            PooledAtom(),
            new AtomRow
            {
                AtomId = "atom.st45e.plain",
                KindId = "stat.derived",
                FamilyId = "atom.st45e.plain",
                Tier = 1,
                Name = "plain",
                ParamsJson = """{"amount":{"max":43,"min":21,"roll":"onApply"},"channel":"combat.dodge","op":"flat"}""",
            },
        };

        var (power, unpriced) = ActorPowerCache.ComposeWithFindings(atoms, Tables);

        Assert.Equal(ActorPowerCache.Compose(atoms, Tables), power);
        Assert.Equal(new[] { "atom.st45e-pooled" }, unpriced);
        Assert.True(power.Total > 0, "the priced atom must still contribute");
    }

    [Fact]
    public void An_unknown_kind_is_named_too()
    {
        var unknown = PooledAtom() with { AtomId = "atom.st45e.unknownkind", KindId = "not.a.kind" };

        var (power, unpriced) = ActorPowerCache.ComposeWithFindings(new[] { unknown }, Tables);

        Assert.True(power.IsZero);
        Assert.Equal(new[] { "atom.st45e.unknownkind" }, unpriced);
    }
}
