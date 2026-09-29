using System.Runtime.CompilerServices;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T11 (action-todo.md, spec-basic-attack-adoption.md): the action program's OWN parity fixtures,
/// on disk at `gk-core/tests/fixtures/action-traces/` — a sibling of, and deliberately separate from, the
/// kernel-adoption program's `PreAdoptionFixtures` (`Battle/Adoption/`). Two different programs,
/// two different fixture sets, so a re-bless in one can never silently cover the other.
///
/// Capture semantics match the precedent exactly: a missing fixture is WRITTEN and returned on
/// first run, so the capture run is green and the file lands in the tree for review. Every later
/// run compares. A deleted fixture silently re-blesses, so these files are part of the change under
/// review, same discipline as the golden hashes.
/// </summary>
static class ActionAdoptionFixtures
{
    static string Dir([CallerFilePath] string here = "")
    {
        // gk-core/tests/FusionRpg.Core.Tests/Actions/ -> gk-core/tests/fixtures/action-traces/
        var actions = Path.GetDirectoryName(here)!;
        var testsRoot = Path.GetFullPath(Path.Combine(actions, "..", ".."));
        return Path.Combine(testsRoot, "fixtures", "action-traces");
    }

    /// <summary>Compares against the stored fixture, capturing it on first run.</summary>
    public static string LoadOrCapture(string name, string actual)
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".trace.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, actual);
            return actual;
        }

        // Fixtures are LF in the blob but check out as CRLF when core.autocrlf=true;
        // the engine always emits LF, so normalize before comparing.
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }
}
