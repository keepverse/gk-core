using FusionRpg.Core.PassiveTree.State;

namespace FusionRpg.Data.Seed;

/// <summary>This call's status — mirrors <see cref="SeedImportStatus"/>'s own four-way split, kept as
/// its own type rather than reused because this import's gate (<c>GetTreeCatalogRevision</c>) and
/// content shape (<see cref="RpgStore.ImportTreeCatalogFiles"/>) are entirely independent of the atom
/// catalog's own.</summary>
public enum PassiveTreeImportStatus
{
    /// <summary>This call imported the corpus — <c>rpg_tree_catalog_meta.revision</c> moved off zero.</summary>
    Imported,

    /// <summary>The revision was already non-zero; nothing was read or written.</summary>
    AlreadyCurrent,

    /// <summary>The revision was zero, but no <c>gk-data/packs/fusion/data/generated/passive-tree</c> directory with any
    /// <c>*.json</c> file was reachable. Not fatal — H9's corpus is still partial as of 2026-09-07, so
    /// an absent or empty directory is an expected state, not a defect.</summary>
    TreeNotFound,

    /// <summary>The revision was zero, tree files were found, but loading the tuning file or importing
    /// them failed. Not fatal — <see cref="Detail"/> in the result says why.</summary>
    Failed,
}

public sealed record PassiveTreeImportResult(PassiveTreeImportStatus Status, string? Detail, TreeCatalogImportOutcome? Outcome);

/// <summary>
/// The player-content-boot counterpart to <see cref="SeedImportRunner"/>, for the passive-tree catalog
/// specifically — found missing 2026-09-07 while checking what H9's own "committed" acceptance bullet
/// actually requires: <see cref="RpgStore.ImportTreeCatalog"/>/<see cref="RpgStore.ImportTreeCatalogFiles"/>
/// (task C4/C5, already shipped and already tested) had ZERO production callers anywhere in the repo —
/// the server never imported a bound tree catalog into the store on its own, only test fixtures did.
///
/// <para><b>A genuinely separate pipeline from <see cref="SeedImportRunner"/>, not an extension of it.</b>
/// Bound tree catalogs live under <c>gk-data/packs/fusion/data/generated/passive-tree/</c> — a different ROOT entirely from
/// <c>gk-data/packs/fusion/data/seed</c>, which <see cref="SeedScanner"/>'s own owned-folder walk never reaches — and their
/// document shape (one JSON object per tree, <see cref="RpgStore.ImportTreeCatalogFiles"/>'s own
/// <c>(FileName, Json)</c> pairs) has nothing in common with <see cref="SeedContent"/>'s atom/container/
/// curve/rarity/element/channel-policy/affix/coefficient shape. Reusing <see cref="SeedImportRunner"/>
/// would mean forcing two structurally unrelated imports through one signature.</para>
///
/// <para><b>Independent, best-effort, never gates atom-content import or vice versa</b> — matching this
/// codebase's own established pattern for boot-time content checks (e.g. <c>Program.cs</c>'s own rarity
/// power-budget validation: "a lint, never a gate... the server boots anyway"). H9's own corpus is
/// still partial as of 2026-09-07 (2 of 480 primary nodes never generated, most bound nodes refused by
/// a disclosed cross-program pricing gap) — a tree-catalog import failing or finding nothing must never
/// take down, or even slow, the rest of content boot.</para>
///
/// <para><b>Never throws</b>, for the identical reason <see cref="SeedImportRunner.RunSelfHealing"/>
/// never does: every failure path is caught and folded into <see cref="PassiveTreeImportStatus.Failed"/>
/// rather than left to propagate.</para>
/// </summary>
public static class PassiveTreeImportRunner
{
    /// <param name="store">Already constructed and <c>Init()</c>-ed — this never opens its own store.</param>
    /// <param name="searchStartDir">Where to start walking up for <c>gk-data/packs/fusion/data/generated/passive-tree</c>
    /// and <c>gk-core/data/tuning</c> — the server passes its own <c>AppContext.BaseDirectory</c>, the same
    /// root <see cref="SeedImportRunner.RunSelfHealing"/> already resolves against.</param>
    public static PassiveTreeImportResult RunSelfHealing(RpgStore store, string searchStartDir)
    {
        if (store.GetTreeCatalogRevision() != 0)
            return new PassiveTreeImportResult(PassiveTreeImportStatus.AlreadyCurrent, null, null);

        try
        {
            var generatedRoot = SeedImportRunner.FindUp(searchStartDir, "data", "generated", "passive-tree");
            if (generatedRoot is null)
                return new PassiveTreeImportResult(PassiveTreeImportStatus.TreeNotFound,
                    $"no data/generated/passive-tree found walking up from {searchStartDir}", null);

            // Ordinal-sorted for the identical reason SeedScanner.Order sorts its own files: a
            // deterministic read order on every machine, never filesystem-enumeration-dependent.
            var files = Directory.GetFiles(generatedRoot, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            if (files.Length == 0)
                return new PassiveTreeImportResult(PassiveTreeImportStatus.TreeNotFound,
                    $"data/generated/passive-tree exists at {generatedRoot} but holds no *.json", null);

            var tuningRoot = SeedImportRunner.FindUp(searchStartDir, "data", "tuning");
            if (tuningRoot is null)
                return new PassiveTreeImportResult(PassiveTreeImportStatus.Failed,
                    $"no data/tuning found walking up from {searchStartDir}", null);

            var tuningPath = Path.Combine(tuningRoot, "passive-tree.v1.json");
            if (!File.Exists(tuningPath))
                return new PassiveTreeImportResult(PassiveTreeImportStatus.Failed,
                    $"{tuningPath} does not exist", null);

            var tuning = PassiveTreeTuningLoader.Parse(File.ReadAllText(tuningPath));

            var treeFiles = files
                .Select(f => (FileName: Path.GetFileName(f), Json: File.ReadAllText(f)))
                .ToList();

            var outcome = store.ImportTreeCatalogFiles(treeFiles, tuning);
            if (!outcome.Ok)
                return new PassiveTreeImportResult(PassiveTreeImportStatus.Failed,
                    $"{outcome.Refusals.Count} refusal(s): {string.Join("; ", outcome.Refusals.Take(5))}"
                    + (outcome.Refusals.Count > 5 ? $" (+{outcome.Refusals.Count - 5} more)" : ""),
                    outcome);

            return new PassiveTreeImportResult(PassiveTreeImportStatus.Imported, null, outcome);
        }
        catch (Exception ex)
        {
            // ImportTreeCatalogFiles is one transaction, so a throw from inside it has already rolled
            // back — this catch only turns that into a verdict, matching RunSelfHealing's own contract
            // that a broken passive-tree corpus must never take server startup down with it.
            return new PassiveTreeImportResult(PassiveTreeImportStatus.Failed, ex.Message, null);
        }
    }
}
