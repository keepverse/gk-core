using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>EP1.8 (spec-specimen-respec-price.md, R18, read in full this session) —
/// <see cref="RpgStore.TryReallocate"/>/<see cref="RpgStore.QuoteReallocation"/>: the one gate for the
/// <see cref="AllocationScope.UniqueCreature"/> and <see cref="AllocationScope.Commander"/> scopes.
/// Covers the module's own slice of the testing strategy: free additions, priced take-backs and
/// escalation, decay, two independent counters, replay under the new reasons, every named refusal
/// writing nothing, and never reading the free stock.</summary>
[Trait("VerificationId", "data.allocation-respec")]
public class AllocationRespecTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    const long PlayerId = 1;

    public AllocationRespecTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        SpeciesBuildTuningHub.Configure(new SpeciesBuildTuning(
            SchemaVersion: 1, Version: 1,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 633, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: LeanSignalWeights.Zero));
        // EP1.10 -- SaveAptitudePreset reads AptitudePresetTuningHub for the soft-max-presets check.
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(
            1, 1, SoftMaxPresets: 32, DefaultRowAbsMax: 1000,
            AssignLadder: new AssignLadderTuning(new[] { AptitudeAutoAssignRules.Even })));
    }

    public void Dispose() => _testStore.Dispose();

    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        CreatureSpeciesCatalog.All.First(s => s.DeployMode != CreatureDeployMode.HypnoAlly);

    EmpireRef HumanOwner => new(new SaveId(PlayerId), _store.HumanEmpireOf(PlayerId));
    static string CommanderScopeKey(long playerId) => $"player:{playerId}";

    string MintHumanSpecimen(ulong seed = 1) =>
        _store.MintForEmpire(HumanOwner, CatalogSpecies.SpeciesId, seed).Actor.InstanceId;

    static AptitudeAllocation Build(AllocationScope scope, long points) =>
        AptitudeAllocation.Single(scope, "Might", points);

    /// <summary>A preset save requires all twelve primaries (E5, sum exactly 1000) -- puts the whole
    /// 1000 on <paramref name="fullAptitudeId"/> and 0 on the other eleven.</summary>
    static IReadOnlyList<RpgAptitudePresetEntryRow> AllTwelveEntries(string presetId, string fullAptitudeId) =>
        AptitudeCatalog.All.Select(a => new RpgAptitudePresetEntryRow(
            presetId, a.Id, a.Id == fullAptitudeId ? 1000L : 0L, null, null, null, null)).ToList();

    // ---- Adding is free (test 2) ----------------------------------------------------------------

    [Fact]
    public void Adding_points_to_a_specimen_is_free_and_writes_no_ledger_or_counter_row()
    {
        var instanceId = MintHumanSpecimen();
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-1");

        var ledgerCountBefore = _store.ListSoulLedger(PlayerId).Items.Count;

        var outcome = _store.TryReallocate(
            HumanOwner, AllocationScope.UniqueCreature, instanceId, Build(AllocationScope.UniqueCreature, 3), correlationId: null);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.False(outcome.Priced);
        Assert.Equal(0, outcome.RespecCount);
        Assert.Equal(1000, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(ledgerCountBefore, _store.ListSoulLedger(PlayerId).Items.Count); // no new ledger row
        Assert.Equal(3, _store.LoadAllocation(AllocationScope.UniqueCreature, instanceId)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));
    }

    [Fact]
    public void A_first_allocation_from_empty_on_the_commander_pool_is_also_free()
    {
        var scopeKey = CommanderScopeKey(PlayerId);
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-1b");

        var outcome = _store.TryReallocate(
            HumanOwner, AllocationScope.Commander, scopeKey, Build(AllocationScope.Commander, 5), correlationId: null);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.False(outcome.Priced);
        Assert.Equal(1000, _store.GetSoulBalance(PlayerId).Balance);
    }

    // ---- A take-back is priced, escalates, and decays (tests 2 and 3) ----------------------------

    [Fact]
    public void A_take_back_charges_PriceOf_bumps_the_counter_and_escalates_next_time()
    {
        var instanceId = MintHumanSpecimen();
        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-2");
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), correlationId: null); // free first allocation

        var balance0 = _store.GetSoulBalance(PlayerId).Balance;
        var first = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 9), "chg-1"); // 10 -> 9, a respec, count 0 -> price 50
        Assert.True(first.Ok, first.Reason);
        Assert.True(first.Priced);
        Assert.Equal(50, first.PriceAmount);
        Assert.Equal(balance0 - 50, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(1, first.RespecCount);

        var second = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 8), "chg-2"); // count 1 -> price 75
        Assert.Equal(75, second.PriceAmount);
        Assert.True(second.PriceAmount > first.PriceAmount);
    }

    [Fact]
    public void Decay_lowers_the_count_and_the_next_price_after_enough_elapsed_time()
    {
        var instanceId = MintHumanSpecimen();
        var day0 = DateTimeOffset.UtcNow.Date;
        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-3");
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), null, day0);
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 9), "chg-1", day0); // count -> 1

        // DecayDays is 3 -- a respec priced three whole days later reads the decayed count (0), base price again.
        var later = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 8), "chg-2", day0.AddDays(3));
        Assert.Equal(50, later.PriceAmount);
    }

    // ---- Two independent counters (test 4) --------------------------------------------------------

    [Fact]
    public void The_commander_pool_and_a_specimen_keep_separate_counters()
    {
        var instanceId = MintHumanSpecimen();
        var commanderKey = CommanderScopeKey(PlayerId);
        _store.AwardSouls(PlayerId, 10_000, "seed", "bank-4");

        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), null); // free
        _store.TryReallocate(HumanOwner, AllocationScope.Commander, commanderKey,
            Build(AllocationScope.Commander, 10), null); // free

        // Respec the specimen three times; the commander pool must still read count 0.
        for (var i = 0; i < 3; i++)
        {
            var pts = 9 - i;
            var r = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
                Build(AllocationScope.UniqueCreature, pts), $"spec-{i}");
            Assert.True(r.Ok, r.Reason);
        }

        var commanderQuote = _store.QuoteReallocation(HumanOwner, AllocationScope.Commander, commanderKey,
            Build(AllocationScope.Commander, 5), out var commanderIsRespec);
        Assert.True(commanderIsRespec);
        Assert.Equal(50, commanderQuote.Souls.Amount); // base price -- the specimen's churn never moved this counter

        // Respec the commander pool once and confirm the specimen's own counter is unaffected.
        var commanderRespec = _store.TryReallocate(HumanOwner, AllocationScope.Commander, commanderKey,
            Build(AllocationScope.Commander, 5), "cmd-1");
        Assert.Equal(1, commanderRespec.RespecCount);

        var specimenQuote = _store.QuoteReallocation(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 6), out var specimenIsRespec);
        Assert.True(specimenIsRespec);
        // The specimen was respecced 3 times above -- its own counter (3) prices independently of
        // the commander pool's counter (1, from the line above).
        Assert.Equal(RespecPolicy.PriceOf(SpeciesBuildTuningHub.Tuning.UniqueRespec, 3).Amount, specimenQuote.Souls.Amount);
    }

    // ---- Replay under the new reasons (test 5) -----------------------------------------------------

    [Fact]
    public void A_replayed_correlation_charges_once_under_the_respec_unique_reason()
    {
        var instanceId = MintHumanSpecimen();
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-5");
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), null);
        var first = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 9), "chg-1"); // 10 -> 9, priced, count 0 -> 1
        Assert.True(first.Ok, first.Reason);
        var balanceAfterFirst = _store.GetSoulBalance(PlayerId).Balance;

        // Reusing "chg-1" with a DIFFERENT target that is still a respec against the now-updated
        // state (9 -> 8): the correlation id is the caller's sole promise of "same request" (spec's
        // own wording), so this still returns the ORIGINAL outcome rather than pricing off the
        // counter the first call already advanced.
        var replay = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 8), "chg-1");
        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal(50, replay.PriceAmount);
        Assert.Equal(1, replay.RespecCount); // never advanced past what the first call set
        Assert.Equal(balanceAfterFirst, _store.GetSoulBalance(PlayerId).Balance);
        // The allocation itself is untouched by the replay -- still the FIRST call's result (9), not 8.
        Assert.Equal(9, _store.LoadAllocation(AllocationScope.UniqueCreature, instanceId)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));

        Assert.Contains(_store.ListSoulLedger(PlayerId).Items, e => e.Reason == "respec-unique" && e.Delta == -50);
    }

    [Fact]
    public void A_commander_respec_ledgers_under_the_respec_commander_reason()
    {
        var commanderKey = CommanderScopeKey(PlayerId);
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-5b");
        _store.TryReallocate(HumanOwner, AllocationScope.Commander, commanderKey,
            Build(AllocationScope.Commander, 10), null);
        _store.TryReallocate(HumanOwner, AllocationScope.Commander, commanderKey,
            Build(AllocationScope.Commander, 9), "cmd-1");

        Assert.Contains(_store.ListSoulLedger(PlayerId).Items, e => e.Reason == "respec-commander" && e.Delta == -50);
    }

    // ---- Refusals write nothing (test 6) ------------------------------------------------------------

    [Fact]
    public void Insufficient_souls_refuses_and_writes_nothing()
    {
        var instanceId = MintHumanSpecimen();
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), null); // free, no souls needed
        var before = _store.LoadAllocation(AllocationScope.UniqueCreature, instanceId)
            .PointsAt(AllocationScope.UniqueCreature, "Might");

        var refused = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 9), "poor-1"); // balance is 0

        Assert.False(refused.Ok);
        Assert.Equal("souls.insufficient", refused.Reason);
        Assert.Equal(0, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(before, _store.LoadAllocation(AllocationScope.UniqueCreature, instanceId)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));
    }

    [Fact]
    public void A_missing_correlation_id_on_a_respec_refuses_and_writes_nothing()
    {
        var instanceId = MintHumanSpecimen();
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-6");
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), null);

        var refused = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 9), correlationId: null);

        Assert.False(refused.Ok);
        Assert.Equal("correlation.missing", refused.Reason);
        Assert.Equal(1000, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(10, _store.LoadAllocation(AllocationScope.UniqueCreature, instanceId)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));
    }

    [Fact]
    public void A_specimen_the_callers_empire_does_not_own_is_refused_including_a_zomboss_specimen_of_the_same_save()
    {
        var zombossOwner = new EmpireRef(new SaveId(PlayerId), EmpireId.Zomboss);
        var zombossSpecimen = _store.MintForEmpire(zombossOwner, CatalogSpecies.SpeciesId, seed: 99).Actor.InstanceId;
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-7");

        // The HUMAN empire of the same save attempts to reallocate Zomboss's own specimen.
        var refused = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, zombossSpecimen,
            Build(AllocationScope.UniqueCreature, 10), null);

        Assert.False(refused.Ok);
        Assert.Equal("respec.target.not-owned", refused.Reason);
        Assert.Equal(1000, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(0, _store.LoadAllocation(AllocationScope.UniqueCreature, zombossSpecimen)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));
    }

    [Fact]
    public void A_non_human_payer_is_refused_before_any_read()
    {
        var zombossOwner = new EmpireRef(new SaveId(PlayerId), EmpireId.Zomboss);
        var zombossSpecimen = _store.MintForEmpire(zombossOwner, CatalogSpecies.SpeciesId, seed: 100).Actor.InstanceId;
        _store.AwardSouls(PlayerId, 1000, "seed", "bank-8");

        // Zomboss attempting to pay for his OWN specimen -- still refused; an AI empire has no
        // re-allocation surface (Tier B, same rule as species).
        var refused = _store.TryReallocate(zombossOwner, AllocationScope.UniqueCreature, zombossSpecimen,
            Build(AllocationScope.UniqueCreature, 10), null);

        Assert.False(refused.Ok);
        Assert.Equal("empire.notHuman", refused.Reason);
        Assert.Equal(1000, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(0, _store.LoadAllocation(AllocationScope.UniqueCreature, zombossSpecimen)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));
    }

    // ---- Never reads the free stock (test 11) --------------------------------------------------------

    [Fact]
    public void No_quote_ever_carries_a_nonzero_free_stock()
    {
        var instanceId = MintHumanSpecimen();
        var commanderKey = CommanderScopeKey(PlayerId);
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), null);
        _store.TryReallocate(HumanOwner, AllocationScope.Commander, commanderKey,
            Build(AllocationScope.Commander, 10), null);

        var uniqueQuote = _store.QuoteReallocation(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 9), out _);
        var commanderQuote = _store.QuoteReallocation(HumanOwner, AllocationScope.Commander, commanderKey,
            Build(AllocationScope.Commander, 9), out _);

        Assert.Equal(0, uniqueQuote.FreeStock);
        Assert.False(uniqueQuote.FreeAvailable);
        Assert.Equal(0, commanderQuote.FreeStock);
        Assert.False(commanderQuote.FreeAvailable);
    }

    // ---- Scope guard ----------------------------------------------------------------------------

    [Fact]
    public void A_scope_other_than_commander_or_unique_creature_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _store.TryReallocate(HumanOwner, AllocationScope.CreatureType, "peashooter",
                AptitudeAllocation.Empty, null));
    }

    // ---- EP1.10: preset activation charges exactly what the by-hand route charges (test 10) ------

    [Fact]
    public void Preset_activation_over_a_specimen_charges_exactly_what_TryReallocate_charges_by_hand()
    {
        var byHandInstance = MintHumanSpecimen(seed: 50);
        var presetInstance = MintHumanSpecimen(seed: 51);
        _store.AwardSouls(PlayerId, 10_000, "seed", "ep1.10-bank-a");
        var day0 = DateTimeOffset.UtcNow.Date;

        // Both specimens start identically: a free first allocation of Might=10.
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, byHandInstance,
            Build(AllocationScope.UniqueCreature, 10), null, day0);
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, presetInstance,
            Build(AllocationScope.UniqueCreature, 10), null, day0);

        var ledgerBefore = _store.ListSoulLedger(PlayerId).Items.Count;

        // By hand: take Might 10 -> 9 on byHandInstance via TryReallocate directly.
        var byHand = _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, byHandInstance,
            Build(AllocationScope.UniqueCreature, 9), "ep1.10-by-hand", day0);
        Assert.True(byHand.Ok, byHand.Reason);
        Assert.True(byHand.Priced);

        // Preset activation: the IDENTICAL take-back (Might 10 -> 9) on presetInstance, through a
        // real saved preset and TryActivateAptitudePreset -- EP1.10's own gate reuse.
        const string presetId = "ep1.10-preset-a";
        var saveResult = _store.SaveAptitudePreset(
            new RpgAptitudePresetRow(presetId, PlayerId, "EP1.10 test preset",
                RpgStore.AptitudePresetKindPlayer, day0.ToString("o"), 0),
            AllTwelveEntries(presetId, "Might"),
            isCreate: true);
        Assert.Equal("", saveResult);

        var shares = new Dictionary<string, long>(StringComparer.Ordinal) { ["Might"] = 9 };
        var activateOutcome = _store.TryActivateAptitudePreset(
            PlayerId, presetId, "unique", presetInstance,
            Build(AllocationScope.UniqueCreature, 9), shares, leftover: 0,
            "ep1.10-preset-activate", day0);

        Assert.True(activateOutcome.Ok, activateOutcome.Reason);
        Assert.Equal(byHand.Priced, activateOutcome.Priced);
        Assert.Equal(byHand.PriceAmount, activateOutcome.PriceAmount);
        Assert.Equal(byHand.RespecCount, activateOutcome.RespecCount);

        // Soul-ledger deltas equal entry for entry: two new rows, same reason, same (negative) amount.
        // ListSoulLedger returns newest-first, so the two new rows are the FRONT of the list, not a
        // suffix -- Skip(ledgerBefore) would instead wrap into the old "seed" row.
        var ledgerAfter = _store.ListSoulLedger(PlayerId).Items;
        Assert.Equal(ledgerBefore + 2, ledgerAfter.Count);
        var newRows = ledgerAfter.Take(2).ToList();
        Assert.All(newRows, e => Assert.Equal(Core.Creatures.SoulEarnPolicy.Reasons.RespecUnique, e.Reason));
        Assert.All(newRows, e => Assert.Equal(-byHand.PriceAmount, e.Delta));

        // rpg_allocation_respec rows equal at the same injected clock: both read count 1 right now.
        Assert.Equal(
            _store.GetAllocationRespecCount(AllocationScope.UniqueCreature, byHandInstance, day0),
            _store.GetAllocationRespecCount(AllocationScope.UniqueCreature, presetInstance, day0));
    }

    [Fact]
    public void A_respec_activation_without_a_correlation_id_refuses_and_writes_nothing()
    {
        var instanceId = MintHumanSpecimen(seed: 52);
        _store.AwardSouls(PlayerId, 1000, "seed", "ep1.10-bank-b");
        _store.TryReallocate(HumanOwner, AllocationScope.UniqueCreature, instanceId,
            Build(AllocationScope.UniqueCreature, 10), null); // free first allocation

        const string presetId = "ep1.10-preset-b";
        Assert.Equal("", _store.SaveAptitudePreset(
            new RpgAptitudePresetRow(presetId, PlayerId, "EP1.10 test preset 2",
                RpgStore.AptitudePresetKindPlayer, DateTime.UtcNow.ToString("o"), 0),
            AllTwelveEntries(presetId, "Might"),
            isCreate: true));

        var ledgerBefore = _store.ListSoulLedger(PlayerId).Items.Count;
        var shares = new Dictionary<string, long>(StringComparer.Ordinal) { ["Might"] = 9 };

        var outcome = _store.TryActivateAptitudePreset(
            PlayerId, presetId, "unique", instanceId,
            Build(AllocationScope.UniqueCreature, 9), shares, leftover: 0,
            correlationId: null);

        Assert.False(outcome.Ok);
        Assert.Equal("correlation.missing", outcome.Reason);
        Assert.Equal(1000, _store.GetSoulBalance(PlayerId).Balance);
        Assert.Equal(ledgerBefore, _store.ListSoulLedger(PlayerId).Items.Count);
        Assert.Equal(10, _store.LoadAllocation(AllocationScope.UniqueCreature, instanceId)
            .PointsAt(AllocationScope.UniqueCreature, "Might"));
    }
}
