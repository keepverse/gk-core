using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Battle;

/// <summary>
/// D2.12 — the one place a delve battle resolves. `BattleEngine.Resolve` is called **explicitly**
/// with the `delve` profile pinned — never `WaveCatalog.ProfileForExpedition`/`.ProfileFor`, both of
/// which resolve a profile by `waveId` (a wave-catalog concept a delve encounter has none of; its own
/// profile choice is always `BattleModeProfileCatalog.Delve`, never a per-encounter pick). Setup
/// construction is `encounter-generator`'s (`Θ_room` + the party half); the intent source is
/// `RaidIntentSource`'s (D2.13) — both are parameters here, not built by this file.
/// </summary>
public static class DelveBattle
{
    /// <param name="unlockStateFor">
    /// T74/A33 (`spec-battle-holder-wiring.md` §2c): a held action costs what its HOLDER's own ladder
    /// says, and this Core-only resolver cannot reach the store that holds it — so the pair is a
    /// parameter, threaded straight through to <see cref="BattleEngine.Resolve"/>, exactly as
    /// <c>DistrictAssaultResolver.HubInputsFor</c> already is. Its one real caller
    /// (`DelveBattleSession`) supplies them from the store it was handed.
    /// </param>
    /// <summary>
    /// combat-ai `delve-automated-wiring` CAI3.4 (spec §4 "role is derived, once at setup"): the delve
    /// role, a pure function of the actor's own held actions over the closed nine-member
    /// <see cref="ActionTag"/> enum. Never seed data and never a tunable — the repo already has one
    /// `RoleId` vocabulary (equipment slots, `actor-sheet.v1.json`), and a second would be the
    /// third-vocabulary defect `ActionEnums.cs:20-24` names.
    ///
    /// <para><b>The rules, verbatim from §4.</b> Buckets are tallies of held-action TAGS, excluding the
    /// hand-built basic attack: <c>support</c> when <c>Heal + Buff + Debuff</c> is largest,
    /// <c>frontliner</c> when <c>Defensive + Construct</c> is, <c>striker</c> otherwise (Offensive,
    /// Movement, Summon, Utility, and the default). <b>Ties break <c>support > frontliner > striker</c></b>,
    /// stated so the result is deterministic. A wave actor (<c>PartyIndex is null</c>) is the enemy row
    /// <b>before any tally runs</b> — which is why the check is the first line. An actor holding only the
    /// hand-built basic attack has no loadout and is a <c>striker</c>, i.e. exactly today's behaviour
    /// expressed as a role.</para>
    ///
    /// <para>The basic attack is excluded by its own empty <c>ContainerId</c> — the same fact
    /// `BattleRunState`'s container-binding pass keys on ("basic attack's own `""` always skips").</para>
    /// </summary>
    public static AiRole RoleOf(bool isWaveActor, IReadOnlyList<CompiledAction> heldActions)
    {
        if (heldActions is null) throw new ArgumentNullException(nameof(heldActions));

        // `delve/enemy`, before any tally (spec §4 bullet 2).
        if (isWaveActor) return AiRole.Striker;

        var support = 0;
        var frontliner = 0;
        for (var i = 0; i < heldActions.Count; i++)
        {
            var action = heldActions[i];
            if (string.IsNullOrEmpty(action.ContainerId)) continue; // the hand-built basic attack

            for (var t = 0; t < action.Tags.Count; t++)
            {
                switch (action.Tags[t])
                {
                    case ActionTag.Heal:
                    case ActionTag.Buff:
                    case ActionTag.Debuff: support++; break;
                    case ActionTag.Defensive:
                    case ActionTag.Construct: frontliner++; break;
                }
            }
        }

        // The stated tie order, spelled out: support wins an equal bucket, and an empty tally on both
        // sides falls through to striker.
        if (support > 0 && support >= frontliner) return AiRole.Support;
        if (frontliner > 0) return AiRole.Frontliner;
        return AiRole.Striker;
    }

    public static BattleReport Run(
        BattleSetup setup, ulong seed, BattleTrace? trace = null,
        Action<BattleEffectHost>? onEffectHostReady = null, ActionCatalog? actionCatalog = null,
        IContainerEffectResolver? containerResolver = null, IIntentSource? intentSource = null,
        BoardState? board = null,
        Func<string, UnlockState>? unlockStateFor = null, UnlockTuning? unlockTuning = null)
        => BattleEngine.Resolve(
            setup, seed, trace, onEffectHostReady,
            profile: BattleModeProfileCatalog.Delve,
            actionCatalog, containerResolver, intentSource, board,
            unlockStateFor: unlockStateFor, unlockTuning: unlockTuning);
}
