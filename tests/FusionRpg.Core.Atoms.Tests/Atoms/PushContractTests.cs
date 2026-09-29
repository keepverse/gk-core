using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E19's contract (spec-compiled-push.md): what the server sends and the injector rebuilds.
///
/// <para>The guarantee under test is the one that makes the Cold/Hot split legal — <b>the injector
/// never holds content rows.</b> Everything travels already-resolved: predicates as flat int ops,
/// values as curve-scaled bounds, status and element names interned away.</para>
///
/// <para>Round-trips are checked <b>by behaviour</b>, not by comparing encoded ops to themselves. A
/// decoded predicate has to answer the same way as the original across a matrix of facts, which is a
/// test the encoder cannot pass by agreeing with its own mistakes.</para>
/// </summary>
public class PushContractTests
{
    static readonly JsonSerializerOptions Wire = new();

    /// <summary>Encode, serialise, deserialise, decode — the whole trip, not an in-memory copy.</summary>
    static RunnerEntry RoundTrip(RunnerEntry entry)
    {
        var json = JsonSerializer.Serialize(AtomPushCodec.Encode(entry), Wire);
        return AtomPushCodec.Decode(JsonSerializer.Deserialize<RunnerEntryDto>(json, Wire)!);
    }

    static ICompiledPredicate Compile(string json, Func<string, int>? statusBit = null)
    {
        Assert.True(AtomJson.TryReadPredicate(JsonDocument.Parse(json).RootElement, out var tree).IsOk);
        Assert.True(PredicateCompiler.TryCompile(tree!, statusBit, out var compiled).IsOk);
        return compiled;
    }

    static RunnerEntry Entry(
        ICompiledPredicate? predicate = null,
        RunnerLimits? limits = null,
        IReadOnlyDictionary<string, ValueBounds>? values = null,
        IReadOnlyDictionary<string, object?>? pars = null) =>
        new("atom.searing-strike.fire.t3",
            "resource.delta",
            AtomTriggers.OnDamageDealt,
            predicate ?? PredicateCompiler.Always,
            250,
            500,
            "atom.searing-strike",
            values ?? new Dictionary<string, ValueBounds>
            {
                ["amount"] = new(-120, -80, RollPolicy.OnApply),
                ["channel"] = new(3, 3, RollPolicy.Fixed),
            },
            limits ?? new RunnerLimits(5, 3, 4, 2),
            pars ?? new Dictionary<string, object?> { ["channel"] = "hp", ["mode"] = "add" });

    // ---- the entry survives the wire ---------------------------------------------------------------

    [Fact]
    public void Every_scalar_on_an_entry_survives_the_round_trip()
    {
        var back = RoundTrip(Entry());

        Assert.Equal("atom.searing-strike.fire.t3", back.AtomId);
        Assert.Equal("resource.delta", back.KindId);
        Assert.Equal(AtomTriggers.OnDamageDealt, back.Trigger);
        Assert.Equal(250, back.ChanceMilli);
        Assert.Equal(500, back.IcdMs);
        Assert.Equal("atom.searing-strike", back.IcdKey);
    }

    [Fact]
    public void The_limits_that_route_an_atom_to_the_runner_survive_the_wire()
    {
        // These are the keys Compilability routes on. E7 already dropped them once between the
        // classifier and the payload; losing them again on the wire would be the same defect with a
        // longer flight time — an atom that caps on the server and mints forever in the game.
        var back = RoundTrip(Entry(limits: new RunnerLimits(5, 3, 4, 2)));

        Assert.Equal(5, back.Limits.CapPerMatch);
        Assert.Equal(3, back.Limits.Charges);
        Assert.Equal(4, back.Limits.EveryHits);
        Assert.Equal(2, back.Limits.MaxStacks);
    }

    [Fact]
    public void Absent_limits_stay_absent_rather_than_becoming_zero()
    {
        var back = RoundTrip(Entry(limits: RunnerLimits.None));

        Assert.Equal(RunnerLimits.None, back.Limits);
        Assert.False(back.Limits.HasCap);
        Assert.False(back.Limits.HasCharges);
    }

    [Fact]
    public void Value_bounds_and_their_roll_policy_survive()
    {
        var back = RoundTrip(Entry());

        Assert.Equal(new ValueBounds(-120, -80, RollPolicy.OnApply), back.Values["amount"]);
        Assert.Equal(new ValueBounds(3, 3, RollPolicy.Fixed), back.Values["channel"]);
    }

    [Fact]
    public void Non_value_params_survive_so_a_dispatch_can_be_built()
    {
        var back = RoundTrip(Entry());

        Assert.Equal("hp", Assert.IsType<JsonElement>(back.Params["channel"]).GetString());
        Assert.Equal("add", Assert.IsType<JsonElement>(back.Params["mode"]).GetString());
    }

    [Fact]
    public void A_missing_icd_key_falls_back_to_the_atom_id_rather_than_empty()
    {
        var dto = AtomPushCodec.Encode(Entry());
        dto.IcdKey = "";

        Assert.Equal("atom.searing-strike.fire.t3", AtomPushCodec.Decode(dto).IcdKey);
    }

    // ---- the predicate answers the same way after the trip ------------------------------------------

    public static IEnumerable<object[]> Predicates()
    {
        yield return new object[] { "{\"leaf\":\"sideIs\",\"subject\":\"target\",\"value\":\"zombie\"}" };
        yield return new object[] { "{\"leaf\":\"hpBelowMilli\",\"subject\":\"target\",\"value\":500}" };
        yield return new object[] { "{\"leaf\":\"typeIdIn\",\"subject\":\"target\",\"value\":[3,7,11]}" };
        yield return new object[]
        {
            "{\"op\":\"and\",\"children\":["
            + "{\"leaf\":\"sideIs\",\"subject\":\"target\",\"value\":\"zombie\"},"
            + "{\"leaf\":\"hpBelowMilli\",\"subject\":\"target\",\"value\":600}]}"
        };
        yield return new object[]
        {
            "{\"op\":\"or\",\"children\":["
            + "{\"leaf\":\"rowIs\",\"subject\":\"self\",\"value\":2},"
            + "{\"leaf\":\"colIs\",\"subject\":\"self\",\"value\":5}]}"
        };
        yield return new object[]
        {
            "{\"op\":\"not\",\"children\":[{\"leaf\":\"isMindControlled\",\"subject\":\"target\",\"value\":true}]}"
        };
        yield return new object[]
        {
            "{\"op\":\"and\",\"children\":["
            + "{\"leaf\":\"actorIsKiller\",\"subject\":\"self\",\"value\":true},"
            + "{\"op\":\"or\",\"children\":["
            + "{\"leaf\":\"typeIdIs\",\"subject\":\"target\",\"value\":12},"
            + "{\"leaf\":\"hpAboveMilli\",\"subject\":\"self\",\"value\":800}]}]}"
        };
    }

    [Theory]
    [MemberData(nameof(Predicates))]
    public void A_decoded_predicate_answers_identically_across_a_fact_matrix(string json)
    {
        var original = Compile(json);
        var decoded = AtomPushCodec.DecodePredicate(
            JsonSerializer.Deserialize<CompiledPredicateDto>(
                JsonSerializer.Serialize(AtomPushCodec.EncodePredicate(original), Wire), Wire));

        var compared = 0;
        var trues = 0;
        foreach (var self in FactMatrix())
        foreach (var target in FactMatrix())
        {
            var a = new FactReader(self, target);
            var b = new FactReader(self, target);
            var expected = original.Evaluate(ref a);
            Assert.Equal(expected, decoded.Evaluate(ref b));
            if (expected) trues++;
            compared++;
        }

        Assert.Equal(64 * 64, compared);

        // Without this the test agrees about nothing: a matrix that never satisfies the predicate
        // would pass identically against a decoder that always answered false.
        Assert.InRange(trues, 1, compared - 1);
    }

    /// <summary>
    /// Deliberately varied and deliberately NOT the values the predicates test for on the nose — a
    /// matrix that only ever produces "false" would agree about nothing.
    /// </summary>
    static IEnumerable<EntityFacts> FactMatrix()
    {
        foreach (var side in new[] { 0, 1 })
        foreach (var typeId in new[] { 3, 12 })
        foreach (var hp in new[] { 250, 900 })
        foreach (var row in new[] { 2, 4 })
        foreach (var col in new[] { 1, 5 })
        foreach (var charmed in new[] { false, true })
            yield return new EntityFacts(side, typeId, hp, -1, row, col, charmed, row == 2, 0UL);
    }

    [Fact]
    public void An_unconditional_entry_carries_no_predicate_at_all()
    {
        // An empty op array and "no condition" would say the same thing twice, and the receiver
        // would have to decide which one meant "always".
        Assert.Null(AtomPushCodec.EncodePredicate(PredicateCompiler.Always));
        Assert.Same(PredicateCompiler.Always, AtomPushCodec.DecodePredicate(null));
    }

    [Fact]
    public void An_interned_status_bit_travels_as_a_bit_not_a_name()
    {
        var original = Compile(
            "{\"leaf\":\"hasStatus\",\"subject\":\"target\",\"value\":\"chilled\"}",
            statusBit: _ => 7);

        var dto = AtomPushCodec.EncodePredicate(original)!;

        // The name is gone by the time it reaches the wire — which is exactly why the injector
        // needs no status catalog to evaluate this.
        Assert.Equal(7, Assert.Single(dto.Ops).Value);
        Assert.DoesNotContain("chilled", JsonSerializer.Serialize(dto, Wire));
    }

    // ---- the payload --------------------------------------------------------------------------------

    static CompiledCatalog Catalog(long revision = 4) => new(
        revision,
        new List<EffectDefDto> { new() { EffectId = "atom.searing-strike", Name = "Searing" } },
        new List<EffectGrantDto> { new() { GrantId = "atom:searing", EffectId = "atom.searing-strike" } },
        new List<string> { "atom.searing-strike.fire.t3" },
        new List<RunnerEntry>(),
        new List<CompileRejection>());

    static RunnerBinding Bind(string id, int priority = 0) =>
        new(id, priority, "player:1", Entry());

    [Fact]
    public void A_receiver_already_on_this_revision_gets_an_empty_apply()
    {
        // E26: the short-circuit is two-term now (§3.3) — the receiver must also echo the current
        // EmitterVersion, or a compiler-code change with no data-side revision bump would never re-push.
        var payload = AtomPushCodec.BuildPayload(
            Catalog(revision: 4), new[] { Bind("b1") }, matchSeed: 7,
            receiverRevision: 4, receiverEmitterVersion: AtomPushCodec.EmitterVersion);

        Assert.True(payload.UpToDate);
        Assert.Empty(payload.Grants);
        Assert.Empty(payload.Defs);
        Assert.Empty(payload.RunnerBindings);
        Assert.Equal(4, payload.CatalogRevision);
    }

    [Fact]
    public void A_stale_receiver_gets_the_full_set_not_a_delta()
    {
        var payload = AtomPushCodec.BuildPayload(
            Catalog(revision: 4), new[] { Bind("b1"), Bind("b2") }, matchSeed: 7,
            receiverRevision: 3, receiverEmitterVersion: AtomPushCodec.EmitterVersion);

        Assert.False(payload.UpToDate);
        Assert.Single(payload.Grants);
        // E26: one compiled def (unchanged) PLUS one runner def — Entry()'s "atom.searing-strike.fire.t3"
        // is a safe kind (resource.delta) with a rolled "amount", so EmitRunnerDefs translates it.
        Assert.Equal(2, payload.Defs.Count);
        Assert.Contains(payload.Defs, d => d.EffectId == "atom.searing-strike");
        Assert.Contains(payload.Defs, d => d.EffectId == "atom.searing-strike.fire.t3");
        Assert.Equal(2, payload.RunnerBindings.Count);
    }

    [Fact]
    public void A_cold_start_with_no_stated_revision_gets_the_full_set()
    {
        var payload = AtomPushCodec.BuildPayload(Catalog(), new[] { Bind("b1") }, matchSeed: 7);

        Assert.False(payload.UpToDate);
        Assert.Single(payload.RunnerBindings);
    }

    [Fact]
    public void The_per_match_seed_travels_so_local_rolls_stay_replayable()
    {
        // D5: the dice are thrown in the injector so the hot loop never waits, but the SERVER owns
        // the seed. Without it a replay cannot reproduce a single proc.
        var payload = AtomPushCodec.BuildPayload(
            Catalog(), Array.Empty<RunnerBinding>(), matchSeed: 0xDEADBEEFCAFE, matchKey: "m1");

        var back = JsonSerializer.Deserialize<AtomPushDto>(JsonSerializer.Serialize(payload, Wire), Wire)!;

        Assert.Equal(0xDEADBEEFCAFEUL, back.MatchSeed);
        Assert.Equal("m1", back.MatchKey);
    }

    [Fact]
    public void The_content_hash_travels_but_never_gates_delivery()
    {
        // A mismatch is a diagnosable state, not a reason to leave a match unarmed.
        var payload = AtomPushCodec.BuildPayload(
            Catalog(), new[] { Bind("b1") }, matchSeed: 1, contentHash: "v1|abc|effect_atom=dead");

        Assert.Equal("v1|abc|effect_atom=dead", payload.ContentHash);
        Assert.NotEmpty(payload.RunnerBindings);
    }

    [Fact]
    public void Bindings_are_emitted_in_evaluation_order()
    {
        var payload = AtomPushCodec.BuildPayload(
            Catalog(),
            new[] { Bind("b-zulu", 1), Bind("b-alpha", 5), Bind("b-mike", 5) },
            matchSeed: 1);

        Assert.Equal(
            new[] { "b-alpha", "b-mike", "b-zulu" },
            payload.RunnerBindings.Select(b => b.BindingId).ToArray());
    }

    [Fact]
    public void A_decoded_payload_builds_a_trigger_index_the_runner_can_use()
    {
        var payload = AtomPushCodec.BuildPayload(
            Catalog(), new[] { Bind("b-zulu", 1), Bind("b-alpha", 5) }, matchSeed: 1);

        var wire = JsonSerializer.Deserialize<AtomPushDto>(JsonSerializer.Serialize(payload, Wire), Wire)!;
        var index = TriggerIndex.Build(AtomPushCodec.DecodeBindings(wire));

        Assert.Equal(2, index.Count);
        Assert.Equal(2, index.SlotsFor(AtomTriggers.OnDamageDealt).Length);
        Assert.Equal("b-alpha", index.Bindings[0].BindingId);
    }

    // ---- ToDef: a static param must survive the real receive path, not just an in-memory copy --------

    /// <summary>
    /// Found 2026-09-03 (E28 session): the real receive path
    /// (<c>CheatCommandRunner.InstallAtomPush</c>) deserializes with
    /// <c>JsonSerializer.Deserialize&lt;AtomPushDto&gt;</c> and no custom converter, so every value in
    /// <c>EffectDefActionDto.Params</c> arrived as a boxed <see cref="JsonElement"/> —
    /// <see cref="JsonElement"/> does not implement <see cref="IConvertible"/>, so
    /// <c>JsonOverlay.GetInt</c>/<c>GetString</c> (what every <c>InjectorEffectActionSink.Exec*</c>
    /// method calls) threw <see cref="InvalidCastException"/> for any def's static param, caught by
    /// that class's own outer try/catch and logged as a failed action, never a crash — invisible to
    /// every test in this suite because <c>SimEffectHost</c> builds <c>EffectDef</c> in memory,
    /// bypassing the wire's JSON round-trip entirely. <c>AtomPushCodec.ToDef</c> now unwraps via
    /// <c>JsonOverlay.FromObject</c> instead of copying the dictionary verbatim — this is the real
    /// wire round-trip, not an in-memory shortcut, so a regression here throws exactly as it would
    /// have thrown live.
    /// </summary>
    [Fact]
    public void ToDef_survives_the_real_wire_round_trip_a_static_int_param_reads_back_as_an_int()
    {
        var dto = new AtomPushDto
        {
            CatalogRevision = 1,
            Defs = new List<EffectDefDto>
            {
                new()
                {
                    EffectId = "fx.board_cherry",
                    EffectType = EffectTypes.Triggered,
                    Name = "Board cherry bomb",
                    Enabled = true,
                    Triggers = new List<string> { EffectTriggers.OnDamageDealt },
                    Actions = new List<EffectDefActionDto>
                    {
                        new()
                        {
                            Seq = 1,
                            Action = EffectActions.BoardAction,
                            Params = new Dictionary<string, object?>
                                { ["op"] = "cherry", ["row"] = 2, ["col"] = 4 },
                        },
                    },
                },
            },
        };

        // Exactly CheatCommandRunner.InstallAtomPush's own call shape: serialize, deserialize, THEN
        // convert — never hand ToDef an in-memory DTO that skipped the wire.
        var wireDto = JsonSerializer.Deserialize<AtomPushDto>(JsonSerializer.Serialize(dto, Wire), Wire)!;
        var def = AtomPushCodec.ToDef(wireDto.Defs[0]);
        var actionRow = def.Actions[0];

        Assert.IsNotType<JsonElement>(actionRow.Params["row"]);
        Assert.IsNotType<JsonElement>(actionRow.Params["op"]);
        Assert.Equal(2, JsonOverlay.GetInt(actionRow.Params, "row"));
        Assert.Equal("cherry", JsonOverlay.GetString(actionRow.Params, "op"));
    }

    [Fact]
    public void ToDef_survives_the_real_wire_round_trip_for_a_nested_array_param()
    {
        // The other shape ToDef must not choke on: an array-of-objects param (E28 fix #7,
        // box.set.cells[]). JsonOverlay.FromObject's recursive unwrap must reach every nesting level,
        // not just the top one.
        var dto = new AtomPushDto
        {
            CatalogRevision = 1,
            Defs = new List<EffectDefDto>
            {
                new()
                {
                    EffectId = "fx.paint-many",
                    EffectType = EffectTypes.Triggered,
                    Name = "Paint many cells",
                    Enabled = true,
                    Triggers = new List<string> { EffectTriggers.OnDamageDealt },
                    Actions = new List<EffectDefActionDto>
                    {
                        new()
                        {
                            Seq = 1,
                            Action = EffectActions.SetBoxType,
                            Params = new Dictionary<string, object?>
                            {
                                ["boxType"] = 2,
                                ["cells"] = new List<object?>
                                {
                                    new Dictionary<string, object?> { ["row"] = 1, ["col"] = 2 },
                                    new Dictionary<string, object?> { ["row"] = 3, ["col"] = 4 },
                                },
                            },
                        },
                    },
                },
            },
        };

        var wireDto = JsonSerializer.Deserialize<AtomPushDto>(JsonSerializer.Serialize(dto, Wire), Wire)!;
        var def = AtomPushCodec.ToDef(wireDto.Defs[0]);
        var cells = Assert.IsType<List<object?>>(def.Actions[0].Params["cells"]);

        Assert.Equal(2, cells.Count);
        var first = Assert.IsType<Dictionary<string, object?>>(cells[0]);
        Assert.Equal(1, JsonOverlay.GetInt(first, "row"));
        Assert.Equal(2, JsonOverlay.GetInt(first, "col"));
    }

    // ---- the guarantee -------------------------------------------------------------------------------

    [Fact]
    public void The_payload_carries_no_content_row_of_any_kind()
    {
        // THE architecture guarantee. If the injector ever needs an atom row to decide something,
        // the compile/run split has leaked and that is a design bug, not a transport gap.
        var payload = AtomPushCodec.BuildPayload(
            Catalog(),
            new[] { Bind("b1"), Bind("b2", 3) },
            matchSeed: 42,
            matchKey: "m1",
            contentHash: "v1|abc|effect_atom=dead");

        var json = JsonSerializer.Serialize(payload, Wire);

        foreach (var column in new[]
                 {
                     "when_json", "params_json", "tags_json", "points_json", "overrides_json",
                     "family_id", "container_id", "curve_id", "rarity_id", "group_key",
                     "pool_rolls", "min_tier", "max_tier", "power_json", "power_override_json",
                 })
            Assert.DoesNotContain(column, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_curve_row_travels_because_bounds_arrive_pre_scaled()
    {
        // D9. The injector could not scale a value even if it wanted to, which is why `input: level`
        // on an OnApply spec is refused all the way back at E4 load.
        var entry = Entry(values: new Dictionary<string, ValueBounds>
        {
            ["amount"] = new(-240, -160, RollPolicy.OnApply), // already doubled by a curve
        });

        var dto = AtomPushCodec.Encode(entry);
        var json = JsonSerializer.Serialize(dto, Wire);

        Assert.DoesNotContain("curve", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(-240, AtomPushCodec.Decode(dto).Values["amount"].Min);
    }

    [Fact]
    public void The_same_entry_serialises_to_the_same_bytes_twice()
    {
        // A payload that reorders between pushes cannot be compared against what the receiver holds.
        var entry = Entry();

        Assert.Equal(
            JsonSerializer.Serialize(AtomPushCodec.Encode(entry), Wire),
            JsonSerializer.Serialize(AtomPushCodec.Encode(entry), Wire));
    }
}
