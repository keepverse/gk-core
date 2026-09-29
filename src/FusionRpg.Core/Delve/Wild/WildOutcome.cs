using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Wild;

/// <summary>The four outcomes an offer's own draw can land on (spec-wild-room.md §2's own row
/// names). `Attacks` here is a DRAWN outcome (an offer gone bad) — distinct from the `fight` VERB
/// a party can choose directly without ever drawing (§2's "Then" column routes both to the same
/// battle, but only one of them rolled dice to get there).</summary>
public enum WildOutcomeKind { Joins, TakesLeaves, Flees, Attacks }

/// <summary>
/// D4.4 (spec-wild-room.md §2, "The outcome draw") — the weighted draw an `offer:*` verb resolves
/// into. Row = `wild.outcome.{effectiveBand}.{joins,takesLeaves,flees,attacks}Milli` (sums to 1000,
/// already loader-checked — `DungeonTuning.cs:310-313`), drawn with the already-shipped
/// `WeightedChoice.Pick` on the caller's own named stream (`dungeon:wild:{r}:{c}:{seq}`, D4.8's own
/// wiring, unbuilt). This file owns no RNG derivation itself — `rollSeed`/`streamName` arrive
/// already resolved, matching every other pure decision function in this program.
/// </summary>
public static class WildOutcome
{
    public static WildOutcomeKind Draw(WildOutcomeRow row, long rollSeed, string streamName)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        // WeightedOption<T>.Weight is `int` (WeightedChoice.cs:6); the four Milli fields are `long`
        // but the loader already proves they sum to exactly 1000 (DungeonTuning.cs:311-312), so a
        // `checked` narrow can never actually overflow -- kept `checked` anyway as a defensive proof,
        // never a silent narrow, matching this program's own numeric discipline.
        var options = new[]
        {
            new WeightedOption<WildOutcomeKind>(WildOutcomeKind.Joins, checked((int)row.JoinsMilli)),
            new WeightedOption<WildOutcomeKind>(WildOutcomeKind.TakesLeaves, checked((int)row.TakesLeavesMilli)),
            new WeightedOption<WildOutcomeKind>(WildOutcomeKind.Flees, checked((int)row.FleesMilli)),
            new WeightedOption<WildOutcomeKind>(WildOutcomeKind.Attacks, checked((int)row.AttacksMilli)),
        };
        return WeightedChoice.Pick(options, rollSeed, streamName);
    }
}
