using FusionRpg.CheatCore;
using Xunit;

namespace FusionRpg.CheatCore.Tests;

public class CheatSchemaTests
{
    [Fact]
    public void ScalePercent_zero_is_not_identity_but_one_is()
    {
        Assert.True(CheatSchema.IsUnsetOrIdentity("A-P-HP%", true, 1));
        Assert.False(CheatSchema.IsUnsetOrIdentity("A-P-HP%", true, 0));
        Assert.False(CheatSchema.IsUnsetOrIdentity("A-P-HP%", true, 2));
    }

    [Fact]
    public void Absolute_negative_and_zero_strip_from_document()
    {
        Assert.True(CheatSchema.ShouldStripFromDocument("P-HP", true, -1, "number"));
        Assert.True(CheatSchema.ShouldStripFromDocument("P-HP", true, 0, "number"));
        Assert.False(CheatSchema.ShouldStripFromDocument("P-HP", true, 500, "number"));
    }

    [Fact]
    public void MigrateEntries_strips_sentinels_keeps_real_values()
    {
        var input = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "A-P-HP%", ["enabled"] = true, ["floatValue"] = 1d, ["kind"] = "slider" },
            new() { ["id"] = "A-P-HP%", ["enabled"] = true, ["floatValue"] = 0d, ["kind"] = "slider" },
            new() { ["id"] = "P-HP", ["enabled"] = true, ["floatValue"] = -1d, ["kind"] = "number" },
            new() { ["id"] = "P-HP", ["enabled"] = true, ["floatValue"] = 900d, ["kind"] = "number" },
            new() { ["id"] = "Z-GOD", ["enabled"] = true, ["floatValue"] = 0d, ["kind"] = "toggle" }
        };
        // First A-P-HP%=1 stripped; second 0 kept (dangerous but intentional set); P-HP -1 stripped; 900 kept; toggle kept
        var first = input.Take(1).ToList();
        var migratedFirst = CheatSchema.MigrateEntries(first, out var c1);
        Assert.True(c1);
        Assert.Empty(migratedFirst);

        var absNeg = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "P-HP", ["enabled"] = true, ["floatValue"] = -1d, ["kind"] = "number" },
            new() { ["id"] = "P-HP", ["enabled"] = true, ["floatValue"] = 900d, ["kind"] = "number" }
        };
        // Can't have duplicate ids in one list meaningfully — test separately
        var mNeg = CheatSchema.MigrateEntries(
            new[] { new Dictionary<string, object?> { ["id"] = "P-HP", ["enabled"] = true, ["floatValue"] = -1d, ["kind"] = "number" } },
            out var cNeg);
        Assert.True(cNeg);
        Assert.Empty(mNeg);

        var mOk = CheatSchema.MigrateEntries(
            new[] { new Dictionary<string, object?> { ["id"] = "P-HP", ["enabled"] = true, ["floatValue"] = 900d, ["kind"] = "number" } },
            out var cOk);
        Assert.False(cOk);
        Assert.Single(mOk);

        var mZeroPct = CheatSchema.MigrateEntries(
            new[] { new Dictionary<string, object?> { ["id"] = "A-P-HP%", ["enabled"] = true, ["floatValue"] = 0d, ["kind"] = "slider" } },
            out var cZero);
        Assert.False(cZero);
        Assert.Single(mZeroPct);
    }

    [Fact]
    public void EffectiveFloat_unset_uses_display_default()
    {
        Assert.Equal(1d, CheatSchema.EffectiveFloat("A-P-HP%", false, 0));
        Assert.Equal(0d, CheatSchema.EffectiveFloat("A-P-HP+", false, 99));
        Assert.Equal(-1d, CheatSchema.EffectiveFloat("P-HP", false, 50));
        Assert.Equal(50d, CheatSchema.EffectiveFloat("P-HP", true, 50));
    }

    [Fact]
    public void EffectiveToggle_unset_defaults()
    {
        Assert.True(CheatSchema.EffectiveToggle("A-APPLY", false, false));
        Assert.True(CheatSchema.EffectiveToggle("SYS-EMIT-PROOF", false, false));
        Assert.True(CheatSchema.EffectiveToggle("SYS-DAMAGE-FX", false, false));
        Assert.False(CheatSchema.EffectiveToggle("P-GOD", false, true));
        Assert.True(CheatSchema.EffectiveToggle("P-GOD", true, true));
        Assert.False(CheatSchema.EffectiveToggle("P-GOD", true, false));
    }

    [Fact]
    public void LawnBasicAttackFeature_kill_switch_display_default_tracks_the_features_own_default()
    {
        // This asserts the SCHEMA's display/migration default, which must track
        // LawnBasicAttackFeature.DefaultEnabled -- it is not itself the feature's default. That
        // separation is the 2026-09-14 fix: the flag used to take its only "on by default" from this
        // row, which is a boundary defect (a production switch cannot source its default from a
        // debug/QA toggle store whose contract never promised one).
        //
        // 2026-09-16 owner decision: ON. It was OFF from 2026-09-15 (lawn-combat-wire L-N1) after a
        // measured 300-zombie frame-budget breach -- a ruling that named its own release condition,
        // "until a perf pass clears it". The pass ran: the RPG capture pipeline went from 28.06% of
        // wall at 19.8 fps to 3.46% at 59.7 fps, under the proposed 6% ceiling.
        //
        // The path exercised below is the real one: a fresh boot is isSet=false, stored=false --
        // CheatEntry's own zero value before anything ever calls SetToggle -- and it is exactly what
        // CheatState.On("LAWN-BASIC-ATTACK") takes on every boot. A 2026-09-14 live incident had this
        // reading off while the feature's doc comment said on; reproduced here in milliseconds
        // instead of a live combat round trip.
        Assert.True(CheatSchema.EffectiveToggle("LAWN-BASIC-ATTACK", isSet: false, stored: false));

        // An explicit set still wins in both directions -- that is what makes a controlled A/B possible.
        Assert.False(CheatSchema.EffectiveToggle("LAWN-BASIC-ATTACK", isSet: true, stored: false));
        Assert.True(CheatSchema.EffectiveToggle("LAWN-BASIC-ATTACK", isSet: true, stored: true));
    }

    [Fact]
    public void ShouldStrip_ScaleFlat_and_Config_identity()
    {
        Assert.True(CheatSchema.ShouldStripFromDocument("A-P-HP+", true, 0, "number"));
        Assert.False(CheatSchema.ShouldStripFromDocument("A-P-HP+", true, 10, "number"));
        Assert.True(CheatSchema.ShouldStripFromDocument("E-ZH", true, 1, "slider"));
        Assert.False(CheatSchema.ShouldStripFromDocument("E-ZH", true, 2.5, "slider"));
        Assert.True(CheatSchema.ShouldStripFromDocument("G-TIMESCALE", true, 1, "slider"));
        Assert.False(CheatSchema.ShouldStripFromDocument("G-TIMESCALE", true, 0.5, "slider"));
    }

    [Fact]
    public void MigrateEntries_mixed_legacy_document()
    {
        var input = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "A-P-HP%", ["enabled"] = true, ["floatValue"] = 1d, ["kind"] = "slider" },
            new() { ["id"] = "A-P-ATK%", ["enabled"] = true, ["floatValue"] = 2d, ["kind"] = "slider" },
            new() { ["id"] = "A-P-HP+", ["enabled"] = true, ["floatValue"] = 0d, ["kind"] = "number" },
            new() { ["id"] = "E-ZH", ["enabled"] = true, ["floatValue"] = 1d, ["kind"] = "slider" },
            new() { ["id"] = "P-HP", ["enabled"] = true, ["floatValue"] = 500d, ["kind"] = "number" },
            new() { ["id"] = "P-GOD", ["enabled"] = true, ["floatValue"] = 0d, ["kind"] = "toggle" }
        };
        var migrated = CheatSchema.MigrateEntries(input, out var changed);
        Assert.True(changed);
        Assert.Equal(3, migrated.Count);
        Assert.Contains(migrated, e => e["id"]?.ToString() == "A-P-ATK%");
        Assert.Contains(migrated, e => e["id"]?.ToString() == "P-HP");
        Assert.Contains(migrated, e => e["id"]?.ToString() == "P-GOD");
        Assert.DoesNotContain(migrated, e => e["id"]?.ToString() == "A-P-HP%");
        Assert.DoesNotContain(migrated, e => e["id"]?.ToString() == "A-P-HP+");
        Assert.DoesNotContain(migrated, e => e["id"]?.ToString() == "E-ZH");
    }
}
