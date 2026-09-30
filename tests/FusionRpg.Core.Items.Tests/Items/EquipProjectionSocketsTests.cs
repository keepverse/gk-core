using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Workspace;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>species-gear-chain T21: filled sockets project their insert instances through the same
/// projection that carries the host — never a parallel Bind. Closed enum + relationships only;
/// no population counts.</summary>
public class EquipProjectionSocketsTests
{
    static readonly EquipGate Gate = new();

    static EquipAssignment Host(string instance = "host-1") =>
        new("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, instance, "2026-09-16T00:00:00Z");

    static EquipProjector Projector(Func<EquipAssignment, IReadOnlyList<SocketSlot>>? socketsOf = null,
        Func<EquipAssignment, IReadOnlyList<SocketSlot>, IReadOnlyList<ComboBindTarget>>? combosOf = null) =>
        new(Gate,
            _ => new SpecimenActor("specimen-1", Frame: null, Level: 50, Faction: null),
            _ => new EquipItemFacts(null, null, FactionReq: null),
            socketsOf,
            combosOf);

    // ── SSH4.6 (spec-combo-bind §2): the combination binds through the SAME projection ────────────

    static ComboBindTarget Target(string comboId, int circuit, string containerId) => new(
        new CombinationResult(comboId, ComboShape.Strain, EffectiveCount: 4, GrantedTier: 1, AllAttuned: false,
            Circuit: circuit),
        circuit,
        ComboBindTargets.InstanceId("host-1", circuit, containerId));

    [Fact]
    public void a_firing_strain_binds_its_container_through_the_projection()
    {
        var target = Target("combo.strain-might-offense", 0, "combo.strain-might-offense-t1");
        var projector = Projector(combosOf: (_, _) => new[] { target });

        var result = projector.Project("specimen-1", new[] { Host() });

        // Host + the one combination, both through this projection: ApplyEquipProjection reaps any
        // binding the desired set does not carry, so a parallel Bind() would be withdrawn next build.
        Assert.Equal(2, result.Bindings.Count);
        var combo = Assert.Single(result.Bindings, b => b.RefId == target.ComboInstanceId);
        Assert.Equal(ItemRole.ArmamentPrimary, combo.Role);
        Assert.Equal(EquipRefKinds.Rolled, combo.RefKind);
        Assert.Equal(Host().AssignedUtc, combo.AssignedUtc);
    }

    [Fact]
    public void every_firing_combination_binds_with_no_actor_wide_count()
    {
        // R12: there is no per-actor cap. Eight targets (two circuits × the words a full host can
        // carry) all bind — nothing is suppressed, and ProjectionResult has no Suppressed list.
        var targets = Enumerable.Range(0, 8)
            .Select(i => Target($"combo.strain-word-{i}", i / 4, $"combo.strain-word-{i}-t1"))
            .ToList();
        var projector = Projector(combosOf: (_, _) => targets);

        var result = projector.Project("specimen-1", new[] { Host() });

        Assert.Equal(9, result.Bindings.Count);   // host + 8
        Assert.All(targets, t => Assert.Contains(result.Bindings, b => b.RefId == t.ComboInstanceId));
        Assert.Empty(result.Shortfalls);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void reprojecting_unchanged_state_writes_no_new_instance()
    {
        // The id is content-derived, so the SAME fill reprojects to the SAME binding set — which is
        // what lets the caller instantiate once and write nothing on a repeat.
        var target = Target("combo.strain-might-offense", 0, "combo.strain-might-offense-t1");
        var projector = Projector(combosOf: (_, _) => new[] { target });

        var first = projector.Project("specimen-1", new[] { Host() });
        var second = projector.Project("specimen-1", new[] { Host() });

        Assert.Equal(
            first.Bindings.Select(b => b.RefId).OrderBy(x => x, StringComparer.Ordinal),
            second.Bindings.Select(b => b.RefId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(ComboBindTargets.InstanceId("host-1", 0, "combo.strain-might-offense-t1"),
            Assert.Single(first.Bindings, b => b.RefId.StartsWith("cmb:", StringComparison.Ordinal)).RefId);
    }

    [Fact]
    public void binding_set_is_independent_of_loadout_iteration_order()
    {
        var a = Target("combo.strain-a", 0, "combo.strain-a-t1");
        var b = Target("combo.strain-b", 1, "combo.strain-b-t1");

        var forward = Projector(combosOf: (_, _) => new[] { a, b })
            .Project("specimen-1", new[] { Host() }).Bindings.Select(x => x.RefId).ToHashSet(StringComparer.Ordinal);
        var reverse = Projector(combosOf: (_, _) => new[] { b, a })
            .Project("specimen-1", new[] { Host() }).Bindings.Select(x => x.RefId).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(forward, reverse);
    }

    // ── SSH5.9 (spec-combo-bind §2–§3): the circuit rides the bind ──────────────────────────────

    static SocketTuning Sockets()
    {
        // data/tuning is gk-core's own. The walk-up that used to find it keyed on CONTRIBUTING.md,
        // which the topology moved to gk-workflow, so the walk ran off the top of the repository and
        // produced a path under the workspace root that cannot exist. A named root is the whole fix.
        return SocketTuning.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.Core(), "data", "tuning", SocketTuningFiles.Current)));
    }

    static AtomRow WordAtom() => new()
    {
        AtomId = AtomRow.DeriveId("atom.might", "", 1), FamilyId = "atom.might", KindId = "stat.derived",
        Variant = "", Tier = 1, Name = "word atom",
        ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":30}",
    };

    static ComboRecipe Word(string id) => new(id, ComboShape.Strain, "", 0, "", "", 1, 1,
        new[] { new ComboIngredient("atom.might", 1) }, BaseFloors: new[] { 1 });

    [Fact]
    public void the_same_word_in_two_circuits_is_two_contributions()
    {
        // An eight-socket host is TWO circuits, and the same word firing in each is two contributions:
        // `ComboBindTarget.Circuit` comes from the evaluator's own `CombinationResult.Circuit`, so the
        // two instance ids (and the two SourceIds) differ only by the circuit suffix — never suppressed.
        var targets = new[]
        {
            Target("combo.strain-might-offense", 0, "combo.strain-might-offense-t1"),
            Target("combo.strain-might-offense", 1, "combo.strain-might-offense-t1"),
        };
        var bindings = Projector(combosOf: (_, _) => targets)
            .Project("specimen-1", new[] { Host() }).Bindings;
        var comboBindings = bindings.Where(b => b.RefId.StartsWith("cmb:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, comboBindings.Count);
        Assert.Equal(2, comboBindings.Select(b => b.RefId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(ComboBindTargets.InstanceId("host-1", 0, "combo.strain-might-offense-t1"), comboBindings.Select(b => b.RefId));
        Assert.Contains(ComboBindTargets.InstanceId("host-1", 1, "combo.strain-might-offense-t1"), comboBindings.Select(b => b.RefId));

        // The two SOURCE ids the Hub sees differ only by `#c{circuit}` (SSH4.5's Combo arm).
        var source = EquipAtomSource.FromEquippedResolver(_ => targets
            .Select(t => new EquippedAtomInput("armament-primary", "host-1", WordAtom(),
                SocketIndex: null, ComboId: t.Result.ComboId, Circuit: t.Circuit))
            .ToList());
        var sourceIds = source.DerivedAtomsFor("specimen-1").Select(d => d.SourceId).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(2, sourceIds.Count);
        Assert.Contains(ContributionSourceIds.Combo("armament-primary", "host-1", "combo.strain-might-offense", 0), sourceIds);
        Assert.Contains(ContributionSourceIds.Combo("armament-primary", "host-1", "combo.strain-might-offense", 1), sourceIds);
    }

    [Fact]
    public void one_identity_per_circuit_is_the_only_limit()
    {
        // Two Strains on one fill: a FOUR-socket host is one circuit, so exactly one identity binds;
        // an EIGHT-socket host is two, so both fire — one per circuit. That per-circuit rule is the
        // only "at most one" left (R12 deleted the per-actor cap), and every result binds.
        var tuning = Sockets();
        var catalog = new[] { Word("combo.strain-word-a"), Word("combo.strain-word-b") };
        var fill = Enumerable.Range(0, 8)
            .Select(i => new SocketFill(i, "", new InsertDef($"gem.w{i}", "atom.might", "fire", 1)))
            .ToList();

        var oneCircuit = CombinationEvaluator.Evaluate(
            new SocketHost("item.four", ItemRole.ArmamentPrimary, "humanoid", 4, Capacity: 8),
            fill.Where(f => f.SocketIndex < 4).ToList(), catalog, tuning);
        Assert.Single(oneCircuit);

        var twoCircuits = CombinationEvaluator.Evaluate(
            new SocketHost("item.eight", ItemRole.ArmamentPrimary, "humanoid", 8, Capacity: 8),
            fill, catalog, tuning);
        Assert.Equal(2, twoCircuits.Count);
        Assert.Equal(new[] { 0, 1 }, twoCircuits.Select(r => r.Circuit).OrderBy(x => x).ToArray());

        // …and both bind through the one projection, one per circuit.
        var targets = twoCircuits.Select(r => new ComboBindTarget(r, r.Circuit,
            ComboBindTargets.InstanceId("host-1", r.Circuit,
                ComboContainerBuild.ContainerId(r.ComboId, r.GrantedTier)))).ToList();
        var comboBindings = Projector(combosOf: (_, _) => targets)
            .Project("specimen-1", new[] { Host() }).Bindings
            .Where(b => b.RefId.StartsWith("cmb:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, comboBindings.Count);
        Assert.Equal(2, comboBindings.Select(b => b.RefId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void unequipping_the_host_withdraws_the_combination()
    {
        // No assignment → the host is not in the desired set → neither it nor its combination binds,
        // and the evaluator is never consulted. ApplyEquipProjection then reaps the old bindings,
        // which is what "withdrawn" means at this layer — the Data test proves the reaping itself.
        var calls = 0;
        var projector = Projector(combosOf: (_, _) => { calls++; return new[] { Target("combo.x", 0, "combo.x-t1") }; });

        var none = projector.Project("specimen-1", Array.Empty<EquipAssignment>());
        Assert.Empty(none.Bindings);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_filled_socket_projects_exactly_one_binding_at_the_hosts_role()
    {
        var projector = Projector(_ => new List<SocketSlot>
        {
            new(0, "", false, "gem.ember-shard.t3", "gem-inst-1"),
        });

        var result = projector.Project("specimen-1", new[] { Host() });

        Assert.Equal(2, result.Bindings.Count);
        var insert = Assert.Single(result.Bindings, b => b.RefId == "gem-inst-1");
        Assert.Equal(ItemRole.ArmamentPrimary, insert.Role);
        Assert.Equal(EquipRefKinds.Rolled, insert.RefKind);
    }

    [Fact]
    public void An_empty_socket_and_an_instance_less_socket_project_zero_bindings()
    {
        var projector = Projector(_ => new List<SocketSlot>
        {
            new(0, "", false),
            new(1, "", false, "gem.ember-shard.t3", null), // container but no instance: pre-gem-tier shape
        });

        var result = projector.Project("specimen-1", new[] { Host() });

        var only = Assert.Single(result.Bindings);
        Assert.Equal("host-1", only.RefId);
    }

    [Fact]
    public void Two_identical_gems_in_two_sockets_project_two_bindings()
    {
        var projector = Projector(_ => new List<SocketSlot>
        {
            new(0, "", false, "gem.ember-shard.t3", "gem-inst-1"),
            new(1, "", false, "gem.ember-shard.t3", "gem-inst-1"),
        });

        var result = projector.Project("specimen-1", new[] { Host() });

        Assert.Equal(3, result.Bindings.Count);
        Assert.Equal(2, result.Bindings.Count(b => b.RefId == "gem-inst-1"));
    }

    [Fact]
    public void An_unwired_projector_projects_the_host_only()
    {
        var result = Projector().Project("specimen-1", new[] { Host() });

        var only = Assert.Single(result.Bindings);
        Assert.Equal("host-1", only.RefId);
    }

    [Fact]
    public void Insert_SourceId_round_trips_through_FictionLabel()
    {
        var id = ContributionSourceIds.Insert("armament-primary", "item.drop-1", 0);

        Assert.Equal("insert:armament-primary:item.drop-1#0", id);
        Assert.Equal("Insert · armament-primary (item.drop-1 socket 0)", ContributionSourceIds.FictionLabel(id));
    }

    [Fact]
    public void Insert_SourceId_with_an_unknown_host_stays_attributed_never_bare()
    {
        Assert.Equal(
            "Insert · armament-primary (socket 2)",
            ContributionSourceIds.FictionLabel(ContributionSourceIds.Insert("armament-primary", "", 2)));
    }
}
