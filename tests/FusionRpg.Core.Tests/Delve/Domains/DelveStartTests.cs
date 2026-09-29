using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.21 (spec-domain-catalog.md §6) — `DelveStart.Run`: one red test per refusal, asserting
/// order where two would otherwise both fail, and "a failed start creates no row and debits no souls"
/// proven structurally (this file references no store type at all, matching the exact
/// `DomainPreflightTests.cs`/D4.17 precedent for the identical claim).</summary>
public class DelveStartTests
{
    static readonly IReadOnlyDictionary<string, int> DangerBandOrdinals = new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 };

    static DomainRow Domain(string id = "domain.a", string entry = "many", string layoutId = "layout.standard") =>
        new(id, "Test Domain", "A test flavor.", "theme.overgrown", "fire", "shallow", entry,
            layoutId, "species.warden", null, "Lair", null);

    static DomainCatalog Catalog(params DomainRow[] rows) => DomainCatalog.Load(rows, DangerBandOrdinals).Catalog;

    static DelveStartRequest Request(
        string correlationId = "corr-1", string domainId = "domain.a", string? parentWorldId = null,
        string rungIdOrTailLabel = "medium", bool oath = false, string raidMode = "solo",
        IReadOnlyList<string>? members = null, IReadOnlyList<PackItem>? carryIn = null) =>
        new(correlationId, PlayerId: 1, domainId, parentWorldId, rungIdOrTailLabel, oath, raidMode,
            members ?? new[] { "actor-1" }, carryIn ?? Array.Empty<PackItem>());

    static RungOfferSet PassingOffer() => new(
        Rungs: new[]
        {
            new RungOfferRow("medium", Offered: true, Band: 2, BandName: "Shallow", IsPermadeath: false, Refusal: RungOfferRefusal.None),
            new RungOfferRow("very-hard", Offered: true, Band: 4, BandName: "Abyssal", IsPermadeath: true, Refusal: RungOfferRefusal.None),
        },
        TailSteps: new[] { new TailOfferRow(1, Offered: true, Band: 4, BandName: "Abyssal", Label: "Abyss +1", Refusal: RungOfferRefusal.None) },
        IsOnceEntry: false, OnceSealOnWipe: false, OnceFailKeepsBossLoot: false);

    static readonly DelveGraph EmptyGraph = new(Array.Empty<WorldSector>(), Array.Empty<WorldLane>(), Array.Empty<DelveRoomFact>(), Array.Empty<DelveWalk>());

    static DelveStartLive Live(
        Func<string, DelveStartReplay?>? replayFor = null,
        Func<string, Staleness>? stalenessFor = null,
        Func<string, (string? State, long? DelveId)>? delveStateFor = null,
        Func<DomainRow, PlayerClears, RungOfferSet>? composeRungs = null,
        Func<string, IReadOnlyList<string>>? raidModesForLayout = null,
        Func<string, (int Parties, int SquadSlots)?>? partyShapeForRaidMode = null,
        Func<string, bool>? memberIsOwnedRosterBound = null,
        Func<string, bool>? memberIsRecovering = null,
        Func<string, bool>? memberIsOnExpedition = null,
        Func<string, bool>? memberIsInAnotherActiveDelve = null,
        Func<string, long>? stockOf = null,
        Func<DomainRow, long>? provisioningPriceFor = null,
        Func<long, long>? soulBalanceFor = null,
        Func<DomainRow, string, ulong, (DelveGraph? Graph, string? RefusalDetail)>? rollAndPreflight = null) => new(
        ReplayFor: replayFor ?? (_ => null),
        StalenessFor: stalenessFor ?? (_ => Staleness.Fresh),
        DelveStateFor: delveStateFor ?? (_ => (null, null)),
        ClearsFor: _ => PlayerClears.None,
        ComposeRungs: composeRungs ?? ((_, _) => PassingOffer()),
        RaidModesForLayout: raidModesForLayout ?? (_ => new[] { "solo" }),
        PartyShapeForRaidMode: partyShapeForRaidMode ?? (_ => (Parties: 1, SquadSlots: 4)),
        MemberIsOwnedRosterBound: memberIsOwnedRosterBound ?? (_ => true),
        MemberIsRecovering: memberIsRecovering ?? (_ => false),
        MemberIsOnExpedition: memberIsOnExpedition ?? (_ => false),
        MemberIsInAnotherActiveDelve: memberIsInAnotherActiveDelve ?? (_ => false),
        ProvisionCells: 10,
        StockOf: stockOf ?? (_ => 100),
        ProvisioningPriceFor: provisioningPriceFor ?? (_ => 50),
        SoulBalanceFor: soulBalanceFor ?? (_ => 1000),
        ContentTermsJsonFor: (_, _) => "{}",
        SealSeed: () => 12345UL,
        RollAndPreflight: rollAndPreflight ?? ((_, _, _) => (EmptyGraph, null)));

    [Fact]
    public void A_fully_passing_request_returns_a_plan_no_refusal_no_replay()
    {
        var (refusal, replay, plan) = DelveStart.Run(Request(), Catalog(Domain()), Live());

        Assert.Null(refusal);
        Assert.Null(replay);
        Assert.NotNull(plan);
        Assert.Equal("domain.a", plan!.DomainId);
        Assert.Equal(12345UL, plan.Seed);
        Assert.Equal(50, plan.ProvisioningPriceSouls);
        Assert.Equal("{}", plan.ContentTermsJson);
    }

    // ---- group 1: correlation, replay, domain state ------------------------------------------------

    [Fact]
    public void Empty_correlationId_refuses_correlation_missing()
    {
        var (refusal, _, _) = DelveStart.Run(Request(correlationId: ""), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RuleCorrelationMissing, refusal!.Rule);
    }

    [Fact]
    public void Overlong_correlationId_refuses_correlation_missing()
    {
        var (refusal, _, _) = DelveStart.Run(Request(correlationId: new string('x', 65)), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RuleCorrelationMissing, refusal!.Rule);
    }

    [Fact]
    public void A_matching_replay_returns_the_recorded_delve_no_refusal_no_plan()
    {
        var replay = new DelveStartReplay(99, "world-1", "domain.a", "solo", "medium", false);
        var (refusal, gotReplay, plan) = DelveStart.Run(Request(), Catalog(Domain()), Live(replayFor: _ => replay));

        Assert.Null(refusal);
        Assert.Null(plan);
        Assert.Equal(99, gotReplay!.DelveId);
    }

    [Fact]
    public void A_replay_with_a_different_rung_refuses_correlation_mismatch()
    {
        var replay = new DelveStartReplay(99, "world-1", "domain.a", "solo", "very-hard", false); // different rung
        var (refusal, _, _) = DelveStart.Run(Request(), Catalog(Domain()), Live(replayFor: _ => replay));

        Assert.Equal(DelveStart.RuleCorrelationMismatch, refusal!.Rule);
    }

    [Fact]
    public void A_replay_that_differs_only_in_oath_refuses_correlation_mismatch()
    {
        // Isolates the Oath comparison specifically -- every other field matches the request's own
        // Oath: false, so this is the only test that can catch a mutation dropping just this clause.
        var replay = new DelveStartReplay(99, "world-1", "domain.a", "solo", "medium", true);
        var (refusal, _, _) = DelveStart.Run(Request(oath: false), Catalog(Domain()), Live(replayFor: _ => replay));

        Assert.Equal(DelveStart.RuleCorrelationMismatch, refusal!.Rule);
    }

    [Fact]
    public void An_unknown_domain_refuses_domain_not_found()
    {
        var (refusal, _, _) = DelveStart.Run(Request(domainId: "domain.unknown"), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RuleDomainNotFound, refusal!.Rule);
    }

    [Fact]
    public void A_stale_domain_refuses_domain_stale()
    {
        var (refusal, _, _) = DelveStart.Run(Request(), Catalog(Domain()), Live(stalenessFor: _ => Staleness.Stale));
        Assert.Equal(DelveStart.RuleDomainStale, refusal!.Rule);
    }

    [Fact]
    public void A_once_domain_already_archived_refuses_domain_sealed()
    {
        var (refusal, _, _) = DelveStart.Run(
            Request(), Catalog(Domain(entry: "once")), Live(delveStateFor: _ => ("Archived", 5L)));
        Assert.Equal(DelveStart.RuleDomainSealed, refusal!.Rule);
    }

    [Fact]
    public void A_many_domain_archived_is_never_sealed()
    {
        var (refusal, _, plan) = DelveStart.Run(
            Request(), Catalog(Domain(entry: "many")), Live(delveStateFor: _ => ("Archived", 5L)));
        Assert.Null(refusal);
        Assert.NotNull(plan);
    }

    [Fact]
    public void An_active_delve_refuses_delve_in_progress()
    {
        var (refusal, _, _) = DelveStart.Run(Request(), Catalog(Domain()), Live(delveStateFor: _ => ("Active", 5L)));
        Assert.Equal(DelveStart.RuleDelveInProgress, refusal!.Rule);
    }

    // ---- group 2: rung/tail, oath, raid mode, party shape ------------------------------------------

    [Fact]
    public void An_unoffered_rung_refuses_rung_not_offered()
    {
        var (refusal, _, _) = DelveStart.Run(Request(rungIdOrTailLabel: "impossible"), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RuleRungNotOffered, refusal!.Rule);
    }

    [Fact]
    public void An_offered_tail_step_passes_group_2()
    {
        var (refusal, _, plan) = DelveStart.Run(Request(rungIdOrTailLabel: "1"), Catalog(Domain()), Live());
        Assert.Null(refusal);
        Assert.NotNull(plan);
    }

    [Fact]
    public void Oath_requested_on_an_already_mandatory_permadeath_rung_refuses_oath_implied()
    {
        var (refusal, _, _) = DelveStart.Run(Request(rungIdOrTailLabel: "very-hard", oath: true), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RuleOathImplied, refusal!.Rule);
    }

    [Fact]
    public void Oath_requested_below_the_gate_is_legal()
    {
        var (refusal, _, plan) = DelveStart.Run(Request(rungIdOrTailLabel: "medium", oath: true), Catalog(Domain()), Live());
        Assert.Null(refusal);
        Assert.NotNull(plan);
    }

    [Fact]
    public void A_raid_mode_the_layout_does_not_offer_refuses_raid_mode_not_offered()
    {
        var (refusal, _, _) = DelveStart.Run(Request(raidMode: "quad"), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RuleRaidModeNotOffered, refusal!.Rule);
    }

    [Fact]
    public void An_unknown_raid_mode_shape_refuses_party_shape()
    {
        var (refusal, _, _) = DelveStart.Run(
            Request(raidMode: "solo"), Catalog(Domain()),
            Live(raidModesForLayout: _ => new[] { "solo" }, partyShapeForRaidMode: _ => null));
        Assert.Equal(DelveStart.RulePartyShape, refusal!.Rule);
    }

    [Fact]
    public void Too_many_members_for_the_raid_mode_refuses_party_shape()
    {
        var (refusal, _, _) = DelveStart.Run(
            Request(members: new[] { "a1", "a2", "a3", "a4", "a5" }), Catalog(Domain()),
            Live(partyShapeForRaidMode: _ => (Parties: 1, SquadSlots: 4)));
        Assert.Equal(DelveStart.RulePartyShape, refusal!.Rule);
    }

    [Fact]
    public void Zero_members_refuses_party_shape()
    {
        var (refusal, _, _) = DelveStart.Run(Request(members: Array.Empty<string>()), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RulePartyShape, refusal!.Rule);
    }

    // ---- group 3: member availability ---------------------------------------------------------------

    [Theory]
    [InlineData("notOwned")]
    [InlineData("recovering")]
    [InlineData("onExpedition")]
    [InlineData("inAnotherDelve")]
    public void Every_member_unavailability_reason_refuses_member_unavailable(string reason)
    {
        var live = reason switch
        {
            "notOwned" => Live(memberIsOwnedRosterBound: _ => false),
            "recovering" => Live(memberIsRecovering: _ => true),
            "onExpedition" => Live(memberIsOnExpedition: _ => true),
            "inAnotherDelve" => Live(memberIsInAnotherActiveDelve: _ => true),
            _ => throw new InvalidOperationException(),
        };
        var (refusal, _, _) = DelveStart.Run(Request(), Catalog(Domain()), live);
        Assert.Equal($"{DelveStart.RuleMemberUnavailable}:actor-1", refusal!.Rule);
    }

    // ---- group 4: pack provisioning and price --------------------------------------------------------

    [Fact]
    public void An_over_provisioned_pack_refuses_pack_invalid()
    {
        var oversized = new[] { new PackItem("supply", "ration", null, Qty: 1, W: 99, H: 99, GrantIndex: 0, PackItemOrigin.CarryIn) };
        var (refusal, _, _) = DelveStart.Run(Request(carryIn: oversized), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RulePackInvalid, refusal!.Rule);
    }

    [Fact]
    public void Insufficient_bank_souls_refuses_souls_insufficient()
    {
        var (refusal, _, _) = DelveStart.Run(
            Request(), Catalog(Domain()), Live(provisioningPriceFor: _ => 500, soulBalanceFor: _ => 10));
        Assert.Equal(DelveStart.RuleSoulsInsufficient, refusal!.Rule);
    }

    [Fact]
    public void Nothing_is_debited_by_a_passing_run_the_plan_only_carries_the_price()
    {
        // "nothing debited yet" (spec §6 step 4, verbatim) -- there is no debit call anywhere in this
        // file (structurally: DelveStartLive has no "debit" delegate at all), so the only way this
        // property could be violated is the plan itself performing a write, which it cannot: it is a
        // plain record.
        var (_, _, plan) = DelveStart.Run(Request(), Catalog(Domain()), Live(provisioningPriceFor: _ => 50));
        Assert.Equal(50, plan!.ProvisioningPriceSouls);
    }

    // ---- group 6: seed, graph roll, object preflight ---------------------------------------------

    [Fact]
    public void A_graph_or_object_refusal_refuses_delve_graph_or_objects()
    {
        var (refusal, _, _) = DelveStart.Run(
            Request(), Catalog(Domain()), Live(rollAndPreflight: (_, _, _) => (null, "no key room reaches the gated door")));
        Assert.Equal(DelveStart.RuleGraphOrObjects, refusal!.Rule);
        Assert.Equal("no key room reaches the gated door", refusal.Detail);
    }

    [Fact]
    public void The_sealed_seed_reaches_the_plan_unchanged()
    {
        var (_, _, plan) = DelveStart.Run(Request(), Catalog(Domain()), Live());
        Assert.Equal(12345UL, plan!.Seed);
    }

    // ---- ordering: an earlier group's refusal wins over a later group's own failure ----------------

    [Fact]
    public void Group_1_domain_not_found_wins_over_a_group_2_bad_rung_that_would_also_fail()
    {
        // "impossible" is not offered AND the domain does not exist -- domain.not-found must win.
        var (refusal, _, _) = DelveStart.Run(
            Request(domainId: "domain.unknown", rungIdOrTailLabel: "impossible"), Catalog(Domain()), Live());
        Assert.Equal(DelveStart.RuleDomainNotFound, refusal!.Rule);
    }

    [Fact]
    public void Group_2_rung_not_offered_wins_over_a_group_3_unavailable_member_that_would_also_fail()
    {
        var (refusal, _, _) = DelveStart.Run(
            Request(rungIdOrTailLabel: "impossible"), Catalog(Domain()), Live(memberIsRecovering: _ => true));
        Assert.Equal(DelveStart.RuleRungNotOffered, refusal!.Rule);
    }

    [Fact]
    public void Group_3_member_unavailable_wins_over_a_group_4_pack_failure_that_would_also_fail()
    {
        var oversized = new[] { new PackItem("supply", "ration", null, Qty: 1, W: 99, H: 99, GrantIndex: 0, PackItemOrigin.CarryIn) };
        var (refusal, _, _) = DelveStart.Run(
            Request(carryIn: oversized), Catalog(Domain()), Live(memberIsRecovering: _ => true));
        Assert.Equal($"{DelveStart.RuleMemberUnavailable}:actor-1", refusal!.Rule);
    }

    [Fact]
    public void Group_4_souls_insufficient_wins_over_a_group_6_graph_refusal_that_would_also_fail()
    {
        var (refusal, _, _) = DelveStart.Run(
            Request(), Catalog(Domain()),
            Live(soulBalanceFor: _ => 0, rollAndPreflight: (_, _, _) => (null, "would also fail")));
        Assert.Equal(DelveStart.RuleSoulsInsufficient, refusal!.Rule);
    }

    // ---- null-arg guards ----------------------------------------------------------------------------

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DelveStart.Run(null!, Catalog(Domain()), Live()));
        Assert.Throws<ArgumentNullException>(() => DelveStart.Run(Request(), null!, Live()));
        Assert.Throws<ArgumentNullException>(() => DelveStart.Run(Request(), Catalog(Domain()), null!));
    }
}
