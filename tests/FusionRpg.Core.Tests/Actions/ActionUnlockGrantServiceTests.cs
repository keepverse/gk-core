using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T59.6 (spec-action-instance-and-grant.md §4): pure roll-and-grant logic, mirroring
/// `UnlockDiscardTests.cs`'s own fake-delegate style. `PoisonAction`/`PoisonState` throw if ever
/// invoked — the mechanism the acceptance bar's "grant/persist are never called on a missed or
/// empty-pool roll" is proven with, not asserted from reading the code.
/// </summary>
public class ActionUnlockGrantServiceTests
{
    static readonly UnlockTuning AlwaysAccepts =
        new(P1Milli: 1000, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);

    static readonly UnlockTuning RarelyAccepts =
        new(P1Milli: 1, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100);

    static ActionRow Row(string id, EligibilityScope scope = EligibilityScope.General, string? scopeKey = null) => new()
    {
        ActionId = id, Name = id, Kind = ActionKind.Skill, Scope = scope, ScopeKey = scopeKey, Rung = 1,
        // Grantable: true is what the imported corpus rows carry (ActionCorpusComposer.cs:186,
        // unconditional) -- the roll's rows are the ones a real catalog offers for a grant.
        Grantable = true,
    };

    /// <summary>The shape `gk-data/packs/fusion/data/seed/actions/authored-basics.json` imports as: `Kind = Basic` while
    /// the corpus composer still stamps `grantable = true` (ADG-F5). Both halves matter -- a filter
    /// keyed on the flag alone would still offer this row.</summary>
    static ActionRow BasicRow(string id) => Row(id) with { Kind = ActionKind.Basic };

    static ActionRow UnGrantableRow(string id) => Row(id) with { Grantable = false };

    static void Poison(string a, string b) => throw new InvalidOperationException("grant must never be called here");
    static void PoisonSave(string a, UnlockState b) => throw new InvalidOperationException("saveUnlockState must never be called here");

    [Fact]
    public void AnEmptyCatalogIsALegalNoOpAndNeverCallsSaveOrGrant()
    {
        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => UnlockState.Empty(),
            saveUnlockState: PoisonSave,
            catalog: () => Array.Empty<ActionRow>(),
            familyOf: new Dictionary<string, IReadOnlyList<string>>(),
            grant: Poison);

        var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: 42, AlwaysAccepts);

        Assert.False(outcome.Granted);
        Assert.Null(outcome.GrantedActionId);
        Assert.Null(outcome.RefusalReason);
    }

    [Fact]
    public void HoldingEveryCatalogActionIsALegalNoOpAndNeverCallsSaveOrGrant()
    {
        var catalog = new[] { Row("action.a"), Row("action.b") };
        var alreadyHeld = UnlockState.FromPersisted(2, new[] { new HeldUnlock("action.a", 1), new HeldUnlock("action.b", 2) });

        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => alreadyHeld,
            saveUnlockState: PoisonSave,
            catalog: () => catalog,
            familyOf: new Dictionary<string, IReadOnlyList<string>>(),
            grant: Poison);

        var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: 42, AlwaysAccepts);

        Assert.False(outcome.Granted);
        Assert.Null(outcome.RefusalReason);
    }

    [Fact]
    public void ASuccessfulRollSavesTheUpdatedStateAndGrantsTheChosenAction()
    {
        var catalog = new[] { Row("action.only") };
        UnlockState? saved = null;
        string? grantedAction = null;
        string? grantedTo = null;

        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => UnlockState.Empty(),
            saveUnlockState: (id, state) => saved = state,
            catalog: () => catalog,
            familyOf: new Dictionary<string, IReadOnlyList<string>>(),
            grant: (id, actionId) => { grantedTo = id; grantedAction = actionId; });

        var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: 42, AlwaysAccepts);

        Assert.True(outcome.Granted);
        Assert.Equal("action.only", outcome.GrantedActionId);
        Assert.NotNull(saved);
        Assert.Contains(saved!.Held, h => h.UnlockId == "action.only");
        Assert.Equal("specimen-1", grantedTo);
        Assert.Equal("action.only", grantedAction);
    }

    [Fact]
    public void TheSameInputsProduceTheSameOutcomeEveryTime()
    {
        var catalog = new[] { Row("action.a"), Row("action.b"), Row("action.c") };

        UnlockGrantOutcome Run() => new ActionUnlockGrantService(
            loadUnlockState: _ => UnlockState.Empty(),
            saveUnlockState: (_, _) => { },
            catalog: () => catalog,
            familyOf: new Dictionary<string, IReadOnlyList<string>>(),
            grant: (_, _) => { }).TryRollOnce("specimen-x", speciesKey: null, specimenWorldSeed: 777, AlwaysAccepts);

        var first = Run();
        var second = Run();

        Assert.Equal(first.Granted, second.Granted);
        Assert.Equal(first.GrantedActionId, second.GrantedActionId);
    }

    /// <summary>Acceptance bar: "a poison-delegate test proves grant/persist are never called on a
    /// missed... roll." `RarelyAccepts` (P1Milli=1, i.e. a 0.1% base chance) makes a miss the
    /// overwhelmingly likely outcome for almost any seed; scanning a bounded range of seeds finds a
    /// real one deterministically rather than asserting from the formula alone.</summary>
    [Fact]
    public void AMissedRollNeverCallsSaveOrGrant()
    {
        var catalog = new[] { Row("action.only") };
        var foundAMiss = false;

        for (ulong seed = 0; seed < 200 && !foundAMiss; seed++)
        {
            var service = new ActionUnlockGrantService(
                loadUnlockState: _ => UnlockState.Empty(),
                saveUnlockState: PoisonSave,
                catalog: () => catalog,
                familyOf: new Dictionary<string, IReadOnlyList<string>>(),
                grant: Poison);

            var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: seed, RarelyAccepts);
            if (!outcome.Granted && outcome.RefusalReason == UnlockRefusalReason.RollMissed)
                foundAMiss = true;
            // Any other outcome (a hit) would already have thrown via the poison delegates above if
            // save/grant fired incorrectly on a miss -- reaching here at all for a hit seed is fine,
            // it just does not yet prove the claim, so the loop keeps scanning.
        }

        Assert.True(foundAMiss, "no missed roll found in 200 seeds at a 0.1% base chance -- something is wrong with the chance formula or the seed derivation");
    }

    [Fact]
    public void CandidatesRespectEligibilityScopeNotJustTheHeldFilter()
    {
        var catalog = new[]
        {
            Row("action.general"),
            Row("action.other-family", EligibilityScope.Family, "other-family"),
        };
        string? granted = null;

        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => UnlockState.Empty(),
            saveUnlockState: (_, _) => { },
            catalog: () => catalog,
            familyOf: new Dictionary<string, IReadOnlyList<string>> { ["species.mine"] = new[] { "my-family" } }, // does not map to "other-family"
            grant: (_, actionId) => granted = actionId);

        service.TryRollOnce("specimen-1", speciesKey: "species.mine", specimenWorldSeed: 1, AlwaysAccepts);

        Assert.Equal("action.general", granted); // the only actually-eligible candidate
    }

    /// <summary>
    /// ADG-F5, the regression proof at this layer. The catalog holds the two rows a real store holds
    /// and the roll may never offer: `act.attack`-shaped (Basic, and `grantable` still true because
    /// `ActionCorpusComposer` stamps it unconditionally) and a `grantable = 0` skill. The grant
    /// delegate is the REAL write path's own refusal
    /// (`RpgStore.UniqueActors.cs:2127-2134`: `ActionValidator.GrantRefusal` -> throw), so any seed
    /// that offered a refused row would fail here the same way a live level-up did — and the roll is
    /// swept across a wide seed range rather than asserted from the filter's source, so "never
    /// offered" is measured, not read.
    /// </summary>
    [Fact]
    public void TheRollNeverOffersABasicOrNonGrantableRowAndNeverTripsTheWritePathRefusal()
    {
        var catalog = new[]
        {
            BasicRow("act.attack"),
            UnGrantableRow("action.not-grantable"),
            Row("action.grantable.a"),
            Row("action.grantable.b"),
        };
        var grantedIds = new List<string>();

        // The real grant delegate's shape: refuse exactly what ActionValidator refuses, then throw --
        // this is the throw ADG-F5 is about, reproduced at the layer that caused it.
        void Grant(string _, string actionId)
        {
            var row = catalog.First(c => c.ActionId == actionId);
            if (ActionValidator.GrantRefusal(row) is { } refusal)
                throw new InvalidOperationException($"action unlock grant refused: {refusal.Reason}");
            grantedIds.Add(actionId);
        }

        // The sweep size is a local symbol rather than a second copy of the number, so the assertion
        // below compares the grant count against the same bound this loop walks and the two cannot
        // drift. (population-pin P1: `Assert.Equal(500, grantedIds.Count)` was an unmarked population
        // pin — the spec's fix is the contract, never a `pin:` marker.)
        const int seedSweep = 500;
        for (ulong seed = 0; seed < seedSweep; seed++)
        {
            var service = new ActionUnlockGrantService(
                loadUnlockState: _ => UnlockState.Empty(),
                saveUnlockState: (_, _) => { },
                catalog: () => catalog,
                familyOf: new Dictionary<string, IReadOnlyList<string>>(),
                grant: Grant);

            var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: seed, AlwaysAccepts);

            Assert.True(outcome.Granted, $"seed {seed}: the grantable candidates were offered, so the roll must land");
            Assert.DoesNotContain(outcome.GrantedActionId, new[] { "act.attack", "action.not-grantable" });
            Assert.Equal(2, outcome.Skipped!.Count); // both refused rows are reported as skips, every seed
        }

        Assert.Equal(seedSweep, grantedIds.Count);
        Assert.All(grantedIds, id => Assert.StartsWith("action.grantable.", id, StringComparison.Ordinal));
    }

    /// <summary>
    /// ADG-F5 acceptance 2: a candidate set that is empty ONLY because nothing left is grantable is
    /// the same legal no-op as an empty catalog — no save, no grant, no throw — and it says so:
    /// <see cref="UnlockRefusalReason.NoGrantableCandidate" /> plus the skipped ids with the validator's
    /// own reason for each. `PoisonSave`/`Poison` prove the last two by being the failure, not by a
    /// comment.
    /// </summary>
    [Fact]
    public void ACatalogOfOnlyUnGrantableRowsIsALegalNoOpAndNeverCallsSaveOrGrant()
    {
        var catalog = new[] { BasicRow("act.attack"), UnGrantableRow("action.not-grantable") };

        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => UnlockState.Empty(),
            saveUnlockState: PoisonSave,
            catalog: () => catalog,
            familyOf: new Dictionary<string, IReadOnlyList<string>>(),
            grant: Poison);

        var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: 42, AlwaysAccepts);

        Assert.False(outcome.Granted);
        Assert.Null(outcome.GrantedActionId);
        Assert.Equal(UnlockRefusalReason.NoGrantableCandidate, outcome.RefusalReason);
        Assert.Equal(
            new[]
            {
                new UnlockSkip("act.attack", ActionRejectionReason.BasicCollision),
                new UnlockSkip("action.not-grantable", ActionRejectionReason.ActionNotGrantable),
            },
            outcome.Skipped!.OrderBy(s => s.ActionId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// ADG-F5's stated acceptance: "a roll whose option set includes an un-grantable action must still
    /// return its grantable options". The skipped options are reported, not merely dropped — the grant
    /// delegate below is the real write path's own refusal, so an option that reached it would throw
    /// exactly as a live level-up did.
    /// </summary>
    [Fact]
    public void ARollWhoseOptionSetHoldsUnGrantableActionsStillReturnsItsGrantableOptionsAndReportsTheSkips()
    {
        var catalog = new[]
        {
            BasicRow("act.attack"),
            UnGrantableRow("action.not-grantable"),
            Row("action.grantable.only"),
        };
        string? granted = null;

        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => UnlockState.Empty(),
            saveUnlockState: (_, _) => { },
            catalog: () => catalog,
            familyOf: new Dictionary<string, IReadOnlyList<string>>(),
            grant: (_, actionId) =>
            {
                if (ActionValidator.GrantRefusal(catalog.First(c => c.ActionId == actionId)) is { } refusal)
                    throw new InvalidOperationException($"action unlock grant refused: {refusal.Reason}");
                granted = actionId;
            });

        var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: 7, AlwaysAccepts);

        Assert.True(outcome.Granted);
        Assert.Equal("action.grantable.only", outcome.GrantedActionId);
        Assert.Equal("action.grantable.only", granted);
        Assert.Null(outcome.RefusalReason);
        Assert.Equal(2, outcome.Skipped!.Count);
        Assert.Contains(outcome.Skipped, s => s.ActionId == "act.attack" && s.Reason == ActionRejectionReason.BasicCollision);
        Assert.Contains(outcome.Skipped, s => s.ActionId == "action.not-grantable" && s.Reason == ActionRejectionReason.ActionNotGrantable);
        Assert.DoesNotContain(outcome.Skipped, s => s.ActionId == "action.grantable.only");
    }

    /// <summary>
    /// A candidate the actor already holds is not a skip: it was never an offer this roll could make,
    /// and reporting it as one would tell a surface that something was refused when nothing was.
    /// </summary>
    [Fact]
    public void AnAlreadyHeldCandidateIsNotReportedAsASkip()
    {
        var catalog = new[] { Row("action.held"), Row("action.grantable.free") };
        var alreadyHeld = UnlockState.FromPersisted(1, new[] { new HeldUnlock("action.held", 1) });

        var service = new ActionUnlockGrantService(
            loadUnlockState: _ => alreadyHeld,
            saveUnlockState: (_, _) => { },
            catalog: () => catalog,
            familyOf: new Dictionary<string, IReadOnlyList<string>>(),
            grant: (_, _) => { });

        var outcome = service.TryRollOnce("specimen-1", speciesKey: null, specimenWorldSeed: 3, AlwaysAccepts);

        Assert.Equal("action.grantable.free", outcome.GrantedActionId);
        Assert.Null(outcome.Skipped);
    }

    /// <summary>
    /// The filter's two halves are one definition, so they cannot drift: `ValidateGrant` and
    /// `IsGrantable` must answer the same for the same row. A row is judged through the validator's
    /// own grant path here rather than by re-stating the rule.
    /// </summary>
    [Fact]
    public void IsGrantableAgreesWithWhatTheValidatorItselfRefusesForTheSameRow()
    {
        var rows = new[] { Row("action.ok"), BasicRow("act.attack"), UnGrantableRow("action.not-grantable") };

        foreach (var row in rows)
        {
            var validatorSaysOk = ActionValidator
                .ValidateGrant(new ActionGrantRow(OwnerKind.UniqueActor, "specimen-1", row.ActionId, Source: "test"),
                    id => rows.FirstOrDefault(r => r.ActionId == id))
                .IsOk;

            Assert.Equal(validatorSaysOk, ActionValidator.IsGrantable(row));
        }
    }
}
