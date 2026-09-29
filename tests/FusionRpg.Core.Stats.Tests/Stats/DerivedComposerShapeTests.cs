using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// ⚡ The fold's SHAPE changed on 2026-09-16 (O(channels × mods) with a `List` allocated per channel →
/// one pass over the modifiers, then one pass over the channels). These tests pin the property that
/// makes such a change safe to make again: <b>the composed snapshot is identical, channel for channel</b>.
///
/// <para>They deliberately assert the CONTRACT (every registered channel present; per-op fold semantics;
/// order independence; the empty-modifier fast path equals the general path), never a µs figure or a
/// channel count — the speed is a reading that `probe-perf.ps1` owns, and the registry's size is a
/// population that grows when content ships.</para>
/// </summary>
public class DerivedComposerShapeTests
{
    static DerivedStatDef Def(string id, DerivedComposeKind compose, double def = 0, double? cap = null) =>
        new(id, compose, DefaultValue: def, Cap: cap);

    /// <summary>The registry's constructor is private by design, so these compose against the REAL
    /// registry with a handful of test channels registered on top — which is also the more honest
    /// substrate: the fold's cost and its correctness both depend on the real channel population being
    /// there, mostly unmodified.</summary>
    static DerivedComposer ComposerWith(params DerivedStatDef[] defs)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        foreach (var d in defs) registry.Register(d);
        return new DerivedComposer(registry);
    }

    [Fact]
    public void Every_registered_channel_is_present_in_the_snapshot_even_with_no_modifiers()
    {
        var composer = ComposerWith(
            Def("a.one", DerivedComposeKind.FlatSum, def: 5),
            Def("a.two", DerivedComposeKind.FlatReplace, def: 7),
            Def("a.three", DerivedComposeKind.MaxPriorityFlag, def: 9));

        var snap = composer.Compose();

        Assert.Equal(5, snap.Get("a.one"));
        Assert.Equal(7, snap.Get("a.two"));
        Assert.Equal(9, snap.Get("a.three"));
    }

    [Fact]
    public void An_empty_modifier_list_composes_exactly_as_an_absent_one()
    {
        var composer = ComposerWith(
            Def("b.flat", DerivedComposeKind.FlatSum, def: 3),
            Def("b.replace", DerivedComposeKind.FlatReplace, def: 4),
            Def("b.increased", DerivedComposeKind.SumIncreased, def: 11, cap: 20),
            Def("b.flag", DerivedComposeKind.MaxPriorityFlag, def: 2));

        var absent = composer.Compose();
        var empty = composer.Compose(new List<DerivedModifier>());

        foreach (var channel in new[] { "b.flat", "b.replace", "b.increased", "b.flag" })
            Assert.Equal(absent.Get(channel), empty.Get(channel));
    }

    [Fact]
    public void A_capped_channel_with_no_modifiers_is_still_capped()
    {
        // The empty fast path must not skip Cap: a default above the cap composes to the cap, exactly as
        // it would through the general path with an empty list.
        var composer = ComposerWith(Def("c.increased", DerivedComposeKind.SumIncreased, def: 50, cap: 30));

        Assert.Equal(30, composer.Compose().Get("c.increased"));
    }

    [Fact]
    public void Modifier_order_does_not_change_the_composed_value()
    {
        var composer = ComposerWith(
            Def("d.flat", DerivedComposeKind.FlatSum),
            Def("d.replace", DerivedComposeKind.FlatReplace));

        var mods = new List<DerivedModifier>
        {
            new("d.flat", DerivedModifierOp.Flat, 4, SourceId: "s1"),
            new("d.replace", DerivedModifierOp.Replace, 10, SourceId: "s1", Priority: 1),
            new("d.flat", DerivedModifierOp.Flat, 6, SourceId: "s2"),
            new("d.replace", DerivedModifierOp.Replace, 99, SourceId: "s2", Priority: 0),
        };

        var forward = composer.Compose(mods);
        var reversed = composer.Compose(Enumerable.Reverse(mods).ToList());

        Assert.Equal(10, forward.Get("d.flat"));
        Assert.Equal(10, forward.Get("d.replace"));   // higher Priority wins, not list position
        Assert.Equal(forward.Get("d.flat"), reversed.Get("d.flat"));
        Assert.Equal(forward.Get("d.replace"), reversed.Get("d.replace"));
    }

    [Fact]
    public void Only_the_named_channels_modifiers_reach_a_channel()
    {
        // The regression the rewrite could plausibly introduce: grouping by channel and then reading the
        // wrong group. A modifier on one channel must never move another channel's value.
        var composer = ComposerWith(
            Def("e.left", DerivedComposeKind.FlatSum, def: 1),
            Def("e.right", DerivedComposeKind.FlatSum, def: 1));

        var snap = composer.Compose(new List<DerivedModifier>
        {
            new("e.left", DerivedModifierOp.Flat, 100, SourceId: "s"),
        });

        Assert.Equal(101, snap.Get("e.left"));
        Assert.Equal(1, snap.Get("e.right"));
    }

    [Fact]
    public void Ops_that_a_channels_compose_kind_ignores_contribute_nothing()
    {
        // FlatSum reads Flat only; SumIncreased reads Increased only. A mixed bag on one channel must
        // fold exactly the op that channel's kind names.
        var composer = ComposerWith(
            Def("f.flatsum", DerivedComposeKind.FlatSum, def: 0),
            Def("f.increased", DerivedComposeKind.SumIncreased, def: 0));

        var snap = composer.Compose(new List<DerivedModifier>
        {
            new("f.flatsum", DerivedModifierOp.Flat, 3, SourceId: "s"),
            new("f.flatsum", DerivedModifierOp.Increased, 500, SourceId: "s"),
            new("f.increased", DerivedModifierOp.Increased, 7, SourceId: "s"),
            new("f.increased", DerivedModifierOp.Flat, 900, SourceId: "s"),
        });

        Assert.Equal(3, snap.Get("f.flatsum"));
        Assert.Equal(7, snap.Get("f.increased"));
    }

    [Fact]
    public void An_unregistered_channel_is_refused_rather_than_silently_folded()
    {
        var composer = ComposerWith(Def("g.known", DerivedComposeKind.FlatSum));

        Assert.Throws<UnknownDerivedChannelException>(() => composer.Compose(new List<DerivedModifier>
        {
            new("g.unknown", DerivedModifierOp.Flat, 1, SourceId: "s"),
        }));
    }

    [Fact]
    public void An_open_prefix_family_channel_composes_even_though_it_has_no_concrete_registered_def()
    {
        // ⚠️ The regression the first cut of the fold rewrite actually shipped, caught by
        // EffectOfflineKitTests on the first full run. `AllRegistered` lists CONCRETE defs only; the
        // registry also resolves open prefix families (`status.immune.*`), which have no row until a
        // modifier names one. Iterating the registered defs alone dropped every one of them, folding a
        // real Flag to 0. This pins the behaviour so the next optimisation cannot lose it again.
        var composer = new DerivedComposer(DerivedStatRegistry.CreateDefault());
        var channel = DerivedStatChannels.StatusImmune("poison");

        Assert.DoesNotContain(composer.Registry.AllRegistered, d =>
            string.Equals(d.ChannelId, channel, StringComparison.Ordinal));

        var snap = composer.Compose(new List<DerivedModifier>
        {
            new(channel, DerivedModifierOp.Flag, 1),
            new(channel, DerivedModifierOp.Flag, 1),
        });

        Assert.Equal(1, snap.Get(channel));
    }

    [Fact]
    public void Registering_after_a_compose_is_visible_to_the_next_compose()
    {
        // `AllRegistered` is cached now. The cache must drop on Register, or a late registration would
        // be invisible for the rest of the process.
        var registry = DerivedStatRegistry.CreateDefault();
        registry.Register(Def("h.first", DerivedComposeKind.FlatSum, def: 1));
        var composer = new DerivedComposer(registry);

        var before = composer.Compose();
        Assert.Equal(1, before.Get("h.first"));

        registry.Register(Def("h.second", DerivedComposeKind.FlatSum, def: 2));
        var after = composer.Compose();

        Assert.Equal(1, after.Get("h.first"));
        Assert.Equal(2, after.Get("h.second"));
    }
}
