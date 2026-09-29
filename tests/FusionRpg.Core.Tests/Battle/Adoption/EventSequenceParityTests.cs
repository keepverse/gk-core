using System.Runtime.CompilerServices;
using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// B14's fourth parity-ladder layer (spec-kernel-adoption.md's ladder: stream → phase-order →
/// per-round-state → event-sequence → hash). Stream/phase-order/per-round-state are already
/// covered by <see cref="PreAdoptionTraceTests"/>'s <c>BattleTrace.Digest</c> fixtures — `Phase`,
/// `Draw`, and `State` are all recorded into that same digest in call order. `report.Events` is a
/// SEPARATE structure `BattleTrace` never touches, so nothing localized a drift there before the
/// golden hash — the coarsest, least diagnosable layer the spec explicitly wants the ladder to
/// have something finer than.
///
/// Captured post-B14, not pre-adoption: B13/B14 already proved byte-identity via the golden hash
/// (which serializes `Events` into what it hashes), so this fixture's first-run capture IS the
/// verified-identical sequence. Its value from here on is diagnostic reach for B16/B17 — a future
/// change that moves `report.Events` will fail HERE, naming which event changed, before the hash
/// even runs.
/// </summary>
public class EventSequenceParityTests
{
    static string Dir([CallerFilePath] string here = "")
    {
        var adoption = Path.GetDirectoryName(here)!;
        var testsRoot = Path.GetFullPath(Path.Combine(adoption, "..", "..", ".."));
        return Path.Combine(testsRoot, "fixtures", "battle-traces");
    }

    static string Serialize(IReadOnlyList<BattleEventRec> events)
    {
        var lines = new string[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            lines[i] = $"{e.Round} {e.Kind} {e.ActorKey} {e.TypeId} {e.Side} {e.Amount} {e.Element} {e.ShieldId}";
        }
        return string.Join("\n", lines);
    }

    /// <summary>Same first-run-captures semantics as <c>PreAdoptionFixtures</c> — a deleted
    /// fixture silently re-blesses, so these files are part of the reviewed diff.</summary>
    static string LoadOrCapture(string name, string actual)
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".events.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, actual);
            return actual;
        }

        // Fixtures are LF in the blob but check out as CRLF when core.autocrlf=true;
        // Serialize joins with LF, so normalize before comparing.
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    [Theory]
    [InlineData("stomp", 1001)]
    [InlineData("close", 2002)]
    [InlineData("wipe", 3003)]
    public void Event_sequence_matches_element_wise(string name, ulong seed)
    {
        var setup = name switch
        {
            "stomp" => BattleGoldenTests.StompSetup(),
            "close" => BattleGoldenTests.CloseSetup(),
            _ => BattleGoldenTests.WipeSetup()
        };

        var report = BattleEngine.Resolve(setup, seed);
        var actual = Serialize(report.Events);

        Assert.NotEmpty(report.Events);
        Assert.Equal(LoadOrCapture(name, actual), actual);
    }

    [Fact]
    public void Event_sequence_is_deterministic_across_runs()
    {
        string Once()
        {
            var report = BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002);
            return Serialize(report.Events);
        }

        Assert.Equal(Once(), Once());
    }
}
