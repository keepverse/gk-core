using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

// Empire-development Task 1.3a (spec-relic-item-kind.md §Testing strategy) — the relic as a new
// sibling item kind: DropEntryKind.Relic (10th member), ContainerKind.Relic (12th member), and the
// MintRelic arm that persists to rpg_item only, never item_generation.
public class RelicTests
{
    // ---- closed vocabularies (pinned with reason: the code owns these enums) ---------------------

    [Fact]
    public void DropEntryKind_has_ten_members_with_Relic_after_Unique()
    {
        var values = Enum.GetValues<DropEntryKind>();
        // pin: closed-vocabulary DropEntryKind — a new drop entry kind is a reviewed change
        Assert.Equal(10, values.Length);
        Assert.Contains(DropEntryKind.Relic, values);
        Assert.Contains(DropEntryKind.Unique, values);
    }

    [Fact]
    public void ContainerKind_has_fifteen_members_with_SpeciesProgression_last()
    {
        // 12 -> 14 on the 2026-09-16 merge: achievement-title T3 added EmpireTitle/ActorTitle on its
        // own branch while this one added Relic on ours, and neither program saw the other. Both sets
        // survive; Relic stayed LAST until the next reviewed append.
        // 14 -> 15 (species-progression SP3.2): ContainerKind.SpeciesProgression, the projector's own
        // persisted output (spec-species-layer-projector.md) — appended, never inserted, so Relic's
        // own ordinal (13) is unchanged and SpeciesProgression now takes the LAST slot. What this test
        // actually protects — the enum is append-only because a reorder would re-price every container
        // that names a kind — still holds.
        var values = Enum.GetValues<ContainerKind>();
        // Canonical site (population-pin SE3.3, 2026-09-19): DropVolumeCorpusTests reads this same
        // live enum length instead of a second literal. 15, not solid-enforcement's own 14:
        // species-progression SP3.2's ContainerKind.SpeciesProgression (this file's own comment
        // above, already merged) is a real 15th member at the mega-merge point, so 14 was stale
        // relative to the merged tree, not a live re-read of a smaller enum.
        // pin: closed-vocabulary ContainerKind — a new container kind is a reviewed change
        Assert.Equal(15, values.Length);
        Assert.Contains(ContainerKind.Relic, values);
        Assert.Contains(ContainerKind.SpeciesProgression, values);
        Assert.Equal((int)ContainerKind.Relic, 13);
        Assert.Equal(ContainerKind.SpeciesProgression, values[^1]);
    }

    [Fact]
    public void Relic_container_kind_requires_the_relic_id_prefix()
    {
        Assert.Equal("relic", ContainerRow.PrefixOf(ContainerKind.Relic));
    }

    // NOTE: the KindSpec itself lives in seedsmith (`tools/seedsmith/.../items/kinds.py`, refs={})
    // with its C# mirror in `gk-forge/tools/ItemSeedValidator/Registries/KindCatalog.cs`. Both are pinned by
    // their own suites — `gk-forge/tools/seedsmith/tests/test_relic_kinds.py` (incl. the spec-named
    // `a_relic_kindspec_has_no_reference_fields`) and
    // `gk-forge/tests/FusionRpg.ItemSeedValidator.Tests/RelicKindTests.cs` — not here, since this project
    // references neither registry.

    [Fact]
    public void A_relic_container_id_carries_the_relic_prefix()
    {
        var container = new ContainerRow { ContainerId = "relic.test-cost-token", Kind = ContainerKind.Relic };
        var check = ContainerValidator.Validate(container, _ => null, _ => null);
        Assert.True(check.IsOk, check.ToString());
    }

    [Fact]
    public void A_relic_kind_with_a_non_relic_prefix_is_refused()
    {
        var container = new ContainerRow { ContainerId = "item.test-cost-token", Kind = ContainerKind.Relic };
        var check = ContainerValidator.Validate(container, _ => null, _ => null);
        Assert.False(check.IsOk);
        Assert.Contains("relic.", check.Detail, StringComparison.Ordinal);
    }

    // ---- UnavailableKinds history (plan acceptance bullet 5) --------------------------------------

    [Fact]
    public void drop_entry_kind_relic_is_available_now_that_mint_relic_exists()
    {
        // Mirrors Unique's own pre-MintUnique history in UnavailableKinds (DropTableModel.cs): Relic
        // never entered the refusal dictionary because its MintRelic arm shipped in the same task
        // that registered the enum member — the same end state Unique reached when D4.27 removed it.
        Assert.True(DropTableDraw.IsAvailable(DropEntryKind.Relic));
        Assert.False(DropTableDraw.UnavailableKinds.ContainsKey(DropEntryKind.Relic));
    }

    // ---- corpus parser gate ----------------------------------------------------------------------

    [Fact]
    public void LootCorpusReader_parses_a_relic_entry_kind()
    {
        Assert.True(LootCorpusReader.TryKind("relic", out var kind));
        Assert.Equal(DropEntryKind.Relic, kind);
        Assert.Equal("relic", LootCorpusReader.KindName(DropEntryKind.Relic));
    }

    // ---- MintRelic arm ----------------------------------------------------------------------------

    static LootContentView RelicView(string refId, Func<LootGrant, LootMintResult>? mintRelic = null)
    {
        var entry = new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Relic, RefId: refId, Weight: 1000);
        var table = new DropTableRow(
            TableId: "t-relic", SourceAllow: new[] { "web" }, MinIlvl: null, MaxIlvl: null,
            Enabled: true, Revision: 1,
            Groups: new[] { new DropTableGroupRow("g1", Seq: 0, Rolls: 1, Entries: new[] { entry }) });
        var source = new LootSourceRow("web-wave", "s1", "t-relic", ContentLevel: 20);
        return new LootContentView(
            new Dictionary<string, LootSourceRow> { [source.Key] = source },
            new Dictionary<string, DropTableRow> { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            (_, _) => Array.Empty<string>(),
            MintRelic: mintRelic);
    }

    static LootGrant ResolveOneRelicGrant(LootContentView view, ulong seed = 0xA11CE)
    {
        var request = new LootRequest("player-1", "web-wave", "s1", seed, ThetaActor: 20);
        Assert.True(LootPipeline.Resolve(request, view, DropVolumeTests.Tuning(), LootPityState.Empty, out var m).IsOk,
            "relic resolve failed");
        var grant = Assert.Single(m!.Grants);
        Assert.Equal(DropEntryKind.Relic, grant.Kind);
        return grant;
    }

    [Fact]
    public void A_relic_draw_carries_no_base_type_role_frame_or_rarity()
    {
        var grant = ResolveOneRelicGrant(RelicView("relic.test-cost-token"));
        Assert.Equal("relic.test-cost-token", grant.RefId);
        Assert.Null(grant.BaseTypeId);
        Assert.Null(grant.Frame);
        Assert.Null(grant.Role);
        Assert.Null(grant.RarityId);
        Assert.NotEqual(0UL, grant.RollSeed);
    }

    [Fact]
    public void A_relic_draw_reaches_MintRelic_and_carries_back_the_minted_instance_id()
    {
        var seen = new List<LootGrant>();
        var view = RelicView("relic.test-cost-token", g =>
        {
            seen.Add(g);
            return new LootMintResult(AtomRejection.Ok, $"inst-{g.RefId}");
        });
        var grant = ResolveOneRelicGrant(view);
        var minted = Assert.Single(seen);
        Assert.Equal(DropEntryKind.Relic, minted.Kind);
        Assert.Null(minted.BaseTypeId);
        Assert.Equal("inst-relic.test-cost-token", grant.InstanceId);
    }

    [Fact]
    public void A_relic_draw_without_MintRelic_still_resolves_unminted()
    {
        // Unlike Unique (which refuses without its rarity/base-type resolvers — the grant would be
        // unpersistable), a relic grant needs no resolver-derived field, so a null MintRelic host
        // leaves the grant resolved but unminted rather than refusing.
        var grant = ResolveOneRelicGrant(RelicView("relic.test-cost-token"));
        Assert.Null(grant.InstanceId);
    }

    [Fact]
    public void A_relic_draw_with_an_empty_ref_is_refused_by_name()
    {
        var view = RelicView("");
        var request = new LootRequest("player-1", "web-wave", "s1", 0xA11CE, ThetaActor: 20);
        var rejection = LootPipeline.Resolve(request, view, DropVolumeTests.Tuning(), LootPityState.Empty, out var m);
        Assert.False(rejection.IsOk);
        Assert.Contains("drop.missing-ref", rejection.Detail, StringComparison.Ordinal);
        Assert.Null(m);
    }

    [Fact]
    public void Same_seed_and_ref_id_reproduce_the_same_relic_roll_seed()
    {
        var a = ResolveOneRelicGrant(RelicView("relic.test-cost-token"), seed: 42);
        var b = ResolveOneRelicGrant(RelicView("relic.test-cost-token"), seed: 42);
        Assert.Equal(a.RollSeed, b.RollSeed);
    }

    // ---- the seven real SourceKinds (spec §Design 4, success criterion 3) --------------------------

    [Theory]
    [InlineData("web-wave", "wave-7")]
    [InlineData("expedition-tier", "tier-3")]
    [InlineData("world-sector", "sector-9")]
    [InlineData("dungeon-room", "d1:0:0")]
    [InlineData("dungeon-clear", "domain.a")]
    [InlineData("dungeon-quest", "d1:quest:q1")]
    [InlineData("siege-assault", "sector-9:12")]
    public void any_of_the_seven_known_source_kinds_can_carry_a_relic_entry(string sourceKind, string sourceId)
    {
        var entry = new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Relic, RefId: "relic.test-cost-token", Weight: 5);
        var table = new DropTableRow(
            TableId: "t-seven", SourceAllow: new[] { "web" }, MinIlvl: null, MaxIlvl: null,
            Enabled: true, Revision: 1,
            Groups: new[] { new DropTableGroupRow("g1", Seq: 0, Rolls: 1, Entries: new[] { entry }) });
        var source = new LootSourceRow(sourceKind, sourceId, "t-seven", ContentLevel: 20);
        var view = new LootContentView(
            new Dictionary<string, LootSourceRow> { [source.Key] = source },
            new Dictionary<string, DropTableRow> { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            (_, _) => Array.Empty<string>());

        var check = DropTableValidator.Validate(
            new[] { source }, new[] { table }, DropVolumeTests.Tuning());
        Assert.True(check.IsOk, check.ToString());

        var request = new LootRequest("player-1", sourceKind, sourceId, 0xA11CE, ThetaActor: 20);
        Assert.True(LootPipeline.Resolve(request, view, DropVolumeTests.Tuning(), LootPityState.Empty, out var m).IsOk,
            $"relic resolve failed for {sourceKind}");
        var grant = Assert.Single(m!.Grants);
        Assert.Equal(DropEntryKind.Relic, grant.Kind);
    }

    [Fact]
    public void pvz_run_still_refuses_regardless_of_entry_kind()
    {
        var entry = new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Relic, RefId: "relic.test-cost-token", Weight: 5);
        var table = new DropTableRow(
            TableId: "t-pvz", SourceAllow: new[] { "web" }, MinIlvl: null, MaxIlvl: null,
            Enabled: true, Revision: 1,
            Groups: new[] { new DropTableGroupRow("g1", Seq: 0, Rolls: 1, Entries: new[] { entry }) });
        var source = new LootSourceRow("pvz-run", "run-1", "t-pvz", ContentLevel: 20);
        var view = new LootContentView(
            new Dictionary<string, LootSourceRow> { [source.Key] = source },
            new Dictionary<string, DropTableRow> { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            (_, _) => Array.Empty<string>());

        var request = new LootRequest("player-1", "pvz-run", "run-1", 0xA11CE, ThetaActor: 20);
        var rejection = LootPipeline.Resolve(request, view, DropVolumeTests.Tuning(), LootPityState.Empty, out var m);
        Assert.False(rejection.IsOk);
        Assert.Contains("drop.source-kind-undesigned", rejection.Detail, StringComparison.Ordinal);
        Assert.Null(m);
    }

    [Fact]
    public void a_relic_entry_disabled_or_out_of_ilvl_band_draws_at_weight_zero()
    {
        // Reuses DropTableDraw.EffectiveWeight's existing, unmodified logic — no relic-specific branch.
        var disabled = new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Relic, RefId: "relic.x", Weight: 100, Enabled: false);
        Assert.Equal(0, DropTableDraw.EffectiveWeight(disabled, itemLevel: 20));

        var banded = new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Relic, RefId: "relic.x", Weight: 100, MinIlvl: 50);
        Assert.Equal(0, DropTableDraw.EffectiveWeight(banded, itemLevel: 20));
        Assert.Equal(100, DropTableDraw.EffectiveWeight(banded, itemLevel: 60));
    }
}
