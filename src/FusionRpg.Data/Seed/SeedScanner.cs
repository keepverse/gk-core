namespace FusionRpg.Data.Seed;

/// <summary>
/// Which files an import sweeps.
///
/// <para>Moved here from <c>gk-forge/tools/AtomImporter</c> (E46, player-content-boot) so the server's
/// self-healing startup import can sweep the same folders the CLI always has — a server referencing
/// a <c>tools/</c> project would be backwards, and a second copy of this class is exactly the kind of
/// drift that let seedsmith's affix folder silently stop being swept once already (see the doc comment
/// on <see cref="OwnedFolders"/>). <c>gk-forge/tools/AtomImporter/Program.cs</c> now calls this same class.</para>
///
/// <para>The one decision in this tool that can be silently wrong. <c>gk-data/packs/fusion/data/seed/</c> also holds the
/// item seed corpus — a different format read by <c>gk-forge/tools/ItemSeedValidator</c> — so a recursive
/// sweep of the seed root refuses all ~125 of its files and reports the import broken. It is in a
/// class rather than in <c>Program</c>'s top-level statements so a test can hold it to that.</para>
/// </summary>
public static class SeedScanner
{
    /// <summary>The folders the atom importer owns. Nothing else under the seed root is its business.
    /// <c>effects/affixes</c> (E32, spec-affix-import-path.md §3.1 break 3) is where seedsmith's own
    /// affix stage writes (`gk-forge/tools/seedsmith/seedsmith/adapters/effects/affix/generate_affixes.py`'s
    /// own `OUTPUT_DIR`) — the two halves of this path must name the same folder, or the pipeline can
    /// silently write to a folder nothing sweeps, exactly as it did before this fix. A test
    /// (`SeedScannerTests.cs`) reads that Python file's own `OUTPUT_DIR` line and asserts it names
    /// this exact entry, mechanically, so the two-halves-disagree failure cannot recur unnoticed.
    /// <c>power</c> (E44 criterion 0, spec-power-sweep.md §4.1) is where a `power-coefficient` seed
    /// file goes — `gk-data/packs/fusion/data/seed/power/coefficients.v1.json` is the canonical path the spec names, but
    /// the folder is swept whole, the same as every other owned folder here.
    ///
    /// <para><b>D4.16</b> (spec-domain-catalog.md §1) added the seven `dungeon` corpus folders the
    /// repository owns — `_registry`/`_plan`/`_containers` (the hubs') are deliberately NOT listed,
    /// `Order` already skips `_`-prefixed entries on its own. These files use the dungeon-specific
    /// seed schemas, not the atom importer envelope, so <see cref="AtomRoots"/> deliberately leaves
    /// them to their dedicated loaders while <see cref="Roots"/> remains the complete ownership map.
    /// Keeping the distinction here prevents a player's atom import from rejecting an otherwise valid
    /// install merely because another subsystem's content is present.</para>
    ///
    /// <para><b>`creatures/species-effects`</b> (T5.3, spec-species-effects.md §6) added 2026-09-06,
    /// the exact same "two halves must name the same folder" discipline `effects/affixes` above
    /// already established — `gk-forge/tools/seedsmith/run_t53_claude_propose.py`'s own `OUTPUT_DIR` writes
    /// here. Unlike the dungeon folders above, this one is NOT a no-op today: a real pilot batch
    /// (`plant/pilot-batch.json`, `zombie/pilot-batch.json`) is already committed under it.</para>
    /// </summary>
    public static readonly string[] AtomFolders =
        {
            "atoms", "containers", "curves", "rarity", "elements", "channel-policy", "channel-pools",
            "effects/affixes", "power", "creatures/species-effects",
        };

    public static readonly string[] DungeonFolders =
        {
            "dungeon/domains", "dungeon/rooms", "dungeon/layouts", "dungeon/events", "dungeon/quests",
            "dungeon/encounters", "dungeon/supplies",
        };

    /// <summary>Every seed subtree owned by a repository content program. This is an ownership
    /// inventory, not necessarily the input set for one parser.</summary>
    public static readonly string[] OwnedFolders = AtomFolders.Concat(DungeonFolders).ToArray();

    /// <summary>
    /// The folders to sweep. A root the caller named explicitly is swept whole — that is the escape
    /// hatch for a migration folder — and the default root expands to the four owned folders that
    /// actually exist.
    /// </summary>
    public static IReadOnlyList<string> Roots(string seedRoot, bool explicitRoot, Func<string, bool> exists)
    {
        if (explicitRoot) return new[] { seedRoot };

        return OwnedFolders
            .Select(d => Path.Combine(seedRoot, d))
            .Where(exists)
            .ToArray();
    }

    /// <summary>The default roots accepted by the atom importer. Dungeon content is owned by the
    /// dungeon loaders and has a different JSON envelope, so it must not be fed to AtomSeedFile.
    /// An explicitly named root remains the documented migration escape hatch and is swept whole.</summary>
    public static IReadOnlyList<string> AtomRoots(string seedRoot, bool explicitRoot, Func<string, bool> exists)
    {
        if (explicitRoot) return new[] { seedRoot };

        return AtomFolders
            .Select(d => Path.Combine(seedRoot, d))
            .Where(exists)
            .ToArray();
    }

    /// <summary>
    /// Ordinal-sorted, so a cross-file duplicate names the same two paths on every machine —
    /// <c>Directory.EnumerateFiles</c> order is filesystem-dependent. Files opening with <c>_</c> are
    /// notes and exemplars, matching the item seed tree's convention.
    /// </summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> paths) =>
        paths.Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
             .OrderBy(f => f, StringComparer.Ordinal)
             .ToArray();

    /// <summary>Every JSON file under the given roots, in the order an import reads them.</summary>
    public static IReadOnlyList<string> Files(IEnumerable<string> roots) =>
        Order(roots.SelectMany(r => Directory.GetFiles(r, "*.json", SearchOption.AllDirectories)));
}
