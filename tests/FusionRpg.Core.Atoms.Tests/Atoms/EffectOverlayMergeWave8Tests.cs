using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// Found 2026-09-04 while independently re-verifying E41's own report: <c>EffectBag.Grant</c> calls
/// <c>EffectOverlayMerge.TryValidateOverlayForDef(def.Actions, ...)</c> UNCONDITIONALLY, for every
/// grant, and that method throws <c>"unknown action &lt;X&gt;"</c> the instant any action in the def's
/// compiled list is missing from <c>EffectOverlayMerge</c>'s private <c>AllowedByAction</c> dictionary —
/// even against an empty overlay. <c>ModifyMatch</c> (E35), <c>WaveControl</c> (E36) and
/// <c>BulletModify</c> (E37) had no entry there, so a real <c>Grant()</c> of any
/// <c>match.modify</c>/<c>wave.control</c>/<c>bullet.modify</c> content — the only way any of that
/// shipped work ever runs in a live match — threw at the very first line of <c>Grant</c>, regardless of
/// runtime or overlay content. Each module's own tests never caught this because they exercise
/// <c>AtomCompiler.Compile</c>/<c>InjectorEffectActionSink.Execute</c> directly, never
/// <c>EffectBag.Grant</c> — this is the missing link. Fixed by adding the three entries
/// (<c>EffectProcAndOwner.cs</c>); this is the regression test that proves it and would have caught the
/// gap the moment any of the three modules shipped.
/// </summary>
public class EffectOverlayMergeWave8Tests
{
    static EffectDef Def(string action, Dictionary<string, object?> pars) => new()
    {
        EffectId = "test." + action.ToLowerInvariant(),
        EffectType = EffectTypes.Passive,
        Name = "test " + action,
        Triggers = new List<string>(),
        Actions = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = action, Params = pars },
        },
    };

    static void GrantSucceeds(string action, Dictionary<string, object?> pars)
    {
        var harness = new FoundationHarness().WithCatalog(new[] { Def(action, pars) });

        // The bug this test exists to catch: before the fix, EVERY one of these three throws
        // InvalidOperationException("unknown action " + action) right here, regardless of the
        // overlay (empty in every case below) and regardless of runtime.
        var grant = harness.Grant(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "test." + action.ToLowerInvariant(),
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "test",
        });

        Assert.NotNull(grant);
    }

    [Fact]
    public void ModifyMatch_grants_without_throwing_unknown_action()
    {
        GrantSucceeds(EffectActions.ModifyMatch, new Dictionary<string, object?>
        {
            ["field"] = "zombieSpeedMultiplier",
            ["amount"] = 400,
        });
    }

    [Fact]
    public void WaveControl_grants_without_throwing_unknown_action()
    {
        GrantSucceeds(EffectActions.WaveControl, new Dictionary<string, object?>
        {
            ["op"] = "setTimer",
            ["timerMs"] = 3000,
        });
    }

    [Fact]
    public void BulletModify_grants_without_throwing_unknown_action()
    {
        GrantSucceeds(EffectActions.BulletModify, new Dictionary<string, object?>
        {
            ["op"] = "scale",
            ["amount"] = 1500,
        });
    }

    /// <summary>
    /// PLANTED VIOLATION shape, inlined rather than toggled: proves the harness genuinely discriminates
    /// — an action this dictionary really doesn't know throws exactly the error the fix removes for the
    /// three kinds above, so the passing tests above are proof of the fix, not an artifact of a harness
    /// that never throws.
    /// </summary>
    [Fact]
    public void An_action_genuinely_absent_from_the_allowlist_still_throws_unknown_action()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GrantSucceeds("NotARealAction", new Dictionary<string, object?>()));

        Assert.Contains("unknown action", ex.Message);
    }
}
