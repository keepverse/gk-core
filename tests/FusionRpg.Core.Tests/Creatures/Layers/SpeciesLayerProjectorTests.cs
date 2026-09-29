using System.Text.Json;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures.Layers;

/// <summary>
/// species-progression SP3.4 (spec-species-layer-projector.md) — the load-bearing parity test: for
/// every species-only allocation (only <see cref="AllocationScope.CreatureType"/> points) and every
/// Θ, <c>SpeciesLayerProjector.Resolve(ProjectEmpire(A), P(Θ))</c> equals
/// <c>AptitudeResolver.Resolve(A, tuning, ladder, Θ, registry)</c> channel for channel, op for op,
/// value for value — only the SourceId differs, by design.
/// </summary>
public class SpeciesLayerProjectorTests
{
    // Two channels, two read modes, one mitigation-family edge -- so the parity grid exercises
    // AptitudeResolver.EffectiveKMilli's mitigation-scale branch too (both paths call the SAME
    // internal function, per spec rule 1 -- "no second copy of the aptitude math").
    static AptitudeTuning Tuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 500, "families": ["combat.defense"] },
          "read": {
            "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 },
            "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000}
          },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": {
            "combat.power": "magnitude",
            "combat.accuracy": "contest",
            "combat.defense": "magnitude"
          },
          "edges": [
            { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 },
            { "channel": "combat.accuracy.omni", "source": "Might", "kMilli": 500 },
            { "channel": "combat.defense.omni", "source": "Fortitude", "kMilli": 1800 }
          ]
        }
        """);

    static PowerLadder Ladder() => new(FusionRpg.Core.Power.PowerTuningHub.Tuning);
    static DerivedStatRegistry Registry() => DerivedStatRegistry.CreateDefault();
    static readonly EmpireId Empire = EmpireId.Dave;
    const string SpeciesId = "peashooter";

    /// <summary>The parity assertion itself, run once per (allocation, theta) grid cell.</summary>
    static void AssertParity(AptitudeAllocation allocation, int theta)
    {
        var tuning = Tuning();
        var ladder = Ladder();
        var registry = Registry();

        var expected = AptitudeResolver.Resolve(allocation, tuning, ladder, theta, registry);
        var rows = SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, allocation, tuning, registry);
        var actual = SpeciesLayerProjector.Resolve(rows, ladder.Value(theta));

        Assert.Equal(expected.Count, actual.Count);
        foreach (var e in expected)
        {
            var match = Assert.Single(actual, a => a.ChannelId == e.ChannelId);
            Assert.Equal(e.Op, match.Op);
            Assert.Equal(e.Value, match.Value, 9);
            // Only the SourceId differs, by design (spec, "The invariant that makes 2b honest").
            Assert.NotEqual(e.SourceId, match.SourceId);
            Assert.StartsWith("species-empire:", match.SourceId, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1)]
    public void SingleShare_exactParityWithAptitudeResolver(int theta)
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 100);
        AssertParity(allocation, theta);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1)]
    public void MixedShares_exactParityWithAptitudeResolver(int theta)
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 300)
                        + AptitudeAllocation.Single(AllocationScope.CreatureType, "Fortitude", 700);
        AssertParity(allocation, theta);
    }

    [Fact]
    public void HighTheta_nearLadderCeiling_exactParityWithAptitudeResolver()
    {
        var ladder = Ladder();
        var highTheta = (int)Math.Min(ladder.MaxIndex, 5_000_000);
        var allocation = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 100);
        AssertParity(allocation, highTheta);
    }

    [Fact]
    public void EmptyAllocation_projectsZeroRows_neverZeroValuedRows()
    {
        var rows = SpeciesLayerProjector.ProjectEmpire(
            Empire, SpeciesId, AptitudeAllocation.Empty, Tuning(), Registry());
        Assert.Empty(rows);
    }

    [Fact]
    public void UnfundedAptitude_contributesNothing()
    {
        // No edge sources "Vigor" in Tuning() -- funding it in CreatureType scope must not somehow
        // produce a stray row (mirrors AptitudeResolverTests.UnfundedAptitude_contributesNothing...).
        var allocation = AptitudeAllocation.Single(AllocationScope.CreatureType, "Vigor", 100);
        var rows = SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, allocation, Tuning(), Registry());
        Assert.Empty(rows);
    }

    [Fact]
    public void MagnitudeEdge_oversizedCoefficient_throwsRatherThanWraps()
    {
        // Mirrors AptitudeResolverTests.MagnitudeEdge_oversizedCoefficient_throwsRatherThanWraps --
        // the projector's own kMicro widening must throw the same way, never silently wrap.
        var oversizedTuning = AptitudeTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
              "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
              "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
              "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
              "familyRead": { "combat.power": "magnitude" },
              "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 9223372036854775807 } ]
            }
            """);
        var allocation = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 1);
        var registry = Registry();
        var ladder = Ladder();
        var theta = (int)Math.Min(ladder.MaxIndex, 5_000_000);

        // The overflow can surface either while projecting (kMicro = checked(kMilli * sharePowMilli))
        // or while resolving (LadderScale.Micro's own decimal-widened throw) -- both are "throws,
        // never wraps", so either call site being the one that throws satisfies the contract.
        Assert.Throws<OverflowException>(() =>
        {
            var rows = SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, allocation, oversizedTuning, registry);
            SpeciesLayerProjector.Resolve(rows, ladder.Value(theta));
        });
    }

    [Fact]
    public void ContestEdge_isFixedAndTheta_free()
    {
        // combat.accuracy.omni is a Contest edge -- its value must not move with pTheta at all,
        // matching AptitudeReadFunctions.Contest's own Θ-free contract (PS-3).
        var allocation = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 100);
        var rows = SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, allocation, Tuning(), Registry());
        var contestRow = Assert.Single(rows, r => r.Channel == "combat.accuracy.omni");
        Assert.IsType<LayerValue.Fixed>(contestRow.Value);

        var atLowTheta = SpeciesLayerProjector.Resolve(rows, Ladder().Value(1));
        var atHighTheta = SpeciesLayerProjector.Resolve(rows, Ladder().Value(1_000_000));
        var lowValue = Assert.Single(atLowTheta, m => m.ChannelId == "combat.accuracy.omni").Value;
        var highValue = Assert.Single(atHighTheta, m => m.ChannelId == "combat.accuracy.omni").Value;
        Assert.Equal(lowValue, highValue, 9);
    }

    [Fact]
    public void MagnitudeEdge_isLadderMicroAndScalesWithTheta()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.CreatureType, "Might", 100);
        var rows = SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, allocation, Tuning(), Registry());
        var powerRow = Assert.Single(rows, r => r.Channel == "combat.power.omni");
        Assert.IsType<LayerValue.LadderMicro>(powerRow.Value);

        var atLowTheta = Assert.Single(
            SpeciesLayerProjector.Resolve(rows, Ladder().Value(1)), m => m.ChannelId == "combat.power.omni").Value;
        var atHighTheta = Assert.Single(
            SpeciesLayerProjector.Resolve(rows, Ladder().Value(1_000_000)), m => m.ChannelId == "combat.power.omni").Value;
        Assert.True(atHighTheta > atLowTheta, "a magnitude read must grow with Theta");
    }

    [Fact]
    public void NullArguments_reject()
    {
        var allocation = AptitudeAllocation.Empty;
        var tuning = Tuning();
        var registry = Registry();
        Assert.Throws<ArgumentNullException>(() =>
            SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, null!, tuning, registry));
        Assert.Throws<ArgumentNullException>(() =>
            SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, allocation, null!, registry));
        Assert.Throws<ArgumentNullException>(() =>
            SpeciesLayerProjector.ProjectEmpire(Empire, SpeciesId, allocation, tuning, null!));
        Assert.Throws<ArgumentNullException>(() => SpeciesLayerProjector.Resolve(null!, 0));
    }
}

/// <summary>
/// species-progression SP3.5 (spec-species-layer-projector.md rules 2-3): 1a/1b partition a fused
/// instance — <see cref="SpeciesLayerProjector.ProjectBase"/> emits exactly the template's core atoms,
/// <see cref="SpeciesLayerProjector.ProjectPlayerMod"/> emits exactly the instance's non-core (rolled)
/// atoms, and their union equals what T4.6's now-retired <c>SpeciesPassiveAtomSource</c> emitted for
/// the same instance (SP3.6 deleted it) — proving nothing was lost in the move (map C3).
/// </summary>
public class SpeciesLayerProjectorOneTwoBTests
{
    static AtomRow Derived(string atomId, string? paramsJson = null) => new()
    {
        AtomId = atomId, KindId = "stat.derived", FamilyId = "atom.species-layer-test",
        Variant = "v", Tier = 1, ParamsJson = paramsJson ?? "{}",
    };

    static ContainerRow Template(params ContainerAtomRow[] atoms) => new()
    {
        ContainerId = "species-passive.fumeshroom",
        Kind = ContainerKind.SpeciesPassive,
        Atoms = atoms,
    };

    static InstanceRow Instance(params InstanceAtomRow[] atoms) => new()
    {
        InstanceId = "inst-1", ContainerId = "species-passive.fumeshroom", Atoms = atoms,
    };

    [Fact]
    public void ProjectBase_reads_the_core_atoms_definition_values()
    {
        var template = Template(new ContainerAtomRow(1, "core-1"));
        var rows = SpeciesLayerProjector.ProjectBase(
            "fumeshroom", template, id => Derived(id, """{"channel":"combat.power.omni","op":"flat","amount":42}"""));

        var row = Assert.Single(rows);
        Assert.Equal("combat.power.omni", row.Channel);
        Assert.Equal(DerivedModifierOp.Flat, row.Op);
        Assert.Equal(42, Assert.IsType<LayerValue.Fixed>(row.Value).Amount);
        Assert.Equal("species-base:fumeshroom", row.SourceId);
    }

    [Fact]
    public void ProjectPlayerMod_reads_the_instance_value_before_the_definition()
    {
        // A pick IS a rolled value -- same "instance wins" discipline T4.6 already established.
        var template = Template(new ContainerAtomRow(1, "core-1"));
        var instance = Instance(new InstanceAtomRow(1, "core-1", "{}"),
            new InstanceAtomRow(2, "roll-1", """{"channel":"combat.power.omni","op":"flat","amount":99}"""));

        var rows = SpeciesLayerProjector.ProjectPlayerMod(
            "fumeshroom", "fusion-pick", template, instance,
            id => Derived(id, """{"channel":"combat.power.omni","op":"flat","amount":1}"""));

        var row = Assert.Single(rows);
        Assert.Equal(99, Assert.IsType<LayerValue.Fixed>(row.Value).Amount);
        Assert.Equal("species-player:fumeshroom:fusion-pick", row.SourceId);
    }

    [Fact]
    public void A_fused_instance_partitions_with_no_overlap_and_no_loss()
    {
        // 2-atom core (seq 1,2) + 3 rolls (seq 3,4,5) -- gives 2 species-base rows and 3
        // species-player rows, and their union equals the SAME five (channel, op, amount) tuples
        // T4.6's now-retired SpeciesPassiveAtomSource emitted for the exact same instance (one call,
        // uniform instance-values-first-then-definition over every seq) -- this literal set IS that
        // proof: it was captured from SpeciesPassiveAtomSource.DerivedAtomsFor against this identical
        // fixture before SP3.6 deleted it (map C3's "nothing was lost in the move").
        var template = Template(
            new ContainerAtomRow(1, "core-1"), new ContainerAtomRow(2, "core-2"));
        AtomRow? Resolve(string id) => id switch
        {
            "core-1" => Derived(id, """{"channel":"combat.power.omni","op":"flat","amount":10}"""),
            "core-2" => Derived(id, """{"channel":"combat.defense.omni","op":"flat","amount":20}"""),
            "roll-1" => Derived(id),
            "roll-2" => Derived(id),
            "roll-3" => Derived(id),
            _ => null,
        };
        var instance = Instance(
            new InstanceAtomRow(1, "core-1", "{}"),
            new InstanceAtomRow(2, "core-2", "{}"),
            new InstanceAtomRow(3, "roll-1", """{"channel":"combat.accuracy.omni","op":"flat","amount":5}"""),
            new InstanceAtomRow(4, "roll-2", """{"channel":"resource.max.hp","op":"flat","amount":50}"""),
            new InstanceAtomRow(5, "roll-3", """{"channel":"combat.dodge.omni","op":"flat","amount":3}"""));

        var baseRows = SpeciesLayerProjector.ProjectBase("fumeshroom", template, Resolve);
        var playerRows = SpeciesLayerProjector.ProjectPlayerMod("fumeshroom", "fusion-pick", template, instance, Resolve);

        Assert.Equal(2, baseRows.Count);
        Assert.Equal(3, playerRows.Count);
        Assert.All(baseRows, r => Assert.StartsWith("species-base:", r.SourceId, StringComparison.Ordinal));
        Assert.All(playerRows, r => Assert.StartsWith("species-player:", r.SourceId, StringComparison.Ordinal));

        var union = baseRows.Select(r => (r.Channel, r.Op, Amount: Assert.IsType<LayerValue.Fixed>(r.Value).Amount))
            .Concat(playerRows.Select(r => (r.Channel, r.Op, Amount: Assert.IsType<LayerValue.Fixed>(r.Value).Amount)))
            .OrderBy(t => t.Channel, StringComparer.Ordinal)
            .ToList();
        var legacySet = new (string Channel, DerivedModifierOp Op, double Amount)[]
        {
            ("combat.power.omni", DerivedModifierOp.Flat, 10),
            ("combat.defense.omni", DerivedModifierOp.Flat, 20),
            ("combat.accuracy.omni", DerivedModifierOp.Flat, 5),
            ("resource.max.hp", DerivedModifierOp.Flat, 50),
            ("combat.dodge.omni", DerivedModifierOp.Flat, 3),
        }.OrderBy(t => t.Channel, StringComparer.Ordinal).ToList();
        Assert.Equal(legacySet, union);
    }

    // ---- refusals (T4.6's three, carried over unchanged) --------------------------------------------

    [Fact]
    public void A_non_derived_atom_is_skipped()
    {
        var template = Template();
        var instance = Instance(new InstanceAtomRow(1, "a1",
            """{"channel":"combat.power.omni","op":"flat","amount":5}"""));
        var notDerived = new AtomRow { AtomId = "a1", KindId = "stat.flat", FamilyId = "f", Variant = "v", Tier = 1, ParamsJson = "{}" };

        Assert.Empty(SpeciesLayerProjector.ProjectPlayerMod("fumeshroom", "fusion-pick", template, instance, _ => notDerived));
    }

    [Fact]
    public void An_unknown_op_is_skipped_never_coerced_to_flat()
    {
        var template = Template();
        var instance = Instance(new InstanceAtomRow(1, "a1",
            """{"channel":"combat.power.omni","op":"more","amount":5}"""));

        Assert.Empty(SpeciesLayerProjector.ProjectPlayerMod("fumeshroom", "fusion-pick", template, instance, id => Derived(id)));
    }

    [Fact]
    public void A_ValueSpec_object_amount_is_skipped_never_evaluated()
    {
        var template = Template();
        var instance = Instance(new InstanceAtomRow(1, "a1",
            """{"channel":"combat.power.omni","op":"flat","amount":{"curve":"x"}}"""));

        Assert.Empty(SpeciesLayerProjector.ProjectPlayerMod("fumeshroom", "fusion-pick", template, instance, id => Derived(id)));
    }

    [Fact]
    public void An_atom_whose_definition_is_missing_is_skipped_rather_than_guessed()
    {
        var template = Template();
        var instance = Instance(new InstanceAtomRow(1, "gone",
            """{"channel":"combat.power.omni","op":"flat","amount":5}"""));

        Assert.Empty(SpeciesLayerProjector.ProjectPlayerMod("fumeshroom", "fusion-pick", template, instance, _ => null));
    }

    [Fact]
    public void NullArguments_reject()
    {
        var template = Template();
        var instance = Instance();
        Assert.Throws<ArgumentNullException>(() => SpeciesLayerProjector.ProjectBase("id", null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => SpeciesLayerProjector.ProjectBase("id", template, null!));
        Assert.Throws<ArgumentNullException>(() => SpeciesLayerProjector.ProjectPlayerMod("id", "m", null!, instance, _ => null));
        Assert.Throws<ArgumentNullException>(() => SpeciesLayerProjector.ProjectPlayerMod("id", "m", template, null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => SpeciesLayerProjector.ProjectPlayerMod("id", "m", template, instance, null!));
    }
}

/// <summary>
/// species-progression SP3.7 (spec-species-layer-projector.md rule 5): <see cref="SpeciesLayerProjector.ToContainer"/>
/// builds a persistable <see cref="ContainerKind.SpeciesProgression"/> container + its synthetic
/// atoms from a set of projected rows.
/// </summary>
public class SpeciesLayerProjectorToContainerTests
{
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 400, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static Func<string, AtomRow?> LookupIn(IReadOnlyList<AtomRow> atoms)
    {
        var byId = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        return id => byId.TryGetValue(id, out var a) ? a : null;
    }

    [Fact]
    public void The_output_passes_ContainerValidator()
    {
        var rows = new[]
        {
            new ProjectedLayerRow("combat.power.omni", DerivedModifierOp.Flat, new LayerValue.LadderMicro(608), "species-empire:dave:peashooter:Might"),
            new ProjectedLayerRow("resource.max.hp", DerivedModifierOp.Flat, new LayerValue.Fixed(45), "species-base:peashooter"),
        };
        var (container, atoms) = SpeciesLayerProjector.ToContainer("species-progression.peashooter", rows);

        var check = ContainerValidator.Validate(container, LookupIn(atoms), _ => null);
        Assert.True(check.IsOk, check.ToString());
    }

    [Fact]
    public void A_LadderMicro_row_serialises_as_the_powerLadder_ValueSpec()
    {
        var rows = new[]
        {
            new ProjectedLayerRow("combat.power.omni", DerivedModifierOp.Flat, new LayerValue.LadderMicro(608), "species-empire:dave:peashooter:Might"),
        };
        var (_, atoms) = SpeciesLayerProjector.ToContainer("species-progression.peashooter", rows);

        var atom = Assert.Single(atoms);
        using var doc = JsonDocument.Parse(atom.ParamsJson);
        var amountEl = doc.RootElement.GetProperty("amount");
        Assert.Equal(JsonValueKind.Object, amountEl.ValueKind);
        var check = AtomJson.TryReadValueSpec(amountEl, out var spec);
        Assert.True(check.IsOk, check.ToString());
        Assert.True(spec.PowerLadder);
        Assert.Equal(608, spec.PowerLadderKMicro);
    }

    [Fact]
    public void A_Fixed_row_serialises_as_a_literal_amount()
    {
        var rows = new[]
        {
            new ProjectedLayerRow("resource.max.hp", DerivedModifierOp.Flat, new LayerValue.Fixed(45), "species-base:peashooter"),
        };
        var (_, atoms) = SpeciesLayerProjector.ToContainer("species-progression.peashooter", rows);

        var atom = Assert.Single(atoms);
        using var doc = JsonDocument.Parse(atom.ParamsJson);
        var amountEl = doc.RootElement.GetProperty("amount");
        Assert.Equal(JsonValueKind.Number, amountEl.ValueKind);
        Assert.Equal(45, amountEl.GetInt32());
    }

    [Fact]
    public void Reading_the_container_back_through_the_real_ValueSpec_parser_resolves_to_the_same_modifiers_as_Resolve()
    {
        // The load-bearing round trip: parse each atom's own ParamsJson through the SAME grammar
        // parser (AtomJson.TryReadValueSpec) and op parser (AtomDerivedSubsystem.TryParseOp) every
        // other stat.derived atom in the repo goes through, and prove the reconstructed values match
        // SpeciesLayerProjector.Resolve's own output exactly, at two different pTheta values (proving
        // a LadderMicro row's serialized kMicro survives the round trip, not merely its already-
        // resolved amount at one Theta).
        var rows = new ProjectedLayerRow[]
        {
            new("combat.power.omni", DerivedModifierOp.Flat, new LayerValue.LadderMicro(608), "species-empire:dave:peashooter:Might"),
            new("combat.defense.omni", DerivedModifierOp.Increased, new LayerValue.LadderMicro(1215), "species-empire:dave:peashooter:Fortitude"),
            new("resource.max.hp", DerivedModifierOp.Flat, new LayerValue.Fixed(45), "species-base:peashooter"),
        };
        var (_, atoms) = SpeciesLayerProjector.ToContainer("species-progression.peashooter", rows);
        Assert.Equal(rows.Length, atoms.Count);

        var ladder = new PowerLadder(Tuning);
        foreach (var theta in new[] { 1, 1000, 500_000 })
        {
            var pTheta = ladder.Value(theta);
            var expected = SpeciesLayerProjector.Resolve(rows, pTheta);

            foreach (var atom in atoms)
            {
                using var doc = JsonDocument.Parse(atom.ParamsJson);
                var root = doc.RootElement;
                var channel = root.GetProperty("channel").GetString()!;
                var opCheck = AtomDerivedSubsystem.TryParseOp(root.GetProperty("op").GetString(), out var op);
                Assert.True(opCheck);

                var valueCheck = AtomJson.TryReadValueSpec(root.GetProperty("amount"), out var spec);
                Assert.True(valueCheck.IsOk, valueCheck.ToString());
                var actualValue = spec.PowerLadder
                    ? (double)FusionRpg.Core.Power.LadderScale.Micro(spec.PowerLadderKMicro, pTheta)
                    : spec.Min;

                var match = Assert.Single(expected, m => m.ChannelId == channel);
                Assert.Equal(match.Op, op);
                Assert.Equal(match.Value, actualValue, 9);
            }
        }
    }

    [Fact]
    public void Two_rows_sharing_a_channel_still_get_distinct_atom_ids()
    {
        var rows = new ProjectedLayerRow[]
        {
            new("combat.power.omni", DerivedModifierOp.Flat, new LayerValue.LadderMicro(608), "species-empire:dave:peashooter:Might"),
            new("combat.power.omni", DerivedModifierOp.Flat, new LayerValue.LadderMicro(200), "species-empire:dave:peashooter:Ferocity"),
        };
        var (container, atoms) = SpeciesLayerProjector.ToContainer("species-progression.peashooter", rows);

        Assert.Equal(2, atoms.Select(a => a.AtomId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, container.Atoms.Count);
        var check = ContainerValidator.Validate(container, LookupIn(atoms), _ => null);
        Assert.True(check.IsOk, check.ToString());
    }

    [Fact]
    public void NullOrEmptyArguments_reject()
    {
        var rows = Array.Empty<ProjectedLayerRow>();
        Assert.Throws<ArgumentException>(() => SpeciesLayerProjector.ToContainer("", rows));
        Assert.Throws<ArgumentException>(() => SpeciesLayerProjector.ToContainer("   ", rows));
        Assert.Throws<ArgumentNullException>(() => SpeciesLayerProjector.ToContainer("species-progression.x", null!));
    }

    [Fact]
    public void An_empty_row_list_builds_an_empty_but_valid_shaped_container()
    {
        var (container, atoms) = SpeciesLayerProjector.ToContainer("species-progression.peashooter", Array.Empty<ProjectedLayerRow>());
        Assert.Empty(atoms);
        Assert.Empty(container.Atoms);
        Assert.Equal(ContainerKind.SpeciesProgression, container.Kind);
        Assert.Equal("species-progression.peashooter", container.ContainerId);
    }
}
