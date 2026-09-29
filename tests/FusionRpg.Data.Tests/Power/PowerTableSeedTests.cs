using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Data;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Data.Tests.Power;

/// <summary>
/// ST4.5c (manager ruling on the ST4.5a diagnosis): the shipped <c>gk-data/packs/fusion/data/seed/power</c> tree carries the
/// trigger frequencies as seed data, and importing it is what stops
/// <c>CostFunction.Conditionality</c> multiplying every triggered atom by zero.
///
/// <para><b>Why the shapes are asserted as contracts and never as counts.</b> The frequency set is a
/// closed, authored vocabulary a balance pass edits; its size is a reading, not a constant. So the
/// tests state "the shipped seed carries what <c>PowerTables.Authored()</c> carries, verbatim" (which
/// is what makes seeding a value a no-op for balance), "the pricing consequence is gone", and "a
/// re-import changes nothing" — none of which moves when a trigger is retuned or added.</para>
///
/// <para>Joins the <c>EffectAtomRuntimeGlobals</c> collection because <c>LoadContentIntoRuntime</c>
/// swaps process-global statics on purpose; the shipped defaults are restored in <c>Dispose</c> so a
/// failure here cannot leak into another test.</para>
/// </summary>
[Collection("EffectAtomRuntimeGlobals")]
public class PowerTableSeedTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public PowerTableSeedTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        PowerTables.ResetToAuthored();
        _testStore.Dispose();
    }

    static string RepoRoot() => ContentRoot.Path;

    /// <summary>The real committed power seed, read through the real seed reader — the same two calls
    /// the boot import makes (`SeedScanner` sweep -> `ImportContent`).</summary>
    static SeedContent ShippedPowerSeed()
    {
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "seed", "power"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToList();
        Assert.NotEmpty(files);

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors.Select(e => e.ToString())));
        return collected.Content;
    }

    [Fact]
    public void The_shipped_seed_carries_every_trigger_Authored_defaults_to_at_the_same_rate()
    {
        // "Verbatim from PowerTables.Authored()" is the claim that makes seeding them a no-op for
        // balance, so it is asserted against the code default itself rather than against a copy of it.
        var seed = ShippedPowerSeed();

        foreach (var authored in PowerTables.Authored().Frequencies)
            Assert.Contains(seed.TriggerFrequencies, r => r.Trigger == authored.Trigger && r.PerMinute == authored.PerMinute);
    }

    [Fact]
    public void Importing_the_shipped_seed_leaves_a_triggered_atom_no_longer_priced_at_zero()
    {
        // The defect, end to end: seed reader -> store -> LoadContentIntoRuntime -> CostFunction.Price.
        // Before this seed, `power_coefficient` held rows so `GetPowerTables` skipped the Authored()
        // fallback, `FrequencyOf` returned 0 for every trigger, and this same atom priced at 0 with a
        // Priced verdict -- a whole family free and nothing flagged.
        var outcome = _store.ImportContent(ShippedPowerSeed());
        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors.Select(e => e.ToString())));

        _store.LoadContentIntoRuntime();
        Assert.True(PowerTables.Current.FrequencyOf(AtomTriggers.OnDamageDealt) > 0,
                    "the imported table must carry a rate for the trigger the real corpus uses");

        var onHitStatus = new AtomRow
        {
            AtomId = "atom.seedtest.spore",
            KindId = "status.apply",
            FamilyId = "atom.seedtest.spore",
            Tier = 1,
            Name = "probe",
            ParamsJson = """{"duration":5,"level":1,"status":"spore"}""",
            WhenJson = """{"chance":135,"trigger":"OnDamageDealt"}""",
        };

        var priced = CostFunction.Price(onHitStatus);

        Assert.True(priced.Ok, priced.Verdict.Reason);
        Assert.True(priced.Power.Total > 0,
                    "a triggered atom must not price at zero once its trigger carries a frequency");
    }

    [Fact]
    public void A_repeat_import_of_the_unchanged_seed_does_not_bump_the_catalog_revision()
    {
        var seed = ShippedPowerSeed();
        Assert.True(_store.ImportContent(seed).IsOk);
        var revision = _store.GetCatalogRevision();

        var again = _store.ImportContent(seed);

        Assert.True(again.IsOk);
        Assert.Equal(revision, _store.GetCatalogRevision());
    }

    [Fact]
    public void A_zero_rate_is_refused_at_the_import_boundary_and_never_reaches_the_table()
    {
        // The seed reader refuses this too; the import is the other boundary a hand-built SeedContent
        // can arrive through (`UpsertPowerTables`'s own reference-scale check is the precedent).
        //
        // The real seed is imported FIRST so the assertion below reads the STORED table: with an empty
        // coefficient table `GetPowerTables` falls back to PowerTables.Authored(), whose five rows
        // would mask whatever the refused import did.
        Assert.True(_store.ImportContent(ShippedPowerSeed()).IsOk);
        var storedBefore = RatesByTrigger();

        var content = new SeedContent();
        content.TriggerFrequencies.Add(new TriggerFrequencyRow(AtomTriggers.OnDamageDealt, 0));

        var outcome = _store.ImportContent(content);

        Assert.False(outcome.IsOk);
        var error = Assert.Single(outcome.Errors);
        Assert.Equal(AtomTriggers.OnDamageDealt, error.EntryId);
        Assert.Contains("prices every atom carrying it at zero", error.Detail, StringComparison.Ordinal);
        Assert.Equal(storedBefore, RatesByTrigger());
    }

    Dictionary<string, int> RatesByTrigger() =>
        _store.GetPowerTables().Frequencies.ToDictionary(f => f.Trigger, f => f.PerMinute, StringComparer.Ordinal);

    /// <summary>The real authored atom corpus, read through the real seed reader — every file the boot
    /// sweep reaches under <c>gk-data/packs/fusion/data/seed/atoms</c>.</summary>
    static SeedContent ShippedAtomCorpus()
    {
        var root = Path.Combine(RepoRoot(), "data", "seed", "atoms");
        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToList();
        Assert.NotEmpty(files);

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors.Take(3).Select(e => e.ToString())));
        return collected.Content;
    }

    static string? TriggerOf(AtomRow atom)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(atom.WhenJson);
        return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
               && doc.RootElement.TryGetProperty("trigger", out var t)
               && t.ValueKind == System.Text.Json.JsonValueKind.String
            ? t.GetString()
            : null;
    }

    [Fact]
    public void Every_trigger_the_real_atom_corpus_uses_has_a_row_in_the_shipped_seed()
    {
        // ST4.5d's closure guardrail. CostFunction now REFUSES an atom whose trigger has no frequency
        // row, which is only safe if the shipped seed covers every trigger real content authors --
        // otherwise the refusal would fire on shipped content instead of on the mistake it exists to
        // catch. Closure, not a count: the seed may grow a row nobody uses yet (OnDamageTaken already
        // does) without failing this, and a corpus that adopts a new trigger fails it until the seed
        // covers that trigger too.
        var seed = ShippedPowerSeed();
        var tables = new PowerTables(seed.Coefficients, seed.TriggerFrequencies);

        var used = ShippedAtomCorpus().Atoms
            .Select(TriggerOf)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(used);
        foreach (var trigger in used)
            Assert.True(tables.FrequencyOf(trigger) > 0,
                        $"the corpus authors trigger '{trigger}', so data/seed/power/trigger-frequencies.v1.json " +
                        "must carry a row for it — otherwise every atom using it is refused as unpriced");
    }
}
