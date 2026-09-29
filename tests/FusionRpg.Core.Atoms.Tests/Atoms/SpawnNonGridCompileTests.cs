using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E40 (spec-spawn-non-grid.md §4): "a plan item whose payload carries type, row and col; the
/// executor calls SetPet once." The compiled half of that claim is provable in Core — the injector's
/// own <c>ExecSpawnEntity</c>/<c>SpawnPetOnce</c> forwarding (untestable here, no Unity reference) is
/// covered separately by <c>SpawnNonGridExecutorGuardTests</c> in FusionRpg.Guard.Tests, which reads
/// the sink's source text and asserts it forwards <c>col</c> rather than silently falling back to
/// <c>CheatState.SpawnCol</c>'s stale value (the exact G1-class defect the spec's own planted
/// violation names).
///
/// <para>Same chain <see cref="BulletModifyCompileTests"/> proves for <c>bullet.modify</c>: kind ->
/// opcode -> compiled <c>EffectDefActionDto</c>, with every declared param passed through VERBATIM
/// (<c>AtomCompiler.ToOpcodeShape</c> only rewrites <c>stat.modify</c>/<c>stat.derived</c>).</para>
/// </summary>
public class SpawnNonGridCompileTests
{
    static AtomRow Row(string kindLabel, string paramsJson) => new()
    {
        AtomId = AtomRow.DeriveId("atom.test-spawn-" + kindLabel, "", 1),
        KindId = "spawn.entity",
        FamilyId = "atom.test-spawn-" + kindLabel,
        Variant = "",
        Tier = 1,
        Name = "Test Spawn " + kindLabel,
        ParamsJson = paramsJson,
        WhenJson = """{"trigger":"OnSpawn"}""",
        IcdKey = "test.spawn-" + kindLabel + ".compile",
    };

    // §4's headline case, verbatim: kind:"pet", typeId:0, row:2, col:3 -- the compiled action carries
    // all three explicit params, none of them re-derived from a default.
    [Fact]
    public void A_pet_spawn_atom_compiles_to_a_SpawnEntity_action_carrying_type_row_and_col()
    {
        var row = Row("pet", """{"kind":"pet","typeId":0,"row":2,"col":3}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);
        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);

        Assert.Equal(EffectActions.SpawnEntity, action.Action);
        Assert.Equal("pet", action.Params["kind"]?.ToString());
        Assert.Equal(0d, Convert.ToDouble(action.Params["typeId"]));
        Assert.Equal(2d, Convert.ToDouble(action.Params["row"]));
        Assert.Equal(3d, Convert.ToDouble(action.Params["col"]));
    }

    // PLANTED VIOLATION (§4): drop col from the authored atom -- the compiled action must not carry
    // it either, and must not silently substitute a default the way the injector's own
    // CheatState.SpawnCol fallback would if the SINK forgot to forward it. Proves the compiler is
    // pure pass-through (never inventing a value), which is the precondition the executor-guard text
    // check in FusionRpg.Guard.Tests builds on.
    [Fact]
    public void PLANTED_VIOLATION_dropping_col_from_the_authored_atom_drops_it_from_the_compiled_action()
    {
        var row = Row("pet-no-col", """{"kind":"pet","typeId":0,"row":2}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.False(action.Params.ContainsKey("col"),
            "col was never authored -- it must not appear from nowhere in the compiled action");
    }

    [Fact]
    public void A_bucket_spawn_atom_compiles_to_a_SpawnEntity_action_carrying_type_row_and_col()
    {
        var row = Row("bucket", """{"kind":"bucket","typeId":1,"row":4,"col":2}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal("bucket", action.Params["kind"]?.ToString());
        Assert.Equal(1d, Convert.ToDouble(action.Params["typeId"]));
        Assert.Equal(4d, Convert.ToDouble(action.Params["row"]));
        Assert.Equal(2d, Convert.ToDouble(action.Params["col"]));
    }

    [Fact]
    public void A_mower_spawn_atom_compiles_to_a_SpawnEntity_action_carrying_type_row_and_x()
    {
        var row = Row("mower", """{"kind":"mower","typeId":0,"row":0,"x":123.5}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal("mower", action.Params["kind"]?.ToString());
        Assert.Equal(0d, Convert.ToDouble(action.Params["typeId"]));
        Assert.Equal(0d, Convert.ToDouble(action.Params["row"]));
        Assert.False(action.Params.ContainsKey("col"), "mower places by x, not col");
    }

    // A bound coin atom can never reach this path in practice -- AtomKindRegistry.Validate refuses it
    // at load, and BindGate runs before compile in the real pipeline. AtomCompiler.Compile itself does
    // not call Validate (Compilability.Classify only checks OpcodeKinds membership and runtime
    // support), so a raw coin AtomRow still compiles mechanically if constructed directly -- recorded
    // here so a reader does not read "coin compiles" as "coin is authorable"; the refusal lives at
    // AtomKindRegistry.Validate, one layer up, and is proven by SpawnNonGridTests instead.
    [Fact]
    public void A_coin_atom_still_compiles_mechanically_the_refusal_is_a_Validate_layer_concern_not_a_compile_one()
    {
        var row = Row("coin", """{"kind":"coin","typeId":0,"row":2,"col":3}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);
        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal("coin", action.Params["kind"]?.ToString());
    }
}
