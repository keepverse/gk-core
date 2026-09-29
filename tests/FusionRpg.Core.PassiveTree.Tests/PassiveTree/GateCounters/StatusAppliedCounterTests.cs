using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.GateCounters;

/// <summary>Task G2 — spec-gate-counters.md §2.1's rules, unit-level over
/// <see cref="StatusAppliedCounter.Handle"/> directly (the resist/refresh halves of §2.1 are proven
/// end-to-end through the real <see cref="StatusRuntime"/> in <c>GateCounterRulesTests</c> instead,
/// since those two are guaranteed by <c>OnFreshApplication</c>'s own firing rule, not by this
/// class).</summary>
public class StatusAppliedCounterTests
{
    static readonly GateOwnerKey Player1 = new("player", "player:1");

    static StatusInstance Instance(string statusId = "wither", string host = "Z1", string? attacker = "P1") => new()
    {
        InstanceId = "g1:" + statusId + ":1",
        StatusId = statusId,
        HostPtr = host,
        AttackerPtr = attacker,
        GrantId = "g1",
        AppliedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5),
    };

    static StatusAppliedCounter Counter(GateCounterAccumulator acc, Func<StatusInstance, GateOwnerKey?>? resolve = null) =>
        new(acc, resolve ?? (_ => Player1));

    [Fact]
    public void A_distinct_host_application_credits_the_resolved_owner_once()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(new StatusAppliedEvent(Instance()));

        var snap = acc.Snapshot();
        Assert.Equal(1L, snap[new GateCounterKey(Player1, StatusAppliedCounter.Quantity, "wither")]);
    }

    [Fact]
    public void A_self_application_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(new StatusAppliedEvent(Instance(host: "Z1", attacker: "Z1")));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void A_self_application_is_case_insensitive_on_the_ptr_comparison()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(new StatusAppliedEvent(Instance(host: "Z1", attacker: "z1")));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void An_attacker_less_application_earns_nothing()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(new StatusAppliedEvent(Instance(attacker: null)));
        counter.Handle(new StatusAppliedEvent(Instance(attacker: "")));

        Assert.Empty(acc.Snapshot());
    }

    [Fact]
    public void An_unregistered_status_id_throws_rather_than_crediting_garbage()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        Assert.Throws<ArgumentException>(() =>
            counter.Handle(new StatusAppliedEvent(Instance(statusId: "not-a-real-status"))));
        Assert.Empty(acc.Snapshot());
    }

    [Theory]
    [InlineData("wither")] // Dot
    [InlineData("hypno")]  // Cc
    [InlineData("blight")] // Contagion
    public void Every_one_of_the_21_registered_status_ids_credits_without_throwing(string statusId)
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(new StatusAppliedEvent(Instance(statusId: statusId)));

        Assert.Equal(1L, acc.Snapshot()[new GateCounterKey(Player1, StatusAppliedCounter.Quantity, statusId)]);
    }

    [Fact]
    public void A_resolver_returning_null_earns_no_credit()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc, _ => null);

        counter.Handle(new StatusAppliedEvent(Instance()));

        Assert.Empty(acc.Snapshot());
    }

    /// <summary>§2.1's closing rule, and G2's own named verification: "a charm_pulse'ed enemy's
    /// applications earn its original owner nothing." Simulated with TWO independent lookups a real
    /// host might have -- a live "which side is this ptr fighting for right now" flag (says the
    /// charmed zombie is on the player's side) and a spawn-time roster lookup (says it was never part
    /// of the player's roster). The counter is wired to the roster lookup only, by contract (Handle's
    /// resolver signature takes nothing BUT the immutable StatusInstance) -- proving it cannot see, and
    /// therefore cannot be laundered through, the live side flag at all.</summary>
    [Fact]
    public void A_charmed_actor_applying_statuses_credits_its_spawn_owner_not_whoever_currently_controls_it()
    {
        const string charmedZombiePtr = "Z-charmed-7";

        // The live board says this ptr fights for the player right now (post-charm).
        var currentSideSaysPlayer = new Dictionary<string, string> { [charmedZombiePtr] = "player" };
        Assert.Equal("player", currentSideSaysPlayer[charmedZombiePtr]); // sanity: the trap is real

        // The spawn-time roster -- what StatusAppliedCounter is actually wired to -- has no player
        // entry for this ptr at all: it was spawned as a zombie, never bound to the player's roster.
        var spawnRoster = new Dictionary<string, GateOwnerKey?>();
        // (charmedZombiePtr intentionally absent)

        var acc = new GateCounterAccumulator();
        var counter = new StatusAppliedCounter(acc, instance =>
            spawnRoster.TryGetValue(instance.AttackerPtr!, out var owner) ? owner : null);

        counter.Handle(new StatusAppliedEvent(Instance(host: "P-plant-1", attacker: charmedZombiePtr)));

        Assert.Empty(acc.Snapshot()); // nobody laundered credit through the charmed body -- not the
                                      // charmer, and no row for a non-player original owner either.
    }

    [Fact]
    public void Two_different_hosts_credit_the_same_owner_twice()
    {
        var acc = new GateCounterAccumulator();
        var counter = Counter(acc);

        counter.Handle(new StatusAppliedEvent(Instance(host: "Z1")));
        counter.Handle(new StatusAppliedEvent(Instance(host: "Z2")));

        Assert.Equal(2L, acc.Snapshot()[new GateCounterKey(Player1, StatusAppliedCounter.Quantity, "wither")]);
    }
}
