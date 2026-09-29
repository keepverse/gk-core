namespace FusionRpg.Core.Creatures;

/// <summary>
/// `catalog-runtime` (T4.7 step 2 / T4.8, `spec-catalog-runtime.md` §3, creature-seed module 14, ⭐ "the
/// riskiest module in the program") — the seam a static class needs to read a roster it did not
/// compile in. Synthesises the two named precedents exactly as the spec asks: <see cref="Configure"/>
/// throws with no built-in default (<c>DerivedStatPolicy</c>'s own discipline — "there is nowhere to
/// pass a data directory" for a static class, so a missing call is a startup-ordering bug, never
/// papered over); <see cref="UseScoped"/> is an <c>AsyncLocal</c> override so one test's roster never
/// leaks into a test running beside it (<c>ElementTable</c>/<c>ChannelPolicyTable</c>'s own shape —
/// unlike those two, this hub has no safe empty default: an empty creature roster is exactly the loud
/// failure §4 asks for, not something to fall back through silently).
///
/// <para><b>Loaded once, immutable for the process lifetime (§3a).</b> No live reload — an import
/// requires a host restart, which is already how this repo deploys. The three downstream catalogs
/// (<c>WaveCatalog</c>, <c>CreatureRecipeCatalog</c>, <c>CreatureMaterialCatalog</c>) already converted to
/// lazy `_x ??= Build()` properties (T4.7 step 1) specifically so their own first touch happens after
/// <see cref="Configure"/> runs, not at an unpredictable point tied to class-load order.</para>
/// </summary>
public static partial class CreatureSpeciesCatalog
{
    static IReadOnlyList<CreatureSpeciesDef>? _configured;
    static readonly AsyncLocal<IReadOnlyList<CreatureSpeciesDef>?> Scoped = new();

    /// <summary>`species-build` T1.2 — a non-throwing check for callers that must treat the roster as
    /// an optional enrichment rather than a hard requirement (`RpgXpAwardMap`'s species-placement
    /// award: most progression tests never configure a roster, and awarding type/player XP must keep
    /// working exactly as it always has when one isn't present).</summary>
    public static bool IsConfigured => Scoped.Value != null || _configured != null;

    /// <summary>
    /// Process-wide. What a host calls once, after loading the roster from its store
    /// (<c>RpgStore.BuildCreatureSpeciesSnapshot()</c>). Validates and rejects a bad roster the same way
    /// <see cref="All"/> always has — a species with an unknown trait, a duplicate id, or a missing
    /// acquisition flag is a startup error here too, not a runtime surprise moved one layer later.
    /// </summary>
    /// <exception cref="InvalidOperationException">The roster is empty (§4: "today the catalog
    /// cannot be empty... after this change it can be — a fresh database, a failed import, a wrong
    /// data directory. Failing loudly at load beats failing later").</exception>
    public static void Configure(IReadOnlyList<CreatureSpeciesDef> snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.Count == 0)
            throw new InvalidOperationException(
                "CreatureSpeciesCatalog.Configure received an empty species roster. A server that starts " +
                "with zero species reports healthy and fails later, untraceably, in SummonRoller. Run " +
                "'dotnet run --project tools/CreatureSpeciesImport' against the data directory this host " +
                "points at, or point FUSIONRPG_DATA at one that already has an imported roster.");

        _configured = Validate(snapshot);
        _byId = null; // a fresh Configure invalidates any cached id map from a previous one
    }

    /// <summary>Swap the roster for THIS async context only, and put it back on dispose — the same
    /// isolation `ElementTable.UseScoped`/`ChannelPolicyTable.UseScoped` already give every other
    /// process-global table, so one test's roster is never visible to a test running beside it under
    /// xUnit's default cross-class parallelism.</summary>
    public static IDisposable UseScoped(IReadOnlyList<CreatureSpeciesDef> snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var validated = Validate(snapshot);
        var previous = Scoped.Value;
        Scoped.Value = validated;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly IReadOnlyList<CreatureSpeciesDef>? _previous;
        public Restore(IReadOnlyList<CreatureSpeciesDef>? previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }

    /// <summary>Reset the process-wide roster — test teardown only, mirroring
    /// `ChannelPolicyTable.ResetToEmpty`'s own role (never called by a host).</summary>
    public static void ResetToUnconfigured() { _configured = null; _byId = null; }

    /// <summary>
    /// The COMPILED-roster path (T4.7 step 2 / T4.8 step 2-4): configure from the same compiled
    /// roster `All` always read before this module existed — behaviour-preserving by construction,
    /// since `Validate(GeneratedSpecies)` is exactly what the old lazy `All` computed.
    ///
    /// <para><b>Which callers these are — corrected 2026-09-23 (spec-species-rank.md §4's own doc
    /// hazard, GAP-3):</b> the tools and the test suites, not the live hosts. This comment used to
    /// claim "Every host calls this today, including the two live-game hosts (`Server/Program.cs`,
    /// `Injector/Host/RpgHost.cs`)", and that had been false since `catalog-runtime`'s own flip:
    /// <c>Server/Program.cs:618</c> calls <see cref="Configure"/> with
    /// <c>store.BuildCreatureSpeciesSnapshot()</c>, and <c>Injector/Host/RpgHost.cs:145</c> calls it
    /// with a roster parsed from the committed <c>gk-data/packs/fusion/data/generated/creatures/**</c> tree by
    /// <c>ConcreteSpeciesSeedReader</c>. The real callers here are
    /// <c>gk-forge/tools/CreatureCorpusEmit</c>, <c>gk-forge/tools/CreatureSpeciesGen</c>, <c>gk-forge/tools/CreatureSpeciesImport</c>,
    /// <c>gk-forge/tools/ProveHubCombat</c>, <c>gk-core/tools/TurnOrderProbe</c>, <c>gk-core/tools/SquadHarness</c> and the
    /// test bootstraps. (The historical reason for the claim still reads true: a store-backed snapshot
    /// CAN shrink a roster, which is why the flip was owner-gated — but it has since happened, so the
    /// sentence as written was a live misdirection about which path a player's roster takes.)</para>
    /// </summary>
    public static void ConfigureFromCompiledDefault() => Configure(GeneratedSpecies);
}
