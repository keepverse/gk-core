using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `respec-free-counter` EP4.5 (module 3, `empire-progression` Wave D; spec:
/// `docs/architecture/empire-progression/spec-respec-free-counter.md`, ruling R18). The earned stock of
/// free empire respecs: an empire level grants rows into `rpg_empire_free_respec_ledger`, the stock is
/// their sum, and the ledger is keyed for EVERY empire — empire-progression **X13** in
/// `solid-enforcement/spec-save-identity.md`'s consumer table, which already records this exception to
/// Tier B's human-only rule.
///
/// <para><c>DiskSemantics</c>: <c>The_stock_survives_a_compaction_run_on_a_file_backed_store</c> drives
/// the archive passes <c>CompactAfterRunClosed</c>/<c>TrimHotTailsNow</c>, which refuse the memory plan,
/// on a <c>DataTestStore.CreateFileBacked()</c> store (docs/contributing/testing-standard.md R2), so the
/// default profile skips the class and it runs at <c>full</c>/nightly/gate.</para>
/// </summary>
[Trait("Category", "DiskSemantics")]
public class EmpireFreeRespecTests : IDisposable
{
    const int FumeshroomGameTypeId = 7;
    const int FumeshroomCreatureTypeId = 60007;

    /// <summary>See `EmpireLevelTests`: `awards.speciesLevelUp` has no other reader in the tree, so
    /// raising it from the shipped 1 to 25 lets ONE credit cross two shipped `xpCurve.empire` steps
    /// (10, then 15) without changing a number another class observes.</summary>
    const long TestSpeciesLevelUpAward = 25;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public EmpireFreeRespecTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression with
        {
            Awards = ContractTuningTestBootstrap.DefaultProgression.Awards with
            {
                SpeciesLevelUp = TestSpeciesLevelUpAward,
            },
        });
    }

    public void Dispose()
    {
        ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression);
        EmpireLevelTuningHub.Configure(new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 1));
        _testStore.Dispose();
    }

    long StartRun(long playerId)
    {
        _store.SetCurrentPlayer(playerId);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            MatchKey = "m-" + Guid.NewGuid().ToString("N"),
            Payload = new { levelName = "test", levelType = "adventure" },
        });
        return _store.ListRuns(playerId).OrderByDescending(r => r.Id).First().Id;
    }

    (long PlayerId, long RunId) Seed(string name)
    {
        var player = _store.CreatePlayer(name);
        return (player.Id, StartRun(player.Id));
    }

    void Append(long playerId, long runId, string kind, string sourceKind, string sourceId,
        string payload, string dedupe) =>
        _store.AppendPvzActivityFact(playerId, new PvzActivityAppendRequest
        {
            Kind = kind, RunId = runId, SourceKind = sourceKind, SourceId = sourceId,
            PayloadJson = payload, DedupeKey = dedupe,
        });

    void PlacePlant(long playerId, long runId, string dedupe) =>
        Append(playerId, runId, PvzActivityKinds.PlantPlaced, "creature.progression.v1",
            "general:fumeshroom", $$"""{"type":{{FumeshroomGameTypeId}}}""", dedupe);

    void EndRun(long playerId, long runId, string dedupe) =>
        Append(playerId, runId, PvzActivityKinds.MatchEnded, "feature", "manual",
            """{"result":"victory"}""", dedupe);

    /// <summary>One resolved match: a per-placement award plus the dominant run-completion one, which
    /// crosses a species level and therefore credits the empire.</summary>
    void PlayAMatch(long playerId, string tag)
    {
        var runId = StartRun(playerId);
        PlacePlant(playerId, runId, $"{tag}-place");
        EndRun(playerId, runId, $"{tag}-end");
    }

    EmpireRef Human(long playerId) => new(new SaveId(playerId), _store.HumanEmpireOf(playerId));

    EmpireRef Zomboss(long playerId) => new(new SaveId(playerId), EmpireId.Zomboss);

    List<(long Level, long Delta, string Reason)> Ledger(long playerId, EmpireRef owner)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT level, delta, reason FROM rpg_empire_free_respec_ledger
            WHERE save_id = $s AND empire_id = $e ORDER BY level;
            """;
        cmd.Parameters.AddWithValue("$s", owner.Save.Value);
        cmd.Parameters.AddWithValue("$e", owner.Empire.Value);
        var rows = new List<(long, long, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
        return rows;
    }

    static RpgStore.EmpireLevelUpEvent Crossing(long playerId, string empireId, long level, long amount) =>
        new(playerId, empireId, level - 1, level,
            new[] { new EmpireLevelGrant(EmpireLevelGrantKind.FreeEmpireRespec, amount) });

    [Fact]
    public void Crossing_empire_levels_writes_one_grant_per_level_keyed_by_it()
    {
        var (playerId, _) = Seed("FreeRespecGrant");
        Assert.Equal(1, EmpireLevelTuningHub.Tuning.FreeRespecsPerEmpireLevel);   // the shipped working value

        PlayAMatch(playerId, "match-0");

        var empire = _store.GetRpgActor(playerId, RpgActorKinds.Empire, 0)!;
        Assert.True(empire.Level >= 3, $"one credit should cross two levels, landed on {empire.Level}");

        var rows = Ledger(playerId, Human(playerId));
        Assert.Equal(2, rows.Count);
        // Keyed by the level the grant is FOR — `L{n}` in the spec's own words, which is also the
        // primary key's third column.
        Assert.Equal(new long[] { 2, 3 }, rows.Select(r => r.Level).ToArray());
        Assert.All(rows, r => Assert.Equal(1, r.Delta));
        Assert.All(rows, r => Assert.Equal(RpgStore.FreeRespecReasonEmpireLevel, r.Reason));
        Assert.Equal(2, _store.FreeRespecStock(Human(playerId)));
    }

    [Fact]
    public void A_replayed_level_up_adds_nothing()
    {
        var (playerId, _) = Seed("FreeRespecReplay");
        PlayAMatch(playerId, "match-0");
        var after = Ledger(playerId, Human(playerId)).Count;
        Assert.Equal(2, after);

        // (a) the SAME crossing applied again — the primary key's own idempotence.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            _store.ApplyEmpireLevelGrantsUnlocked(db, Human(playerId),
                new[] { Crossing(playerId, _store.HumanEmpireOf(playerId).Value, 2, 1) },
                DateTime.UtcNow.ToString("o"));
        }
        Assert.Equal(after, Ledger(playerId, Human(playerId)).Count);

        // (b) a replayed FACT, which is how a real replay arrives.
        var runId = _store.ListRuns(playerId).OrderByDescending(r => r.Id).First().Id;
        EndRun(playerId, runId, "match-0-end");
        Assert.Equal(after, Ledger(playerId, Human(playerId)).Count);
        Assert.Equal(2, _store.FreeRespecStock(Human(playerId)));
    }

    [Fact]
    public void A_published_zero_writes_nothing()
    {
        var (playerId, _) = Seed("FreeRespecZero");
        try
        {
            EmpireLevelTuningHub.Configure(new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 0));
            PlayAMatch(playerId, "match-0");

            // The LEVEL still moves (it is a level, not a payout) and the stock stays empty.
            Assert.True(_store.GetRpgActor(playerId, RpgActorKinds.Empire, 0)!.Level >= 3);
            Assert.Empty(Ledger(playerId, Human(playerId)));
            Assert.Equal(0, _store.FreeRespecStock(Human(playerId)));
        }
        finally
        {
            EmpireLevelTuningHub.Configure(new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 1));
        }
    }

    [Fact]
    public void The_stock_is_the_sum_of_the_ledger_and_a_negative_sum_throws()
    {
        var (playerId, _) = Seed("FreeRespecSum");
        PlayAMatch(playerId, "match-0");
        var rows = Ledger(playerId, Human(playerId));
        Assert.Equal(rows.Sum(r => r.Delta), _store.FreeRespecStock(Human(playerId)));

        // A spend is a NEGATIVE delta (EP4.9 writes them); an over-spend leaves a negative sum, which is
        // a corrupt ledger rather than a balance to clamp to 0.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var overspend = db.CreateCommand();
            overspend.CommandText = """
                INSERT INTO rpg_empire_free_respec_ledger(save_id, empire_id, level, delta, reason, granted_utc)
                VALUES ($s, $e, -1, -99, 'species-respec', $t);
                """;
            overspend.Parameters.AddWithValue("$s", playerId);
            overspend.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
            overspend.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            overspend.ExecuteNonQuery();
        }

        var thrown = Assert.Throws<InvalidOperationException>(() => _store.FreeRespecStock(Human(playerId)));
        Assert.Contains("never taken back", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// spec test 10, the accrual half: the stock is keyed for EVERY empire, so Zomboss's accrues under
    /// his own `EmpireRef` and the human's does not move. Driven through the applier directly because the
    /// R1 side rule (EP4.3) keeps today's zombie species XP on the human's empire, so nothing credits
    /// Zomboss's row until `ai-empire-species` routes it — the KEYING is what this proves, and it is the
    /// half X13 records.
    /// </summary>
    [Fact]
    public void Zomboss_accrues_under_his_own_empire_ref_and_the_human_does_not_move()
    {
        var (playerId, _) = Seed("FreeRespecZomboss");
        PlayAMatch(playerId, "match-0");
        var humanStock = _store.FreeRespecStock(Human(playerId));
        Assert.True(humanStock > 0);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            _store.ApplyEmpireLevelGrantsUnlocked(db, Zomboss(playerId),
                new[] { Crossing(playerId, EmpireId.Zomboss.Value, 2, 1), Crossing(playerId, EmpireId.Zomboss.Value, 3, 1) },
                DateTime.UtcNow.ToString("o"));
        }

        Assert.Equal(2, _store.FreeRespecStock(Zomboss(playerId)));
        Assert.Equal(humanStock, _store.FreeRespecStock(Human(playerId)));
        Assert.Equal(new long[] { 2, 3 }, Ledger(playerId, Zomboss(playerId)).Select(r => r.Level).ToArray());
    }

    // ---- EP4.9: the payWith choice -----------------------------------------------------------------

    /// <summary>The respec price/decay surface, read from the SHIPPED document so this class cannot
    /// disagree with `SpeciesRespecTests` about the working set — both configure the same process-wide
    /// hub, so a deviation here would race that suite (a lesson from EP4.3's own session).</summary>
    void ConfigureShippedSpeciesBuild() =>
        FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningHub.Configure(
            FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningLoader.Parse(File.ReadAllText(
                Path.Combine(FindRepoRoot(), "data", "tuning", "species-build.v6.json"))));

    const string Species = "fumeshroom";

    static AptitudeAllocation Override() =>
        AptitudeAllocation.Single(AllocationScope.CreatureType, AptitudeCatalog.All[0].Id, 1);

    /// <summary>Puts <paramref name="count"/> free respecs in an empire's stock through the same
    /// applier an empire level uses.
    ///
    /// <para><paramref name="priced"/> also TOUCHES the species first, because a FIRST override is free by
    /// the pre-existing rule (spec-species-respec.md: the first override is not churn) and therefore never
    /// reaches the priced path this suite is about. A test that means "the free action" passes
    /// <c>priced: false</c>; every other test passes the default, so the change it then makes is a priced
    /// one and the choice applies.</para></summary>
    void GiveFreeRespecStock(long playerId, int count, EmpireRef? owner = null, bool priced = true)
    {
        // Touch first, while the species is still untouched: this override is the free one.
        if (priced)
        {
            var touch = _store.TryRespecSpecies(playerId, Species, Override(), $"touch-{Guid.NewGuid():N}");
            Assert.True(touch.Ok, touch.Reason);
            Assert.Equal("", touch.PaidWith);   // the first override really was free
        }

        var to = owner ?? Human(playerId);
        var events = Enumerable.Range(0, count)
            .Select(i => Crossing(playerId, to.Empire.Value, i + 2, 1)).ToArray();
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        _store.ApplyEmpireLevelGrantsUnlocked(db, to, events, DateTime.UtcNow.ToString("o"));
    }

    long SpendRows(long playerId) =>
        Ledger(playerId, Human(playerId)).Count(r => r.Reason == RpgStore.FreeRespecReasonSpeciesRespec);

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void With_stock_and_no_choice_the_spend_refuses_carrying_the_quote_and_writes_nothing()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PayChoice");
        _store.AwardSouls(playerId, 1000, "seed", "bank-choice");
        GiveFreeRespecStock(playerId, 1);
        var stockBefore = _store.FreeRespecStock(Human(playerId));
        var balanceBefore = _store.GetSoulBalance(playerId).Balance;

        var outcome = _store.TryRespecSpecies(playerId, Species, Override(), "corr-choice");

        Assert.False(outcome.Ok);
        Assert.Equal("respec.payment.choice-required", outcome.Reason);
        Assert.True(outcome.Priced);
        Assert.Equal(_store.QuoteSpeciesRespec(playerId, Species).Souls.Amount, outcome.PriceAmount);
        Assert.True(outcome.FreeStock > 0);
        Assert.Equal(stockBefore, _store.FreeRespecStock(Human(playerId)));       // no stock spent
        Assert.Equal(balanceBefore, _store.GetSoulBalance(playerId).Balance);    // and no soul charged
        Assert.Equal(0, _store.GetSpeciesRespecCount(playerId, Species));        // and no churn
    }

    [Fact]
    public void A_free_respec_spends_one_and_leaves_the_churn_counter_alone()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PayFree");
        GiveFreeRespecStock(playerId, 1);
        var balanceBefore = _store.GetSoulBalance(playerId).Balance;

        var outcome = _store.TryRespecSpecies(playerId, Species, Override(), "corr-free",
            payWith: RespecPayment.FreeRespec);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(RespecPayments.FreeRespec, outcome.PaidWith);
        Assert.False(outcome.Priced);
        Assert.Equal(0, outcome.FreeStock);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(playerId).Balance);     // the stock paid, not souls
        Assert.Equal(0, _store.GetSpeciesRespecCount(playerId, Species));        // the free stock is the payment
        Assert.Equal(1, SpendRows(playerId));
    }

    [Fact]
    public void A_souls_spend_charges_the_price_and_leaves_the_stock_alone()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PaySouls");
        _store.AwardSouls(playerId, 1000, "seed", "bank-souls");
        GiveFreeRespecStock(playerId, 1);
        var stockBefore = _store.FreeRespecStock(Human(playerId));
        var price = _store.QuoteSpeciesRespec(playerId, Species).Souls.Amount;

        var outcome = _store.TryRespecSpecies(playerId, Species, Override(), "corr-souls",
            payWith: RespecPayment.Souls);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(RespecPayments.Souls, outcome.PaidWith);
        Assert.Equal(price, outcome.PriceAmount);
        Assert.Equal(1000 - price, _store.GetSoulBalance(playerId).Balance);
        Assert.Equal(stockBefore, _store.FreeRespecStock(Human(playerId)));      // souls path, stock untouched
        Assert.Equal(1, _store.GetSpeciesRespecCount(playerId, Species));
        Assert.Equal(0, SpendRows(playerId));
    }

    [Fact]
    public void With_no_stock_a_free_respec_refuses_and_an_omitted_choice_charges_souls_as_before()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PayNoStock");
        _store.AwardSouls(playerId, 1000, "seed", "bank-none");
        // Touch the species first: the FIRST override is free by the pre-existing rule, and this test is
        // about the priced change after it.
        GiveFreeRespecStock(playerId, 0);

        var refused = _store.TryRespecSpecies(playerId, Species, Override(), "corr-none",
            payWith: RespecPayment.FreeRespec);
        Assert.False(refused.Ok);
        Assert.Equal("respec.free.none", refused.Reason);
        Assert.Equal(0, refused.FreeStock);

        // An omitted choice with no stock is exactly the pre-EP4.9 behaviour: charged in souls.
        var charged = _store.TryRespecSpecies(playerId, Species, Override(), "corr-none");
        Assert.True(charged.Ok, charged.Reason);
        Assert.Equal(RespecPayments.Souls, charged.PaidWith);
        Assert.Equal(1000 - charged.PriceAmount, _store.GetSoulBalance(playerId).Balance);
    }

    [Fact]
    public void A_first_override_and_a_revert_ask_no_choice_and_move_neither_stock_nor_counter()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PayFreeAction");
        GiveFreeRespecStock(playerId, 1, priced: false);   // the species stays UNTOUCHED: that is the point
        var stockBefore = _store.FreeRespecStock(Human(playerId));

        var first = _store.TryRespecSpecies(playerId, Species, Override(), "corr-first");
        Assert.True(first.Ok, first.Reason);
        Assert.Equal("", first.PaidWith);                                        // nothing was charged

        var revert = _store.TryRespecSpecies(playerId, Species, AptitudeAllocation.Empty, "corr-revert");
        Assert.True(revert.Ok, revert.Reason);
        Assert.Equal("", revert.PaidWith);

        Assert.Equal(stockBefore, _store.FreeRespecStock(Human(playerId)));
        Assert.Equal(0, SpendRows(playerId));
        Assert.Equal(0, _store.GetSpeciesRespecCount(playerId, Species));
    }

    [Fact]
    public void A_replay_returns_the_original_payment_on_either_ledger()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PayReplay");
        _store.AwardSouls(playerId, 10_000, "seed", "bank-replay");
        GiveFreeRespecStock(playerId, 2);

        var paid = _store.TryRespecSpecies(playerId, Species, Override(), "corr-f", payWith: RespecPayment.FreeRespec);
        Assert.True(paid.Ok, paid.Reason);
        var stockAfterPaid = _store.FreeRespecStock(Human(playerId));
        var replayFree = _store.TryRespecSpecies(playerId, Species, Override(), "corr-f", payWith: RespecPayment.FreeRespec);
        Assert.True(replayFree.Ok, replayFree.Reason);
        Assert.Equal("replay", replayFree.Reason);
        Assert.Equal(RespecPayments.FreeRespec, replayFree.PaidWith);
        Assert.Equal(stockAfterPaid, _store.FreeRespecStock(Human(playerId)));    // never spent twice
        Assert.Equal(1, SpendRows(playerId));

        var souls = _store.TryRespecSpecies(playerId, Species, Override(), "corr-s", payWith: RespecPayment.Souls);
        Assert.True(souls.Ok, souls.Reason);
        var balanceAfter = _store.GetSoulBalance(playerId).Balance;
        var replaySouls = _store.TryRespecSpecies(playerId, Species, Override(), "corr-s", payWith: RespecPayment.Souls);
        Assert.True(replaySouls.Ok, replaySouls.Reason);
        Assert.Equal("replay", replaySouls.Reason);
        Assert.Equal(RespecPayments.Souls, replaySouls.PaidWith);
        Assert.Equal(balanceAfter, _store.GetSoulBalance(playerId).Balance);
    }

    [Fact]
    public void The_preview_equals_the_spend_for_both_stocks_at_counts_zero_to_three()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PayPreview");
        _store.AwardSouls(playerId, 100_000, "seed", "bank-preview");
        // One touch outside the loop, so every change the loop makes is a PRICED one (the first override
        // is free and would otherwise make the first comparison vacuous).
        GiveFreeRespecStock(playerId, 0);

        foreach (var withStock in new[] { false, true })
        {
            if (withStock) GiveFreeRespecStock(playerId, 4, priced: false);   // stock, without a second touch
            for (var count = 0; count < 4; count++)
            {
                var quote = _store.QuoteSpeciesRespec(playerId, Species);
                Assert.Equal(RespecPolicy.PriceOf(
                        FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningHub.Tuning.SpeciesRespec,
                        _store.GetSpeciesRespecCount(playerId, Species)), quote.Souls);
                Assert.Equal(withStock, quote.FreeAvailable);

                // The spend reports the price the preview just quoted, on whichever path it took.
                var outcome = _store.TryRespecSpecies(playerId, Species, Override(), $"corr-p-{withStock}-{count}",
                    payWith: withStock ? RespecPayment.FreeRespec : RespecPayment.Souls);
                Assert.True(outcome.Ok, outcome.Reason);
                if (!withStock) Assert.Equal(quote.Souls.Amount, outcome.PriceAmount);
            }
        }
    }

    [Fact]
    public void A_human_free_respec_never_touches_zomboss_stock()
    {
        ConfigureShippedSpeciesBuild();
        var (playerId, _) = Seed("PayZombossStock");
        GiveFreeRespecStock(playerId, 1);                              // the human's
        GiveFreeRespecStock(playerId, 2, Zomboss(playerId), priced: false);   // Zomboss's, same save (no touch)
        var zombieStock = _store.FreeRespecStock(Zomboss(playerId));

        var outcome = _store.TryRespecSpecies(playerId, Species, Override(), "corr-human",
            payWith: RespecPayment.FreeRespec);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(0, _store.FreeRespecStock(Human(playerId)));
        Assert.Equal(zombieStock, _store.FreeRespecStock(Zomboss(playerId)));
    }

    // ---- EP4.11: the stock is never trimmed ----------------------------------------------------

    /// <summary>
    /// spec test 12, the architecture half: a compaction or archive pass must never name this ledger.
    /// The stock is a lifetime accrual held by an empire - `empire-resource-ssot.md` section 3's own row -
    /// so a trim that reached it would silently delete respecs the player earned; unlike the XP ledger
    /// (whose tails compaction is right to trim, and whose rule the empire credit deliberately does NOT
    /// depend on) there is no window in which an old row stops mattering.
    ///
    /// <para>A source scan rather than a runtime probe, because the failure to prevent is an EDIT: someone
    /// adding the table to a trim list. The last assertion stops it passing vacuously if the table were ever
    /// renamed out from under it.</para>
    ///
    /// <para><b>The runtime half of test 12 needs a FILE-BACKED store, not the in-memory default.</b>
    /// `CompactAfterRunClosed` and `TrimHotTailsNow` both refuse on an in-memory plan -
    /// `StorePlanException: A memory plan has no filesystem archive; archive entry points are file-only
    /// until the archive-target module makes the archive target memory-capable` (asserted by
    /// `RpgStoreStoragePlanTests`) - so the live pass is driven below on `DataTestStore.CreateFileBacked()`,
    /// which owns its directory and whose dispose clears the pool before deleting and throws if the delete
    /// fails. `ColdArchiveCompactionTests` is the sibling that exercises the same pass for other tables.</para>
    /// </summary>
    [Fact]
    public void No_compaction_or_archive_pass_names_the_free_respec_ledger()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Data", "Sqlite");
        var mentioning = Directory.EnumerateFiles(dir, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("rpg_empire_free_respec_ledger", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "RpgStore.EmpireFreeRespec.cs", "RpgStore.cs" }, mentioning);
        Assert.DoesNotContain(mentioning, n =>
            n.Contains("Compaction", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Archive", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Trim", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// spec test 12, the runtime half: a stock read after a compaction run equals the read before it, rows
    /// included, on a store where the pass can actually run. `CompactAfterRunClosed(null)` is what
    /// `TrimHotTailsNow` calls, and both are driven so neither entry point can start trimming this ledger
    /// unnoticed.
    /// </summary>
    [Fact]
    public void The_stock_survives_a_compaction_run_on_a_file_backed_store()
    {
        using var fileStore = DataTestStore.CreateFileBacked();
        var store = fileStore.Store;
        var playerId = store.GetCurrentPlayerId();
        var empire = new EmpireRef(new SaveId(playerId), store.HumanEmpireOf(playerId));

        // Two grants, the state an empire level leaves (EP4.5's own key shape: level, delta, reason).
        using (var db = SqliteConnectionFactory.Open(store.HotPath))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rpg_empire_free_respec_ledger(save_id, empire_id, level, delta, reason, granted_utc)
                VALUES ($s, $e, $l, 1, 'empire-level', $t);
                """;
            cmd.Parameters.AddWithValue("$s", playerId);
            cmd.Parameters.AddWithValue("$e", empire.Empire.Value);
            cmd.Parameters.AddWithValue("$l", 2L);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            cmd.Parameters["$l"].Value = 3L;
            cmd.ExecuteNonQuery();
        }

        var stockBefore = store.FreeRespecStock(empire);
        Assert.Equal(2, stockBefore);

        store.CompactAfterRunClosed(null);
        store.TrimHotTailsNow();

        Assert.Equal(stockBefore, store.FreeRespecStock(empire));
        // And the ROWS are still there, not merely a sum that happens to agree.
        using (var db = SqliteConnectionFactory.Open(store.HotPath))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rpg_empire_free_respec_ledger WHERE save_id=$s AND empire_id=$e;";
            cmd.Parameters.AddWithValue("$s", playerId);
            cmd.Parameters.AddWithValue("$e", empire.Empire.Value);
            Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
    }

}
