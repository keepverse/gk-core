using System.Collections.Generic;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>aura-skill T2 (audit D6): proves <see cref="AtomRowValidator.DerivedComposeAcceptedOps"/>
/// cannot drift from the real <see cref="DerivedComposer"/>. For every one of the 4 compose kinds ×
/// 4 ops = 16 cells, this builds a registry with exactly one test channel per kind, composes it with
/// a single modifier of that op, and checks whether the composed value actually moved — the empirical
/// truth — against what the validator's table claims. If a future change to `DerivedComposer` ever
/// starts (or stops) reading an op for a kind, this test fails before the validator's table goes
/// stale silently.</summary>
public class DerivedComposeKindOpsTests
{
    static readonly (DerivedComposeKind Kind, string Channel)[] KindChannels =
    {
        (DerivedComposeKind.FlatSum, "test.flatsum"),
        (DerivedComposeKind.FlatReplace, "test.flatreplace"),
        (DerivedComposeKind.SumIncreased, "test.sumincreased"),
        (DerivedComposeKind.MaxPriorityFlag, "test.maxpriorityflag"),
    };

    static readonly string[] AllOps = { "flat", "increased", "replace", "flag" };

    public static IEnumerable<object[]> AllSixteenCells()
    {
        foreach (var (kind, _) in KindChannels)
            foreach (var op in AllOps)
                yield return new object[] { kind, op };
    }

    static string ChannelFor(DerivedComposeKind kind)
    {
        foreach (var (k, channel) in KindChannels)
            if (k == kind) return channel;
        throw new System.ArgumentException($"no test channel registered for {kind}");
    }

    static DerivedModifierOp ParseOp(string op) => op switch
    {
        "flat" => DerivedModifierOp.Flat,
        "increased" => DerivedModifierOp.Increased,
        "replace" => DerivedModifierOp.Replace,
        "flag" => DerivedModifierOp.Flag,
        _ => throw new System.ArgumentException(op),
    };

    static DerivedComposer BuildComposer()
    {
        // CreateDefault(), not a bare constructor -- the ctor is private (registries are always built
        // from the shipped default set). Registering four probe-only channel ids on top of it is safe:
        // they don't collide with anything real, and Compose only ever asked for these when queried.
        var registry = DerivedStatRegistry.CreateDefault();
        foreach (var (kind, channel) in KindChannels)
            registry.Register(new DerivedStatDef(channel, kind, DefaultValue: 0));
        return new DerivedComposer(registry);
    }

    [Theory]
    [MemberData(nameof(AllSixteenCells))]
    public void Validator_table_matches_real_composer_behaviour(DerivedComposeKind kind, string op)
    {
        var channel = ChannelFor(kind);
        var composer = BuildComposer();

        var baseline = composer.Compose(System.Array.Empty<DerivedModifier>()).Get(channel);
        var withMod = composer
            .Compose(new[] { new DerivedModifier(channel, ParseOp(op), 999.0, SourceId: "probe") })
            .Get(channel);

        var composerActuallyReadsThisOp = withMod != baseline;
        var validatorClaimsAccepted =
            System.Array.Exists(AtomRowValidator.DerivedComposeAcceptedOps[kind], o => o == op);

        Assert.Equal(composerActuallyReadsThisOp, validatorClaimsAccepted);
    }

    [Fact]
    public void Every_compose_kind_has_at_least_one_accepted_op()
    {
        // A kind with an empty accepted-op list would reject every possible authored row for any
        // channel of that kind — a real content-authoring dead end, not just a table gap.
        foreach (DerivedComposeKind kind in System.Enum.GetValues(typeof(DerivedComposeKind)))
            Assert.NotEmpty(AtomRowValidator.DerivedComposeAcceptedOps[kind]);
    }
}
