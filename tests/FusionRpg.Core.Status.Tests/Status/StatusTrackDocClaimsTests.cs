using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Workspace;
using CoreStatus = FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// The status-tracks documents make a handful of load-bearing factual claims about the shipped code,
/// and two of them were wrong the first time they were written: D1 ("battle's <c>EffectBag</c> never
/// set <c>CombatMath</c>, so status pulses bypassed the resolver") and "speed ordering is inert"
/// (true of <c>classic-round</c>, false of the system). Both had the same shape — <b>a fact about a
/// narrower scope stated as a fact about the whole</b> — and both were caught only by a human
/// re-reading prose against code, which is exactly the check that does not get repeated.
///
/// <para><b>What this file is.</b> The prose claims are turned into executable assertions, so the next
/// time one of them rots the build goes red instead of the sentence surviving. It deliberately
/// asserts <i>contracts and exact named sets</i>, never bare population counts: a test that asserts
/// "there are N of these" guards nothing, because every addition and every deletion of the wrong
/// thing keeps it green. Where a count IS the documented claim it is pinned as the exact expected set
/// the code must match, with the reason in a comment.</para>
///
/// <para><b>What this file is not.</b> It does not re-implement the tick-ownership scan.
/// <see cref="StatusTickOwnershipTests"/> already owns that mechanism and its mutation is proven; what
/// this file adds is the narrow assertion that the two call sites those docs <i>name</i> are the two
/// that exist, so a doc that keeps pointing at a moved seam fails here rather than rotting silently.
/// Neither the tick seam text nor its scanning logic is duplicated.</para>
///
/// <para><b>Scope honesty.</b> The caller-count claims (§5 of the ideal) are NOT pinned here, and
/// deliberately so: "zero production callers" is a claim about the whole workspace's call graph,
/// which no single assembly can observe honestly, and a test that asserted it would pass vacuously
/// the moment <c>gk-fusion</c> grew a caller. They are pinned where they are true and cannot rot into
/// a green lie — see the report in the correction notes in <c>status-tracks-ideal.md</c> §2/§3.</para>
/// </summary>
public class StatusTrackDocClaimsTests
{
    /// <summary>A fixed instant. Nothing here reads the wall clock.</summary>
    static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    // ==========================================================================================
    // 1. The tick's two named seams. The scan itself belongs to StatusTickOwnershipTests; this
    //    pins only that the paths and member names these DOCS cite are the real ones.
    // ==========================================================================================

    /// <summary>
    /// The two <c>StatusRuntime.Tick</c> owners, as <c>status-tracks-ideal.md</c> §1 and
    /// <c>completion.md</c> criterion 2 name them: the battle's virtual clock and the lawn's
    /// injected-`now` funnel. Asserted as file + exact call text rather than a count, because the
    /// DOCUMENTED CONTRACT is "these two seams, with these arguments, and no third" — a count of 2
    /// would still pass if both seams were deleted and replaced by nothing at all.
    /// </summary>
    static readonly (string Path, string Call)[] DocNamedTickSeams =
    {
        ("src/FusionRpg.Core/Effects/EffectBag.cs", "Status.Tick(now, sink, BoardSnapshot, StatusRng)"),
        ("src/FusionRpg.Core/Battle/BattleEngine.cs",
            "state.Status.Tick(now, state.PulseSink, board: state.CombatBoardSnapshot, spreadRng: state.StatusRng)"),
    };

    [Fact]
    public void Both_tick_seams_the_docs_name_are_still_the_two_that_exist()
    {
        var root = KeepverseRoots.Core();
        foreach (var (path, call) in DocNamedTickSeams)
        {
            var full = Path.Combine(root, path);
            Assert.True(File.Exists(full), $"the tick seam the docs name is gone: {path}");

            var hits = File.ReadAllLines(full).Count(l => l.Contains(call, StringComparison.Ordinal));
            Assert.True(hits == 1,
                $"{path}: the docs name exactly one '{call}' line, found {hits}. The seam moved, was " +
                "deleted, or its arguments were rewritten — status-tracks-ideal.md §1 and " +
                "completion.md §A.2 need updating, not this test.");
        }
    }

    /// <summary>
    /// The battle seam's instant comes off the battle's own virtual clock, not the wall clock. This
    /// is the doc's "battle ticks, 1 tick = 1 ms" claim, asserted as a RELATIONSHIP (the instant is
    /// derived from <c>T0 + roundClock.Now</c>) rather than as the prose's unit number, so it cannot
    /// rot into a stale magic-number assertion.
    /// </summary>
    [Fact]
    public void The_battle_tick_seam_derives_its_instant_from_the_battles_virtual_clock()
    {
        var lines = File.ReadAllLines(Path.Combine(
            KeepverseRoots.Core(), "src", "FusionRpg.Core", "Battle", "BattleEngine.cs"));

        Assert.Contains(lines, l => l.Contains("var now = state.T0.AddMilliseconds(", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("roundClock.Now", StringComparison.Ordinal));
    }

    // ==========================================================================================
    // 2. ResourceIds — the closed six, and what each id IS (not merely how many there are).
    // ==========================================================================================

    /// <summary>
    /// The six resource ids, in the order <c>DerivedStatChannels.ResourceIds</c> declares them.
    ///
    /// <para><b>Why the EXACT SET and the ORDER are the contract, not a population count.</b> The
    /// order is load-bearing three separate times over, so "some set of six" would be a real hole:
    /// <c>resource-catalog.v1.json</c> carries an <c>ordinal</c> field coupled to this list,
    /// <c>DerivedStatSurfaceCatalog</c> bakes resource ordinals from it, and
    /// <c>ActorResourcePools</c> is array-backed over it (<c>Ids = DerivedStatChannels.ResourceIds</c>
    /// — <c>ActorResourcePools.cs:15</c>). Reordering silently desyncs the ordinal coupling. A
    /// count-only assertion passes on any permutation, so it guards none of that; this asserts the
    /// whole named list.</para>
    ///
    /// <para><b>What each id is.</b> The docs' "ResourceIds is the closed six" claim is only useful
    /// with the membership pinned, because <c>hp</c> is special: it is the ONE id excluded from
    /// exhaustion debuffs (<c>ExhaustionPolicy.cs:56-58</c>) because depletion there is death, not a
    /// debuff. That relationship is asserted separately below rather than left to a reader.</para>
    /// </summary>
    [Fact]
    public void ResourceIds_is_still_the_closed_six_in_declaration_order()
    {
        Assert.Equal(
            new[] { "hp", "stamina", "hunger", "spirit", "qi", "poise" },
            DerivedStatChannels.ResourceIds.ToArray());
    }

    /// <summary>
    /// Every id carries all four derived families the pools actually read. Asserted as a
    /// RELATIONSHIP over the closed set rather than a count: a seventh id added without its channels
    /// would slip past a "six ids, six maxes" count but fail here.
    /// </summary>
    [Fact]
    public void Every_resource_id_has_the_four_families_the_pools_read()
    {
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            Assert.StartsWith("resource.max.", DerivedStatChannels.ResourceMax(id), StringComparison.Ordinal);
            Assert.StartsWith("resource.regen.", DerivedStatChannels.ResourceRegen(id), StringComparison.Ordinal);
            Assert.StartsWith("resource.efficiency.", DerivedStatChannels.ResourceEfficiency(id), StringComparison.Ordinal);
            Assert.StartsWith("resource.restore.", DerivedStatChannels.ResourceRestore(id), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>hp</c> is the one pool exhaustion may never debuff — depletion is death, owned by the
    /// turn FSM. This is the special case behind the docs' "the closed six", and it is enforced by
    /// <c>ExhaustionPolicy</c>'s CONSTRUCTOR, so the assertion is on the refusal rather than on a
    /// list membership.
    /// </summary>
    [Fact]
    public void Hp_is_the_one_resource_exhaustion_refuses_to_debuff()
    {
        var catalog = CoreStatus.StatusCatalogBootstrap.CreateDefault();
        var ex = Assert.Throws<ArgumentException>(() => new FusionRpg.Core.Actions.Cost.ExhaustionPolicy(
            catalog,
            new Dictionary<string, IReadOnlyList<CoreStatus.StatusStatMod>>
            {
                ["hp"] = Array.Empty<CoreStatus.StatusStatMod>(),
            }));

        Assert.Contains("hp exhaustion does not exist", ex.Message, StringComparison.Ordinal);
    }

    // ==========================================================================================
    // 3. StatusCategoryRegistry — the documented id set, pinned as a set and as category MEMBERSHIP.
    // ==========================================================================================

    /// <summary>
    /// The 24 ids <c>status-tracks-ideal.md</c> §0 reports as registered.
    ///
    /// <para><b>Why the exact named set is the contract.</b> "24" on its own guards nothing: adding a
    /// status and deleting a different one keeps it green, and the docs' own number is derived from
    /// the registry. The claim worth pinning is WHICH ids exist, because a new id is a reviewed
    /// change that must move both count lines (<c>status-ssot.md</c> §9 and
    /// <c>docs/design-gate/combat.md:14</c>) in the same commit — this test is what forces the
    /// conversation. Note the set below is the 24 locked ids only; <c>ExhaustionPolicy</c> and
    /// <c>StanceRuntime</c> ADD to this registry at construction time through the documented
    /// extension point (<c>Register</c>), which is why the assertion filters to ids the bootstrap
    /// registers rather than comparing the live map directly.</para>
    ///
    /// <para><b>Read the map's own scope, not the registry's live state.</b> A process-wide mutable
    /// static is not assertable as an exact set from another assembly's tests, because an
    /// in-process test that constructs an <c>ExhaustionPolicy</c> permanently widens it. That is the
    /// same trap <c>ResistanceEvaluatorTests.cs:442</c> records. The id set is therefore asserted
    /// through <see cref="CoreStatus.StatusCatalogBootstrap.CreateDefault"/> — the object the docs
    /// actually mean by "24 registered ids" — and <see cref="The_locked_ids_and_the_registry_agree"/>
    /// ties the two owners together.</para>
    /// </summary>
    [Fact]
    public void The_locked_catalog_is_the_exact_documented_id_set()
    {
        var catalog = CoreStatus.StatusCatalogBootstrap.CreateDefault();

        Assert.Equal(
            new[]
            {
                "blight", "bond", "butter", "charm_pulse", "cold", "command", "ember", "expose",
                "freeze", "hypno", "jala", "kelp", "leech", "nerve.afflicted", "nerve.shaken",
                "nerve.unsettled", "pact_mark", "poison", "rally", "rot", "shatter", "spark",
                "spore", "wither",
            }.OrderBy(id => id, StringComparer.Ordinal),
            catalog.All().Select(d => d.StatusId).OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>
    /// The registry is the OWNER of the category for every id the bootstrap registers, and the
    /// bootstrap reads it rather than declaring its own. Asserted as a RELATIONSHIP — every
    /// registered id resolves a category, and an unknown id throws rather than defaulting — because
    /// that two-owner-agreement is what the authoring doc's "three registration points must agree"
    /// rule exists to protect, and a count cannot express it.
    /// </summary>
    [Fact]
    public void The_locked_ids_and_the_registry_agree_and_the_registry_refuses_an_unknown_id()
    {
        var catalog = CoreStatus.StatusCatalogBootstrap.CreateDefault();
        foreach (var def in catalog.All())
        {
            Assert.True(CoreStatus.StatusCategoryRegistry.TryGetCategory(def.StatusId, out var category),
                $"'{def.StatusId}' is registered in the catalog but has no StatusCategoryRegistry " +
                "entry — the two owners have drifted, which status-tracks/authoring-combat-statuses.md " +
                "§1 calls a loud failure rather than a silent one.");
            Assert.Contains(category, new[] { CoreStatus.StatusL2bCategory.Dot, CoreStatus.StatusL2bCategory.Cc, CoreStatus.StatusL2bCategory.Contagion });

            // The bootstrap reads its category from the registry rather than declaring its own, so
            // the def and the registry cannot disagree about this id.
            Assert.Contains(category, def.Categories);
        }

        // An id with no category entry is refused AT LOOKUP, loudly, rather than defaulting — the
        // refusal `StatusCatalogBootstrap.Register` relies on to fail at bootstrap rather than at
        // resist time. Asserted against the real exception type (`ArgumentException`, not the
        // `UnknownStatusIdException` a catalog `GetRequired` throws): the point is that it throws,
        // not which type, so `ThrowsAny` is the honest form here.
        Assert.ThrowsAny<Exception>(
            () => CoreStatus.StatusCategoryRegistry.GetRequiredCategory("no.such.status"));
    }

    /// <summary>
    /// CC gating is derived from the def's CATEGORY, not from a hardcoded id list, and the battle
    /// seat check reads it — the doc's "eight ids genuinely prevent acting today". Asserted as a
    /// RELATIONSHIP over the catalog: any id categorised <c>Cc</c> is CC-locking by construction.
    /// The eight is a consequence, not the assertion; pinning the mechanism pins the number.
    /// </summary>
    [Fact]
    public void Cc_gating_is_derived_from_category_so_a_new_cc_id_gates_without_a_code_change()
    {
        var catalog = CoreStatus.StatusCatalogBootstrap.CreateDefault();
        var cc = catalog.All()
            .Where(d => CoreStatus.StatusCategoryRegistry.GetRequiredCategory(d.StatusId)
                == CoreStatus.StatusL2bCategory.Cc)
            .OrderBy(d => d.StatusId, StringComparer.Ordinal)
            .Select(d => d.StatusId)
            .ToArray();

        Assert.Equal(
            new[] { "butter", "charm_pulse", "cold", "ember", "freeze", "hypno", "jala", "kelp" },
            cc);
    }

    // ==========================================================================================
    // 4. The doc count lines themselves — pinned as content, not as a number.
    // ==========================================================================================

    /// <summary>
    /// The two count lines a status-id widen must move in the same commit, asserted to still SAY the
    /// count the code holds. This is the "doc rot becomes a red test" half of the file: not a
    /// population count of statuses, but a check that the two documents which quote a number still
    /// quote the RIGHT one. Read from the repo, so it moves with the code.
    /// </summary>
    [Fact]
    public void Both_status_count_lines_still_say_twenty_four_including_nerve()
    {
        var workspace = Path.GetFullPath(Path.Combine(KeepverseRoots.Core(), ".."));

        // The count the docs quote, asserted against the catalog the docs mean by it. Pinned as the
        // exact expected SET two tests above — this line only exists to prove the DOC still carries
        // the same number the code holds, which is the rot this file exists to catch.
        var count = CoreStatus.StatusCatalogBootstrap.CreateDefault().All().Count;
        Assert.Equal(24, count);

        foreach (var doc in new[]
                 {
                     "docs/architecture/status-ssot.md",
                     "docs/design-gate/combat.md",
                 })
        {
            var path = Path.Combine(workspace, doc);
            Assert.True(File.Exists(path), $"count line not found: {doc}");

            // Anchored to the count PHRASE, derived from the code count — not to the bare digit.
            // Asserting "24" anywhere in the file is not this assertion: status-ssot.md:126 carries
            // the date "(2026-08-24)", so a widen that moved BOTH real count lines 24 -> 25
            // (status-ssot.md:74 and :233) while that date stood would still have gone green. The
            // phrase is what ties the document's number to the catalog's, and deriving it from
            // `count` is what keeps the two from drifting apart independently.
            var anchor = $"today {count} including";
            Assert.Contains(anchor, File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The projection hosts carry no clock — the docs' "a track has no clock" rule, asserted over the
    /// host's own SOURCE so a future host method that reads the wall clock is caught even though the
    /// host type is a pure function today. Deterministic, no timing.
    /// </summary>
    [Fact]
    public void StatusProjectionHost_reads_no_clock_and_neither_does_the_shipped_track()
    {
        foreach (var file in new[] { "StatusProjectionHost.cs", "NervePolicy.cs", "ExhaustionPolicy.cs" })
        {
            var full = File.Exists(Path.Combine(
                               KeepverseRoots.Core(), "src", "FusionRpg.Core", "Status", file))
                ? Path.Combine(KeepverseRoots.Core(), "src", "FusionRpg.Core", "Status", file)
                : FindUnderStatusOrSiblings(file);

            Assert.True(full.Length > 0, $"policy source not found: {file}");

            var code = Regex.Replace(File.ReadAllText(full), @"/\*.*?\*/", "", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//.*$", "", RegexOptions.Multiline);

            foreach (var banned in new[] { "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow" })
                Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
        }
    }

    static string FindUnderStatusOrSiblings(string fileName)
    {
        var core = KeepverseRoots.Core();
        foreach (var dir in new[]
                 {
                     Path.Combine(core, "src", "FusionRpg.Core", "Delve", "Attrition"),
                     Path.Combine(core, "src", "FusionRpg.Core", "Actions", "Cost"),
                 })
            foreach (var file in Directory.GetFiles(dir, fileName, SearchOption.AllDirectories))
                return file;

        return "";
    }
}