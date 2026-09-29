using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// aura-skill-todo.md Phase 5 / TC2 — <b>the `stat.derived` atom → lawn entity chain, now complete.</b>
///
/// <para><b>History, kept because it is the point.</b> This file was written as four <i>tripwires</i>:
/// each asserted a CURRENT ABSENCE and was designed to fail the day Wave 6 closed it. Probing had
/// found the chain broken in four places, and `spec-derived-write-lawn.md` was claiming *"this module's
/// own half is done"* while the executor would in fact have consumed nothing.</para>
///
/// <para>The tripwires then fired — because the gap was closed the same day, once measuring (rather
/// than assuming) showed the missing opcode moved <b>no goldens and no content hashes</b>. What was
/// recorded as "a loader, an importer run, and a producer of bindings" turned out to be, for this
/// path, <b>one opcode mapping and one overlay-whitelist row</b>. The assertions below are their
/// inverted form: they now prove each link EXISTS, so the chain cannot silently regress.</para>
/// </summary>
public class StatDerivedCompileGapTests
{
    /// <summary><b>Link 1 — the action constant exists.</b> Was: "no derived action constant exists".
    /// <c>ModifyDerivedStat</c> is deliberately declarative: nothing executes it, because a
    /// <c>stat.derived</c> atom is a permanent modifier that declares no trigger, so the bag never
    /// fires it. That is why adding it needed no sink executor in either runtime.</summary>
    [Fact]
    public void Link1_an_effect_action_exists_for_a_derived_stat_write()
    {
        var actionNames = typeof(EffectActions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Contains(EffectActions.ModifyDerivedStat, actionNames);
        Assert.Equal("ModifyDerivedStat", EffectActions.ModifyDerivedStat);
    }

    /// <summary><b>Link 2 — the whitelist row matches the atom's own ParamSchema exactly.</b> The
    /// schema is the SSOT for what a `stat.derived` atom carries; a second spelling in the overlay
    /// whitelist would be a silent divergence that only shows up as a rejected grant at runtime. This
    /// asserts the two agree, in both directions.</summary>
    [Fact]
    public void Link2_the_whitelist_matches_the_COMPILED_shape_not_the_authored_schema()
    {
        // The authored atom carries {channel, op, amount}...
        var kind = AtomKindRegistry.Get("stat.derived");
        Assert.NotNull(kind);
        Assert.Equal(new[] { "amount", "channel", "op" },
            kind!.Params.Defs.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        // ...but AtomCompiler.ToOpcodeShape rewrites it to the op-as-KEY form {channel, flat} before it
        // reaches a def, exactly as it already does for stat.modify. The overlay whitelist must match
        // that COMPILED shape, not the authored one -- getting this backwards is a grant that validates
        // in a unit test and is refused at runtime.
        var actions = new[] { new FusionRpg.Core.Effects.EffectActionRow { Action = EffectActions.ModifyDerivedStat } };

        foreach (var key in new[] { "channel", "flat", "increased", "replace", "flag" })
            Assert.True(FusionRpg.Core.Effects.EffectOverlayMerge.TryValidateOverlayForDef(
                actions, new Dictionary<string, object?> { [key] = 1 }, out var err),
                $"compiled-shape key '{key}' must be accepted: {err}");

        // `more` is deliberately absent: the derived side has no More op.
        Assert.False(FusionRpg.Core.Effects.EffectOverlayMerge.TryValidateOverlayForDef(
            actions, new Dictionary<string, object?> { ["more"] = 1 }, out _));

        // ...and it is a whitelist, not a wildcard.
        Assert.False(FusionRpg.Core.Effects.EffectOverlayMerge.TryValidateOverlayForDef(
            actions, new Dictionary<string, object?> { ["notAParam"] = 1 }, out _));
    }

    /// <summary><b>Link 3 — the compiler maps the kind to that action.</b> Was: "`stat.derived` falls
    /// through to null, so a compiled atom gets no action row and therefore no params for anyone to
    /// read". Asserted through the compiler's real output on a real atom row rather than by reading a
    /// private switch.</summary>
    [Fact]
    public void Link3_a_stat_derived_atom_compiles_to_a_ModifyDerivedStat_action_row()
    {
        // Same shape as the real shipped atom in gk-data/packs/fusion/data/seed/atoms/trait-critical-hunter.json, and the
        // same construction idiom AtomCompilerTests uses.
        var row = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.test-derived", "", 1),
            KindId = "stat.derived",
            FamilyId = "atom.test-derived",
            Variant = "",
            Tier = 1,
            Name = "Test Derived",
            ParamsJson = """{"channel":"combat.crit.rate.omni","op":"flat","amount":150}""",
            WhenJson = "{}",   // no trigger: a permanent modifier
            IcdKey = "test.derived.compile",
        };

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);

        Assert.Equal(EffectActions.ModifyDerivedStat, action.Action);
        Assert.Equal("combat.crit.rate.omni", action.Params["channel"]?.ToString());

        // Op-as-KEY: ToOpcodeShape turned {op:"flat", amount:150} into {flat:150}. Asserting the
        // transform here is what stops the reader and the whitelist from drifting back to {op, amount},
        // which would validate in isolation and then match nothing real.
        Assert.False(action.Params.ContainsKey("op"));
        Assert.False(action.Params.ContainsKey("amount"));
        Assert.Equal(150d, Convert.ToDouble(action.Params["flat"]));

        // A permanent modifier declares no trigger, so the def must be Passive or the bag never
        // completes its lifecycle (definitions.md §14.2).
        Assert.Equal(EffectTypes.Passive, def.EffectType);
    }

    /// <summary><b>Link 4 — `Lawn = Full` is now true end to end, not just "a consumer exists".</b>
    /// Sim moved too: mechanism-wiring E5 (2026-09-06) opened `ActorDerivedLookup`'s contribution fold
    /// to `Partial` (a plain sum honouring `Flat`/`Increased` but not `Replace`/`Flag`), then
    /// sim-hub-parity (T15, 2026-09-13) closed the gap to `Full` (the SAME op-aware fold every other
    /// runtime uses — `EffectOfflineKitTests.The_four_derived_ops_decide_Full_versus_Partial`). Renamed
    /// from "...and_sim_remains_quarantined", then from "...and_sim_opens_partially", neither of which
    /// stayed true.</summary>
    [Fact]
    public void Link4_lawn_and_sim_are_both_served_end_to_end()
    {
        var kind = AtomKindRegistry.Get("stat.derived");
        Assert.NotNull(kind);

        Assert.Equal(RuntimeState.Full, kind!.Support.Lawn);
        Assert.Equal(RuntimeState.Full, kind.Support.Sim);
    }
}
