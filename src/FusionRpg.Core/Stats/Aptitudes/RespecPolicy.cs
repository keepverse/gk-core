using FusionRpg.Core.Creatures.Generation;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// species-build-todo.md T4.1 — spec-species-respec.md, read in full this session. Prices CHURN, not
/// investment (decision 15, replacing decision 9's level-scaled price after audit finding A2 showed
/// species level and soul income don't relate the way that formula assumed). Deliberately NOT three
/// things, same reasoning class-system-todo.md P6.3 already established for the original placeholder:
/// not a cooldown (a cooldown forbids; this only prices, and the decay means being away makes it
/// CHEAPER — the opposite of the "punishes being away" failure a cooldown would cause); not a cap on
/// respec count (PS-8); not free (a free respec makes a build a menu selection, not a commitment).
///
/// <para><b>Soul, not Hunger</b> — spec-point-economy.md §8's "Ask first: which resource respec costs"
/// is answered by spec-species-respec.md's own decision 1. The prior <see cref="RespecResource.Hunger"/>
/// value was an explicitly documented placeholder pending that answer, not a shipped default.</para>
///
/// <para><b>Count, never level</b> — <see cref="PriceOf"/> takes the caller's own persisted respec
/// counter (<c>RpgStore.SpeciesRespec.cs</c>, T4.2) as an argument; this policy holds no state and does
/// not know or care which species is being repriced, matching <see cref="RespecPrice"/>'s own
/// unscoped-by-<see cref="AllocationScope"/> shape from the original design.</para>
/// </summary>
public enum RespecResource { Soul }

public readonly record struct RespecPrice(RespecResource Resource, long Amount);

/// <summary>
/// `respec-free-counter` EP4.9 (ruling R18) — HOW a species respec is paid for. R18 gives only the
/// species/empire respec a choice: one earned free empire respec (the stock an empire level pays out)
/// or souls through <see cref="RespecPolicy.PriceOf"/>. A unique-creature or commander respec has no
/// free option (R18's correction: a commander pays) and never asks.
///
/// <para>The player chooses; nothing defaults. A caller that could pay either way and names neither is
/// refused by the store (`respec.payment.choice-required`) rather than silently charged or silently
/// spent — the two are not interchangeable, and picking one for the player is the failure this enum
/// exists to prevent.</para>
/// </summary>
public enum RespecPayment
{
    /// <summary>Charged through the soul ledger at <see cref="RespecPolicy.PriceOf"/>'s price.</summary>
    Souls,

    /// <summary>Paid with one earned free empire respec, spent from the empire's stock ledger.</summary>
    FreeRespec,
}

/// <summary>The wire spellings of <see cref="RespecPayment"/>, and their one parser. The route layer
/// (`SpeciesBuildEndpoints`) passes these strings through; the store takes the enum, so the spelling
/// lives in exactly one place.</summary>
public static class RespecPayments
{
    public const string Souls = "souls";
    public const string FreeRespec = "freeRespec";

    /// <summary>Parses a caller's spelling. Unknown text is REFUSED (false), never mapped to a default:
    /// a typo must not become a charge the player did not choose.</summary>
    public static bool TryParse(string? raw, out RespecPayment payment)
    {
        switch (raw?.Trim())
        {
            case Souls: payment = RespecPayment.Souls; return true;
            case FreeRespec: payment = RespecPayment.FreeRespec; return true;
            default: payment = default; return false;
        }
    }
}

/// <summary>The three numbers <see cref="RespecPolicy.PriceOf"/> reads — re-typed off the whole
/// <see cref="SpeciesBuildTuning"/> record (EP1.6, spec-specimen-respec-price.md "One price function,
/// re-typed to take its parameters") so a unique-creature respec can price against its own working
/// values without a second formula. <see cref="SpeciesBuildTuning.SpeciesRespec"/> and
/// <see cref="SpeciesBuildTuning.UniqueRespec"/> are the two shipped views onto this shape — two
/// parameter sets, one line.</summary>
public sealed record RespecPriceTuning(long BasePrice, long EscalationPermille, int DecayDays);

/// <summary>A priced quote plus the free stock available against it (EP1.6). Every unique-creature or
/// commander quote passes <c>freeStock: 0</c> — R18 gives free stock only to the species/empire respec
/// (`respec-free-counter`'s own caller); this type exists once so both readings share it.</summary>
public readonly record struct RespecQuote(RespecPrice Souls, long FreeStock)
{
    public bool FreeAvailable => FreeStock > 0;
}

public static class RespecPolicy
{
    /// <summary>`price(count) = basePrice + basePrice × count × escalationPermille / 1000` — linear,
    /// not geometric (spec's own reasoning: geometric escalation against a flat soul faucet is how a
    /// price becomes a ceiling, and soul income is flat today per <c>RpgStore.Souls.cs</c>'s Θ pin).
    /// Widened to `long` throughout and divided by 1000 last, exactly once (docs/architecture/numeric-types.md's overflow
    /// rules); <c>checked</c> so a runaway count throws rather than wraps. Always available, always
    /// priced, never refused — there is no "cannot respec" return here on purpose, exactly like the
    /// policy this replaces.</summary>
    public static RespecPrice PriceOf(RespecPriceTuning tuning, long count)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "respec count cannot be negative");

        checked
        {
            var amount = tuning.BasePrice
                + tuning.BasePrice * count * tuning.EscalationPermille / 1000;
            return new RespecPrice(RespecResource.Soul, amount);
        }
    }

    /// <summary>The read-side twin of <see cref="PriceOf"/> — a price plus whatever free stock the
    /// caller is holding, so a preview and a spend can share one shape
    /// (spec-specimen-respec-price.md "One price function, re-typed"). This module's own callers
    /// (`RpgStore.AllocationRespec.cs`) always pass <c>freeStock: 0</c>.</summary>
    public static RespecQuote Quote(RespecPriceTuning tuning, long effectiveCount, long freeStock)
    {
        if (freeStock < 0)
            throw new ArgumentOutOfRangeException(nameof(freeStock), freeStock, "free stock cannot be negative");
        return new RespecQuote(PriceOf(tuning, effectiveCount), freeStock);
    }

    /// <summary>A re-allocation is a respec when it takes points back: some aptitude holds fewer
    /// points in <paramref name="proposed"/> than in <paramref name="current"/>, within
    /// <paramref name="scope"/> alone (spec-specimen-respec-price.md "What counts as a respec").
    /// Spending unspent points — every aptitude unchanged or higher — is not a respec, so a first
    /// allocation (from <see cref="AptitudeAllocation.Empty"/>) is always free.</summary>
    public static bool IsRespec(AllocationScope scope, AptitudeAllocation current, AptitudeAllocation proposed)
    {
        if (current is null) throw new ArgumentNullException(nameof(current));
        if (proposed is null) throw new ArgumentNullException(nameof(proposed));

        foreach (var apt in AptitudeCatalog.All)
            if (proposed.PointsAt(scope, apt.Id) < current.PointsAt(scope, apt.Id))
                return true;
        return false;
    }
}
