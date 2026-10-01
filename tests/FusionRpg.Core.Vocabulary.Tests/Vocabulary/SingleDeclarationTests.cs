using System.Linq;
using System.Text.RegularExpressions;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Vocabulary;

/// <summary>
/// `solid-remediation` T5.1/T5.2 (X1, X2) — two closed vocabularies, one declaration each, one failure
/// mode each.
///
/// <para><b>Counts ARE pinned here, deliberately, and that is the correct call rather than an exception
/// to `validation-ssot.md`.</b> These are enums and registries the code owns and a human edits — the
/// standard's own "closed vocabulary" case, where the count IS the contract. A seventh element or a
/// changed category is a reviewed change that SHOULD turn this red and be re-read. The banned case is
/// pinning a derived population, and neither of these grows when content ships.</para>
/// </summary>
[Trait("VerificationId", "core.vocabulary-single-declaration")]
public class SingleDeclarationTests
{
    // ---- X2: element ids ------------------------------------------------------------------------

    [Fact]
    public void Six_elements_and_both_spellings_agree_member_for_member()
    {
        // The two declarations were ElementTable.IdOf and ActorElementTypes.ToElementId. One is now an
        // alias of the other, so this can only fail if someone re-forks them.
        var all = Enum.GetValues(typeof(ElementTypeId)).Cast<ElementTypeId>().ToArray();

        Assert.Equal(6, all.Length);
        foreach (var id in all)
            Assert.Equal(id.ToElementId(), ElementTable.IdOf(id));
    }

    [Fact]
    public void An_unknown_element_throws_naming_it_and_never_returns_an_empty_string()
    {
        // X2's actual defect, restated from the test that used to pin the `""` return. IdOf feeds
        // ElementRingMatrix and ShieldElementMatrix, which look the id up in the matchup table — so an
        // empty id matched no row and a missing element resolved to a NEUTRAL matchup with no error.
        // A plausible-looking damage multiplier that nothing reports is worse than a crash.
        var unknown = (ElementTypeId)999;

        var fromTable = Assert.Throws<ArgumentOutOfRangeException>(() => ElementTable.IdOf(unknown));
        var fromTypes = Assert.Throws<ArgumentOutOfRangeException>(() => unknown.ToElementId());

        // Same failure mode, and the message names the member rather than saying "invalid input".
        Assert.Contains("999", fromTable.Message, StringComparison.Ordinal);
        Assert.Contains("999", fromTypes.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_element_maps_to_an_empty_or_duplicated_id()
    {
        var ids = Enum.GetValues(typeof(ElementTypeId)).Cast<ElementTypeId>()
            .Select(ElementTable.IdOf).ToArray();

        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_unknown_element_throws_at_each_of_the_three_former_IdOf_call_sites()
    {
        // The three consumers the fail-open `""` used to reach: the element ring, the shield matrix,
        // and the HUD shield fold. All three now share the one throwing declaration, so each throws
        // rather than resolving a missing element to a NEUTRAL matchup with no error.
        var unknown = (ElementTypeId)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => ElementRingMatrix.GetRelation(unknown, ElementTypeId.Fire));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElementRingMatrix.GetRelation(ElementTypeId.Fire, unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShieldElementMatrix.RelationUnit(unknown, ElementTypeId.Fire));
        Assert.Throws<ArgumentOutOfRangeException>(() => ActorHudShieldStacks.AggregateByElement(
            new[] { new ShieldInstance { Element = unknown, Hp = 1, MaxHp = 1 } }));
    }

    [Fact]
    public void The_AS_normal_CDF_coefficient_has_exactly_one_home()
    {
        // D7: the Abramowitz & Stegun 7.1.26 coefficient set is the standard normal CDF, declared once
        // in Race.Erf. tools/CombatSim/Analytic.Phi carried a second copy of the six constants until
        // T5 deleted it and called Race.Phi; this scan catches a third copy. A literal is pinned here
        // because it is a fixed approximation constant, not a balance number or a population.
        var root = RepoRoot();
        var hits = new List<string>();
        foreach (var dir in new[] { Path.Combine(root, "src"), Path.Combine(root, "tools") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in EnumerateSources(dir))
            {
                if (File.ReadAllText(file).Contains("0.254829592", StringComparison.Ordinal))
                    hits.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        Assert.Equal(new[] { "src/FusionRpg.Core/Balance/Analytic/Race.cs" }, hits);
    }

    [Fact]
    public void Only_ActorElementTypes_maps_the_element_enum_to_ids()
    {
        // T2's guard (D2): a source scan, because the duplication this forbids is a property of the
        // SOURCE. A second `ElementTypeId.X => "fire"` arm anywhere else compiles, passes every
        // behavioural test above (they only exercise IdOf/ToElementId), and is a fresh fork of the id
        // vocabulary — exactly the class this program exists to stop. Only ActorElementTypes.cs may
        // carry one. Scoped to gk-core/src/FusionRpg.Core (bin/obj pruned); the id strings appear nowhere else.
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in EnumerateSources(Path.Combine(root, "src", "FusionRpg.Core")))
        {
            if (Path.GetFileName(file) == "ActorElementTypes.cs") continue;
            var text = File.ReadAllText(file);
            foreach (var id in new[] { "fire", "ice", "air", "earth", "light", "dark" })
            {
                if (Regex.IsMatch(text, "ElementTypeId\\s*\\.\\s*[A-Za-z]+\\s*=>\\s*\"" + id + "\""))
                    offenders.Add($"{Path.GetRelativePath(root, file)} -> \"{id}\"");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_inspect_scope_literals_live_only_in_InspectScopes()
    {
        // T14 (D18): the server's `/inspect` route and the injector's `debug.inspect` command carried
        // a byte-identical scope line with no declaring type anywhere. The vocabulary now lives in
        // FusionRpg.Contracts.InspectScopes; those two consumer files may not re-literalise it.
        var root = RepoRoot();
        // Split across two repositories, measured rather than assumed: DebugEndpoints.cs is gk-core's
        // and ControlInspect.cs is gk-fusion's, because the Injector moved out with the launcher. Built
        // from `root` both were read from gk-core, so the Injector one refused:
        //     missing ...\gk-core\src\FusionRpg.Injector\ControlInspect.cs
        var consumers = new[]
        {
            Path.Combine(root, "src", "FusionRpg.Server", "DebugEndpoints.cs"),
            Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "ControlInspect.cs")
        };
        foreach (var file in consumers)
        {
            Assert.True(File.Exists(file), "missing " + file);
            var text = File.ReadAllText(file);
            foreach (var scope in new[] { "menu", "lawn", "all" })
                Assert.DoesNotContain("\"" + scope + "\"", text, StringComparison.Ordinal);
        }

        // Non-vacuity: the declaration itself carries the three, so this cannot pass by the vocabulary
        // having gone missing everywhere.
        var declaration = File.ReadAllText(
            Path.Combine(root, "src", "FusionRpg.Contracts", "InspectScopes.cs"));
        foreach (var scope in new[] { "menu", "lawn", "all" })
            Assert.Contains("\"" + scope + "\"", declaration, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_hud_folds_call_the_shared_shield_helpers()
    {
        // T16 (D20/D21): ActorHudDirector re-summed shield HP/max, and both HUD files divided hp/max
        // themselves. The folds now call Core's ActorHudShieldStacks.Totals and
        // ShieldBarVisual.TrueRatio — nothing under Injector/Hud may do either arithmetic itself.
        var root = RepoRoot();
        // The Injector is gk-fusion's; `root` is gk-core's. Measured, not inferred from the name.
        var hudDir = Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "Hud");
        Assert.True(Directory.Exists(hudDir), "missing " + hudDir);

        foreach (var file in EnumerateSources(hudDir))
        {
            var text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text, @"\bhp\s*\+=\s*shields"), $"{file} sums shield HP itself");
            Assert.False(Regex.IsMatch(text, @"\(float\)\s*\w+\.?Hp\s*/"), $"{file} divides hp/max itself");
        }

        // Non-vacuity: the two folds actually call the shared helpers.
        var director = File.ReadAllText(Path.Combine(hudDir, "ActorHudDirector.cs"));
        var pool = File.ReadAllText(Path.Combine(hudDir, "ActorHudPool.cs"));
        Assert.Contains("ActorHudShieldStacks.Totals(", director, StringComparison.Ordinal);
        Assert.Contains("ShieldBarVisual.TrueRatio(", director, StringComparison.Ordinal);
        Assert.Contains("ShieldBarVisual.TrueRatio(", pool, StringComparison.Ordinal);
    }

    static IEnumerable<string> EnumerateSources(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            if (name is "bin" or "obj" or "node_modules" or "__pycache__" or "artifacts") continue;
            foreach (var file in EnumerateSources(sub))
                yield return file;
        }
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    // ---- X1: status id to resist category --------------------------------------------------------

    [Fact]
    public void Every_catalogued_status_takes_its_category_from_the_one_registry()
    {
        // X1: the catalog bootstrap used to carry its own 23 literals beside the registry's map. It now
        // reads GetRequiredCategory, so the two cannot disagree — and an id registered in the catalog
        // with no registry entry fails at bootstrap rather than shipping a status whose resist channel
        // answers a different category than its catalog entry claims.
        var catalog = StatusCatalogBootstrap.CreateDefault();

        // The catalog exposes no enumeration, so walk the registry — which is the owner, and therefore
        // the right direction to walk anyway: every id it declares that the catalog also carries must
        // agree, and the catalog cannot carry one the registry does not know (Register throws).
        var checkedAny = false;
        foreach (var statusId in StatusCategoryRegistry.AllStatusIds)
        {
            if (!catalog.TryGet(statusId, out var def)) continue;
            checkedAny = true;
            Assert.Contains(StatusCategoryRegistry.GetRequiredCategory(statusId), def.Categories);
        }

        Assert.True(checkedAny, "no catalogued status was checked — this test would pass vacuously");
    }

    [Fact]
    public void An_unknown_status_id_throws_naming_it()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => StatusCategoryRegistry.GetRequiredCategory("not-a-status"));

        Assert.Contains("not-a-status", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_category_vocabulary_itself_is_closed_at_three()
    {
        // Dot / Cc / Contagion. Register refuses anything else rather than storing it, which is what
        // keeps "one failure mode" true on the write side as well as the read side.
        var categories = StatusCategoryRegistry.AllStatusIds
            .Select(StatusCategoryRegistry.GetRequiredCategory)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(3, categories.Length);
        Assert.Throws<ArgumentException>(() => StatusCategoryRegistry.Register("x", "not-a-category"));
    }

    [Fact]
    public void AllStatusIds_is_the_locked_twenty_four_member_set()
    {
        // T1's AC: the status roster is a CLOSED vocabulary (status-ssot.md §9) that a human edits, so
        // membership IS the contract — pinned by the id set, never by a count that would pass if one id
        // were swapped for another. A new status is a reviewed change that should turn this red and be
        // re-read. The three `nerve.*` rows are the P3 addition (21 -> 24).
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wither", "poison", "leech", "bond", "rally", "expose", "command", "shatter",
            "butter", "freeze", "cold", "hypno", "ember", "jala", "kelp", "charm_pulse",
            "blight", "rot", "spark", "pact_mark", "spore",
            "nerve.unsettled", "nerve.shaken", "nerve.afflicted"
        };

        Assert.Equal(expected, StatusCategoryRegistry.AllStatusIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void StatusCatalogBootstrap_declares_no_l2b_category_of_its_own()
    {
        // T1 (D1), in the direction solid-remediation T5.1 landed: the registry owns the
        // status-id -> L2b-category rule and the bootstrap reads GetRequiredCategory. This is the
        // negative half of that contract — `leech` used to pass `StatusL2bCategory.Dot` explicitly, a
        // second declaration that could drift from the registry's. A re-introduced category literal
        // here now fails this scan.
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Status", "StatusCatalogBootstrap.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("StatusL2bCategory.", text, StringComparison.Ordinal);
    }
}
