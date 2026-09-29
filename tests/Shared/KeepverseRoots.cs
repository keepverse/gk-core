// The three roots a test resolves a path from: content, core, workspace.
// Resolver contract: tasks/keepverse-split-plan.md "Resolver contract". Same rules as
// gk-forge/tools/seedsmith/seedsmith/workspace_roots.py and scripts/lib/KeepverseRoots.ps1: the KEEPVERSE_*_ROOT env
// override wins; otherwise walk up from the test binary to the first legacy repo (FusionRpg.slnx next to
// gk-data/packs/fusion/data/seed/) or Keepverse workspace (gk-core/ next to gk-data/). Nothing found throws.
//
// Compiled into every *.Tests project by Directory.Build.props (linked, not copied).

#nullable enable

using System;
using System.IO;

namespace FusionRpg.TestSupport;

internal static class KeepverseLayout
{
    // Structural: the pack the current corpus ships as (decision D7), not a balance number.
    internal const string DefaultPack = "fusion";

    internal static (bool Legacy, string Root) Find(string? start = null)
    {
        var d = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "FusionRpg.slnx")) &&
                Directory.Exists(Path.Combine(d.FullName, "data", "seed")))
                return (true, d.FullName);
            if (Directory.Exists(Path.Combine(d.FullName, "gk-core")) &&
                Directory.Exists(Path.Combine(d.FullName, "gk-data")))
                return (false, d.FullName);
            d = d.Parent;
        }
        throw new DirectoryNotFoundException($"No legacy repo or Keepverse workspace above '{start ?? AppContext.BaseDirectory}'.");
    }

    internal static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

/// <summary>Root that repo-relative content paths (gk-data/packs/fusion/data/seed, gk-data/packs/fusion/data/generated, content) resolve from.</summary>
internal static class ContentRoot
{
    internal static string Path => Resolve();

    internal static string Resolve(string? start = null)
    {
        if (KeepverseLayout.Env("KEEPVERSE_CONTENT_ROOT") is { } env) return env;
        var (legacy, root) = KeepverseLayout.Find(start);
        if (legacy) return root;
        var pack = System.IO.Path.Combine(root, "gk-data", "packs", KeepverseLayout.Env("KEEPVERSE_PACK") ?? KeepverseLayout.DefaultPack);
        if (!Directory.Exists(pack)) throw new DirectoryNotFoundException($"Content pack '{pack}' does not exist.");
        return pack;
    }
}

/// <summary>Root of the engine repo: src/, tests/, gk-core/data/tuning/.</summary>
internal static class CoreRoot
{
    internal static string Path => Resolve();

    internal static string Resolve(string? start = null)
    {
        if (KeepverseLayout.Env("KEEPVERSE_CORE_ROOT") is { } env) return env;
        var (legacy, root) = KeepverseLayout.Find(start);
        return legacy ? root : System.IO.Path.Combine(root, "gk-core");
    }
}

/// <summary>Root holding docs/ and tasks/.</summary>
internal static class WorkspaceRoot
{
    internal static string Path => Resolve();

    internal static string Resolve(string? start = null) =>
        KeepverseLayout.Env("KEEPVERSE_WORKSPACE_ROOT") ?? KeepverseLayout.Find(start).Root;
}
