using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// T3.7 ⭐ <b>THE PROOF</b> (`spec-instance-producer.md`): fixture container → <c>ProduceAndBind</c> →
/// <c>ResolveBindings</c> non-empty → <c>AtomPushService</c> compiles → <c>AtomRunner</c> receives an
/// entry. The first time in the repo's history this path runs in production shape — every piece
/// (`Instantiator.TryInstantiate`, `RpgStore.SaveInstance`, `RpgStore.Bind`, `ActionSeeder.Generate`)
/// shipped tested with zero production callers until T3.6 wrote the first one.
///
/// <para><b>Deliberately in <c>FusionRpg.Server.Tests</c>, not <c>FusionRpg.Core.Tests</c></b> — the
/// spec's own stated file path (`tests/FusionRpg.Core.Tests/Atoms/AtomEndToEndTests.cs`) cannot reach
/// <c>AtomPushService</c>, which lives in <c>FusionRpg.Server</c> and only <c>Server.Tests</c>
/// references. Verified against both `.csproj` files, not assumed — the same "spec vs real
/// architecture" correction this program has already made twice (T3.2's `AtomImporter` root, T3.6's
/// own `InstanceProducer` split).</para>
///
/// <para>The fixture is a <c>species-passive</c>, never an <c>item</c> bound to an equipped slot —
/// the mixed-source invariant this module's own spec names: path 1 (this module) must not bind an
/// equipped-item effect while path 2 (`mods_json`) is still live for the same slot.</para>
/// </summary>
public class AtomEndToEndTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly AtomPushService _push;

    public AtomEndToEndTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _push = new AtomPushService(_store);
        Seed();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void Seed()
    {
        var r = _store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.searing.t1", KindId = "resource.delta",
            FamilyId = "atom.searing", Variant = "", Tier = 1, Name = "Searing",
            ParamsJson = """{"amount":{"min":-120,"max":-80,"roll":"onApply"},"element":"fire"}""",
            WhenJson = """{"trigger":"OnDamageDealt","chance":1000}""",
        });
        Assert.True(r.IsOk, r.ToString());

        var c = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = "species-passive.searing-hide", Kind = ContainerKind.SpeciesPassive,
            Atoms = new[] { new ContainerAtomRow(1, "atom.searing.t1") },
        });
        Assert.True(c.IsOk, c.ToString());
    }

    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, // pinned anchor, contentScale(20) == 1.000 exactly
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    [Fact]
    public void The_full_chain_runs_in_production_shape()
    {
        var owner = new OwnerScope(OwnerKind.Player, "1");

        // 1. Fixture container -> ProduceAndBind
        var produce = _store.ProduceAndBind(
            _store.GetContainer("species-passive.searing-hide")!, NoDomains, rollSeed: 11,
            thetaContent: 20, Tuning, owner, slot: null, priority: 1, source: "test",
            out var instanceId, out var bindingId);
        Assert.True(produce.IsOk, produce.ToString());
        Assert.NotNull(instanceId);
        Assert.NotNull(bindingId);

        // 2. ResolveBindings returns non-empty for a real owner.
        var resolution = _store.ResolveBindings(owner, new BindContext(RuntimeId.Lawn));
        Assert.NotEmpty(resolution.Bindings);
        Assert.Contains(resolution.Bindings, b => b.BindingId == bindingId);

        // 3. AtomPushService compiles the produced instance. The fixture's own atom is a triggered
        // resource.delta with no fixed-core permanent modifier, so it travels entirely as a runner
        // entry — zero Defs is correct here (CompiledPushTests.cs's own
        // "A_compiled_atom_travels_as_a_grant_not_as_a_runner_entry" proves the OPPOSITE shape for a
        // stat.modify atom; Defs only populate for the grant path, which this fixture never exercises).
        var payload = _push.Build(owner, new BindContext(RuntimeId.Lawn), matchSeed: 99, matchKey: "m1");
        Assert.False(payload.UpToDate);
        Assert.NotEmpty(payload.RunnerBindings);

        // 4. AtomRunner receives an entry — decode the SAME wire shape an injector would, build the
        // trigger index from it, and fire a real event through it.
        var decoded = AtomPushCodec.DecodeBindings(payload);
        Assert.NotEmpty(decoded);

        var index = TriggerIndex.Build(decoded);
        Assert.True(index.Count > 0, "AtomRunner's own trigger index received zero entries");

        var bag = new EffectBag(
            new InMemoryEffectCatalog(), new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)),
            new RecordingEffectSink());
        var funnel = new EffectFunnel(bag);
        var proc = new AtomRandom(99, AtomStreams.Proc);
        var apply = new AtomRandom(99, AtomStreams.Apply);
        var runner = new AtomRunner(funnel, index, proc, apply, () => 0, "m1");

        var visited = runner.OnEvent(new RunnerEvent(
            TriggerIndex.Ordinal(AtomTriggers.OnDamageDealt), "0xA", "0xB",
            new EntityFacts(0, 1, 1000, -1, 0, 0, false, false, 0),
            new EntityFacts(1, 2, 1000, -1, 0, 1, false, false, 0)));

        Assert.True(visited > 0, "no binding was visited for the fixture's own trigger");
    }
}
