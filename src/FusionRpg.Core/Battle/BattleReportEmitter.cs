using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;

namespace FusionRpg.Core.Battle;

/// <summary>
/// Maps a BattleReport onto the lean event vocabulary (spec-match-source-core.md): board.start,
/// spawn/die analogues, match.result, mandatory board.end — no per-attack events; per-actor
/// tallies ride the board.end summary. Actor ptrs use the synthetic `web:{matchKey}:{n}` scheme
/// (load-bearing for entities uniqueness and ledger dedupe). The emitter stamps no wall-clock
/// time — envelopes leave with T empty and WebMatchService assigns strictly monotonic t at ingest.
/// </summary>
public static class BattleReportEmitter
{
    public static IReadOnlyList<EventEnvelope> Emit(BattleReport report, string matchKey)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (string.IsNullOrWhiteSpace(matchKey))
            throw new ArgumentException("matchKey is required.", nameof(matchKey));

        // Stable actor → ptr map from report order (index is the `n` in web:{matchKey}:{n}).
        var ptrByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < report.Actors.Count; i++)
            ptrByKey[report.Actors[i].Key] = $"web:{matchKey}:{i}";

        var events = new List<EventEnvelope>(report.Actors.Count + 8);

        events.Add(Envelope("board.start", matchKey, new Dictionary<string, object?>
        {
            ["levelName"] = report.WaveId,
            ["levelType"] = "web-battle",
            ["engineVersion"] = report.EngineVersion,
            ["rulesetVersion"] = report.RulesetVersion,
            ["rngAlgoVersion"] = report.RngAlgoVersion,
            ["environmentStamp"] = report.EnvironmentStamp, // owner decision 7: cross-arch guard
            ["seed"] = report.Seed.ToString() // string — ulong seeds overflow JSON numbers
        }));

        foreach (var actor in report.Actors)
        {
            events.Add(Envelope(actor.Side == "squad" ? "plant.spawn" : "zombie.spawn", matchKey,
                ActorPayload(actor, ptrByKey)));
        }

        foreach (var rec in report.Events)
        {
            // Deliberate vocabulary expansion (battle-adoption): die events + the four
            // shield.* kinds. Absorbed entries are already per-round aggregates from the
            // shield runtime, so the "no per-attack events" stance holds. Spawn stays
            // covered by the plant/zombie.spawn envelopes above.
            if (rec.Kind == BattleEventKinds.Die)
            {
                var actor = report.Actors.First(a => a.Key == rec.ActorKey);
                var payload = ActorPayload(actor, ptrByKey);
                payload["reason"] = 0;
                payload["round"] = rec.Round;
                if (rec.KillerActorKey is not null)
                {
                    if (string.IsNullOrWhiteSpace(rec.KillerActorKey))
                        throw new InvalidOperationException(
                            $"Die event '{rec.ActorKey}' carries a blank killer actor key.");
                    if (!ptrByKey.TryGetValue(rec.KillerActorKey!, out var killerPtr))
                        throw new InvalidOperationException(
                            $"Die event '{rec.ActorKey}' references unknown killer actor '{rec.KillerActorKey}'.");
                    payload["killerPtr"] = killerPtr;

                    // solid-remediation T4.3 (D9): the killer's ptr says WHICH entity killed, never
                    // WHOSE it was — which is why kill attribution was "absent outside the lawn" even
                    // though this payload already named a killer. The empire is resolved here, at the
                    // one point every mode's die event passes through, so no consumer has to re-derive
                    // it from a side token and none can derive it differently.
                    var killer = report.Actors.First(a => a.Key == rec.KillerActorKey);
                    payload["creditEmpire"] =
                        KillAttribution.EmpireOfBattleSide(killer.Side).Value;
                }
                events.Add(Envelope(actor.Side == "squad" ? "plant.die" : "zombie.die", matchKey, payload));
                continue;
            }

            if (rec.Kind.StartsWith("shield.", StringComparison.Ordinal))
            {
                var actor = report.Actors.First(a => a.Key == rec.ActorKey);
                var payload = ActorPayload(actor, ptrByKey);
                payload["round"] = rec.Round;
                payload["shieldId"] = rec.ShieldId ?? "";
                payload["element"] = rec.Element ?? "none";
                payload["amount"] = rec.Amount;
                events.Add(Envelope(rec.Kind, matchKey, payload));
            }
        }

        events.Add(Envelope("match.result", matchKey, new Dictionary<string, object?>
        {
            ["result"] = report.Outcome switch
            {
                BattleOutcome.Victory => "victory",
                BattleOutcome.Defeat => "defeat",
                _ => "stalemate"
            }
        }));

        events.Add(Envelope("board.end", matchKey, new Dictionary<string, object?>
        {
            ["levelName"] = report.WaveId,
            ["summary"] = new Dictionary<string, object?>
            {
                ["rounds"] = report.Rounds,
                ["outcome"] = report.Outcome.ToString(),
                ["soulLootMilli"] = report.SoulLootMilli,
                ["actors"] = report.Actors.Select(a =>
                {
                    var tally = ActorPayload(a, ptrByKey);
                    tally["key"] = a.Key;
                    tally["side"] = a.Side;
                    tally["hpRemaining"] = a.HpRemaining;
                    tally["damageDealt"] = a.DamageDealt;
                    tally["kills"] = a.Kills;
                    tally["survived"] = a.Survived;
                    tally["retreated"] = a.Retreated;
                    tally["xpMilli"] = a.XpMilli;
                    tally["shieldAbsorbed"] = a.ShieldAbsorbed;
                    return tally;
                }).ToList()
            }
        }));

        return events;
    }

    static Dictionary<string, object?> ActorPayload(BattleActorResult actor, Dictionary<string, string> ptrByKey) =>
        new()
        {
            ["ptr"] = ptrByKey[actor.Key],
            ["type"] = actor.TypeId,
            ["typeName"] = actor.SpeciesId,
            ["source"] = RpgConstants.SourceWeb
        };

    static EventEnvelope Envelope(string kind, string matchKey, Dictionary<string, object?> payload) =>
        new()
        {
            T = "",
            Game = RpgConstants.GameIdWebRpg,
            Kind = kind,
            MatchKey = matchKey,
            Payload = payload
        };
}
