using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Data;
using FusionRpg.Server;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// A resolvable <c>setup_json</c> for the boot-sweep tests (B40.6).
///
/// <para><b>Why this exists.</b> The sweep has two failure kinds and only one of them marks the row:
/// an <c>ArgumentException</c> from <c>BattleEngine.Resolve</c> ("Squad is empty.") is now refused and
/// terminal, while a transient failure leaves the row unresolved to retry. A test that wants to assert
/// something <i>other</i> than a resolve failure — a content-hash verdict, or "an unresolvable wave is
/// not interactive" — must therefore hand the sweep a setup that genuinely resolves, or it is silently
/// asserting the resolve path instead of its own subject. Four tests did exactly that with
/// <c>setupJson: "{}"</c>; an empty squad can never heal, so "not refused" was the only thing they could
/// observe and the defect behind it stayed invisible.</para>
///
/// <para><b>Built from the real producers, never a hand-rolled shape.</b> The squad comes from
/// <see cref="WebMatchService.BuildSquad"/> — which already falls back to a deterministic synthetic
/// squad for an empty roster, the exact path a real first match takes — and the wave from
/// <see cref="WaveCatalog"/>. A copy of the actor shape here would drift from what production writes,
/// which is the whole class of bug this helper exists to prevent.</para>
/// </summary>
static class SweepSetupFixture
{
    /// <summary>The wave id every shipped build knows; the loader is configured at host startup.</summary>
    public const string KnownWaveId = "rift-skirmish";

    /// <summary>
    /// A setup that resolves end to end for player 1: a real wave's own enemies plus whatever squad
    /// <see cref="WebMatchService.BuildSquad"/> produces. The player must already exist (the factory's
    /// host seeds it) because a squad is built from the roster.
    /// </summary>
    /// <param name="waveId">
    /// The setup's own <see cref="BattleSetup.WaveId"/>. Defaults to a known wave; pass an unknown id
    /// to exercise the "an unresolvable wave id is not an interactive profile" path — the enemy list
    /// still comes from a real wave, because <c>WaveId</c> is metadata for the profile lookup and does
    /// not gate the resolve (which reads <see cref="BattleSetup.Wave"/> directly).
    /// </param>
    public static string Resolvable(WebMatchService matches, string waveId = KnownWaveId)
    {
        var (ok, reason, squad, _) = matches.BuildSquad(playerId: 1, squadInstanceIds: null);
        if (!ok || squad is null || squad.Count == 0)
            throw new InvalidOperationException(
                $"BuildSquad must yield a squad for the sweep fixture (got ok={ok}, reason='{reason}', " +
                $"count={squad?.Count ?? 0}). An empty roster is supposed to fall back to a synthetic " +
                "squad, so this means the fallback regressed — not that the test should weaken.");

        var setup = new BattleSetup
        {
            WaveId = waveId,
            Squad = squad,
            Wave = WaveCatalog.Get(KnownWaveId).Enemies,
        };
        return JsonSerializer.Serialize(setup);
    }

    /// <summary>Append an unresolved log row carrying a resolvable setup.</summary>
    public static (long Id, string Corr) AppendUnresolved(
        RpgStore store, WebMatchService matches, string prefix, string tag,
        string? contentStamp, string? profileId = null, string? decisionsJson = null,
        string waveId = KnownWaveId)
    {
        var corr = prefix + tag + "-" + Guid.NewGuid().ToString("N")[..8];
        var (created, entry) = store.AppendWebMatchLog(
            playerId: 1, correlationId: corr, matchKey: "match-" + corr,
            setupJson: Resolvable(matches, waveId), seed: 7,
            engineVersion: BattleRuleset.EngineVersion,
            rulesetVersion: BattleRuleset.RulesetVersion,
            rngAlgoVersion: SeededRng.RngAlgoVersion,
            environmentStamp: BattleEnvironment.Stamp,
            contentHash: contentStamp,
            profileId: profileId);
        if (!created) throw new InvalidOperationException("AppendWebMatchLog refused a fresh correlation");
        if (decisionsJson is not null) store.WriteWebMatchDecisions(entry.Id, decisionsJson);
        return (entry.Id, corr);
    }
}
