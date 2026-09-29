using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// The twelve ordered steps plus 5a (`drop-volume`, item module 11), driven end to end over the REAL
/// shipped loot corpus, the REAL ten-rung ladder and the REAL 560-row base-type corpus.
/// </summary>
public class LootPipelineTests
{
    static DropVolumeTuning Tuning() => DropVolumeTests.Tuning();

    static LootContentView View(
        Func<string, string, string?>? recorded = null,
        Func<string, string, string, bool>? firstClear = null,
        Func<string, int, int, int>? drawable = null,
        Func<LootGrant, LootMintResult>? mint = null)
    {
        var corpus = DropVolumeCorpusTests.Corpus();
        var baseTypes = DropVolumeCorpusTests.BaseTypes();
        return new LootContentView(
            corpus.Sources.ToDictionary(s => s.Key, StringComparer.Ordinal),
            corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal),
            DropVolumeCorpusTests.Ladder(),
            (frame, role) => baseTypes.TryGetValue((frame, role), out var l)
                ? l
                : (IReadOnlyList<string>)Array.Empty<string>(),
            firstClear, recorded, drawable, mint);
    }

    static LootRequest Request(string kind = "web-wave", string id = "rift-warband", ulong seed = 0xA11CE, int theta = 20) =>
        new("player-1", kind, id, seed, theta);

    // ---- steps 0-2: the event, the gate, the seal -------------------------------------------------

    [Fact]
    public void The_correlation_id_is_server_derived()
    {
        // LootRequest has no correlation field at all — there is no client-reachable knob. Verified
        // structurally, not by inspection.
        var props = typeof(LootRequest).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("CorrelationId", props);

        Assert.Equal("loot:rift-warband", LootCorrelation.Derive("web-wave", "rift-warband"));
        Assert.Equal("loot:exp:warpath-20h", LootCorrelation.Derive("expedition-tier", "warpath-20h"));
        Assert.Throws<ArgumentException>(() => LootCorrelation.Derive("made-up", "x"));

        // D3.10 (spec-dungeon-loot.md §1): the three delve source kinds, added the same day.
        Assert.Equal("loot:delve:d42:3:5", LootCorrelation.Derive("dungeon-room", "d42:3:5"));
        Assert.Equal("loot:delve:domain.fire-shallow-001:clear", LootCorrelation.Derive("dungeon-clear", "domain.fire-shallow-001"));
        Assert.Equal("loot:delve:d42:quest:q7", LootCorrelation.Derive("dungeon-quest", "d42:quest:q7"));

        Assert.True(LootPipeline.Resolve(Request(), View(), Tuning(), LootPityState.Empty, out var m).IsOk);
        Assert.Equal("loot:rift-warband", m!.CorrelationId);
    }

    // ---- D3.10: the three new delve source kinds ---------------------------------------------------

    [Theory]
    [InlineData("dungeon-room", "d1:0:0", "loot:delve:d1:0:0")]
    [InlineData("dungeon-room", "d1:5:9", "loot:delve:d1:5:9")]
    [InlineData("dungeon-clear", "domain.a", "loot:delve:domain.a:clear")]
    [InlineData("dungeon-clear", "domain.b", "loot:delve:domain.b:clear")]
    [InlineData("dungeon-quest", "d1:quest:q1", "loot:delve:d1:quest:q1")]
    [InlineData("dungeon-quest", "d2:quest:q9", "loot:delve:d2:quest:q9")]
    public void Each_delve_source_kind_round_trips_its_own_correlation_shape(string kind, string sourceId, string expected)
    {
        Assert.Equal(expected, LootCorrelation.Derive(kind, sourceId));
        // Same (kind, sourceId) twice reproduces the identical correlation -- deterministic, not just
        // formatted correctly once (spec §9: "server-derived correlation" implies reproducibility).
        Assert.Equal(LootCorrelation.Derive(kind, sourceId), LootCorrelation.Derive(kind, sourceId));
    }

    [Fact]
    public void All_three_delve_source_kinds_are_now_known_to_the_validator()
    {
        Assert.Contains("dungeon-room", DropTableValidator.KnownSourceKinds);
        Assert.Contains("dungeon-clear", DropTableValidator.KnownSourceKinds);
        Assert.Contains("dungeon-quest", DropTableValidator.KnownSourceKinds);
    }

    [Fact]
    public void Distinct_rooms_in_the_same_delve_never_collide_on_correlation()
    {
        var a = LootCorrelation.Derive("dungeon-room", "d1:2:3");
        var b = LootCorrelation.Derive("dungeon-room", "d1:2:4");
        var c = LootCorrelation.Derive("dungeon-room", "d1:3:3");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void A_retry_mints_nothing()
    {
        const string stored = "[{\"Index\":0}]";
        var view = View(recorded: (_, _) => stored);

        Assert.True(LootPipeline.Resolve(Request(), view, Tuning(), new LootPityState(3, 4), out var m).IsOk);
        Assert.True(m!.Replayed);
        Assert.Equal(stored, m.ReplayedResultJson);
        Assert.Empty(m.Grants);
        Assert.Equal(m.PityIn, m.PityOut);   // no counter advanced
    }

    [Fact]
    public void Replay_within_one_revision_pair_is_identical()
    {
        var view = View();
        var request = Request(seed: 0xDEADBEEF);

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var a).IsOk);
        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var b).IsOk);

        Assert.Equal(JsonSerializer.Serialize(a!.Grants), JsonSerializer.Serialize(b!.Grants));
        Assert.Equal(a.LootSeed, b.LootSeed);
        Assert.Equal(a.ItemLevel, b.ItemLevel);
        Assert.Equal(a.ContextJson, b.ContextJson);
    }

    // ---- step 3: item level reads content, never the player --------------------------------------

    [Fact]
    public void Item_level_never_reads_the_player()
    {
        var view = View();
        var tuning = Tuning();

        // Same source, same seed, wildly different Θ — the item level cannot move.
        Assert.True(LootPipeline.Resolve(Request(theta: 1), view, tuning, LootPityState.Empty, out var low).IsOk);
        Assert.True(LootPipeline.Resolve(Request(theta: 9999), view, tuning, LootPityState.Empty, out var high).IsOk);
        Assert.Equal(low!.ItemLevel, high!.ItemLevel);

        // And it tracks the CONTENT: a deeper wave gives a higher level, within ±1 jitter.
        Assert.True(LootPipeline.Resolve(Request(id: "rift-skirmish"), view, tuning, LootPityState.Empty, out var shallow).IsOk);
        Assert.True(LootPipeline.Resolve(Request(id: "rift-tyrant"), view, tuning, LootPityState.Empty, out var deep).IsOk);
        Assert.InRange(shallow!.ItemLevel, 1, 2);
        Assert.InRange(deep!.ItemLevel, 9, 11);
    }

    [Fact]
    public void The_jitter_is_minus_one_flat_plus_one_in_the_authored_proportions()
    {
        var t = Tuning();
        var counts = new Dictionary<int, int> { [-1] = 0, [0] = 0, [1] = 0 };
        for (var i = 0; i < 20_000; i++)
            counts[LootPipeline.ItemLevel(50, (ulong)i, t) - 50]++;

        // 150 / 600 / 250 per mille.
        Assert.InRange(counts[-1], 2_600, 3_400);
        Assert.InRange(counts[0], 11_400, 12_600);
        Assert.InRange(counts[1], 4_500, 5_500);

        Assert.Equal(1, LootPipeline.ItemLevel(1, 0UL, t) is >= 1 ? 1 : 0);  // max(1, ...) holds
        Assert.Equal(1, LootPipeline.LevelReq(3));
        Assert.Equal(8, LootPipeline.LevelReq(10));
    }

    [Fact]
    public void A_pvz_run_loot_source_is_refused_by_name()
    {
        var corpus = DropVolumeCorpusTests.Corpus();
        var baseTypes = DropVolumeCorpusTests.BaseTypes();
        var sources = corpus.Sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
        sources["pvz-run:run-42"] = new LootSourceRow("pvz-run", "run-42", "drop.pvz.run", 5);

        var view = new LootContentView(
            sources, corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal),
            DropVolumeCorpusTests.Ladder(),
            (f, r) => baseTypes.TryGetValue((f, r), out var l) ? l : (IReadOnlyList<string>)Array.Empty<string>());

        var result = LootPipeline.Resolve(
            Request("pvz-run", "run-42"), view, Tuning(), LootPityState.Empty, out var m);

        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.source-kind-undesigned", result.Detail, StringComparison.Ordinal);
        Assert.Null(m);
    }

    // ---- step 5a / 5: volume, and the nesting rule ------------------------------------------------

    [Fact]
    public void More_theta_yields_more_items_and_nothing_saturates()
    {
        var view = View();
        var t = Tuning();

        long Yield(int theta)
        {
            long n = 0;
            for (var s = 0UL; s < 400; s++)
            {
                Assert.True(LootPipeline.Resolve(
                    new LootRequest("p", "expedition-tier", "warpath-20h", s, theta), view, t,
                    LootPityState.Empty, out var m).IsOk);
                n += m!.Grants.Count(g => g.Kind == DropEntryKind.Equipment);
            }

            return n;
        }

        var pin = Yield(20);
        var mid = Yield(200);
        var far = Yield(2000);

        Assert.True(mid > pin);
        Assert.True(far > mid);
        // No ceiling: the far-veteran yield is an order of magnitude above the pin's.
        Assert.True(far > pin * 10, $"pin={pin} far={far} — a cap would show up as saturation here");
    }

    [Fact]
    public void A_nested_table_draws_its_own_rolls_and_does_not_compound_theta()
    {
        // The shared slate is nested one level deep. Compounding Θ per level would make the yield
        // quadratic in Θ — exactly the shape D18 refuses.
        var view = View();
        var t = Tuning();
        var byId = DropVolumeCorpusTests.Corpus().Tables.ToDictionary(x => x.TableId, StringComparer.Ordinal);

        long Observed(int theta)
        {
            long n = 0;
            for (var s = 0UL; s < 2000; s++)
            {
                Assert.True(LootPipeline.Resolve(
                    new LootRequest("p", "web-wave", "rift-warband", s, theta), view, t,
                    LootPityState.Empty, out var m).IsOk);
                n += m!.Grants.Count(g => g.Kind == DropEntryKind.Equipment);
            }

            return n;
        }

        var expectedPin = DropTableDraw.ExpectedEquipmentPerMille(byId["drop.web.wave-normal"], 3,
            DropVolume.VolumeScaleMilli(20, t), id => byId.TryGetValue(id, out var x) ? x : null) * 2000 / 1000;
        var expectedHigh = DropTableDraw.ExpectedEquipmentPerMille(byId["drop.web.wave-normal"], 3,
            DropVolume.VolumeScaleMilli(100, t), id => byId.TryGetValue(id, out var x) ? x : null) * 2000 / 1000;

        // Linear, not quadratic: the ratio of observed yields tracks the ratio of SCALES.
        Assert.InRange(Observed(20), expectedPin * 88 / 100, expectedPin * 112 / 100);
        Assert.InRange(Observed(100), expectedHigh * 88 / 100, expectedHigh * 112 / 100);
    }

    // ---- steps 6-10 -------------------------------------------------------------------------------

    [Fact]
    public void Smart_loot_is_off_and_the_draw_is_uniform_over_legal_base_types()
    {
        // Written to FLIP when X1 (frame-classify) and X4 land.
        var view = View();
        var t = Tuning();
        var frames = new Dictionary<string, int>(StringComparer.Ordinal);
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var s = 0UL; s < 4000; s++)
        {
            Assert.True(LootPipeline.Resolve(
                new LootRequest("p", "web-wave", "rift-tyrant", s, 20), view, t,
                LootPityState.Empty, out var m).IsOk);
            foreach (var g in m!.Grants.Where(g => g.Kind == DropEntryKind.Equipment && g.Frame is not null))
            {
                frames[g.Frame!] = frames.GetValueOrDefault(g.Frame!) + 1;
                roles[g.Role!] = roles.GetValueOrDefault(g.Role!) + 1;
            }
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal(12, roles.Count);

        // Uniform: no frame and no role is favoured. (A frame-weighted draw would break this, which
        // is the point — the test flips when smart loot lands.)
        var total = frames.Values.Sum();
        foreach (var n in frames.Values) Assert.InRange(n, total * 45 / 100, total * 55 / 100);
        foreach (var n in roles.Values) Assert.InRange(n, total * 6 / 100, total * 11 / 100);

        // And the context records the deferral as a replay input.
        Assert.True(LootPipeline.Resolve(Request(), view, t, LootPityState.Empty, out var one).IsOk);
        using var ctx = JsonDocument.Parse(one!.ContextJson);
        Assert.False(ctx.RootElement.GetProperty("smartLoot").GetBoolean());
    }

    // ---- D3.11: LootPipeline's own RefId/BaseTypeSetFor arm (spec-dungeon-loot.md §3) --------------

    static LootContentView MinimalEquipmentView(string refId, Func<string, IReadOnlyList<string>>? baseTypeSetFor)
    {
        var entry = new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Equipment, RefId: refId, Weight: 1000, Frame: "light", Role: "helm");
        var table = new DropTableRow(
            TableId: "t1", SourceAllow: Array.Empty<string>(), MinIlvl: null, MaxIlvl: null,
            Enabled: true, Revision: 1,
            Groups: new[] { new DropTableGroupRow("g1", Seq: 0, Rolls: 1, Entries: new[] { entry }) });
        var source = new LootSourceRow("web-wave", "s1", "t1", ContentLevel: 20);

        IReadOnlyList<string> baseTypesFor(string frame, string role) =>
            frame == "light" && role == "helm" ? new[] { "helm-a", "helm-b", "helm-c" } : Array.Empty<string>();

        return new LootContentView(
            new Dictionary<string, LootSourceRow> { [source.Key] = source },
            new Dictionary<string, DropTableRow> { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            baseTypesFor,
            BaseTypeSetFor: baseTypeSetFor);
    }

    static LootGrant ResolveOneEquipmentGrant(LootContentView view, ulong seed = 1)
    {
        Assert.True(LootPipeline.Resolve(
            new LootRequest("p", "web-wave", "s1", seed, ThetaActor: 20), view, Tuning(),
            LootPityState.Empty, out var m).IsOk);
        return Assert.Single(m!.Grants);
    }

    [Fact]
    public void A_null_BaseTypeSetFor_leaves_the_frame_role_set_untouched_even_with_a_RefId_authored()
    {
        var view = MinimalEquipmentView(refId: "helm-set-a", baseTypeSetFor: null);
        for (ulong seed = 0; seed < 10; seed++)
            Assert.Contains(ResolveOneEquipmentGrant(view, seed).BaseTypeId, new[] { "helm-a", "helm-b", "helm-c" });
    }

    [Fact]
    public void An_empty_RefId_ignores_a_supplied_BaseTypeSetFor()
    {
        var view = MinimalEquipmentView(refId: "", baseTypeSetFor: _ => new[] { "helm-b" });
        for (ulong seed = 0; seed < 10; seed++)
            Assert.Contains(ResolveOneEquipmentGrant(view, seed).BaseTypeId, new[] { "helm-a", "helm-b", "helm-c" });
    }

    [Fact]
    public void A_populated_RefId_with_a_resolver_narrows_the_draw_to_the_domain_set()
    {
        var view = MinimalEquipmentView(refId: "helm-set-a", baseTypeSetFor: refId =>
            refId == "helm-set-a" ? new[] { "helm-b" } : Array.Empty<string>());
        for (ulong seed = 0; seed < 20; seed++)
            Assert.Equal("helm-b", ResolveOneEquipmentGrant(view, seed).BaseTypeId);
    }

    [Fact]
    public void The_intersection_is_a_real_intersection_not_a_replacement()
    {
        // The domain set includes an id that is NOT in the frame/role's own legal set -- it must never
        // be drawn, proving this narrows `legal`, it does not just substitute the resolver's own list.
        var view = MinimalEquipmentView(refId: "helm-set-a", baseTypeSetFor: _ => new[] { "helm-b", "not-a-real-helm" });
        for (ulong seed = 0; seed < 20; seed++)
            Assert.Equal("helm-b", ResolveOneEquipmentGrant(view, seed).BaseTypeId);
    }

    [Fact]
    public void A_RefId_whose_domain_set_shares_nothing_with_the_frame_role_set_refuses()
    {
        var view = MinimalEquipmentView(refId: "helm-set-a", baseTypeSetFor: _ => new[] { "totally-unrelated" });
        var r = LootPipeline.Resolve(
            new LootRequest("p", "web-wave", "s1", SourceSeed: 1, ThetaActor: 20), view, Tuning(),
            LootPityState.Empty, out _);
        Assert.False(r.IsOk);
        Assert.Contains("no-legal-base-type", r.Detail);
    }

    [Fact]
    public void Context_json_carries_smartLoot_and_squadFrameMix_from_the_first_drop()
    {
        var view = View();
        var request = Request() with
        {
            SquadFrameMilli = new Dictionary<string, int> { ["plant"] = 600, ["humanoid"] = 400 },
        };

        Assert.True(LootPipeline.Resolve(request, view, Tuning(), LootPityState.Empty, out var m).IsOk);
        using var doc = JsonDocument.Parse(m!.ContextJson);
        Assert.False(doc.RootElement.GetProperty("smartLoot").GetBoolean());

        var mix = doc.RootElement.GetProperty("squadFrameMix");
        Assert.Equal(600, mix.GetProperty("plant").GetInt32());
        Assert.Equal(400, mix.GetProperty("humanoid").GetInt32());

        // The seal's own inputs are in there too, so a replay is reconstructible from the log alone.
        Assert.Equal(20, doc.RootElement.GetProperty("thetaActor").GetInt32());
        Assert.Equal(1000, doc.RootElement.GetProperty("volumeScaleMilli").GetInt32());
    }

    [Fact]
    public void Affix_channel_is_authored_and_threaded_to_step_9()
    {
        // Provable BEFORE X4 lands: the channel reaches the grant that step 9 mints from.
        var seen = new List<LootGrant>();
        var view = View(mint: g => { seen.Add(g); return new LootMintResult(AtomRejection.Ok, "inst-" + g.Index); });
        var t = Tuning();

        for (var s = 0UL; s < 200; s++)
            Assert.True(LootPipeline.Resolve(
                new LootRequest("p", "web-wave", "rift-tyrant", s, 20), view, t,
                LootPityState.Empty, out _).IsOk);

        Assert.NotEmpty(seen);
        Assert.All(seen, g => Assert.True(AffixChannels.IsKnown(g.AffixChannel)));
        Assert.Contains(seen, g => g.AffixChannel == AffixChannels.Boss);
        Assert.All(seen, g => Assert.Equal("inst-" + g.Index, "inst-" + g.Index));
    }

    [Fact]
    public void Step_10_is_a_documented_no_op_and_reserves_its_stream()
    {
        var view = View();
        var t = Tuning();

        for (var s = 0UL; s < 200; s++)
        {
            Assert.True(LootPipeline.Resolve(
                new LootRequest("p", "web-wave", "rift-tyrant", s, 40), view, t,
                LootPityState.Empty, out var m).IsOk);
            foreach (var g in m!.Grants.Where(g => g.Kind == DropEntryKind.Equipment))
                Assert.Equal(0, g.SocketCount);
        }

        // The stream really is reserved: it derives from the instance's own roll_seed with the name
        // module 16 is written against, so landing the count later moves no other draw.
        Assert.Equal("item.socket", LootStreams.Sockets);
    }

    [Fact]
    public void Sockets_roll_last_and_shift_no_affix()
    {
        // Draining the socket stream leaves every affix-facing decision byte-identical, because the
        // socket stream is derived from roll_seed and shares no state with the pool streams.
        var view = View();
        var t = Tuning();

        Assert.True(LootPipeline.Resolve(Request(id: "rift-tyrant"), view, t, LootPityState.Empty, out var a).IsOk);

        var before = a!.Grants.Select(g => (g.BaseTypeId, g.RarityId, g.PrefixRolls, g.SuffixRolls, g.MinTier, g.MaxTier, g.RollSeed)).ToList();

        foreach (var g in a.Grants.Where(g => g.RollSeed != 0))
        {
            var socket = FusionRpg.Core.Battle.SeededRng.DeriveStream(g.RollSeed, LootStreams.Sockets);
            for (var i = 0; i < 32; i++) socket.NextULong();
        }

        Assert.True(LootPipeline.Resolve(Request(id: "rift-tyrant"), view, t, LootPityState.Empty, out var b).IsOk);
        var after = b!.Grants.Select(g => (g.BaseTypeId, g.RarityId, g.PrefixRolls, g.SuffixRolls, g.MinTier, g.MaxTier, g.RollSeed)).ToList();

        Assert.Equal(before, after);
    }

    [Fact]
    public void A_narrowed_envelope_narrows_rolls_and_is_recorded()
    {
        // Never a rejection of a legal drop from legal content: the COUNT narrows and the log says so.
        var view = View(drawable: (_, _, _) => 1);
        var t = Tuning();

        var narrowedSomewhere = false;
        for (var s = 0UL; s < 300 && !narrowedSomewhere; s++)
        {
            Assert.True(LootPipeline.Resolve(
                new LootRequest("p", "web-wave", "rift-tyrant", s, 20), view, t,
                LootPityState.Empty, out var m).IsOk);
            if (!m!.Notes.Contains(LootPipeline.NoteEnvelopeNarrowed)) continue;

            narrowedSomewhere = true;
            foreach (var g in m.Grants.Where(g => g.Kind == DropEntryKind.Equipment))
            {
                Assert.True(g.PrefixRolls <= 1);
                Assert.True(g.SuffixRolls <= 1);
            }
        }

        Assert.True(narrowedSomewhere, "a one-group pool must narrow SOME rung's two-roll budget");
    }

    [Fact]
    public void Quality_still_reads_p_theta_through_content_scale()
    {
        // The rarity / tier path is untouched by the volume change: the envelope depends on the
        // rung and the ITEM level, never on Θ_actor, and growth past t5 is carried by contentScale.
        var t = Tuning();
        var ladder = DropVolumeCorpusTests.Ladder();
        var heirloom = ladder.First(r => r.RarityId == "heirloom");

        var atLow = DropEnvelope.Resolve(heirloom, 40, new AtomRandom(5UL, "e"));
        var atHigh = DropEnvelope.Resolve(heirloom, 500, new AtomRandom(5UL, "e"));
        Assert.Equal(atLow, atHigh);                              // tier saturates at t5
        Assert.Equal(IlvlTierLadder.MaxTier, atHigh.MaxTier);

        // …and the magnitude keeps growing, through the shipped contentScale, not through a tier.
        var tuning = PowerTuningHub.Tuning;
        Assert.True(ContentScale.Milli(500, tuning) > ContentScale.Milli(40, tuning));
    }

    [Fact]
    public void Tier_ceiling_is_i12s_table_not_i8s()
    {
        // D29 §2: I8's t5@60 "is strictly worse — it delays the last band without adding growth".
        Assert.Equal(new[] { 1, 1, 8, 18, 32 }, IlvlTierLadder.MinIlvlByTier);
        Assert.Equal(2, IlvlTierLadder.MaxTierAt(1));
        Assert.Equal(3, IlvlTierLadder.MaxTierAt(8));
        Assert.Equal(4, IlvlTierLadder.MaxTierAt(18));
        Assert.Equal(5, IlvlTierLadder.MaxTierAt(32));
        Assert.Equal(5, IlvlTierLadder.MaxTierAt(60));
        Assert.Equal(5, IlvlTierLadder.MaxTierAt(100_000));
    }

    // ---- first clear ------------------------------------------------------------------------------

    [Fact]
    public void The_first_clear_grant_fires_once_and_never_rolls()
    {
        var t = Tuning();

        Assert.True(LootPipeline.Resolve(Request(id: "rift-tyrant"), View(), t, LootPityState.Empty, out var first).IsOk);
        Assert.Equal("item.first-clear-almanac-seed", first!.FirstClearGrant);
        var granted = first.Grants.Single(g => g.RefId == "item.first-clear-almanac-seed");
        Assert.Equal(0, granted.PrefixRolls);
        Assert.Equal(0, granted.SuffixRolls);

        var already = View(firstClear: (_, _, _) => true);
        Assert.True(LootPipeline.Resolve(Request(id: "rift-tyrant"), already, t, LootPityState.Empty, out var second).IsOk);
        Assert.Null(second!.FirstClearGrant);
        Assert.DoesNotContain(second.Grants, g => g.RefId == "item.first-clear-almanac-seed");
    }

    // ---- guaranteed groups ------------------------------------------------------------------------

    [Fact]
    public void A_guaranteed_group_that_loses_everything_is_unsatisfiable_not_a_silent_nothing()
    {
        var t = Tuning();
        var table = new DropTableRow("drop.test.guaranteed", new[] { "web" }, null, null, true, 1, new[]
        {
            new DropTableGroupRow("gear", 0, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Equipment, "", 100, Enabled: false,
                    Frame: "plant", Role: "girdle"),
            }),
        });

        var view = new LootContentView(
            new Dictionary<string, LootSourceRow>(StringComparer.Ordinal)
            {
                ["web-wave:test"] = new("web-wave", "test", "drop.test.guaranteed", 5),
            },
            new Dictionary<string, DropTableRow>(StringComparer.Ordinal) { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            (_, _) => new[] { "item.x" });

        var result = LootPipeline.Resolve(Request("web-wave", "test"), view, t, LootPityState.Empty, out _);
        Assert.Equal(AtomRejectionReason.UnsatisfiablePool, result.Reason);
    }

    [Fact]
    public void A_chance_group_that_loses_everything_falls_through_to_nothing()
    {
        var t = Tuning();
        var table = new DropTableRow("drop.test.chance", new[] { "web" }, null, null, true, 1, new[]
        {
            new DropTableGroupRow("gear", 0, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Equipment, "", 100, MinIlvl: 900,
                    Frame: "plant", Role: "girdle"),
                new DropTableEntryRow(1, DropEntryKind.Nothing, "", 900),
            }),
        });

        var view = new LootContentView(
            new Dictionary<string, LootSourceRow>(StringComparer.Ordinal)
            {
                ["web-wave:test"] = new("web-wave", "test", "drop.test.chance", 5),
            },
            new Dictionary<string, DropTableRow>(StringComparer.Ordinal) { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            (_, _) => new[] { "item.x" });

        Assert.True(LootPipeline.Resolve(Request("web-wave", "test"), view, t, LootPityState.Empty, out var m).IsOk);
        Assert.Empty(m!.Grants);   // the ilvl band excluded the only equipment row; `nothing` remains
    }

    [Fact]
    public void Pity_advances_across_the_manifest_and_is_reported()
    {
        var view = View();
        var t = Tuning();
        var pityIn = new LootPityState(5, 6);

        Assert.True(LootPipeline.Resolve(Request(id: "rift-tyrant"), view, t, pityIn, out var m).IsOk);
        var minted = m!.Grants.Count(g => g.Kind == DropEntryKind.Equipment && g.RarityId is not null);
        Assert.True(minted > 0);

        Assert.Equal(pityIn, m.PityIn);
        Assert.True(m.PityOut.ItemsSinceHeirloom <= pityIn.ItemsSinceHeirloom + minted);
        Assert.True(m.PityOut.ItemsSinceSunwoven <= pityIn.ItemsSinceSunwoven + minted);
    }

    // ---- D4.27: the MintUnique arm — "rung = the container's own Rarity, never drawn" ----------------

    static LootContentView MinimalUniqueView(string refId, Func<string, string?>? uniqueRarityFor, Func<LootGrant, LootMintResult>? mint = null,
        Func<string, (string Frame, string BaseTypeId)?>? uniqueBaseTypeFor = null)
    {
        var entry = new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Unique, RefId: refId, Weight: 1000);
        var table = new DropTableRow(
            TableId: "t1", SourceAllow: Array.Empty<string>(), MinIlvl: null, MaxIlvl: null,
            Enabled: true, Revision: 1,
            Groups: new[] { new DropTableGroupRow("g1", Seq: 0, Rolls: 1, Entries: new[] { entry }) });
        var source = new LootSourceRow("web-wave", "s1", "t1", ContentLevel: 20);

        return new LootContentView(
            new Dictionary<string, LootSourceRow> { [source.Key] = source },
            new Dictionary<string, DropTableRow> { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            (_, _) => Array.Empty<string>(), // no equipment draws in this fixture
            UniqueRarityFor: uniqueRarityFor,
            Mint: mint,
            // D3.15/D4.12/D3.11: every test in this section is about RARITY/mint behaviour, not this
            // resolver specifically -- default it to a real, always-resolving pair keyed off the SAME
            // refId the caller already configured, so those tests keep exercising exactly what they
            // already did. The two dedicated resolver tests below override this explicitly.
            UniqueBaseTypeFor: uniqueBaseTypeFor ?? (r => r == refId ? ("humanoid", "test.base-type") : null));
    }

    [Fact]
    public void A_unique_draw_mints_at_the_containers_own_authored_rarity_never_drawn()
    {
        var view = MinimalUniqueView("item.rot-bloom-30-002", refId => refId == "item.rot-bloom-30-002" ? "cultivated" : null);
        var grant = ResolveOneEquipmentGrant(view);

        Assert.Equal(DropEntryKind.Unique, grant.Kind);
        Assert.Equal("item.rot-bloom-30-002", grant.RefId);
        Assert.Equal("cultivated", grant.RarityId);
        var cultivated = DropVolumeCorpusTests.Ladder().Single(r => r.RarityId == "cultivated");
        Assert.Equal(cultivated.Ordinal, grant.RarityOrdinal);
    }

    [Fact]
    public void A_unique_draw_takes_its_own_RollSeed_never_the_flat_zero_the_pre_D427_arm_shipped()
    {
        var view = MinimalUniqueView("item.rot-bloom-30-002", _ => "cultivated");
        var grant = ResolveOneEquipmentGrant(view);
        Assert.NotEqual(0UL, grant.RollSeed);
    }

    [Fact]
    public void A_unique_draw_reaches_Mint_and_carries_back_the_minted_instance_id()
    {
        var view = MinimalUniqueView("item.rot-bloom-30-002", _ => "cultivated",
            mint: g => new LootMintResult(FusionRpg.Core.Effects.Atoms.AtomRejection.Ok, $"inst-{g.RefId}"));
        var grant = ResolveOneEquipmentGrant(view);
        Assert.Equal("inst-item.rot-bloom-30-002", grant.InstanceId);
    }

    [Fact]
    public void A_unique_draw_with_no_UniqueRarityFor_resolver_refuses_naming_the_reason()
    {
        var view = MinimalUniqueView("item.rot-bloom-30-002", uniqueRarityFor: null);
        Assert.True(LootPipeline.Resolve(Request("web-wave", "s1"), view, Tuning(), LootPityState.Empty, out var m)
            is { IsOk: false } r && r.Detail.Contains("drop.unique-rarity-unresolved", StringComparison.Ordinal));
        Assert.Null(m);
    }

    [Fact]
    public void A_unique_draw_whose_resolver_cannot_place_it_refuses_naming_the_ref_id()
    {
        var view = MinimalUniqueView("item.does-not-exist", uniqueRarityFor: _ => null);
        var rejection = LootPipeline.Resolve(Request("web-wave", "s1"), view, Tuning(), LootPityState.Empty, out var m);
        Assert.False(rejection.IsOk);
        Assert.Contains("item.does-not-exist", rejection.Detail);
        Assert.Null(m);
    }

    [Fact]
    public void Same_seed_and_ref_id_reproduce_the_same_unique_roll_seed()
    {
        var view = MinimalUniqueView("item.rot-bloom-30-002", _ => "cultivated");
        var a = ResolveOneEquipmentGrant(view, seed: 42);
        var b = ResolveOneEquipmentGrant(view, seed: 42);
        Assert.Equal(a.RollSeed, b.RollSeed);
    }

    // ---- D3.15/D4.12/D3.11: a unique's own Frame/BaseTypeId now carry onto its LootGrant ------------

    [Fact]
    public void A_unique_draw_carries_its_resolved_frame_and_base_type_onto_the_grant()
    {
        var view = MinimalUniqueView("item.rot-bloom-30-002", _ => "cultivated",
            uniqueBaseTypeFor: r => r == "item.rot-bloom-30-002" ? ("plant", "footing.woody-stem") : null);
        var grant = ResolveOneEquipmentGrant(view);

        Assert.Equal("plant", grant.Frame);
        Assert.Equal("footing.woody-stem", grant.BaseTypeId);
    }

    [Fact]
    public void A_unique_draw_with_no_UniqueBaseTypeFor_resolver_refuses_naming_the_reason()
    {
        // `with { UniqueBaseTypeFor = null }` overrides MinimalUniqueView's own always-resolving
        // default, proving the real "resolver not supplied at all" path -- the identical shape
        // `A_unique_draw_with_no_UniqueRarityFor_resolver_refuses_naming_the_reason` already uses for
        // its own resolver.
        var view = MinimalUniqueView("item.rot-bloom-30-002", uniqueRarityFor: _ => "cultivated") with { UniqueBaseTypeFor = null };

        Assert.True(LootPipeline.Resolve(Request("web-wave", "s1"), view, Tuning(), LootPityState.Empty, out var m)
            is { IsOk: false } r && r.Detail.Contains("drop.unique-base-type-unresolved", StringComparison.Ordinal));
        Assert.Null(m);
    }

    [Fact]
    public void A_unique_draw_whose_base_type_resolver_cannot_place_it_refuses_naming_the_ref_id()
    {
        var view = MinimalUniqueView("item.does-not-exist", uniqueRarityFor: _ => "cultivated",
            uniqueBaseTypeFor: _ => null);
        var rejection = LootPipeline.Resolve(Request("web-wave", "s1"), view, Tuning(), LootPityState.Empty, out var m);
        Assert.False(rejection.IsOk);
        Assert.Contains("item.does-not-exist", rejection.Detail);
        Assert.Contains("drop.unknown-unique", rejection.Detail, StringComparison.Ordinal);
        Assert.Null(m);
    }
}
