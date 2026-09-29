using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions.Unlock;

/// <summary>ADG-F5: one candidate the roll declined to OFFER, with the refusal <see cref="ActionValidator"/>
/// itself makes for it — <see cref="ActionRejectionReason.BasicCollision"/> for a basic,
/// <see cref="ActionRejectionReason.ActionNotGrantable"/> for `grantable = 0`. Carried on
/// <see cref="UnlockGrantOutcome.Skipped"/> so a caller, and through it a player-visible surface, can say
/// WHY an option never reached the roll instead of inferring it from a grant that did not appear.</summary>
public readonly record struct UnlockSkip(string ActionId, ActionRejectionReason Reason);

/// <summary>One roll attempt's result. <see cref="Granted"/> false with a null
/// <see cref="RefusalReason"/> means "no eligible candidate" — a legal no-op, not a refusal the
/// ladder itself made. That covers both an empty catalog and a catalog whose every eligible row is
/// un-grantable (see <see cref="TryRollOnce"/>): the level-up stands either way, nothing is granted,
/// and the ratchet's own state is untouched, so the NEXT level gained rolls again.
///
/// <para><see cref="Skipped"/> is the roll's own report of the options it declined to offer, and
/// <see cref="UnlockRefusalReason.NoGrantableCandidate"/> is the reason it returns when those skips are
/// why nothing was offered. A successful roll reports its skips too — the point is that a refused
/// option is visible as a skip with a reason, never as a throw and never as silence.</para></summary>
public readonly record struct UnlockGrantOutcome(
    bool Granted, string? GrantedActionId, UnlockRefusalReason? RefusalReason,
    IReadOnlyList<UnlockSkip>? Skipped = null);

/// <summary>
/// T59.6 (spec-action-instance-and-grant.md §4): the roll-and-grant logic for one level gained. Pure,
/// DB-free like its sibling <see cref="UnlockDiscardService"/> (T20) — same injected-delegate seam, so
/// Core stays free of SQL per the DAL boundary. The caller (T59.7,
/// <c>AwardUniqueActorXpUnlocked</c>'s own transaction) invokes <see cref="TryRollOnce"/> once per
/// level gained, in the SAME transaction as the XP award — never a post-commit hook (spec's own stated
/// reason: <see cref="UnlockState"/>'s persistence must never observe a level the XP award itself
/// failed to commit).
/// </summary>
public sealed class ActionUnlockGrantService
{
    readonly Func<string, UnlockState> _loadUnlockState;
    readonly Action<string, UnlockState> _saveUnlockState;
    readonly Func<IReadOnlyList<ActionRow>> _catalog;
    readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _familyOf;
    readonly Action<string, string> _grant;

    /// <param name="loadUnlockState">Reads one owner's current <see cref="UnlockState"/> —
    /// <see cref="UnlockState.Empty"/> for a specimen with no row yet.</param>
    /// <param name="saveUnlockState">Persists the updated state. Called ONLY on a successful accept —
    /// never on an empty candidate set or a missed roll.</param>
    /// <param name="catalog">The full imported action catalog — read once per call, matching
    /// <see cref="ActionEligibility.Candidates"/>'s own caller-supplies-everything contract. It is a
    /// raw catalog, not a pre-filtered candidate list: the roll itself narrows it (see
    /// <see cref="TryRollOnce"/>), so a caller cannot hand in a set the grant path would refuse.</param>
    /// <param name="familyOf">The specimen's own species → families lookup (A-E1's decided mapping —
    /// a relation, not a scalar; see <see cref="FamilyMap"/>).</param>
    /// <param name="grant">Grants the chosen action to the owner — the already-proven
    /// <c>RpgStore.UpsertGrant</c> path, one level up. Called ONLY alongside <paramref name="saveUnlockState"/>,
    /// never independently.</param>
    public ActionUnlockGrantService(
        Func<string, UnlockState> loadUnlockState,
        Action<string, UnlockState> saveUnlockState,
        Func<IReadOnlyList<ActionRow>> catalog,
        IReadOnlyDictionary<string, IReadOnlyList<string>> familyOf,
        Action<string, string> grant)
    {
        _loadUnlockState = loadUnlockState ?? throw new ArgumentNullException(nameof(loadUnlockState));
        _saveUnlockState = saveUnlockState ?? throw new ArgumentNullException(nameof(saveUnlockState));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _familyOf = familyOf ?? throw new ArgumentNullException(nameof(familyOf));
        _grant = grant ?? throw new ArgumentNullException(nameof(grant));
    }

    /// <summary>
    /// One roll attempt. <paramref name="specimenWorldSeed"/> plus the ratchet's own
    /// <c>state.EarnCount</c> (already "which attempt is this" — no separate counter needed, spec §4
    /// point 3) name the one deterministic stream this call uses for BOTH which candidate is offered
    /// and whether the roll lands — two sub-streams off that name, never a caller-supplied RNG, so two
    /// calls with the same inputs are always byte-identical.
    ///
    /// <para><b>ADG-F5 — the offered set must be grantable, and the filter lives here.</b> The catalog
    /// is the store's WHOLE imported corpus, so it holds rows no write path will ever accept:
    /// <c>act.attack</c> is <c>Kind = Basic</c> (imported on every real boot, <c>Program.cs</c>), and a
    /// basic is intrinsic on every actor, so <see cref="ActionValidator.ValidateGrant"/> refuses it by
    /// construction. Offering such a row made the grant throw and the XP award's own transaction roll
    /// the level-up back — a player's level silently vanishing. The filter is
    /// <see cref="ActionValidator.IsGrantable"/>, i.e. the very predicate the grant write path
    /// re-checks, so the roll cannot offer what the write refuses and neither side owns a second
    /// definition of "grantable".</para>
    ///
    /// <para><b>Why here and not in the two layers around it.</b> Not
    /// <see cref="ActionEligibility.Candidates"/>: that is the eligibility axis (spec-eligibility-axis.md
    /// §3.2 — who may HOLD an action, by scope) and its own doc-fixed formula is scope-only; folding a
    /// grantability filter into it would make every eligibility caller silently answer a different
    /// question. Not the catalog delegate the store hands in (<c>RpgStore.TryRollActionUnlocks</c>): the
    /// data layer already supplies the raw catalog it is asked for, and "what may be granted" is
    /// action-layer law — the DAL would have to re-state it. The roll is the layer that DECIDES which
    /// candidate to offer, so the offer is where the constraint belongs.</para>
    ///
    /// <para><b>Empty candidate set.</b> Reached when the catalog has no eligible row, when every
    /// eligible row is already held, or when every remaining one is un-grantable (a store holding only
    /// basics, say). All three are the same legal no-op — <c>Granted: false</c>, no save, no grant,
    /// <c>EarnCount</c> unchanged — never a throw: the level-up that triggered the roll has already been
    /// committed and must not be discarded over an empty candidate list. The third case is distinguished
    /// for the caller: <see cref="UnlockRefusalReason.NoGrantableCandidate" /> plus the skipped ids in
    /// <see cref="UnlockGrantOutcome.Skipped" />, so a surface can say why nothing was offered.</para>
    /// </summary>
    public UnlockGrantOutcome TryRollOnce(string instanceId, string? speciesKey, ulong specimenWorldSeed, UnlockTuning tuning)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("instanceId required", nameof(instanceId));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var state = _loadUnlockState(instanceId);
        var heldIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in state.Held) heldIds.Add(h.UnlockId);

        var candidates = new List<ActionRow>();
        List<UnlockSkip>? skipped = null;
        foreach (var a in ActionEligibility.Candidates(_catalog(), speciesKey, _familyOf))
        {
            if (heldIds.Contains(a.ActionId)) continue; // held already -- this roll could not offer it either way
            if (ActionValidator.GrantRefusal(a) is { } refusal)
            {
                // ADG-F5: SKIP it, naming the validator's own refusal, and never offer it -- offering
                // `act.attack` made the grant delegate throw and rolled the whole level-up back.
                (skipped ??= new List<UnlockSkip>()).Add(new UnlockSkip(a.ActionId, refusal.Reason));
                continue;
            }
            candidates.Add(a);
        }

        if (candidates.Count == 0)
            return new UnlockGrantOutcome(false, null,
                skipped is { Count: > 0 } ? UnlockRefusalReason.NoGrantableCandidate : null,
                skipped); // legal no-op (empty, all-held, or nothing grantable), never a throw

        var streamName = $"unlock:{instanceId}:{state.EarnCount}";
        var options = new List<WeightedOption<ActionRow>>(candidates.Count);
        foreach (var c in candidates) options.Add(new WeightedOption<ActionRow>(c, Weight: 1));
        var chosen = WeightedChoice.Pick(options, unchecked((long)specimenWorldSeed), streamName);

        // AtomRngImpl, never the literal type name -- the action-layer purity guard
        // (ActionsPurityGuardTests.cs) bans the substring "Random" anywhere under Core/Actions/, and
        // this repo's own global alias (TargetModeNames.cs: `global using AtomRngImpl = ...AtomRandom`)
        // exists specifically so real code can construct one without tripping it.
        var acceptRng = new AtomRngImpl(specimenWorldSeed, streamName + ":accept");
        var accept = state.TryAccept(chosen.ActionId, tuning, acceptRng);
        if (!accept.Accepted)
            return new UnlockGrantOutcome(false, null, accept.Reason, skipped);

        _saveUnlockState(instanceId, state);
        _grant(instanceId, chosen.ActionId);
        return new UnlockGrantOutcome(true, chosen.ActionId, null, skipped);
    }
}
