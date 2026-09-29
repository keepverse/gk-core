using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Difficulty;

/// <summary>Why a row is absent from the offer — never a clamp (spec-difficulty-ladder.md §6).
/// <see cref="BandBelowFloor"/> is the exact name the spec cites for the composer's own refusal.</summary>
public enum RungOfferRefusal { None, BandBelowFloor, NotUnlockedYet, MaxIndexExceeded }

/// <summary>What one `(playerId, domainId)` has cleared — <see cref="OathUnlock"/>'s only inputs.
/// Persistence is `domain-catalog`'s; this is the in-memory shape callers pass in.</summary>
public sealed record PlayerClears(IReadOnlySet<string> RungIds, IReadOnlySet<int> TailSteps)
{
    public static readonly PlayerClears None = new(new HashSet<string>(), new HashSet<int>());

    /// <summary>Rung 10's registry id is `impossible` — the tail's own unlock condition (§4:
    /// "abyss +1 needs a clear at rung 10") reads this rather than a separate caller-supplied flag.</summary>
    public bool Rung10Cleared => RungIds.Contains("impossible");
}

public sealed record RungOfferRow(string RungId, bool Offered, int? Band, string? BandName, bool IsPermadeath, RungOfferRefusal Refusal);

public sealed record TailOfferRow(int N, bool Offered, int? Band, string? BandName, string Label, RungOfferRefusal Refusal);

public sealed record RungOfferSet(
    IReadOnlyList<RungOfferRow> Rungs, IReadOnlyList<TailOfferRow> TailSteps,
    bool IsOnceEntry, bool OnceSealOnWipe, bool OnceFailKeepsBossLoot);

/// <summary>
/// The picker's per-`(domain, player)` view (spec-difficulty-ladder.md §4, §6). Composes
/// <see cref="RoomThetaComposer"/>, <see cref="OathUnlock"/>, <see cref="PermadeathGate"/> and
/// <see cref="TailLadder"/> — <b>refuse, never clamp</b>: a rung that would floor on this domain, or
/// one not yet unlocked, is omitted with a named reason rather than greyed or clamped; the composed
/// band is always a name (<see cref="EffectiveBandName"/>), never the raw integer or a Θ.
/// </summary>
public static class RungOffer
{
    public static RungOfferSet For(
        PowerTuning power, DungeonTuning dungeon, DomainThetaInputs domain, ParentWorldTerms world, PlayerClears clears)
    {
        var rows = new List<RungOfferRow>();
        foreach (var (rungId, _) in RungTable.All())
        {
            var isPermadeath = PermadeathGate.Applies(dungeon, domain, rungId);

            if (!OathUnlock.IsRungOffered(dungeon, rungId, clears.RungIds))
            {
                rows.Add(new RungOfferRow(rungId, false, null, null, isPermadeath, RungOfferRefusal.NotUnlockedYet));
                continue;
            }

            var rung = RungTable.Get(rungId);
            try
            {
                var composed = RoomThetaComposer.Compose(power, dungeon, domain, rung, row: 0, tailPlus: 0, isBoss: false, world);
                rows.Add(new RungOfferRow(rungId, true, composed.Band, EffectiveBandName(dungeon, composed.Band), isPermadeath, RungOfferRefusal.None));
            }
            catch (RungNotOffered)
            {
                rows.Add(new RungOfferRow(rungId, false, null, null, isPermadeath, RungOfferRefusal.BandBelowFloor));
            }
        }

        var tail = new List<TailOfferRow>();
        if (dungeon.DifficultyTail.Enabled)
        {
            for (var n = 1; ; n++)
            {
                var label = TailLadder.Label(dungeon, n);

                if (!OathUnlock.IsTailStepOffered(n, clears.Rung10Cleared, clears.TailSteps))
                {
                    // clear-opens-next (§4): once one step is un-cleared every larger step is
                    // unreachable too -- stop rather than manufacture refusal rows nobody can act on.
                    tail.Add(new TailOfferRow(n, false, null, null, label, RungOfferRefusal.NotUnlockedYet));
                    break;
                }

                var step = TailLadder.TryBand(power, dungeon, domain, n, isBoss: false, world);
                if (!step.Offered)
                {
                    tail.Add(new TailOfferRow(n, false, null, null, label, RungOfferRefusal.MaxIndexExceeded));
                    break; // MaxIndex only grows further out -- every larger n refuses too.
                }

                tail.Add(new TailOfferRow(n, true, step.Band, EffectiveBandName(dungeon, step.Band), label, RungOfferRefusal.None));
            }
        }

        return new RungOfferSet(
            rows, tail,
            IsOnceEntry: domain.IsOnceEntry,
            OnceSealOnWipe: dungeon.Domain.OnceEntry.SealOnWipe,
            OnceFailKeepsBossLoot: dungeon.Domain.OnceEntry.FailKeepsBossLoot);
    }

    /// <summary>Composed entrance band → display name (§6: "past the list, the last name with
    /// `+k`"), the same past-the-end shape as <see cref="TailLadder.Label"/>. Reads
    /// `bands.dangerBand` (`dungeon-registries`, added by `delve-stage` wave 5) — never a private
    /// name list, so a new tier is a registry edit, not a code change.</summary>
    public static string EffectiveBandName(DungeonTuning dungeon, int band)
    {
        if (band < dungeon.MinOfferedBand)
            throw new ArgumentOutOfRangeException(nameof(band), "a composed band below the offered floor is refused, never named (§6).");

        var danger = BandCatalog.Get("dangerBand");
        var index = band - 1; // band is 1-based; dangerBand's members start at band 1.
        if (index < danger.Members.Count)
            return danger.DisplayNames[danger.Members[index]];

        var lastMember = danger.Members[^1];
        var overflow = index - (danger.Members.Count - 1);
        return $"{danger.DisplayNames[lastMember]} +{overflow}";
    }
}
