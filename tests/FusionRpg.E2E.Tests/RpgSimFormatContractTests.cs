using System.Text.Json;
using FusionRpg.Core.Effects;
using FusionRpg.Tools.RpgSim;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS2.1 — the scenario contract's tests. No server, no HTTP: these are the refusals
/// the format makes before a run touches anything, plus the expressibility measurement the shape
/// lane could not run (<c>docs/architecture/rpg-simulator-shape-idea.md</c> §9: "whether sim.*/api.*/
/// read.* fit that shape or force a discriminated-union redesign has <b>not</b> been tried").
///
/// <para>The contract itself is <c>gk-core/tools/RpgSim/scenario-format.md</c>; the machine that enforces it
/// is <c>gk-core/tools/RpgSim/ScenarioValidator.cs</c>. This file is where both are proven, on documents
/// written as JSON text — the same thing a scenario author writes.</para>
/// </summary>
public class RpgSimFormatContractTests
{
    // ---- the expressibility measurement ------------------------------------------------------ //

    /// <summary>
    /// A step that names its route cannot be a <see cref="EffectScenarioStepDto"/>. The effect DTO is
    /// a flat bag of op-specific optional fields (<c>EffectScenarioRunner.cs:34-60</c>): it has
    /// <c>op</c>, effect-shaped members (<c>grant</c>, <c>ms</c>, <c>ptr</c>, <c>damage</c>), and an
    /// <c>expect</c> that is an <c>IntentPlanDto</c>. It has no <c>route</c>, no <c>args</c>, no
    /// <c>capture</c> and no <c>why</c> — and because the default serializer ignores unknown members,
    /// handing it this program's step JSON would drop every one of them <i>silently</i>. That silence
    /// is the argument: a shared step type would let a scenario drift from its runner with no error
    /// at all, which is the one property RS1 shipped to prevent.
    /// </summary>
    [Fact]
    public void The_effect_scenario_step_dto_cannot_express_the_rpg_step_and_drops_it_silently()
    {
        const string stepJson = """
            {
              "op": "api.player.create",
              "route": "POST /api/players",
              "args": { "name": "ScenarioFirstSession" },
              "capture": [{ "name": "playerId", "path": "$.id" }],
              "why": "the subject of every later read-back"
            }
            """;

        var effectStep = JsonSerializer.Deserialize<EffectScenarioStepDto>(stepJson)!;

        // The op name survives — and carries no route with it, so the runner would have to hold its
        // own op -> route table while the effect runner holds another.
        Assert.Equal("api.player.create", effectStep.Op);

        // The step members this program's format requires have no home on the effect type...
        foreach (var member in new[] { "Route", "Args", "Capture", "Why" })
            Assert.Null(typeof(EffectScenarioStepDto).GetProperty(member));

        // ...and `Expect` means something else there: an effect intent plan, not an assertion about a
        // reading. Sharing the type would give one word two meanings (DESIGN-GATE §1's parallel
        // vocabulary, the atom and action rows).
        Assert.Equal(typeof(FusionRpg.Contracts.IntentPlanDto),
            typeof(EffectScenarioStepDto).GetProperty("Expect")!.PropertyType);

        // The loss is silent, not loud: deserialization succeeds with the route gone.
        Assert.Equal("POST /api/players", JsonDocument.Parse(stepJson).RootElement.GetProperty("route").GetString());
        Assert.DoesNotContain("POST /api/players", CanonicalJson.Of(JsonSerializer.SerializeToElement(effectStep)));
    }

    /// <summary>The other side of the same measurement: the type this program does ship carries all
    /// four, so the format is expressible once it owns its step type.</summary>
    [Fact]
    public void The_rpg_step_type_carries_the_route_args_capture_and_why()
    {
        var step = ScenarioFile.Parse(Minimal("""
            { "op": "api.player.create", "route": "POST /api/players", "args": { "name": "n" },
              "capture": [{ "name": "playerId", "path": "$.id" }], "why": "subject" }
            """, withRead: false)).Steps.Single();

        Assert.Equal("POST /api/players", step.Route);
        Assert.Equal("n", step.Args!["name"].GetString());
        Assert.Equal("playerId", step.Capture!.Single().Name);
        Assert.Equal("subject", step.Why);
    }

    // ---- the refusals ------------------------------------------------------------------------ //

    [Fact]
    public void The_shipped_corpus_validates_against_the_contract()
    {
        // The corpus is the contract's instance: every file under gk-core/tests/fixtures/rpg-scenarios/** must
        // pass the same validation the runner performs before it touches a server. RS2.1 landed the
        // validator; RS2.3 migrated the corpus to the shape it accepts, so this is the first point at
        // which the format is proven against a real scenario rather than a literal.
        var files = Directory.EnumerateFiles(FindScenariosDir(), "*.json").ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var doc = ScenarioFile.Read(file);
            Assert.Empty(ScenarioValidator.Validate(doc));
        }
    }

    [Fact]
    public void A_step_whose_declared_route_is_not_the_route_its_op_calls_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "api.player.create", "route": "POST /api/creatures/summon", "why": "wrong route" }
            """));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("is not the route 'api.player.create' calls"));
    }

    [Fact]
    public void An_op_outside_the_closed_vocabulary_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "api.player.ensure", "route": "POST /api/players", "why": "invented op" }
            """));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("not in the closed call vocabulary"));
    }

    [Fact]
    public void The_surface_prefix_is_derived_from_the_route_and_the_read_rule_follows_it()
    {
        Assert.Equal("api", ScenarioVocabulary.RouteSurface("GET /api/souls/1"));
        Assert.Equal("test", ScenarioVocabulary.RouteSurface("POST /api/test/seed-souls-demo"));
        Assert.Equal("sim", ScenarioVocabulary.RouteSurface("POST /api/sim/board"));
        Assert.Equal("hub", ScenarioVocabulary.RouteSurface("/hub/rpg"));
        Assert.Null(ScenarioVocabulary.RouteSurface("not a route"));

        Assert.True(ScenarioVocabulary.IsFeFacingRead("GET /api/creatures/1"));
        Assert.True(ScenarioVocabulary.IsFeFacingRead("/hub/rpg"));
        Assert.False(ScenarioVocabulary.IsFeFacingRead("GET /api/test/snapshot"));
        Assert.False(ScenarioVocabulary.IsFeFacingRead("GET /api/sim/board"));
        Assert.False(ScenarioVocabulary.IsFeFacingRead("POST /api/players"));
    }

    [Fact]
    public void A_read_that_is_not_fe_facing_is_refused()
    {
        // /api/test/snapshot is the anti-pattern named by live-probe-standard.md §3.3, and /api/sim/*
        // is the input feed: neither may carry a verdict.
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "read.snapshot", "route": "GET /api/test/snapshot", "why": "convenient" }
            """, withRead: false));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("is not an FE-facing read"));

        var sim = ScenarioFile.Parse(Minimal("""
            { "op": "read.feed", "route": "GET /api/sim/board", "why": "wrong direction" }
            """, withRead: false));
        Assert.Contains(ScenarioValidator.Validate(sim), e => e.Contains("is not an FE-facing read"));
    }

    [Fact]
    public void A_write_route_used_as_a_read_back_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "read.summon", "route": "POST /api/creatures/summon", "why": "body is not evidence" }
            """, withRead: false));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("is not an FE-facing read"));
    }

    [Fact]
    public void An_assertion_over_a_reading_that_was_never_declared_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "expect.gold", "reading": "read.souls", "path": "$.balance", "check": "atLeast",
              "value": 1, "why": "wishful" }
            """));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("was never declared before this step"));
    }

    [Fact]
    public void An_assertion_whose_check_is_not_in_the_closed_set_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "expect.ratio", "reading": "read.souls", "path": "$.balance", "check": "greaterThanHalfOf",
              "value": 1, "why": "computed" }
            """, withRead: true));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("is not one of"));
    }

    [Fact]
    public void An_order_insensitive_comparison_needs_another_reading_or_a_capture_never_a_constant()
    {
        var constant = ScenarioFile.Parse(Minimal("""
            { "op": "expect.squad", "reading": "read.roster", "path": "$.items", "check": "equalSet",
              "value": [1, 2], "why": "constant" }
            """, withRead: true));
        Assert.Contains(ScenarioValidator.Validate(constant), e => e.Contains("needs other"));

        var unknown = ScenarioFile.Parse(Minimal("""
            { "op": "expect.squad", "reading": "read.roster", "path": "$.items", "check": "equalSet",
              "other": "{squadIds}", "why": "never captured" }
            """, withRead: true));
        Assert.Contains(ScenarioValidator.Validate(unknown), e => e.Contains("was never captured"));
    }

    [Fact]
    public void A_digest_exclusion_without_a_reason_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "digest", "include": ["read.souls#$.balance"],
              "exclude": [{ "field": "instanceId", "reason": "" }], "why": "hide it" }
            """, withRead: true));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("has no reason"));
    }

    [Fact]
    public void A_digest_including_a_reading_declared_later_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "digest", "include": ["read.expeditions#$.items"], "why": "ahead of itself" }
            """, withRead: true));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("never declared before this step"));
    }

    [Fact]
    public void A_declared_offset_is_accepted_and_an_undeclared_one_is_refused()
    {
        // RS3 increment 5a (owner ruling on RS-F16): `offset` is honest now — both hosts apply the declared
        // offset at boot through the one seam. What is still refused is an offset the scenario never states,
        // and the absolute `explicit` mode the seam has no product shape for.
        var missing = Minimal("""
            { "op": "api.player.create", "route": "POST /api/players", "why": "ok" }
            """).Replace("\"clock\": { \"mode\": \"ambient\", \"note\": \"ambient\" }",
                         "\"clock\": { \"mode\": \"offset\", \"note\": \"+1d\" }");
        Assert.Contains(ScenarioValidator.Validate(ScenarioFile.Parse(missing)),
            e => e.Contains("clock.offsetSeconds: required"));

        var declared = Minimal("""
            { "op": "api.player.create", "route": "POST /api/players", "why": "ok" }
            """).Replace("\"clock\": { \"mode\": \"ambient\", \"note\": \"ambient\" }",
                         "\"clock\": { \"mode\": \"offset\", \"offsetSeconds\": 86400, \"note\": \"+1d\" }");
        Assert.Empty(ScenarioValidator.Validate(ScenarioFile.Parse(declared)));

        var absolute = Minimal("""
            { "op": "api.player.create", "route": "POST /api/players", "why": "ok" }
            """).Replace("\"clock\": { \"mode\": \"ambient\", \"note\": \"ambient\" }",
                         "\"clock\": { \"mode\": \"explicit\", \"note\": \"2026-01-01T00:00:00Z\" }");
        Assert.Contains(ScenarioValidator.Validate(ScenarioFile.Parse(absolute)),
            e => e.Contains("not accepted"));

        var strayOnAmbient = Minimal("""
            { "op": "api.player.create", "route": "POST /api/players", "why": "ok" }
            """).Replace("\"clock\": { \"mode\": \"ambient\", \"note\": \"ambient\" }",
                         "\"clock\": { \"mode\": \"ambient\", \"offsetSeconds\": 60, \"note\": \"ambient\" }");
        Assert.Contains(ScenarioValidator.Validate(ScenarioFile.Parse(strayOnAmbient)),
            e => e.Contains("only 'offset' takes an offset"));
    }

    [Fact]
    public void A_scenario_with_no_read_back_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "api.player.create", "route": "POST /api/players", "why": "ok" }
            """, withRead: false));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("no read.* step"));
    }

    [Fact]
    public void A_step_with_no_reason_is_refused()
    {
        var doc = ScenarioFile.Parse(Minimal("""
            { "op": "api.player.create", "route": "POST /api/players" }
            """));
        Assert.Contains(ScenarioValidator.Validate(doc), e => e.Contains("why required"));
    }

    [Fact]
    public void A_zero_seed_is_refused()
    {
        var json = Minimal("""
            { "op": "api.player.create", "route": "POST /api/players", "why": "ok" }
            """).Replace("\"seed\": 20260922", "\"seed\": 0");
        Assert.Contains(ScenarioValidator.Validate(ScenarioFile.Parse(json)), e => e.Contains("seed: required"));
    }

    // ---- the selection grammar --------------------------------------------------------------- //

    [Fact]
    public void The_pointer_grammar_selects_by_member_index_slice_wildcard_and_key()
    {
        using var doc = JsonDocument.Parse("""
            { "items": [
                { "id": 7, "actor": { "instanceId": "a" }, "tags": ["x","y"] },
                { "id": 9, "actor": { "instanceId": "b" }, "tags": ["z"] }
            ] }
            """);
        var root = doc.RootElement;

        Assert.Equal("b", JsonPointer.Select(root, "$.items[1].actor.instanceId")!.Value.GetString());
        Assert.Equal("a", JsonPointer.Select(root, "$.items[-2].actor.instanceId")!.Value.GetString());
        Assert.Equal(2, JsonPointer.Select(root, "$.items[*]")!.Value.GetArrayLength());
        Assert.Equal(1, JsonPointer.Select(root, "$.items[1:2]")!.Value.GetArrayLength());
        Assert.Equal("a", JsonPointer.Select(root, "$.items[id==7].actor.instanceId")!.Value.GetString());
        Assert.Equal("b", JsonPointer.Select(root, "$.items[id=={wanted}].actor.instanceId",
            new Dictionary<string, string> { ["wanted"] = "9" })!.Value.GetString());

        // A pointer that selects nothing is a null reading, not a zero and not a throw.
        Assert.Null(JsonPointer.Select(root, "$.items[id==99]"));
        Assert.Null(JsonPointer.Select(root, "$.missing.deeper"));

        // An OUT-OF-RANGE index selects nothing too, and the tail of a null selection stays null. This is
        // the mechanism the corpus's roster floor rides on (`first-session-forward.json`'s
        // `expect.roster.held` points at `$.items[9].actor.instanceId`, because the summon asked for ten and
        // the vocabulary has no length check) -- so it is pinned here rather than assumed from the class
        // comment. `JsonPointer.cs:76-79` adds nothing when `k >= items.Count`, and `:107` returns null.
        Assert.Null(JsonPointer.Select(root, "$.items[9]"));
        Assert.Null(JsonPointer.Select(root, "$.items[9].actor.instanceId"));

        // A slice past the end CLAMPS rather than throwing (the corpus captures `$.items[0:2]` for its
        // squad): one element here, not an exception and not a padded array.
        Assert.Equal(1, JsonPointer.Select(root, "$.items[1:9]")!.Value.GetArrayLength());
    }

    /// <summary>
    /// The floor a scenario writes must be enforced by the mechanism it names, not by hope: `notEmpty`
    /// over a selection that found nothing FAILS. This is the corpus's roster floor in miniature --
    /// `expect.roster.held` asserts `$.items[9].actor.instanceId` is non-empty, which is an existence
    /// proof for ten elements only because a null selection fails `notEmpty` (`ScenarioExpectations.cs:168`
    /// answers false for null). The vocabulary's first direct test, deliberately narrow: the corpus
    /// exercises the rest through real runs.
    /// </summary>
    [Fact]
    public void NotEmpty_over_a_selection_that_found_nothing_fails()
    {
        using var doc = JsonDocument.Parse("""
            { "items": [ { "actor": { "instanceId": "a" } }, { "actor": { "instanceId": "b" } } ] }
            """);
        var root = doc.RootElement;

        var floor = new ScenarioStep
        {
            Op = "expect.roster.held",
            Reading = "read.roster",
            Path = "$.items[9].actor.instanceId",
            Check = "notEmpty",
            Why = "ten were asked for"
        };

        // The tenth element of a two-element array: the selection is null, and the check refuses it.
        Assert.Null(JsonPointer.Select(root, floor.Path!));
        var failure = ScenarioExpectations.Evaluate(floor, JsonPointer.Select(root, floor.Path!), null);
        Assert.NotNull(failure);
        Assert.Contains("is empty", failure!, StringComparison.Ordinal);

        // The first element of the same array: the same check passes, so the refusal above is about the
        // missing element and not about the check itself.
        var first = new ScenarioStep
        {
            Op = "expect.roster.held",
            Reading = "read.roster",
            Path = "$.items[0].actor.instanceId",
            Check = "notEmpty",
            Why = "the element is there"
        };
        Assert.Null(ScenarioExpectations.Evaluate(first, JsonPointer.Select(root, first.Path!), null));
    }

    [Fact]
    public void A_malformed_pointer_is_a_scenario_defect_not_an_empty_reading()
    {
        using var doc = JsonDocument.Parse("{}");
        var root = doc.RootElement;
        Assert.Throws<FormatException>(() => { _ = JsonPointer.Select(root, "items[0]"); });
        Assert.Throws<FormatException>(() => { _ = JsonPointer.Select(root, "$.items[oops]"); });
        Assert.Throws<FormatException>(() => { _ = JsonPointer.Select(root, "$.items[1"); });
    }

    [Fact]
    public void Canonical_json_sorts_members_and_normalizes_number_spelling()
    {
        using var a = JsonDocument.Parse("""{ "b": 1, "a": { "y": [1, 2], "x": "s" } }""");
        using var b = JsonDocument.Parse("""{ "a": { "x": "s", "y": [1, 2] }, "b": 1 }""");
        Assert.Equal(CanonicalJson.Of(a.RootElement), CanonicalJson.Of(b.RootElement));
        Assert.Equal("""{"a":{"x":"s","y":[1,2]},"b":1}""", CanonicalJson.Of(a.RootElement));

        using var one = JsonDocument.Parse("""{"n": 1}""");
        using var onePointZero = JsonDocument.Parse("""{"n": 1.0}""");
        Assert.Equal(CanonicalJson.Of(one.RootElement), CanonicalJson.Of(onePointZero.RootElement));
    }

    // ---- fixtures ----------------------------------------------------------------------------- //

    /// <summary>A minimal valid envelope plus the given step(s). `withRead` appends the read step the
    /// assertions in these tests address.</summary>
    static string Minimal(string step, bool withRead = true)
    {
        const string readStep =
            """{ "op": "read.souls", "route": "GET /api/souls/{playerId}", "why": "the funded balance" }""";
        var steps = withRead ? step + "," + Environment.NewLine + "    " + readStep : step;
        return $$"""
            {
              "id": "contract-test",
              "title": "contract test",
              "description": "a document for the validator's own tests",
              "seed": 20260922,
              "clock": { "mode": "ambient", "note": "ambient" },
              "steps": [
                {{steps}}
              ]
            }
            """;
    }

    /// <summary>Mirrors the E2E hosts' resolution of the shared fixture tree from the test bin output.</summary>
    static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "rpg-scenarios");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures", "rpg-scenarios"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures/rpg-scenarios");
    }
}
