using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E37 (spec-projectile-control.md §2b). Proves the same chain <c>StatDerivedCompileGapTests</c>
/// proves for <c>stat.derived</c> — kind → opcode → compiled def → Passive effect type, plus
/// <c>Compilability</c>'s own separate OpcodeKinds gate (the one <c>wave.control</c> was found to be
/// missing from during this module's own build; not this module's kind to fix) — but for
/// <c>bullet.modify</c>, which never rewrites its params (<c>AtomCompiler.ToOpcodeShape</c> only
/// touches <c>stat.modify</c>/<c>stat.derived</c>), so <c>op</c>/<c>amount</c>/<c>bulletType</c>/
/// <c>moveWay</c> reach the def exactly as authored.
/// </summary>
public class BulletModifyCompileTests
{
    static AtomRow Row(string paramsJson) => new()
    {
        AtomId = AtomRow.DeriveId("atom.test-bullet-modify", "", 1),
        KindId = "bullet.modify",
        FamilyId = "atom.test-bullet-modify",
        Variant = "",
        Tier = 1,
        Name = "Test Bullet Modify",
        ParamsJson = paramsJson,
        WhenJson = "{}", // no trigger: a permanent modifier
        IcdKey = "test.bullet-modify.compile",
    };

    [Fact]
    public void A_bullet_modify_atom_compiles_to_a_BulletModify_action_row_with_plain_params()
    {
        var row = Row("""{"op":"scale","amount":1500,"bulletType":3,"moveWay":"Track"}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);
        Assert.Empty(compiled.Runtime); // must land on the COMPILED path, not the runner

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);

        Assert.Equal(EffectActions.BulletModify, action.Action);

        // No op-as-key rewrite for this kind — op/amount/bulletType/moveWay travel verbatim, unlike
        // stat.modify/stat.derived's {op,amount} -> {flat} transform.
        Assert.Equal("scale", action.Params["op"]?.ToString());
        Assert.Equal(1500d, Convert.ToDouble(action.Params["amount"]));
        Assert.Equal(3d, Convert.ToDouble(action.Params["bulletType"]));
        Assert.Equal("Track", action.Params["moveWay"]?.ToString());

        // A permanent modifier declares no trigger, so the def must be Passive or the bag's lifecycle
        // pair never fires (definitions.md §14.2) — the same rule stat.derived's own compile test pins.
        Assert.Equal(EffectTypes.Passive, def.EffectType);
    }

    [Fact]
    public void A_bullet_modify_atom_with_only_the_required_params_still_compiles()
    {
        var row = Row("""{"op":"add","amount":50}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);
        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);

        Assert.Equal("add", action.Params["op"]?.ToString());
        Assert.Equal(50d, Convert.ToDouble(action.Params["amount"]));
        Assert.False(action.Params.ContainsKey("bulletType"));
        Assert.False(action.Params.ContainsKey("moveWay"));
    }

    // §2b: Battle has no projectile consumer today — RuntimeUnsupported at classify/bind, not a
    // silent no-op. Battle_support_is_narrow_and_honest is the AtomKindRegistryTests sibling of this.
    [Fact]
    public void A_bullet_modify_atom_is_RuntimeUnsupported_in_Battle()
    {
        var row = Row("""{"op":"set","amount":100}""");

        var verdict = Compilability.Classify(row, RuntimeId.Battle);

        Assert.Equal(AtomPath.Rejected, verdict.Path);
        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, verdict.Rejection);
    }

    // Bind-gate counterpart of the classify-level check above, same shape as BindGateTests'
    // "board.action is RuntimeUnsupported in battle" case.
    [Fact]
    public void BulletModify_is_RuntimeUnsupported_at_BindGate_in_Battle()
    {
        var row = Row("""{"op":"set","amount":100}""");

        var r = BindGate.Check(new[] { row }, OwnerScope.Match, new BindContext(RuntimeId.Battle));

        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, r.Reason);
    }
}
