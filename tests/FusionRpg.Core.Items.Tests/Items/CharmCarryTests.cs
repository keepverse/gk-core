using System.Reflection;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Thresholds;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `charm-carry` (item module 22, split out of 12 by D40) — the pouch gate, the run-start snapshot and
/// the binding shape. The tuning is the REAL `gk-core/data/tuning/charm-attunement.v1.json` throughout; only
/// the pouches themselves are fixtures, because a pouch is a player's choice and no corpus ships one.
/// Corpus-driven assertions live in <see cref="CharmCarryCorpusTests"/>.
/// </summary>
public class CharmCarryTests
{
    internal static string RepoRoot() => ThresholdGrantTests.RepoRoot();

    internal static CharmAttunementTuning Tuning() => CharmAttunementTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "charm-attunement.v1.json")));

    static AttunedCharm Charm(string instance, string container, string axis, long ap,
        bool unique = false, int? levelReq = null) =>
        new(instance, container, axis, ap, unique, levelReq);

    // ---- the tuning file ---------------------------------------------------------------------------

    [Fact]
    public void The_shipped_tuning_parses_and_carries_the_lanes_own_numbers()
    {
        var t = Tuning();
        Assert.Equal(new[] { 1, 2, 3, 5 }, t.ApCostDomain);          // §3.3
        Assert.Equal(6, t.StartingCapacityAp);                        // §3.3 "6 AP at start"
        Assert.Equal(20, t.CapacityLadder[^1]);                       // "...20 AP at cap" — the LAST RUNG
        Assert.Equal(3, t.AxisCapPerSnapshot);
        Assert.Equal(2, t.CopyCapPerContainer);
        Assert.Equal(1, t.UniqueCarryCopyCap);
        Assert.Equal("charm", t.BindingSource);
        Assert.Equal(-100, t.BindingPriority);
    }

    [Fact]
    public void The_capacity_ladders_last_rung_is_not_a_ceiling_and_the_gate_never_clamps_to_it()
    {
        // AGENTS.md: a cap on a magnitude is a configurable soft cap, never a hard stop. §3.3's "20 AP
        // at cap" is the last AUTHORED rung — a pouch priced against a capacity above every rung is
        // legal, and CapacityAtRung past the end returns the last rung rather than throwing.
        var t = Tuning();
        var huge = t.CapacityLadder[^1] * 1000;

        var axes = new[] { "offense", "survivability", "control", "utility", "economy" };
        var pouch = Enumerable.Range(0, 40)
            .Where(i => i / axes.Length < 3)   // stay inside the axis cap; the budget is what is on trial
            .Select(i => Charm($"i{i}", $"charm.filler-{i:D3}", axes[i % axes.Length], 5))
            .ToList();

        Assert.Empty(CharmPouchGate.Explain(pouch, huge, t));
        Assert.Equal(t.CapacityLadder[^1], t.CapacityAtRung(9999));
    }

    [Fact]
    public void A_capacity_ceiling_key_is_refused_at_load_by_name_rather_than_ignored()
    {
        // The failure this prevents: a balance pass adds `maxCapacityAp`, the parser ignores it, and
        // the file now says something the code does not do. Refusing by name is the only honest answer.
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "charm-attunement.v1.json"))
            .TrimEnd().TrimEnd('}') + ", \"maxCapacityAp\": 20 }";

        var ex = Assert.Throws<CharmAttunementTuningRejection>(() => CharmAttunementTuning.Parse(json));
        Assert.Contains("charm.capacity-ceiling-not-permitted", ex.Message);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, ex.Rejection.Reason);
    }

    [Fact]
    public void A_starting_capacity_below_the_largest_charm_is_refused_at_load()
    {
        // §6.1: "a signet is 5 of 6" — 62% of a starting player's whole capacity is what makes the
        // biggest class a build rather than a stat stick. Start below 5 and every signet is dead content.
        var t = Tuning();
        var broken = t with { CapacityLadder = new long[] { 4, 8, 10 } };
        var ex = Assert.Throws<CharmAttunementTuningRejection>(() => CharmAttunementTuning.Validate(broken));
        Assert.Contains("charm.starting-capacity-below-largest-charm", ex.Message);
    }

    [Fact]
    public void A_unique_carry_cap_looser_than_the_default_is_refused_at_load()
    {
        var t = Tuning();
        var ex = Assert.Throws<CharmAttunementTuningRejection>(
            () => CharmAttunementTuning.Validate(t with { UniqueCarryCopyCap = 3 }));
        Assert.Contains("charm.unique-carry-cap-not-tighter", ex.Message);
    }

    [Fact]
    public void A_non_negative_binding_priority_is_refused_at_load()
    {
        var t = Tuning();
        var ex = Assert.Throws<CharmAttunementTuningRejection>(
            () => CharmAttunementTuning.Validate(t with { BindingPriority = 0 }));
        Assert.Contains("charm.binding-priority-not-below-equipment", ex.Message);
    }

    [Fact]
    public void The_binding_owner_kind_is_unique_actor_and_player_is_refused_at_load_by_name()
    {
        // ⭐ D33(a). This is the one tuning value whose wrong setting is invisible in every test that
        // does not name it: `player:` parses, binds, and then resolves match-wide, so the charm buffs
        // the zombies. The file cannot express the withdrawn option C at all.
        var t = Tuning();
        Assert.Equal("unique-actor", t.BindingOwnerKind);

        foreach (var wrong in new[] { "player", "match", "entity" })
        {
            var ex = Assert.Throws<CharmAttunementTuningRejection>(
                () => CharmAttunementTuning.Validate(t with { BindingOwnerKind = wrong }));
            Assert.Contains("charm.binding-owner-kind-not-actor", ex.Message);
        }
    }

    [Fact]
    public void The_run_start_binding_priority_mirrors_module_18s_draught_priority_value_for_value()
    {
        // ⭐ "One snapshot mechanism, two sources" (ssot-consumables.md §9 item 10) made CHECKABLE.
        // Both real files are read; a balance pass that reorders one run-start layer and forgets the
        // other turns this red instead of silently splitting the layer in two.
        var charm = Tuning().BindingPriority;

        var consumables = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "consumables.v1.json")));
        using (consumables)
        {
            var draught = consumables.RootElement.GetProperty("draughtBindingPriority").GetInt32();
            Assert.Equal(draught, charm);
        }

        Assert.NotEqual(FusionRpg.Core.Items.Consumables.DraughtProjection.BindingSource, Tuning().BindingSource);
    }

    // ---- the gate ----------------------------------------------------------------------------------

    [Fact]
    public void Ap_budget_axis_cap_and_copy_cap_each_refuse_with_their_own_reason()
    {
        var t = Tuning();

        // §6.3 loadout C — 9 AP against 8. One refusal, and it is the budget's.
        var overBudget = new[]
        {
            Charm("a", "charm.signet-hollow-crown", "offense", 5, unique: true),
            Charm("b", "charm.hardened-seedcase", "survivability", 2),
            Charm("c", "charm.rootbound-ward", "survivability", 2),
        };
        var budget = CharmPouchGate.Explain(overBudget, 8, t);
        Assert.Equal(CharmCarryRefusalReason.CharmBudgetExceeded, Assert.Single(budget).Reason);

        // §6.3 loadout D — four offense charms. AxisOverflow names the fourth, and the third copy of
        // the same container trips DuplicateKey as well: two different mistakes, two different fixes.
        var overAxis = new[]
        {
            Charm("a", "charm.signet-hollow-crown", "offense", 5, unique: true),
            Charm("b", "charm.tallykeepers-notch", "offense", 1),
            Charm("c", "charm.tallykeepers-notch", "offense", 1),
            Charm("d", "charm.tallykeepers-notch", "offense", 1),
        };
        var axis = CharmPouchGate.Explain(overAxis, 8, t);
        Assert.Contains(axis, f => f.Reason == CharmCarryRefusalReason.CharmAxisOverflow && f.InstanceId == "d");
        Assert.Contains(axis, f => f.Reason == CharmCarryRefusalReason.DuplicateKey && f.InstanceId == "d");
    }

    [Fact]
    public void A_signet_caps_at_one_copy_while_other_classes_cap_at_two()
    {
        var t = Tuning();

        var twoOrdinary = new[]
        {
            Charm("a", "charm.hardened-seedcase", "survivability", 2),
            Charm("b", "charm.hardened-seedcase", "survivability", 2),
        };
        Assert.Empty(CharmPouchGate.Explain(twoOrdinary, 20, t));

        var twoSignets = new[]
        {
            Charm("a", "charm.signet-hollow-crown", "offense", 5, unique: true),
            Charm("b", "charm.signet-hollow-crown", "offense", 5, unique: true),
        };
        var fails = CharmPouchGate.Explain(twoSignets, 20, t);
        var dup = Assert.Single(fails, f => f.Reason == CharmCarryRefusalReason.DuplicateKey);
        Assert.Contains("unique_carry", dup.Detail);
    }

    [Fact]
    public void The_wide_and_tall_loadouts_of_section_6_3_both_fit_the_same_eight_AP()
    {
        // §6.3's whole point, as a fixture: A and B cost the same and produce genuinely different
        // squads. If either stops fitting, the packing decision the mechanic exists for is gone.
        var t = Tuning();

        var wide = new[]
        {
            Charm("s1", "charm.hardened-seedcase", "survivability", 2),
            Charm("s2", "charm.rootbound-ward", "survivability", 2),
            Charm("o1", "charm.tallykeepers-notch", "offense", 1),
            Charm("o2", "charm.tallykeepers-notch", "offense", 1),
            Charm("e1", "charm.sunwarden-bead", "economy", 1),
            Charm("e2", "charm.sunwarden-bead", "economy", 1),
        };
        var tall = new[]
        {
            Charm("g", "charm.signet-hollow-crown", "offense", 5, unique: true),
            Charm("o1", "charm.tallykeepers-notch", "offense", 1),
            Charm("o2", "charm.tallykeepers-notch", "offense", 1),
            Charm("e1", "charm.sunwarden-bead", "economy", 1),
        };

        Assert.Equal(8, CharmPouchGate.TotalAp(wide));
        Assert.Equal(8, CharmPouchGate.TotalAp(tall));
        Assert.Empty(CharmPouchGate.Explain(wide, 8, t));
        Assert.Empty(CharmPouchGate.Explain(tall, 8, t));
    }

    [Fact]
    public void The_gate_returns_every_refusal_rather_than_first_fail()
    {
        var t = Tuning();
        var pouch = new[]
        {
            Charm("a", "charm.res-offense-2", "offense", 1),                    // NotCarryable
            Charm("b", "charm.x", "offense", 5),
            Charm("c", "charm.x", "offense", 5),
            Charm("d", "charm.x", "offense", 5),                                // axis + copy
            Charm("e", "charm.gated", "economy", 3, levelReq: 40),              // level
        };

        var fails = CharmPouchGate.Explain(pouch, 6, t, playerLevel: 5);
        Assert.Contains(fails, f => f.Reason == CharmCarryRefusalReason.CharmNotCarryable);
        Assert.Contains(fails, f => f.Reason == CharmCarryRefusalReason.CharmBudgetExceeded);
        Assert.Contains(fails, f => f.Reason == CharmCarryRefusalReason.CharmAxisOverflow);
        Assert.Contains(fails, f => f.Reason == CharmCarryRefusalReason.DuplicateKey);
        Assert.Contains(fails, f => f.Reason == CharmCarryRefusalReason.LevelTooLow);
        Assert.True(fails.Count >= 5, $"expected every refusal, got {fails.Count}");
    }

    [Fact]
    public void A_level_gated_charm_with_no_player_level_refuses_rather_than_passing_the_check()
    {
        // ⛔ §8 item 6 is STILL unanswered: `players` is (id, name, created_utc, world_seed) and carries
        // no level. SC6 says reject, never ignore — so a check the gate cannot make refuses by name.
        var t = Tuning();
        var pouch = new[] { Charm("a", "charm.gated", "economy", 1, levelReq: 10) };

        var fail = Assert.Single(CharmPouchGate.Explain(pouch, 20, t, playerLevel: null));
        Assert.Equal(CharmCarryRefusalReason.PlayerLevelUnavailable, fail.Reason);

        Assert.Empty(CharmPouchGate.Explain(pouch, 20, t, playerLevel: 10));
    }

    [Fact]
    public void A_resonance_container_can_never_sit_in_the_pouch_padded_or_unpadded()
    {
        // §4.2: "a `charm.` container with no charm_def row is not attunable — that is how resonance
        // containers stay out of the pouch". The corpus ships UNPADDED ids and module 12 kept that
        // divergence measured rather than normalised, so the gate must recognise BOTH spellings or ten
        // shipped containers walk straight in.
        var t = Tuning();
        foreach (var id in new[] { "charm.res-offense-2", "charm.res-offense-02", "charm.res-survivability-3" })
        {
            Assert.True(CharmPouchGate.IsResonanceContainer(id), id);
            var fail = Assert.Single(CharmPouchGate.Explain(
                new[] { Charm("a", id, "offense", 1) }, 20, t));
            Assert.Equal(CharmCarryRefusalReason.CharmNotCarryable, fail.Reason);
        }

        Assert.False(CharmPouchGate.IsResonanceContainer("charm.surv-util-001"));
    }

    [Fact]
    public void A_container_with_no_charm_def_row_is_not_carryable_even_though_it_resolves()
    {
        var t = Tuning();
        var attunable = new HashSet<string>(StringComparer.Ordinal) { "charm.surv-util-001" };

        Assert.Empty(CharmPouchGate.Explain(
            new[] { Charm("a", "charm.surv-util-001", "survivability", 1) }, 20, t,
            attunableContainerIds: attunable));

        var fail = Assert.Single(CharmPouchGate.Explain(
            new[] { Charm("a", "charm.not-in-the-def-table", "survivability", 1) }, 20, t,
            attunableContainerIds: attunable));
        Assert.Equal(CharmCarryRefusalReason.CharmNotCarryable, fail.Reason);
    }

    [Fact]
    public void Un_attuning_a_held_charm_refuses_CharmInUse_and_names_the_run()
    {
        var t = Tuning();
        var pouch = new[] { Charm("inst-7", "charm.hardened-seedcase", "survivability", 2) };
        var held = new Dictionary<string, string>(StringComparer.Ordinal) { ["inst-7"] = "expedition#4" };

        var fail = Assert.Single(CharmPouchGate.Explain(pouch, 20, t, heldByOtherRun: held));
        Assert.Equal(CharmCarryRefusalReason.CharmInUse, fail.Reason);
        Assert.Contains("expedition#4", fail.Detail);
    }

    [Fact]
    public void The_gate_re_runs_at_run_start_and_can_refuse_what_attunement_allowed()
    {
        // §5.3's own reason for the re-check: capacity can SHRINK. Same pouch, same tuning, different
        // answer — which is exactly the drift a snapshot binding under a stale gate would hide.
        var t = Tuning();
        var pouch = new[]
        {
            Charm("a", "charm.signet-hollow-crown", "offense", 5, unique: true),
            Charm("b", "charm.tallykeepers-notch", "offense", 1),
            Charm("c", "charm.sunwarden-bead", "economy", 1),
        };

        Assert.True(CharmPouchGate.Admits(pouch, 8, t));
        Assert.False(CharmPouchGate.Admits(pouch, 6, t));
    }

    [Fact]
    public void An_AP_total_overflows_by_throwing_never_by_wrapping()
    {
        // AGENTS.md: overflow throws, never wraps. A wrapped AP sum is a pouch that fits everything —
        // the budget silently gone, with a green suite.
        var pouch = new[]
        {
            Charm("a", "charm.x", "offense", long.MaxValue),
            Charm("b", "charm.y", "offense", 1),
        };
        Assert.Throws<OverflowException>(() => CharmPouchGate.TotalAp(pouch));
    }

    [Fact]
    public void A_negative_capacity_throws_rather_than_being_clamped_to_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CharmPouchGate.Explain(Array.Empty<AttunedCharm>(), -1, Tuning()));
    }

    // ---- the run lifecycle -------------------------------------------------------------------------

    [Fact]
    public void Attuning_creates_no_binding_and_the_snapshot_is_what_binds()
    {
        // §3.8: attunement is durable intent, not a runtime fact. The gate produces refusals and
        // nothing else — there is no Bind on it, asserted by reflection so a later convenience method
        // cannot quietly make attunement a binding moment.
        var names = typeof(CharmPouchGate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Bind", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Explain", names);
    }

    [Fact]
    public void Bindings_apply_from_the_run_start_snapshot_not_the_live_pouch()
    {
        // The seal: "an expedition's outcome is sealed at dispatch by recorded seed, and a loadout that
        // changes after the seal makes the seal a lie."
        var t = Tuning();
        var atDispatch = new[]
        {
            Charm("i1", "charm.hardened-seedcase", "survivability", 2),
            Charm("i2", "charm.tallykeepers-notch", "offense", 1),
        };

        var snapshot = CharmRunBinder.Snapshot(atDispatch);

        // the player edits the pouch mid-run; the snapshot does not move
        var afterEdit = atDispatch.Append(Charm("i3", "charm.sunwarden-bead", "economy", 1)).ToList();
        Assert.Equal(3, afterEdit.Count);
        Assert.Equal(2, snapshot.Count);

        var bindings = CharmRunBinder.Bindings(snapshot, new[] { "spec-1" }, Resonance(), t);
        Assert.DoesNotContain(bindings, b => b.ContainerId == "charm.sunwarden-bead");
    }

    [Fact]
    public void The_snapshot_seq_is_stable_across_input_orderings()
    {
        // A determinism input: two replays that disagree about row order do not reproduce.
        var a = new[]
        {
            Charm("i3", "charm.c", "offense", 1),
            Charm("i1", "charm.a", "offense", 1),
            Charm("i2", "charm.b", "economy", 1),
        };
        var b = a.Reverse().ToList();

        Assert.Equal(CharmRunBinder.Snapshot(a), CharmRunBinder.Snapshot(b));
        Assert.Equal(new[] { "i1", "i2", "i3" }, CharmRunBinder.Snapshot(a).Select(h => h.InstanceId));
        Assert.Equal(new[] { 0, 1, 2 }, CharmRunBinder.Snapshot(a).Select(h => h.Seq));
    }

    static OwnerScope Scope(string text)
    {
        var rejection = OwnerScope.TryParse(text, out var scope);
        Assert.True(rejection.IsOk, $"'{text}' did not parse: {rejection}");
        return scope;
    }

    static IReadOnlyList<CharmResonanceRow> Resonance() => CharmResonance.DeriveTable(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "items", "charms", "resonance.json")));

    [Fact]
    public void Resonance_tiers_come_from_module_12s_evaluator_and_are_cumulative()
    {
        // ⭐ The "no forked copy" claim, extended: this module counts nothing of its own. Three
        // survivability charms hold BOTH the 2-tier and the 3-tier, because ThresholdEvaluator's grants
        // are cumulative — a rule this module never restates.
        var table = Resonance();
        var snapshot = CharmRunBinder.Snapshot(new[]
        {
            Charm("i1", "charm.a", "survivability", 1),
            Charm("i2", "charm.b", "survivability", 1),
            Charm("i3", "charm.c", "survivability", 1),
            Charm("i4", "charm.d", "offense", 1),
        });

        var tiers = CharmRunBinder.ResonanceTiers(snapshot, table);
        Assert.Contains(ThresholdContainerIds.CharmResonance("survivability", 2), tiers);
        Assert.Contains(ThresholdContainerIds.CharmResonance("survivability", 3), tiers);
        Assert.DoesNotContain(ThresholdContainerIds.CharmResonance("offense", 2), tiers);
    }

    [Fact]
    public void The_binder_counts_nothing_of_its_own_and_agrees_with_the_evaluator_directly_driven()
    {
        var table = Resonance();
        var snapshot = CharmRunBinder.Snapshot(new[]
        {
            Charm("i1", "charm.a", "economy", 1),
            Charm("i2", "charm.b", "economy", 1),
        });

        var direct = ThresholdEvaluator.Grant(
            CharmResonance.Consumer("economy", table),
            snapshot.Select(h => new HeldCharm(h.ContainerId, h.Axis))).WantedContainerIds;

        Assert.Equal(direct, CharmRunBinder.ResonanceTiers(snapshot, table)
            .Where(id => id.StartsWith("charm.res-economy", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void Every_binding_is_unique_actor_scoped_and_never_player_scoped()
    {
        // ⭐ D33(a), at the row level. One binding per DEPLOYED ACTOR — the count scales with the squad,
        // which is option B's stated cost and the price of a scope the resolver can actually express.
        var t = Tuning();
        var snapshot = CharmRunBinder.Snapshot(new[]
        {
            Charm("i1", "charm.a", "survivability", 2),
            Charm("i2", "charm.b", "survivability", 2),
        });

        var squad = new[] { "spec-1", "spec-2", "spec-3" };
        var bindings = CharmRunBinder.Bindings(snapshot, squad, Resonance(), t);

        Assert.All(bindings, b => Assert.Equal("unique-actor", b.OwnerKind));
        Assert.All(bindings, b => Assert.Contains(b.OwnerKey, squad));
        Assert.All(bindings, b => Assert.Equal("charm", b.Source));
        Assert.All(bindings, b => Assert.Equal(-100, b.Priority));

        // 2 charms + 1 satisfied resonance tier, per actor
        Assert.Equal(squad.Length * 3, bindings.Count);
        Assert.Equal(squad.Length, bindings.Count(b => b.InstanceId is null));
    }

    [Fact]
    public void No_charm_binding_is_ever_written_at_player_or_match_scope()
    {
        // The refusal delegates to module 12's own, so the two layers cannot disagree about which
        // scopes are legal. `player:` returns true unconditionally in StatApplyScope and `match`
        // matches both sides — a player-scoped +atk charm buffs the zombies.
        Assert.Equal(AtomRejectionReason.ScopeUnsupported,
            CharmRunBinder.RefuseUnsupportedScope(Scope("player:7")).Reason);
        Assert.Equal(AtomRejectionReason.ScopeUnsupported,
            CharmRunBinder.RefuseUnsupportedScope(Scope("match")).Reason);
        Assert.True(CharmRunBinder.RefuseUnsupportedScope(Scope("unique-actor:spec-1")).IsOk);
    }

    [Fact]
    public void Withdrawal_is_by_source_and_the_two_run_start_layers_do_not_share_a_key()
    {
        var t = Tuning();
        Assert.Equal("charm", CharmRunBinder.WithdrawalKey(t));
        Assert.NotEqual(FusionRpg.Core.Items.Consumables.DraughtProjection.WithdrawalKey,
            CharmRunBinder.WithdrawalKey(t));

        var ex = Assert.Throws<CharmAttunementTuningRejection>(
            () => CharmAttunementTuning.Validate(t with { BindingSource = "draught" }));
        Assert.Contains("charm.binding-source-collides-with-draught", ex.Message);
    }

    [Fact]
    public void There_is_no_second_snapshot_mechanism_and_the_binder_declares_no_clock()
    {
        // ⛔ P5.5's own instruction: "do not build a second snapshot mechanism". The binder carries no
        // expiry, duration or tick concept of any kind — effect_binding has none either, so a timed
        // buff is a status and a run-scoped one is a lifecycle.
        var members = typeof(CharmRunBinder).GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name).ToList();
        foreach (var banned in new[] { "Expire", "Duration", "Tick", "Until", "Ttl" })
            Assert.DoesNotContain(members, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }

    // ---- import-time authoring rules ---------------------------------------------------------------

    [Fact]
    public void An_ap_cost_outside_the_authored_domain_is_a_content_rule_not_a_new_reason_code()
    {
        var t = Tuning();
        var def = new CharmDef("charm.odd", "Odd", "offense", CharmClass.Standard, 4, false, 1, 0, false);

        var fail = Assert.Single(CharmPouchGate.ValidateForCarry(def, t));
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, fail.Reason);
        Assert.Contains(CharmCarryRules.ApCostOutsideDomain, fail.Detail);
    }

    [Fact]
    public void A_resonance_container_carrying_a_charm_def_row_is_refused_at_import()
    {
        var def = new CharmDef("charm.res-offense-2", "Paired Strikes", "offense", CharmClass.Minor, 1,
            false, 0, 0, false);
        var fails = CharmPouchGate.ValidateForCarry(def, Tuning());
        Assert.Contains(fails, f => f.Detail.Contains(CharmCarryRules.ResonanceIsAttunable, StringComparison.Ordinal));
    }

    [Fact]
    public void A_frame_hint_its_atoms_do_not_serve_is_a_rejection_not_a_warning()
    {
        // §3.7. Inert on today's corpus — all 60 charms declare `any` — and written anyway, because the
        // FIRST frame-restricted charm must not ship as a silent dud.
        var t = Tuning();
        var def = new CharmDef("charm.rooted", "Rooted", "survivability", CharmClass.Minor, 1, false, 0, 0, false);

        Assert.Empty(CharmPouchGate.ValidateForCarry(def, t, "any", new[] { "plant" }));
        Assert.Empty(CharmPouchGate.ValidateForCarry(def, t, "plant", new[] { "plant" }));

        var fail = Assert.Single(CharmPouchGate.ValidateForCarry(def, t, "humanoid", new[] { "plant" }));
        Assert.Contains(CharmCarryRules.FrameHintMismatch, fail.Detail);

        var bad = Assert.Single(CharmPouchGate.ValidateForCarry(def, t, "hybrid", new[] { "plant" }));
        Assert.Contains(CharmCarryRules.FrameHintMismatch, bad.Detail);
    }

    [Fact]
    public void This_module_mints_no_new_reason_code_and_registers_a_namespace_instead()
    {
        // definitions.md §10's list is closed at 33 + ContentRuleViolated. ssot-charms §5.2 asked for
        // five more; this module takes none. The names survive as a MODULE-LOCAL enum (module 4's
        // EquipRefusalReason precedent) and as rule ids under the registered `charm` prefix.
        CharmCarryRules.EnsureRegistered();
        Assert.Contains("charm", ContentRuleNamespaces.All);

        var codes = Enum.GetNames<AtomRejectionReason>();
        foreach (var asked in new[]
                 {
                     "CharmBudgetExceeded", "CharmAxisOverflow", "CharmInUse", "CharmNotCarryable",
                     "CharmAtomNotPermitted",
                 })
            Assert.DoesNotContain(asked, codes);

        // …and every one of the five names still exists somewhere a UI string can look it up.
        var local = Enum.GetNames<CharmCarryRefusalReason>();
        Assert.Contains("CharmBudgetExceeded", local);
        Assert.Contains("CharmAxisOverflow", local);
        Assert.Contains("CharmInUse", local);
        Assert.Contains("CharmNotCarryable", local);
        Assert.Equal("charm.atom-not-permitted", CharmCarryRules.AtomNotPermitted);
    }

    [Fact]
    public void The_gate_carries_no_max_charms_parameter()
    {
        // The cap that must not exist, in this module's own shape: a flat "how many charms may you
        // carry" would undo §3.3's whole argument (a count converges; a size budget packs) and would be
        // a hard progression ceiling wearing a balance name.
        var surface = typeof(CharmPouchGate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(m => m.GetParameters().Select(p => p.Name ?? ""))
            .Concat(typeof(CharmAttunementTuning).GetProperties().Select(p => p.Name))
            .ToList();

        foreach (var banned in new[] { "maxCharms", "charmSlots", "maxCapacity", "capacityCap" })
            Assert.DoesNotContain(surface, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }
}
