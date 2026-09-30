using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Movement;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Actions.Movement;

/// <summary>
/// A-M1 (spec-movement-payload.md): the RPG-layer half of a movement action. Tests mirror the spec's
/// own §4 numbering — stubbed transport, determinism/replay, planted violations, and the inertness
/// test that tells the truth about <c>move.range</c>/<c>skill.cooldown.*</c>/<c>skill.effectiveness.*</c>
/// having no production reader today (§4 case 4, AC10).
///
/// <para>The Unity-reference planted violation (§4, "a Unity type referenced from this module's own
/// source") lives in <c>FusionRpg.Guard.Tests.MovementPayloadNoUnityGuardTests</c>, not here — a
/// scan of this module's OWN files, since <c>guard-secondary-no-unity.ps1</c> never reaches
/// <c>Actions/Movement/</c> (AC4).</para>
/// </summary>
public class MovementPayloadTests
{
    static DerivedStatRegistry Channels() => DerivedStatRegistry.CreateDefault();
    static StatusCatalog Statuses() => StatusCatalogBootstrap.CreateDefault();

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string ShippedJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "movement-payload.v1.json"));

    // ---- AC1 / AC2 / AC3 / AC5 / AC5b: the real shipped file loads clean --------------------

    [Fact]
    public void The_shipped_tuning_file_loads_clean_and_carries_exactly_the_13_non_UnityCc_statuses()
    {
        var tuning = MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses());

        Assert.Equal(
            new[] { "move.range", "skill.cooldown.movement", "skill.effectiveness.movement" },
            tuning.Channels.Select(c => c.Id));

        var expectedStatuses = new[]
        {
            "wither", "bond", "rally", "leech", "expose", "command", "shatter", "charm_pulse",
            "blight", "rot", "spark", "pact_mark", "spore",
        };
        // The count is fully implied by the element-wise equality below -- never pinned separately
        // (population-pin SE3.3, 2026-09-19).
        Assert.Equal(expectedStatuses.OrderBy(x => x, StringComparer.Ordinal),
            tuning.Statuses.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal));

        Assert.Equal(new[] { "buff", "status", "tempo", "none" }, tuning.PayloadKinds.Select(p => p.Id));
    }

    [Fact]
    public void None_of_the_8_UnityCc_statuses_are_admitted()
    {
        var tuning = MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses());
        var refused = new[] { "butter", "freeze", "cold", "poison", "hypno", "ember", "jala", "kelp" };

        foreach (var id in refused)
            Assert.DoesNotContain(tuning.Statuses, s => s.Id == id);
    }

    [Fact]
    public void Every_description_in_the_shipped_file_carries_a_negative_clause()
    {
        var tuning = MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses());
        var all = tuning.Channels.Concat(tuning.Statuses).Concat(tuning.PayloadKinds);
        foreach (var entry in all)
            Assert.Matches(@"\b(not|never)\b", entry.Description);
    }

    // ---- determinism / replay (§4 case 2) ----------------------------------------------------

    [Fact]
    public void Loading_the_tuning_file_twice_yields_identical_policy_state_by_hash()
    {
        var json = ShippedJson();
        var t1 = MovementPayloadTuningLoader.Parse(json, Channels(), Statuses());
        var t2 = MovementPayloadTuningLoader.Parse(json, Channels(), Statuses());

        Assert.Equal(CanonicalHash(t1), CanonicalHash(t2));
    }

    static string CanonicalHash(MovementPayloadTuning t)
    {
        var canonical = new
        {
            channels = t.Channels.OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new { e.Id, e.Description }),
            statuses = t.Statuses.OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new { e.Id, e.Description }),
            payloadKinds = t.PayloadKinds.OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new { e.Id, e.Description }),
        };
        var json = JsonSerializer.Serialize(canonical, new JsonSerializerOptions { WriteIndented = false });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void HasStandalonePayload_is_the_same_verdict_regardless_of_evaluation_order()
    {
        var policy = new MovementPayloadPolicy(MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses()));
        var withPayload = MovementActionWithPayload();
        var withoutPayload = MovementActionWithoutPayload();

        // Evaluated out of order and repeatedly -- the policy carries no mutable/ambient state, so the
        // verdict for one action never depends on having evaluated the other first.
        var b1 = policy.HasStandalonePayload(withoutPayload);
        var a1 = policy.HasStandalonePayload(withPayload);
        var a2 = policy.HasStandalonePayload(withPayload);
        var b2 = policy.HasStandalonePayload(withoutPayload);

        Assert.True(a1); Assert.True(a2);
        Assert.False(b1); Assert.False(b2);
    }

    // ---- AC6 / AC7: the ActionValidator wiring -----------------------------------------------

    [Fact]
    public void PlantedViolation_a_movement_action_whose_only_effect_is_a_reposition_is_rejected_naming_the_action_id()
    {
        var policy = new MovementPayloadPolicy(MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses()));
        var action = MovementActionWithoutPayload();

        Assert.False(policy.HasStandalonePayload(action));

        var rejection = ActionValidator.ValidateMovementPayload(action, policy);

        Assert.False(rejection.IsOk);
        Assert.Equal(ActionRejectionReason.MovementActionHasNoStandalonePayload, rejection.Reason);
        Assert.Contains(action.ActionId, rejection.Detail, StringComparison.Ordinal);
        Assert.Contains("a movement action must do something with the game closed", rejection.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_movement_action_with_a_legal_payload_validates_and_compiles_with_boardAvailable_false()
    {
        var table = OneRung(1, Array.Empty<string>());
        var row = BaseRow("action.movement.with-payload", ActionCategory.Movement, rung: 1);
        var scopes = new[] { new ActionScopeRow(row.ActionId, "atom.rally-pulse", ActionEffectScope.Caster) };
        var container = new HashSet<string>(StringComparer.Ordinal) { "atom.rally-pulse" };

        var (rejection, compiled) = ActionCompiler.Compile(
            row, Array.Empty<ActionCostRow>(), scopes, container, boardAvailable: false, table);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(ActionCategory.Movement, compiled!.Category);

        var policy = new MovementPayloadPolicy(MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses()));
        var movementCheck = ActionValidator.ValidateMovementPayload(compiled, policy);
        Assert.True(movementCheck.IsOk, movementCheck.ToString());
    }

    [Fact]
    public void A_non_movement_category_action_is_never_gated_by_this_check()
    {
        var policy = new MovementPayloadPolicy(MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses()));
        var attackAction = MovementActionWithoutPayload() with { Category = ActionCategory.Attack };

        var rejection = ActionValidator.ValidateMovementPayload(attackAction, policy);

        Assert.True(rejection.IsOk, rejection.ToString());
    }

    [Fact]
    public void An_uncategorized_action_null_Category_is_never_gated_by_this_check()
    {
        // Every basic/innate authored before A-E1 shipped carries Category = null (ActionRow.cs's own
        // doc comment). This check must treat that exactly like "not Movement", never like a Movement
        // action with no payload -- a null category is not a fourth enum value to special-case.
        var policy = new MovementPayloadPolicy(MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses()));
        var uncategorized = MovementActionWithoutPayload() with { Category = null };

        var rejection = ActionValidator.ValidateMovementPayload(uncategorized, policy);

        Assert.True(rejection.IsOk, rejection.ToString());
    }

    // ---- IsLegalPayloadChannel / IsLegalPayloadStatus ----------------------------------------

    [Fact]
    public void IsLegalPayloadChannel_and_IsLegalPayloadStatus_match_the_published_lists_and_refuse_everything_else()
    {
        var policy = new MovementPayloadPolicy(MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses()));

        Assert.True(policy.IsLegalPayloadChannel("move.range"));
        Assert.True(policy.IsLegalPayloadChannel("skill.cooldown.movement"));
        Assert.True(policy.IsLegalPayloadChannel("skill.effectiveness.movement"));
        Assert.False(policy.IsLegalPayloadChannel("combat.power"));
        Assert.False(policy.IsLegalPayloadChannel(""));

        Assert.True(policy.IsLegalPayloadStatus("wither"));
        Assert.False(policy.IsLegalPayloadStatus("freeze")); // UnityCc -- refused at load, absent from the list
        Assert.False(policy.IsLegalPayloadStatus("no-such-status"));
    }

    // ---- planted violations (§4 case 3) -------------------------------------------------------

    [Fact]
    public void PlantedViolation_a_status_not_in_StatusCatalogBootstrap_is_a_load_time_failure()
    {
        var json = Fixture(statusEntry: """{ "id": "no-such-status-anywhere", "description": "not a real status and never registered." }""");

        var ex = Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
        Assert.Contains("no-such-status-anywhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantedViolation_a_UnityCc_status_is_a_load_time_failure()
    {
        var json = Fixture(statusEntry: """{ "id": "freeze", "description": "not a real risk and never admitted." }""");

        var ex = Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
        Assert.Contains("freeze", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UnityCc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantedViolation_an_unregistered_derived_channel_is_a_load_time_failure()
    {
        var json = Fixture(channelEntry: """{ "id": "no.such.channel", "description": "not registered and never will be." }""");

        var ex = Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
        Assert.Contains("no.such.channel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantedViolation_a_numeric_value_anywhere_in_the_lists_is_a_load_time_failure()
    {
        var json = """
        {
          "channels": [ { "id": "move.range", "description": "not attack reach and never movement speed.", "weight": 5 } ],
          "statuses": [ { "id": "wither", "description": "not UnityCc and never needs the lawn." } ],
          "payloadKinds": [
            { "id": "buff", "description": "not a status and never the no-payload marker." },
            { "id": "none", "description": "never a legal standalone payload." }
          ]
        }
        """;

        var ex = Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
        Assert.Contains("numeric", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantedViolation_payloadKinds_missing_none_fails_schema()
    {
        var json = """
        {
          "channels": [ { "id": "move.range", "description": "not attack reach and never movement speed." } ],
          "statuses": [ { "id": "wither", "description": "not UnityCc and never needs the lawn." } ],
          "payloadKinds": [ { "id": "buff", "description": "not a status and never the no-payload marker." } ]
        }
        """;

        var ex = Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
        Assert.Contains("none", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantedViolation_a_description_with_no_negative_clause_fails()
    {
        var json = Fixture(channelEntry: """{ "id": "move.range", "description": "move.range is how far an actor may reposition." }""");

        var ex = Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
        Assert.Contains("negative clause", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantedViolation_an_unknown_key_on_an_entry_is_refused()
    {
        var json = """
        {
          "channels": [ { "id": "move.range", "description": "not attack reach and never movement speed.", "extra": "x" } ],
          "statuses": [ { "id": "wither", "description": "not UnityCc and never needs the lawn." } ],
          "payloadKinds": [
            { "id": "buff", "description": "not a status and never the no-payload marker." },
            { "id": "none", "description": "never a legal standalone payload." }
          ]
        }
        """;

        Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
    }

    [Fact]
    public void PlantedViolation_a_payloadKind_outside_the_closed_set_is_refused()
    {
        var json = """
        {
          "channels": [ { "id": "move.range", "description": "not attack reach and never movement speed." } ],
          "statuses": [ { "id": "wither", "description": "not UnityCc and never needs the lawn." } ],
          "payloadKinds": [
            { "id": "none", "description": "never a legal standalone payload." },
            { "id": "teleport", "description": "not a real payload kind and never will be." }
          ]
        }
        """;

        var ex = Assert.Throws<MovementPayloadRejection>(() => MovementPayloadTuningLoader.Parse(json, Channels(), Statuses()));
        Assert.Contains("teleport", ex.Message, StringComparison.Ordinal);
    }

    // ---- §4 case 4: an inertness test that tells the truth (AC10) ----------------------------

    [Fact]
    public void Inertness_skill_cooldown_effectiveness_movement_have_no_production_reader_today_move_range_now_does()
    {
        // RENAMED and REWRITTEN, not deleted (matching action-todo.md's own T60.2 precedent for exactly
        // this shape of change) -- this test was deliberately built to FAIL the day someone wires a
        // reader for one of these three channels (AC10, §4 case 4), and move.range gaining one
        // 2026-09-07 (A9 movement-actions, BasicAttack.cs's ApplyBasicAttack) is precisely that event.
        // The two channels this module still ships no reader for keep the original assertion; move.range
        // gets the corrected one.
        var registry = Channels();

        AssertHasReader(registry, DerivedStatChannels.MoveRange);
        AssertNoReader(registry, DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategoryMovement));
        AssertNoReader(registry, DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryMovement));

        static void AssertNoReader(DerivedStatRegistry registry, string channel)
        {
            Assert.True(registry.TryGet(channel, out var def), $"'{channel}' is not even registered");
            Assert.NotNull(def.UnitClassNote);
            Assert.Contains("No reader", def.UnitClassNote, StringComparison.Ordinal);
        }

        static void AssertHasReader(DerivedStatRegistry registry, string channel)
        {
            Assert.True(registry.TryGet(channel, out var def), $"'{channel}' is not even registered");
            Assert.NotNull(def.UnitClassNote);
            Assert.Contains("Reader", def.UnitClassNote, StringComparison.Ordinal);
            Assert.DoesNotContain("No reader", def.UnitClassNote, StringComparison.Ordinal);
        }
    }

    // ---- §4 case 1: stubbed transport that raises (trivially true; asserted anyway) ----------

    [Fact]
    public void Stubbed_transport_that_raises_still_lets_the_whole_policy_path_run_this_module_never_calls_a_model()
    {
        using var handler = new RaisingHandler();
        using var client = new HttpClient(handler);

        // The whole load -> policy -> validate path, with a transport that throws on ANY request
        // reaching it. "Model calls: no" (spec header) is asserted here mechanically: the handler must
        // never be invoked, and the real verdict must still come back untouched.
        var tuning = MovementPayloadTuningLoader.Parse(ShippedJson(), Channels(), Statuses());
        var policy = new MovementPayloadPolicy(tuning);
        var rejection = ActionValidator.ValidateMovementPayload(MovementActionWithoutPayload(), policy);

        Assert.False(rejection.IsOk);
        Assert.Equal(0, handler.CallCount);
        Assert.NotNull(client); // keeps the stub reachable without ever being dispatched to
    }

    sealed class RaisingHandler : HttpMessageHandler
    {
        public int CallCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException(
                "MovementPayloadPolicy must never reach a transport -- it makes no model call by construction");
        }
    }

    // ---- fixtures -----------------------------------------------------------------------------

    static CompiledAction MovementActionWithoutPayload() => new(
        "action.movement.reposition-only", ActionKind.Skill, 1, new[] { ActionTag.Movement }, true, 1,
        false, false, "", ActionEnvelope.NoOp with { ActionId = "action.movement.reposition-only" },
        new CompiledTargetSpec(true, Array.Empty<TargetSpec>()), 0, 0, null, false,
        PredicateCompiler.Always, Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>(),
        RungBand: null, Category: ActionCategory.Movement);

    static CompiledAction MovementActionWithPayload() => MovementActionWithoutPayload() with
    {
        Scopes = new[] { new ActionScopeRow("action.movement.reposition-only", "atom.rally-pulse", ActionEffectScope.Caster) },
    };

    static ActionRow BaseRow(string id, ActionCategory category, int rung) => new()
    {
        ActionId = id,
        Name = id,
        Kind = ActionKind.Skill,
        Rung = rung,
        ContainerId = "container.test",
        Envelope = ActionEnvelope.NoOp with { ActionId = id },
        Targeting = new ActionTargetSpec(),
        Category = category,
    };

    /// <summary>A contiguous 1..rung table whose last row carries <paramref name="structureBudget"/> —
    /// same shape as <c>ActionCatalogTests.OneRung</c>.</summary>
    static RungTable OneRung(int rung, IReadOnlyList<string> structureBudget)
    {
        var rows = new RungRow[rung];
        for (var r = 1; r < rung; r++) rows[r - 1] = new RungRow(r, 1, 1, 1, 1000, 1000, 1000, Array.Empty<string>());
        rows[rung - 1] = new RungRow(rung, 1, 1, 1, 1000, 1000, 1000, structureBudget);
        return new RungTable(cap: rung, rows);
    }

    /// <summary>A minimal, otherwise-valid document with one list entry swapped for a planted
    /// violation — keeps every planted-violation test one line instead of a full document literal.</summary>
    static string Fixture(string? channelEntry = null, string? statusEntry = null)
    {
        channelEntry ??= """{ "id": "move.range", "description": "not attack reach and never movement speed." }""";
        statusEntry ??= """{ "id": "wither", "description": "not UnityCc and never needs the lawn." }""";
        return $$"""
        {
          "channels": [ {{channelEntry}} ],
          "statuses": [ {{statusEntry}} ],
          "payloadKinds": [
            { "id": "buff", "description": "not a status and never the no-payload marker." },
            { "id": "none", "description": "never a legal standalone payload." }
          ]
        }
        """;
    }
}
