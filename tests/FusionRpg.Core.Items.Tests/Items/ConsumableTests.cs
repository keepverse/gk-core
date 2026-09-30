using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Consumables;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// Module 18 <c>consumables</c> — the class, its two closed vocabularies, the atom-layer facts the
/// lane asked for and the shipped runtime gave it, and the per-row validator.
///
/// <para>Every atom-layer claim here is asserted against the SHIPPED registry rather than against the
/// spec's transcription of it, because three of the spec's own numbers have moved since it was
/// written — see the <c>Trigger</c>/<c>Kind</c> count tests.</para>
/// </summary>
public class ConsumableTests
{
    internal static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    internal static string TuningJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "consumables.v1.json"));

    internal static ConsumableTuning Tuning() => ConsumableTuning.Parse(TuningJson());

    static ConsumableDefRow Def(
        string id = "consumable.k1-001",
        ConsumableClass cls = ConsumableClass.Restore,
        UseContext[]? contexts = null,
        int grade = 2,
        string group = "atom.vitality|",
        int manifestCost = 1) =>
        new(id, cls, contexts ?? new[] { UseContext.Menu }, grade, group, manifestCost);

    // ---- the eighth trigger: what the lane asked for, and what shipped -------------------------------

    [Fact]
    public void OnActivate_exists_and_OnUse_is_not_a_trigger_and_never_becomes_one()
    {
        // ssot-consumables.md §4.2 / §9 item 1 asked for an eighth trigger CALLED `OnUse`. A18b landed
        // it as `OnActivate`. One name per concept: the fallback name must not exist anywhere.
        Assert.True(AtomTriggers.IsKnown(AtomTriggers.OnActivate));
        Assert.False(AtomTriggers.IsKnown("OnUse"));
        Assert.DoesNotContain("OnUse", AtomTriggers.All);
        Assert.Equal(new[] { AtomTriggers.OnActivate }, AtomTriggers.Actions);
    }

    [Fact]
    public void OnActivate_is_a_third_category_neither_board_event_nor_grant_lifecycle()
    {
        Assert.DoesNotContain(AtomTriggers.OnActivate, AtomTriggers.Events);
        Assert.DoesNotContain(AtomTriggers.OnActivate, AtomTriggers.Lifecycle);
        Assert.DoesNotContain(AtomTriggers.OnActivate, AtomTriggers.MatchEvents);
        Assert.DoesNotContain(AtomTriggers.OnActivate, AtomTriggers.BoardEconomyEvents);
    }

    [Fact]
    public void OnActivate_is_legal_on_SIX_kinds_today_not_the_specs_four_and_two_are_cosmetic_or_siege_only()
    {
        // spec-consumables.md's Code-style block: "the check asks the registry rather than carrying a
        // list that can drift". So does this test — the expectation is the SET, derived by asking.
        //
        // ⛔ And it has drifted: the spec's evidence table lists FOUR carriers. E41 added `ui.present`
        // afterwards, which takes AllTriggers like the other four. It is harmless here — a present
        // writes no state, carries PowerCategory.None, and is Battle/Sim None so a dispatch consumable
        // naming one is refused by the runtime check anyway. base-defense `siege-construction`
        // (2026-09-06) added a sixth, `structure.place` — also harmless for a Lawn-dispatch consumable
        // specifically: it is Lawn=None (there is no tactical siege board on the PvZ lawn), so naming
        // it is refused by the SAME runtime check `ui.present`'s own carve-out already relies on — but
        // the number in the spec is wrong and the module asserts the shipped set rather than the
        // transcribed one.
        var carriers = AtomKindRegistry.All
            .Where(k => k.AllowsTrigger(AtomTriggers.OnActivate))
            .Select(k => k.KindId)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "resource.delta", "shield.grant", "stat.modify", "status.apply", "structure.place", "ui.present" },
            carriers);

        // the four the spec names really are all there — the drift is an addition, not a substitution
        foreach (var named in new[] { "resource.delta", "shield.grant", "stat.modify", "status.apply" })
            Assert.Contains(named, carriers);
    }

    [Fact]
    public void status_clear_carries_no_OnActivate_and_is_Battle_None_which_is_what_catches_the_corpus_defect()
    {
        // H3, deliberate: `status.clear` stays on the narrow board-event set and its Battle sink does
        // not exist. Both halves are what make `consumable.k2-015` a real refusal rather than a
        // fixture — see ConsumableCorpusTests.
        var clear = AtomKindRegistry.Get("status.clear");
        Assert.NotNull(clear);
        Assert.False(clear!.AllowsTrigger(AtomTriggers.OnActivate));
        Assert.Equal(RuntimeState.None, clear.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.Full, clear.SupportIn(RuntimeId.Lawn));
    }

    [Fact]
    public void A_permanent_stat_modify_with_no_trigger_still_validates_because_the_kind_is_TriggerOptional()
    {
        // The lane wanted `stat.modify` EXCLUDED from the new trigger so "no trigger" would keep its
        // one meaning (definitions §14.2). Shipped code kept the invariant a better way — a third case
        // in a binary that had only two. Assert the mechanism, because the lane's remedy is not what
        // protects the invariant today.
        var statModify = AtomKindRegistry.Get("stat.modify");
        Assert.NotNull(statModify);
        Assert.True(statModify!.TriggerOptional);
        Assert.True(statModify.AllowsTrigger(AtomTriggers.OnActivate));

        // and no other kind needed that third case
        Assert.Equal(
            new[] { "stat.modify" },
            AtomKindRegistry.All.Where(k => k.TriggerOptional).Select(k => k.KindId).ToArray());
    }

    [Fact]
    public void The_specs_own_trigger_and_kind_counts_are_stale_and_the_module_asserts_the_shipped_ones()
    {
        // ⛔ spec-consumables.md's evidence table says "There are 8 triggers, not 7" and cites
        // `TriggerCount = 8`. E34 (spec-trigger-vocabulary.md) took it to 13 afterwards. Nothing here
        // asserts 8; what this module actually depends on is that the count MATCHES the list and that
        // OnActivate is in it.
        Assert.Equal(AtomKindRegistry.TriggerCount, AtomTriggers.All.Length);
        Assert.Equal(AtomKindRegistry.KindCount, AtomKindRegistry.All.Count);
        Assert.Contains(AtomTriggers.OnActivate, AtomTriggers.All);
    }

    // ---- D6, and the ruling that outlived its reason -------------------------------------------------

    [Fact]
    public void resource_delta_is_battle_full_so_the_v1_reason_is_the_use_site_not_the_runtime()
    {
        // ssot-consumables.md §2.3 and §4.1(b) reject in-combat use with ONE argument: "in battle a
        // bound resource.delta is a silent no-op". A18c retired that. The v1 shape does not change; its
        // justification does, and an out-of-date reason attached to a correct decision is how a
        // decision gets reopened for the wrong cause.
        var delta = AtomKindRegistry.Get("resource.delta");
        Assert.NotNull(delta);
        Assert.Equal(RuntimeState.Full, delta!.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.Full, delta.SupportIn(RuntimeId.Lawn));
    }

    [Fact]
    public void shield_grant_is_battle_full_too_so_section_7_3s_SC1_deviation_is_narrower_than_the_lane_states()
    {
        // §7.3 takes the BattleInnateShield setup road because "shield.grant's battle runtime support
        // is None". Re-verified today: it is Full (T14 wired Battle's own Bag.ShieldGate, A18c grew the
        // grant path). ⚠ Sim is still None, which is the part of §12(b) that has NOT closed.
        var grant = AtomKindRegistry.Get("shield.grant");
        Assert.NotNull(grant);
        Assert.Equal(RuntimeState.Full, grant!.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.None, grant.SupportIn(RuntimeId.Sim));
    }

    [Fact]
    public void No_binding_carries_a_duration_which_is_why_a_run_scoped_buff_is_a_lifecycle()
    {
        // §4.5's conclusion, asserted where it is checkable from Core: the withdrawal key is a SOURCE,
        // not a clock, and this module names one rather than inventing a timer.
        Assert.Equal("draught", DraughtProjection.BindingSource);
        Assert.Equal("draught", DraughtProjection.WithdrawalKey);
        Assert.Equal("player", DraughtProjection.BindingOwnerKind);
    }

    // ---- the closed vocabularies --------------------------------------------------------------------

    [Fact]
    public void The_class_and_context_vocabularies_are_closed_at_six_and_six()
    {
        Assert.Equal(6, ConsumableClasses.All.Count);
        Assert.Equal(6, Enum.GetValues<ConsumableClass>().Length);
        Assert.Equal(6, UseContexts.All.Count); // D3.24 widened Menu/Dispatch/Battle/Lawn to add Rest/Curio
        Assert.Equal(6, Enum.GetValues<UseContext>().Length);

        foreach (var c in ConsumableClasses.All)
        {
            Assert.True(ConsumableClasses.TryParse(ConsumableClasses.Wire(c), out var back));
            Assert.Equal(c, back);
        }

        foreach (var u in UseContexts.All)
        {
            Assert.True(UseContexts.TryParse(UseContexts.Wire(u), out var back));
            Assert.Equal(u, back);
        }
    }

    [Fact]
    public void The_use_context_runtime_map_is_derived_from_section_6_2_and_menu_names_no_combat_runtime()
    {
        Assert.Equal(new[] { RuntimeId.Battle }, UseContexts.RuntimesFor(UseContext.Dispatch));
        Assert.Equal(new[] { RuntimeId.Battle }, UseContexts.RuntimesFor(UseContext.Battle));
        Assert.Equal(new[] { RuntimeId.Lawn }, UseContexts.RuntimesFor(UseContext.Lawn));
        // ⛔ menu must not require the game to be running (SC8), and §6.2's own code-4 row names only
        // `battle` and `lawn` as the contexts a host can fail to serve.
        Assert.Empty(UseContexts.RuntimesFor(UseContext.Menu));

        // D3.24 (spec-supplies-and-objects.md §2 table row 2): "neither is a combat runtime; a supply
        // used at rest or at a curio resolves through the battle runtime's OnActivate grant path
        // OUTSIDE a fight" -- the Menu -> [] shape, not the Dispatch/Battle -> [Battle] one.
        Assert.Empty(UseContexts.RuntimesFor(UseContext.Rest));
        Assert.Empty(UseContexts.RuntimesFor(UseContext.Curio));
    }

    [Fact]
    public void Rest_and_curio_wire_spellings_are_the_approved_seed_contract_ones()
    {
        // D3.24: the brief's own "Rest"/"Room" naming loses to the approved seed contract's "rest"/"curio".
        Assert.Equal("rest", UseContexts.Wire(UseContext.Rest));
        Assert.Equal("curio", UseContexts.Wire(UseContext.Curio));
        Assert.True(UseContexts.TryParse("rest", out var rest));
        Assert.Equal(UseContext.Rest, rest);
        Assert.True(UseContexts.TryParse("curio", out var curio));
        Assert.Equal(UseContext.Curio, curio);
        Assert.False(UseContexts.TryParse("room", out _)); // the brief's own spelling was never approved
    }

    [Fact]
    public void Rest_and_curio_are_appended_after_lawn_every_existing_wire_string_stays_byte_identical()
    {
        // §2's own stated reason for append-only placement: UseContextWire sorts by declaration order.
        var all = UseContexts.All;
        Assert.Equal(UseContext.Rest, all[4]);
        Assert.Equal(UseContext.Curio, all[5]);
        Assert.Equal("menu", UseContexts.Wire(all[0]));
        Assert.Equal("dispatch", UseContexts.Wire(all[1]));
        Assert.Equal("battle", UseContexts.Wire(all[2]));
        Assert.Equal("lawn", UseContexts.Wire(all[3]));
    }

    [Fact]
    public void No_new_member_of_the_closed_rejection_list_and_every_rule_is_namespaced()
    {
        // §6.2 proposed four new codes. This module mints none — the closed list stays at 35.
        Assert.Equal(35, Enum.GetValues<AtomRejectionReason>().Length);

        ConsumableRules.EnsureRegistered();
        Assert.Contains(ConsumableRules.Namespace, ContentRuleNamespaces.All);

        foreach (var id in RuleIds())
        {
            Assert.StartsWith(ConsumableRules.Namespace + ".", id, StringComparison.Ordinal);
            Assert.True(ContentRuleNamespaces.IsRegistered(id));
        }
    }

    static IEnumerable<string> RuleIds() =>
        typeof(ConsumableRules)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(ConsumableRules.Namespace))
            .Select(f => (string)f.GetRawConstantValue()!);

    // ---- the structural limits, and their exemption comments -----------------------------------------

    [Fact]
    public void There_is_no_carry_limit_in_the_tuning_file_and_a_reintroduced_one_is_refused_by_name()
    {
        // ⭐ D37 withdrew §10.1's `N = 2`. A tuning that still carries one would silently do nothing,
        // and "the number I set had no effect" is the worst failure a balance file can have.
        var doc = JsonDocument.Parse(TuningJson());
        foreach (var withdrawn in new[] { "carryLimit", "maxManifestEntries", "n", "N" })
            Assert.False(doc.RootElement.TryGetProperty(withdrawn, out _));

        var withN = TuningJson().TrimEnd().TrimEnd('}') + ", \"carryLimit\": 2 }";
        var ex = Assert.Throws<ConsumableTuningRejection>(() => ConsumableTuning.Parse(withN));
        Assert.Contains("D37", ex.Message, StringComparison.Ordinal);
        Assert.Contains("consumableSlots", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_belt_equipped_the_count_is_zero_and_it_is_not_a_default()
    {
        Assert.Equal(0, ConsumableLimits.UnbeltedSlots);
        Assert.Equal(0, BeltCapacity.Unequipped.Slots);
        // ⛔ and a negative slot count is a caller bug, not a stingy belt — it throws rather than
        // clamping to zero, which would hide the bug behind the correct-looking answer.
        Assert.Throws<ArgumentOutOfRangeException>(() => BeltCapacity.FromEquippedGirdle(-1));
    }

    [Fact]
    public void There_is_no_upper_bound_on_a_belts_slots_which_is_the_point_of_D37()
    {
        // A carry limit the player GROWS is a content axis, so nothing here caps it. int.MaxValue is
        // legal and the gate simply passes.
        var belt = BeltCapacity.FromEquippedGirdle(int.MaxValue);
        Assert.Equal(int.MaxValue, belt.Slots);
    }

    [Fact]
    public void Every_structural_limit_carries_the_AGENTS_md_exemption_reason_in_its_own_file()
    {
        // The same grep-shaped guard modules 11 and 17 used: a tidy-up that deletes the justification
        // is the thing that fails, not the number.
        var src = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "FusionRpg.Core", "Items", "Consumables", "ConsumableDef.cs"));
        Assert.Contains("STRUCTURAL", src, StringComparison.Ordinal);
        Assert.Contains("not a progression ceiling", src, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D37", src, StringComparison.Ordinal);
    }

    // ---- the tuning parser refuses rather than defaults ----------------------------------------------

    [Theory]
    [InlineData("classesAuthored")]
    [InlineData("contextsAuthored")]
    [InlineData("gradeTierMap")]
    [InlineData("authoringCeilingPerMille")]
    [InlineData("draughtBindingPriority")]
    public void Stripping_any_key_throws_at_load_rather_than_resolving_to_an_invented_value(string key)
    {
        var doc = JsonDocument.Parse(TuningJson());
        var kept = doc.RootElement.EnumerateObject().Where(p => p.Name != key);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in kept) p.WriteTo(w);
            w.WriteEndObject();
        }

        Assert.Throws<ConsumableTuningRejection>(
            () => ConsumableTuning.Parse(System.Text.Encoding.UTF8.GetString(ms.ToArray())));
    }

    [Fact]
    public void A_grade_map_that_is_not_a_bijection_onto_one_to_five_is_refused_at_load()
    {
        var broken = TuningJson().Replace("\"extreme\": 5", "\"extreme\": 4", StringComparison.Ordinal);
        var ex = Assert.Throws<ConsumableTuningRejection>(() => ConsumableTuning.Parse(broken));
        Assert.Contains("bijection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unauthored_class_or_context_name_in_the_tuning_is_refused_rather_than_ignored()
    {
        var badClass = TuningJson().Replace("\"restore\", \"draught\", \"ward\"",
            "\"restore\", \"draught\", \"potion\"", StringComparison.Ordinal);
        Assert.Throws<ConsumableTuningRejection>(() => ConsumableTuning.Parse(badClass));

        var badCtx = TuningJson().Replace("\"menu\", \"dispatch\"", "\"menu\", \"inventory\"",
            StringComparison.Ordinal);
        Assert.Throws<ConsumableTuningRejection>(() => ConsumableTuning.Parse(badCtx));
    }

    [Fact]
    public void The_authoring_ceiling_is_a_bounded_ratio_and_leaving_the_band_throws()
    {
        var over = TuningJson().Replace("\"authoringCeilingPerMille\": 100",
            "\"authoringCeilingPerMille\": 1001", StringComparison.Ordinal);
        Assert.Throws<ConsumableTuningRejection>(() => ConsumableTuning.Parse(over));

        Assert.Equal(100, Tuning().AuthoringCeilingPerMille);
    }

    [Fact]
    public void The_shipped_tuning_authors_three_classes_and_five_contexts()
    {
        var t = Tuning();
        Assert.Equal(
            new[] { ConsumableClass.Restore, ConsumableClass.Draught, ConsumableClass.Ward },
            t.ClassesAuthored);

        // `battle` joined 2026-09-05 once the action layer served it end to end -- holdsStock reads
        // the precondition (T10) and IStockLedger/RpgStore.TrySpendStock take the stack at commit.
        // Until that second half existed, authoring the context would have shipped a free item.
        // `rest`/`curio` joined D3.24 (spec-supplies-and-objects.md §2) -- neither needs a combat
        // runtime (RuntimesFor maps both to []), so there is no analogous host-readiness gate for them.
        Assert.Equal(
            new[] { UseContext.Menu, UseContext.Dispatch, UseContext.Battle, UseContext.Rest, UseContext.Curio },
            t.ContextsAuthored);
        Assert.False(t.Authors(ConsumableClass.Board));
        Assert.True(t.Authors(UseContext.Battle));
        Assert.True(t.Authors(UseContext.Rest));
        Assert.True(t.Authors(UseContext.Curio));

        // `lawn` stays refused for its OWN reason, not by association: spec-usability-conditions.md
        // §3a's mode matrix makes a holdsStock action not bindable there at all, and capPerMatch (G4)
        // is unimplemented.
        Assert.False(t.Authors(UseContext.Lawn));
        Assert.Equal(-100, t.DraughtBindingPriority);
    }

    // ---- the per-row validator ------------------------------------------------------------------------

    [Fact]
    public void A_consumable_container_with_any_roll_a_rarity_or_a_tier_window_is_refused()
    {
        var fails = ConsumableValidator.ValidateShape(
            Def(), Array.Empty<ConsumableCoreAtom>(), prefixRolls: 1, suffixRolls: 0,
            rarityId: "heirloom", minTier: 2, maxTier: 4, Tuning());

        var ids = fails.Select(f => f.Detail.Split(':')[0]).ToArray();
        Assert.Equal(3, fails.Count(f => f.Detail.StartsWith(ConsumableRules.Rolls, StringComparison.Ordinal)));
        Assert.All(fails, f => Assert.Equal(AtomRejectionReason.ContentRuleViolated, f.Reason));
        Assert.NotEmpty(ids);
    }

    [Fact]
    public void A_clean_row_produces_no_refusal_at_all()
    {
        var fails = ConsumableValidator.ValidateShape(
            Def(), new[] { new ConsumableCoreAtom("atom.vitality.t2", "stat.derived", 2, "{}") },
            0, 0, null, null, null, Tuning());
        Assert.Empty(fails);
    }

    [Fact]
    public void A_consumable_authoring_chance_or_icd_ms_is_refused_because_the_lifecycle_path_honours_neither()
    {
        var chance = ConsumableValidator.ValidateShape(
            Def(), new[] { new ConsumableCoreAtom("atom.vitality.t2", "resource.delta", 2, "{\"chance\":500}") },
            0, 0, null, null, null, Tuning());
        Assert.Single(chance);
        Assert.StartsWith(ConsumableRules.ParamNotHonoured, chance[0].Detail, StringComparison.Ordinal);
        Assert.Contains("FireGrant", chance[0].Detail, StringComparison.Ordinal);

        var icd = ConsumableValidator.ValidateShape(
            Def(), new[] { new ConsumableCoreAtom("atom.vitality.t2", "resource.delta", 2, "{\"icd_ms\":2000}") },
            0, 0, null, null, null, Tuning());
        Assert.Single(icd);
        Assert.StartsWith(ConsumableRules.ParamNotHonoured, icd[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_consumable_names_OnActivate_and_validates_over_a_real_resource_delta_atom()
    {
        var fails = ConsumableValidator.ValidateShape(
            Def(contexts: new[] { UseContext.Dispatch }),
            new[] { new ConsumableCoreAtom("atom.vitality.t2", "resource.delta", 2,
                "{\"trigger\":\"OnActivate\"}") },
            0, 0, null, null, null, Tuning());
        Assert.Empty(fails);
    }

    [Fact]
    public void An_OnUse_trigger_is_refused_by_name_and_the_message_says_what_shipped_instead()
    {
        var fails = ConsumableValidator.ValidateShape(
            Def(), new[] { new ConsumableCoreAtom("atom.vitality.t2", "resource.delta", 2,
                "{\"trigger\":\"OnUse\"}") },
            0, 0, null, null, null, Tuning());
        Assert.Single(fails);
        Assert.StartsWith(ConsumableRules.TriggerNotAllowed, fails[0].Detail, StringComparison.Ordinal);
        Assert.Contains("OnActivate", fails[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_atom_naming_a_trigger_its_kind_forbids_is_refused_by_asking_the_registry()
    {
        // `board.action` does not carry OnActivate — it stays on the widened board-event set (H3).
        var fails = ConsumableValidator.ValidateShape(
            Def(contexts: new[] { UseContext.Menu }),
            new[] { new ConsumableCoreAtom("atom.board.t2", "board.action", 2,
                "{\"trigger\":\"OnActivate\"}") },
            0, 0, null, null, null, Tuning());
        Assert.Contains(fails, f => f.Detail.StartsWith(ConsumableRules.TriggerNotAllowed, StringComparison.Ordinal));
    }

    [Fact]
    public void Grade_must_equal_the_tier_of_every_core_atom()
    {
        var fails = ConsumableValidator.ValidateShape(
            Def(grade: 2),
            new[]
            {
                new ConsumableCoreAtom("atom.vitality.t2", "stat.derived", 2, "{}"),
                new ConsumableCoreAtom("atom.mending.t4", "stat.derived", 4, "{}"),
            },
            0, 0, null, null, null, Tuning());
        Assert.Single(fails);
        Assert.StartsWith(ConsumableRules.GradeMismatch, fails[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_planted_violation_proves_the_invisible_nerf_guard_has_teeth()
    {
        // failure mode 5, and the reason it is checked at catalog load: `board.action` is
        // Battle = None, so a dispatch-context consumable carrying one would bind and do nothing.
        Assert.Equal(RuntimeState.None, AtomKindRegistry.Get("board.action")!.SupportIn(RuntimeId.Battle));

        var fails = ConsumableValidator.ValidateShape(
            Def(contexts: new[] { UseContext.Dispatch }),
            new[] { new ConsumableCoreAtom("atom.board.t2", "board.action", 2, "{}") },
            0, 0, null, null, null, Tuning());

        Assert.Contains(fails, f => f.Detail.StartsWith(ConsumableRules.RuntimeUnsupported, StringComparison.Ordinal));
    }

    [Fact]
    public void A_declare_only_class_is_refused_by_name_with_the_reason_it_has_no_executor()
    {
        foreach (var cls in new[] { ConsumableClass.Board, ConsumableClass.Revive, ConsumableClass.Utility })
        {
            var fails = ConsumableValidator.ValidateShape(
                Def(cls: cls), Array.Empty<ConsumableCoreAtom>(), 0, 0, null, null, null, Tuning());
            Assert.Contains(fails, f => f.Detail.StartsWith(ConsumableRules.ClassUnavailable, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_battle_use_context_is_now_ACCEPTED_and_only_lawn_is_still_refused()
    {
        // ⭐ The "before" this test used to pin: both contexts refused, citing ssot-consumables.md
        // §9.5(b)'s A3/A4 blocker. That blocker had already been ANSWERED on 2026-08-27 ("consuming
        // the item is a precondition, not a cost") and shipped as LeafId.HoldsStock on 2026-08-28.
        // What was genuinely missing was the spend, which now exists -- so `battle` is authored.
        var battle = ConsumableValidator.ValidateShape(
            Def(contexts: new[] { UseContext.Battle }), Array.Empty<ConsumableCoreAtom>(),
            0, 0, null, null, null, Tuning());
        Assert.DoesNotContain(battle,
            f => f.Detail.StartsWith(ConsumableRules.UseContextUnsupported, StringComparison.Ordinal));

        // `lawn` is refused on its own merits, and the message says which of the two reasons applies.
        var lawn = ConsumableValidator.ValidateShape(
            Def(contexts: new[] { UseContext.Lawn }), Array.Empty<ConsumableCoreAtom>(),
            0, 0, null, null, null, Tuning());
        var fail = Assert.Single(lawn,
            f => f.Detail.StartsWith(ConsumableRules.UseContextUnsupported, StringComparison.Ordinal));
        Assert.Contains("not bindable", fail.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Widening_a_context_stays_one_line_of_tuning_and_never_invalidates_a_row()
    {
        // §4.1's no-migration proof, still true in the other direction: a tuning that has not yet
        // authored `battle` refuses it by name, and the whole change is this list.
        const string liveContextsLine = "\"contextsAuthored\": [\"menu\", \"dispatch\", \"battle\", \"rest\", \"curio\"]";
        var json = TuningJson();
        Assert.Contains(liveContextsLine, json, StringComparison.Ordinal); // fails loudly, not silently, the day this literal next drifts
        var narrower = ConsumableTuning.Parse(json.Replace(
            liveContextsLine, "\"contextsAuthored\": [\"menu\", \"dispatch\"]", StringComparison.Ordinal));

        Assert.False(narrower.Authors(UseContext.Battle));
        Assert.Contains(
            ConsumableValidator.ValidateShape(
                Def(contexts: new[] { UseContext.Battle }), Array.Empty<ConsumableCoreAtom>(),
                0, 0, null, null, null, narrower),
            f => f.Detail.StartsWith(ConsumableRules.UseContextUnsupported, StringComparison.Ordinal));
    }

    [Fact]
    public void Grade_and_manifest_cost_outside_their_structural_bounds_are_refused()
    {
        foreach (var grade in new[] { 0, 6 })
            Assert.Contains(
                ConsumableValidator.ValidateShape(Def(grade: grade), Array.Empty<ConsumableCoreAtom>(),
                    0, 0, null, null, null, Tuning()),
                f => f.Detail.StartsWith(ConsumableRules.BadValue, StringComparison.Ordinal));

        Assert.Contains(
            ConsumableValidator.ValidateShape(Def(manifestCost: 0), Array.Empty<ConsumableCoreAtom>(),
                0, 0, null, null, null, Tuning()),
            f => f.Detail.StartsWith(ConsumableRules.BadValue, StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrong_container_kind_is_refused_BY_NAME_now_that_X7_has_minted_Consumable()
    {
        // ⭐ CORRECTED 2026-09-07 (container-kind-expansion, X7 landed): this used to prove EVERY
        // kind was refused because the enum had no `Consumable` value at all. It now proves the
        // real, narrower rule: binding to anything OTHER than `Consumable` is a real content
        // mistake, and binding to `Consumable` itself is accepted.
        Assert.True(ConsumableLimits.ConsumableContainerKindAvailable);
        // 14, not 7: `Gem`/`Charm`/`Combo`/`Consumable` are container-kind-expansion's four
        // additions (2026-09-07) alongside the pre-existing seven, `Enemy` included; then
        // `EmpireTitle`/`ActorTitle` (achievement-title T3) and `Relic` (empire-development Task
        // 1.3a). Merged 2026-09-16 — each program asserted its own total on its own branch, so the
        // merged count is both. Reviewed vocabulary growth, not drift, and none of it this module's.
        // 14 -> 15 (species-progression SP3.2): ContainerKind.SpeciesProgression, also none of this
        // module's own kinds, so the refusal claim below still holds.
        Assert.Equal(15, Enum.GetValues<ContainerKind>().Length);
        Assert.Contains("Consumable",
            Enum.GetNames<ContainerKind>(), StringComparer.Ordinal);

        var fails = ConsumableValidator.ValidateDef(
            Def(), ContainerKind.Item, Array.Empty<ConsumableCoreAtom>(), 0, 0, null, null, null, Tuning());
        var kindFail = Assert.Single(fails,
            f => f.Detail.StartsWith(ConsumableRules.ContainerKindUnavailable, StringComparison.Ordinal));
        Assert.Contains("Consumable", kindFail.Detail, StringComparison.Ordinal);
        Assert.Contains("X7", kindFail.Detail, StringComparison.Ordinal);

        // The real kind itself is accepted — no ContainerKindUnavailable finding.
        Assert.DoesNotContain(
            ConsumableValidator.ValidateDef(
                Def(), ContainerKind.Consumable, Array.Empty<ConsumableCoreAtom>(), 0, 0, null, null, null, Tuning()),
            f => f.Detail.StartsWith(ConsumableRules.ContainerKindUnavailable, StringComparison.Ordinal));

        // …and with no container to bind at all, only the shape rules run.
        Assert.Empty(ConsumableValidator.ValidateDef(
            Def(), null, Array.Empty<ConsumableCoreAtom>(), 0, 0, null, null, null, Tuning()));
    }

    [Fact]
    public void No_scalar_effect_column_exists_anywhere_in_the_module()
    {
        // Success criterion 1, as a grep rather than a review: the one thing that would make the later
        // absorption a migration is an effect encoded as a number on a row.
        var dir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Consumables");
        var dal = Path.Combine(RepoRoot(), "src", "FusionRpg.Data", "Sqlite", "RpgStore.Consumables.cs");
        var files = Directory.GetFiles(dir, "*.cs").Append(dal);

        foreach (var f in files)
        {
            var code = CodeOnly(File.ReadAllText(f));
            foreach (var banned in new[] { "heal_amount", "HealAmount", "duration_ms", "DurationMs", "shield_hp" })
                Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Comment lines stripped, so an honest explanation of a rule is never the thing that fails a
    /// grep-shaped guard — module 11's own <c>DropVolumeTests.CodeOnly</c> lesson, reused.
    /// </summary>
    internal static string CodeOnly(string source) =>
        string.Join("\n", source.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public void The_module_builds_no_second_scheduler()
    {
        // Boundaries, "Never": no timer, no cooldown, no queue in this module. `cooldown_key` is a
        // COLUMN carried for the action layer and is inert; nothing reads a clock.
        var dir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Consumables");
        foreach (var f in Directory.GetFiles(dir, "*.cs"))
        {
            var code = CodeOnly(File.ReadAllText(f));
            foreach (var banned in new[] { "Timer", "Stopwatch", "DateTime.UtcNow", "Task.Delay", "Queue<" })
                Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
        }
    }
}
