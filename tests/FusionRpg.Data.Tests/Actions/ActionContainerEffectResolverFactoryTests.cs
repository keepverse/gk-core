using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// A24 (spec-container-effect-resolver-production.md §2): the real, `RpgStore`-backed
/// `IContainerEffectResolver` implementation, in isolation from any battle — the factory's own
/// contract, not the wiring into `WebMatchService` (that's `BuildSquadEquippedActionsTests`' job,
/// through the real production seam this program has used for every prior module's integration proof).
/// </summary>
public class ActionContainerEffectResolverFactoryTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionContainerEffectResolverFactoryTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    // ContainerValidator's own grammar is ONE dot, right after the kind prefix -- "skill.<dashes>",
    // never a second dot (ActionCorpusComposer.cs's own established fix for this exact trap: dots in
    // an id become dashes). actionId keeps its own dot-segmented naming freely; only the derived
    // containerId needs the real grammar.
    static string ContainerIdFor(string actionId) =>
        ContainerRow.PrefixOf(ContainerKind.Skill) + "." + actionId.Replace('.', '-');

    void SeedFixedValueAction(string actionId)
    {
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);
        Assert.Empty(_store.UpsertAtoms(new[]
        {
            new AtomRow
            {
                AtomId = atomId, KindId = "stat.modify", FamilyId = "atom." + actionId.Replace('.', '-'),
                Variant = "", Tier = 1, Name = actionId,
                // A bare JSON number -- AtomJson.TryReadValueSpec's own documented "42 -> Fixed(42)"
                // shape, Min == Max, never routed to the Runner path by Classify's Rule 3.
                ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":5000}",
            },
        }).Rejected);

        var containerId = ContainerIdFor(actionId);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId, Name = actionId, Kind = ActionKind.Skill, Rung = 1,
            ContainerId = containerId, Grantable = true, Tags = new[] { ActionTag.Offensive },
        }).IsOk);
    }

    void SeedRunnerOnlyAction(string actionId)
    {
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);
        Assert.Empty(_store.UpsertAtoms(new[]
        {
            new AtomRow
            {
                AtomId = atomId, KindId = "stat.modify", FamilyId = "atom." + actionId.Replace('.', '-'),
                Variant = "", Tier = 1, Name = actionId,
                // A per-hit roll (min != max, roll: onApply) -- Classify's Rule 3 routes this to the
                // Runner path, exactly like the real atom.fortitude/atom.vitality seed atoms do.
                // No "when" at all -- matching those same real atoms, which author no trigger either.
                ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onApply\"}}",
            },
        }).Rejected);

        var containerId = ContainerIdFor(actionId);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId, Name = actionId, Kind = ActionKind.Skill, Rung = 1,
            ContainerId = containerId, Grantable = true, Tags = new[] { ActionTag.Offensive },
        }).IsOk);
    }

    /// <summary>A25: unlike <see cref="SeedRunnerOnlyAction"/>, this atom carries a real, authored
    /// `when.trigger` -- a well-formed Runner-path atom (a per-hit-roll proc on landing a hit), the
    /// shape real content WOULD need to author to activate through A25, distinct from
    /// atom.fortitude/atom.vitality's own (separately named) missing-trigger defect.</summary>
    void SeedTriggeredRunnerAction(string actionId)
    {
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);
        Assert.Empty(_store.UpsertAtoms(new[]
        {
            new AtomRow
            {
                AtomId = atomId, KindId = "stat.modify", FamilyId = "atom." + actionId.Replace('.', '-'),
                Variant = "", Tier = 1, Name = actionId,
                WhenJson = "{\"trigger\":\"OnDamageDealt\"}",
                ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onApply\"}}",
            },
        }).Rejected);

        var containerId = ContainerIdFor(actionId);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId, Name = actionId, Kind = ActionKind.Skill, Rung = 1,
            ContainerId = containerId, Grantable = true, Tags = new[] { ActionTag.Offensive },
        }).IsOk);
    }

    [Fact]
    public void ACompiledPathContainerResolvesToARealNonEmptyEffectIdBackedByAMatchingDef()
    {
        SeedFixedValueAction("action.resolver-test.fixed");

        var (resolver, defs, _, _) = ActionContainerEffectResolverFactory.Build(_store);

        var containerId = ContainerIdFor("action.resolver-test.fixed");
        var effectIds = resolver.EffectIdsFor(containerId);
        Assert.NotEmpty(effectIds);
        var effectId = Assert.Single(effectIds);

        var def = Assert.Single(defs, d => d.EffectId == effectId);
        Assert.Equal(EffectTypes.Passive, def.EffectType); // triggerless stat.modify -> Passive, not the Triggered default
        Assert.NotEmpty(def.Actions);
    }

    [Fact]
    public void ARunnerPathOnlyContainerResolvesToEmptyNeverAThrow()
    {
        SeedRunnerOnlyAction("action.resolver-test.runner");

        var (resolver, defs, runnerBindings, _) = ActionContainerEffectResolverFactory.Build(_store);

        Assert.Empty(resolver.EffectIdsFor(ContainerIdFor("action.resolver-test.runner")));
        Assert.Empty(defs); // the only container in this store compiled to nothing
        // A25: triggerless, exactly like atom.fortitude/atom.vitality -- skipped, not passed through to
        // TriggerIndex.Build (which would throw), matching the factory's own documented boundary.
        Assert.Empty(runnerBindings);
    }

    /// <summary>A25 (spec-container-effect-resolver-production.md's own follow-on): the positive case
    /// the Runner-path-only test above cannot cover -- a WELL-FORMED Runner-path atom (real trigger,
    /// per-hit roll) is collected into a real `RunnerBinding`, ready for `BattleEffectHost.UseRunner`.
    /// </summary>
    [Fact]
    public void AWellFormedTriggeredRunnerAtomIsCollectedIntoARunnerBinding()
    {
        SeedTriggeredRunnerAction("action.resolver-test.triggered-runner");

        var (_, defs, runnerBindings, _) = ActionContainerEffectResolverFactory.Build(_store);

        Assert.Empty(defs); // still nothing on the Compiled path -- this atom is Runner-only
        var binding = Assert.Single(runnerBindings);
        Assert.Equal("OnDamageDealt", binding.Entry.Trigger);
        Assert.False(binding.Entry.Values["amount"].IsFixed); // the roll this atom exists to make
        // Builds a real TriggerIndex without throwing -- the exact construction BattleEffectHost.
        // UseRunner performs, proven here in isolation from a full battle.
        var index = TriggerIndex.Build(runnerBindings);
        Assert.Equal(1, index.Count);
    }

    /// <summary>
    /// A25's own decisive proof, isolated from a full battle's own def-registration concerns (a
    /// separate, already-named gap -- E15's own spec: "nothing emits a def for a runner atom... until
    /// [E19] a host has to have the def in its catalog already"). `AtomRunner.OnEvent`'s own return
    /// value (how many dispatched) reflects a successful gate-evaluation and Funnel enqueue
    /// UNCONDITIONALLY -- `EffectFunnel.EnqueueModifier` queues without validating the def exists, so
    /// this proof needs no matching def and no flush, and is not affected by whether one exists.
    /// </summary>
    [Fact]
    public void ARealFactoryBuiltRunnerBindingDispatchesThroughARealAtomRunner()
    {
        SeedTriggeredRunnerAction("action.resolver-test.dispatch-proof");
        var (_, _, runnerBindings, _) = ActionContainerEffectResolverFactory.Build(_store);
        Assert.Single(runnerBindings);

        var bag = new EffectBag(new InMemoryEffectCatalog(), new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)),
            new NullEffectActionSink());
        var funnel = new EffectFunnel(bag);
        var runner = new AtomRunner(funnel, TriggerIndex.Build(runnerBindings),
            new AtomRandom(1, AtomStreams.Proc), new AtomRandom(1, AtomStreams.Apply), () => 0L);

        var facts = new EntityFacts(0, 0, 1000, 0, 0, 0, false, false, 0);
        var dispatched = runner.OnEvent(new RunnerEvent(
            TriggerIndex.Ordinal("OnDamageDealt"), "attacker", "defender", facts, facts));

        Assert.Equal(1, dispatched);
        Assert.Empty(runner.LastSkipped); // no gate refused it -- a real, well-formed proc
    }

    sealed class NullEffectActionSink : IEffectActionSink
    {
        public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item) => true;
    }

    [Fact]
    public void AContainerNoActionEverReferencesIsAbsentFromTheResolver()
    {
        // A real container that exists in RpgStore but that ListActionContainers' own scoping join
        // (through rpg_action.container_id) never reaches -- proving the join scopes correctly rather
        // than the resolver happening to cover every container in the database.
        var atomId = AtomRow.DeriveId("atom.orphan", "", 1);
        Assert.Empty(_store.UpsertAtoms(new[]
        {
            new AtomRow { AtomId = atomId, KindId = "stat.modify", FamilyId = "atom.orphan", Variant = "", Tier = 1, Name = "orphan", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}" },
        }).Rejected);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "skill.orphan", Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);
        // Deliberately no UpsertAction referencing "skill.orphan".

        var (resolver, defs, _, _) = ActionContainerEffectResolverFactory.Build(_store);

        Assert.Empty(resolver.EffectIdsFor("skill.orphan"));
        Assert.Empty(defs);
    }

    [Fact]
    public void TwoBuildsAgainstTheSameContentProduceByteIdenticalEffectIds()
    {
        SeedFixedValueAction("action.resolver-test.determinism");

        var (resolverA, defsA, _, _) = ActionContainerEffectResolverFactory.Build(_store);
        var (resolverB, defsB, _, _) = ActionContainerEffectResolverFactory.Build(_store);

        var containerId = ContainerIdFor("action.resolver-test.determinism");
        Assert.Equal(resolverA.EffectIdsFor(containerId), resolverB.EffectIdsFor(containerId));
        Assert.Equal(defsA.Select(d => d.EffectId).OrderBy(x => x, StringComparer.Ordinal),
                     defsB.Select(d => d.EffectId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void NoCompiledEffectIdEverCollidesWithTheStaticHandAuthoredCatalog()
    {
        SeedFixedValueAction("action.resolver-test.collision-check");

        var (_, defs, _, _) = ActionContainerEffectResolverFactory.Build(_store);
        var staticIds = EffectAtomCatalog.CreateAll().Select(d => d.EffectId).ToHashSet(StringComparer.Ordinal);

        Assert.All(defs, d => Assert.DoesNotContain(d.EffectId, staticIds));
    }
}
