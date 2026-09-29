using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Grants;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai `lawn-held-actions` (module 16, CAI4.2, spec-lawn-held-actions.md) — the per-match store
/// behind the lawn's held-action feed. Rows 1–5 and 8–9 of the spec's test table are this file's;
/// rows 6 and 7 (`Remove(ptr)` / `Clear()` on the ptr binding) belong to the injector's
/// <c>LawnHeldActionRegistry</c> (CAI4.3) and are asserted here only at the match boundary the store
/// owns.
///
/// <para>Lives in the Balance project because this lane's fence covers
/// <c>gk-core/tests/FusionRpg.Core.Balance.Tests/**</c>; the project already hosts this program's decision and
/// schedule policy tests, and no new test project can be registered from inside the lane
/// (`FusionRpg.slnx` and <c>gk-core/scripts/verification-boundaries.v1.json</c> both belong to other sessions).</para>
/// </summary>
public class LawnHeldActionSetsTests
{
    // ---- fixtures ---------------------------------------------------------------------------

    static SpeciesBasicsRow Basics(string species = "species.alpha") =>
        new(species, "act.attack", "act.guard", "act.move", "act.innate");

    static ActionGrantRow Grant(string actionId, string source, string role = "") =>
        new(OwnerKind.UniqueActor, "subject.1", actionId, source, role);

    /// <summary>A minimal real <see cref="CompiledAction"/>: the same shape the sibling
    /// `ActionScheduleMatchesCorePolicyTests` fixture uses, so this file leans on the shipped record
    /// rather than inventing a second action model.</summary>
    static CompiledAction Action(string id, params ActionTag[] tags) => new(
        id, ActionKind.Skill, 1, tags, true, 1, false, false, "container." + id,
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static ActionCatalog Catalog(params CompiledAction[] actions) => ActionCatalog.Build(actions);

    /// <summary>The four intrinsic basics every <see cref="Basics"/> row names must be resolvable, or
    /// the assembly's own input is the thing under test rather than the store. `extra` is the granted
    /// and species-specific actions a case adds on top.</summary>
    static ActionCatalog CatalogWithBasics(params CompiledAction[] extra)
    {
        var list = new List<CompiledAction>
        {
            Action("act.attack", ActionTag.Offensive),
            Action("act.guard", ActionTag.Defensive),
            Action("act.move", ActionTag.Movement),
            Action("act.innate", ActionTag.Utility),
        };
        list.AddRange(extra);
        return ActionCatalog.Build(list);
    }

    /// <summary>The closed vocabulary's own ordering, read from the shared comparison rather than
    /// transcribed — the store's contract is "ordered by ONE comparison", not a pinned sequence.</summary>
    static string[] ExpectedOrder(IEnumerable<CompiledAction> actions)
    {
        var list = actions.ToList();
        list.Sort(ActionTagPreference.Compare);
        return list.Select(a => a.ActionId).ToArray();
    }

    sealed class RefusalLog
    {
        readonly List<string> _messages = new();
        public void Report(string message) => _messages.Add(message);
        public int Count => _messages.Count;
        public string Last => _messages[^1];
    }

    // ---- row 1: determinism ----------------------------------------------------------------

    [Fact]
    public void Assembling_the_same_inputs_twice_yields_the_same_ids_in_the_same_order()
    {
        var catalog = CatalogWithBasics(Action("act.skill", ActionTag.Heal));
        var grants = new[] { Grant("act.skill", "item.1") };

        var first = new LawnHeldActionSets(catalog);
        var second = new LawnHeldActionSets(catalog);
        var a = first.PushSpecies("species.alpha", Basics(), grants, _ => false).Held;
        var b = second.PushSpecies("species.alpha", Basics(), grants, _ => false).Held;

        Assert.Equal(ExpectedOrder(a), a.Select(x => x.ActionId).ToArray());
        Assert.Equal(a.Select(x => x.ActionId), b.Select(x => x.ActionId));
    }

    [Fact]
    public void Held_ids_are_ordered_by_the_shared_tag_preference_not_by_insertion_order()
    {
        // The store's own sort, over a deliberately reversed input: a low-priority Utility action and
        // a Movement one assembled before the Offensive attack. `ActionTagPreference`'s rank map is the
        // closed, code-owned vocabulary (Offensive < ... < Movement < Utility), so this pins the
        // CONTRACT rather than a population.
        var catalog = CatalogWithBasics();
        var sets = new LawnHeldActionSets(catalog);

        var held = sets.PushSpecies("species.alpha", Basics(), Array.Empty<ActionGrantRow>(), _ => false).Held;

        Assert.Equal(new[] { "act.attack", "act.guard", "act.move", "act.innate" }, held.Select(x => x.ActionId).ToArray());
    }

    // ---- row 2: the freeze, and the match boundary -----------------------------------------

    [Fact]
    public void A_grant_added_after_the_freeze_is_absent_until_the_next_match()
    {
        var catalog = CatalogWithBasics(Action("act.late", ActionTag.Heal));
        var sets = new LawnHeldActionSets(catalog);

        sets.PushSpecies("species.alpha", Basics(), Array.Empty<ActionGrantRow>(), _ => false);
        var frozenSnapshot = sets.SetFor("species.alpha")!.Snapshotted();

        // The grant arrives mid-match. A second push must NOT re-assemble: the freeze is the contract,
        // and re-assembling here is the mutation this test kills.
        var midMatch = sets.PushSpecies("species.alpha", Basics(), new[] { Grant("act.late", "item.late") }, _ => false);

        Assert.False(midMatch.FrozenNow);
        Assert.DoesNotContain("act.late", midMatch.Held.Select(x => x.ActionId));
        Assert.DoesNotContain("act.late", sets.SetFor("species.alpha")!.Snapshotted().Actions.Select(x => x.ActionId));
        Assert.Equal(
            frozenSnapshot.Actions.Select(x => x.ActionId),
            sets.SetFor("species.alpha")!.Snapshotted().Actions.Select(x => x.ActionId));

        // The match boundary is the lawn's RefreshAtNextRunStart: the next push in the next match
        // re-assembles for real and the grant is present.
        sets.BeginMatch();
        var nextMatch = sets.PushSpecies("species.alpha", Basics(), new[] { Grant("act.late", "item.late") }, _ => false);

        Assert.True(nextMatch.FrozenNow);
        Assert.Contains("act.late", nextMatch.Held.Select(x => x.ActionId));
    }

    [Fact]
    public void BeginMatch_drops_every_freeze_so_the_next_push_assembles_again()
    {
        var catalog = CatalogWithBasics();
        var sets = new LawnHeldActionSets(catalog);
        sets.PushSpecies("species.alpha", Basics(), Array.Empty<ActionGrantRow>(), _ => false);
        sets.PushBound("subject.1", Basics(), Array.Empty<ActionGrantRow>(), _ => false);

        sets.BeginMatch();

        Assert.Equal(0, sets.KeyCount);
        Assert.Equal(0, sets.AssemblyCount);
        Assert.Null(sets.SetFor("species.alpha"));
        Assert.Empty(sets.HeldFor("species.alpha"));
        Assert.Empty(sets.HeldFor("species.alpha", "subject.1"));
    }

    // ---- row 3: one set per key per match, never per ptr -----------------------------------

    [Fact]
    public void Many_push_of_one_key_share_one_frozen_set_and_assemble_once()
    {
        var catalog = CatalogWithBasics(Action("act.skill", ActionTag.Heal));
        var sets = new LawnHeldActionSets(catalog);

        // A default-attack grant is the one input `ActionSetAssembler` consults the eligibility
        // delegate for, which makes the delegate a real assembly counter rather than a proxy.
        var eligibilityCalls = 0;
        bool Eligible(string id) { eligibilityCalls++; return true; }

        var grants = new[] { Grant("act.skill", "item.1", ActionGrantRoles.DefaultAttack) };
        var first = sets.PushSpecies("species.alpha", Basics(), grants, Eligible);
        var second = sets.PushSpecies("species.alpha", Basics(), grants, Eligible);
        var third = sets.PushSpecies("species.alpha", Basics(), grants, Eligible);

        Assert.True(first.FrozenNow);
        Assert.False(second.FrozenNow);
        Assert.False(third.FrozenNow);
        Assert.Same(first.Held, second.Held);
        Assert.Same(first.Held, third.Held);
        Assert.Same(sets.SetFor("species.alpha"), sets.SetFor("species.alpha"));
        Assert.Equal(1, sets.AssemblyCount);
        Assert.Equal(1, eligibilityCalls);
    }

    // ---- row 4: the bound instance key wins ------------------------------------------------

    [Fact]
    public void A_bound_instance_key_resolves_to_its_own_set_not_its_species_set()
    {
        var catalog = CatalogWithBasics(Action("act.species", ActionTag.Heal), Action("act.bound", ActionTag.Buff));
        var sets = new LawnHeldActionSets(catalog);

        sets.PushSpecies("species.alpha", Basics(), new[] { Grant("act.species", "item.s") }, _ => false);
        sets.PushBound("subject.1", Basics(), new[] { Grant("act.bound", "item.b") }, _ => false);
        sets.PushSpecies("species.beta", Basics("species.beta"), new[] { Grant("act.bound", "item.b") }, _ => false);

        var boundIds = sets.HeldFor("species.alpha", "subject.1").Select(x => x.ActionId).ToArray();
        Assert.Contains("act.bound", boundIds);
        Assert.DoesNotContain("act.species", boundIds);

        // An unbound ptr of the same species still gets the species set — the instance key is a
        // precedence, not a replacement.
        var speciesIds = sets.HeldFor("species.alpha").Select(x => x.ActionId).ToArray();
        Assert.Contains("act.species", speciesIds);
        Assert.DoesNotContain("act.bound", speciesIds);

        // And a bound key unknown to this match falls back to its species, never to nothing.
        Assert.Contains("act.bound", sets.HeldFor("species.beta", "subject.other").Select(x => x.ActionId));
    }

    // ---- row 5: no set means empty, never the basic attack ---------------------------------

    [Fact]
    public void A_species_with_no_pushed_set_yields_empty_and_never_a_basic_attack()
    {
        var catalog = Catalog(Action("act.attack", ActionTag.Offensive), Action("act.basic", ActionTag.Offensive));
        var sets = new LawnHeldActionSets(catalog);

        var held = sets.HeldFor("species.unknown");

        Assert.Empty(held);
        Assert.Empty(sets.HeldFor("species.unknown", "subject.nobody"));
        Assert.Null(sets.SetFor("species.unknown"));
        Assert.DoesNotContain(held, a => a.ActionId == "act.basic");
    }

    // ---- rows 8 & 9: loud once, never silently dropped -------------------------------------

    [Fact]
    public void Compile_failure_reports_once_and_is_refused_on_every_later_call()
    {
        var catalog = CatalogWithBasics();
        var log = new RefusalLog();
        var sets = new LawnHeldActionSets(catalog, log.Report);
        var grants = new[] { Grant("act.missing", "item.broken") };

        var first = sets.PushSpecies("species.alpha", Basics(), grants, _ => false);
        var second = sets.PushSpecies("species.alpha", Basics(), grants, _ => false);
        var third = sets.PushSpecies("species.alpha", Basics(), grants, _ => false);

        Assert.True(first.Refused);
        Assert.True(second.Refused);
        Assert.True(third.Refused);
        Assert.Empty(first.Held);
        Assert.Empty(second.Held);
        Assert.Empty(third.Held);
        Assert.Equal(1, log.Count);
        Assert.Contains("act.missing", log.Last);
        Assert.Contains("species.alpha", log.Last);
        Assert.Contains("act.missing", sets.RefusalReasonFor("species.alpha")!);
    }

    [Fact]
    public void An_unknown_id_refuses_the_whole_set_rather_than_silently_dropping_it()
    {
        var catalog = CatalogWithBasics(Action("act.known", ActionTag.Heal));
        var log = new RefusalLog();
        var sets = new LawnHeldActionSets(catalog, log.Report);

        // One resolvable grant beside one unresolvable id: a partial set would run the actor with a kit
        // the push never described, so the whole key is refused instead.
        var held = sets.PushSpecies(
            "species.alpha", Basics(),
            new[] { Grant("act.known", "item.ok"), Grant("act.ghost", "item.bad") }, _ => false).Held;

        Assert.Empty(held);
        Assert.Equal(1, log.Count);
        Assert.Contains("act.ghost", log.Last);
        Assert.DoesNotContain("act.known", log.Last);
    }

    [Fact]
    public void A_refusal_after_the_match_boundary_reports_again_because_it_re_assembled()
    {
        var catalog = CatalogWithBasics();
        var log = new RefusalLog();
        var sets = new LawnHeldActionSets(catalog, log.Report);
        var grants = new[] { Grant("act.missing", "item.broken") };

        sets.PushSpecies("species.alpha", Basics(), grants, _ => false);
        sets.BeginMatch();
        sets.PushSpecies("species.alpha", Basics(), grants, _ => false);

        // "Loud once" is once PER ASSEMBLY, not once per process: a new match genuinely assembled again.
        Assert.Equal(2, log.Count);
    }

    // ---- hygiene ---------------------------------------------------------------------------

    [Fact]
    public void Push_rejects_an_empty_key_and_null_inputs()
    {
        var sets = new LawnHeldActionSets(Catalog(Action("act.attack", ActionTag.Offensive)));

        Assert.Throws<ArgumentException>(() =>
            sets.PushSpecies("", Basics(), Array.Empty<ActionGrantRow>(), _ => false));
        Assert.Throws<ArgumentNullException>(() =>
            sets.PushSpecies("species.alpha", null!, Array.Empty<ActionGrantRow>(), _ => false));
        Assert.Throws<ArgumentNullException>(() =>
            sets.PushSpecies("species.alpha", Basics(), null!, _ => false));
        Assert.Throws<ArgumentNullException>(() =>
            sets.PushSpecies("species.alpha", Basics(), Array.Empty<ActionGrantRow>(), null!));
        Assert.Throws<ArgumentNullException>(() => new LawnHeldActionSets(null!));
    }

    // ---- spec Success criterion 5, second half: the boundary modules 17 and 18 sit on --------

    /// <summary>
    /// A view whose board reads are unreachable: <see cref="HeldActionsOf"/> comes from a real
    /// <see cref="LawnHeldActionSets"/> (so it answers exactly what the store answers), and EVERY other
    /// member throws. That is the point — a policy that gets past step 1 fails loudly here, so these
    /// tests prove where <see cref="StubIntentSource"/> stopped rather than merely what it returned.
    /// </summary>
    sealed class StoreBackedView : IBattleView
    {
        public const string Actor = "ptr.probe";
        readonly LawnHeldActionSets _sets;
        readonly string _speciesKey;

        public StoreBackedView(LawnHeldActionSets sets, string speciesKey)
        {
            _sets = sets;
            _speciesKey = speciesKey;
        }

        public int HeldReads { get; private set; }

        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey)
        {
            HeldReads++;
            return _sets.HeldFor(_speciesKey);
        }

        static InvalidOperationException Unreachable([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
            new($"the board member '{member}' must not be read for an actor with no kit");

        public IReadOnlyList<string> LiveActorKeys => throw Unreachable();
        public int SideOf(string actorKey) => throw Unreachable();
        public GridPos? PositionOf(string actorKey) => throw Unreachable();
        public EntityFacts FactsOf(string actorKey) => throw Unreachable();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => throw Unreachable();
        public string? GarrisonedStructureKeyOf(string actorKey) => throw Unreachable();
        public GridPos? ObjectivePositionOf(string actorKey) => throw Unreachable();
        public long? MaxHpOf(string actorKey) => throw Unreachable();
        public int AggressionOf(string actorKey) => throw Unreachable();
    }

    /// <summary>
    /// spec-lawn-held-actions.md Success criterion 5: *"an unknown species yields empty, and
    /// `StubIntentSource` answers `None` for it."* This is the module-17/18 boundary itself — the
    /// reason the store must NOT fall back to the basic-attack row, because the vanilla swing is charged
    /// by the rider on a path that already charges it.
    /// </summary>
    [Fact]
    public void An_unknown_species_answers_None_through_the_shipped_stub_policy()
    {
        var sets = new LawnHeldActionSets(CatalogWithBasics());
        var view = new StoreBackedView(sets, "species.never-pushed");
        var policy = new StubIntentSource(view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance);

        var intent = policy.TryDeclare(StoreBackedView.Actor, 0);

        Assert.True(intent.IsNone);
        Assert.Equal(1, view.HeldReads); // asked once, and the board was never touched
        Assert.Empty(sets.HeldFor("species.never-pushed"));
    }

    /// <summary>
    /// The mirror, and the one that makes the test above a statement about a CONDITIONAL refusal rather
    /// than about a stub that always answers `None`: with a pushed set, the same policy gets past step 1
    /// and asks the board for a target — the fake throws there, which is exactly the proof.
    /// </summary>
    [Fact]
    public void A_species_with_a_pushed_set_gets_past_step_one_and_asks_the_board()
    {
        var sets = new LawnHeldActionSets(CatalogWithBasics());
        sets.PushSpecies("species.alpha", Basics(), Array.Empty<ActionGrantRow>(), _ => false);
        var view = new StoreBackedView(sets, "species.alpha");
        var policy = new StubIntentSource(view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance);

        Assert.NotEmpty(sets.HeldFor("species.alpha"));
        Assert.Throws<InvalidOperationException>(() => policy.TryDeclare(StoreBackedView.Actor, 0));
        Assert.Equal(1, view.HeldReads);
    }
}
