using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W26 (spec-ai-commander.md §The decision layer): the seam, before there is anything behind it.
///
/// A policy is handed one faction's belief and hands back orders. It is not a phase, it does not
/// touch <c>WorldState</c>, and it cannot ask about a faction other than the one whose eyes it was
/// given — the view carries the faction and the turn, so there is no second parameter to disagree
/// with them.
/// </summary>
public class PolicySeamTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static IWorldView ViewOf(string faction) => new BelievedWorldView(World(), faction);

    // ---- the catalog ---------------------------------------------------------------------

    [Fact]
    public void The_policy_the_template_names_is_one_the_catalog_knows()
    {
        // `first-light` points both non-player factions at `stand-fast`. If that id ever stops
        // resolving, every AI faction silently stops playing — so the template and the catalog are
        // asserted against each other rather than trusted to stay in step.
        foreach (var faction in World().Factions.Where(f => f.PolicyId != null))
            Assert.True(FactionPolicies.IsKnown(faction.PolicyId),
                $"'{faction.FactionId}' names policy '{faction.PolicyId}', which nothing can resolve.");
    }

    [Fact]
    public void Resolving_a_policy_nobody_has_heard_of_throws_rather_than_returning_nothing()
    {
        // A null here would read as "this faction has no brain", which is exactly what a typo in a
        // template would look like — and it would look like it for the whole campaign.
        Assert.Throws<KeyNotFoundException>(() => FactionPolicies.Resolve("skulk"));
    }

    [Fact]
    public void Every_policy_in_the_catalog_answers_to_the_id_it_is_filed_under()
    {
        foreach (var id in FactionPolicies.All)
            Assert.Equal(id, FactionPolicies.Resolve(id).PolicyId);
    }

    // ---- validation ----------------------------------------------------------------------

    [Fact]
    public void A_world_naming_a_policy_that_does_not_exist_fails_validation()
    {
        var world = World();
        var broken = world with
        {
            Factions = world.Factions
                .Select(f => f.FactionId == "zomboss" ? f with { PolicyId = "galaxy-brain" } : f)
                .ToList()
        };

        var error = Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(broken));
        Assert.Contains("galaxy-brain", error.Message, StringComparison.Ordinal);
        Assert.Contains("zomboss", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_faction_with_no_policy_at_all_is_fine_because_that_is_the_human()
    {
        var world = World();
        var human = world with
        {
            Factions = world.Factions
                .Select(f => f.FactionId == "zomboss" ? f with { PolicyId = null } : f)
                .ToList()
        };

        WorldValidation.Validate(human);   // no throw: null means "somebody is playing this one"
    }

    // ---- stand-fast ----------------------------------------------------------------------

    [Fact]
    public void Stand_fast_files_one_order_so_the_log_can_tell_silence_from_absence()
    {
        var orders = FactionPolicies.Resolve("stand-fast").Decide(ViewOf("wild"), seed: 1);

        var order = Assert.Single(orders);
        Assert.Equal(WorldCommandKinds.StandFast, order.Command.Kind);
        Assert.Equal("wild", order.Command.CommanderId);
        Assert.Null(order.Command.EntityId);
        Assert.False(string.IsNullOrWhiteSpace(order.Reason));
    }

    [Fact]
    public void What_stand_fast_files_is_a_legal_order()
    {
        // A policy that files something admission refuses would be reported as dropped every turn
        // and nobody would notice, because doing nothing is what it was trying to do anyway.
        var order = FactionPolicies.Resolve("stand-fast").Decide(ViewOf("zomboss"), seed: 1).Single();

        var (ok, reason) = WorldCommandAdmission.Admit(World(), order.Command);
        Assert.True(ok, reason);
    }

    [Fact]
    public void An_order_is_addressed_to_the_faction_whose_eyes_it_was_given()
    {
        // The view carries the faction, so a policy cannot file on somebody else's behalf even by
        // accident. This is the test that fails if `Decide` ever grows a factionId parameter.
        foreach (var faction in new[] { "dave", "wild", "zomboss" })
            Assert.All(
                FactionPolicies.Resolve("stand-fast").Decide(ViewOf(faction), seed: 1),
                o => Assert.Equal(faction, o.Command.CommanderId));
    }

    // ---- purity --------------------------------------------------------------------------

    [Fact]
    public void The_same_belief_and_seed_decide_the_same_way_twice()
    {
        static string Render(IReadOnlyList<PolicyOrder> orders) =>
            string.Join("|", orders.Select(o => $"{o.Command.CommandId}:{o.Command.Kind}:{o.Reason}"));

        var policy = FactionPolicies.Resolve("stand-fast");

        Assert.Equal(
            Render(policy.Decide(ViewOf("zomboss"), seed: 1)),
            Render(policy.Decide(ViewOf("zomboss"), seed: 1)));
    }

    [Fact]
    public void A_command_id_is_unique_per_commander_per_turn_and_fits_the_store()
    {
        // The store's primary key is (world, turn, commander, commandId), and admission bounds the
        // id's length. Both are the policy's problem, because the policy is what invents them.
        var orders = FactionPolicies.Resolve("stand-fast").Decide(ViewOf("wild"), seed: 1);
        var ids = orders.Select(o => o.Command.CommandId).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.InRange(id.Length, 1, WorldCommandAdmission.MaxCommandIdLength));
    }
}
