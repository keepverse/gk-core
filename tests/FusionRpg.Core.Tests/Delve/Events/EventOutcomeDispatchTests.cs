using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>
/// D3.3/D3.5 (spec-event-deck.md §5) — the five-way atom-kind dispatch table, the last of D3.5's own
/// three named blockers. No real production caller exists yet (`EventDeck.Resolve`, D3.3's own larger
/// orchestrator, is still unbuilt) — fixtures build a real <see cref="InstanceRow"/> by hand, matching
/// this whole program's established "provably correct, zero production trigger yet" posture
/// (<c>EventCatalog.Load</c>, `ConsumableCatalog.Load`).
/// </summary>
public class EventOutcomeDispatchTests
{
    static AtomRow Atom(string id, string kind) => new() { AtomId = id, KindId = kind, FamilyId = id, Tier = 1 };

    static InstanceRow Instance(string containerId, params InstanceAtomRow[] atoms) => new()
    {
        InstanceId = "inst-1",
        ContainerId = containerId,
        Atoms = atoms,
    };

    static DelveMemberState Member(string id, long hp = 1000, long spirit = 1000, bool downed = false,
        IReadOnlyList<BattleStatusSpec>? statuses = null, BattleInnateShield? shield = null, int nerveStacks = 0) =>
        new(id,
            new Dictionary<string, long> { ["hp"] = hp, ["stamina"] = 1000, ["hunger"] = 1000, ["spirit"] = spirit, ["qi"] = 1000, ["poise"] = 1000 },
            statuses ?? Array.Empty<BattleStatusSpec>(), shield, nerveStacks, downed, DownedOnce: downed);

    static ActorDerivedSnapshot Snapshot(long max = 1000, long regenPerTick = 0) =>
        ActorDerivedSnapshot.FromValues(DerivedStatChannels.ResourceIds.SelectMany(id => new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax(id), max),
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceRegen(id), regenPerTick),
        }));

    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal)
    {
        ["atom.heal"] = Atom("atom.heal", EventOutcomeDispatch.ResourceDeltaKind),
        ["atom.drain-spirit"] = Atom("atom.drain-spirit", EventOutcomeDispatch.ResourceDeltaKind),
        ["atom.bless"] = Atom("atom.bless", EventOutcomeDispatch.StatusApplyKind),
        ["atom.curse-nerve"] = Atom("atom.curse-nerve", EventOutcomeDispatch.StatusApplyKind),
        ["atom.ward"] = Atom("atom.ward", EventOutcomeDispatch.ShieldGrantKind),
        ["atom.ward2"] = Atom("atom.ward2", EventOutcomeDispatch.ShieldGrantKind),
        ["atom.buff"] = Atom("atom.buff", EventOutcomeDispatch.StatDerivedKind),
        ["atom.banner"] = Atom("atom.banner", EventOutcomeDispatch.UiPresentKind),
        ["atom.meter"] = Atom("atom.meter", EventOutcomeDispatch.UiPresentKind),
        ["atom.unsupported"] = Atom("atom.unsupported", "match.modify"),
    };

    static AtomRow? Lookup(string id) => Catalog.TryGetValue(id, out var a) ? a : null;

    static readonly int StackPerCurio = DungeonTuningHub.Tuning.AttritionNerve.StackPerCurio;

    EventOutcomeDispatchResult Run(InstanceRow instance, IReadOnlyList<DelveMemberState> party, IUiPresentSink? sink = null) =>
        EventOutcomeDispatch.Dispatch(
            instance, party, Lookup, _ => Snapshot(), sink ?? new RecordingUiPresentSink(),
            delveId: "42", StackPerCurio, atTick: 0);

    // ---- argument validation ----

    [Fact]
    public void Dispatch_null_arguments_throw()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":10}"));
        var party = new[] { Member("m1") };
        Assert.Throws<ArgumentNullException>(() =>
            EventOutcomeDispatch.Dispatch(null!, party, Lookup, _ => Snapshot(), new RecordingUiPresentSink(), "1", 0, 0));
        Assert.Throws<ArgumentNullException>(() =>
            EventOutcomeDispatch.Dispatch(instance, null!, Lookup, _ => Snapshot(), new RecordingUiPresentSink(), "1", 0, 0));
        Assert.Throws<ArgumentNullException>(() =>
            EventOutcomeDispatch.Dispatch(instance, party, null!, _ => Snapshot(), new RecordingUiPresentSink(), "1", 0, 0));
        Assert.Throws<ArgumentNullException>(() =>
            EventOutcomeDispatch.Dispatch(instance, party, Lookup, null!, new RecordingUiPresentSink(), "1", 0, 0));
        Assert.Throws<ArgumentNullException>(() =>
            EventOutcomeDispatch.Dispatch(instance, party, Lookup, _ => Snapshot(), null!, "1", 0, 0));
    }

    [Fact]
    public void Dispatch_empty_party_throws()
    {
        var instance = Instance("c1");
        Assert.Throws<ArgumentException>(() => Run(instance, Array.Empty<DelveMemberState>()));
    }

    [Fact]
    public void Dispatch_blank_delveId_throws()
    {
        var instance = Instance("c1");
        var party = new[] { Member("m1") };
        Assert.Throws<ArgumentException>(() =>
            EventOutcomeDispatch.Dispatch(instance, party, Lookup, _ => Snapshot(), new RecordingUiPresentSink(), " ", 0, 0));
    }

    [Fact]
    public void Dispatch_negative_stackPerCurio_throws()
    {
        var instance = Instance("c1");
        var party = new[] { Member("m1") };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EventOutcomeDispatch.Dispatch(instance, party, Lookup, _ => Snapshot(), new RecordingUiPresentSink(), "1", -1, 0));
    }

    [Fact]
    public void Dispatch_duplicate_member_instance_id_throws()
    {
        var instance = Instance("c1");
        var party = new[] { Member("dupe"), Member("dupe") };
        Assert.Throws<ArgumentException>(() => Run(instance, party));
    }

    [Fact]
    public void Dispatch_unresolvable_atom_id_throws_EventDeckRefusal()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.does-not-exist", "{}"));
        var party = new[] { Member("m1") };
        Assert.Throws<EventDeckRefusal>(() => Run(instance, party));
    }

    [Fact]
    public void Dispatch_an_atom_kind_outside_the_five_refuses()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.unsupported", "{}"));
        var party = new[] { Member("m1") };
        var ex = Assert.Throws<EventDeckRefusal>(() => Run(instance, party));
        Assert.Contains("atom.unsupported", ex.Message);
        Assert.Contains("match.modify", ex.Message);
    }

    // ---- resource.delta ----

    [Fact]
    public void ResourceDelta_default_target_touches_every_standing_member()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":100}"));
        var party = new[] { Member("m1", hp: 500), Member("m2", hp: 500) };
        var result = Run(instance, party);
        Assert.Equal(600, result.Members[0].Pools["hp"]);
        Assert.Equal(600, result.Members[1].Pools["hp"]);
    }

    [Fact]
    public void ResourceDelta_never_touches_a_downed_member()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":100}"));
        var party = new[] { Member("m1", hp: 500, downed: true), Member("m2", hp: 500) };
        var result = Run(instance, party);
        Assert.Equal(500, result.Members[0].Pools["hp"]); // downed, untouched
        Assert.Equal(600, result.Members[1].Pools["hp"]);
    }

    [Fact]
    public void ResourceDelta_target_one_touches_only_the_first_standing_member()
    {
        var instance = Instance("c1",
            new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":100,\"target\":{\"scope\":\"one\"}}"));
        var party = new[] { Member("m1", hp: 500, downed: true), Member("m2", hp: 500), Member("m3", hp: 500) };
        var result = Run(instance, party);
        Assert.Equal(500, result.Members[0].Pools["hp"]); // downed, skipped even for "one"
        Assert.Equal(600, result.Members[1].Pools["hp"]); // first standing
        Assert.Equal(500, result.Members[2].Pools["hp"]); // second standing, untouched
    }

    [Fact]
    public void ResourceDelta_target_one_with_every_member_downed_touches_nobody_and_does_not_throw()
    {
        var instance = Instance("c1",
            new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":100,\"target\":{\"scope\":\"one\"}}"));
        var party = new[] { Member("m1", hp: 0, downed: true) };
        var result = Run(instance, party);
        Assert.Equal(0, result.Members[0].Pools["hp"]);
    }

    [Fact]
    public void ResourceDelta_unknown_channel_propagates_the_real_ArgumentException()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"mana\",\"amount\":10}"));
        var party = new[] { Member("m1") };
        Assert.Throws<ArgumentException>(() => Run(instance, party));
    }

    // ---- the negative-spirit-delta nerve special case (spec §5, row 1) -- the verify line's own headline ----

    [Fact]
    public void A_negative_spirit_delta_adds_stackPerCurio_to_NerveStacks()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.drain-spirit", "{\"channel\":\"spirit\",\"amount\":-100}"));
        var party = new[] { Member("m1", spirit: 500, nerveStacks: 2) };
        var result = Run(instance, party);
        Assert.Equal(2 + StackPerCurio, result.Members[0].NerveStacks);
    }

    [Fact]
    public void A_positive_spirit_delta_never_touches_NerveStacks()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.drain-spirit", "{\"channel\":\"spirit\",\"amount\":100}"));
        var party = new[] { Member("m1", spirit: 500, nerveStacks: 2) };
        var result = Run(instance, party);
        Assert.Equal(2, result.Members[0].NerveStacks);
    }

    [Fact]
    public void A_negative_delta_on_a_channel_other_than_spirit_never_touches_NerveStacks()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":-100}"));
        var party = new[] { Member("m1", nerveStacks: 2) };
        var result = Run(instance, party);
        Assert.Equal(2, result.Members[0].NerveStacks);
    }

    // ---- status.apply ----

    [Fact]
    public void StatusApply_appends_a_BattleStatusSpec_with_seconds_converted_to_ms_and_zero_magnitude()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.bless", "{\"status\":\"blessed\",\"duration\":5}"));
        var party = new[] { Member("m1") };
        var result = Run(instance, party);
        var spec = Assert.Single(result.Members[0].Statuses);
        Assert.Equal("blessed", spec.StatusId);
        Assert.Equal(5000, spec.DurationMs);
        Assert.Equal(0, spec.MagnitudePerPulse);
    }

    [Fact]
    public void StatusApply_preserves_a_members_own_prior_statuses()
    {
        var prior = new BattleStatusSpec("watch", 0, 1000);
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.bless", "{\"status\":\"blessed\",\"duration\":1}"));
        var party = new[] { Member("m1", statuses: new[] { prior }) };
        var result = Run(instance, party);
        Assert.Equal(2, result.Members[0].Statuses.Count);
        Assert.Contains(result.Members[0].Statuses, s => s.StatusId == "watch");
        Assert.Contains(result.Members[0].Statuses, s => s.StatusId == "blessed");
    }

    [Fact]
    public void StatusApply_targeting_a_nerve_id_refuses()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.curse-nerve", "{\"status\":\"nerve.shaken\",\"duration\":1}"));
        var party = new[] { Member("m1") };
        var ex = Assert.Throws<EventDeckRefusal>(() => Run(instance, party));
        Assert.Contains("nerve.shaken", ex.Message);
    }

    // ---- shield.grant ----

    [Fact]
    public void ShieldGrant_sets_the_members_shield_with_amount_and_element()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.ward", "{\"amount\":250,\"element\":\"fire\"}"));
        var party = new[] { Member("m1") };
        var result = Run(instance, party);
        Assert.NotNull(result.Members[0].Shield);
        Assert.Equal(250, result.Members[0].Shield!.BaseHp);
        Assert.Equal(ElementTypeId.Fire, result.Members[0].Shield!.Element);
    }

    [Fact]
    public void ShieldGrant_replaces_a_prior_shield_rather_than_stacking()
    {
        var priorShield = new BattleInnateShield(50);
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.ward", "{\"amount\":250}"));
        var party = new[] { Member("m1", shield: priorShield) };
        var result = Run(instance, party);
        Assert.Equal(250, result.Members[0].Shield!.BaseHp); // replaced, not 50+250
    }

    [Fact]
    public void ShieldGrant_a_second_grant_in_the_same_instance_replaces_the_first()
    {
        var instance = Instance("c1",
            new InstanceAtomRow(0, "atom.ward", "{\"amount\":100}"),
            new InstanceAtomRow(1, "atom.ward2", "{\"amount\":300}"));
        var party = new[] { Member("m1") };
        var result = Run(instance, party);
        Assert.Equal(300, result.Members[0].Shield!.BaseHp);
    }

    // ---- stat.derived ----

    [Fact]
    public void StatDerived_produces_one_grant_per_targeted_standing_member()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.buff", "{\"channel\":\"combat.dodge.chance\",\"op\":\"flat\",\"amount\":50}"));
        var party = new[] { Member("m1"), Member("m2", downed: true) };
        var result = Run(instance, party);
        var grant = Assert.Single(result.StatDerivedGrants);
        Assert.Equal("m1", grant.MemberInstanceId);
        Assert.Equal("c1", grant.ContainerId);
        Assert.Equal("delve:42", grant.Source);
    }

    [Fact]
    public void StatDerived_never_grants_a_downed_member()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.buff", "{}"));
        var party = new[] { Member("m1", downed: true) };
        var result = Run(instance, party);
        Assert.Empty(result.StatDerivedGrants);
    }

    [Fact]
    public void StatDerived_two_atoms_targeting_the_same_member_produce_one_deduped_grant()
    {
        var instance = Instance("c1",
            new InstanceAtomRow(0, "atom.buff", "{}"),
            new InstanceAtomRow(1, "atom.buff", "{}"));
        var party = new[] { Member("m1") };
        var result = Run(instance, party);
        Assert.Single(result.StatDerivedGrants);
    }

    // ---- ui.present ----

    [Fact]
    public void UiPresent_op_banner_calls_the_sink_with_bannerId_and_durationMs()
    {
        var sink = new RecordingUiPresentSink();
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.banner", "{\"op\":\"banner\",\"bannerId\":\"curio.good\",\"durationMs\":3000}"));
        var party = new[] { Member("m1") };
        Run(instance, party, sink);
        var call = Assert.Single(sink.Banners);
        Assert.Equal("curio.good", call.BannerId);
        Assert.Equal(3000, call.DurationMs);
    }

    [Fact]
    public void UiPresent_op_banner_with_no_durationMs_passes_null_through_to_the_sink()
    {
        var sink = new RecordingUiPresentSink();
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.banner", "{\"op\":\"banner\",\"bannerId\":\"curio.good\"}"));
        var party = new[] { Member("m1") };
        Run(instance, party, sink);
        Assert.Null(Assert.Single(sink.Banners).DurationMs);
    }

    [Fact]
    public void UiPresent_a_non_banner_op_refuses()
    {
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.meter", "{\"op\":\"meter\",\"meterId\":\"hp\",\"ratio\":500}"));
        var party = new[] { Member("m1") };
        var ex = Assert.Throws<EventDeckRefusal>(() => Run(instance, party));
        // Not just "the message mentions the atom id" (atom.meter would pass that trivially) --
        // the actual op-guard wording, so a mutation that removes the guard and falls through to a
        // DIFFERENT refusal (missing bannerId) cannot masquerade as this one.
        Assert.Contains("only op:banner", ex.Message);
        Assert.Contains("op 'meter'", ex.Message);
    }

    [Fact]
    public void UiPresent_never_reaches_a_member_state_it_only_calls_the_sink()
    {
        var sink = new RecordingUiPresentSink();
        var instance = Instance("c1", new InstanceAtomRow(0, "atom.banner", "{\"op\":\"banner\",\"bannerId\":\"curio.good\"}"));
        var party = new[] { Member("m1", hp: 500) };
        var result = Run(instance, party, sink);
        Assert.Equal(500, result.Members[0].Pools["hp"]);
    }

    // ---- composition -- the verify line's own headline: a real multi-kind outcome dispatched in one pass ----

    [Fact]
    public void A_mixed_five_kind_instance_dispatches_every_row_correctly_in_one_pass()
    {
        var sink = new RecordingUiPresentSink();
        var instance = Instance("c1",
            new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":100}"),
            new InstanceAtomRow(1, "atom.drain-spirit", "{\"channel\":\"spirit\",\"amount\":-50,\"target\":{\"scope\":\"one\"}}"),
            new InstanceAtomRow(2, "atom.bless", "{\"status\":\"blessed\",\"duration\":2}"),
            new InstanceAtomRow(3, "atom.ward", "{\"amount\":200}"),
            new InstanceAtomRow(4, "atom.buff", "{}"),
            new InstanceAtomRow(5, "atom.banner", "{\"op\":\"banner\",\"bannerId\":\"curio.good\",\"durationMs\":1500}"));
        var party = new[] { Member("m1", hp: 500, spirit: 500, nerveStacks: 0), Member("m2", hp: 500, spirit: 500) };

        var result = Run(instance, party, sink);

        Assert.Equal(600, result.Members[0].Pools["hp"]); // party heal
        Assert.Equal(600, result.Members[1].Pools["hp"]);
        Assert.Equal(450, result.Members[0].Pools["spirit"]); // "one" -> first standing member only
        Assert.Equal(500, result.Members[1].Pools["spirit"]);
        Assert.Equal(StackPerCurio, result.Members[0].NerveStacks); // negative spirit delta on the "one" target
        Assert.Equal(0, result.Members[1].NerveStacks);
        Assert.Equal(2, result.Members.Count(m => m.Statuses.Any(s => s.StatusId == "blessed"))); // party status
        Assert.Equal(200, result.Members[0].Shield!.BaseHp); // party shield
        Assert.Equal(200, result.Members[1].Shield!.BaseHp);
        Assert.Equal(2, result.StatDerivedGrants.Count); // party stat.derived, one grant per member
        Assert.Equal("delve:42", result.StatDerivedGrants[0].Source);
        var banner = Assert.Single(sink.Banners);
        Assert.Equal("curio.good", banner.BannerId);
    }

    [Fact]
    public void Member_order_is_preserved_in_the_result_regardless_of_targeting()
    {
        var instance = Instance("c1",
            new InstanceAtomRow(0, "atom.heal", "{\"channel\":\"hp\",\"amount\":10,\"target\":{\"scope\":\"one\"}}"));
        var party = new[] { Member("m3"), Member("m1"), Member("m2") };
        var result = Run(instance, party);
        Assert.Equal(new[] { "m3", "m1", "m2" }, result.Members.Select(m => m.InstanceId));
    }
}
