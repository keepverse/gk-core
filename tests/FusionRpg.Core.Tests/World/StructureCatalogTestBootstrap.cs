using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.World;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.Workspace;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Configures <c>StructureCatalog</c> once per assembly from the real, shipped corpus.
///
/// <para><b>Same correction as <c>DungeonHubTestBootstrap</c> (d41167f), and for the same measured
/// reason.</b> This is a <c>[ModuleInitializer]</c>, so it runs at ASSEMBLY LOAD, and it resolved
/// <see cref="KeepverseRoots.Content()"/> inline. The structure corpus lives in gk-data, so in a
/// standalone clone the initializer threw and the <c>TypeInitializationException</c> took the whole
/// assembly down:
///
///     isolated clone at b68bb74, counted by stack frame:
///       7,624  StructureCatalogTestBootstrap.Init() -> CorpusRoot()
///
/// which is the single largest remaining source of failures in a clone — more than half of
/// <c>FusionRpg.Core.Tests</c>'s 6,379.</para>
///
/// <para><b>Nothing is skipped and no exception is swallowed.</b> The corpus is still loaded from the
/// real file; the catch is narrowed to the three IO-shaped exceptions a missing directory produces, and
/// the reason is exposed on <see cref="RootResolutionRefusal"/> so the absence is reportable rather than
/// silent. A test that needs a structure still fails, from the catalog's own unconfigured error, AT THAT
/// TEST.</para>
///
/// <para><b>INERT WHERE THE CORPUS IS REACHABLE</b> — in a workspace this branch never runs, which is
/// why the workspace suite is the control rather than an argument.</para>
/// </summary>
internal static class StructureCatalogTestBootstrap
{
    /// <summary>Why the catalog was left unconfigured, or null when it was configured.</summary>
    internal static string? RootResolutionRefusal { get; private set; }

    [ModuleInitializer]
    public static void Init()
    {
        try
        {
            StructureCatalog.Configure(StructureCorpus.Load(CorpusRoot()));
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            RootResolutionRefusal =
                "CONTENT-ROOT-UNREACHABLE: StructureCatalog was NOT configured because "
                + "data/seed/structures does not resolve from this checkout. " + ex.Message;
        }
    }

    static string CorpusRoot() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");
}
