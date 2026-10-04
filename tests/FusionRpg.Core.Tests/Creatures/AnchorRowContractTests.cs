using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// The C# anchor consumer's contract, and — the reason most of this file exists — proof that the
/// predicate can both over- and under-report, because an enumeration that cannot fail proves
/// nothing.
/// </summary>
/// <remarks>
/// <para><b>The control is bidirectional and differential.</b> Every fixture goes through
/// <see cref="ConsumerRefusal"/>, which calls the consumer's OWN code in the order
/// <c>CreatureRecipeReconcileInput/Program.cs</c> runs it, and through
/// <see cref="AnchorRowContract.Violations"/>. The two must AGREE, both ways: empty exactly when
/// the consumer accepts, non-empty exactly when the consumer refuses. A predicate that over-reports
/// fails one half; one that under-reports fails the other.</para>
///
/// <para><b>No corpus, no sibling.</b> Inline anchors plus gk-core's OWN <c>data/tuning/</c>,
/// resolved through <see cref="CoreRoot"/>. Nothing here reads <c>gk-data</c> or <c>gk-forge</c> —
/// see <see cref="TheContractAndItsTestsMustNotNameAPrivateSibling"/>, which fails the build if one
/// ever creeps in. That is what lets this file run in a standalone gk-core clone.</para>
/// </remarks>
public class AnchorRowContractTests
{
    static AnchorContractTunings? _tunings;

    // Parsed once: `EveryNamedGuardHasABehaviouralFixture…` re-parses four JSON files per fixture,
    // and a test that is slow enough to be skipped is a test that stops being a gate.
    static AnchorContractTunings Tunings() => _tunings ??= new AnchorContractTunings(
        AptitudeTuningLoader.Parse(ReadTuning("aptitudes.v2.json")),
        PowerTuningLoader.Parse(ReadTuning("power-scale.v2.json")),
        CreatureShapeTuningLoader.Parse(ReadTuning("creature-shape.v1.json")),
        CreatureThreatTuningLoader.Parse(ReadTuning("creature-threat.v1.json")));

    /// <summary>gk-core's OWN balance surface. Never a sibling's: see the A11 test at the bottom.</summary>
    static string ReadTuning(string name) => File.ReadAllText(
        Path.Combine(new[] { CoreRoot.Path, "data", "tuning" }.Concat(new[] { name }).ToArray()));

    /// <summary>
    /// The consumer, called: <c>Program.cs</c>'s own four steps in its own order —
    /// <c>AnchorRowReader.ReadAll</c> → skip an <c>UnresolvedFields</c> species → <c>Expand</c> →
    /// the store's own projection → the <c>Validate</c> inside <c>CreatureSpeciesCatalog.Configure</c>.
    /// Returns the refusal message, or null when the consumer accepts.
    ///
    /// <para>This is the thing the predicate must agree with, so it must not be a second
    /// implementation of it: every step is a call into the production reader/expander/catalog. The
    /// only judgement is the ORDER, which is the tool's, cited to
    /// <c>gk-forge/tools/CreatureRecipeReconcileInput/Program.cs:84-140</c>.</para>
    /// </summary>
    static string? ConsumerRefusal(string anchorJson, AnchorContractTunings t)
    {
        IReadOnlyList<AnchorRow> anchors;
        try { anchors = AnchorRowReader.ReadAll("[" + anchorJson + "]"); }
        catch (AnchorRowRejection ex) { return ex.Message; }

        foreach (var anchor in anchors)
        {
            if (SpeciesExpander.UnresolvedFields(anchor).Count > 0) continue;
            try
            {
                var species = SpeciesExpander.Expand(anchor, t.Aptitudes, t.Power, t.Shape, t.Threat);
                CreatureSpeciesCatalog.Validate(
                    new[] { ConcreteSpeciesMapper.ToCreatureSpeciesDef(species) });
            }
            catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
            {
                return ex.Message;
            }
        }
        return null;
    }

    static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A legal anchor: every guard clear. Built from vocabularies the shipped tuning files
    /// actually declare, so it cannot rot into a refusal for a reason the corpus has none of.</summary>
    internal static string Legal(string speciesId = "pea_pult") =>
        $$"""
        {"speciesId":"{{speciesId}}","gameTypeId":20,"side":"plant","rarity":"grafted",
         "elementPrimary":"fire","elementSecondary":"ice","pure":false,
         "aptitudePrimary":"Might","aptitudeSecondary":"Fortitude",
         "attackTempo":"steady","reach":"short","deployMode":"PlantAvatar",
         "acquisition":["Summonable"],"variants":["normal"],"traits":["berserker"],
         "resourceProfile":["hp","stamina"],"family":["legume"],
         "targetPreference":"frontline","threatBand":"common","rank":"sprout",
         "speciesKind":"creature","basis":"stated"}
        """;

    /// <summary>Replace one key's value in the legal anchor, keeping the JSON well formed.</summary>
    static string Splice(string key, string jsonValue) => new System.Text.RegularExpressions.Regex(
        $"\"{key}\":(\"[^\"]*\"|\\[[^\\]]*\\]|true|false|-?\\d+)").Replace(Legal(), $"\"{key}\":{jsonValue}", 1);

    /// <summary>Drop one key and its value, taking whichever comma belongs to it. The comma has to be
    /// part of the match: `resourceProfile` is the LAST key, so eating only the following comma
    /// leaves a trailing one and the fixture stops being JSON — which is how a control turns into a
    /// test that asserts nothing but its own typo.</summary>
    static string Drop(string key) =>
        new System.Text.RegularExpressions.Regex(
            $"(\"\\n?{key}\":(\"[^\"]*\"|\\[[^\\]]*\\]|true|false|-?\\d+),?)|(,\"\\n?{key}\":(\"[^\"]*\"|\\[[^\\]]*\\]|true|false|-?\\d+))")
            .Replace(Legal(), m => m.Groups[3].Success ? "" : "");

    // ---------------------------------------------------------------- the coverage pin

    public static TheoryData<string> RefusalFixtures() => new()
    {
        // --- stage 2, the reader's presence and shape guards --------------------------------
        { Drop("speciesId") },                             // reader.str-field
        { Splice("reach", "7") },                          // reader.str-field (non-string)
        { Splice("side", "true") },                        // reader.str-field (non-string)
        { Splice("gameTypeId", "\"20\"") },                // reader.game-type-id (string)
        { Splice("gameTypeId", "true") },                  // reader.game-type-id (bool)
        { Splice("gameTypeId", "20.5") },                  // reader.game-type-id (fraction)

        // --- stage 3, every vocabulary ---------------------------------------------------------
        { Splice("rarity", "\"legendary\"") },
        { Splice("aptitudePrimary", "\"Telekinesis\"") },
        { Splice("aptitudeSecondary", "\"Telekinesis\"") },
        { Splice("attackTempo", "\"frantic\"") },
        { Splice("reach", "\"adjacent\"") },
        { Splice("elementPrimary", "\"plasma\"") },
        { Splice("elementSecondary", "\"plasma\"") },
        { Splice("deployMode", "\"plantavatar\"") },       // Enum.TryParse is ignoreCase: FALSE
        { Splice("acquisition", "[\"Summonable2\"]") },
        { Splice("rank", "\"ascendant\"") },

        // --- stage 4, the catalog. Five a transcription of stages 2-3 alone misses entirely -----
        { Splice("acquisition", "[]") },
        { Splice("side", "\"robot\"") },
        { Splice("variants", "[\"normal\",\"prime\"]") },
        // The plant offset is 50_000, so a negative gameTypeId only drops under the floor on the
        // ZOMBIE side. A fixture that got this wrong would read as a passing floor guard that never
        // fired — the exact shape of a green test proving nothing.
        { Splice("side", "\"zombie\"").Replace("\"gameTypeId\":20", "\"gameTypeId\":-1") },
    };

    /// <summary>
    /// THE CONTROL, both directions, over every refusal fixture: the predicate reports exactly when
    /// the consumer refuses, and the consumer's own message is among the predicate's — so the
    /// predicate cannot invent a DIFFERENT reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusalFixtures))]
    public void ThePredicateAndTheConsumerAgreeOnEveryRefusal(string anchorJson)
    {
        var t = Tunings();
        var consumer = ConsumerRefusal(anchorJson, t);
        Assert.NotNull(consumer);

        var reported = AnchorRowContract.Violations(El(anchorJson), t);
        Assert.NotEmpty(reported);
        Assert.Contains(reported, r => r.Contains(consumer!, StringComparison.Ordinal));
    }

    /// <summary>
    /// The coverage assertion, as a gate rather than a claim: every named guard must have a fixture
    /// that names it. A guard added to the reader without being added to
    /// <see cref="AnchorRowContract.GuardNames"/> fails the count; a guard named without a fixture
    /// fails here. Either way the enumeration cannot quietly shrink in either direction.
    /// </summary>
    [Fact]
    public void EveryNamedGuardHasABehaviouralFixtureAndEveryFixtureNamesItsGuard()
    {
        // The eleven Str keys are ONE named guard, and the contract must be reading the reader's own
        // list rather than carrying eleven names of its own.
        Assert.Equal(11, AnchorRowContract.StrFields.Count);
        Assert.Equal(11, AnchorRowContract.StrFields.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(17, AnchorRowContract.GuardNames.Count);
        Assert.Equal(17, AnchorRowContract.GuardNames.Distinct(StringComparer.Ordinal).Count());

        // Each Str key must actually be refused when absent — the eleven-fold expansion of that one
        // named guard. Done here, not in the theory, so a new twelfth key fails loudly HERE instead
        // of being reported as one more field nobody checked.
        foreach (var key in AnchorRowContract.StrFields)
            Assert.Contains(AnchorRowContract.Violations(El(Drop(key)), Tunings()),
                r => r == $"anchor: missing or non-string '{key}'");

        // Every fixture must resolve to exactly the guard it was written for, and the union must be
        // the named set. `reader.record-is-an-object` is the one guard with no fixture HERE: the
        // consumer does not refuse a non-object row, it throws (see the asymmetry test below), so it
        // is asserted there instead of smuggled into a symmetric theory that would have to lie.
        var covered = GuardedFixtures().Select(f => (string)f[0]).ToHashSet(StringComparer.Ordinal);
        covered.Add("reader.record-is-an-object");
        Assert.Equal(
            AnchorRowContract.GuardNames.OrderBy(g => g, StringComparer.Ordinal).ToArray(),
            covered.OrderBy(g => g, StringComparer.Ordinal).ToArray());

        // And every fixture in the wide set must name a guard at all — a refusal the predicate
        // cannot attribute is a refusal a reader has to re-derive by hand.
        foreach (var fixture in RefusalFixtures())
            Assert.Contains(AnchorRowContract.GuardNames, g => NamesGuardAny(fixture, g));
    }

    static bool NamesGuardAny(string anchorJson, string guard) =>
        AnchorRowContract.Violations(El(anchorJson), Tunings()).Any(r => NamesGuard(r, guard));

    /// <summary>The entries that hold real fixtures: one per named guard, grouped.</summary>
    public static TheoryData<string, string> GuardedFixtures() => new()
    {
        { "reader.str-field", Drop("speciesId") },
        { "reader.game-type-id", Splice("gameTypeId", "\"20\"") },
        { "expand.rarity", Splice("rarity", "\"legendary\"") },
        { "expand.aptitude-primary", Splice("aptitudePrimary", "\"Telekinesis\"") },
        { "expand.aptitude-secondary", Splice("aptitudeSecondary", "\"Telekinesis\"") },
        { "expand.attack-tempo", Splice("attackTempo", "\"frantic\"") },
        { "expand.reach", Splice("reach", "\"adjacent\"") },
        { "expand.element-primary", Splice("elementPrimary", "\"plasma\"") },
        { "expand.element-secondary", Splice("elementSecondary", "\"plasma\"") },
        { "expand.deploy-mode", Splice("deployMode", "\"plantavatar\"") },
        { "expand.acquisition-flag", Splice("acquisition", "[\"Summonable2\"]") },
        { "expand.rank", Splice("rank", "\"ascendant\"") },
        { "catalog.acquisition-none", Splice("acquisition", "[]") },
        { "catalog.side", Splice("side", "\"robot\"") },
        { "catalog.variant", Splice("variants", "[\"normal\",\"prime\"]") },
        { "catalog.creature-type-id-floor",
          Splice("side", "\"zombie\"").Replace("\"gameTypeId\":20", "\"gameTypeId\":-1") },
    };

    /// <summary>Each named guard fires on its own fixture, and the consumer agrees it is a refusal.
    /// This is the test that makes the coverage list mean something.</summary>
    [Theory]
    [MemberData(nameof(GuardedFixtures))]
    public void EachNamedGuardFiresOnItsOwnFixture(string guard, string anchorJson)
    {
        var t = Tunings();
        Assert.NotNull(ConsumerRefusal(anchorJson, t));

        var reported = AnchorRowContract.Violations(El(anchorJson), t);
        Assert.Contains(reported, r => NamesGuard(r, guard));
    }

    /// <summary>
    /// Entries the predicate must report NOTHING for, though several look like they should. Each is
    /// something the consumer tolerates on purpose; reporting any of them is the predicate crying
    /// wolf on the real corpus.
    /// </summary>
    public static TheoryData<string, string> AcceptedAnchors() => new()
    {
        // `pure: true` makes the secondary share ZERO by construction, so a garbage secondary is
        // INERT. `Expand` gates the edge check on `hasSecondary` for exactly this reason.
        { "a garbage secondary on a PURE anchor is inert",
          Splice("pure", "true").Replace("\"Fortitude\"", "\"Telekinesis\"") },

        // `Expand` maps a secondary equal to the primary to "no secondary", so the catalog's
        // `primary == secondary element` guard is UNREACHABLE down this path. Refusing it would
        // invent a defect the consumer cannot produce.
        { "elementSecondary == elementPrimary is mapped to none",
          Splice("elementSecondary", "\"fire\"") },

        // `StrArray` makes absent and empty the SAME empty list, and nothing downstream refuses an
        // empty one. `acquisition` is deliberately NOT in this list, because stage 4 does refuse it.
        { "variants may be empty", Splice("variants", "[]") },

        // `threatBand` and `speciesKind` degrade to null and never raise: a pre-fill or pre-mark
        // anchor must load rather than be refused for a missing key.
        { "threatBand may be absent", Drop("threatBand") },
        { "speciesKind may be absent", Drop("speciesKind") },

        // A non-string `rank` never reaches ResolveRank (the reader's own ValueKind == String test
        // makes it null), and the literal "unresolved" maps to null too. Both are skips, not raises.
        { "rank may be absent", Drop("rank") },
        { "a non-string rank is never a violation", Splice("rank", "7") },

        // `resourceProfile`, `family` and `basis` are read by NO stage of the C# path at all.
        // `resourceProfile` is absent from 314 of the 904 committed entries and every one of them
        // loads: a guard here would refuse real anchors.
        { "resourceProfile may be absent", Drop("resourceProfile") },
        { "family and basis are not this consumer's business", Drop("family") },

        // `targetPreference` is presence-guarded and nothing more — no stage of this path reads it
        // through a vocabulary — so a present-but-unknown value is not a defect. (`side` IS
        // vocabulary-checked one stage later, at the catalog; see `catalog.side`.)
        { "targetPreference is presence-guarded only", Splice("targetPreference", "\"wherever\"") },

        // MEASURED, not assumed: `vampiric` is in no catalog, and the anchor's own `traits` is OPEN
        // LLM flavour text. `ConcreteSpeciesMapper.ToCreatureSpeciesDef` substitutes
        // `CreatureTraitPoolCuration.PickFor(...)` for it, so the flavour string never reaches
        // `Validate`'s closed-vocabulary check. A `catalog.trait` guard would refuse 904 real
        // anchors to police a value this consumer deliberately does not read.
        { "an unknown anchor trait is never a violation",
          Splice("traits", "[\"Projectile-launching\",\"vampiric\"]") },
    };

    [Theory]
    [MemberData(nameof(AcceptedAnchors))]
    public void ThePredicateReportsNothingForAnEntryTheConsumerAccepts(string because, string anchorJson)
    {
        var t = Tunings();
        var consumer = ConsumerRefusal(anchorJson, t);
        Assert.True(consumer is null, $"the consumer refused this anchor ({because}): {consumer}");

        var reported = AnchorRowContract.Violations(El(anchorJson), t);
        Assert.True(reported.Count == 0,
            $"the predicate reported {reported.Count} violation(s) on an anchor the consumer " +
            $"accepts ({because}): {string.Join(" | ", reported)}");
    }

    static bool NamesGuard(string message, string guard) => guard switch
    {
        "reader.str-field" => message.Contains("missing or non-string", StringComparison.Ordinal),
        "reader.game-type-id" => message.Contains("non-integer", StringComparison.Ordinal),
        "reader.record-is-an-object" => message.Contains("must be an object", StringComparison.Ordinal),
        "expand.rarity" => message.Contains("CreatureRarity", StringComparison.Ordinal),
        "expand.aptitude-primary" => message.Contains("aptitudePrimary", StringComparison.Ordinal),
        "expand.aptitude-secondary" => message.Contains("aptitudeSecondary", StringComparison.Ordinal),
        "expand.attack-tempo" => message.Contains("attackTempo", StringComparison.Ordinal),
        "expand.reach" => message.Contains("reach ", StringComparison.Ordinal),
        "expand.element-primary" => message.Contains("elementPrimary", StringComparison.Ordinal),
        "expand.element-secondary" => message.Contains("elementSecondary", StringComparison.Ordinal),
        "expand.deploy-mode" => message.Contains("CreatureDeployMode", StringComparison.Ordinal),
        "expand.acquisition-flag" => message.Contains("CreatureAcquisition", StringComparison.Ordinal),
        "expand.rank" => message.Contains("CreatureRank", StringComparison.Ordinal),
        "catalog.acquisition-none" => message.Contains("no acquisition flags", StringComparison.Ordinal),
        "catalog.side" => message.Contains("side must be", StringComparison.Ordinal),
        "catalog.variant" => message.Contains("unknown variant", StringComparison.Ordinal),
        "catalog.creature-type-id-floor" => message.Contains("below floor", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(guard), guard, "an unnamed guard"),
    };

    // ---------------------------------------------------------------- the skip is not a refusal

    [Fact]
    public void AnUnresolvedSentinelSkipsTheSpeciesAndIsNeverAViolation()
    {
        var t = Tunings();
        foreach (var key in AnchorRowContract.SkipFields)
        {
            var el = El(Splice(key, $"\"{AnchorRowContract.UnresolvedSentinel}\""));
            Assert.Equal(new[] { key }, AnchorRowContract.SkippedFields(el));
            Assert.Empty(AnchorRowContract.Violations(el, t));
        }
    }

    [Fact]
    public void TheSentinelComparisonIsOrdinalSoCasingIsNotTheSentinel()
    {
        // `UnresolvedFields` compares `== "unresolved"`, so "Unresolved" is a real, refused value.
        var t = Tunings();
        var el = El(Splice("rarity", "\"Unresolved\""));
        Assert.Empty(AnchorRowContract.SkippedFields(el));
        Assert.NotEmpty(AnchorRowContract.Violations(el, t));
    }

    // ---------------------------------------------------------------- ordering rules

    [Fact]
    public void APresenceDefectShadowsItsOwnFieldsMembershipGuard()
    {
        // `attackTempo: ""` is present-and-wrong and fails ONLY at the table lookup; an ABSENT
        // attackTempo fails ONLY at presence. Two defects with two different repairs, so they must
        // not collapse into one word — and neither may report the other's guard.
        var t = Tunings();
        var empty = AnchorRowContract.Violations(El(Splice("attackTempo", "\"\"")), t);
        Assert.Contains(empty, r => r.Contains("attackTempo", StringComparison.Ordinal)
                                    && r.Contains("creature-shape.v1.json", StringComparison.Ordinal));
        Assert.DoesNotContain(empty, r => r.Contains("missing or non-string", StringComparison.Ordinal));

        var absent = AnchorRowContract.Violations(El(Drop("attackTempo")), t);
        Assert.Contains(absent, r => r == "anchor: missing or non-string 'attackTempo'");
        Assert.DoesNotContain(absent, r => r.Contains("creature-shape.v1.json", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryViolationIsReportedNotJustTheFirst()
    {
        // The consumer is fail-fast and reports WHERE IT STOPPED. That is the whole reason this
        // predicate exists, so it is asserted first: eight independent defects in one entry, eight
        // messages. A repair programme that found one per run is the failure mode being prevented.
        var t = Tunings();
        var anchor = Splice("rarity", "\"legendary\"")
            .Replace("\"attackTempo\":\"steady\"", "\"attackTempo\":\"frantic\"")
            .Replace("\"reach\":\"short\"", "\"reach\":\"adjacent\"")
            .Replace("\"elementPrimary\":\"fire\"", "\"elementPrimary\":\"plasma\"")
            .Replace("\"elementSecondary\":\"ice\"", "\"elementSecondary\":\"plasma\"")
            .Replace("\"deployMode\":\"PlantAvatar\"", "\"deployMode\":\"plantavatar\"")
            .Replace("\"aptitudePrimary\":\"Might\"", "\"aptitudePrimary\":\"Telekinesis\"")
            .Replace("\"rank\":\"sprout\"", "\"rank\":\"ascendant\"");

        var reported = AnchorRowContract.Violations(El(anchor), t);
        Assert.Equal(8, reported.Count);

        // ... while the consumer still stops at the first, which is why the eight matter.
        Assert.NotNull(ConsumerRefusal(anchor, t));
    }

    [Fact]
    public void OmittingTheTuningsIsReportedRatherThanSilentlyGreen()
    {
        // A predicate that cannot say "I did not check" can only ever be confidently wrong.
        Assert.Contains(AnchorRowContract.Violations(El(Legal())),
            r => r.Contains("no tunings supplied", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the non-object asymmetry

    /// <summary>
    /// A non-object element is the one place the consumer does NOT refuse cleanly:
    /// <c>JsonElement.TryGetProperty</c> throws <see cref="InvalidOperationException"/> rather than
    /// raising <see cref="AnchorRowRejection"/>, so it escapes <c>ReadAll</c> as an unhandled
    /// exception instead of a refusal the tool can print and return 1 from.
    ///
    /// <para>Asserted as an asymmetry rather than papered over: the contract still reports it (a bad
    /// row must become a refusal, never an exception out of the run), and this test records that the
    /// consumer's own behaviour is the exception. Anything else here would be a claim about the
    /// consumer that it does not support.</para>
    /// </summary>
    [Fact]
    public void ANonObjectRowIsARefusalForTheContractAndAnUncaughtExceptionForTheConsumer()
    {
        var el = El("\"not-an-object\"");
        var reported = AnchorRowContract.Violations(el, Tunings());
        var only = Assert.Single(reported);
        Assert.Contains("must be an object", only, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(
            () => AnchorRowReader.ReadAll("[\"not-an-object\"]"));
    }

    // ---------------------------------------------------------------- file-level guards

    [Fact]
    public void TheTwoFileLevelGuardsFireAndNameTheFile()
    {
        AssertHasSubstring(AnchorRowContract.FileViolations("{ not json", "a.json"), "not valid JSON");
        AssertHasSubstring(AnchorRowContract.FileViolations("{\"speciesId\":\"x\"}", "a.json"),
            "expected a top-level array");
    }

    /// <summary>"One of these messages says X". Named because xUnit's
    /// <c>Assert.Contains(collection, expected)</c> does not resolve against an
    /// <see cref="IReadOnlyList{T}"/>, and a collection overload that silently does not compile is
    /// worse than three honest lines.</summary>
    static void AssertHasSubstring(IReadOnlyList<string> messages, string substring) =>
        Assert.True(messages.Any(m => m.Contains(substring, StringComparison.Ordinal)),
            $"expected one of [{string.Join(" | ", messages)}] to contain '{substring}'");

    [Fact]
    public void AFileScanNamesTheFileAndTheSpecies()
    {
        var only = AnchorRowContract.FileViolations(
            "[" + Splice("rarity", "\"legendary\"") + "]", "seed/pea.json");
        var onlyMessage = Assert.Single(only);
        Assert.Contains("seed/pea.json", onlyMessage, StringComparison.Ordinal);
        Assert.Contains("pea_pult", onlyMessage, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- document-level guards

    [Fact]
    public void CorpusViolationsFindsEveryDuplicateRatherThanTheFirst()
    {
        // The consumer raises at the FIRST duplicate and stops, which is exactly why a roster scan
        // has to re-derive this one pair: it is asked to COUNT.
        var roster = new (string, string, int)[]
        {
            ("a.json", "pea", 10020), ("a.json", "pea", 10021),
            ("a.json", "sun", 10020), ("a.json", "pea", 10022),
        };
        var reported = AnchorRowContract.CorpusViolations(roster);
        // Two repeats of `pea` past the first, and one repeat of 10020 past the first. Three, not
        // two — and asserting the split rather than the total, because a total alone would pass on a
        // predicate that reported one class twice and the other not at all.
        Assert.Equal(2, reported.Count(r => r.Contains("Duplicate species id", StringComparison.Ordinal)));
        Assert.Equal(1, reported.Count(r => r.Contains("Duplicate creatureTypeId 10020", StringComparison.Ordinal)));
        Assert.Equal(3, reported.Count);
    }

    // ---------------------------------------------------------------- A11

    /// <summary>
    /// gk-core must build and test with every private sibling absent, so a gk-core test that reads
    /// the creature corpus out of gk-data is a defect rather than a convenience: it breaks a
    /// standalone clone. This is the gate that keeps the contract and its tests free of one.
    ///
    /// <para><b>Scope, stated rather than implied.</b> It scans CODE lines only, and its token list
    /// is spelled in fragments. A comment may name a sibling — the citation to the tool whose load
    /// order this file mirrors is exactly the evidence worth keeping — and a gate that flags its own
    /// token list is a gate that gets deleted rather than fixed. The fragments below are the cost of
    /// that: <c>"gk-" + "data"</c> cannot match itself, where <c>"gk-data"</c> would. What must not
    /// exist is a READ, and a read is code. The other half of the argument — that a standalone clone
    /// actually builds and passes these tests — is demonstrated outside the repository, not asserted
    /// here.</para>
    /// </summary>
    [Fact]
    public void TheContractAndItsTestsMustNotNameAPrivateSibling()
    {
        // Spelled in fragments so this file does not contain the tokens it is searching for. Every
        // entry is a real sibling reference; the concatenation is what keeps the scan self-consistent.
        var forbidden = new[]
        {
            "gk-" + "data", "gk-" + "forge",
            "Content" + "Root", "KeepverseRoots" + ".Content",
        };
        var subject = new[]
        {
            Path.Combine(CoreRoot.Path, "src", "FusionRpg.Core", "Creatures", "Generation",
                         "AnchorRowContract.cs"),
            Path.Combine(CoreRoot.Path, "tests", "FusionRpg.Core.Tests", "Creatures",
                         "AnchorRowContractTests.cs"),
        };
        foreach (var file in subject)
        {
            Assert.True(File.Exists(file), $"not found: {file}");
            var code = string.Join('\n', File.ReadAllLines(file)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (var token in forbidden)
                Assert.False(code.Contains(token, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} names '{token}' in code; a gk-core test must not " +
                    "need a private sibling to exist");
        }
    }
}