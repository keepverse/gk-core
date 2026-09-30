using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The verification-boundary guard resolves every declared path through the workspace resolver rather
/// than against gk-core, because the registry writes its paths repository-relative and nine
/// repositories own them. That fix took the guard from <b>292 findings to 0</b>, and 274 of the 292
/// were the resolution itself failing rather than a real finding.
///
/// <para>
/// It also introduced a risk nobody checked: <c>owning_base()</c> searches gk-core, then gk-forge,
/// gk-fusion, gk-web, the workspace root, gk-data's pack and gk-content, and returns the FIRST
/// repository that has the file. So a declared path absent from gk-core but present in a sibling now
/// SATISFIES the check. That is the point for 255 of the paths, and it is a silent hole for the rest.
///
/// <para>
/// The hole is this shape. A boundary declares <c>scripts/foo.py</c> meaning gk-fusion's. gk-core does
/// not have that path, so gk-fusion's copy satisfies it - correctly today. Later gk-core grows a
/// <c>scripts/foo.py</c> of its own for unrelated reasons. The local root is searched FIRST, so the
/// check now passes against gk-core's file and <b>silently stops testing gk-fusion's</b>. Nothing
/// fails, no finding is reported, and the boundary quietly stops meaning what it says.
///
/// <para>
/// So these assert the CONTRACT, in the two directions that matter, against the real
/// <c>owning_base()</c> executed by python - never a C# re-implementation of it, because a contract
/// with two implementations and no parity check is two contracts, and that is how the three resolver
/// copies drifted apart in the first place.
/// </para>
/// </summary>
[Trait("VerificationId", "guard.cross-repo-resolution")]
public sealed class CrossRepoResolutionTests
{
    private static string Core() => KeepverseRoots.Core();

    /// <summary>One declared path and where the guard's own resolver sends it.</summary>
    private sealed record Resolution(string Path, string? ResolvedTo, bool LocalHasIt);

    /// <summary>
    /// Run the guard's own resolver over every exact path the registry declares.
    /// </summary>
    /// <remarks>
    /// The probe is written to a temp file rather than passed inline because a path with spaces in it
    /// - and every one of these has one - does not survive argv quoting, and a probe that silently
    /// fails to import would report ZERO paths as a pass. It therefore also prints the count it
    /// examined, which the caller asserts is non-zero: a probe that examined nothing is not a pass.
    /// </remarks>
    private static IReadOnlyList<Resolution> ResolveEveryDeclaredPath()
    {
        var core = Core();
        var scripts = Path.Combine(core, "scripts");
        // ITS OWN DIRECTORY, NOT %TEMP% ITSELF. A python script's directory becomes sys.path[0], and
        // this machine's %TEMP% root carries a stale `inspect.py` that shadows the stdlib module -
        // so a probe written there dies with
        //     AttributeError: module 'inspect' has no attribute 'get_annotations'
        // the moment anything imports `dataclasses`. That is not hypothetical: it is what this test
        // did first, and it is the third time this session that stale-file trap has cost a run. A
        // unique subdirectory is immune to whatever else is lying in the temp root, and it is also the
        // only way the cleanup can be exact.
        var work = Path.Combine(Path.GetTempPath(),
            "fusionrpg-xrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var probe = Path.Combine(work, "resolve_declared.py");
        var guard = Path.Combine(scripts, "guard-verification-boundaries.py");
        var registry = Path.Combine(scripts, "verification-boundaries.v1.json");
        Assert.True(File.Exists(guard), "the guard this test interrogates is missing: " + guard);
        Assert.True(File.Exists(registry), "the boundary registry is missing: " + registry);

        var source = $$"""
            import importlib.util, json, pathlib, sys

            scripts = pathlib.Path(r"{{scripts}}")
            root = pathlib.Path(r"{{core}}")
            sys.path.insert(0, str(scripts))
            sys.path.insert(0, str(scripts / "lib"))
            spec = importlib.util.spec_from_file_location("gvb", scripts / "guard-verification-boundaries.py")
            gvb = importlib.util.module_from_spec(spec)
            sys.modules["gvb"] = gvb
            spec.loader.exec_module(gvb)

            doc = json.loads((scripts / "verification-boundaries.v1.json").read_text(encoding="utf-8"))

            def is_glob(s):
                return any(c in s for c in "*?[")

            declared = set()
            for b in doc.get("boundaries", []):
                for p in (b.get("paths") or []):
                    if not is_glob(str(p)):
                        declared.add(str(p).replace("\\", "/"))
            for value in (doc.get("projects") or {}).values():
                items = value if isinstance(value, list) else [value]
                for m in items:
                    if isinstance(m, str) and not is_glob(m):
                        declared.add(m.replace("\\", "/"))
                    elif isinstance(m, dict):
                        for f in ("root", "script", "tests"):
                            if m.get(f) and not is_glob(str(m[f])):
                                declared.add(str(m[f]).replace("\\", "/"))

            rows = []
            for rel in sorted(declared):
                base = gvb.owning_base(rel, root)
                rows.append({
                    "path": rel,
                    "resolvedTo": str(base) if base is not None else None,
                    "localHasIt": (root / rel).exists(),
                })
            print(json.dumps({"examined": len(rows), "rows": rows}))
            """;
        File.WriteAllText(probe, source);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{probe}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var (exit, stdout, stderr) = ExternalProcess.Run(psi, 120_000, "resolution probe timed out");
            // The probe's own SOURCE travels with the failure. A traceback truncated by the test
            // runner names a line number nobody can look at, and a probe that failed to generate the
            // way it was meant to is the common case - so the source is the first thing wanted.
            Assert.True(exit == 0,
                $"the resolution probe failed (exit {exit}): {stderr}\n--- probe source ---\n{source}");

            using var doc = JsonDocument.Parse(stdout);
            var examined = doc.RootElement.GetProperty("examined").GetInt32();
            // A probe that examined NOTHING is not a pass - it is the absence-as-success shape.
            Assert.True(examined > 0, "the probe examined no declared path, so it proved nothing");
            return doc.RootElement.GetProperty("rows").EnumerateArray()
                .Select(r => new Resolution(
                    r.GetProperty("path").GetString()!,
                    r.GetProperty("resolvedTo").GetString(),
                    r.GetProperty("localHasIt").GetBoolean()))
                .ToList();
        }
        finally
        {
            // A failed temp delete is a FAILURE, not a swallowed cleanup: this repository's testing
            // standard records a 65.5 GB leak that came from exactly this shape.
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"failed to remove the resolution probe directory at {work}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// LOCAL-FIRST IS THE WHOLE CONTRACT. A file this repository owns is answered by this repository,
    /// never by a sibling's copy, even where a sibling has one too.
    /// </summary>
    /// <remarks>
    /// This is the invariant that stops the silent hole. Eight declared paths exist in more than one
    /// repository - <c>AGENTS.md</c> and <c>README.md</c> in all nine, <c>ci.yml</c>,
    /// <c>Directory.Build.props</c> and <c>FusionRpg.slnx</c> in three, and
    /// <c>scripts/lib/keepverse_roots.py</c> in two - and every one of them is a case where a sibling
    /// carries a near-identical file. Resolving any of them to the sibling would satisfy the check
    /// while testing nothing.
    /// </remarks>
    [Fact]
    [Trait("VerificationId", "guard.cross-repo-resolution")]
    public void A_file_this_repository_owns_is_answered_by_this_repository()
    {
        var rows = ResolveEveryDeclaredPath();
        var stolen = rows
            .Where(r => r.LocalHasIt && !string.Equals(r.ResolvedTo, Core(), StringComparison.Ordinal))
            .ToList();

        Assert.True(
            stolen.Count == 0,
            "a declared path this repository owns was resolved to a sibling's copy, so the check is "
            + "passing against a file that is not the one it names: "
            + string.Join("; ", stolen.Take(8).Select(r => $"{r.Path} -> {r.ResolvedTo}")));
    }

    /// <summary>
    /// EVERY DECLARED PATH RESOLVES SOMEWHERE. This pins the 292-to-0 result as a structural property
    /// of the registry rather than a one-time observation: delete or move a declared file and the
    /// guard goes red here first, naming the path, instead of silently reappearing in a count.
    /// </summary>
    [Fact]
    [Trait("VerificationId", "guard.cross-repo-resolution")]
    public void Every_declared_path_resolves_in_some_repository()
    {
        var rows = ResolveEveryDeclaredPath();
        var unresolved = rows.Where(r => r.ResolvedTo is null).Select(r => r.Path).ToList();

        Assert.True(
            unresolved.Count == 0,
            $"{unresolved.Count} declared path(s) resolve in no repository, which is what the guard's "
            + $"292 findings were before the resolution was routed through the owner: "
            + string.Join("; ", unresolved.Take(8)));
    }

    /// <summary>
    /// THE CROSS-REPOSITORY PATH IS LIVE, so the two tests above are not passing vacuously.
    /// </summary>
    /// <remarks>
    /// If every path resolved to gk-core, then "local-first holds" and "everything resolves" would
    /// both be true of a mechanism that never reaches a sibling - and this is exactly the shape of a
    /// green test over a dead code path. So assert that a substantial number of declared paths are
    /// answered by a repository OTHER than this one. 255 of the 551 were, before the fix turned them
    /// from findings into resolutions.
    /// </remarks>
    [Fact]
    [Trait("VerificationId", "guard.cross-repo-resolution")]
    public void The_cross_repository_path_is_actually_exercised()
    {
        var rows = ResolveEveryDeclaredPath();
        var crossRepo = rows
            .Where(r => !r.LocalHasIt && r.ResolvedTo is not null)
            .Select(r => r.Path)
            .ToList();

        Assert.True(
            crossRepo.Count > 0,
            "no declared path was answered by a sibling repository, so the cross-repository resolution "
            + "is dead code and the two tests beside it prove nothing about it");
        Assert.True(
            crossRepo.Count < rows.Count,
            $"every one of the {rows.Count} declared paths was answered by a sibling, which means this "
            + "repository owns none of them - the registry and the tree have diverged");
    }
}
