using FusionRpg.Core.Creatures.Generation;

namespace FusionRpg.Data.Seed;

/// <summary>What one <see cref="SpeciesImportRunner.RunSelfHealing"/> call did.</summary>
public enum SpeciesImportStatus
{
    /// <summary>This call wrote the roster — the <c>creature_species</c> tables were empty.</summary>
    Imported,

    /// <summary>The tables already held species; nothing was read or written. A normal relaunch is a
    /// true no-op, exactly like <see cref="SeedImportRunner.RunSelfHealing"/>'s own gate.</summary>
    AlreadyCurrent,

    /// <summary>The tables were empty and no <c>gk-data/packs/fusion/data/generated/creatures</c> tree was reachable from the
    /// search start directory. Not fatal here — the caller decides, and the server's own decision is to
    /// fail loudly at <c>CreatureSpeciesCatalog.Configure</c> rather than run a stale roster.</summary>
    SpeciesTreeNotFound,

    /// <summary>The tables were empty, a tree was found, and reading or importing it failed.
    /// <c>Detail</c> says why.</summary>
    Failed,
}

/// <param name="Detail">Human-readable reason, present on every status except <see cref="SpeciesImportStatus.Imported"/>
/// and <see cref="SpeciesImportStatus.AlreadyCurrent"/>.</param>
/// <param name="Outcome">The transaction's own report, when an import transaction actually ran.</param>
public sealed record SpeciesImportRunResult(SpeciesImportStatus Status, string? Detail, SpeciesImportOutcome? Outcome)
{
    /// <summary>True when the roster holds real species either because this call just wrote them or
    /// because an earlier launch already did.</summary>
    public bool Ok => Status is SpeciesImportStatus.Imported or SpeciesImportStatus.AlreadyCurrent;
}

/// <summary>
/// `player-content-boot` (E46) — the species half of the same defect <see cref="SeedImportRunner"/> closes
/// for the atom seed tree. Owner ruling 2026-09-23 (recorded in <c>tasks/content-stack-todo.md</c> CS-F3):
/// <b>the species tree ships inside the pack and the boot self-heals the roster once</b>, mirroring
/// <see cref="SeedImportRunner.RunSelfHealing"/>. No launcher change.
///
/// <para><b>Why it is needed.</b> Since the <c>catalog-runtime</c> flip (2026-09-05) the roster is
/// store-backed — <c>Program.cs</c> calls <c>CreatureSpeciesCatalog.Configure(store.BuildCreatureSpeciesSnapshot())</c>
/// — and <see cref="FusionRpg.Core.Creatures.CreatureSpeciesCatalog.Configure"/> THROWS on an empty roster.
/// The only writer of those tables was <c>gk-forge/tools/CreatureSpeciesImport</c>, a developer CLI, so a fresh
/// player install died at startup with an empty-roster exception. This runner is that write path, called
/// by the boot when the tables are empty.</para>
///
/// <para><b>It reads the committed CONCRETE tree, not the anchors.</b> <c>gk-data/packs/fusion/data/generated/creatures/*.json</c>
/// are already the expanded, reviewable species rows, parsed by the same
/// <see cref="ConcreteSpeciesSeedReader"/> and mapped by the same
/// <see cref="ConcreteSpeciesMapper"/> that <c>RpgStore.BuildCreatureSpeciesSnapshot</c> uses — the
/// Injector's own host already loads its roster exactly this way (<c>RpgHost</c>). Re-deriving from
/// <c>gk-data/packs/fusion/data/seed/creatures/species</c> at boot would put the whole classification/expansion pipeline in a
/// player's startup path, and its staleness refusal (which the CLI needs before it writes) would refuse a
/// shipped install rather than repair it.</para>
///
/// <para><b>Never throws</b>, for the same reason <see cref="SeedImportRunner.RunSelfHealing"/> never does:
/// a missing or broken tree must not reach the caller as an exception, so the caller can decide what to
/// report. <c>ImportSpecies</c> is one transaction, so a throw from inside it has already rolled back.</para>
/// </summary>
public static class SpeciesImportRunner
{
    /// <summary>The tree the concrete species rows live in, relative to the content root.</summary>
    public static readonly string[] TreeSegments = { "data", "generated", "creatures" };

    /// <param name="store">Already constructed and <c>Init()</c>-ed — this never opens its own store.</param>
    /// <param name="searchStartDir">Where to start walking up for <c>gk-data/packs/fusion/data/generated/creatures</c> — the
    /// server passes its own <c>AppContext.BaseDirectory</c>, the same root <c>gk-core/data/tuning</c> and
    /// <c>gk-data/packs/fusion/data/seed</c> already resolve against, so the import reads the tree relative to wherever this
    /// process actually runs.</param>
    public static SpeciesImportRunResult RunSelfHealing(RpgStore store, string searchStartDir)
    {
        if (store.ListSpeciesIds().Count > 0)
            return new SpeciesImportRunResult(SpeciesImportStatus.AlreadyCurrent, null, null);

        try
        {
            var speciesDir = SeedImportRunner.FindUp(searchStartDir, TreeSegments);
            if (speciesDir is null)
                return new SpeciesImportRunResult(SpeciesImportStatus.SpeciesTreeNotFound,
                    $"no data/generated/creatures found walking up from {searchStartDir}", null);

            // `_`-prefixed entries are the build plan and the fusion recipes — sibling artifacts of the
            // same tree, not species rows. `RpgHost` and `gk-forge/tools/CreatureSpeciesImport` both skip them the
            // same way, so all three readers agree on what a species file is.
            var files = Directory.EnumerateFiles(speciesDir, "*.json", SearchOption.AllDirectories)
                .Where(p => !Path.GetFileName(p).StartsWith('_'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
                return new SpeciesImportRunResult(SpeciesImportStatus.SpeciesTreeNotFound,
                    $"{speciesDir} exists but holds no species *.json", null);

            var species = new List<ConcreteSpecies>(files.Count);
            foreach (var file in files)
            {
                try
                {
                    species.Add(ConcreteSpeciesSeedReader.ParseFile(file));
                }
                catch (Exception ex)
                {
                    // One unreadable row is a broken install, not a reason to import a partial roster:
                    // report the file and write nothing, so the tables stay empty and the caller's own
                    // loud path fires.
                    return new SpeciesImportRunResult(SpeciesImportStatus.Failed,
                        $"{Path.GetFileName(file)}: {ex.Message}", null);
                }
            }

            var outcome = store.ImportSpecies(species);
            if (!outcome.IsOk)
                return new SpeciesImportRunResult(SpeciesImportStatus.Failed, Describe(outcome.Errors), outcome);

            return new SpeciesImportRunResult(SpeciesImportStatus.Imported, null, outcome);
        }
        catch (Exception ex)
        {
            return new SpeciesImportRunResult(SpeciesImportStatus.Failed, ex.Message, null);
        }
    }

    static string Describe(IReadOnlyList<SpeciesImportError> errors) =>
        $"{errors.Count} error(s): " + string.Join("; ", errors.Take(5).Select(e => e.ToString()))
        + (errors.Count > 5 ? $" (+{errors.Count - 5} more)" : "");
}
