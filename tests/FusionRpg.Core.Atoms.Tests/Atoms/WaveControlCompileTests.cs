using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// Closes a real, silent gap found while re-verifying E37's own <c>bullet.modify</c> fix to
/// <c>Compilability.OpcodeKinds</c>: E36 shipped <c>wave.control</c> -> <c>EffectActions.WaveControl</c>
/// in <c>AtomCompiler.OpcodeOf</c> but never added the kind to this separate gate, so every
/// <c>wave.control</c> atom silently routed to the Runner path ("has no FA opcode") and was never read
/// there — the <c>ChainDepth</c>-guarded <c>ExecWaveControl</c> opcode never actually ran. No shipped
/// <c>fx-*.json</c> content exists for this kind yet, so <c>EffectCatalogExecutionParityTests</c>'s
/// corpus sweep could not have caught it. Fixed directly in <c>Compilability.cs</c>; this is the test
/// that would have caught the gap in the first place, mirroring <c>BulletModifyCompileTests</c>'s shape.
/// </summary>
public class WaveControlCompileTests
{
    static AtomRow Row(string paramsJson) => new()
    {
        AtomId = AtomRow.DeriveId("atom.test-wave-control", "", 1),
        KindId = "wave.control",
        FamilyId = "atom.test-wave-control",
        Variant = "",
        Tier = 1,
        Name = "Test Wave Control",
        ParamsJson = paramsJson,
        WhenJson = """{"trigger":"OnWave"}""",
        IcdKey = "test.wave-control.compile",
    };

    [Fact]
    public void A_wave_control_atom_compiles_to_a_WaveControl_action_row_on_the_COMPILED_path()
    {
        var row = Row("""{"op":"setTimer","timerMs":3000}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);
        // The regression this test exists to catch: before the Compilability.cs fix, this atom
        // landed in compiled.Runtime (Runner path) instead, and ExecWaveControl never ran it.
        Assert.Empty(compiled.Runtime);

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);

        Assert.Equal(EffectActions.WaveControl, action.Action);
        Assert.Equal("setTimer", action.Params["op"]?.ToString());
        Assert.Equal(3000d, Convert.ToDouble(action.Params["timerMs"]));
    }
}
