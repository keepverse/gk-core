using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P2.1 — AptitudeTuning parser. Pure parser (tunables-ssot.md §7.2):
/// no file I/O inside Core, so this test class owns reading `data/tuning/aptitudes.v{n}.json` itself,
/// the same way `FusionRpg.Guard.Tests`/`FusionRpg.ElementEnumGen.Tests` read `gk-data/packs/fusion/data/seed/*` directly
/// rather than through the library under test.</summary>
public class AptitudeTuningTests
{
    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate repo root (Directory.Build.props not found above " + AppContext.BaseDirectory + ")");
    }

    static string ShippedJson() =>
        // passive-tree D55 (2026-09-06): v6 -> v7, hosts moved with it (RpgHost.cs/Program.cs) --
        // kept in sync so "the shipped file" here stays the one actually loaded in production.
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "aptitudes.v10.json"));

    // ── six-resource coverage (resource-hub-ssot.md, Phase 0 2026-09-02) ─────────────────────────

    /// <summary>The drift guard this defect existed for want of. `DerivedStatRegistry` loops
    /// <see cref="DerivedStatChannels.ResourceIds"/> and so never drifted; the aptitude edges were
    /// hand-maintained and did — `poise` had ZERO edges of any kind, which is why `guard-economy`
    /// was blocked (class-system P7.2), and `resource.efficiency` had four edges total against three
    /// families x six resources. Asserting COVERAGE, never a coefficient: what an edge is worth is a
    /// balance question, but whether the cell exists at all is not.</summary>
    [Theory]
    [InlineData("resource.max")]
    [InlineData("resource.regen")]
    [InlineData("resource.efficiency")]
    [InlineData("resource.restore")]
    public void EveryResourceIsFedInEveryResourceFamily(string family)
    {
        using var doc = JsonDocument.Parse(ShippedJson());
        var fed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in doc.RootElement.GetProperty("edges").EnumerateArray())
        {
            if (!edge.TryGetProperty("channel", out var ch)) continue;   // _group marker rows
            var channel = ch.GetString()!;
            if (channel.StartsWith(family + ".", StringComparison.Ordinal))
                fed.Add(channel[(family.Length + 1)..]);
        }

        var missing = DerivedStatChannels.ResourceIds.Where(r => !fed.Contains(r)).ToList();
        Assert.True(missing.Count == 0,
            $"{family} feeds {fed.Count}/{DerivedStatChannels.ResourceIds.Count} resources — missing: " +
            $"{string.Join(", ", missing)}. Every derived family that affects a resource must cover all " +
            "six (resource-hub-ssot.md, six-coverage rule). Add the edge with " +
            "`python tools/tuning/publish.py aptitudes --add-edge \"channel=...,source=...,kMilli=...\"`.");
    }

    /// <summary>The other half: a source named in an edge must be a real aptitude. Cheap, and it
    /// catches a typo'd `--add-edge` before it reaches a resolve.</summary>
    [Fact]
    public void EveryEdgeSourceIsAKnownAptitude()
    {
        using var doc = JsonDocument.Parse(ShippedJson());
        var known = AptitudeCatalog.All.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var edge in doc.RootElement.GetProperty("edges").EnumerateArray())
        {
            if (!edge.TryGetProperty("source", out var src)) continue;
            var s = src.GetString()!;
            if (!known.Contains(s)) unknown.Add(s);
        }
        Assert.True(unknown.Count == 0, "edges name sources that are not aptitudes: " + string.Join(", ", unknown));
    }

    // ── the real shipped file ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesTheShippedFile()
    {
        var tuning = AptitudeTuningLoader.Parse(ShippedJson());

        Assert.Equal(1, tuning.SchemaVersion);
        Assert.Equal(10, tuning.Version); // Phase 0, all 2026-09-02: v3 six-resource coverage, v4 resource.restore generalisation, v5 rename + Fortitude anchor; v6 (passive-tree C6, 2026-09-06) added pointEconomy.skillPointsPerThetaMilliByScope; v7 (D55, 2026-09-06) gave creatureType/aspect/uniqueCreature their real rates; v8 (A9 movement-actions, 2026-09-07) refreshed _meta.measurable's reader census now that move.range has its first reader; v9 (solid-enforcement `retire-atk` R3, 2026-09-18) dropped the two progression.bonus.atk edges and its familyRead row, published through tools/tuning/publish.py --remove-edge/--remove-key; v10 (species-progression SP6.0, R21, rebased onto v9 at the features/mega-merge merge 2026-09-20) adds read.layerWeightMilliByScope -- v9's own edges/familyRead removal is unchanged underneath it
        Assert.Equal(3, tuning.Grant.AptitudePointsPerThetaMilli);
        Assert.Equal(1, tuning.Grant.SkillPointsPerThetaMilli);
        Assert.Equal(11, tuning.PointEconomy.SkillPointsPerThetaMilliByScope[AllocationScope.Commander]); // D38: 10.40 corner-share, rounded up
        Assert.Equal(15, tuning.PointEconomy.SkillPointsPerThetaMilliByScope[AllocationScope.CreatureType]); // D55: {3,4,4,6} ratio vs commander=11
        Assert.Equal(15, tuning.PointEconomy.SkillPointsPerThetaMilliByScope[AllocationScope.Aspect]); // D55: ties creatureType, same ratio cell
        Assert.Equal(22, tuning.PointEconomy.SkillPointsPerThetaMilliByScope[AllocationScope.UniqueCreature]); // D55: {3,4,4,6} ratio vs commander=11
        Assert.Equal(100_000, tuning.Read.Contest.SpanPointsMilli); // 100.0 spanPoints * 1000
        Assert.Equal(1000, tuning.Read.Contest.ShareExponentMilli); // gamma = 1.0
        Assert.Equal(1000, tuning.Read.Magnitude.ShareExponentMilli); // gamma = 1.0
        Assert.Equal(374, tuning.Recovery.ScaleMilli);
        Assert.Equal(670, tuning.Recovery.TargetRecoveryShareMilli);
        Assert.Equal(new[] { "resource.regen", "combat.shield.regen" }, tuning.Recovery.Families);
        Assert.Equal(300, tuning.Mitigation.ScaleMilli); // class-system-todo.md P8.3, published v2 2026-08-27
        Assert.Equal(
            new[] { "combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal" },
            tuning.Mitigation.Families);
        // 48 -> 47 (solid-enforcement `retire-atk` R3, 2026-09-18): v9 dropped the
        // `familyRead` key naming the retired channel (`progression.bonus.atk`), and the loader drops
        // it from any archived version that still carries it (R2). Both halves agree on the parsed
        // surface; the ARCHIVED files keep their historical text.
        // pin: immutable gk-core/data/tuning/aptitudes.v10.json
        Assert.Equal(47, tuning.FamilyRead.Count);
        Assert.Equal(0, tuning.DroppedRetiredEdges); // v9 has nothing left to drop — R3 removed it
        Assert.DoesNotContain("progression.bonus.atk", tuning.FamilyRead.Keys);
    }

    [Fact]
    public void GroupDividersAreSkipped_524RealEdgesNot528RawEntries()
    {
        // The authored file has 530 array entries in `edges`; 4 are `_group` section dividers with
        // no `channel` key. class-system P1.5/P1.6's reader census counted 486 real edges in v2;
        // v3 added 32 for six-resource coverage (Phase 0, 2026-09-02 -- 12 max.poise + 12 regen.poise
        // + 8 efficiency), so 518; v4 added 7 more for resource.restore's five non-hp members, so 525; v5 added Fortitude as hp's restoration anchor, so 526. The literal is deliberate: it is what makes an accidental edge
        // addition visible, which is the same reason the census pinned it in the first place. v6
        // (passive-tree C6) touched only pointEconomy, not edges, so the count is unchanged.
        var tuning = AptitudeTuningLoader.Parse(ShippedJson());
        // 526 -> 524 (solid-enforcement `retire-atk` R3, 2026-09-18): v9 no longer AUTHORED the two
        // Might/Ferocity -> progression.bonus.atk edges. The raw array holds 528 entries (524 real +
        // 4 dividers), and the loader has nothing left to drop on the live file — the drop path is
        // what keeps the ARCHIVED v1-v8 loadable, and `AptitudeResolverTests`' theory covers those.
        // pin: immutable gk-core/data/tuning/aptitudes.v10.json
        Assert.Equal(524, tuning.Edges.Count);
        Assert.Equal(0, tuning.DroppedRetiredEdges);
    }

    [Fact]
    public void Edge_carriesResolvedReadMode()
    {
        var tuning = AptitudeTuningLoader.Parse(ShippedJson());
        var edge = Assert.Single(tuning.Edges, e => e.Channel == "combat.power.omni" && e.Source == "Might");
        Assert.Equal(2200, edge.KMilli);
        Assert.Equal(AptitudeReadMode.Magnitude, edge.Mode);
    }

    [Fact]
    public void FamilyOf_exactMatch()
    {
        var tuning = AptitudeTuningLoader.Parse(ShippedJson());
        Assert.Equal("combat.power", tuning.FamilyOf("combat.power"));
    }

    [Fact]
    public void FamilyOf_stripsOneAxisSuffix()
    {
        var tuning = AptitudeTuningLoader.Parse(ShippedJson());
        Assert.Equal("combat.power", tuning.FamilyOf("combat.power.omni"));
    }

    [Fact]
    public void FamilyOf_noMatchReturnsNull()
    {
        var tuning = AptitudeTuningLoader.Parse(ShippedJson());
        Assert.Null(tuning.FamilyOf("not.a.real.channel.at.all"));
    }

    [Fact]
    public void EveryEdgeChannel_isRegistered_inDerivedStatRegistry()
    {
        // spec-aptitude-tuning.md §6 test 4: a typo'd channel must not silently read zero forever --
        // it must fail to resolve against the SAME registry the resolver (P2.4/P2.5) will read from.
        var tuning = AptitudeTuningLoader.Parse(ShippedJson());
        var registry = DerivedStatRegistry.CreateDefault();
        var unresolved = tuning.Edges
            .Select(e => e.Channel)
            .Distinct(StringComparer.Ordinal)
            .Where(ch => !registry.TryResolveChannel(ch, out _))
            .ToList();
        Assert.True(unresolved.Count == 0, "unregistered edge channel(s): " + string.Join(", ", unresolved));
    }

    // ── R1: `Retired` is a closed vocabulary, pinned with its reason ──────────────────────────────

    /// <summary>`retire-atk` R1. Adding a retired id is a reviewed change to the design, not content
    /// growth, so this member set is CLOSED and pinning it is correct (validation-ssot.md). The pin
    /// exists so a second retirement cannot slip in unremarked beside the first.</summary>
    [Fact]
    public void RetiredChannels_isAClosedVocabulary()
    {
        Assert.Equal(new[] { "progression.bonus.atk" }, DerivedStatChannels.Retired.OrderBy(x => x).ToArray());
    }

    /// <summary>`retire-atk` R1/R2's boundary, asserted exactly: those two retire the id in the LOADER,
    /// and do NOT unregister it. Unregistering is R4's step, and it is deliberately not done there —
    /// `gk-data/packs/fusion/data/seed/derived-stats/catalog.json` still names the channel and `guard-class-system`'s G2
    /// checks the seed catalog against the registry, so R4 must move them together. (R3 has since
    /// published `aptitudes.v9` without the channel, which is why the LIVE file no longer needs the
    /// drop; the loader keeps it for the archived versions.)</summary>
    [Fact]
    public void A_retired_channel_isNotRegistered()
    {
        // (`retire-atk` R4, 2026-09-18: R2's loader drop and R4's unregistration are the two halves of
        // one state — nothing resolves what the loader drops. The doc comment above records the
        // R1/R2 intermediate state for history.)
        var registry = DerivedStatRegistry.CreateDefault();
        foreach (var retired in DerivedStatChannels.Retired)
        {
            Assert.False(registry.TryResolveChannel(retired, out _),
                $"{retired} is retired but still resolves — it would compose and be read");
            Assert.DoesNotContain(retired, registry.AllRegistered.Select(d => d.ChannelId));
        }
    }

    // ── rejection: every missing key names itself, never a default ────────────────────────────────

    [Theory]
    [InlineData("grant")]
    [InlineData("pointEconomy")]
    [InlineData("guardEconomy")]
    [InlineData("mitigation")]
    [InlineData("read")]
    [InlineData("recovery")]
    [InlineData("familyRead")]
    [InlineData("edges")]
    public void MissingTopLevelBlock_rejectsNamingIt(string key)
    {
        var doc = MinimalValidDoc();
        doc.Remove(key);
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGrantSubKey_rejectsNamingIt()
    {
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["grant"]).Remove("aptitudePointsPerTheta");
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("aptitudePointsPerTheta", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPointEconomyScope_rejectsNamingIt()
    {
        // class-system P6.1 — mirrors MissingGrantSubKey_rejectsNamingIt above: each of the four
        // per-scope rates is required on its own, not defaulted if absent (tunables-ssot.md §7.2).
        var doc = MinimalValidDoc();
        var byScope = (Dictionary<string, object>)((Dictionary<string, object>)doc["pointEconomy"])["aptitudePointsPerThetaMilliByScope"];
        byScope.Remove("aspect");
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("aspect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSkillPointsPerThetaScope_rejectsNamingIt()
    {
        // C6, spec-tree-state.md §3 (D34) -- once skillPointsPerThetaMilliByScope IS present, it is
        // exactly as strict as its aptitude-point sibling above: a half-authored table is a rejection,
        // never four rates with one silently missing.
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["pointEconomy"])["skillPointsPerThetaMilliByScope"] =
            new Dictionary<string, object> { ["commander"] = 11, ["creatureType"] = 4, ["uniqueCreature"] = 6 }; // aspect missing
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("aspect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentSkillPointsPerThetaScope_parsesToAnEmptyTable_notARejection()
    {
        // C6's own deliberate divergence from the sibling: a tuning file that never carries the key at
        // all (every fixture in this test class, and aptitudes.v1-v5.json) still parses -- the empty
        // table is not a guessed rate, and PointBudget.SkillPointsFor is what rejects a lookup against
        // it, naming the scope, rather than the parser inventing four numbers nobody authored.
        var doc = MinimalValidDoc(); // no skillPointsPerThetaMilliByScope key
        var tuning = AptitudeTuningLoader.Parse(Serialize(doc));
        Assert.Empty(tuning.PointEconomy.SkillPointsPerThetaMilliByScope);
    }

    // ── species-progression SP6.0 (species-layer-delivery step 6.1, R21): layerWeightMilliByScope ──

    [Fact]
    public void AbsentLayerWeightBlock_parsesToAnEmptyTable_notARejection()
    {
        // Archived pre-R21 files (aptitudes.v1-v8, still pinned by gk-forge/tools/CreatureSpeciesGen's own
        // aptitudes.v2.json bake) must stay loadable -- the same "absent is not a rejection" contract
        // AbsentSkillPointsPerThetaScope_... already proves for its sibling table.
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["read"]).Remove("layerWeightMilliByScope");
        var tuning = AptitudeTuningLoader.Parse(Serialize(doc));
        Assert.Empty(tuning.Read.LayerWeights.WeightMilliByScope);
    }

    [Fact]
    public void LayerWeight_ofAnyScope_onAnAbsentBlock_defaultsToTheUnweightedIdentity_neverRejects()
    {
        // Corrected 2026-09-19 against evidence: an earlier draft had this throw at first use, on the
        // theory that "never falls back to 1000 silently" should hold all the way to a live resolve
        // (matching PointBudget.SkillPointsFor's own sibling refusal). That broke every real regression
        // fixture that deliberately pins an exact pre-R21 file (aptitudes.v1.json, v2.json) and then
        // resolves against it -- TerminationGuardTests, DominanceGuardTests, BossBuildTests,
        // ZombossPatternTests, ChannelModsHubParityTests, DominanceBaselineTests,
        // ProveAptitudeJsonEmitTests all do exactly this, on purpose, to prove old behaviour never
        // drifts. 1000 is safe here specifically because Of() only ever sees an empty table when the
        // WHOLE BLOCK was absent (see AbsentLayerWeightBlock_parsesToAnEmptyTable_notARejection) -- a
        // PRESENT block missing one scope key already fails loudly at PARSE (below), so this default
        // can never mask a half-authored weight table.
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["read"]).Remove("layerWeightMilliByScope");
        var tuning = AptitudeTuningLoader.Parse(Serialize(doc));
        foreach (var scope in Enum.GetValues<AllocationScope>())
            Assert.Equal(1000, tuning.Read.LayerWeights.Of(scope));
    }

    [Fact]
    public void LayerWeightBlock_missingAScopeKey_rejectsNamingIt()
    {
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["read"])["layerWeightMilliByScope"] = new Dictionary<string, object>
        {
            ["commander"] = 500, ["creatureType"] = 667, ["aspect"] = 667,
            // uniqueCreature omitted
        };
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("uniqueCreature", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LayerWeightBlock_unknownScopeKey_rejectsNamingIt()
    {
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["read"])["layerWeightMilliByScope"] = new Dictionary<string, object>
        {
            ["commander"] = 500, ["creatureType"] = 667, ["aspect"] = 667, ["uniqueCreature"] = 1000,
            ["guildmaster"] = 999,
        };
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("guildmaster", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LayerWeightBlock_negativeWeight_rejectsNamingIt()
    {
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["read"])["layerWeightMilliByScope"] = new Dictionary<string, object>
        {
            ["commander"] = -1, ["creatureType"] = 667, ["aspect"] = 667, ["uniqueCreature"] = 1000,
        };
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("commander", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LayerWeightBlock_zeroWeight_loadsAndIsNotACap()
    {
        // "any non-negative value loads, including above 1000" -- zero is the boundary case: a
        // legitimate way to fully mute a layer's contribution, never refused as if it were a cap.
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["read"])["layerWeightMilliByScope"] = new Dictionary<string, object>
        {
            ["commander"] = 0, ["creatureType"] = 667, ["aspect"] = 667, ["uniqueCreature"] = 5000,
        };
        var tuning = AptitudeTuningLoader.Parse(Serialize(doc));
        Assert.Equal(0, tuning.Read.LayerWeights.Of(AllocationScope.Commander));
        Assert.Equal(5000, tuning.Read.LayerWeights.Of(AllocationScope.UniqueCreature)); // above 1000 loads fine
    }

    [Fact]
    public void ShippedFile_layerWeightOrderingContract_commanderSmallest_uniqueCreatureLargest()
    {
        // The ORDERING is the contract asserted here (spec: "commander < creatureType <= aspect <
        // uniqueCreature"); the literal values are tunables and are never pinned.
        var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath(FindRepoRoot())));
        var w = tuning.Read.LayerWeights;
        Assert.True(w.Of(AllocationScope.Commander) < w.Of(AllocationScope.CreatureType));
        Assert.True(w.Of(AllocationScope.CreatureType) <= w.Of(AllocationScope.Aspect));
        Assert.True(w.Of(AllocationScope.Aspect) < w.Of(AllocationScope.UniqueCreature));
    }

    /// <summary>Dynamic "whatever ships now" resolver, matching AptitudeMatrixTests.cs's own — kept
    /// as a private sibling copy rather than a shared static, since neither class exposes the other's
    /// helper and this file's own convention (ShippedJson, hardcoded v8) deliberately pins an OLD
    /// version for its historical `ParsesTheShippedFile` regression instead.</summary>
    static string LatestAptitudesPath(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, "data", "tuning");
        var best = Directory.GetFiles(dir, "aptitudes.v*.json")
            .Select(f => (Path: f, V: int.TryParse(
                Path.GetFileNameWithoutExtension(f).Split(".v").Last(), out var v) ? v : -1))
            .Where(x => x.V >= 0).OrderByDescending(x => x.V).FirstOrDefault();
        if (best.Path is null) throw new InvalidOperationException("no data/tuning/aptitudes.v*.json under " + dir);
        return best.Path;
    }

    [Fact]
    public void MissingContestSpanPoints_rejectsNamingIt()
    {
        var doc = MinimalValidDoc();
        var read = (Dictionary<string, object>)doc["read"];
        var contest = (Dictionary<string, object>)read["contest"];
        contest.Remove("spanPoints");
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("spanPoints", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRecoveryFamilies_rejects()
    {
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["recovery"]).Remove("families");
        Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
    }

    [Fact]
    public void MissingMitigationFamilies_rejects()
    {
        // class-system P8.3 -- mirrors MissingRecoveryFamilies_rejects above: Mitigation is
        // Recovery's own sibling dial (AptitudeMitigation's own doc comment) and required the same way.
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["mitigation"]).Remove("families");
        Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
    }

    [Fact]
    public void UnknownReadMode_rejectsNamingIt()
    {
        var doc = MinimalValidDoc();
        ((Dictionary<string, object>)doc["familyRead"])["combat.power"] = "sideways";
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("sideways", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeWithNoFamilyReadRow_rejectsNamingChannel()
    {
        var doc = MinimalValidDoc();
        var edges = (List<object>)doc["edges"];
        edges.Add(new Dictionary<string, object>
        {
            ["channel"] = "totally.unclassified.channel",
            ["source"] = "Might",
            ["kMilli"] = 100,
        });
        var ex = Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
        Assert.Contains("totally.unclassified.channel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeWithNoSource_rejects()
    {
        var doc = MinimalValidDoc();
        var edges = (List<object>)doc["edges"];
        edges.Add(new Dictionary<string, object> { ["channel"] = "combat.power.omni", ["kMilli"] = 100 });
        Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
    }

    [Fact]
    public void GroupDividerWithNoChannel_isSkipped_notARejection()
    {
        var doc = MinimalValidDoc();
        var edges = (List<object>)doc["edges"];
        edges.Insert(0, new Dictionary<string, object> { ["_group"] = "=== a section heading ===" });
        var tuning = AptitudeTuningLoader.Parse(Serialize(doc));
        Assert.Single(tuning.Edges); // the divider contributed nothing; the one real edge still parsed
    }

    [Fact]
    public void EmptyDocument_rejects()
    {
        Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(""));
    }

    [Fact]
    public void MalformedJson_rejects()
    {
        Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse("{ not json"));
    }

    [Fact]
    public void EmptyEdgesArray_rejects()
    {
        var doc = MinimalValidDoc();
        doc["edges"] = new List<object>();
        Assert.Throws<AptitudeTuningRejection>(() => AptitudeTuningLoader.Parse(Serialize(doc)));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    static string Serialize(object doc) => JsonSerializer.Serialize(doc);

    static Dictionary<string, object> MinimalValidDoc() => new()
    {
        ["schemaVersion"] = 1,
        ["version"] = 1,
        ["grant"] = new Dictionary<string, object> { ["aptitudePointsPerTheta"] = 3, ["skillPointsPerTheta"] = 1 },
        ["pointEconomy"] = new Dictionary<string, object>
        {
            ["aptitudePointsPerThetaMilliByScope"] = new Dictionary<string, object>
            {
                ["commander"] = 3, ["creatureType"] = 4, ["aspect"] = 4, ["uniqueCreature"] = 6,
            },
            ["respecPrice"] = 10,
        },
        ["guardEconomy"] = new Dictionary<string, object>
        {
            ["flatCommitCost"] = 50, ["absorbDrainSharePermille"] = 300, ["riposteShareCapPermille"] = 400,
        },
        ["mitigation"] = new Dictionary<string, object>
        {
            ["scaleMilli"] = 1000, ["families"] = new List<object> { "combat.defense" },
        },
        ["read"] = new Dictionary<string, object>
        {
            ["contest"] = new Dictionary<string, object> { ["spanPoints"] = 100.0, ["shareExponentMilli"] = 1000 },
            ["magnitude"] = new Dictionary<string, object> { ["shareExponentMilli"] = 1000 },
            ["layerWeightMilliByScope"] = new Dictionary<string, object>
            {
                ["commander"] = 1000, ["creatureType"] = 1000, ["aspect"] = 1000, ["uniqueCreature"] = 1000,
            },
        },
        ["recovery"] = new Dictionary<string, object>
        {
            ["scaleMilli"] = 374,
            ["targetRecoveryShareMilli"] = 670,
            ["families"] = new List<object> { "resource.regen" },
        },
        ["familyRead"] = new Dictionary<string, object> { ["combat.power"] = "magnitude" },
        ["edges"] = new List<object>
        {
            new Dictionary<string, object> { ["channel"] = "combat.power.omni", ["source"] = "Might", ["kMilli"] = 2200 },
        },
    };
}
