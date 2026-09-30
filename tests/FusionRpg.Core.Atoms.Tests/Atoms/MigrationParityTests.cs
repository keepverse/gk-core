using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// Checkpoint D (spec-effect-def-migration.md): the 16 hardcoded defs are rows, and the rows behave
/// identically.
///
/// <para><b>The gate is the whole fixture corpus, not a sample.</b> Every scenario under
/// <c>fixtures/effects/scenarios</c> is run twice — once against <c>EffectSeedCatalog</c>, once
/// against the catalog compiled from <c>data/seed/atoms/fx-*.json</c> — and every plan either side
/// produces is compared as serialized JSON. Not equivalent: identical.</para>
///
/// <para>This is the only place the schema gets falsified before content authoring begins, and it
/// earned its keep: it found the compiled def id was <c>atom.atom.*</c>, that a compiled
/// <c>stat.modify</c> reached FA1 as neither <c>flat</c> nor <c>increased</c> nor <c>more</c> and
/// applied a flat zero, and that a three-trigger group emitted three copies of one action.</para>
/// </summary>
public class MigrationParityTests
{
    static readonly JsonSerializerOptions Bytes = new() { WriteIndented = false };

    // ---- the migrated catalog ------------------------------------------------------------------

    /// <summary>
    /// The migrated defs, and only those — the <c>fx-*.json</c> files.
    ///
    /// <para>This swept the whole <c>atoms/</c> folder until E12 put a trait atom in it and every
    /// fixture went red at once. The gate is about <b>the def migration</b>: it asserts nothing was
    /// rejected and nothing needed the runner, which is true of the 16 defs and is not a claim about
    /// authored content in general. A later atom that is deliberately battle-only would otherwise
    /// break a gate that has nothing to do with it.</para>
    /// </summary>
    static IReadOnlyList<AtomRow> SeedRows()
    {
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "atoms");
        var files = Directory.GetFiles(dir, "fx-*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        return collected.Content.Atoms;
    }

    /// <summary>
    /// Compiled for the <b>lawn</b>, which is the runtime the corpus describes. Sim is the host, but
    /// the fixtures assert lawn behaviour, and two kinds (`shield.grant`, `stat.modify`) are
    /// deliberately not supported in sim — compiling for sim would drop them and the corpus would
    /// pass by omission.
    /// </summary>
    static List<EffectDef> MigratedCatalog()
    {
        var compiled = AtomCompiler.Compile(SeedRows(), RuntimeId.Lawn, 1, hostIsPlanner: true);
        Assert.Empty(compiled.Rejected);
        Assert.Empty(compiled.Runtime); // every migrated def must COMPILE; none may need the runner
        return compiled.Defs.Select(AtomPushCodec.ToDef).ToList();
    }

    [Fact]
    public void Every_seeded_def_has_a_migrated_row_with_the_same_id()
    {
        var seeded = EffectSeedCatalog.CreateAll().Select(d => d.EffectId).OrderBy(x => x, StringComparer.Ordinal);
        var migrated = MigratedCatalog().Select(d => d.EffectId).OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(seeded, migrated);
    }

    [Fact]
    public void There_are_sixteen_of_them()
    {
        // The count the whole program has quoted. Verified by counting, not by trusting the number.
        Assert.Equal(16, EffectSeedCatalog.CreateAll().Count);
        Assert.Equal(16, MigratedCatalog().Count);
    }

    [Theory]
    [MemberData(nameof(SeededDefIds))]
    public void Each_migrated_def_matches_its_seeded_twin(string effectId)
    {
        var seeded = EffectSeedCatalog.CreateAll().Single(d => d.EffectId == effectId);
        var migrated = MigratedCatalog().Single(d => d.EffectId == effectId);

        Assert.Equal(seeded.EffectType, migrated.EffectType);
        Assert.Equal(seeded.Name, migrated.Name);
        Assert.Equal(seeded.Enabled, migrated.Enabled);

        // A Passive def and the lifecycle pair are the same statement, and the spec resolved
        // `fx.passive_atk_flat` to one triggerless atom for exactly that reason: `EffectBag` fires
        // the pair for a Passive def whether or not it lists them, and injects `remove = true`
        // itself. The plan comparison below is what actually holds the two paths together.
        var lifecycleOnly = seeded.Triggers.Count > 0
            && seeded.Triggers.All(t => t is EffectTriggers.OnGranted or EffectTriggers.OnRemoved);
        if (!(lifecycleOnly && migrated.EffectType == EffectTypes.Passive && migrated.Triggers.Count == 0))
            Assert.Equal(
                seeded.Triggers.OrderBy(t => t, StringComparer.Ordinal),
                migrated.Triggers.OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(
            seeded.Actions.Select(a => a.Action),
            migrated.Actions.Select(a => a.Action));

        foreach (var (want, got) in seeded.Actions.Zip(migrated.Actions))
        {
            var wantCanon = Canonical(want.Params);
            var gotCanon = Canonical(got.Params);

            // E28 (spec-param-parity.md, content fix + fix #4): two genuine, deliberate content
            // corrections in the live seed data, NOT mirrored into the frozen EffectSeedCatalog
            // oracle — its own class doc comment is explicit that it stays "byte-identical to what
            // it always was" and "do not add new defs here". Pinned exactly on both sides, so a
            // THIRD, unintended drift on top of these two known ones still fails loudly instead of
            // hiding behind a broad exclusion.
            if (effectId == "fx.set_dirt_box")
            {
                Assert.Equal("boxType=1,col=3,row=2", wantCanon); // frozen: pre-fix boxType (Water)
                Assert.Equal("boxType=2,col=3,row=2", gotCanon);  // live: corrected to Dirt
                continue;
            }
            if (effectId == "fx.grid_item_cycle" && want.Action == EffectActions.ClearGridItem)
            {
                Assert.Equal("gridItemType=7,selector=\"last\"", wantCanon); // frozen: the naming lie
                Assert.Equal("col=3,gridItemType=7,row=2", gotCanon);       // live: targets the cell
                continue;
            }
            // lawn-combat-wire (2026-09-15): a third genuine, deliberate content correction. The
            // frozen oracle's `fx.overlay_damage` never carried an "amount" at all -- ResolveAmount
            // (DamagePacketBuilder.cs) silently defaulted every direct hit through this atom to a
            // SignedAmount of 0, regardless of the real incoming vanilla damage. Fixed live by
            // authoring the already-built (never-used) event-linked ValueSpec marker
            // ({"eventField":"damage","multiplierMilli":1000}) so the atom actually reads the real
            // hit amount -- a wiring gap, not a new mechanism. Never mirrored into EffectSeedCatalog,
            // same as the two exceptions above.
            if (effectId == "fx.overlay_damage")
            {
                Assert.Equal("channel=\"hp\"", wantCanon); // frozen: no amount source at all
                Assert.Equal(
                    "amount={\"eventField\":\"damage\",\"multiplierMilli\":-1000},channel=\"hp\"",
                    gotCanon); // live: reads the real event damage
                continue;
            }

            Assert.Equal(wantCanon, gotCanon);
        }
    }

    public static TheoryData<string> SeededDefIds()
    {
        var data = new TheoryData<string>();
        foreach (var def in EffectSeedCatalog.CreateAll()) data.Add(def.EffectId);
        return data;
    }

    [Fact]
    public void The_passive_modifier_reaches_FA1_by_the_key_FA1_reads()
    {
        // The defect this module found. FA1 spells the operation with the key — `flat`, `increased`,
        // `more` — and a compiled `{channel, op, amount}` matched none of them, so the executor fell
        // through to its `mods.Count == 0` arm and applied a flat ZERO. A real modifier of no size.
        var def = MigratedCatalog().Single(d => d.EffectId == "fx.passive_atk_flat");

        var action = Assert.Single(def.Actions);
        Assert.True(action.Params.ContainsKey("flat"), "FA1 reads 'flat', not 'op' + 'amount'");
        Assert.False(action.Params.ContainsKey("op"));
        Assert.False(action.Params.ContainsKey("amount"));
    }

    [Fact]
    public void The_three_trigger_shield_grants_one_shield_not_three()
    {
        // Three atoms, one shared icd_key, one action, three triggers.
        var def = MigratedCatalog().Single(d => d.EffectId == "fx.shield_grant");

        Assert.Single(def.Actions);
        Assert.Equal(3, def.Triggers.Count);
    }

    [Fact]
    public void The_permanent_modifier_is_passive_and_declares_no_trigger()
    {
        // definitions §14.2: the bag fires the lifecycle pair only for a Passive def or one whose
        // triggers contain OnGranted. Compiled as the default Triggered, it would never apply at all.
        var def = MigratedCatalog().Single(d => d.EffectId == "fx.passive_atk_flat");

        Assert.Equal(EffectTypes.Passive, def.EffectType);
        Assert.Empty(def.Triggers);
    }

    [Fact]
    public void The_two_action_defs_keep_their_order()
    {
        // `seq` is authoring order and it is stable. A plant spawned after its bullet is a different
        // effect from one spawned before it.
        var def = MigratedCatalog().Single(d => d.EffectId == "fx.spawn_plant_bullet");

        Assert.Equal(2, def.Actions.Count);
        Assert.Equal("plant", def.Actions[0].Params["kind"]);
        Assert.Equal("bullet", def.Actions[1].Params["kind"]);
    }

    [Fact]
    public void The_patron_aura_container_now_carries_the_real_migration_atoms()
    {
        // Updated 2026-09-06 (patron-absorption, T6.2b) — this test's own earlier name/assertion
        // ("...with_no_atoms") was the LOCKED invariant that correctly refused an UNPLANNED fill
        // before the real migration existed (see this test file's own history and
        // patron-aura-not-atom-backed's memory note). That lock did its job: it is being lifted here
        // deliberately, by the real migration itself (spec-patron-absorption.md's own 2026-09-06
        // amendment), not silently. `patron.aura` is no longer "a Passive with no triggers and no
        // actions" — it carries 12 real stat.derived atoms (one per element × power/defense), each
        // resolved via the new externalRef ValueSpec marker, never an invented magnitude.
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "containers");
        var files = Directory.GetFiles(dir, "*.json").Select(f => (f, File.ReadAllText(f))).ToArray();

        var collected = AtomSeedFile.Collect(files);

        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        var marker = collected.Content.Containers.Single(c => c.ContainerId == "patron.aura");
        // The exact count (12 today, one per element x power/defense) is a growing content fact, never
        // pinned (population-pin SE3.3, 2026-09-19); the real contract this test protects is "no longer
        // zero atoms" (the pre-migration lock this section replaces).
        Assert.NotEmpty(marker.Atoms);
        Assert.Empty(marker.Pool); // still fixed-core only — deterministic per (rarity, star, level, Θ), never rolled
    }

    // ---- the fixture corpus, both paths ------------------------------------------------------------

    [Fact]
    public void The_corpus_is_the_whole_scenario_set_not_a_sample()
    {
        // The program quoted "19 fixtures" for several rounds; the sweep found 49. Counted here so
        // the gate cannot quietly shrink.
        Assert.True(ScenarioFiles().Count >= 49,
            $"only {ScenarioFiles().Count} scenarios found — the parity gate must run all of them");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_fixture_produces_byte_identical_plans_down_both_paths(string file)
    {
        var root = FindFixtures();
        var path = Path.Combine(root, "effects", "scenarios", file);

        var seeded = EffectScenarioRunner.RunFile(path, root);
        var migrated = EffectScenarioRunner.RunFile(path, root, MigratedCatalog());

        Assert.Equal(seeded.Ok, migrated.Ok);
        Assert.Equal(seeded.Steps.Count, migrated.Steps.Count);

        for (var i = 0; i < seeded.Steps.Count; i++)
        {
            var a = seeded.Steps[i];
            var b = migrated.Steps[i];
            Assert.Equal(a.Op, b.Op);
            Assert.Equal(a.Ok, b.Ok);
            Assert.Equal(Plan(a.Plan), Plan(b.Plan));
        }
    }

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var f in ScenarioFiles()) data.Add(f);
        return data;
    }

    static List<string> ScenarioFiles() =>
        Directory.GetFiles(Path.Combine(FindFixtures(), "effects", "scenarios"), "*.json")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// The plan, canonically: keys sorted, arrays left alone.
    ///
    /// <para><b>Key emission order is normalised because it is not part of plan equality.</b> The
    /// shipped comparison — the same one the 15 golden files are checked with — looks each expected
    /// param up by name (<c>EffectScenarioRunner.ComparePlans</c>), so no consumer can observe the
    /// order a dictionary happened to enumerate in.</para>
    ///
    /// <para>Everything else stays strict, and this is <b>stronger</b> than the shipped comparison,
    /// not weaker: <c>ComparePlans</c> only checks that expected's keys are present, so a migrated
    /// def emitting an <i>extra</i> param passes it. Here it fails.</para>
    /// </summary>
    static string Plan(object? plan) =>
        plan is null ? "(none)" : ContentHash.CanonicalJson(JsonSerializer.Serialize(plan, Bytes));

    /// <summary>
    /// Params compared by value, not by boxed type. The seeded catalog writes <c>4f</c> where a row
    /// writes <c>4</c>; both serialize to <c>4</c>, which is what actually reaches the executor.
    /// </summary>
    static string Canonical(IReadOnlyDictionary<string, object?> pars) =>
        string.Join(",", pars.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + JsonSerializer.Serialize(p.Value, Bytes)));

    static string FindFixtures()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("fixtures");
    }

    static string RepoRoot() => ContentRoot.Path;
}
