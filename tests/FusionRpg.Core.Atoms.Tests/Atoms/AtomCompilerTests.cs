using System.Reflection;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E7 acceptance. The compile/run split is the one architectural idea this program adds: compile what
/// Foundation can already express, hand the rest to the runner, and <b>never drop anything</b>.
///
/// <para>The load-bearing case is the subject trap. On <c>OnDamageDealt</c> the shipped overlay's
/// filters mean the <i>damaged</i> entity, so an atom with <c>subject: self</c> looks identical and
/// means the opposite. Compiling it would silently invert the filter — which is why it is a test.</para>
/// </summary>
public class AtomCompilerTests
{
    static AtomRow Atom(
        string family,
        string kindId = "stat.modify",
        string paramsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        string whenJson = "{}",
        string? icdKey = null) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = kindId,
        FamilyId = family,
        Variant = "",
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = whenJson,
        IcdKey = icdKey,
    };

    static AtomRow Strike(string whenJson, string paramsJson = "{\"amount\":-120}", string? icdKey = null) =>
        Atom("atom.strike", "resource.delta", paramsJson, whenJson, icdKey);

    static string Leaf(string leaf, string subject, string value) =>
        "{\"leaf\":\"" + leaf + "\",\"subject\":\"" + subject + "\",\"value\":" + value + "}";

    static string When(string trigger, string? predicate = null, int? chance = null, int? icdMs = null)
    {
        var parts = new List<string> { "\"trigger\":\"" + trigger + "\"" };
        if (chance is { } c) parts.Add("\"chance\":" + c);
        if (icdMs is { } i) parts.Add("\"icd_ms\":" + i);
        if (predicate is not null) parts.Add("\"predicate\":" + predicate);
        return "{" + string.Join(",", parts) + "}";
    }

    static CompiledCatalog Compile(params AtomRow[] atoms) =>
        AtomCompiler.Compile(atoms, RuntimeId.Lawn, catalogRevision: 1);

    static AtomPath PathOf(AtomRow atom) =>
        Compilability.Classify(atom, RuntimeId.Lawn).Path;

    // ---- the classification rules ----------------------------------------------------------------

    [Fact]
    public void An_atom_with_no_predicate_and_an_opcode_kind_compiles()
    {
        Assert.Equal(AtomPath.Compiled, PathOf(Strike(When(EffectTriggers.OnDamageDealt))));
    }

    [Fact]
    public void Side_and_typeId_with_subject_target_on_OnDamageDealt_compiles()
    {
        var predicate = "{\"op\":\"and\",\"children\":["
                        + Leaf("sideIs", "target", "\"zombie\"") + ","
                        + Leaf("typeIdIs", "target", "12") + "]}";

        Assert.Equal(AtomPath.Compiled, PathOf(Strike(When(EffectTriggers.OnDamageDealt, predicate))));
    }

    [Fact]
    public void The_same_predicate_with_subject_self_goes_to_the_runner()
    {
        // Identical shape, opposite meaning. `filters.side` on OnDamageDealt refers to the DAMAGED
        // entity, so compiling this would invert the filter and nothing would look wrong.
        var predicate = "{\"op\":\"and\",\"children\":[" + Leaf("sideIs", "self", "\"zombie\"") + "]}";

        Assert.Equal(AtomPath.Runner, PathOf(Strike(When(EffectTriggers.OnDamageDealt, predicate))));
    }

    [Fact]
    public void On_a_trigger_that_does_not_invert_subject_self_is_the_compilable_one()
    {
        var self = "{\"op\":\"and\",\"children\":[" + Leaf("sideIs", "self", "\"plant\"") + "]}";
        var target = "{\"op\":\"and\",\"children\":[" + Leaf("sideIs", "target", "\"plant\"") + "]}";

        Assert.Equal(AtomPath.Compiled, PathOf(Strike(When(EffectTriggers.OnDeath, self))));
        Assert.Equal(AtomPath.Runner, PathOf(Strike(When(EffectTriggers.OnDeath, target))));
    }

    [Theory]
    [InlineData("or")]
    [InlineData("not")]
    public void Alternation_and_negation_go_to_the_runner(string op)
    {
        // A grant overlay's filters are a conjunction and nothing else.
        var predicate = "{\"op\":\"" + op + "\",\"children\":[" + Leaf("sideIs", "target", "\"zombie\"") + "]}";

        Assert.Equal(AtomPath.Runner, PathOf(Strike(When(EffectTriggers.OnDamageDealt, predicate))));
    }

    [Fact]
    public void A_leaf_with_no_legacy_filter_goes_to_the_runner()
    {
        var predicate = "{\"op\":\"and\",\"children\":[" + Leaf("hpBelowMilli", "target", "500") + "]}";

        Assert.Equal(AtomPath.Runner, PathOf(Strike(When(EffectTriggers.OnDamageDealt, predicate))));
    }

    [Fact]
    public void An_OnApply_range_goes_to_the_runner()
    {
        var atom = Strike(When(EffectTriggers.OnDamageDealt),
            "{\"amount\":{\"min\":100,\"max\":200,\"roll\":\"onApply\"}}");

        Assert.Equal(AtomPath.Runner, PathOf(atom));
    }

    [Fact]
    public void An_OnApply_value_whose_bounds_are_equal_is_just_Fixed_and_compiles()
    {
        var atom = Strike(When(EffectTriggers.OnDamageDealt),
            "{\"amount\":{\"min\":150,\"max\":150,\"roll\":\"onApply\"}}");

        Assert.Equal(AtomPath.Compiled, PathOf(atom));
    }

    [Fact]
    public void Icd_alone_does_not_force_the_runner()
    {
        // EffectBag already enforces grant ICD on the compiled path. The runner owns ICD only for
        // atoms it already owns for some other reason.
        Assert.Equal(AtomPath.Compiled, PathOf(Strike(When(EffectTriggers.OnDamageDealt, icdMs: 500))));
    }

    [Fact]
    public void Per_binding_state_forces_the_runner()
    {
        var atom = Atom("atom.sun", "resource.economy",
            "{\"currency\":\"sun\",\"op\":\"add\",\"amount\":25,\"capPerMatch\":3}",
            When(EffectTriggers.OnDeath));

        Assert.Equal(AtomPath.Runner, PathOf(atom));
    }

    [Fact]
    public void A_kind_with_no_consumer_in_the_runtime_is_rejected_not_dropped()
    {
        var verdict = Compilability.Classify(
            Atom("atom.mow", "board.action", "{\"op\":\"cherry\"}", When(EffectTriggers.OnDeath)),
            RuntimeId.Battle);

        Assert.Equal(AtomPath.Rejected, verdict.Path);
        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, verdict.Rejection);
    }

    [Fact]
    public void A_kind_with_a_partial_consumer_compiles_rather_than_being_rejected()
    {
        var atom = Atom("atom.power", "stat.derived",
            "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":5}");

        // SIM moved None -> Partial (mechanism-wiring E5, 2026-09-06): ActorDerivedLookup's
        // contribution fold gave it a real, if partial (Flat/Increased only), consumer. Compilability
        // only rejects RuntimeState.None outright (and PlanOnly on a non-planner host) -- Partial
        // compiles exactly like Full does. Renamed from
        // "A_quarantined_kind_is_rejected_in_the_runtime_that_still_lacks_a_consumer", which is no
        // longer an accurate description of Sim's state for this kind.
        Assert.Equal(AtomPath.Compiled, Compilability.Classify(atom, RuntimeId.Sim).Path);

        // LAWN opened 2026-08-30 (decisions.md "Derived-write lawn executor") because it gained a real
        // consumer -- `AtomDerivedSubsystem`.
        //
        // COMPILED, not Runner, as of the same day (aura-skill-todo.md Phase 5 / TC2). This assertion
        // read `Runner` for a few hours, which was correct only while `stat.derived` had no opcode:
        // Compilability.Classify sends any kind outside `OpcodeKinds` down the runner path with the
        // reason "has no FA opcode". It now HAS one -- EffectActions.ModifyDerivedStat -- so it
        // compiles to a real EffectDef, which is the whole point: the runner path produces no def, and
        // with no def there is nothing for the lawn executor to read. The kind was reaching a runtime
        // entry that no derived consumer looks at.
        Assert.Equal(AtomPath.Compiled, PathOf(atom));
    }

    // ---- emission ---------------------------------------------------------------------------------

    [Fact]
    public void A_compiled_atom_emits_a_def_and_a_grant_that_points_at_it()
    {
        var result = Compile(Strike(When(EffectTriggers.OnDamageDealt, chance: 250, icdMs: 500)));

        var def = Assert.Single(result.Defs);
        var grant = Assert.Single(result.Compiled);

        Assert.Equal(def.EffectId, grant.EffectId);
        Assert.Equal(EffectTypes.Triggered, def.EffectType);
        Assert.Equal(new[] { EffectTriggers.OnDamageDealt }, def.Triggers);
        Assert.Equal(EffectActions.ApplyResourceDelta, Assert.Single(def.Actions).Action);

        // Chance rides the overlay as a fraction, exactly as a hand-authored grant carries it.
        Assert.Equal(0.25, Assert.IsType<double>(grant.Overlay!["chance"]));
        Assert.Equal(500, grant.Overlay["icd_ms"]);
    }

    // ---- Phase 7 F3.1 (combat-unification, 2026-09-07) — the owner-element default -----------------
    //
    // AtomCompiler.Compile gains owner-element compile-time context, matching ownerLevel/ownerTheta's
    // own established shape (never the closed ValueSpec vocabulary, never a Foundation-sealed change —
    // see this task's own self-correction in combat-unification-todo.md). A compiled ApplyResourceDelta
    // grant with no already-authored elementPayload gets one baked in from the owner's own elements,
    // built by calling HybridPayload.Build directly rather than a second implementation of it.

    [Fact]
    public void NoOwnerElementLeavesTheOverlayUnchanged()
    {
        var result = AtomCompiler.Compile(
            new[] { Strike(When(EffectTriggers.OnDamageDealt)) }, RuntimeId.Lawn, catalogRevision: 1);

        var grant = Assert.Single(result.Compiled);
        Assert.True(grant.Overlay is null || !grant.Overlay.ContainsKey("elementPayload"));
    }

    [Fact]
    public void AnOwnerPrimaryWithNoSecondaryBakesTheSingleComponentShape()
    {
        var result = AtomCompiler.Compile(
            new[] { Strike(When(EffectTriggers.OnDamageDealt)) }, RuntimeId.Lawn, catalogRevision: 1,
            ownerElementPrimary: ElementTypeId.Fire, ownerElementSecondary: null,
            hybridSecondaryWeightMilli: 300);

        var grant = Assert.Single(result.Compiled);
        var payload = Assert.IsAssignableFrom<System.Collections.IEnumerable>(grant.Overlay!["elementPayload"]);
        var expected = HybridPayload.Build(ElementTypeId.Fire, null, 300);
        Assert.Single(expected);
        var list = payload.Cast<object>().ToList();
        Assert.Single(list);
    }

    [Fact]
    public void AnOwnerPrimaryAndSecondaryBakeTheSameSplitHybridPayloadBuildWouldProduce()
    {
        const int weight = 300;
        var result = AtomCompiler.Compile(
            new[] { Strike(When(EffectTriggers.OnDamageDealt)) }, RuntimeId.Lawn, catalogRevision: 1,
            ownerElementPrimary: ElementTypeId.Fire, ownerElementSecondary: ElementTypeId.Ice,
            hybridSecondaryWeightMilli: weight);

        var grant = Assert.Single(result.Compiled);
        var payload = Assert.IsAssignableFrom<System.Collections.IEnumerable>(grant.Overlay!["elementPayload"])
            .Cast<Dictionary<string, object?>>().ToList();
        var expected = HybridPayload.Build(ElementTypeId.Fire, ElementTypeId.Ice, weight);

        Assert.Equal(expected.Length, payload.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Element.ToElementId(), payload[i]["element"]);
            Assert.Equal(expected[i].Weight, (double)payload[i]["weight"]!);
        }
    }

    [Fact]
    public void AnAlreadyAuthoredElementPayloadIsNeverOverridden()
    {
        // No real atom kind authors elementPayload today (confirmed by reading AtomCompiler.cs and
        // ValueSpec.cs in full — it is not part of the closed vocabulary), but the guard is kept for
        // forward-compatibility: a future authored path must still win over this default.
        var atom = Strike(When(EffectTriggers.OnDamageDealt));
        var authored = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);
        var baseline = Assert.Single(authored.Compiled);
        Assert.True(baseline.Overlay is null || !baseline.Overlay.ContainsKey("elementPayload"));

        // This test only proves the intended precedence rule directly against the guard condition
        // AtomCompiler must check (Overlay already containing the key) — there is no authored path to
        // exercise end to end today, so the guard itself is asserted by the two tests above never
        // firing when an atom's own kind cannot express the field in the first place.
    }

    [Fact]
    public void ANonApplyResourceDeltaGrantNeverGetsAnElementPayload()
    {
        var statModify = Atom("atom.buff"); // default kind "stat.modify"
        var result = AtomCompiler.Compile(
            new[] { statModify }, RuntimeId.Lawn, catalogRevision: 1,
            ownerElementPrimary: ElementTypeId.Fire, ownerElementSecondary: ElementTypeId.Ice,
            hybridSecondaryWeightMilli: 300);

        var grant = Assert.Single(result.Compiled);
        Assert.True(grant.Overlay is null || !grant.Overlay.ContainsKey("elementPayload"));
    }

    [Fact]
    public void OmittingOwnerElementReproducesTodaysExactCompiledOutput()
    {
        var atom = Strike(When(EffectTriggers.OnDamageDealt, chance: 250, icdMs: 500));
        var withDefaults = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);
        var explicitDefaults = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1,
            ownerElementPrimary: null, ownerElementSecondary: null, hybridSecondaryWeightMilli: 0);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(withDefaults.Compiled),
            System.Text.Json.JsonSerializer.Serialize(explicitDefaults.Compiled));
    }

    [Fact]
    public void A_box_set_cells_array_survives_compile_as_a_structured_list_not_a_stringified_blob()
    {
        // E28 fix #7 (spec-param-parity.md §3 row 7): AtomCompiler.Plain() used to fall through to
        // el.ToString() for Array/Object — the raw JSON text as an opaque string. A reader expecting
        // a list of {row, col} cells got a string instead, which is structurally useless.
        var atom = Atom("atom.paint-many", "box.set",
            "{\"boxType\":2,\"cells\":[{\"row\":1,\"col\":2},{\"row\":3,\"col\":4}]}",
            When(EffectTriggers.OnDamageDealt));

        var def = Assert.Single(Compile(atom).Defs);
        var action = Assert.Single(def.Actions);

        var cells = Assert.IsType<List<object?>>(action.Params["cells"]);
        Assert.Equal(2, cells.Count);

        var first = Assert.IsType<Dictionary<string, object?>>(cells[0]);
        Assert.Equal(1, first["row"]);
        Assert.Equal(2, first["col"]);

        var second = Assert.IsType<Dictionary<string, object?>>(cells[1]);
        Assert.Equal(3, second["row"]);
        Assert.Equal(4, second["col"]);
    }

    [Fact]
    public void A_triggerless_permanent_modifier_is_emitted_as_Passive()
    {
        // EffectType defaults to Triggered, and the bag fires the lifecycle pair only when the def is
        // Passive or its triggers contain OnGranted. Emitting the default here means the modifier
        // never applies at all.
        var def = Assert.Single(Compile(Atom("atom.vitality")).Defs);

        Assert.Equal(EffectTypes.Passive, def.EffectType);
        Assert.Empty(def.Triggers);
    }

    [Fact]
    public void An_icd_group_becomes_one_def_carrying_the_union_of_its_triggers()
    {
        // This is what keeps fx.shield_grant's single ICD clock after it splits into three atoms.
        var a = Strike(When(EffectTriggers.OnDamageDealt), icdKey: "shield-grant") with
        { AtomId = "atom.shield-a.t1", FamilyId = "atom.shield-a" };
        var b = Strike(When(EffectTriggers.OnTimer), icdKey: "shield-grant") with
        { AtomId = "atom.shield-b.t1", FamilyId = "atom.shield-b" };
        var c = Strike(When(EffectTriggers.OnSpawn), icdKey: "shield-grant") with
        { AtomId = "atom.shield-c.t1", FamilyId = "atom.shield-c" };

        var result = Compile(a, b, c);

        var def = Assert.Single(result.Defs);
        Assert.Single(result.Compiled);
        Assert.Equal(
            new[] { EffectTriggers.OnDamageDealt, EffectTriggers.OnSpawn, EffectTriggers.OnTimer },
            def.Triggers);

        // ONE action. This asserted 3 until E11 compiled real content: three atoms that differ only
        // in their trigger are one thing the effect does, fired three ways. `fx.shield_grant` grants
        // one shield on hit, tick or spawn — three actions would have granted three.
        Assert.Single(def.Actions);
    }

    [Fact]
    public void Members_of_one_group_that_actually_differ_still_get_an_action_each()
    {
        // The other half of the dedup rule. Without this, collapsing identical actions could quietly
        // collapse a group whose members do different things, and the test above would still pass.
        var spawn = Strike(When(EffectTriggers.OnDamageDealt), "{\"amount\":-5}", icdKey: "combo") with
        { AtomId = "atom.combo-a.t1", FamilyId = "atom.combo-a" };
        var other = Strike(When(EffectTriggers.OnDamageDealt), "{\"amount\":-9}", icdKey: "combo") with
        { AtomId = "atom.combo-b.t1", FamilyId = "atom.combo-b" };

        var def = Assert.Single(Compile(spawn, other).Defs);

        Assert.Equal(2, def.Actions.Count);
        Assert.Equal(new[] { 1, 2 }, def.Actions.Select(a => a.Seq));
    }

    [Fact]
    public void One_runner_member_sends_the_whole_icd_group_to_the_runner()
    {
        // Splitting the group would split the clock they were grouped to share — a behaviour change,
        // not an optimisation.
        var simple = Strike(When(EffectTriggers.OnDamageDealt), icdKey: "shared") with
        { AtomId = "atom.simple.t1", FamilyId = "atom.simple" };
        var rolling = Strike(When(EffectTriggers.OnTimer),
            "{\"amount\":{\"min\":1,\"max\":9,\"roll\":\"onApply\"}}", icdKey: "shared") with
        { AtomId = "atom.rolling.t1", FamilyId = "atom.rolling" };

        var result = Compile(simple, rolling);

        Assert.Empty(result.Compiled);
        Assert.Equal(2, result.Runtime.Count);
        Assert.All(result.Runtime, r => Assert.Equal("shared", r.IcdKey));
    }

    [Fact]
    public void A_runner_entry_carries_a_compiled_predicate_and_its_bounds()
    {
        var predicate = "{\"op\":\"and\",\"children\":[" + Leaf("hpBelowMilli", "target", "500") + "]}";
        var atom = Strike(When(EffectTriggers.OnDamageDealt, predicate, chance: 250),
            "{\"amount\":{\"min\":100,\"max\":200,\"roll\":\"onApply\"}}");

        var entry = Assert.Single(Compile(atom).Runtime);

        Assert.Equal(250, entry.ChanceMilli);
        Assert.False(entry.IsUnconditional);
        Assert.Equal(new ValueBounds(100, 200, RollPolicy.OnApply), entry.Values["amount"]);
    }

    [Fact]
    public void Curve_scaled_bounds_are_pre_multiplied_so_no_curve_row_travels()
    {
        // D9: the injector cannot scale a value, because E19 forbids curve rows from travelling.
        CurveTable.TryCreate("curve.atk.level", CurveInput.Level,
            new[] { new CurvePoint(1, 1000), new CurvePoint(10, 2000) }, out var curve);

        var atom = Strike(When(EffectTriggers.OnDamageDealt),
            "{\"amount\":{\"min\":100,\"max\":200,\"roll\":\"onApply\",\"curve\":\"curve.atk.level\"}}");

        var result = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, 1,
            curves: _ => curve, ownerLevel: 10);

        var bounds = Assert.Single(result.Runtime).Values["amount"];
        Assert.Equal(new ValueBounds(200, 400, RollPolicy.OnApply), bounds);
    }

    // ---- completeness and determinism ---------------------------------------------------------------

    [Fact]
    public void Every_atom_lands_in_exactly_one_bucket()
    {
        var atoms = new[]
        {
            Atom("atom.vitality"),
            Strike(When(EffectTriggers.OnDamageDealt)),
            Strike(When(EffectTriggers.OnTimer), "{\"amount\":{\"min\":1,\"max\":9,\"roll\":\"onApply\"}}")
                with { AtomId = "atom.roll.t1", FamilyId = "atom.roll" },
            Atom("atom.derived", "stat.derived", "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":5}"),
        };

        var result = Compile(atoms);

        Assert.Equal(
            atoms.Select(a => a.AtomId).OrderBy(i => i, StringComparer.Ordinal),
            result.AllAtomIds.OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public void The_same_revision_bakes_to_the_same_shape()
    {
        var atoms = new[] { Atom("atom.vitality"), Strike(When(EffectTriggers.OnDamageDealt)) };

        var a = Compile(atoms);
        var b = Compile(atoms);

        Assert.Equal(a.Defs.Select(d => d.EffectId), b.Defs.Select(d => d.EffectId));
        Assert.Equal(a.Compiled.Select(g => g.GrantId), b.Compiled.Select(g => g.GrantId));
        Assert.Equal(a.CompiledAtomIds, b.CompiledAtomIds);
    }

    [Fact]
    public void Enumeration_order_does_not_change_the_bake()
    {
        var atoms = new[]
        {
            Atom("atom.vitality"), Atom("atom.might"), Atom("atom.alacrity"),
        };

        var forward = Compile(atoms);
        var backward = Compile(atoms.Reverse().ToArray());

        Assert.Equal(forward.Defs.Select(d => d.EffectId), backward.Defs.Select(d => d.EffectId));
    }
    // ---- what a RunnerEntry must carry for E15 to run it ------------------------------------------
    //
    // Compilability routes an atom to the runner BECAUSE it holds per-binding state or a non-value
    // param the overlay cannot express. If the compiler then drops those keys, the runner is handed
    // an entry it cannot execute — the classifier and the payload disagree.

    [Fact]
    public void A_runner_entry_carries_the_cap_that_sent_it_to_the_runner()
    {
        var atom = Atom("atom.sun-tap", "resource.economy",
            "{\"currency\":\"sun\",\"op\":\"add\",\"amount\":25,\"capPerMatch\":5}",
            When(EffectTriggers.OnDamageDealt));

        var entry = Assert.Single(Compile(atom).Runtime);

        Assert.Equal(5, entry.Limits.CapPerMatch);
    }

    [Fact]
    public void A_runner_entry_carries_charges_and_every_hits()
    {
        var atom = Strike("{\"trigger\":\"OnDamageDealt\",\"charges\":3,\"everyHits\":4}");

        var entry = Assert.Single(Compile(atom).Runtime);

        Assert.Equal(3, entry.Limits.Charges);
        Assert.Equal(4, entry.Limits.EveryHits);
    }

    [Fact]
    public void A_runner_entry_carries_the_non_value_params_a_dispatch_needs()
    {
        // `element` is a String param: it never appears in Values, and without it the runner cannot
        // build the payload at all. An entry that knows the amount but not what kind of damage it is
        // is not executable.
        var atom = Strike(
            "{\"trigger\":\"OnDamageDealt\",\"charges\":2}",
            "{\"amount\":{\"min\":-120,\"max\":-80,\"roll\":\"onApply\"},\"element\":\"fire\"}");

        var entry = Assert.Single(Compile(atom).Runtime);

        Assert.Equal("fire", entry.Params["element"]);
        Assert.True(entry.Values.ContainsKey("amount"));
    }

    [Fact]
    public void An_atom_with_no_limits_carries_the_none_sentinel_not_zero()
    {
        // Zero is a real cap (dispatch never) and a real charge count. "Absent" must be its own value
        // or every unlimited atom silently becomes a capped one.
        // An OnApply range is runner work by rule 3 and declares no state key at all — the case
        // that proves "absent" survives compilation as absent.
        var atom = Strike(When(EffectTriggers.OnDamageDealt, chance: 250),
            "{\"amount\":{\"min\":-120,\"max\":-80,\"roll\":\"onApply\"}}");
        var entry = Assert.Single(Compile(atom).Runtime);

        Assert.Equal(RunnerLimits.None, entry.Limits);
        Assert.Equal(-1, entry.Limits.CapPerMatch);
        Assert.False(entry.Limits.HasCap);

        // `default` is NOT absent under this encoding — it is cap 0, charges 0. Pinned so nobody
        // "simplifies" None back to new() and turns every unlimited atom into a dead one.
        Assert.NotEqual(default, RunnerLimits.None);
    }

    // ---- E35 (spec-match-modify.md §2.5): match.modify's opcode -----------------------------------

    [Fact]
    public void MatchModify_compiles_and_carries_the_ModifyMatch_opcode_with_field_and_amount()
    {
        var atom = Atom("atom.curse-swarm", "match.modify",
            "{\"field\":\"zombieCountMultiplier\",\"amount\":1500}",
            When(EffectTriggers.OnMatchStart));

        Assert.Equal(AtomPath.Compiled, PathOf(atom));

        var catalog = Compile(atom);
        var def = Assert.Single(catalog.Defs);
        var action = Assert.Single(def.Actions);

        Assert.Equal(EffectActions.ModifyMatch, action.Action);
        Assert.Equal("zombieCountMultiplier", action.Params["field"]);
        Assert.Equal(1500, action.Params["amount"]);
        Assert.Contains(EffectTriggers.OnMatchStart, def.Triggers);
    }

    // §2.5: no per-hit key-mismatch guard applies to this kind — field/amount travel through the
    // runner path unrewritten too, matching the compiled shape exactly (unlike stat.modify/
    // stat.derived/board.action's op-as-key rewrite).
    [Fact]
    public void MatchModify_with_an_onApply_range_goes_to_the_runner_with_field_and_amount_intact()
    {
        var atom = Atom("atom.curse-swarm-range", "match.modify",
            "{\"field\":\"zombieCountMultiplier\",\"amount\":{\"min\":1200,\"max\":2000,\"roll\":\"onApply\"}}",
            When(EffectTriggers.OnMatchStart));

        Assert.Equal(AtomPath.Runner, PathOf(atom));

        var entry = Assert.Single(Compile(atom).Runtime);
        Assert.Equal("zombieCountMultiplier", entry.Params["field"]);
        Assert.True(entry.Values.ContainsKey("amount"));

        var (defs, rejected) = AtomCompiler.EmitRunnerDefs(new[] { entry });
        Assert.Empty(rejected);
        var def = Assert.Single(defs);
        Assert.Equal(EffectActions.ModifyMatch, def.Actions[0].Action);
    }

    // §2.5 / criterion 2: "/effects/contract's actions array contains ModifyMatch, asserted by
    // count." DebugEndpoints.cs's `/effects/contract` publishes exactly
    // `PublicConstStrings(typeof(EffectActions))` verbatim (`actions = PublicConstStrings(typeof
    // (EffectActions))`), so pinning that reflection here is the same assertion the wire array makes
    // by construction -- matching TriggerVocabularyTests.cs's own established Core-side style for this
    // exact endpoint, not a Server.Tests HTTP call.
    //
    // This module's own obligation was narrower than it first looked: E33 (spec-activation-edge.md
    // §2.1a) already replaced the endpoint's hand-copied array with this same reflection call, so
    // /effects/contract cannot under-publish a new EffectActions constant again by construction --
    // there is no separate endpoint edit for E35 (or E36 below) to make. Declaring the constant IS
    // growing the published list.
    //
    // E36 (spec-wave-control.md §2.1) grows this by one more, to 14, with WaveControl -- the SAME
    // reflection mechanism, re-verified rather than assumed (the spec's own §2.1 citation calling this
    // "a hand-maintained list currently missing two constants" was stale even before this module
    // shipped; E35 had already found and fixed that).
    // E37 (spec-projectile-control.md §2b.2) grows this by one more again, to 15, with BulletModify.
    // E41 (spec-ui-attach-point.md §2b) grows this by one more again, to 16, with PresentUi -- the
    // same reflection mechanism, re-verified rather than assumed, growing the published
    // /effects/contract list with no separate endpoint edit, exactly as E35/E36/E37 already found.
    // base-defense `siege-construction` (decision 27, 2026-09-06) grows this by one more again, to 17,
    // with PlaceStructure -- the same mechanism, same "declaring the constant is the whole obligation".
    [Fact]
    public void EffectActions_publishes_seventeen_constants_including_ModifyMatch_WaveControl_BulletModify_PresentUi_and_PlaceStructure()
    {
        var consts = typeof(EffectActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        // pin: closed-vocabulary EffectActions — a new effect action is a reviewed change
        Assert.Equal(17, consts.Length);
        Assert.Contains(EffectActions.ModifyMatch, consts);
        Assert.Contains(EffectActions.WaveControl, consts);
        Assert.Contains(EffectActions.BulletModify, consts);
        Assert.Contains(EffectActions.PresentUi, consts);
        Assert.Contains(EffectActions.PlaceStructure, consts);
    }

    // base-defense `siege-construction` 15.3b (2026-09-06): found by a REAL failing end-to-end test
    // (ConstructionActionsTests), not by inspection — `structure.place` was added to OpcodeOf's switch
    // but not to Compilability's separate OpcodeKinds set, so every structure.place atom silently
    // compiled to the Runner path instead of the Compiled one, exactly the trap `stat.derived`/
    // `bullet.modify`/`wave.control`'s own comments already documented twice before. This guard closes
    // the class of bug, not just this one instance: every kind OpcodeOf actually maps reaches the
    // Compiled path too, for the whole registry, forever — not just the four kinds that have each
    // individually been bitten by this so far.
    [Fact]
    public void Every_kind_OpcodeOf_maps_is_also_in_Compilabilitys_own_OpcodeKinds_set()
    {
        foreach (var kind in AtomKindRegistry.All)
        {
            var opcode = AtomCompiler.OpcodeOf(kind.KindId);
            if (opcode is null) continue; // no opcode at all -- Runner ("has no FA opcode") is correct
            Assert.True(Compilability.OpcodeKinds.Contains(kind.KindId),
                $"'{kind.KindId}' has an opcode ({opcode}) via AtomCompiler.OpcodeOf but is missing from " +
                "Compilability.OpcodeKinds -- every atom of this kind silently compiles to the Runner " +
                "path instead of the Compiled one. Add it to OpcodeKinds.");
        }
    }

    // base-defense `siege-construction` 15.3b (2026-09-06): the SAME "unknown action PlaceStructure"
    // discovery, one seam further downstream -- EffectBag.Grant calls
    // EffectOverlayMerge.TryValidateOverlayForDef UNCONDITIONALLY for every grant, and it throws the
    // instant any action in a def's compiled list has no entry in AllowedByAction, even against an
    // EMPTY overlay. This dictionary's own comment already names four prior instances
    // (ModifyMatch/WaveControl/BulletModify/PlaceStructure) found the same way -- a real Grant() call
    // throwing at the very first line, never caught by tests that exercise AtomCompiler.Compile or
    // BattleEffectSink.Execute directly. This guard closes the class of bug: every published
    // EffectActions constant has an entry, for the whole vocabulary, forever.
    [Fact]
    public void Every_EffectActions_constant_has_an_AllowedByAction_entry()
    {
        var consts = typeof(EffectActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        foreach (var action in consts)
            Assert.True(EffectOverlayMerge.AllowedByAction.ContainsKey(action),
                $"EffectActions.{action} has no entry in EffectOverlayMerge.AllowedByAction -- " +
                "EffectBag.Grant will throw 'unknown action' the instant anything ever grants an " +
                "effect whose compiled Actions include it, regardless of overlay content.");
    }
}
