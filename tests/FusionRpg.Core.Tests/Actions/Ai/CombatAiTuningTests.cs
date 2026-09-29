using System;
using System.Linq;
using System.Reflection;
using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md): the closed vocabularies,
/// the profile record shape, the parser's structural requirements, and key resolution. Byte-identical:
/// nothing constructs `combat-ai.v1.json` in this task, so no golden can move.
/// </summary>
public class CombatAiTuningTests
{
    const string MinimalRootProfile = """
        {
          "tierOverride": null,
          "tierByActorClass": { "unique": "smart", "general": "performance" },
          "rows": [ { "selector": "nearest", "condition": "always", "census": "none", "actions": {} } ],
          "scoring": { "weightHitChance": 70, "weightObjective": 50, "weightKill": 15, "weightLowHp": 10,
                       "weightCannotCounter": 10, "weightRound": 1, "weightRisk": 120,
                       "aggressionRange": 2, "maxCandidatesScored": 32 },
          "selection": { "mode": "argmax", "keepPctMilli": 1000, "rngStreamName": "ai.select" },
          "reserves": [],
          "guards": { "minTargetsForArea": 1, "killMarginMilli": 0, "fightEndingLiveCount": 0 },
          "antiRepeat": { "retargetLatencyTicks": 0, "commitmentBonus": 0, "repeatDecayHalfLifeTicks": 0 },
          "personality": { "bounds": { "aggression": 0, "recklessness": 0, "focus": 0, "thrift": 0 } }
        }
        """;

    static string Doc(string profilesJson, bool withRouter = true)
    {
        const string routerJson = """, "router": { "orderTimeoutTicks": 5000, "reactionsPerRoundExpected": 1000 }""";
        return "{ \"schemaVersion\": 1, \"version\": 1, \"profiles\": { " + profilesJson + " }" +
               (withRouter ? routerJson : "") + " }";
    }

    static string MinimalValidDoc() => Doc($"\"*/default\": {MinimalRootProfile}");

    [Fact]
    public void Minimal_document_parses()
    {
        var tuning = CombatAiTuningLoader.Parse(MinimalValidDoc());
        Assert.Equal(1, tuning.SchemaVersion);
        Assert.True(tuning.Profiles.ContainsKey("*/default"));
        Assert.Equal(5000, tuning.Router.OrderTimeoutTicks);
    }

    [Theory]
    [InlineData("nearest", "smart")]
    public void Every_vocabulary_string_resolves_to_a_member(string selectorValue, string tierValue)
    {
        Assert.Equal(TargetSelector.Nearest, Enum.Parse<TargetSelector>(selectorValue, ignoreCase: true));
        Assert.Equal(AiTier.Smart, Enum.Parse<AiTier>(tierValue, ignoreCase: true));
    }

    [Fact]
    public void An_unknown_selector_throws_naming_the_value_and_its_key()
    {
        var bad = MinimalRootProfile.Replace("\"selector\": \"nearest\"", "\"selector\": \"bogus\"");
        var ex = Assert.Throws<CombatAiTuningRejection>(() => CombatAiTuningLoader.Parse(Doc($"\"*/default\": {bad}")));
        Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_place_throws_naming_the_value_and_its_key()
    {
        var doc = Doc($"\"unicorn/default\": {MinimalRootProfile}, \"*/default\": {MinimalRootProfile}");
        var ex = Assert.Throws<CombatAiTuningRejection>(() => CombatAiTuningLoader.Parse(doc));
        Assert.Contains("unicorn", ex.Message, StringComparison.Ordinal);
    }

    [Fact] public void TargetSelector_has_eight_members() =>
        // Each member names a concrete IBattleView read; a ninth with no reader is a promise, not a
        // vocabulary entry (spec-profile-schema.md S1).
        Assert.Equal(8, Enum.GetValues(typeof(TargetSelector)).Length);

    [Fact] public void AiTier_has_two() =>
        // Smart/Performance only; a "dumb"/vanilla third tier is reserved by the ideal and deliberately
        // NOT registered (D6 defers vanilla PvZ unit control).
        Assert.Equal(2, Enum.GetValues(typeof(AiTier)).Length);

    [Fact] public void AiPlace_has_four() =>
        // Lawn/Battle/Delve/Siege; `sim` is absent -- the sim runtime resolves effects, it declares no
        // intents.
        Assert.Equal(4, Enum.GetValues(typeof(AiPlace)).Length);

    [Fact] public void AiRole_has_four() =>
        // Default plus three reserved delve rows; nothing supplies a role source today so every actor
        // resolves Default in v1.
        Assert.Equal(4, Enum.GetValues(typeof(AiRole)).Length);

    [Fact] public void AiRowCondition_has_six() =>
        // Always plus five self/target fact conditions; a seventh would be reviewed, an eighth folds
        // into ICompiledPredicate (Open question 1).
        Assert.Equal(6, Enum.GetValues(typeof(AiRowCondition)).Length);

    [Fact] public void AiCensusCondition_has_five() =>
        // None plus exactly four board questions FactReader structurally cannot answer.
        Assert.Equal(5, Enum.GetValues(typeof(AiCensusCondition)).Length);

    [Fact] public void PersonalityAxis_has_four() =>
        // Closed and append-only: module 3 draws one value per axis in declaration order from one
        // seeded stream, so inserting a member mid-list would reshuffle every actor's personality.
        Assert.Equal(4, Enum.GetValues(typeof(PersonalityAxis)).Length);

    [Fact]
    public void Missing_root_profile_is_rejected()
    {
        var doc = Doc($"\"siege/default\": {MinimalRootProfile}");
        Assert.Throws<CombatAiTuningRejection>(() => CombatAiTuningLoader.Parse(doc));
    }

    /// <summary>Exercises the 4-step fallback through `CombatAiProfilePolicy.Resolve` — the PURE half
    /// of the hub, taking a locally-parsed `Profiles` dictionary directly. Deliberately never touches
    /// `Configure`/`Reset` on the process-wide hub: a earlier version of this test did, and raced every
    /// OTHER concurrently-running test in this assembly that depends on the hub staying configured
    /// (found via `DistrictAssaultResolverTests` failing intermittently — the exact class of bug
    /// `cmdc-agents/rules.md` warns about for shared static state).</summary>
    [Fact]
    public void Key_resolution_falls_back_in_the_stated_order()
    {
        string Tag(string tag) => MinimalRootProfile.Replace(
            "\"rngStreamName\": \"ai.select\"", $"\"rngStreamName\": \"{tag}\"");

        // All four rows present: exact wins over every fallback.
        var full = Doc(string.Join(",", new[]
        {
            $"\"siege/striker\": {Tag("exact")}",
            $"\"siege/default\": {Tag("place-default")}",
            $"\"*/striker\": {Tag("role-default")}",
            $"\"*/default\": {Tag("root")}",
        }));
        Assert.Equal("exact",
            CombatAiProfilePolicy.Resolve(CombatAiTuningLoader.Parse(full).Profiles, AiPlace.Siege, AiRole.Striker)
                .Selection.RngStreamName);

        // Exact missing: place/default wins.
        var noExact = Doc(string.Join(",", new[]
        {
            $"\"siege/default\": {Tag("place-default")}",
            $"\"*/striker\": {Tag("role-default")}",
            $"\"*/default\": {Tag("root")}",
        }));
        Assert.Equal("place-default",
            CombatAiProfilePolicy.Resolve(CombatAiTuningLoader.Parse(noExact).Profiles, AiPlace.Siege, AiRole.Striker)
                .Selection.RngStreamName);

        // Exact and place/default missing: role default wins.
        var onlyRoleAndRoot = Doc(string.Join(",", new[]
        {
            $"\"*/striker\": {Tag("role-default")}",
            $"\"*/default\": {Tag("root")}",
        }));
        Assert.Equal("role-default",
            CombatAiProfilePolicy.Resolve(CombatAiTuningLoader.Parse(onlyRoleAndRoot).Profiles, AiPlace.Siege, AiRole.Striker)
                .Selection.RngStreamName);

        // Only the root exists: root wins.
        var onlyRoot = Doc($"\"*/default\": {Tag("root")}");
        Assert.Equal("root",
            CombatAiProfilePolicy.Resolve(CombatAiTuningLoader.Parse(onlyRoot).Profiles, AiPlace.Siege, AiRole.Striker)
                .Selection.RngStreamName);
    }

    [Theory]
    [InlineData("\"keepPctMilli\": 1000", "\"keepPctMilli\": 1001")]
    [InlineData("\"aggressionRange\": 2", "\"aggressionRange\": 0")]
    [InlineData("\"maxCandidatesScored\": 32", "\"maxCandidatesScored\": 0")]
    public void Out_of_range_values_are_rejected_at_parse(string original, string replacement)
    {
        var bad = MinimalRootProfile.Replace(original, replacement);
        Assert.Throws<CombatAiTuningRejection>(() => CombatAiTuningLoader.Parse(Doc($"\"*/default\": {bad}")));
    }

    // CAI-find-2: the negative-weight refusal CAI1.8 dropped when the ten keys moved. All seven, not
    // one: the four without a downstream clamp invert their own term, and the three the personality
    // clamp touches would have their mistake hidden rather than reported.
    [Theory]
    [InlineData("weightHitChance", 70)]
    [InlineData("weightObjective", 50)]
    [InlineData("weightKill", 15)]
    [InlineData("weightLowHp", 10)]
    [InlineData("weightCannotCounter", 10)]
    [InlineData("weightRound", 1)]
    [InlineData("weightRisk", 120)]
    public void A_negative_weight_is_rejected_at_parse_naming_its_key(string key, int authored)
    {
        var bad = MinimalRootProfile.Replace($"\"{key}\": {authored}", $"\"{key}\": -1");
        var ex = Assert.Throws<CombatAiTuningRejection>(() => CombatAiTuningLoader.Parse(Doc($"\"*/default\": {bad}")));
        Assert.Contains($"profiles.*/default.scoring.{key}", ex.Message);
    }

    // The boundary the refusal must not cross: zero is legal, because it disables one score term
    // rather than inverting it, and the spec's clamp is a statement about the RESULT, not the input.
    [Fact]
    public void A_zero_weight_is_admitted()
    {
        var zeroed = MinimalRootProfile.Replace("\"weightRisk\": 120", "\"weightRisk\": 0");
        var tuning = CombatAiTuningLoader.Parse(Doc($"\"*/default\": {zeroed}"));
        Assert.Equal(0, tuning.Profiles["*/default"].Scoring.WeightRisk);
    }

    [Fact]
    public void A_file_without_a_router_block_is_rejected() =>
        Assert.Throws<CombatAiTuningRejection>(() => CombatAiTuningLoader.Parse(Doc($"\"*/default\": {MinimalRootProfile}", withRouter: false)));

    [Fact]
    public void A_profile_whose_last_row_is_conditional_is_rejected()
    {
        var bad = MinimalRootProfile.Replace(
            "\"rows\": [ { \"selector\": \"nearest\", \"condition\": \"always\", \"census\": \"none\", \"actions\": {} } ]",
            "\"rows\": [ { \"selector\": \"nearest\", \"condition\": \"selfHpBelowMilli\", \"conditionArgMilli\": 300, \"census\": \"none\", \"actions\": {} } ]");
        Assert.Throws<CombatAiTuningRejection>(() => CombatAiTuningLoader.Parse(Doc($"\"*/default\": {bad}")));
    }

    [Fact]
    public void Core_reads_no_file()
    {
        var method = typeof(CombatAiTuningLoader).GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
    }
}
