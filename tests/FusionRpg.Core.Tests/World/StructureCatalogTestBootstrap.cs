using System.Runtime.CompilerServices;
using FusionRpg.Core.World;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// base-defense-todo.md 25.4: once `StructureCatalog`'s own C# `Seed` literal is deleted, EVERY test
/// in this assembly that reaches `StructureCatalog.All`/`.Get`/`.IsKnown` — directly or transitively,
/// through `BuildResolver`/`DistrictAssaultResolver`/`LoamProduction`/etc. — needs a real, loaded
/// corpus first, exactly like the real server now gets from `Program.cs`.
///
/// <para><b>Deliberately its OWN, separate module initializer — not folded into
/// `ContractTuningTestBootstrap.cs`.</b> That file's own doc comment states a real, load-bearing
/// policy: "construct one inline; no fixture files ... kept as literal C# objects here rather than a
/// file read, so tests exercise construction, not file I/O" (tunables-ssot.md §7.2). `StructureCorpus`
/// is a committed JSON tree, not a small tuning struct — mirroring the dungeon registry's own
/// established precedent (`DungeonRegistryHub` is likewise NOT configured in that bootstrap; it is
/// real file I/O, kept in its own place). Splitting it out keeps that file's own stated charter true
/// rather than quietly breaking it.</para>
/// </summary>
internal static class StructureCatalogTestBootstrap
{
    [ModuleInitializer]
    public static void Init() => StructureCatalog.Configure(StructureCorpus.Load(CorpusRoot()));

    static string CorpusRoot() => Path.Combine(ContentRoot.Path, "data", "seed", "structures");
}
