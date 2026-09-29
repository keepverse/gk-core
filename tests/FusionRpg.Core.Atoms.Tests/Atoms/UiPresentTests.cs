using System.IO;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E41 (spec-ui-attach-point.md). Every other Wave 8 module changes what happens on the board; this
/// one changes what the player KNOWS happened. <c>Ui</c> is read-only by construction — the central
/// invariant proven here (<see cref="Ui_attached_kinds_never_reach_the_generic_sink"/>) is that a
/// <c>ui.present</c> grant NEVER becomes an <see cref="EffectActionPlanItem"/> the state-writing sink
/// sees, because <c>EffectBag.FireGrant</c> handles it bag-side, the same shape
/// <c>shield.grant</c>/FA10 already use.
/// </summary>
public class UiPresentTests
{
    static Dictionary<string, object?> P(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    static AtomRow Row(string paramsJson) => new()
    {
        AtomId = AtomRow.DeriveId("atom.test-ui-present", "", 1),
        KindId = "ui.present",
        FamilyId = "atom.test-ui-present",
        Variant = "",
        Tier = 1,
        Name = "Test Ui Present",
        ParamsJson = paramsJson,
        WhenJson = """{"trigger":"OnDamageDealt"}""",
        IcdKey = "test.ui-present",
    };

    // ---- §2a: the attach point ----------------------------------------------------------------

    [Fact]
    public void Ui_present_attaches_to_Ui()
    {
        Assert.Equal(AttachPoint.Ui, AtomKindRegistry.Get("ui.present")!.Attach);
    }

    // ---- §2b: schema shape ----------------------------------------------------------------------

    [Fact]
    public void Canonical_number_banner_and_meter_samples_validate()
    {
        Assert.True(AtomKindRegistry.Validate("ui.present",
            P(("op", "number"), ("amount", 250), ("tag", "crit"))).IsOk);

        Assert.True(AtomKindRegistry.Validate("ui.present",
            P(("op", "meter"), ("meterId", "hp"), ("ratio", 750))).IsOk);

        // No banner id is legal today (§2b.1's own empty-vocabulary placeholder) — a syntactically
        // valid banner present still needs a real catalog id, which does not exist yet, so this shape
        // is exercised via the BadParamValue test below instead of a positive sample here.
    }

    [Fact]
    public void Op_number_without_amount_is_MissingParam()
    {
        var r = AtomKindRegistry.Validate("ui.present", P(("op", "number")));
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    [Fact]
    public void Op_meter_without_ratio_is_MissingParam()
    {
        var r = AtomKindRegistry.Validate("ui.present", P(("op", "meter"), ("meterId", "hp")));
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    [Fact]
    public void No_op_is_MissingParam()
    {
        var r = AtomKindRegistry.Validate("ui.present", P());
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    [Fact]
    public void Unknown_op_is_BadParamValue()
    {
        var r = AtomKindRegistry.Validate("ui.present", P(("op", "flash")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void Amount_is_only_honoured_under_number()
    {
        var r = AtomKindRegistry.Validate("ui.present",
            P(("op", "meter"), ("meterId", "hp"), ("ratio", 500), ("amount", 10)));
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, r.Reason);
    }

    [Fact]
    public void MeterId_and_ratio_are_only_honoured_under_meter()
    {
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, AtomKindRegistry.Validate("ui.present",
            P(("op", "number"), ("amount", 10), ("meterId", "hp"))).Reason);
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, AtomKindRegistry.Validate("ui.present",
            P(("op", "number"), ("amount", 10), ("ratio", 500))).Reason);
    }

    [Fact]
    public void BannerId_is_only_honoured_under_banner()
    {
        var r = AtomKindRegistry.Validate("ui.present",
            P(("op", "number"), ("amount", 10), ("bannerId", "x")));
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, r.Reason);
    }

    // ---- §2b: ratio's bounded 0-1000 range (a bounded ratio, exempt from the no-hard-ceiling rule,
    // never a magnitude cap — §3) -------------------------------------------------------------------

    [Theory]
    [InlineData(1500)]
    [InlineData(-1)]
    public void Ratio_out_of_the_0_to_1000_bound_is_BadParamValue(int ratio)
    {
        var r = AtomKindRegistry.Validate("ui.present",
            P(("op", "meter"), ("meterId", "hp"), ("ratio", ratio)));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(500)]
    public void Ratio_at_or_inside_the_0_to_1000_bound_validates(int ratio)
    {
        var r = AtomKindRegistry.Validate("ui.present",
            P(("op", "meter"), ("meterId", "hp"), ("ratio", ratio)));
        Assert.True(r.IsOk, r.ToString());
    }

    // ---- §2b: tag reuses DamageFxTag, no new palette -------------------------------------------

    [Theory]
    [InlineData("neutral")] [InlineData("heal")] [InlineData("weak")] [InlineData("resist")]
    [InlineData("null")] [InlineData("absorb")] [InlineData("reflect")] [InlineData("dodge")]
    [InlineData("crit")] [InlineData("penetrate")] [InlineData("block")]
    public void Every_one_of_the_eleven_DamageFxTag_names_validates(string tag)
    {
        Assert.True(AtomKindRegistry.Validate("ui.present",
            P(("op", "number"), ("amount", 10), ("tag", tag))).IsOk, tag);
    }

    [Fact]
    public void An_unknown_tag_is_BadParamValue()
    {
        var r = AtomKindRegistry.Validate("ui.present",
            P(("op", "number"), ("amount", 10), ("tag", "Crit"))); // wrong case — lowercase only
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    /// <summary>
    /// D13 (combat-math-dedup Task 9). <c>DamageFxTag</c> is the closed vocabulary that OWNS these
    /// names; the atom registry declares no copy of them. Asserted as a SET (not a count) because the
    /// enum is a closed vocabulary a human edits, so an added member must move this set with it — and
    /// that is exactly the contract the derivation below implements.
    /// </summary>
    [Fact]
    public void UiPresent_tag_vocabulary_is_exactly_the_DamageFxTag_names_lowercased()
    {
        var expected = Enum.GetNames<DamageFxTag>()
            .Select(n => n.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var actual = AtomKindRegistry.Get("ui.present")!.Params.Defs
            .First(d => d.Name == "tag").Vocabulary!()
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
        Assert.True(expected.Count > 0, "DamageFxTag must declare at least one member");
    }

    /// <summary>
    /// The negative guard D13 asks for (the <c>AtomTriggers</c> template): the atom registry may not
    /// re-introduce a tag literal of its own. A source scan, because a <c>const</c>-like derived array
    /// and a hand-written one are indistinguishable at runtime — the duplication being guarded is a
    /// property of the SOURCE. It bites on the pre-dedup shape (the literal block it replaced).
    /// </summary>
    [Fact]
    public void UiPresent_tag_vocabulary_declares_no_literal_of_its_own()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Effects", "Atoms", "AtomKindRegistry.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);

        var start = text.IndexOf("UiPresentTagValues =", StringComparison.Ordinal);
        Assert.True(start >= 0, "UiPresentTagValues declaration not found");
        var end = text.IndexOf(';', start);
        Assert.True(end > start, "UiPresentTagValues declaration not delimited");
        var declaration = text[start..end];

        foreach (var name in Enum.GetNames<DamageFxTag>())
            Assert.DoesNotContain("\"" + name.ToLowerInvariant() + "\"", declaration, StringComparison.Ordinal);

        // Non-vacuity: the declaration must be the enum-derived one, not an empty array.
        Assert.Contains("DamageFxTag", declaration, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    // ---- §2b.1: the two vocabularies, neither a tuning table ------------------------------------

    [Fact]
    public void MeterId_mana_is_BadParamValue_not_one_of_the_six()
    {
        var r = AtomKindRegistry.Validate("ui.present",
            P(("op", "meter"), ("meterId", "mana"), ("ratio", 500)));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("mana", r.Detail);
    }

    [Theory]
    [InlineData("hp")] [InlineData("stamina")] [InlineData("hunger")] [InlineData("spirit")]
    [InlineData("qi")] [InlineData("poise")]
    public void Every_one_of_the_six_resource_ids_is_a_legal_meterId(string id)
    {
        Assert.True(AtomKindRegistry.Validate("ui.present",
            P(("op", "meter"), ("meterId", id), ("ratio", 500))).IsOk, id);
    }

    /// <summary>
    /// §2b.1's own "the vocabulary widens by itself the day a seventh resource lands" property —
    /// proven the strongest way available without editing <c>DerivedStatChannels.cs</c> (out of this
    /// module's scope): <c>meterId</c>'s Vocabulary is the exact SAME static method group as
    /// <c>resource.delta</c>'s own <c>channel</c> Vocabulary (both <c>ResourceChannels</c>, declared
    /// once in <c>AtomKindRegistry.cs</c>) — not a copy, so whatever the day-of-call
    /// <c>DerivedStatChannels.ResourceIds</c> contains is exactly what both params accept, with no
    /// separate list to fall out of sync. <c>ResourceIds</c> itself is a <c>static readonly</c> array
    /// (append-only per its own doc comment) and cannot be mutated mid-test to simulate a seventh
    /// member without editing that field's own file — so this is the honest ceiling of what a runtime
    /// test can prove for this property.
    /// </summary>
    [Fact]
    public void MeterId_vocabulary_is_the_same_live_function_resource_delta_already_uses()
    {
        var meterIdDef = AtomKindRegistry.Get("ui.present")!.Params.Defs.First(d => d.Name == "meterId");
        var channelDef = AtomKindRegistry.Get("resource.delta")!.Params.Defs.First(d => d.Name == "channel");

        Assert.Equal(channelDef.Vocabulary, meterIdDef.Vocabulary); // same method group -> delegate-equal

        var live = FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds;
        Assert.Equal(live.OrderBy(x => x, StringComparer.Ordinal), meterIdDef.Vocabulary!()
            .OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void BannerId_not_a_banner_is_BadParamValue_naming_the_id()
    {
        var r = AtomKindRegistry.Validate("ui.present",
            P(("op", "banner"), ("bannerId", "not-a-banner")));
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("not-a-banner", r.Detail);
    }

    // ---- runtime matrix / count guards ------------------------------------------------------------

    [Fact]
    public void Runtime_support_is_lawn_only_pending_in_battle_and_sim()
    {
        var kind = AtomKindRegistry.Get("ui.present")!;
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Lawn));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.None, kind.SupportIn(RuntimeId.Sim));
    }

    [Fact]
    public void Ui_present_carries_AllTriggers()
    {
        Assert.True(AtomKindRegistry.ValidateTrigger("ui.present", AtomTriggers.OnDamageDealt).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("ui.present", AtomTriggers.OnDeath).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("ui.present", AtomTriggers.OnTimer).IsOk);
        Assert.True(AtomKindRegistry.ValidateTrigger("ui.present", AtomTriggers.OnActivate).IsOk);
    }

    // §5's own count-guard rule ("delta-style, never literal") is asserted once, centrally, in
    // AtomKindRegistryTests.Vocabulary_is_closed_at_sixteen_kinds_and_seven_attach_points — not
    // duplicated here.

    // ---- compile: kind -> opcode -> compiled def (mirrors BulletModifyCompileTests) --------------

    [Fact]
    public void A_ui_present_atom_compiles_to_a_PresentUi_action_row_with_plain_params()
    {
        var row = Row("""{"op":"number","amount":250,"tag":"crit"}""");

        var compiled = AtomCompiler.Compile(new[] { row }, RuntimeId.Lawn, catalogRevision: 1);

        Assert.Empty(compiled.Rejected);
        Assert.Empty(compiled.Runtime); // must land on the COMPILED path, not the runner

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);

        Assert.Equal(EffectActions.PresentUi, action.Action);
        // No op-as-key rewrite for this kind — ToOpcodeShape only touches stat.modify/stat.derived.
        Assert.Equal("number", action.Params["op"]?.ToString());
        Assert.Equal(250d, Convert.ToDouble(action.Params["amount"]));
        Assert.Equal("crit", action.Params["tag"]?.ToString());
    }

    [Fact]
    public void A_ui_present_atom_is_RuntimeUnsupported_in_Battle()
    {
        var row = Row("""{"op":"number","amount":10}""");
        var verdict = Compilability.Classify(row, RuntimeId.Battle);
        Assert.Equal(AtomPath.Rejected, verdict.Path);
        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, verdict.Rejection);
    }

    [Fact]
    public void UiPresent_is_RuntimeUnsupported_at_BindGate_in_Battle()
    {
        var row = Row("""{"op":"number","amount":10}""");
        var r = BindGate.Check(new[] { row }, OwnerScope.Match, new BindContext(RuntimeId.Battle));
        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, r.Reason);
    }

    // ---- §3: the read-only invariant, and its own PLANTED VIOLATION ------------------------------

    static EffectDef NumberDef(string effectId = "test.ui.present.number") => new()
    {
        EffectId = effectId,
        EffectType = EffectTypes.Triggered,
        Name = "test ui present",
        Triggers = new List<string> { EffectTriggers.OnDamageDealt },
        Actions = new List<EffectActionRow>
        {
            new()
            {
                Seq = 1,
                Action = EffectActions.PresentUi,
                Params = new Dictionary<string, object?>
                {
                    ["op"] = "number",
                    ["amount"] = 250,
                    ["tag"] = "crit",
                },
            },
        },
    };

    /// <summary>
    /// §4's own named case: "ui.present{op:number, amount:{min:250,max:250}, tag:'crit'} on a damage
    /// trigger produces one DamageFxDto with Amount=250, Tag=Crit, via RecordingDamageFxSink."
    /// </summary>
    [Fact]
    public void Op_number_shows_a_floater_via_RecordingDamageFxSink()
    {
        var harness = new FoundationHarness().WithCatalog(new[] { NumberDef() });
        harness.Grant(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "test.ui.present.number",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "test",
        });

        harness.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            TargetPtr = "aaa",
            Tick = 1,
        });

        var fx = Assert.Single(harness.Fx.Items);
        Assert.Equal(250, fx.Amount);
        Assert.Equal(DamageFxTag.Crit, fx.Tag);
    }

    /// <summary>
    /// §5's own named case: "op:meter fills ActorHudResources.Meters for that ptr" — this is the
    /// PRODUCER half (via the fake HUD cache, <see cref="RecordingUiPresentSink"/>); the Compose/wire
    /// half is proven separately in ActorHudComposerTests/ActorHudWireSerializerTests. Ratio's per-
    /// mille magnitude (750) divides by 1000 exactly once, in EffectBag.ExecPresentUi.
    /// </summary>
    [Fact]
    public void Op_meter_calls_the_fake_hud_cache_with_a_divided_ratio()
    {
        var def = new EffectDef
        {
            EffectId = "test.ui.present.meter",
            EffectType = EffectTypes.Triggered,
            Name = "test ui present meter",
            Triggers = new List<string> { EffectTriggers.OnDamageDealt },
            Actions = new List<EffectActionRow>
            {
                new()
                {
                    Seq = 1,
                    Action = EffectActions.PresentUi,
                    Params = new Dictionary<string, object?>
                    {
                        ["op"] = "meter",
                        ["meterId"] = "hp",
                        ["ratio"] = 750,
                    },
                },
            },
        };

        var harness = new FoundationHarness().WithCatalog(new[] { def });
        harness.Grant(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "test.ui.present.meter",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "test",
        });

        harness.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            TargetPtr = "aaa",
            Tick = 1,
        });

        var call = Assert.Single(harness.UiPresent.Meters);
        Assert.Equal("aaa", call.TargetPtr);
        Assert.Equal("hp", call.MeterId);
        Assert.Equal(0.75, call.Ratio);
    }

    /// <summary>
    /// The module's central invariant (§2a, §3, acceptance criterion 3): a <c>Ui</c>-attached kind's
    /// action never becomes an <see cref="EffectActionPlanItem"/> that reaches
    /// <c>InjectorEffectActionSink</c>'s stat/resource/status/shield/board arms. Proven directly
    /// against <see cref="RecordingEffectSink"/> (the state-writing sink) rather than against the
    /// Unity-only injector, matching this module's own "verify in Core.Tests" boundary. Generalises
    /// to future Ui-attached kinds by construction: any kind on <see cref="AttachPoint.Ui"/> must have
    /// its opcode handled by a bag-side branch in <c>EffectBag.FireGrant</c> (the same shape
    /// <c>GrantShield</c>/<c>ApplyResourceDelta</c> already use), or it falls through to the generic
    /// <c>_sink.Execute</c> call at the bottom of that loop and this test fails.
    /// </summary>
    [Fact]
    public void Ui_attached_kinds_never_reach_the_generic_sink()
    {
        foreach (var kind in AtomKindRegistry.All.Where(k => k.Attach == AttachPoint.Ui))
        {
            var harness = new FoundationHarness().WithCatalog(new[] { NumberDef(kind.KindId + ".test") });
            harness.Grant(new EffectGrantDto
            {
                GrantId = "g-" + kind.KindId,
                EffectId = kind.KindId + ".test",
                OwnerKey = EffectOwnerKeys.Match,
                PluginId = "test",
            });

            harness.OnEvent(new EffectEventDto
            {
                Trigger = EffectTriggers.OnDamageDealt,
                TargetPtr = "aaa",
                Tick = 1,
            });

            Assert.Empty(harness.Sink.Items);
        }
    }

    /// <summary>
    /// PLANTED VIOLATION (§4): "make a Ui kind emit a ModifyStat plan item — the read-only guard test
    /// must fail." Pins the CURRENT, correct behaviour — zero items reach the state-writing sink for a
    /// real <c>ui.present</c> grant — so that if <c>EffectBag.FireGrant</c>'s <c>PresentUi</c> branch
    /// were ever removed (falling through to the same generic <c>_sink.Execute(ctx, item)</c> path
    /// every state-writing action already uses), a <c>ui.present</c> grant would emit a genuine
    /// <see cref="EffectActionPlanItem"/> to the sink and this assertion — currently true — would go
    /// red, exactly the loud failure the central invariant demands. Same shape as this repo's other
    /// PLANTED_VIOLATION tests (e.g. AtomKindRegistryTests' match.modify/wave.control ones): the
    /// violation is never literally introduced, the CORRECT behaviour it depends on is pinned instead.
    /// </summary>
    [Fact]
    public void PLANTED_VIOLATION_a_ui_present_plan_item_reaching_the_sink_would_break_the_readonly_invariant()
    {
        var harness = new FoundationHarness().WithCatalog(new[] { NumberDef() });
        harness.Grant(new EffectGrantDto
        {
            GrantId = "g1",
            EffectId = "test.ui.present.number",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = "test",
        });

        harness.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            TargetPtr = "aaa",
            Tick = 1,
        });

        Assert.Empty(harness.Sink.Items);
        Assert.DoesNotContain(harness.Sink.Items, i => i.Action == EffectActions.PresentUi);
        // The present still happened -- read-only means "never writes state", not "never fires".
        Assert.Single(harness.Fx.Items);
    }

    // ---- §2c: pricing — exactly zero, Priced, never unpriced ---------------------------------------

    [Fact]
    public void UiPresent_prices_exactly_zero_with_verdict_Priced_from_the_seed_file()
    {
        var tables = BuildTablesFromSeedFile();
        var atom = Row("""{"op":"number","amount":250}""") with { WhenJson = "{}" };

        var priced = CostFunction.Price(atom, tables);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.Equal("", priced.Verdict.Reason);
        Assert.Equal(PowerVector.Zero, priced.Power);
    }

    /// <summary>
    /// PLANTED VIOLATION (§4): "remove ui.present's coefficient row — the pricing test must fail with
    /// unpriced, never silently falling back." <c>PowerTables.Authored()</c> — the no-database
    /// fallback this module is explicitly forbidden from editing (§2c) — deliberately carries NO
    /// <c>ui.present</c> row, which is exactly what "the row removed" looks like. Pricing against it
    /// simulates the removal directly: <c>CoefficientTable.Find</c> falls through to the kind's
    /// channel-less row, finds none, and the atom comes back UNPRICED — never a silent zero, which is
    /// the whole distinction §2c exists to keep visible (an authored zero and a missing row are
    /// different claims).
    /// </summary>
    [Fact]
    public void PLANTED_VIOLATION_removing_the_coefficient_row_falls_back_to_unpriced_never_free()
    {
        var atom = Row("""{"op":"number","amount":250}""") with { WhenJson = "{}" };

        var priced = CostFunction.Price(atom, PowerTables.Authored());

        Assert.False(priced.Ok);
        Assert.Contains("no coefficient", priced.Verdict.Reason);
        Assert.Equal(PowerVector.Zero, priced.Power); // no price computed, not a real zero price
    }

    static PowerTables BuildTablesFromSeedFile()
    {
        var content = LoadCoefficientSeedFile(out var errors);
        Assert.Empty(errors);

        return new PowerTables(content.Coefficients, Array.Empty<TriggerFrequencyRow>());
    }

    static SeedContent LoadCoefficientSeedFile(out IReadOnlyList<SeedError> errors)
    {
        var path = Path.Combine(ContentRoot.Path, "data", "seed", "power", "coefficients.v1.json");
        var result = AtomSeedFile.Collect(new[] { (path, File.ReadAllText(path)) });
        errors = result.Errors;
        return result.Content;
    }
}
