using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Delve.Battle;

/// <summary>
/// D2.16 (spec-delve-battle-profile.md §3) — the PURE half of "three consecutive timeouts (or an
/// explicit disconnect/steer-away) freezes a steered delve fight." Kept separate from the actual
/// freeze mechanics (cancelling the background <c>Task</c>, calling
/// <see cref="BattleSessionRegistry.Disconnect"/>, appending the delve-level log entry) because those
/// touch threading/store/hub concerns this project's own "Core decides, transport executes" split
/// (already used repeatedly this program — e.g. <c>ExtractionSettlement.Decide</c> →
/// <c>RpgStore.Delve.SettleExtractionUnlocked</c>) keeps out of Core.
///
/// <para><b>Deliberately NOT <see cref="BattleSessionRegistry.NoteTurn"/>.</b> That method counts the
/// identical way but calls <see cref="BattleSessionRegistry.Abandon"/> at the threshold — correct for
/// an ordinary web match ("nobody answering must end"), wrong for a delve
/// (spec-delve-battle-profile.md §3: "AFK inside a delve does NOT abandon... the delve session layer
/// therefore calls `Disconnect`... the fight is frozen, not lost"). This class reuses
/// <see cref="BattleSessionRegistry.MaxConsecutiveTimeouts"/> — the one structural constant — rather
/// than forking a second literal <c>3</c>, but never calls <c>NoteTurn</c> itself.</para>
/// </summary>
public sealed class LiveFreezeTrigger
{
    /// <summary>Turns of silence since the last player decision (or the last freeze/resume).</summary>
    public int ConsecutiveTimeouts { get; private set; }

    /// <summary>
    /// Records one decision (the caller's <see cref="InteractiveIntentSource"/>'s own
    /// <c>onRecorded</c> hook is the intended source, per spec §4b). A player decision resets the
    /// count; a timeout advances it. Returns <c>true</c> the instant this decision pushes the count to
    /// <see cref="BattleSessionRegistry.MaxConsecutiveTimeouts"/> — the caller freezes then, exactly
    /// once for this run of timeouts (a later player decision resets the count, so a fight that
    /// resumes and goes AFK again can trip this a second time).
    /// </summary>
    public bool OnDecisionRecorded(DecisionSource source)
    {
        if (source == DecisionSource.Player)
        {
            ConsecutiveTimeouts = 0;
            return false;
        }

        ConsecutiveTimeouts++;
        return ConsecutiveTimeouts >= BattleSessionRegistry.MaxConsecutiveTimeouts;
    }

    /// <summary>Resets the count — a fresh live session (a genuine resume) starts with a clean slate,
    /// exactly as a player decision would.</summary>
    public void Reset() => ConsecutiveTimeouts = 0;

    /// <summary>
    /// §4a's <c>steer{from,to}</c> payload (spec-delve-battle-profile.md §4a) for an IMPLICIT freeze —
    /// three timeouts or a dropped connection, never a player choosing to steer elsewhere. Nobody is
    /// steering afterward, so <c>to</c> is <c>null</c>, matching the spec's own words verbatim:
    /// "records a <c>steer{party, to: none}</c> entry."
    /// </summary>
    public static SteerLogPayload FreezeAwayPayload(int partyIndex) => new(partyIndex, null);

    /// <summary>The same payload shape for an EXPLICIT steer — the player deliberately moved control
    /// from one party to another (or to none, on leaving every fight). Both shapes reuse this one
    /// record rather than each call site hand-rolling an anonymous object, so the JSON key names
    /// (<c>from</c>/<c>to</c>) are written in exactly one place.</summary>
    public static SteerLogPayload SteerPayload(int? from, int? to) => new(from, to);
}

/// <summary>The <c>steer{from,to}</c> entry payload (spec-delve-battle-profile.md §4a) — a real type,
/// not an anonymous object, so every writer (implicit AFK-freeze, explicit player steer) serialises
/// the identical shape.</summary>
public sealed record SteerLogPayload(int? From, int? To);
