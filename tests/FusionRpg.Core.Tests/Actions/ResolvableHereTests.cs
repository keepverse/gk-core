using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;
using RegexMatch = System.Text.RegularExpressions.Match;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// combat-ai `resolvable-here` (module 5, CAI1.12, spec-resolvable-here.md): the "will this action's
/// effects fire HERE" gate, derived from each place's own executor — never from an authored allowlist.
/// The pinned literals are closed vocabularies only; the battle allowlist's SIZE is asserted equal to
/// the dispatch table instead of to a number, so widening the executor moves both together.
/// </summary>
public class ResolvableHereTests
{
    sealed class DeclaredSink : IDeclaresExecution
    {
        readonly HashSet<string> _executed;
        public DeclaredSink(params string[] executed) => _executed = new HashSet<string>(executed, StringComparer.OrdinalIgnoreCase);
        public IReadOnlySet<string> ExecutedActions => _executed;
    }

    static EffectDef Def(params string[] triggers) => new()
    {
        EffectId = "fx.test",
        Triggers = new List<string>(triggers),
        Actions = new List<EffectActionRow>
        {
            new() { Seq = 0, Action = EffectActions.ModifyStat },
            new() { Seq = 1, Action = EffectActions.ClearStatus },
        },
    };

    /// <summary>One unit, and an opcode battle's table does NOT carry — the status-only support
    /// action the `Inert` + veto case is built from.</summary>
    static EffectDef ClearStatusDef(params string[] triggers) => new()
    {
        EffectId = "fx.clear",
        Triggers = new List<string>(triggers),
        Actions = new List<EffectActionRow> { new() { Seq = 0, Action = EffectActions.ClearStatus } },
    };

    static CompiledAction Action(string id, string? containerId, ActionCategory? category) => new(
        id, ActionKind.Skill, 1, Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<TargetSpec>()),
        0, int.MaxValue, containerId, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>(), RungBand: null, Category: category);

    static PlaceExecutionProfile BattleProfile()
    {
        var host = new BattleEffectHost(_ => null, rngSeed: 1UL);
        return PlaceExecutionProfile.FromSink(host, RuntimeId.Battle, BattleEffectHost.RaisedTriggers);
    }

    // -- the anti-drift contracts -----------------------------------------------------------------

    /// <summary>The declaration and the executor are the SAME object: `BattleEffectSink.ExecutedActions`
    /// is derived from the dispatch table's own keys, and both are checked against the table's own
    /// source. Nothing pins the table's size — widening the executor (a reviewed change) moves the
    /// declaration with it.</summary>
    [Fact]
    public void Battles_declared_allowlist_equals_its_dispatch_table()
    {
        var source = File.ReadAllText(FindSourceFile("BattleEffects.cs"));
        var tableStart = source.IndexOf("_handlers = new Dictionary<string, Func<EffectExecuteContext, EffectActionPlanItem, bool>>", StringComparison.Ordinal);
        Assert.True(tableStart >= 0, "the battle dispatch table was not found -- BattleEffects.cs may have moved");
        var tableEnd = source.IndexOf("};", tableStart, StringComparison.Ordinal);
        Assert.True(tableEnd > tableStart);
        var tableSource = source.Substring(tableStart, tableEnd - tableStart);

        var fromTable = Regex.Matches(tableSource, @"\[EffectActions\.(\w+)\]\s*=")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var host = new BattleEffectHost(_ => null, rngSeed: 1UL);
        var declared = host.ExecutedActions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(declared);
        Assert.Equal(fromTable, declared);
    }

    /// <summary>
    /// The lawn half of the anti-drift contract (spec §1 / Testing strategy). `InjectorEffectActionSink`
    /// is the LIVE lawn host's effect-action sink, so the per-place allowlist is built from ITS
    /// declaration and never from a profile row. The declared array and the dispatch switch are held
    /// equal by reading the file as text, because the injector assembly needs a real PVZ Fusion install
    /// and never builds under CI — a source scan is the regression coverage this half gets, the same
    /// idiom the battle assertion above uses for battle's table.
    /// </summary>
    [Fact]
    public void The_lawn_sinks_declared_allowlist_matches_every_EffectActions_constant_its_dispatch_references()
    {
        var source = File.ReadAllText(FindSourceFile("InjectorEffectActionSink.cs"));

        var dispatched = EffectActionsConstantsIn(source, "var ok = item.Action switch", "the injector dispatch switch");
        var declared = EffectActionsConstantsIn(source, "public static readonly string[] Executes", "the injector `Executes` declaration");

        Assert.NotEmpty(dispatched);
        Assert.Equal(
            dispatched.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            declared.OrderBy(c => c, StringComparer.Ordinal).ToArray());

        // The declaration is a list; the property must READ that list, or the assertion above would
        // certify a set nothing hands to `PlaceExecutionProfile.FromSink`.
        var propertyAt = source.IndexOf("public IReadOnlySet<string> ExecutedActions", StringComparison.Ordinal);
        Assert.True(propertyAt >= 0, "InjectorEffectActionSink.ExecutedActions was not found");
        var propertyBody = source.Substring(propertyAt, Math.Min(200, source.Length - propertyAt));
        Assert.Contains("Executes", propertyBody, StringComparison.Ordinal);
    }

    /// <summary>Every trigger battle raises must be declared. The scan is a superset of the spec's own
    /// (`Trigger = AtomTriggers.` alone): it reads every `AtomTriggers.X` reference under
    /// `gk-core/src/FusionRpg.Core/Battle/**`, so `RaiseTrigger(...)`/`RaiseLifecycle(...)` arguments count too.
    /// That is why the declared array carries eleven triggers rather than the spec's four — see the
    /// finding note on CAI1.12.</summary>
    [Fact]
    public void Every_trigger_battle_raises_appears_in_its_declared_trigger_set()
    {
        var battleDir = Path.GetDirectoryName(FindSourceFile("BattleEffects.cs"))!;
        var vocabulary = AtomTriggers.All.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var raised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(battleDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (RegexMatch m in Regex.Matches(text, @"AtomTriggers\.(\w+)"))
            {
                // Restricted to the closed thirteen: a doc comment that writes `AtomTriggers.X` as a
                // placeholder is not a raise site, and a name outside the vocabulary could not be one.
                var name = m.Groups[1].Value;
                if (vocabulary.Contains(name)) raised.Add(name);
            }
        }

        Assert.NotEmpty(raised);
        var declared = BattleEffectHost.RaisedTriggers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in raised)
            Assert.Contains(trigger, declared);

        // The two the scan must never find a battle raise site for: lawn-only board-economy inputs.
        Assert.DoesNotContain("OnSunCollect", raised);
        Assert.DoesNotContain("OnGridPlace", raised);
    }

    /// <summary>The filter is built from a sink and a host — never from a tuning or profile row. A
    /// source scan is the honest check: a profile type in this file would be the authored-allowlist
    /// defect the module exists to prevent.</summary>
    [Fact]
    public void A_place_profile_is_built_from_the_sink_and_never_from_a_profile_row()
    {
        var text = File.ReadAllText(FindSourceFile("ResolvableHere.cs"));
        Assert.DoesNotContain("AiPlace", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CombatAiProfile", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AiProfileRow", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuning", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sink_that_does_not_declare_is_refused_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PlaceExecutionProfile.FromSink(null!, RuntimeId.Battle, BattleEffectHost.RaisedTriggers));
    }

    // -- Of / Veto --------------------------------------------------------------------------------

    [Fact]
    public void An_action_with_no_declared_effect_units_is_Full_everywhere()
    {
        var f = new ActionEffectFootprint("act.attack", DeclaredUnits: 0, ResolvableUnits: 0, Category: ActionCategory.Attack);
        Assert.Equal(Resolvability.Full, ResolvableHere.Of(in f));
        Assert.False(ResolvableHere.Veto(in f));
    }

    [Fact]
    public void A_status_only_support_action_is_Inert_in_battle_and_Full_on_the_lawn()
    {
        // Battle executes neither opcode above, so BOTH declared units are inert here.
        var battle = BattleProfile();
        var action = Action("act.status.clear", "container.x", ActionCategory.Support);
        var defined = ClearStatusDef(AtomTriggers.OnActivate);

        var inBattle = EffectFootprintTable.BuildOne(
            action, _ => new[] { "fx.clear" }, _ => defined, in battle);
        Assert.Equal(1, inBattle.DeclaredUnits);
        Assert.Equal(0, inBattle.ResolvableUnits);
        Assert.Equal(Resolvability.Inert, ResolvableHere.Of(in inBattle));
        Assert.True(ResolvableHere.Veto(in inBattle));

        // The lawn declares ClearStatus and raises every trigger (ideal §4.3), so the same action is
        // fully resolvable there — the filter agrees with the runtime matrix without reading it.
        var lawn = PlaceExecutionProfile.FromSink(
            new DeclaredSink(EffectActions.ClearStatus, EffectActions.ModifyStat),
            RuntimeId.Lawn, AtomTriggers.All);
        var inLawn = EffectFootprintTable.BuildOne(action, _ => new[] { "fx.clear" }, _ => defined, in lawn);
        Assert.Equal(1, inLawn.ResolvableUnits);
        Assert.Equal(Resolvability.Full, ResolvableHere.Of(in inLawn));
        Assert.False(ResolvableHere.Veto(in inLawn));
    }

    [Fact]
    public void An_attack_action_with_an_inert_rider_is_Partial_and_is_never_vetoed()
    {
        var battle = BattleProfile();
        // Two units on one trigger: ModifyStat is executed by battle, ClearStatus is not.
        var defined = Def(AtomTriggers.OnActivate);
        var action = Action("act.attack.rider", "container.x", ActionCategory.Attack);

        var f = EffectFootprintTable.BuildOne(action, _ => new[] { "fx.test" }, _ => defined, in battle);
        Assert.Equal(2, f.DeclaredUnits);
        Assert.Equal(1, f.ResolvableUnits);
        Assert.Equal(Resolvability.Partial, ResolvableHere.Of(in f));
        Assert.False(ResolvableHere.Veto(in f));
    }

    [Fact]
    public void An_uncategorised_action_is_never_vetoed_and_still_reports_its_resolvability()
    {
        var f = new ActionEffectFootprint("act.uncategorised", DeclaredUnits: 2, ResolvableUnits: 0, Category: null);
        Assert.Equal(Resolvability.Inert, ResolvableHere.Of(in f));
        Assert.False(ResolvableHere.Veto(in f)); // fail open
    }

    [Fact]
    public void Veto_is_true_only_for_the_three_effect_carried_categories()
    {
        foreach (var category in Enum.GetValues<ActionCategory>())
        {
            var f = new ActionEffectFootprint("a", DeclaredUnits: 1, ResolvableUnits: 0, Category: category);
            var expected = category is ActionCategory.Support or ActionCategory.Status or ActionCategory.Defense;
            Assert.Equal(expected, ResolvableHere.Veto(in f));
        }
    }

    // -- cost / shape -----------------------------------------------------------------------------

    /// <summary>The closed resolvability vocabulary, pinned with the reason: `Veto`'s whole rule is
    /// stated over these three members (`Inert x {Support, Status, Defense}`), and the count is what
    /// `docs/DESIGN-GATE.md` §1's decision row records — a fourth member is a reviewed change, not a
    /// wider predicate.</summary>
    [Fact]
    public void Resolvability_has_three_members() =>
        Assert.Equal(3, Enum.GetValues(typeof(Resolvability)).Length);

    [Fact]
    public void The_filter_allocates_zero_bytes_across_two_hundred_candidate_actions()
    {
        var candidates = new ActionEffectFootprint[200];
        for (var i = 0; i < candidates.Length; i++)
            candidates[i] = new ActionEffectFootprint($"act.{i}", DeclaredUnits: 2, ResolvableUnits: i % 3, Category: ActionCategory.Support);

        var pass = () =>
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                var f = candidates[i];
                ResolvableHere.Of(in f);
                ResolvableHere.Veto(in f);
            }
        };

        // Warm + collect, the harness `KernelAllocationTests` established. Three passes, measured:
        // with one, this same body reports a constant 67 bytes regardless of what it contains (a
        // plain array read included), which is a runtime charge, not the filter's.
        pass(); pass(); pass();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        pass();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void A_footprint_is_computed_once_per_battle_and_not_per_decision()
    {
        var walks = 0;
        var battle = BattleProfile();
        var defined = Def(AtomTriggers.OnActivate);

        // Two actors hold the SAME action; a third holds another. Build is the once-per-battle call.
        var actions = new IReadOnlyList<CompiledAction>[]
        {
            new[] { Action("act.a", "container.a", ActionCategory.Attack), Action("act.b", "container.b", ActionCategory.Support) },
            new[] { Action("act.a", "container.a", ActionCategory.Attack) },
        };

        var table = EffectFootprintTable.Build(
            actions.SelectMany(a => a).ToList(),
            containerId => { walks++; return new[] { "fx.test" }; },
            _ => defined,
            in battle);

        Assert.Equal(2, walks); // container.a, container.b -- once each, never once per actor
        Assert.Equal(2, table.Count);
    }

    /// <summary>
    /// The module's own rule — no authored per-profile/per-place opcode list — checked against the
    /// tuning tree. Two parts, because the literal form of this acceptance line
    /// (`No file under gk-core/data/tuning/** names an opcode`) does NOT hold at HEAD and cannot be made to
    /// hold from here: `gk-core/data/tuning/status-catalog.v1.json` already names `"ModifyStat"` as a status
    /// PAYLOAD KIND (lines 62, 94, 126, 223, 239), which is unrelated to this filter but is still the
    /// opcode string; tuning files are never hand-edited (a fix publishes v{n+1}). Reported as a
    /// finding on CAI1.12 rather than silently narrowed.
    /// </summary>
    [Fact]
    public void No_file_under_data_tuning_authors_a_place_allowlist()
    {
        var root = FindRepoRoot();
        var tuningDir = Path.Combine(root, "data", "tuning");
        Assert.True(Directory.Exists(tuningDir), $"data/tuning not found under {root}");

        // The keys a per-place allowlist would have to use. A tuning file naming one of these is the
        // authored-allowlist defect the module exists to prevent.
        var allowlistKeys = new[] { "executedActions", "executedOpcodes", "raisedTriggers", "resolvableUnits", "resolvableHere" };

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(tuningDir, "*.json"))
        {
            var text = File.ReadAllText(file);
            foreach (var key in allowlistKeys)
                if (text.Contains($"\"{key}\"", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} -> {key}");
        }

        Assert.Empty(offenders);
    }

    // -- helpers ----------------------------------------------------------------------------------

    /// <summary>The `EffectActions.*` constant names inside one region of a source file: from
    /// <paramref name="anchor"/> up to the first `};` that closes it — the idiom the battle scan
    /// above uses.</summary>
    static HashSet<string> EffectActionsConstantsIn(string source, string anchor, string what)
    {
        var start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{what} was not found -- the file may have moved or been rewritten");
        var end = source.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{what} has no closing `}};`");
        return Regex.Matches(source.Substring(start, end - start), @"EffectActions\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    static string FindSourceFile(string fileName, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        for (var i = 0; i < 10 && dir != null; i++)
        {
            var found = Directory.GetFiles(dir, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
            if (found.Length > 0) return found[0];
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"{fileName} not found by walking up from {here}");
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
