using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// The receiver's state machine (E19), extracted to Core so it can be tested at all — the injector
/// host needs the game's interop assemblies, so nothing that stays there can be checked.
///
/// <para>What these pin is the behaviour that is easy to get quietly wrong: an up-to-date reply must
/// keep what is held rather than drop it, a new match must re-seed rather than reuse the last one's
/// dice, and <c>board.end</c> must forget the revision or the next Hello will claim to hold bindings
/// it has already dropped.</para>
/// </summary>
public class AtomPushInstallerTests
{
    long _now;
    int _dispatched;

    AtomPushInstaller New(Func<EffectGrantDto, bool>? dispatch = null) =>
        new(() => _now, dispatch ?? (_ => { _dispatched++; return true; }));

    static RunnerEntry Entry(string atomId = "atom.searing.t1", int cap = -1) =>
        new(atomId, "resource.delta", AtomTriggers.OnDamageDealt,
            PredicateCompiler.Always, 1000, 0, atomId,
            new Dictionary<string, ValueBounds> { ["amount"] = new(-120, -80, RollPolicy.OnApply) },
            new RunnerLimits(cap, -1, -1, -1),
            new Dictionary<string, object?>());

    static AtomPushDto Push(long revision, params RunnerBinding[] bindings)
    {
        var dto = new AtomPushDto { CatalogRevision = revision, ContentHash = "v1|abc|effect_atom=dead" };
        foreach (var b in bindings) dto.RunnerBindings.Add(AtomPushCodec.Encode(b));
        return dto;
    }

    static RunnerBinding Bind(string id, string atomId = "atom.searing.t1", int priority = 0, int cap = -1) =>
        new(id, priority, "player:1", Entry(atomId, cap));

    static RunnerEvent Hit() => new(
        TriggerIndex.Ordinal(AtomTriggers.OnDamageDealt), "0xA", "0xB",
        new EntityFacts(0, 1, 1000, -1, 0, 0, false, false, 0),
        new EntityFacts(1, 2, 1000, -1, 0, 1, false, false, 0));

    // ---- install ------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_held_before_the_first_push()
    {
        var i = New();

        Assert.Null(i.Runner);
        Assert.Equal(-1, i.CatalogRevision);
        Assert.Equal(0, i.BindingCount);
    }

    [Fact]
    public void A_push_installs_its_bindings_and_revision()
    {
        var i = New();

        Assert.Equal(2, i.Install(Push(4, Bind("b1"), Bind("b2", "atom.other.t1"))));

        Assert.Equal(4, i.CatalogRevision);
        Assert.Equal(2, i.BindingCount);
        Assert.Equal("v1|abc|effect_atom=dead", i.ContentHash);
    }

    [Fact]
    public void An_up_to_date_reply_keeps_what_is_already_held()
    {
        // The entire point of the revision negotiation. Treating an empty payload as "install
        // nothing" rather than "keep everything" would disarm the injector on every reconnect.
        var i = New();
        i.Install(Push(4, Bind("b1")));
        i.BeginMatch("m1");
        var runner = i.Runner;

        i.Install(new AtomPushDto { CatalogRevision = 4, UpToDate = true, ContentHash = "v1|zzz|x=1" });

        Assert.Equal(1, i.BindingCount);
        Assert.Same(runner, i.Runner);
        Assert.Equal(4, i.CatalogRevision);
        Assert.Equal("v1|zzz|x=1", i.ContentHash); // still recorded — a mismatch must stay visible
    }

    [Fact]
    public void A_later_push_replaces_the_set_rather_than_adding_to_it()
    {
        // Full set, never a delta.
        var i = New();
        i.Install(Push(4, Bind("b1"), Bind("b2", "atom.other.t1")));

        i.Install(Push(5, Bind("b3", "atom.third.t1")));

        Assert.Equal(1, i.BindingCount);
        Assert.Equal(5, i.CatalogRevision);
    }

    [Fact]
    public void A_push_with_no_bindings_leaves_no_runner()
    {
        // A match with no pushed bindings runs with none. Not an error.
        var i = New();

        Assert.Equal(0, i.Install(Push(4)));
        Assert.Null(i.Runner);
    }

    // ---- the seed -----------------------------------------------------------------------------------

    [Fact]
    public void A_match_start_seeds_the_runner_from_the_match_key()
    {
        var i = New();
        i.Install(Push(4, Bind("b1")));

        i.BeginMatch("m-123");

        Assert.NotNull(i.Runner);
        Assert.Equal("m-123", i.Runner!.State.MatchKey);
    }

    [Fact]
    public void A_new_match_rebuilds_the_runner_so_counters_and_dice_both_reset()
    {
        var i = New();
        i.Install(Push(4, Bind("b1", cap: 2)));
        i.BeginMatch("m1");
        var first = i.Runner!;

        for (var n = 0; n < 5; n++) first.OnEvent(Hit());
        Assert.Equal(2, first.State.DispatchesThisMatch(0));

        i.BeginMatch("m2");

        Assert.NotSame(first, i.Runner);
        Assert.Equal(0, i.Runner!.State.DispatchesThisMatch(0));
        Assert.Equal("m2", i.Runner.State.MatchKey);
    }

    [Fact]
    public void Two_match_keys_that_differ_produce_different_seeds()
    {
        Assert.NotEqual(MatchSeed.For("m-1"), MatchSeed.For("m-2"));
        Assert.Equal(MatchSeed.For("m-1"), MatchSeed.For("m-1"));
        Assert.Equal(0UL, MatchSeed.For(null));
    }

    [Fact]
    public void The_seed_is_a_named_hash_not_the_randomised_runtime_one()
    {
        // String.GetHashCode is randomised per process: "same match key, same rolls" would be false
        // after every restart. FNV-1a of "m-123", computed independently here.
        var expected = 14695981039346656037UL;
        foreach (var ch in "m-123")
        {
            expected ^= ch;
            expected *= 1099511628211UL;
        }

        Assert.Equal(expected, MatchSeed.For("m-123"));
    }

    // ---- dispatch and clear --------------------------------------------------------------------------

    [Fact]
    public void A_proc_reaches_the_host_supplied_sink()
    {
        var i = New();
        i.Install(Push(4, Bind("b1")));
        i.BeginMatch("m1");

        i.Runner!.OnEvent(Hit());

        Assert.Equal(1, _dispatched);
    }

    [Fact]
    public void Board_end_drops_the_revision_as_well_as_the_bindings()
    {
        // If the revision survived, the next Hello would claim to hold bindings that were just
        // dropped — and the server would answer "up to date" with nothing to install.
        var i = New();
        i.Install(Push(4, Bind("b1")));
        i.BeginMatch("m1");

        i.Clear();

        Assert.Null(i.Runner);
        Assert.Equal(0, i.BindingCount);
        Assert.Equal(-1, i.CatalogRevision);
        Assert.Null(i.Hello().ContentHash);
        Assert.Equal(-1, i.Hello().CatalogRevision);
    }

    [Fact]
    public void Hello_reports_what_is_held_so_the_server_can_skip_a_resend()
    {
        var i = New();
        i.Install(Push(9, Bind("b1")));

        var hello = i.Hello();

        Assert.Equal(9, hello.CatalogRevision);
        Assert.Equal("v1|abc|effect_atom=dead", hello.ContentHash);
    }

    [Fact]
    public void A_mid_match_push_re_arms_without_waiting_for_the_next_match()
    {
        // A reconnect during a match must not leave the player unarmed until board.start.
        var i = New();
        i.Install(Push(4, Bind("b1")));
        i.BeginMatch("m1");

        i.Install(Push(5, Bind("b2", "atom.other.t1")));

        Assert.NotNull(i.Runner);
        Assert.Equal("m1", i.Runner!.State.MatchKey);
        Assert.Equal(1, i.Runner.Index.Count);
    }
}
