using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>aura-skill T3 (audit D3): an actor whose <c>EquippedActionIds</c> cannot be resolved
/// (no <see cref="ActionCatalog"/> supplied, or an id the catalog doesn't have) used to throw and
/// fail the whole battle — which meant the first authored Skill grant broke every web battle, and a
/// stored <see cref="BattleSetup"/> log row carrying one re-threw on every replay, forever. This
/// degrades to the basic-attack fallback plus a named <see cref="BattleReport.Warnings"/> entry
/// instead.</summary>
public class BattleRunStateDegradeTests
{
    static BattleSetup SetupWithEquippedActions(IReadOnlyList<string> ids)
    {
        var stomp = BattleGoldenTests.StompSetup();
        var squad0 = stomp.Squad[0] with { EquippedActionIds = ids };
        return stomp with { Squad = new[] { squad0 }.Concat(stomp.Squad.Skip(1)).ToArray() };
    }

    [Fact]
    public void No_catalog_supplied_degrades_to_basic_attack_instead_of_throwing()
    {
        var setup = SetupWithEquippedActions(new[] { "atom.some-skill.t1" });

        // Pre-T3 this threw ArgumentException every time — the whole point of the regression.
        var report = BattleEngine.Resolve(setup, seed: 1001);

        Assert.NotNull(report.Warnings);
        Assert.Contains(report.Warnings!, w => w.Contains("squad:0") && w.Contains("no ActionCatalog"));
    }

    [Fact]
    public void A_previously_poisoned_stored_setup_replays_cleanly()
    {
        // The exact D3 shape: a stored BattleSetup with a non-empty EquippedActionIds and no
        // catalog at replay time (WebMatchService.cs's own replay call sites never pass one).
        var storedSetup = SetupWithEquippedActions(new[] { "atom.retired-skill.t1" });

        var first = BattleEngine.Resolve(storedSetup, seed: 2002);
        var replay = BattleEngine.Resolve(storedSetup, seed: 2002);

        Assert.Equal(first.Outcome, replay.Outcome);
        Assert.NotNull(replay.Warnings);
    }

    [Fact]
    public void An_id_missing_from_a_real_catalog_also_degrades_with_a_warning()
    {
        var setup = SetupWithEquippedActions(new[] { "atom.does-not-exist.t1" });
        var emptyCatalog = ActionCatalog.Empty;

        var report = BattleEngine.Resolve(setup, seed: 3003, actionCatalog: emptyCatalog);

        Assert.NotNull(report.Warnings);
        Assert.Contains(report.Warnings!, w => w.Contains("atom.does-not-exist.t1"));
    }

    [Fact]
    public void A_setup_with_no_equipped_actions_never_produces_a_warning()
    {
        var report = BattleEngine.Resolve(BattleGoldenTests.StompSetup(), seed: 4004);

        Assert.Null(report.Warnings);
    }
}
