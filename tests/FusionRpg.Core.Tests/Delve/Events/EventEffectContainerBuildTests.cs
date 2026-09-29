using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.3/D3.5 (spec-event-deck.md §5, "Effects" paragraph) — `EventEffectContainerBuild.From`,
/// the small `EventEffectRef -> ContainerRow` resolver, structurally identical to the already-shipped
/// `UniqueContainerBuild.From`'s own fixed-atom resolution. No real production caller exists yet
/// (`EventDeck.Resolve`, still unbuilt) — hand-built fixtures, matching this program's established
/// "provably correct, zero production trigger yet" posture.</summary>
public class EventEffectContainerBuildTests
{
    static AtomRow Atom(string family, int tier) =>
        new() { AtomId = AtomRow.DeriveId(family, "", tier), KindId = "resource.delta", FamilyId = family, Tier = tier };

    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal)
    {
        [AtomRow.DeriveId("herbs", "", 1)] = Atom("herbs", 1),
        [AtomRow.DeriveId("herbs", "", 3)] = Atom("herbs", 3),
        [AtomRow.DeriveId("bandages", "", 2)] = Atom("bandages", 2),
    };

    static AtomRow? Lookup(string id) => Catalog.TryGetValue(id, out var a) ? a : null;

    // ---- argument validation ----

    [Fact]
    public void From_null_and_blank_arguments_throw()
    {
        var effects = new[] { new EventEffectRef("herbs", "trivial") };
        Assert.Throws<ArgumentException>(() => EventEffectContainerBuild.From("", effects, Lookup));
        Assert.Throws<ArgumentException>(() => EventEffectContainerBuild.From(" ", effects, Lookup));
        Assert.Throws<ArgumentException>(() => EventEffectContainerBuild.From(null!, effects, Lookup));
        Assert.Throws<ArgumentNullException>(() => EventEffectContainerBuild.From("c1", null!, Lookup));
        Assert.Throws<ArgumentNullException>(() => EventEffectContainerBuild.From("c1", effects, null!));
    }

    [Fact]
    public void From_an_empty_effects_list_throws()
    {
        Assert.Throws<ArgumentException>(() => EventEffectContainerBuild.From("c1", Array.Empty<EventEffectRef>(), Lookup));
    }

    [Fact]
    public void From_a_null_entry_in_the_effects_list_throws()
    {
        var effects = new EventEffectRef?[] { null };
        Assert.Throws<ArgumentException>(() => EventEffectContainerBuild.From("c1", effects!, Lookup));
    }

    // ---- happy path -- the verify line's own headline: family x powerBand -> tier -> real atom id ----

    [Fact]
    public void From_resolves_one_effect_to_one_fixed_core_atom_at_the_bands_tier()
    {
        var effects = new[] { new EventEffectRef("herbs", "trivial") }; // trivial -> tier 1
        var container = EventEffectContainerBuild.From("event:cache:good", effects, Lookup);

        Assert.Equal("event:cache:good", container.ContainerId);
        Assert.Equal(ContainerKind.Item, container.Kind);
        var atom = Assert.Single(container.Atoms);
        Assert.Equal(AtomRow.DeriveId("herbs", "", 1), atom.AtomId);
        Assert.Equal(0, atom.Seq);
    }

    [Fact]
    public void From_maps_every_powerBand_to_the_bands_own_real_tier()
    {
        Assert.Equal(1, TierOf("trivial"));
        Assert.Equal(3, TierOf("medium"));

        static int TierOf(string band)
        {
            var effects = new[] { new EventEffectRef("herbs", band) };
            var container = EventEffectContainerBuild.From("c", effects, id => id == AtomRow.DeriveId("herbs", "", 1) || id == AtomRow.DeriveId("herbs", "", 3)
                ? Lookup(id) : null);
            return int.Parse(container.Atoms[0].AtomId[^1..]);
        }
    }

    [Fact]
    public void From_multiple_effects_build_the_fixed_core_in_authoring_order()
    {
        var effects = new[]
        {
            new EventEffectRef("herbs", "trivial"),
            new EventEffectRef("bandages", "low"),
        };
        var container = EventEffectContainerBuild.From("c1", effects, Lookup);

        Assert.Equal(2, container.Atoms.Count);
        Assert.Equal(0, container.Atoms[0].Seq);
        Assert.Equal(AtomRow.DeriveId("herbs", "", 1), container.Atoms[0].AtomId);
        Assert.Equal(1, container.Atoms[1].Seq);
        Assert.Equal(AtomRow.DeriveId("bandages", "", 2), container.Atoms[1].AtomId);
    }

    [Fact]
    public void From_never_authors_a_pool_or_roll_budget()
    {
        var effects = new[] { new EventEffectRef("herbs", "trivial") };
        var container = EventEffectContainerBuild.From("c1", effects, Lookup);
        Assert.Empty(container.Pool);
        Assert.Equal(0, container.PrefixRolls);
        Assert.Equal(0, container.SuffixRolls);
    }

    // ---- refusals ----

    [Fact]
    public void From_an_unknown_powerBand_refuses_by_name()
    {
        var effects = new[] { new EventEffectRef("herbs", "not-a-real-band") };
        var ex = Assert.Throws<EventDeckRefusal>(() => EventEffectContainerBuild.From("c1", effects, Lookup));
        Assert.Contains("not-a-real-band", ex.Message);
    }

    [Fact]
    public void From_a_family_with_no_atom_at_the_resolved_tier_refuses_naming_the_missing_atom_id()
    {
        var effects = new[] { new EventEffectRef("unknown-family", "trivial") };
        var ex = Assert.Throws<EventDeckRefusal>(() => EventEffectContainerBuild.From("c1", effects, Lookup));
        Assert.Contains(AtomRow.DeriveId("unknown-family", "", 1), ex.Message);
    }
}
