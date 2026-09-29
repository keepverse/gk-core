using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>aura-skill T2 (audit D6): proves <see cref="AtomRowValidator.Validate"/> actually rejects
/// a <c>stat.derived</c> atom whose <c>op</c> the target channel's compose kind never reads, and that
/// the check is opt-in — a caller that doesn't supply <c>composeKindOf</c> (every context with no
/// <see cref="DerivedStatRegistry"/> to ask) gets the old behaviour unchanged.
///
/// <c>combat.power.fire</c> is a real, production-registered channel (RegisterCombatDefaults,
/// DerivedStatRegistry.cs) — FlatSum, so it reads only <c>flat</c>.</summary>
public class AtomRowValidatorDerivedOpTests
{
    const string Channel = "combat.power.fire";

    static AtomRow DerivedAtom(string op) => new()
    {
        AtomId = AtomRow.DeriveId("atom.derived-op-probe", "", 1),
        KindId = "stat.derived",
        FamilyId = "atom.derived-op-probe",
        Variant = "",
        Tier = 1,
        Name = "atom.derived-op-probe",
        ParamsJson = $"{{\"channel\":\"{Channel}\",\"op\":\"{op}\",\"amount\":5}}",
        WhenJson = "{}",
    };

    static DerivedComposeKind? ComposeKindOf(string channel) =>
        DerivedStatRegistry.CreateDefault().TryGet(channel, out var def) ? def.Compose : null;

    [Fact]
    public void An_op_the_channels_compose_kind_never_reads_is_rejected_when_a_resolver_is_supplied()
    {
        // combat.power.fire is FlatSum; FlatSum reads only "flat", never "increased".
        var result = AtomRowValidator.Validate(DerivedAtom("increased"), composeKindOf: ComposeKindOf);

        Assert.False(result.IsOk);
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, result.Reason);
    }

    [Fact]
    public void An_op_the_channels_compose_kind_does_read_is_accepted()
    {
        var result = AtomRowValidator.Validate(DerivedAtom("flat"), composeKindOf: ComposeKindOf);

        Assert.True(result.IsOk);
    }

    [Fact]
    public void Without_a_resolver_the_same_mismatched_op_is_accepted_the_check_is_opt_in()
    {
        // No composeKindOf supplied (the default) -- must behave exactly as it did before T2 ever
        // existed, since most callers have no DerivedStatRegistry to ask.
        var result = AtomRowValidator.Validate(DerivedAtom("increased"));

        Assert.True(result.IsOk);
    }
}
