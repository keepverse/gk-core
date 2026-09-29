using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// What E11 falsified about the schema (spec-effect-def-migration.md).
///
/// <para>The module's whole point is that migrating real content is the first thing that can prove
/// the vocabulary wrong. It proved four things wrong, each the same shape: a param declared from the
/// documentation rather than from the executor, or marked required when the shipped content supplies
/// it through the grant overlay.</para>
/// </summary>
public class MigrationSchemaTests
{
    static AtomRow Row(string kind, string paramsJson, string? when = null) => new()
    {
        AtomId = "atom.sample.t1",
        KindId = kind,
        FamilyId = "atom.sample",
        Tier = 1,
        Name = "Sample",
        WhenJson = when ?? "{}",
        ParamsJson = paramsJson,
    };

    // ---- the compiled def's identity ---------------------------------------------------------------

    [Fact]
    public void The_compiled_def_id_is_the_icd_key_verbatim()
    {
        // It used to be "atom." + icdKey, and icdKey defaults to atom_id, which already opens with
        // `atom.` — so every compiled def answered to `atom.atom.something`. Nothing asserted it.
        var row = Row("status.apply", """{"status":"butter","duration":4}""",
            $$"""{"trigger":"{{AtomTriggers.OnDamageDealt}}"}""");

        var catalog = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, 1);

        var def = Assert.Single(catalog.Defs);
        Assert.Equal("atom.sample.t1", def.EffectId);
        Assert.DoesNotContain("atom.atom.", def.EffectId, StringComparison.Ordinal);
    }

    [Fact]
    public void An_authored_icd_key_becomes_the_def_id_so_a_migration_keeps_its_effect_id()
    {
        // The reason the id has to be authorable at all: a player's stored grant already names
        // `fx.butter_on_hit`, and it must keep resolving once the def is a row.
        var row = Row("status.apply", """{"status":"butter","duration":4}""",
            $$"""{"trigger":"{{AtomTriggers.OnDamageDealt}}"}""") with { IcdKey = "fx.butter_on_hit" };

        var catalog = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, 1);

        Assert.Equal("fx.butter_on_hit", Assert.Single(catalog.Defs).EffectId);
    }

    // ---- status.clear ------------------------------------------------------------------------------

    [Fact]
    public void Status_clear_takes_status_which_is_the_key_the_executor_reads()
    {
        // ExecClearStatus reads "status" (InjectorEffectActionSink.cs:260). The schema declared
        // `statusId`, so the one shipped FA3 effect was unauthorable as an atom.
        var ok = AtomKindRegistry.Validate("status.clear",
            new Dictionary<string, object?> { ["status"] = "butter" });

        Assert.True(ok.IsOk, ok.ToString());
    }

    [Fact]
    public void Status_clear_rejects_the_key_that_nothing_reads()
    {
        // The opposite half. Without this the previous test only proves `status` was added, not that
        // the key nothing reads was taken away.
        var bad = AtomKindRegistry.Validate("status.clear",
            new Dictionary<string, object?> { ["statusId"] = "butter" });

        Assert.Equal(AtomRejectionReason.UnknownParam, bad.Reason);
    }

    [Fact]
    public void Status_clear_does_not_demand_a_target_it_can_resolve_from_the_event()
    {
        // `target` was a required object. The executor reads it as an optional string and falls back
        // to the resolved event target, which is what `fx.clear_butter` relies on.
        var ok = AtomKindRegistry.Validate("status.clear",
            new Dictionary<string, object?> { ["status"] = "butter", ["target"] = "self" });

        Assert.True(ok.IsOk, ok.ToString());
    }

    // ---- resource.delta ------------------------------------------------------------------------------

    [Fact]
    public void Resource_delta_declares_the_channel_the_executor_reads()
    {
        // ExecApplyResourceDelta reads "channel" (line 132). It was undeclared, so `fx.overlay_damage`
        // — whose entire params are `{channel: hp}` — could not be expressed.
        var ok = AtomKindRegistry.Validate("resource.delta",
            new Dictionary<string, object?> { ["channel"] = "hp" });

        Assert.True(ok.IsOk, ok.ToString());
    }

    [Fact]
    public void Resource_delta_still_rejects_a_channel_that_is_not_a_declared_key()
    {
        var bad = AtomKindRegistry.Validate("resource.delta",
            new Dictionary<string, object?> { ["chanel"] = "hp" });

        Assert.Equal(AtomRejectionReason.UnknownParam, bad.Reason);
    }

    // ---- D10: the magnitude that rides the overlay ----------------------------------------------------

    [Fact]
    public void A_shield_grant_with_no_amount_loads_because_the_overlay_carries_it()
    {
        // fx.shield_grant ships with EMPTY params. A required `amount` would force migration to
        // author a number the original never had.
        var ok = AtomKindRegistry.Validate("shield.grant", new Dictionary<string, object?>());

        Assert.True(ok.IsOk, ok.ToString());
    }

    [Fact]
    public void A_binding_whose_magnitude_is_in_neither_the_row_nor_an_overlay_is_refused_at_bind()
    {
        // The other half of making it optional. Load-time says "well-formed"; bind-time says
        // "executable here" — and a magnitude named nowhere applies nothing.
        var row = Row("shield.grant", "{}");

        var verdict = BindGate.Check(
            new[] { row }, Scope("match"), new BindContext(RuntimeId.Lawn), overlayKeys: null);

        Assert.Equal(AtomRejectionReason.MissingParam, verdict.Reason);
        Assert.Contains("amount", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_binding_passes_when_the_overlay_supplies_the_magnitude()
    {
        var row = Row("shield.grant", "{}");

        var verdict = BindGate.Check(
            new[] { row }, Scope("match"), new BindContext(RuntimeId.Lawn),
            overlayKeys: new[] { "amount" });

        Assert.True(verdict.IsOk, verdict.ToString());
    }

    [Fact]
    public void The_row_alone_is_enough_when_it_authors_the_magnitude_itself()
    {
        var row = Row("shield.grant", """{"amount":250}""");

        var verdict = BindGate.Check(
            new[] { row }, Scope("match"), new BindContext(RuntimeId.Lawn), overlayKeys: null);

        Assert.True(verdict.IsOk, verdict.ToString());
    }

    [Fact]
    public void A_null_overlay_means_no_overlay_rather_than_do_not_check()
    {
        // The dangerous reading of an optional parameter. If null had meant "skip", every caller that
        // had not yet been taught about overlays would bind magnitude-less content silently.
        var row = Row("resource.delta", """{"channel":"hp"}""");

        Assert.Equal(
            AtomRejectionReason.MissingParam,
            BindGate.Check(new[] { row }, Scope("match"), new BindContext(RuntimeId.Lawn)).Reason);
    }

    static OwnerScope Scope(string key)
    {
        OwnerScope.Validate(OwnerKind.Match, key, out var scope);
        return scope;
    }
}
