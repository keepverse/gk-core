using FusionRpg.Core.Effects.Atoms;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E28 test 12 (spec-param-parity.md §5, acceptance criterion 8): <i>"a declared param that reaches no
/// executor fails a test."</i> Every one of E28's seven fixes (#2-7, plus the `fx.set_dirt_box` content
/// fix) closed one instance of the exact same defect shape: a param declared on a kind's
/// <see cref="ParamSchema"/> — so it validates, so an author can write it — that no executor ever read.
/// Tests 1-11 (see <c>ParamSchemaVsExecutorTests.cs</c>, a pre-E28/D7 file that proves specific
/// name/type mismatches, and the per-fix tests the module's own todo entry names) each prove ONE
/// instance now works. <b>This file is the generic form</b>: it walks every kind
/// <see cref="AtomKindRegistry"/> actually ships, every param each one actually declares, and asserts
/// the param's name literally appears in the real source of whichever file(s) consume that kind — read
/// as plain TEXT, the same "does the source actually reference X" technique
/// <c>gk-core/tests/FusionRpg.Guard.Tests/PlantSideStatusGuardTests.cs</c> and
/// <c>SpawnNonGridExecutorGuardTests.cs</c> use for the Injector's own Unity-hosted half. A future
/// thirteenth kind, or a new param on an existing one, needs no new test written — it is caught by
/// walking the registry, not by a hand-added case.
///
/// <para><b>Why this lives in Core.Tests, not Guard.Tests, despite reusing Guard.Tests' own
/// text-scanning technique.</b> <c>gk-core/tests/FusionRpg.Guard.Tests/*.csproj</c> carries ZERO
/// <c>ProjectReference</c> to <c>FusionRpg.Core</c> — verified by reading every one of its (then) 32
/// existing test files: not one has a <c>using FusionRpg.*</c> line, and the csproj itself lists only
/// the xunit/Microsoft.NET.Test.Sdk packages. That is a deliberate, consistent project boundary
/// (dependency-free, text/process-scanning only), not an oversight, and adding a
/// <c>ProjectReference</c> there to make <see cref="AtomKindRegistry"/> reachable would itself be an
/// undiscussed structural change to that boundary — exactly the kind of thing to flag rather than do
/// silently. This module's own "generic, not a dozen hand-copied per-kind cases" requirement means the
/// walk must read the REAL <see cref="AtomKindRegistry.All"/> and each kind's REAL
/// <see cref="ParamSchema.Defs"/>, never a hand-maintained mirror of them — that is only reachable from
/// a project that already references Core. Reading an Injector <c>.cs</c> file as plain TEXT (the
/// actual "does the executor read this" proof) needs no compilation of the Injector project at all, so
/// that half costs nothing extra to do from here too — Core.Tests already carries the same idea one
/// step further, `&lt;Compile Include&gt;`-ing a handful of individually Unity-free Injector files
/// directly (see this project's own .csproj, `InjectorDerivedOverride.cs` etc.). Core.Tests already builds
/// and runs under CI with no <c>$env:FUSIONRPG_GAME_DIR</c> requirement, so this test runs on every
/// commit — the "durable" shape spec-param-parity.md §5 asks test 12 to have.</para>
/// </summary>
public class ParamParityGuardTests
{
    // ---- the map: kindId -> the real file(s) that consume its declared params ---------------------
    //
    // This is the "which file(s)/method(s) is this kind's real consumer" input the spec's own
    // instructions call out as fine and expected to hand-write — it names WHERE to look, not WHETHER a
    // param passes. Several kinds share a consumer file (all board-attach kinds plus stat.modify's
    // `channel`, status.apply/clear, spawn.entity, wave.control, match.modify, resource.economy's
    // `currency`/`op`/`amount` all execute through `InjectorEffectActionSink.cs`'s own opcode
    // switch — FA1/FA9/FA2/FA3/FA4/FA5/FA6/FA7/FA8, one file, many opcodes). Four kinds are genuinely
    // different consumer shapes, each verified by reading the real method before it went in this map:
    //
    //  - `stat.modify`/`stat.derived`'s `op`/`amount` are NOT read by name anywhere in the sink at
    //    all — `AtomCompiler.ToOpcodeShape` rewrites `{op:"flat", amount:150}` into `{flat:150}`
    //    *before* the row ever reaches an executor (FA1 "reads flat/increased/more... and knows
    //    nothing about op or amount", the compiler's own doc comment). Their real consumer for those
    //    two params is the compiler, not the sink.
    //  - `resource.delta`'s DoT/contagion payload (`statusId`/`periodMs`/`durationMs`/`tickBudget`/
    //    `spread`) is bag-side: `EffectBag`'s own `ApplyResourceDelta` branch calls
    //    `StatusEffectBridge.TryApplyFromGrant` directly and never builds a plan item for those keys at
    //    all (D7's own note on the kind: "the DoT and contagion payload lives HERE... EffectBag calls
    //    StatusEffectBridge.TryApplyFromGrant only inside the ApplyResourceDelta branch"). `amount`/
    //    `element`/`target` on the same kind resolve through `DamagePacketBuilder.FromOverlay`,
    //    likewise bag-side, before any FA10 plan item exists — `channel`/`amount` are also read a
    //    second time in the sink's own `ExecApplyResourceDelta` (the multi-target spread sub-plan).
    //  - `shield.grant` and `ui.present` are both bag-side executors living directly in
    //    `EffectBag.cs` (`ExecGrantShield`/`ExecPresentUi`) — FA11-equivalent and Ui-attached kinds
    //    never become an `EffectActionPlanItem` and never reach the sink at all (E41's own "the
    //    module's central read-only invariant").
    //  - `stat.derived`'s `channel` and `bullet.modify`'s four params are resolved-read, permanent-
    //    modifier shapes: no sink opcode exists for either, so a live grant's own compiled action row
    //    is read directly by `GrantedDerivedAtomReader`/`GrantedBulletModifyAtomReader` at resolve
    //    time (the "grant's presence is the effect" shape both kinds' own doc comments describe).
    const string Sink = "src/FusionRpg.Injector/Effects/InjectorEffectActionSink.cs";
    const string Compiler = "src/FusionRpg.Core/Effects/Atoms/AtomCompiler.cs";
    const string DerivedReader = "src/FusionRpg.Core/Stats/Derived/Subsystems/GrantedDerivedAtomReader.cs";
    const string DamagePacketBuilderFile = "src/FusionRpg.Core/Combat/DamagePacketBuilder.cs";
    const string StatusBridge = "src/FusionRpg.Core/Status/StatusEffectBridge.cs";
    const string Bag = "src/FusionRpg.Core/Effects/EffectBag.cs";
    const string BulletReader = "src/FusionRpg.Core/Effects/Atoms/GrantedBulletModifyAtomReader.cs";
    const string BattleEffects = "src/FusionRpg.Core/Battle/BattleEffects.cs";

    static readonly Dictionary<string, string[]> ConsumerFiles = new(StringComparer.Ordinal)
    {
        ["stat.modify"] = new[] { Sink, Compiler },                 // channel: Sink. op/amount: Compiler.
        ["stat.derived"] = new[] { DerivedReader, Compiler },       // channel: DerivedReader. op/amount: Compiler.
        ["resource.delta"] = new[] { Sink, DamagePacketBuilderFile, StatusBridge },
        ["resource.economy"] = new[] { Sink, Compiler },            // currency/op/amount: Sink. capPerMatch: Compiler (AtomRunner's own read).
        ["status.apply"] = new[] { Sink },
        ["status.clear"] = new[] { Sink },
        ["shield.grant"] = new[] { Bag, DamagePacketBuilderFile },  // amount/element/sourceClass/priority/durationTicks/refillOnMerge: Bag. target: DamagePacketBuilder.
        ["spawn.entity"] = new[] { Sink },
        ["board.action"] = new[] { Sink },
        ["grid.spawn"] = new[] { Sink },
        ["grid.clear"] = new[] { Sink },
        ["box.set"] = new[] { Sink },
        ["bullet.modify"] = new[] { BulletReader },
        ["match.modify"] = new[] { Sink },
        ["wave.control"] = new[] { Sink },
        ["ui.present"] = new[] { Bag },
        ["structure.place"] = new[] { BattleEffects },
    };

    /// <summary>
    /// E29's own generic grant-overlay keys (`EffectProcAndOwner.cs`'s
    /// <c>EffectOverlayMerge.AllowedByAction</c> — "chance", "icd_ms", "max_stacks", "filters") are
    /// consumed by the BAG itself, generically, before an action ever reaches a kind-specific executor
    /// — they are not declared as a <see cref="ParamDef"/> on any kind's own <see cref="ParamSchema"/>
    /// today (checked directly: <see cref="AtomKindRegistry.All"/> below never yields one of these four
    /// names from any kind's <c>Params.Defs</c>). So this exemption set is empty in the one place that
    /// would matter — <see cref="Every_declared_param_on_every_shipped_kind_reaches_its_real_consumer_source"/>
    /// exercises it against the live registry and it removes nothing — and it stays in the mechanism
    /// only so the day one of these four IS ever declared on a kind's own schema (rather than merely
    /// accepted generically by the overlay merge), this test does not misreport a legitimate generic
    /// key as a param-parity violation. Named explicitly, per this module's own discipline for every
    /// other exemption it carries.
    /// </summary>
    static readonly HashSet<string> GenericOverlayKeys = new(StringComparer.Ordinal)
        { "chance", "icd_ms", "max_stacks", "filters" };

    /// <summary>
    /// The generic mechanism itself. Given one kind's declared param names and the concatenated text
    /// of its real consumer file(s), returns one message per param whose literal quoted name
    /// (<c>"paramName"</c>) does not appear anywhere in that text — the exact "declared, accepted,
    /// ignored" shape every one of E28's seven fixes closed one instance of. Kept as a bare static
    /// function over plain data, not <see cref="AtomKindRegistry"/> directly, so the planted-violation
    /// tests below can drive it with a fabricated kind/text pair and prove it discriminates without
    /// mutating the real registry (<see cref="AtomKindRegistry"/>'s own kind table is a private static
    /// field built once at class-init — not meant to be reached into by a test).
    /// </summary>
    static List<string> FindUnwiredParams(string kindId, IEnumerable<string> paramNames, string consumerText)
    {
        var missing = new List<string>();
        foreach (var name in paramNames)
        {
            if (GenericOverlayKeys.Contains(name)) continue;

            var needle = "\"" + name + "\"";
            if (!consumerText.Contains(needle, StringComparison.Ordinal))
                missing.Add($"{kindId}.{name}: declared on ParamSchema but {needle} does not appear " +
                    "anywhere in its mapped consumer source -- declared, accepted, ignored " +
                    "(spec-param-parity.md Test 12)");
        }
        return missing;
    }

    /// <summary>
    /// Read a repository-relative source file, asking each repository whether it carries it.
    ///
    /// <para><b>Why not just <c>FindRepoRoot()</c>.</b> Ten of the sixteen kinds map to <c>Sink</c>, and
    /// <c>Sink</c> is <c>src/FusionRpg.Injector/Effects/InjectorEffectActionSink.cs</c> - the INJECTOR, which
    /// is gk-fusion's. Measured: every other consumer constant is gk-core's, and that one is not. Built
    /// from gk-core the read missed, and because ten kinds share the constant the test failed on
    /// <c>String.Join</c> over a sequence whose first element threw - a stack trace naming LINQ rather than
    /// the file that moved.
    ///
    /// <para>The order is gk-core first, so a path both repositories carry resolves the way it did before
    /// the split; a path only gk-fusion carries then resolves there. That is nearest-match-wins over the
    /// repositories, which is what the shared <c>KeepverseRoots</c> does for a marker and what this does
    /// for a whole file - and it is why the accessor is asked per FILE rather than per prefix: the
    /// repositories share the directory name <c>src/</c> and not its contents.
    /// </para></summary>
    static readonly (string Name, Func<string> Root)[] SourceOwners =
    {
        ("gk-core", () => KeepverseRoots.Core()),
        ("gk-fusion", () => KeepverseRoots.Fusion()),
        ("gk-forge", () => KeepverseRoots.Forge()),
    };

    static string ReadRepoFile(string repoRelativePath)
    {
        var relative = repoRelativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var (name, root) in SourceOwners)
        {
            var path = Path.Combine(root(), relative);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        var tried = string.Join(", ", SourceOwners.Select(o => $"{o.Name}/{relative}"));
        throw new FileNotFoundException(
            "no repository carries this source file: " + repoRelativePath + " (looked for " + tried + ")");
    }

    // Mirrors the FindRepoRoot pattern PlantSideStatusGuardTests.cs / SpawnNonGridExecutorGuardTests.cs
    // already use in Guard.Tests, generalised to any src/ path rather than only gk-fusion/src/FusionRpg.Injector.
    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    // ---- the real check, against the live registry --------------------------------------------------

    // Mirrors AtomKindRegistryTests' own `awaitingConsumer` set exactly (same kinds, same meaning: no
    // real executor in ANY runtime yet, so nothing can have read a declared param). First occupant
    // since this guard shipped (E28): `element.convert` (D56) validates its own params but has no real
    // reader — the combat-dispatch call site is a genuine, undecided engineering choice (spec
    // §2b), not forced here just to keep this guard green. A param-parity violation on a kind with NO
    // consumer at all is not the "declared, accepted, ignored" defect this file exists to catch (that
    // defect needs a real, wrong consumer to ignore the param) — it is the same "awaiting a consumer"
    // state AtomKindRegistryTests already has a name for. Remove from both sets together the day a
    // real reader lands.
    static readonly HashSet<string> AwaitingConsumer = new(StringComparer.Ordinal) { "element.convert" };

    [Fact]
    public void Every_declared_param_on_every_shipped_kind_reaches_its_real_consumer_source()
    {
        Assert.True(AtomKindRegistry.All.Count > 0, "registry is empty -- nothing to check");

        var missing = new List<string>();
        foreach (var kind in AtomKindRegistry.All)
        {
            if (AwaitingConsumer.Contains(kind.KindId))
            {
                // No runtime claims support (AtomKindRegistryTests' own guard proves this), so there is
                // structurally nothing to read this kind's params yet -- asserted here rather than
                // silently skipped, so a kind that leaves the AwaitingConsumer set without ever gaining
                // a ConsumerFiles entry still fails loudly below, not silently.
                Assert.True(kind.SupportIn(RuntimeId.Lawn) == RuntimeState.None
                    && kind.SupportIn(RuntimeId.Battle) == RuntimeState.None
                    && kind.SupportIn(RuntimeId.Sim) == RuntimeState.None,
                    $"{kind.KindId} is listed as AwaitingConsumer but claims real runtime support -- " +
                    "remove it from AwaitingConsumer and add a real ConsumerFiles entry instead");
                continue;
            }

            if (!ConsumerFiles.TryGetValue(kind.KindId, out var files))
            {
                missing.Add($"{kind.KindId}: no consumer-file mapping registered in " +
                    $"{nameof(ParamParityGuardTests)}.{nameof(ConsumerFiles)} -- every kind " +
                    "AtomKindRegistry ships must be mapped here, or this test stops protecting it " +
                    "the moment a new kind lands (spec-param-parity.md Test 12's own " +
                    "'a thirteenth kind' case)");
                continue;
            }

            var text = string.Join("\n", files.Select(ReadRepoFile));
            var paramNames = kind.Params.Defs.Select(d => d.Name);
            missing.AddRange(FindUnwiredParams(kind.KindId, paramNames, text));
        }

        Assert.True(missing.Count == 0, "param-parity violations:\n" + string.Join("\n", missing));
    }

    // Drift protection the other way: a stale or mistyped map entry (a kind id the registry no longer
    // ships, or never did) should fail loudly rather than sit silently unread forever.
    [Fact]
    public void The_consumer_file_map_names_no_kind_id_the_registry_does_not_actually_ship()
    {
        var realKindIds = AtomKindRegistry.All.Select(k => k.KindId).ToHashSet(StringComparer.Ordinal);
        foreach (var mappedKindId in ConsumerFiles.Keys)
            Assert.True(realKindIds.Contains(mappedKindId),
                $"{mappedKindId} is mapped in {nameof(ConsumerFiles)} but AtomKindRegistry does not " +
                "ship it -- stale map entry");
    }

    // ---- PLANTED VIOLATION: proves the mechanism above actually discriminates -----------------------
    //
    // `board.action`'s own real `damage` defect (E28 fix #2, spec-param-parity.md row 2) was exactly
    // this shape before it landed: `damage` was declared on the kind, validated at bind, and never
    // once reached `ExecBoardAction`'s payload -- validation is not proof of use. Reproduced here
    // against FindUnwiredParams directly, matching WaveControlTests'
    // PLANTED_VIOLATION_a_chainDepth_one_event_must_not_be_allowed_to_reach_any_op and MatchModifyTests'
    // own planted violations: call the real check at both a safe and an unsafe input, side by side, so
    // the discriminating power of the mechanism is proven, not merely asserted.

    [Fact]
    public void PLANTED_VIOLATION_a_declared_param_missing_from_its_consumer_text_is_caught()
    {
        // Simulates the pre-fix #2 shape: `wiredParam` reaches the (fake) executor, `droppedParam`
        // is declared on the schema and never read anywhere in the (fake) consumer source.
        const string consumerTextMissingOneParam =
            "static bool ExecFake(JsonElement p) => JsonOverlay.GetInt(p, \"wiredParam\", 0) > 0;";

        var missing = FindUnwiredParams(
            "fake.kind", new[] { "wiredParam", "droppedParam" }, consumerTextMissingOneParam);

        var only = Assert.Single(missing);
        Assert.Contains("fake.kind.droppedParam", only, StringComparison.Ordinal);
    }

    [Fact]
    public void CONTRAST_the_same_check_reports_nothing_once_every_param_is_wired()
    {
        // The post-fix #2 shape: identical two params, but the (fake) consumer source now reads both
        // -- the exact difference `damage` forwarding into ExecBoardAction's payload made for real.
        const string consumerTextWithBothParams =
            "static bool ExecFake(JsonElement p) => JsonOverlay.GetInt(p, \"wiredParam\", 0) > 0 " +
            "&& JsonOverlay.GetInt(p, \"droppedParam\", 0) > 0;";

        var missing = FindUnwiredParams(
            "fake.kind", new[] { "wiredParam", "droppedParam" }, consumerTextWithBothParams);

        Assert.Empty(missing);
    }

    // A generic-overlay key IS exempt when it happens to collide with a declared name -- proves
    // GenericOverlayKeys actually short-circuits FindUnwiredParams rather than merely existing unused.
    [Fact]
    public void PLANTED_VIOLATION_but_a_generic_overlay_key_name_is_exempt_even_when_unwired()
    {
        var missing = FindUnwiredParams("fake.kind", new[] { "chance" }, consumerText: "");
        Assert.Empty(missing);
    }
}
