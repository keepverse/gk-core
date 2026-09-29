using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §9.1 -- "SHA-256 over the artifact's canonical form with the provenance fields
/// blanked -- the exact idiom <c>BattleGoldenTests.Hash</c> uses", which blanks <c>EnvironmentStamp</c>
/// and <c>ContentHash</c> so a portable hash stays portable across CI and machines.
///
/// <para>This harness's own provenance is simpler: <see cref="HarnessRun"/> carries none of it. The
/// per-mille integer grid plus the roster ids ARE the whole hash input, by construction -- there is no
/// <c>at</c>/<c>environmentStamp</c>/<c>wallClockMs</c> field to blank because <see cref="HarnessRun"/>
/// was designed with none. A caller that wraps a <see cref="HarnessRun"/> in a larger artifact (F2's own
/// job) is responsible for hashing the <see cref="HarnessRun"/> sub-object alone, exactly as this class
/// does, rather than the artifact's outer envelope.</para>
/// </summary>
public static class DeterminismHash
{
    static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    public static string CanonicalJson(HarnessRun run) => JsonSerializer.Serialize(run, CanonicalOptions);

    public static string Hash(HarnessRun run) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(run))));
}
