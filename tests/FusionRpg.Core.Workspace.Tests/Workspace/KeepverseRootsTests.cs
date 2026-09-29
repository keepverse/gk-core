using System;
using System.IO;
using FusionRpg.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.Workspace;

/// <summary>Resolver contract (tasks/keepverse-split-plan.md): legacy layout, Keepverse workspace layout.</summary>
[Trait("VerificationId", "core.keepverse-roots")]
public sealed class KeepverseRootsTests
{
    [Fact]
    public void Legacy_layout_resolves_every_root_to_the_repo()
    {
        var content = ContentRoot.Resolve();
        Assert.True(Directory.Exists(Path.Combine(content, "data", "seed")));
        Assert.Equal(content, CoreRoot.Resolve());
        Assert.Equal(content, WorkspaceRoot.Resolve());
    }

    [Fact]
    public void Workspace_layout_resolves_pack_core_and_workspace()
    {
        // Disk is the thing under test here (directory discovery); a failed delete fails the test.
        var ws = Directory.CreateTempSubdirectory("kv-roots-").FullName;
        try
        {
            var start = Directory.CreateDirectory(Path.Combine(ws, "gk-core", "tests")).FullName;
            var pack = Directory.CreateDirectory(Path.Combine(ws, "gk-data", "packs", "fusion", "data", "seed")).Parent!.Parent!.FullName;

            Assert.Equal(Path.Combine(ws, "gk-core"), CoreRoot.Resolve(start));
            Assert.Equal(ws, WorkspaceRoot.Resolve(start));
            Assert.Equal(pack, ContentRoot.Resolve(start));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void No_layout_above_the_start_throws()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<DirectoryNotFoundException>(() => CoreRoot.Resolve(root));
    }
}
