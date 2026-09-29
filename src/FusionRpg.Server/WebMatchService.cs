using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// Web-mode match producer (spec-match-source-core.md §WebMatchService): replay check →
/// log-before-ingest → pure BattleEngine resolve → DEDICATED single-transaction ingest
/// (explicit player, never the shared channel) → hub notifies. A crash between log and ingest
/// is healed by the boot sweep — resolution is deterministic from the logged setup + seed.
/// </summary>
public sealed class WebMatchService
{

    /// <summary>
    /// B21 — whether this logged match ran under a profile that expects a live human.
    ///
    /// <para>party-dungeon D2.16: a row that carries its own <see cref="WebMatchLogEntry.ProfileId"/>
    /// (D2.15) answers directly from it via <see cref="BattleModeProfileCatalog.Resolve"/> — the SSOT
    /// for "which profile is this id", the same lookup <c>DelveBattle.Run</c> pins against. This is
    /// the path a delve row actually needs: its own <c>WaveId</c> is an encounter-anchor id
    /// <see cref="WaveCatalog"/> has never heard of, so the wave-lookup fallback below can never see
    /// it as interactive on its own. An unrecognised id (a hand-edited or foreign row) fails toward
    /// refusing rather than toward silently auto-resolving — the same loud-over-silent stance
    /// <see cref="BattleModeProfileCatalog.Resolve"/> itself takes for an unknown id, just caught here
    /// so one bad row cannot abort the whole sweep.</para>
    ///
    /// <para>Every other row (every match logged today — no production caller stamps <c>profile_id</c>
    /// yet) falls through to the ORIGINAL check: the profile is content's choice, read from the wave
    /// the setup names, exactly as `WaveDef.Profile`'s own doc requires (a field on `BattleSetup`
    /// would move all four expedition hashes).</para>
    ///
    /// <para>A row whose setup will not parse is NOT treated as interactive — it has its own refusal
    /// further down, and guessing here would refuse it for the wrong stated reason.</para>
    /// </summary>
    static bool IsInteractive(WebMatchLogEntry entry)
    {
        if (entry.ProfileId is { } pid)
        {
            try { return BattleModeProfileCatalog.Resolve(pid).RequiresLiveInput; }
            catch (ArgumentException) { return true; } // unrecognised id: refuse, never silently auto-resolve
        }

        BattleSetup? setup;
        try { setup = JsonSerializer.Deserialize<BattleSetup>(entry.SetupJson); }
        catch (JsonException) { return false; }

        return setup is not null && ProfileForWave(setup.WaveId)?.RequiresLiveInput == true;
    }

    /// <summary>
    /// B36 — the wave's own mode profile, the last link in "content chooses the profile"
    /// (battle-timeline-map.md decision 4). <c>WaveDef.Profile</c> is <c>null</c> for every shipped
    /// wave, so this resolves to <c>classic-round</c> and every battle is byte-identical today; the
    /// wiring exists so that setting a profile on a wave actually reaches the engine, which it did
    /// not before — <c>BattleEngine.Resolve</c> had no caller passing one at all.
    ///
    /// <para>An unknown wave id keeps the resolver's own loud behaviour rather than being defaulted
    /// here: "content did not choose" and "content chose wrong" are different failures and only the
    /// first has a default.</para>
    /// </summary>
    static FusionRpg.Core.Battle.Timeline.BattleModeProfile? ProfileForWave(string? waveId) =>
        FusionRpg.Core.Battle.WaveCatalog.IsKnown(waveId)
            ? FusionRpg.Core.Battle.WaveCatalog.ProfileFor(waveId!)
            : null;

    readonly RpgStore _store;
    readonly IHubContext<RpgHub> _hub;

    /// <summary>
    /// combat-ai `replay-identity` (CAI2.2, spec-replay-identity.md §2/§5): the version-addressed profile
    /// set source this service stamps every fresh row with and resolves every replay against.
    /// <para>
    /// Optional and last, so every existing construction site compiles unchanged — and null is handled
    /// honestly rather than defaulted: a fresh row then carries NO stamp (the legacy shape, which reads
    /// back as "this match predates combat-ai profiles"), and a STAMPED row is REFUSED
    /// <c>profile.unavailable:v{n}</c>, because a host with no source can supply no version and
    /// resolving it on <c>Current</c> would be the drift. Production wires the source in
    /// <c>Program.cs</c>; a hand-built test host that omits it gets exactly the pre-combat-ai behaviour.
    /// </para>
    /// </summary>
    readonly ICombatAiProfileSource? _combatAiProfiles;

    public WebMatchService(RpgStore store, IHubContext<RpgHub> hub, ICombatAiProfileSource? combatAiProfiles = null)
    {
        _store = store;
        _hub = hub;
        _combatAiProfiles = combatAiProfiles;
    }

    /// <param name="TurnOrder">
    /// `battle-tempo` `forecast-rail` FR3 (spec-forecast-rail.md §2, §6): the acting order this
    /// battle actually recorded, projected from the trace FR1 built — a RECORD of what happened, not
    /// a forecast (an expedition is resolved before the player sees it). Empty when no trace was
    /// built for this resolve (never null — "declared, not deferred", `game-gui-map.md`'s own
    /// contract discipline every other DTO in this repo already follows).
    /// </param>
    public sealed record WebMatchOutcome(
        bool Replayed, string MatchKey, long? RunId, BattleReport Report,
        IReadOnlyList<TurnOrderEntry> TurnOrder);

    public async Task<(bool Ok, string Reason, WebMatchOutcome? Outcome)> RunWebMatchAsync(
        long playerId, string correlationId, string waveId, IReadOnlyList<string>? squadInstanceIds)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return (false, "correlation.missing", null);
        if (correlationId.Trim().Length > 64) return (false, "correlation.toolong", null);
        if (!WaveCatalog.IsKnown(waveId)) return (false, "wave.unknown", null);
        var corr = correlationId.Trim();

        // 1. Server-authoritative setup: squad snapshots from the roster (or a SIM synthetic
        //    squad when the roster is empty), wave from the code-authored catalog.
        var (squadOk, squadReason, squad, pickedIds) = BuildSquad(playerId, squadInstanceIds);
        if (!squadOk) return (false, squadReason, null);
        var setup = new BattleSetup
        {
            WaveId = waveId,
            Squad = squad!,
            Wave = WaveCatalog.Get(waveId).Enemies,
            // W7 (battle-derived-wire T4): the player's real active commander auras. `ActiveAuras`
            // lives on the battle-level `BattleSetup`, which `BuildSquad` returns only the squad half
            // of, so the producer sits here where the setup is assembled.
            ActiveAuras = ActiveAurasFor(playerId),
        };
        var seed = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
        var matchKey = "web-" + Guid.NewGuid().ToString("N");

        // 2. Durable idempotency anchor BEFORE any ingest. The atomic append IS the replay gate:
        //    a pre-check would be a separate lock acquisition, and two concurrent requests with
        //    the same correlation could both pass it — the loser would ingest a duplicate battle
        //    (2026-08-21 review, Critical 1). Created=false means another request (now or ever)
        //    holds this correlation; validate against ITS stored row and replay it.
        var (created, entry) = _store.AppendWebMatchLog(playerId, corr, matchKey,
            JsonSerializer.Serialize(setup), seed,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion,
            BattleEnvironment.Stamp, _store.ComputeContentHash().ToCompact(),
            // combat-ai `replay-identity` §5, stamping site 1 of 3: the profile set this match resolves
            // under, in the SAME call that writes `rulesetVersion` — a profiled match cannot forget to
            // stamp. Provenance only; the report carries no new field and no golden moves.
            combatAiProfile: CombatAiProfilePin.StampFor(_combatAiProfiles));
        if (!created)
        {
            var storedSetup = JsonSerializer.Deserialize<BattleSetup>(entry.SetupJson);
            if (storedSetup == null || storedSetup.WaveId != waveId)
                return (false, "correlation.mismatch", null);
            // The pin (spec §2, five steps). Refused BEFORE any resolve, and the refusal is the
            // returned reason — a player-visible named failure, not a silent re-resolve on whatever has
            // been published since. A row with no stamp falls straight through to today's default.
            var pin = CombatAiProfilePin.Resolve(_combatAiProfiles, entry.CombatAiProfile);
            if (pin.IsRefused)
            {
                Console.Error.WriteLine($"[web-match] refused replay {entry.MatchKey}: {pin.Refusal} — {pin.Detail}");
                return (false, pin.Refusal!, null);
            }
            // FR1: also a player-facing replay -- opts in, matching the fresh-resolve paths below.
            var replayTrace = new BattleTrace();
            var (replayContainerResolver, replayContainerDefs, replayRunnerBindings, replayRunnerCoverage) = ActionContainerEffectResolverFactory.Build(_store);
            // spec-equip-runtime.md Battle-half amendment (2026-09-07): a specimen's equipped
            // stat.modify atoms reach this replay the same way a held action's container does —
            // compiled once here (Data layer), registered into the SAME EffectHost.Bag.Catalog
            // alongside the container defs, granted per-actor inside BattleRunState.BindEquip.
            var replaySpecimenIds = storedSetup.Squad.Select(a => a.SpecimenId)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToList();
            var (replayEquipDefs, replayEquipEffectIdsFor) = ActionContainerEffectResolverFactory.BuildEquip(_store, replaySpecimenIds);
            var storedReport = ApplyZombossReveal(
                BattleEngine.Resolve(storedSetup, entry.Seed, replayTrace, profile: ProfileForWave(storedSetup.WaveId), actionCatalog: _store.BuildActionCatalog(RungPolicy.Table),
                    // T74/A33: the replay resolves the SAME way the fresh fight did, so it must price
                    // at the holder's effective rung too -- otherwise a replay of a match is not a
                    // replay of that match. Read through the one key->owner mapping.
                    unlockStateFor: _store.UnlockStateFor(storedSetup),
                    unlockTuning: UnlockTuningPolicy.Tuning,
                    containerResolver: replayContainerResolver,
                    onEffectHostReady: host => ActionContainerEffectResolverFactory.RegisterInto(
                        host, replayContainerDefs.Concat(replayEquipDefs).ToList()),
                    runnerBindings: replayRunnerBindings,
                    containersWithRunnerCoverage: replayRunnerCoverage,
                    equipEffectIdsFor: replayEquipEffectIdsFor,
                    board: NormalBattleBoard.Build(storedSetup.Squad.Select(a => a.Key).ToList(), storedSetup.Wave.Select(a => a.Key).ToList(), entry.Seed)),
                playerId, storedSetup);
            // FR3: BattleTrace is a class -- `replayTrace` reflects the resolve that just ran, no
            // second return path needed.
            var replayTurnOrder = TurnOrderRecord.FromTrace(replayTrace, storedSetup);
            return (true, "replay", new WebMatchOutcome(true, entry.MatchKey, entry.RunId, storedReport, replayTurnOrder));
        }

        // 3–4. Resolve + dedicated ingest; 5. notify.
        // FR1: player-facing -- opts in.
        var freshTrace = new BattleTrace();
        var (report, notify) = ResolveAndIngest(playerId, matchKey, setup, seed, freshTrace);
        // 4b. The squad lived it: contracted members gain or lose loyalty for the result. Replays
        //     return above, so a retry never credits a second time. Deliberately OUTSIDE the
        //     log-before-ingest envelope: a crash between ingest and here loses ±15 loyalty and the
        //     boot sweep will not replace it. That is the accepted trade — widening the exactly-once
        //     envelope for a loyalty point is not worth the coupling.
        _store.ApplyContractResults(playerId, pickedIds, report.Outcome == BattleOutcome.Victory);
        await BroadcastAsync(playerId, notify).ConfigureAwait(false);

        var linked = _store.TryGetWebMatchLog(playerId, corr);
        var turnOrder = TurnOrderRecord.FromTrace(freshTrace, setup);
        return (true, "ok", new WebMatchOutcome(false, matchKey, linked?.RunId, report, turnOrder));
    }

    /// <summary>
    /// Runs a pre-planned battle (expedition collect): the caller supplies the sealed setup +
    /// seed and a deterministic correlation, so retries replay instead of re-fighting. Same
    /// log-before-ingest discipline as ad-hoc matches.
    /// </summary>
    public async Task<(bool Ok, string Reason, WebMatchOutcome? Outcome)> RunPlannedMatchAsync(
        long playerId, string correlationId, string matchKey, BattleSetup setup, ulong seed)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return (false, "correlation.missing", null);
        var corr = correlationId.Trim();

        // The atomic append doubles as the replay gate (no check-then-act window; see
        // RunWebMatchAsync). On replay the STORED setup is authoritative by design: a collect
        // retry may see drifted roster stats, and the sealed snapshot must win — so only the
        // cross-wiring sentinels (seed, matchKey) are compared, never the setup body.
        var (created, entry) = _store.AppendWebMatchLog(playerId, corr, matchKey,
            JsonSerializer.Serialize(setup), seed,
            BattleRuleset.EngineVersion, BattleRuleset.RulesetVersion, SeededRng.RngAlgoVersion,
            BattleEnvironment.Stamp, _store.ComputeContentHash().ToCompact(),
            // combat-ai `replay-identity` §5, stamping site 2 of 3: the expedition-collect path — the
            // one a player is most likely to retry, and therefore the one whose drift would be most
            // visible. Same call, same stamp, same reason.
            combatAiProfile: CombatAiProfilePin.StampFor(_combatAiProfiles));
        if (!created)
        {
            var storedSetup = JsonSerializer.Deserialize<BattleSetup>(entry.SetupJson);
            if (storedSetup == null || entry.Seed != seed || entry.MatchKey != matchKey)
                return (false, "correlation.mismatch", null);
            // The pin, exactly as in RunWebMatchAsync: one procedure (`CombatAiProfilePin`), two call
            // sites. A collect retry that cannot resolve its own profile is refused by name.
            var pin = CombatAiProfilePin.Resolve(_combatAiProfiles, entry.CombatAiProfile);
            if (pin.IsRefused)
            {
                Console.Error.WriteLine($"[web-match] refused replay {entry.MatchKey}: {pin.Refusal} — {pin.Detail}");
                return (false, pin.Refusal!, null);
            }
            // FR1: also a player-facing replay -- opts in, matching the fresh-resolve paths below.
            var replayTrace = new BattleTrace();
            var (replayContainerResolver, replayContainerDefs, replayRunnerBindings, replayRunnerCoverage) = ActionContainerEffectResolverFactory.Build(_store);
            // spec-equip-runtime.md Battle-half amendment (2026-09-07): a specimen's equipped
            // stat.modify atoms reach this replay the same way a held action's container does —
            // compiled once here (Data layer), registered into the SAME EffectHost.Bag.Catalog
            // alongside the container defs, granted per-actor inside BattleRunState.BindEquip.
            var replaySpecimenIds = storedSetup.Squad.Select(a => a.SpecimenId)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToList();
            var (replayEquipDefs, replayEquipEffectIdsFor) = ActionContainerEffectResolverFactory.BuildEquip(_store, replaySpecimenIds);
            var storedReport = ApplyZombossReveal(
                BattleEngine.Resolve(storedSetup, entry.Seed, replayTrace, profile: ProfileForWave(storedSetup.WaveId), actionCatalog: _store.BuildActionCatalog(RungPolicy.Table),
                    // T74/A33: the replay resolves the SAME way the fresh fight did, so it must price
                    // at the holder's effective rung too -- otherwise a replay of a match is not a
                    // replay of that match. Read through the one key->owner mapping.
                    unlockStateFor: _store.UnlockStateFor(storedSetup),
                    unlockTuning: UnlockTuningPolicy.Tuning,
                    containerResolver: replayContainerResolver,
                    onEffectHostReady: host => ActionContainerEffectResolverFactory.RegisterInto(
                        host, replayContainerDefs.Concat(replayEquipDefs).ToList()),
                    runnerBindings: replayRunnerBindings,
                    containersWithRunnerCoverage: replayRunnerCoverage,
                    equipEffectIdsFor: replayEquipEffectIdsFor,
                    board: NormalBattleBoard.Build(storedSetup.Squad.Select(a => a.Key).ToList(), storedSetup.Wave.Select(a => a.Key).ToList(), entry.Seed)),
                playerId, storedSetup);
            var replayTurnOrder = TurnOrderRecord.FromTrace(replayTrace, storedSetup);
            return (true, "replay", new WebMatchOutcome(true, entry.MatchKey, entry.RunId, storedReport, replayTurnOrder));
        }

        // FR1: player-facing (an expedition collect) -- opts in, same as RunWebMatchAsync.
        var freshTrace = new BattleTrace();
        var (report, notify) = ResolveAndIngest(playerId, matchKey, setup, seed, freshTrace);
        await BroadcastAsync(playerId, notify).ConfigureAwait(false);
        var linked = _store.TryGetWebMatchLog(playerId, corr);
        var turnOrder = TurnOrderRecord.FromTrace(freshTrace, setup);
        return (true, "ok", new WebMatchOutcome(false, matchKey, linked?.RunId, report, turnOrder));
    }

    /// <summary>Boot sweep: re-ingest logged matches whose ingest never landed. Version-guarded —
    /// a report from a different engine/ruleset must not be silently re-blessed.</summary>
    public int SweepUnresolved()
    {
        var healed = 0;
        // Hashed ONCE for the whole sweep. Per entry it would be a full read of every covered
        // content table per row — the content cannot change while this loop runs.
        var content = _store.ComputeContentHash();

        foreach (var entry in _store.ListUnresolvedWebMatches())
        {
            // A refusal is TERMINAL, not a skip: the row is marked so it leaves the unresolved
            // window for good. Left unmarked, refused rows are re-listed every boot and (since
            // the query is ORDER BY id ASC LIMIT n) enough of them would crowd out every newer
            // row — crash recovery dies silently while still reporting a clean sweep.
            if (entry.EngineVersion != BattleRuleset.EngineVersion
                || entry.RulesetVersion != BattleRuleset.RulesetVersion
                || entry.RngAlgoVersion != SeededRng.RngAlgoVersion)
            {
                var why = $"version {entry.EngineVersion}/{entry.RulesetVersion}/{entry.RngAlgoVersion} != " +
                          $"{BattleRuleset.EngineVersion}/{BattleRuleset.RulesetVersion}/{SeededRng.RngAlgoVersion}";
                Console.Error.WriteLine($"[web-match] sweep refused {entry.MatchKey}: {why}");
                _store.MarkWebMatchSweepRefused(entry.Id, why);
                continue;
            }

            // Owner decision 7: Math.Exp is not bit-identical across architecture/OS — a report
            // logged on another platform must not be silently re-resolved here.
            if (entry.EnvironmentStamp != null && entry.EnvironmentStamp != BattleEnvironment.Stamp)
            {
                var why = $"platform '{entry.EnvironmentStamp}' != '{BattleEnvironment.Stamp}'";
                Console.Error.WriteLine($"[web-match] sweep refused {entry.MatchKey}: {why}");
                _store.MarkWebMatchSweepRefused(entry.Id, why);
                continue;
            }

            // B21 (spec-interactive-turns.md §4): an INTERACTIVE match is only reproducible from its
            // decision trace, because with real input `(setup, seed)` stops describing the battle.
            // Refused UNCONDITIONALLY, trace or no trace (party-dungeon D2.16, found while wiring the
            // delve profile through this check): even WITH a trace, `ResolveAndIngest` below has no
            // path to replay it or to pick the row's own profile — it always resolves through
            // `ProfileForWave`, which can never name a delve room's synthetic wave id — nor is there
            // any signal in THIS row distinguishing "the trace covers a completed battle, the crash
            // was only in the ingest gap" from "the session was frozen mid-battle" (that distinction
            // lives only in the in-memory `BattleSessionRegistry`, never persisted here). Re-resolving
            // either shape would substitute AI decisions for a player's and risk silently finishing a
            // steered party on autopilot across a restart, which is the exact hole the trace exists to
            // close. Reachable only once a real caller starts stamping `profile_id` (still none in
            // `src/` today — every `AppendWebMatchLog` call site omits it); replaying a recorded prefix
            // and handing off to a live session is `DelveBattleEndpoints`'s own job when it exists,
            // never the boot sweep's.
            if (IsInteractive(entry))
            {
                const string why = "interactive match — refused rather than re-resolved with AI decisions or a stale trace";
                Console.Error.WriteLine($"[web-match] sweep refused {entry.MatchKey}: {why}");
                _store.MarkWebMatchSweepRefused(entry.Id, why);
                continue;
            }

            // E8: effect content is rows, so a resolve is only reproducible against the content it
            // ran on. A REGISTRY change is not a refusal — a table joining the covered set is
            // expected and attributable — but an edited row under the same registry version is.
            var contentCheck = ContentHashComparison.Compare(entry.ContentHash, content);
            if (contentCheck.ShouldRefuse)
            {
                Console.Error.WriteLine($"[web-match] sweep refused {entry.MatchKey}: {contentCheck.Reason}");
                _store.MarkWebMatchSweepRefused(entry.Id, contentCheck.Reason);
                continue;
            }
            if (contentCheck.Verdict == ContentHashVerdict.RegistryChanged)
                Console.Error.WriteLine($"[web-match] sweep {entry.MatchKey}: {contentCheck.Reason}");

            // combat-ai `replay-identity` (spec §2) — the fifth guard, placed after the content-hash
            // check because that one answers a different question ("did the effect CONTENT move?") and
            // this one answers "can this row still resolve under the profile set it used?". Written in
            // the same shape with the same terminal mark: a refusal leaves the unresolved window for
            // good, so a row that can never be re-resolved cannot crowd newer crash-recovery rows out
            // of the `ORDER BY id ASC LIMIT n` window. The sweep does NOT attempt a pinned re-resolve —
            // it already refuses anything it cannot reproduce exactly, and re-resolving under a pinned
            // set is the two replay branches' job, never the boot path's.
            //
            // The persisted reason keeps the token AND the detail, matching the guards above it:
            // `sweep_refused` is kept for forensics, and an operator reading this row needs both which
            // refusal it was and which profile moved.
            var pin = CombatAiProfilePin.Resolve(_combatAiProfiles, entry.CombatAiProfile);
            if (pin.IsRefused)
            {
                var why = $"{pin.Refusal}: {pin.Detail}";
                Console.Error.WriteLine($"[web-match] sweep refused {entry.MatchKey}: {why}");
                _store.MarkWebMatchSweepRefused(entry.Id, why);
                continue;
            }

            try
            {
                var setup = JsonSerializer.Deserialize<BattleSetup>(entry.SetupJson);
                if (setup == null)
                {
                    // Unparseable setup can never heal on a later boot either — terminal.
                    Console.Error.WriteLine($"[web-match] sweep refused {entry.MatchKey}: unreadable setup_json");
                    _store.MarkWebMatchSweepRefused(entry.Id, "unreadable setup_json");
                    continue;
                }
                // FR1: the boot sweep -- the ONE caller that must never trace. Nobody is watching a
                // crash-recovery re-ingest; tracing it would be exactly the bulk-path cost D3 scopes
                // this feature away from. `trace` defaults to null; left unpassed deliberately.
                ResolveAndIngest(entry.PlayerId, entry.MatchKey, setup, entry.Seed);
                healed++;
            }
            catch (ArgumentException ex)
            {
                // A DETERMINISTIC resolve failure — the persisted setup_json + seed produce the same
                // exception on every boot, so this row can never heal. Refusing it is the rule
                // spec-interactive-turns.md §4 states for the trace case ("The sweep must refuse, not
                // heal"), applied here to the resolve case it left implicit, and it is the hazard this
                // method's own comment above already names: an unmarked row is re-listed every boot,
                // and enough of them crowd every newer row out of the `ORDER BY id ASC LIMIT` window.
                //
                // The exception TYPE is deliberately narrow. `ArgumentException` here is exactly what
                // "the data itself is unusable" means in the resolver: empty squad / empty wave / bad
                // or duplicate actor key (BattleEngine.ValidateActorKey + Resolve's own guards), and
                // `W <= 0` from WaveCatalog.ProfileFor. It is NOT a proxy for "any failure" —
                // `InvalidOperationException` is deliberately left to the transient branch below,
                // because it covers BOTH a row-specific runaway loop AND process-global
                // preconditions ("ActionTimingPolicy.Configure(...) has not run"); refusing on it
                // would mark every row terminal for what is a server misconfiguration.
                var why = "unresolvable setup: " + ex.Message;
                Console.Error.WriteLine($"[web-match] sweep refused {entry.MatchKey}: {why}");
                _store.MarkWebMatchSweepRefused(entry.Id, why);
            }
            catch (Exception ex)
            {
                // Transient (SqliteException from InsertWebMatchEvents' rolled-back transaction, a
                // busy/locked database, a global tuning precondition): leave the row UNRESOLVED so the
                // next boot retries it. Marking it refused would bury recoverable work permanently.
                //
                // DELIBERATE TRADE-OFF for BattleEngine's own runaway-loop guard, which throws
                // InvalidOperationException ("a runaway event loop, not a long battle") and IS
                // deterministic for a given setup+seed. It is left here on purpose rather than
                // refused: its message names an ENGINE defect, not unusable data, so the correct
                // signal is a loud recurring log that a human reads, not a terminal mark that would
                // bury every affected row the first time an engine change causes one. If that guard
                // ever proves reachable in a shipped build, it wants its own decision — not this
                // catch's default.
                Console.Error.WriteLine($"[web-match] sweep failed for {entry.MatchKey}: {ex.Message}");
            }
        }

        return healed;
    }

    /// <param name="trace">
    /// `battle-tempo` `forecast-rail` FR1 (spec-forecast-rail.md §2.2, D3): trace opt-in PER BATTLE —
    /// null (the default) traces nothing. `SweepUnresolved` (the boot-sweep bulk path) is the ONE
    /// caller that must always pass null; every other caller of this method is player-facing and
    /// opts in. `Turns` is excluded from `BattleTrace.Digest` by design, so passing one here moves no
    /// determinism hash regardless of which callers opt in.
    /// </param>
    (BattleReport Report, EventInsertNotify Notify) ResolveAndIngest(
        long playerId, string matchKey, BattleSetup setup, ulong seed, BattleTrace? trace = null)
    {
        // E12: the report carries WHICH CONTENT produced it. Stamped here, where the store is, and
        // never recomputed later — a power or a trait magnitude read back under a different
        // contentHash is a different number, so a recomputed stamp would describe a battle that did
        // not happen. Provenance only: it is excluded from the determinism hash, exactly as the


        // platform stamp is, or every added row would look like a determinism break.
        var (freshContainerResolver, freshContainerDefs, freshRunnerBindings, freshRunnerCoverage) = ActionContainerEffectResolverFactory.Build(_store);
        // spec-equip-runtime.md Battle-half amendment (2026-09-07): see the replay call sites above
        // for the full rationale — same compile-once/register/grant-per-actor pattern, here for the
        // fresh-resolve path every real (non-replayed) web match actually takes.
        var freshSpecimenIds = setup.Squad.Select(a => a.SpecimenId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToList();
        var (freshEquipDefs, freshEquipEffectIdsFor) = ActionContainerEffectResolverFactory.BuildEquip(_store, freshSpecimenIds);
        var report = BattleEngine.Resolve(setup, seed, trace, profile: ProfileForWave(setup.WaveId), actionCatalog: _store.BuildActionCatalog(RungPolicy.Table),
            // T74/A33: the fresh path every real web match takes -- a held action costs what the
            // HOLDER's own ladder says, read through the store this service already owns.
            unlockStateFor: _store.UnlockStateFor(setup),
            unlockTuning: UnlockTuningPolicy.Tuning,
            containerResolver: freshContainerResolver,
            onEffectHostReady: host => ActionContainerEffectResolverFactory.RegisterInto(
                host, freshContainerDefs.Concat(freshEquipDefs).ToList()),
            runnerBindings: freshRunnerBindings,
            containersWithRunnerCoverage: freshRunnerCoverage,
            equipEffectIdsFor: freshEquipEffectIdsFor,
            board: NormalBattleBoard.Build(setup.Squad.Select(a => a.Key).ToList(), setup.Wave.Select(a => a.Key).ToList(), seed)) with
        {
            ContentHash = _store.ComputeContentHash().ToCompact(),
        };
        var events = BattleReportEmitter.Emit(report, matchKey).ToList();

        // class-system-todo.md P9.2's own prerequisite, found while building it: without this, no
        // later query can attribute a battle to the allocation that produced it — rpg_aptitude_
        // allocation is an upsert with no history, so a later re-allocation silently erases every
        // earlier battle's own provenance. Captured here, once, reading the SAME store/scope/key
        // AptitudeChannelMods already read to build this same squad (BuildSquad, above) — not
        // re-derived from a different assumption, and never affects combat math or goldens (a pure
        // additional event, not a resolver input).
        //
        // Unique specimens use their own persisted allocation. Do not label this as a species
        // allocation: the empire fallback is a separate source used only by general spawns.
        var aptitudeShares = _store.LoadAllocation(
            FusionRpg.Core.Stats.Aptitudes.AllocationScope.Commander, AptitudeEndpoints.ScopeKey(playerId)).Shares();
        var uniqueShares = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var instanceId in setup.Squad.Select(a => a.SpecimenId).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal))
        {
            // EP1.14 (spec-default-build.md) -- this provenance snapshot never affects combat math or
            // goldens (a pure additional event), but it is still a production reader of the scope: a
            // levelled, never-built specimen now snapshots its real default shares instead of {}.
            uniqueShares[instanceId!] = _store.EffectiveUniqueAllocation(
                instanceId!, FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Tuning).Allocation.Shares();
        }
        events.Add(new EventEnvelope
        {
            T = "",
            Game = RpgConstants.GameIdWebRpg,
            Kind = "aptitude.snapshot",
            MatchKey = matchKey,
            Payload = new Dictionary<string, object?>
            {
                ["scope"] = "commander+unique",
                ["shares"] = aptitudeShares,
                ["unique"] = uniqueShares
            }
        });

        // The engine is clockless — stamp strictly monotonic t here, at ingest.
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < events.Count; i++)
            events[i].T = t0.AddMilliseconds(i).ToString("o");

        var notify = _store.InsertWebMatchEvents(playerId, matchKey, events);

        // species-build-todo.md T4.6: ResolveAndIngest is the sole exactly-once ingest point (this
        // method's own doc history) -- the one place a Zomboss encounter outcome can be recorded
        // exactly once, never on a replay (the two replay branches above never call this method).
        if (setup.ZombossPatternId != null)
            _store.RecordZombossEncounterOutcome(playerId, report.Outcome == BattleOutcome.Victory);

        return (ApplyZombossReveal(report, playerId, setup), notify);
    }

    /// <summary>
    /// species-build-todo.md T4.6, spec-zomboss-adaptive.md decision 4: the report leaving this
    /// service never carries the RAW pattern a battle actually resolved with — only the one from
    /// `revealDelayEncounters` encounters ago (null before enough history exists, never a fake
    /// placeholder). The audit trail (the persisted setup, and the events already emitted above) keeps
    /// the true value regardless; only the value handed back to the caller is delayed.
    /// </summary>
    internal BattleReport ApplyZombossReveal(BattleReport report, long playerId, BattleSetup setup)
    {
        if (setup.ZombossPatternId is null || setup.ZombossEncounterIndex is not int encounterIndex)
            return report;

        var revealed = _store.GetRevealedZombossPatternId(
            playerId, encounterIndex, FusionRpg.Core.Battle.Ai.ZombossAdaptiveTuningHub.Tuning.RevealDelayEncounters);
        return report with { ZombossPatternId = revealed };
    }

    /// <summary>
    /// species-build-todo.md T4.6 — the enemy side's own commander (spec-zomboss-adaptive.md's own ⛔
    /// seam callout): resolves which pattern the Zomboss fights this encounter with BEFORE the battle
    /// runs (never during resolution, so `(setup, seed)` stays reproducible), applies it as real
    /// combat channel mods on every wave actor, and stamps the setup so the reveal and the post-battle
    /// outcome record both have what they need.
    ///
    /// <para><b>Two different Θ-likes, on purpose.</b> <paramref name="theta"/> is the PLAYER's own
    /// progression Θ (`IPowerIndexProvider.ActorIndex`) — it drives the Zomboss's point BUDGET via the
    /// SAME `PointBudget.PointsFor` every commander build uses, so "a harder Zomboss is a higher Θ or a
    /// better allocation, never a stat nobody could have had" holds literally: the Zomboss spends from
    /// the identical pool the human commander does. `WaveCatalog.Get(...).ContentIndex` — Θ_content, a
    /// SEPARATE, already-established scale (`WaveCatalog.cs`'s own doc comment) — is what
    /// <see cref="ZombossPatternSelector"/> reads as "the Zomboss's own level," since it is the
    /// difficulty of the CONTENT the player is facing, not the player's personal progression.</para>
    /// </summary>
    public BattleSetup ApplyZombossPattern(long playerId, BattleSetup baseSetup, long theta, ulong seed)
    {
        // DEBT — channelmods-hub: one-release BattleChannelMod concat over the pattern allocation;
        // the Hub twin is AptitudeResolver.Resolve over the same ZombossCommanderAllocation.
        // Delete in battle-hub-fuse (T6).
        var tuning = FusionRpg.Core.Battle.Ai.ZombossAdaptiveTuningHub.Tuning;
        var level = WaveCatalog.Get(baseSetup.WaveId).ContentIndex;
        var selection = _store.SelectZombossPattern(playerId, level, seed, tuning);

        var zomboss = new FusionRpg.Core.Battle.Ai.ZombossCommanderAllocation(selection.PatternId);
        zomboss.Refresh(FusionRpg.Core.Stats.Aptitudes.AllocationScope.Commander, theta, FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Tuning);
        // battle-hub-fuse T5: the pattern reaches wave actors as Hub inputs (resolved through the
        // Hub aptitude twin at the actors' own content level, exactly the ladder input the old
        // concat used), not pre-folded ChannelMods.
        var patternAllocation = zomboss.Resolve(new FusionRpg.Core.Stats.StatContext());

        return baseSetup with
        {
            ZombossPatternId = selection.PatternId,
            ZombossEncounterIndex = selection.EncounterIndex,
            Wave = baseSetup.Wave.Select(a => a with
            {
                HubInputs = (a.HubInputs ?? new BattleHubInputs()) with { Aptitude = patternAllocation }
            }).ToList(),
        };
    }

    /// <summary>Server-authoritative squad snapshots from the roster — shared with expeditions.</summary>
    public (bool Ok, string Reason, List<BattleActorSetup>? Squad, List<string> InstanceIds) BuildSquad(
        long playerId, IReadOnlyList<string>? squadInstanceIds)
    {
        // Own guards, not caller trust: every future battle producer inherits them.
        const int maxSquad = 6;
        var none = new List<string>();
        if (squadInstanceIds is { Count: > maxSquad })
            return (false, "squad.toolarge", null, none);
        if (squadInstanceIds != null
            && squadInstanceIds.Distinct(StringComparer.Ordinal).Count() != squadInstanceIds.Count)
            return (false, "squad.duplicate", null, none);

        // Contracts gate fielding (spec-creature-contracts.md). Settling first is what keeps an
        // un-migrated player from being refused everything; on any day already settled it is a
        // single read, so this is not a billing loop on the battle path.
        _store.SettleContracts(playerId);
        var contracts = _store.ListContracts(playerId)
            .ToDictionary(c => c.InstanceId, c => c, StringComparer.Ordinal);
        bool Bound(string id) => contracts.TryGetValue(id, out var c) && c.Bound;
        bool Deployable(string id) => contracts.TryGetValue(id, out var c) && c.Deployable;

        var roster = _store.ListCreatureRoster(playerId).Items;
        List<CreatureSpecimenDto> picked;
        if (squadInstanceIds is { Count: > 0 })
        {
            picked = new List<CreatureSpecimenDto>();
            foreach (var id in squadInstanceIds)
            {
                var specimen = roster.FirstOrDefault(s =>
                    string.Equals(s.Profile.InstanceId, id, StringComparison.Ordinal));
                if (specimen == null) return (false, "squad.unknown-specimen", null, none);
                if (!Bound(id)) return (false, "squad.unbound", null, none);
                if (!Deployable(id)) return (false, "squad.insubordinate", null, none);
                picked.Add(specimen);
            }
        }
        else
        {
            // Auto-pick skips what cannot serve rather than refusing — the player asked for a
            // battle, not for a lecture about their roster.
            picked = roster.Where(s => Deployable(s.Profile.InstanceId)).Take(3).ToList();
        }

        if (picked.Count == 0)
        {
            // SIM convenience: an empty roster still gets a deterministic synthetic squad.
            return (true, "", Enumerable.Range(0, 2).Select(i => Synthetic(i)).ToList(), none);
        }

        // ai-empire-species EP4.18 (R23): one commander pool PER OWNER EMPIRE, memoised so a squad of N
        // still does one read per DISTINCT empire rather than N (the batching this load already existed
        // for) -- and never the human's pool applied to another empire's specimen. The one empire-keyed
        // read is `CommanderPoolFor` (EP4.17 + this store's configured host Theta).
        var commanderPools = new Dictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation>(StringComparer.Ordinal);
        FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation CommanderPoolForEmpire(FusionRpg.Core.Commanders.EmpireId empire)
        {
            if (!commanderPools.TryGetValue(empire.Value, out var pool))
            {
                pool = _store.CommanderPoolFor(new FusionRpg.Core.Saves.EmpireRef(
                    new FusionRpg.Core.Saves.SaveId(playerId), empire)).Allocation;
                commanderPools[empire.Value] = pool;
            }
            return pool;
        }
        // species-progression SP1.4 (layer-source-selector, three-path parity): every squad member
        // here already comes from THIS player's own roster (ListCreatureRoster(playerId) above), so
        // it is usually human-owned in practice -- this asks the SAME selector the lawn (SP1.3) and
        // the sheet (SP1.4) ask anyway, making that invariant structural rather than assumed. The pool
        // itself is memoised per owner empire (above); only the per-actor DECISION to apply it is
        // selector-driven, so this adds no per-actor I/O.
        var humanEmpire = _store.HumanEmpireOf(playerId);

        var squad = new List<BattleActorSetup>(picked.Count);
        for (var i = 0; i < picked.Count; i++)
        {
            var s = picked[i];
            var species = FusionRpg.Core.Creatures.CreatureSpeciesCatalog.Get(s.Profile.SpeciesId);
            var level = (int)Math.Max(1, s.Actor.Level);

            // item module 4/5's own "deploy" moment (RpgStore.MaterializeRolledEquipRuntime's own doc) —
            // a rolled item's binding and granted actions did not exist anywhere outside
            // rpg_item_assignment until this call. Must run before EquippedActionIdsFor below, which
            // reads what this writes.
            _store.MaterializeRolledEquipRuntime(s.Profile.InstanceId, level);

            var ownerEmpire = _store.SpecimenOwnerEmpire(s.Profile.InstanceId)?.Empire ?? humanEmpire;
            var layers = FusionRpg.Core.Stats.Aptitudes.ProgressionLayerSelector.Select(
                FusionRpg.Core.Creatures.CreatureProgressionSource.UniqueSpecimen(
                    s.Profile.InstanceId, occurrenceId: "web-squad"),
                ownerEmpire, humanEmpire);
            var actorCommander = layers.CarriesCommander
                ? CommanderPoolForEmpire(ownerEmpire) : FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;

            squad.Add(new BattleActorSetup
            {
                Key = $"squad:{i}",
                Side = "squad",
                // spec-equip-runtime.md Battle-half amendment (2026-09-07): found while proving the
                // stat.modify equip path end to end — BuildSquad never set SpecimenId at all, so
                // BattleRunState.BindEquip (keyed on it) could never fire for a real web match/
                // expedition squad, silently, regardless of how correctly the rest of the equip-atom
                // wiring was built. A real, previously-unnoticed structural gap, not a hypothetical one.
                SpecimenId = s.Profile.InstanceId,
                SpeciesId = species.SpeciesId,
                TypeId = species.CreatureTypeId,
                Level = level,
                ElementPrimary = species.ElementPrimary,
                ElementSecondary = species.ElementSecondary,
                TraitIds = s.Profile.TraitIds,
                MaxHp = BattleRuleset.BaseHp(level),
                Atk = BattleRuleset.BaseAtk(level),
                Defense = BattleRuleset.BaseDefense(level),
                // battle-hub-fuse T5: producers reach battle as Hub inputs (resolved through the
                // Hub twins), not pre-folded ChannelMods. The DEBT-tagged ChannelMods adapters stay
                // for tests until T6 deletes them with the composer.
                HubInputs = new BattleHubInputs
                {
                    // EP1.14 (spec-default-build.md) -- the real combat-math input: a levelled
                    // specimen the player never built now fights with its species' default
                    // distribution instead of Empty (R-S1: "same stats as a general unit").
                    Aptitude = actorCommander + _store.EffectiveUniqueAllocation(
                        s.Profile.InstanceId, FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Tuning).Allocation,
                    BoundAtoms = EquippedBoundAtoms.DerivedFromStore(_store, s.Profile.InstanceId),
                    StarLoyalty = new FusionRpg.Core.Stats.Derived.Subsystems.StarLoyaltyContribution(
                        s.Profile.Star,
                        contracts.TryGetValue(s.Profile.InstanceId, out var c) ? c.Loyalty : 0,
                        level),
                    // species-progression `species-layer-delivery` step 6.2 (SP6.7): 1a + 1b of the
                    // specimen's own species, of its OWNER empire -- the SAME ownerEmpire this loop
                    // already resolved for the commander split above, never re-derived. Never 2b.
                    SpeciesLayers = _store.SpeciesLayersForSpecimen(
                        new FusionRpg.Core.Saves.EmpireRef(new FusionRpg.Core.Saves.SaveId(playerId), ownerEmpire),
                        species.SpeciesId) is { Count: > 0 } webSquadSpeciesLayers ? webSquadSpeciesLayers : null,
                },
                EquippedActionIds = EquippedActionIdsFor(s.Profile.InstanceId, _store),
            });
        }

        return (true, "", squad, picked.Select(p => p.Profile.InstanceId).ToList());
    }

    /// <summary>
    /// W7 (battle-derived-wire T4): the player's real active commander auras, projected onto the
    /// battle setup — the first production producer of `ActiveCommanderAura` (before this, `ActiveAuras`
    /// was assigned in tests only).
    ///
    /// <para><b>Sources, both shipped, neither re-derived.</b> The active SET comes from the SAME
    /// process-local `AuraRuntime` the aura endpoints use (`AuraRuntimeEndpoints.ActiveRuntime` — one
    /// cache, never a second dictionary). The per-channel MAGNITUDE comes from
    /// `AtomPushService.AuraChannelReferenceValue`, the resolver the atom push already uses — a
    /// structural reference (rung floor, full share, pin Θ), NOT yet live per-commander scaling;
    /// `AuraMagnitude.ReferenceChannelValue`'s own doc names that per-player follow-up. This method
    /// adds no aura resolver of its own.</para>
    ///
    /// <para><b>Read fresh on every call, never cached</b> — so an enable/disable is reflected on the
    /// very next battle, and the result does not depend on when the squad list was built.</para>
    ///
    /// <para>`CommanderSide` is `"squad"`: battle's own squad/wave partition IS the own/enemy split,
    /// and `BattleRunState`'s delivery loop applies each row only to actors whose `Setup.Side` matches
    /// it. A player with no active aura gets an EMPTY list, byte-identical to every prior battle.</para>
    /// </summary>
    internal IReadOnlyList<ActiveCommanderAura> ActiveAurasFor(long playerId)
    {
        var active = AuraRuntimeEndpoints.ActiveRuntime(playerId, _store).ActiveAuraIds;
        if (active.Count == 0) return Array.Empty<ActiveCommanderAura>();

        var rows = new List<ActiveCommanderAura>();
        foreach (var auraId in active)
        {
            if (!AuraContentCatalog.IsKnown(auraId)) continue;
            var row = AuraContentCatalog.Resolve(auraId);
            for (var i = 0; i < row.GrantChannels.Count; i++)
            {
                var channel = row.GrantChannels[i];
                rows.Add(new ActiveCommanderAura(
                    CommanderSide: "squad",
                    TargetChannel: channel,
                    Value: AtomPushService.AuraChannelReferenceValue(auraId, channel),
                    SourceId: "aura:" + auraId));
            }
        }
        return rows;
    }

    /// <summary>
    /// T22 (action-todo.md): "the auto-equipped set appears in the battle report." A real loadout row
    /// wins if the specimen has one; otherwise it is auto-equipped live from whatever skills the
    /// specimen currently holds, ranked by <see cref="RungPolicy.Table"/> — never persisted, so a
    /// later unlock/discard is reflected immediately with no stale cache to invalidate (the same
    /// contract `RpgStore.GetLoadoutOrAutoEquip`'s own doc comment states).
    ///
    /// <para>Loadout stays keyed on <see cref="OwnerKind.Entity"/> + the specimen's own instance id,
    /// matching `LoadoutStoreTests.cs`'s own convention for "one creature's loadout, independent of who
    /// currently owns it" — unchanged, since a loadout PREFERENCE is not permanent progress: losing one
    /// on a session boundary degrades gracefully to auto-equip, never to nothing.</para>
    ///
    /// <para><b>Grants read under <see cref="OwnerKind.UniqueActor"/>, fixed 2026-09-07
    /// (`action-grant-owner-kind-durability`)</b>: a specimen's hard-earned unlock-ladder grant IS
    /// permanent progress, and `OwnerKind.Entity` is session-scoped by design — `ClearSessionScopedBindings()`
    /// (real production caller: `Program.cs:654`, the boot sweep) deletes every `entity:` binding on a
    /// session boundary, which would silently wipe it. `OwnerKind.UniqueActor` was built 2026-09-01
    /// specifically for a durable, never-reused specimen identity, and is already proven for equipment
    /// — this is the second real caller, not a new mechanism. The write side
    /// (`RpgStore.UniqueActors.cs`'s `TryRollActionUnlocks`) is fixed in the same pass; read and write
    /// must always agree.</para>
    /// </summary>
    static IReadOnlyList<string> EquippedActionIdsFor(string instanceId, RpgStore store)
    {
        // Two grant sources, two lifetimes, both real. UniqueActor carries the durable unlock-ladder
        // grant (action-grant-owner-kind-durability, fixed 2026-09-07 — a specimen's hard-earned grant
        // must survive a session boundary). Entity carries an ITEM's granted action
        // (EquippedGrantProjection.GrantFor, module 19) — the opposite lifetime is deliberate:
        // unequipping the item must make the action disappear, and the boot sweep's
        // ClearSessionScopedBindings() already clears every `entity:` binding on a session boundary.
        // Reading only one scope silently drops the other grant source, so both are read and merged.
        var unlockLadderGrants = new OwnerScope(OwnerKind.UniqueActor, instanceId);
        var entityScope = new OwnerScope(OwnerKind.Entity, instanceId);

        var candidates = store.ListGrants(unlockLadderGrants)
            .Concat(store.ListGrants(entityScope))
            .Select(g => store.GetAction(g.ActionId))
            .Where(a => a is { Kind: ActionKind.Skill })
            .Select(a => new AutoEquipCandidate(a!.ActionId, a.Rung))
            .Distinct()
            .ToList();

        return store.GetLoadoutOrAutoEquip(entityScope, candidates);
    }

    static BattleActorSetup Synthetic(int i) => new()
    {
        Key = $"squad:{i}",
        Side = "squad",
        SpeciesId = "sim-squad",
        TypeId = 10_000,
        Level = 5,
        MaxHp = BattleRuleset.BaseHp(5),
        Atk = BattleRuleset.BaseAtk(5),
        Defense = BattleRuleset.BaseDefense(5)
    };

    async Task BroadcastAsync(long playerId, EventInsertNotify notify)
    {
        try
        {
            foreach (var pid in notify.ActivityPlayers.Distinct())
            {
                var rollup = _store.GetPvzActivityRollup(pid);
                if (rollup != null)
                    await _hub.Clients.Group(RpgConstants.WebGroup)
                        .SendAsync("PvzActivityUpdated", rollup).ConfigureAwait(false);
            }

            foreach (var d in notify.Progression)
            {
                await _hub.Clients.Group(RpgConstants.WebGroup).SendAsync("RpgProgressionUpdated", new
                {
                    playerId = d.PlayerId,
                    kind = d.Kind,
                    typeId = d.TypeId,
                    revision = d.Revision
                }).ConfigureAwait(false);
            }

            await _hub.Clients.Group(RpgConstants.WebGroup)
                .SendAsync("SoulsUpdated", new { playerId }).ConfigureAwait(false);
        }
        catch
        {
            // notifies are best-effort; the match is durable regardless
        }
    }
}

public static class WebMatchEndpoints
{
    /// <summary>SIM-only trigger — mapped inside the /api/test group (no player-facing battle
    /// API in this module; expeditions owns the player surface).</summary>
    public static void MapWebMatchTest(this RouteGroupBuilder test)
    {
        test.MapPost("/web-match", async (WebMatchRequest body, WebMatchService svc, RpgStore store) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            var (ok, reason, outcome) = await svc.RunWebMatchAsync(
                pid, body.CorrelationId ?? "", body.WaveId ?? "rift-skirmish", body.Squad);
            if (!ok) return Results.BadRequest(new { reason });
            return Results.Ok(new
            {
                replayed = outcome!.Replayed,
                matchKey = outcome.MatchKey,
                runId = outcome.RunId,
                outcome = outcome.Report.Outcome.ToString().ToLowerInvariant(),
                rounds = outcome.Report.Rounds,
                soulLootMilli = outcome.Report.SoulLootMilli,
                actors = outcome.Report.Actors
            });
        });
    }

    public sealed class WebMatchRequest
    {
        public long? PlayerId { get; set; }
        public string? CorrelationId { get; set; }
        public string? WaveId { get; set; }
        public List<string>? Squad { get; set; }
    }
}
