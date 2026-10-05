using FusionRpg.Contracts;
using FusionRpg.Core.Match;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// lawn `LW5.1` (<c>creature-lawn-deploy/spec-unique-deploy-cap.md</c>) — the WIRING tests: the
/// owner ruling D6 ("Lawn deploy cap: at most 5 unique creatures per side, 10 on the board")
/// enforced at the one gate a player's deploy call reaches, <c>RpgStore.TryBeginUniqueDeploy</c>.
///
/// <para><b>Why this file exists beside <c>Core.Tests/Match/LawnUniqueDeployCapTests.cs</c>, which
/// already passes.</b> That file proves the POLICY is correct in isolation: two counts in, a
/// <c>GateResult</c> out, over seven cases. It cannot prove the policy is REACHED, and it does not
/// try — the cap it describes had a full green test suite and zero production callers for as long as
/// it existed, which is exactly what made the gap invisible. These tests are the only ones that can
/// go red when the wiring is removed; the contrast is asserted by mutating the wiring and running
/// both suites (see the report for the measured result).</para>
///
/// <para><b>Contract, never a reading.</b> A refusal is asserted as a <c>GateReasons</c> constant plus
/// the ROW's own state read back through <c>GetUniqueActor</c> — never from the returned tuple alone,
/// and never from an HTTP response body (<c>live-probe-standard.md</c>: a response body is never
/// proof). The shipped 5/10 pair is exercised by ONE test that loads the real file, because the
/// ruling is what the gate must enforce end to end; the axis tests supply their own limits, which is
/// what makes each axis separately observable.</para>
///
/// <para><b>Concurrency, not parity.</b> Multiple saves are multiple empires for this gate's key
/// (<c>(player_id, empire_id)</c>), so "two sides" is exercised as two real saves rather than as a
/// fabricated pair — an empire is a save plus an empire id
/// (<c>EmpireRef</c>: "Zomboss in save 1 and Zomboss in save 2 are two different owners, which is the
/// whole of ruling R3").</para>
/// </summary>
[Trait("VerificationId", "data.lawn-unique-deploy-cap")]
[Collection(LawnUniqueDeployLimitsTuningCollection.Name)]
public class LawnUniqueDeployCapWiringTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    /// <summary>The store's own default save (player 1).</summary>
    readonly long _saveOne;

    public LawnUniqueDeployCapWiringTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _saveOne = _store.GetCurrentPlayerId();
    }

    public void Dispose()
    {
        // The hub is process-wide: leave it as this class found it, or the next test class inherits a
        // cap it never asked for.
        LawnDeployLimitsTuningHub.Reset();
        _testStore.Dispose();
    }

    /// <summary>
    /// Installs explicit limits for ONE test. Axis isolation needs them: with the shipped 5/10 no
    /// single empire can ever be under its own limit while the board is full, so the board axis is
    /// unobservable at the ruling's own numbers.
    /// </summary>
    static void UseLimits(int perEmpire, int onBoard) =>
        LawnDeployLimitsTuningHub.Configure(
            new LawnDeployLimitsTuning(SchemaVersion: 1, Version: 1, Limits: new LawnDeployLimits(perEmpire, onBoard)));

    static void UseShippedLimits() =>
        LawnDeployLimitsTuningHub.Configure(LawnDeployLimitsTuningLoader.Parse(
            File.ReadAllText(FindTuning("lawn-deploy.v1.json"))));

    /// <summary>Two more SAVES, i.e. two more empires on this store — the concurrency case D6 names.</summary>
    long NewSave() => _store.CreateUnseededPlayerForTest("rival-" + Guid.NewGuid().ToString("N")[..8]).Id;

    /// <summary>A bare UniqueActor: no creature profile, so the contract gate is skipped and the cap
    /// is the only thing that can refuse it.</summary>
    string Bare(long save) => _store.CreateUniqueActor(save, "plant", 3).InstanceId;

    /// <summary>Deploys and asserts it was admitted, so a failure names the specimen, not the count.</summary>
    string Admit(long save, string instanceId, string matchKey, string tag)
    {
        var deploy = _store.TryBeginUniqueDeploy(instanceId, "corr-" + tag, matchKey);
        Assert.True(deploy.Ok, $"{tag}: {deploy.Reason}");
        return deploy.Reason;
    }

    // ---------------------------------------------------------------------------------------
    // The ruling itself, end to end through the real shipped file.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE headline test. With the shipped <c>lawn-deploy.v1.json</c> configured, the sixth unique of
    /// one empire is refused by name — and the row is left exactly where a refusal must leave it,
    /// which is what distinguishes a refusal from a rollback.
    /// </summary>
    [Fact]
    public void The_sixth_unique_of_one_empire_is_refused_by_name_and_its_row_is_untouched()
    {
        UseShippedLimits();

        for (var i = 0; i < 5; i++) Admit(_saveOne, Bare(_saveOne), "m-cap", "live-" + i);

        var subject = Bare(_saveOne);
        var before = _store.GetUniqueActor(subject)!;
        var refused = _store.TryBeginUniqueDeploy(subject, "corr-sixth", "m-cap");

        Assert.False(refused.Ok);
        Assert.Equal(GateReasons.CapUniquePerEmpire, refused.Reason);
        Assert.Equal("cap.unique_per_empire", refused.Reason); // the wire string, pinned

        // Read the row back through the normal reader: a refusal changes NOTHING.
        var after = _store.GetUniqueActor(subject)!;
        Assert.Equal(UniqueActorPhases.Roster, after.Phase);
        Assert.Null(after.MatchKey);
        Assert.Null(after.DeployCorrelationId);
        Assert.Null(after.LastPtr);
        Assert.Equal(before.Revision, after.Revision);
        Assert.False(refused.Queued);
    }

    /// <summary>
    /// The reservation property: a row that has been queued to the Injector but not yet acked is a
    /// specimen that is ABOUT TO EXIST. Excluding <c>Deploying</c> would let a burst of concurrent
    /// calls overshoot the cap by the whole in-flight window.
    /// </summary>
    [Fact]
    public void An_unacked_Deploying_reservation_occupies_a_slot()
    {
        UseShippedLimits();

        // Deliberately NOT acked: all five stay in the Deploying reservation.
        for (var i = 0; i < 5; i++) Admit(_saveOne, Bare(_saveOne), "m-cap", "res-" + i);

        var subject = Bare(_saveOne);
        var refused = _store.TryBeginUniqueDeploy(subject, "corr-res-sixth", "m-cap");

        Assert.False(refused.Ok);
        Assert.Equal(GateReasons.CapUniquePerEmpire, refused.Reason);
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(subject)!.Phase);
    }

    /// <summary>
    /// The ordering property, which is why the cap sits AFTER the idempotent re-entry branch: a retry
    /// of an in-flight deploy is refused by its OWN reservation if the cap is asked first. Getting this
    /// wrong produces a bug that only appears under retry, so it is its own named test.
    /// </summary>
    [Fact]
    public void A_retry_of_an_in_flight_deploy_still_succeeds_at_the_cap()
    {
        UseShippedLimits();

        for (var i = 0; i < 4; i++) Admit(_saveOne, Bare(_saveOne), "m-cap", "retry-" + i);
        var subject = Bare(_saveOne);
        var first = _store.TryBeginUniqueDeploy(subject, "corr-retry", "m-cap");
        Assert.True(first.Ok, first.Reason);
        Assert.True(first.Queued);

        // The store now holds FIVE live uniques, which is exactly the shipped limit — the retry is
        // refused if and only if the cap runs before the re-entry branch.
        var retry = _store.TryBeginUniqueDeploy(subject, "corr-retry", "m-cap");

        Assert.True(retry.Ok, retry.Reason);
        Assert.False(retry.Queued);
        Assert.Equal(UniqueActorPhases.Deploying, retry.Actor!.Phase);
    }

    /// <summary>
    /// D6 is about CONCURRENCY per side, so two empires keep two tallies: five each is ten on the
    /// board, which is the shipped board limit exactly, and both are admitted.
    /// </summary>
    [Fact]
    public void Two_empires_each_hold_their_own_five_and_neither_is_refused()
    {
        UseShippedLimits();
        var other = NewSave();

        for (var i = 0; i < 5; i++) Admit(_saveOne, Bare(_saveOne), "m-cap", "mine-" + i);
        for (var i = 0; i < 5; i++) Admit(other, Bare(other), "m-cap", "theirs-" + i);

        // Six for either side: named by the EMPIRE, never by the board — the stated precedence.
        var mine = _store.TryBeginUniqueDeploy(Bare(_saveOne), "corr-mine-6", "m-cap");
        var theirs = _store.TryBeginUniqueDeploy(Bare(other), "corr-theirs-6", "m-cap");

        Assert.False(mine.Ok);
        Assert.Equal(GateReasons.CapUniquePerEmpire, mine.Reason);
        Assert.False(theirs.Ok);
        Assert.Equal(GateReasons.CapUniquePerEmpire, theirs.Reason);
    }

    /// <summary>A run that ends releases its slots, so the next deploy in a new run is admitted.</summary>
    [Fact]
    public void Ending_the_run_frees_the_slots_and_the_next_run_deploys()
    {
        UseShippedLimits();

        for (var i = 0; i < 5; i++)
        {
            var id = Bare(_saveOne);
            Admit(_saveOne, id, "m-cap", "ack-" + i);
            Assert.True(_store.TryAckUniqueSpawn("corr-ack-" + i, "ptr-" + i, "m-cap").Ok);
        }

        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("board.end", "m-cap", "{}")
        });

        var after = Bare(_saveOne);
        var deploy = _store.TryBeginUniqueDeploy(after, "corr-next-run", "m-cap-next");

        Assert.True(deploy.Ok, deploy.Reason);
        Assert.Equal(UniqueActorPhases.Deploying, _store.GetUniqueActor(after)!.Phase);
    }

    // ---------------------------------------------------------------------------------------
    // The board axis, isolated from the per-empire axis.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The board limit is the frame-budget backstop, and it is a DIFFERENT axis from the per-empire
    /// one: three empires each hold one, every empire is comfortably under its own limit, and the
    /// fourth unique is refused by the BOARD. Stated separately so the two refusals cannot be
    /// mistaken for one rule with one number.
    /// </summary>
    [Fact]
    public void The_board_limit_refuses_an_empire_that_is_still_under_its_own_limit()
    {
        UseLimits(perEmpire: 2, onBoard: 3);
        var second = NewSave();
        var third = NewSave();

        Admit(_saveOne, Bare(_saveOne), "m-x", "b1");
        Admit(second, Bare(second), "m-x", "b2");
        Admit(third, Bare(third), "m-x", "b3");

        var subject = Bare(_saveOne);
        var refused = _store.TryBeginUniqueDeploy(subject, "corr-board", "m-x");

        Assert.False(refused.Ok);
        Assert.Equal(GateReasons.CapUniqueBoard, refused.Reason);
        Assert.Equal("cap.unique_board", refused.Reason); // the wire string, pinned
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(subject)!.Phase);
    }

    /// <summary>"On the board" is scoped to ONE board: a second run's board starts empty, so the same
    /// empire that cannot add to a full board is admitted on a fresh one.</summary>
    [Fact]
    public void Two_boards_do_not_count_against_each_other()
    {
        UseLimits(perEmpire: 2, onBoard: 3);
        var second = NewSave();
        var third = NewSave();

        Admit(_saveOne, Bare(_saveOne), "m-a", "a1");
        Admit(second, Bare(second), "m-a", "a2");
        Admit(third, Bare(third), "m-a", "a3");

        var subject = Bare(_saveOne);
        var deploy = _store.TryBeginUniqueDeploy(subject, "corr-board-b", "m-b");

        Assert.True(deploy.Ok, deploy.Reason);
        Assert.Equal("m-b", _store.GetUniqueActor(subject)!.MatchKey);
    }

    /// <summary>
    /// The missing-<c>matchKey</c> case, which is a shape real callers produce rather than a defect:
    /// the browser control room sends <c>matchKey: model.matchKey ?? undefined</c>. The board axis is
    /// then unevaluable by name, and the chosen answer is that the count WIDENS to every live unique
    /// — strictly the largest board that can exist — so a caller that omits the key gets a stricter
    /// board, never an unenforced one. Three omitted-key deploys across three empires fill the
    /// widened board of 3, and the fourth is refused by it.
    /// </summary>
    [Fact]
    public void A_deploy_with_no_matchKey_meets_the_widened_board_count_rather_than_skipping_it()
    {
        UseLimits(perEmpire: 2, onBoard: 3);
        var second = NewSave();
        var third = NewSave();

        Admit(_saveOne, Bare(_saveOne), null!, "n1");
        Admit(second, Bare(second), null!, "n2");
        Admit(third, Bare(third), null!, "n3");

        var subject = Bare(_saveOne);
        var refused = _store.TryBeginUniqueDeploy(subject, "corr-nokey-4", null);

        Assert.False(refused.Ok);
        Assert.Equal(GateReasons.CapUniqueBoard, refused.Reason);
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(subject)!.Phase);
    }

    // ---------------------------------------------------------------------------------------
    // The unconfigured case — the decision, and its price, stated in a test.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The unconfigured hub ADMITS, and this test exists so that choice cannot be made silently
    /// again.</b> The server's composition root configures the hub from
    /// <c>data/tuning/lawn-deploy.v1.json</c> and the loader THROWS on a missing or invalid file, so a
    /// running server never reaches this branch. In the state where something else did, the cap
    /// admits: refusing would fail every unique deploy in the game for a config problem and name a cap
    /// reason that says nothing about the real cause. Admitting loses frame protection until the host
    /// is configured; refusing loses the feature.
    /// </summary>
    [Fact]
    public void An_unconfigured_hub_admits_instead_of_refusing_every_deploy_in_the_game()
    {
        LawnDeployLimitsTuningHub.Reset();
        Assert.False(LawnDeployLimitsTuningHub.IsConfigured);

        // Well past the ruling's own numbers, on one board, one empire.
        for (var i = 0; i < 12; i++) Admit(_saveOne, Bare(_saveOne), "m-unset", "u-" + i);

        var subject = Bare(_saveOne);
        var deploy = _store.TryBeginUniqueDeploy(subject, "corr-unset", "m-unset");

        Assert.True(deploy.Ok, deploy.Reason);
        Assert.Equal(UniqueActorPhases.Deploying, _store.GetUniqueActor(subject)!.Phase);
    }

    // ---------------------------------------------------------------------------------------

    static string FindTuning(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"no data/tuning/{fileName} above the test output");
    }
}
