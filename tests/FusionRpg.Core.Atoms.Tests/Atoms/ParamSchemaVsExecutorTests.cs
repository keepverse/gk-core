using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// D7 (definitions.md §13): a param schema declares only keys its executor honours, with the type the
/// executor reads. Five schemas did not, and each mismatch produces a different silent failure — a
/// string where an int is read, a key no allowlist carries, a payload on the wrong opcode.
///
/// <para>These tests pin the schema to the executor, so a future edit to either side breaks a test
/// rather than shipping content that validates and then does nothing.</para>
/// </summary>
public class ParamSchemaVsExecutorTests
{
    static ParamDef Param(string kindId, string name)
    {
        var kind = AtomKindRegistry.Get(kindId);
        Assert.NotNull(kind);
        var def = kind!.Params.Defs.FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.Ordinal));
        Assert.True(def is not null, $"{kindId} declares no param '{name}'");
        return def!;
    }

    static bool Declares(string kindId, string name) =>
        AtomKindRegistry.Get(kindId)!.Params.Defs.Any(d =>
            string.Equals(d.Name, name, StringComparison.Ordinal));

    // ---- (a) box.set.boxType is read as an int -------------------------------------------------
    // InjectorEffectActionSink.ExecSetBox: JsonOverlay.GetInt(item.Params, "boxType", 1).
    // Declared as String, an atom authoring boxType: "dirt" validated and then silently set box 1.

    [Fact]
    public void BoxType_is_an_int_because_that_is_what_the_executor_reads()
    {
        Assert.Equal(ParamKind.Int, Param("box.set", "boxType").Kind);
    }

    // ---- (b) status.apply uses FA2's names and units -------------------------------------------
    // FA2 allowlist: { status, duration, level, chance, icd_ms, max_stacks, filters }.
    // The executor reads "status" (string) and "duration" as float SECONDS.

    [Fact]
    public void Status_apply_declares_the_names_FA2_actually_allowlists()
    {
        Assert.True(Declares("status.apply", "status"), "FA2 reads 'status', not 'statusId'");
        Assert.True(Declares("status.apply", "duration"), "FA2 reads 'duration' (seconds), not 'durationMs'");
        Assert.True(Declares("status.apply", "level"));
    }

    [Fact]
    public void Status_apply_does_not_declare_keys_FA2_has_never_carried()
    {
        Assert.False(Declares("status.apply", "statusId"), "statusId is an FA10 key");
        Assert.False(Declares("status.apply", "durationMs"), "durationMs is an FA10 key");
    }

    // ---- (c) status.apply.target does not exist on FA2 -----------------------------------------
    // The target comes from ResolveStatusTargetPtr(ctx) — the EVENT, not a param. Declaring it
    // required made G5 unauthorable: a load-time check cannot close a runtime-empty-ptr hole.

    [Fact]
    public void Status_apply_does_not_declare_a_target_param()
    {
        Assert.False(Declares("status.apply", "target"),
            "FA2 has no target param; the target is resolved from the event");
    }

    // ---- (d) the DoT / contagion payload lives on FA10, not FA2 --------------------------------
    // periodMs, tickBudget and spread are in the ApplyResourceDelta allowlist and are consumed by
    // StatusEffectBridge.TryApplyFromGrant, which EffectBag calls only in the resource-delta branch.

    [Theory]
    [InlineData("periodMs")]
    [InlineData("tickBudget")]
    [InlineData("spread")]
    public void Dot_and_contagion_payload_is_declared_on_resource_delta_not_status_apply(string key)
    {
        Assert.False(Declares("status.apply", key), $"{key} is an FA10 key, not FA2");
        Assert.True(Declares("resource.delta", key), $"{key} belongs on resource.delta (FA10)");
    }

    // ---- (e) shield.grant.sourceClass is honoured and was undeclared ---------------------------
    // EffectBag.ExecGrantShield reads it to pick PriorityAura/PriorityInnate and to flip the
    // refillOnMerge default. Undeclared, every atom-granted shield was PrioritySkill/refill=true.

    [Fact]
    public void Shield_grant_declares_sourceClass()
    {
        Assert.Equal(ParamKind.String, Param("shield.grant", "sourceClass").Kind);
    }

    // ---- (f) spawn.entity.count, referenced by the pricing formula and declared nowhere --------

    [Fact]
    public void Spawn_entity_declares_count()
    {
        Assert.Equal(ParamKind.Int, Param("spawn.entity", "count").Kind);
    }

    // ---- the general rule, asserted rather than trusted ----------------------------------------

    [Fact]
    public void Every_declared_param_has_a_name_and_no_kind_declares_a_duplicate()
    {
        foreach (var kind in AtomKindRegistry.All)
        {
            var names = kind.Params.Defs.Select(d => d.Name).ToList();
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }
}
