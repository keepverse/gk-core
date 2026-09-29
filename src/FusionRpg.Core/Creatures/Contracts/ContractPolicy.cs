using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Creatures.Contracts;

/// <summary>Where a creature's loyalty sits. Derived from the number, never stored — a policy change
/// must never leave a stale rank in the database.</summary>
public enum LoyaltyRank
{
    /// <summary>Under the deploy floor: owned, contracted, and refusing to be fielded.</summary>
    Insubordinate,
    Bound,
    Sworn,
    Trusted,
    Devoted
}

/// <summary>Rolled per specimen; scales how fast loyalty is earned, lost, and what tribute it demands.</summary>
public enum CreaturePersonality
{
    Loyal,
    Stoic,
    Proud,
    Calculating,
    Feral
}

/// <summary>Integer percentages, applied as <c>x * pct / 100</c>.</summary>
public sealed record PersonalityRates(int GainPct, int DecayPct, int UpkeepPct);

public static class CreaturePersonalityIds
{
    public static string ToId(this CreaturePersonality personality) => personality switch
    {
        CreaturePersonality.Loyal => "loyal",
        CreaturePersonality.Stoic => "stoic",
        CreaturePersonality.Proud => "proud",
        CreaturePersonality.Calculating => "calculating",
        CreaturePersonality.Feral => "feral",
        _ => throw new ArgumentOutOfRangeException(nameof(personality), personality, null)
    };

    public static bool TryParse(string? value, out CreaturePersonality personality)
    {
        switch (value)
        {
            case "loyal": personality = CreaturePersonality.Loyal; return true;
            case "stoic": personality = CreaturePersonality.Stoic; return true;
            case "proud": personality = CreaturePersonality.Proud; return true;
            case "calculating": personality = CreaturePersonality.Calculating; return true;
            case "feral": personality = CreaturePersonality.Feral; return true;
            default: personality = CreaturePersonality.Loyal; return false;
        }
    }
}

/// <summary>
/// Contract rules (spec-creature-contracts.md, owner locks 2026-08-21). Pure integers, no clock of its
/// own — the store passes the time in. Numbers live in <c>data/tuning/contracts.v{n}.json</c>
/// (tunables-ssot.md T1); <see cref="Configure"/> must run before any rule below is read.
///
/// The one property worth stating out loud: a fresh contract lands in the <see cref="LoyaltyRank.Bound"/>
/// band, which pays +0‰. Adopting contracts therefore cannot move a single battle or expedition
/// golden — loyalty only starts paying once a creature has earned Sworn.
/// </summary>
public static class ContractPolicy
{
    public const int PolicyVersion = 1;

    static ContractTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction) — never called
    /// from Core itself (tunables-ssot.md §7.2: Core takes a loaded object, it does not load one).</summary>
    public static void Configure(ContractTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static ContractTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ContractPolicy.Configure(...) has not run. Every contract rule reads data/tuning/" +
        "contracts.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static int LoyaltyMax => Tuning.Loyalty.Max;
    /// <summary>Below this a creature cannot be fielded. Decay stops here; only defeats cross it.</summary>
    public static int DeployFloor => Tuning.Loyalty.DeployFloor;
    public static int BindLoyalty => Tuning.Loyalty.BindLoyalty;

    public static int WinGain => Tuning.Loyalty.WinGain;
    public static int LossPenalty => Tuning.Loyalty.LossPenalty;
    public static int DailyGainCap => Tuning.Loyalty.DailyGainCap;
    public static int DecayPerDay => Tuning.Loyalty.DecayPerDay;
    public static int RitualGain => Tuning.Loyalty.RitualGain;

    public static int BaseSlots => Tuning.Slots.BaseSlots;
    public static int SlotPriceStep => Tuning.Slots.SlotPriceStep;

    /// <summary>A six-month absence settles thirty days: bounded work, bounded bill.</summary>
    public static int MaxSettleDays => Tuning.Settlement.MaxSettleDays;

    public static LoyaltyRank RankFor(int loyalty) => loyalty switch
    {
        _ when loyalty < DeployFloor => LoyaltyRank.Insubordinate,
        _ when loyalty < Tuning.Loyalty.SwornThreshold => LoyaltyRank.Bound,
        _ when loyalty < Tuning.Loyalty.TrustedThreshold => LoyaltyRank.Sworn,
        _ when loyalty < Tuning.Loyalty.DevotedThreshold => LoyaltyRank.Trusted,
        _ => LoyaltyRank.Devoted
    };

    /// <summary>Per-mille bonus on the creature's OWN combat channels, applied at squad build.</summary>
    public static int RankBonusMilli(LoyaltyRank rank) => rank switch
    {
        LoyaltyRank.Sworn => Tuning.Loyalty.RankBonusSwornMilli,
        LoyaltyRank.Trusted => Tuning.Loyalty.RankBonusTrustedMilli,
        LoyaltyRank.Devoted => Tuning.Loyalty.RankBonusDevotedMilli,
        _ => 0
    };

    public static bool IsDeployable(int loyalty) => loyalty >= DeployFloor;

    public static PersonalityRates Rates(CreaturePersonality personality)
    {
        if (!Tuning.PersonalityRates.TryGetValue(personality, out var r))
            throw new ArgumentOutOfRangeException(nameof(personality), personality, null);
        return new PersonalityRates(r.GainPct, r.DecayPct, r.UpkeepPct);
    }

    public static int BaseUpkeepPerDay(CreatureRarity rarity)
    {
        if (!Tuning.BaseUpkeepPerDay.TryGetValue(rarity, out var v))
            throw new ArgumentOutOfRangeException(nameof(rarity), rarity, null);
        return v;
    }

    /// <summary>Daily tribute for one bound creature. Truncation favours the player, but never to zero —
    /// a free creature would make the whole capacity economy optional.</summary>
    public static int UpkeepPerDay(CreatureRarity rarity, CreaturePersonality personality) =>
        Math.Max(1, BaseUpkeepPerDay(rarity) * Rates(personality).UpkeepPct / 100);

    public static int DecayPerDayFor(CreaturePersonality personality) =>
        DecayPerDay * Rates(personality).DecayPct / 100;

    /// <summary>One unpaid day. Never drops a creature below the floor, and never pushes a creature that
    /// is already under it (defeats put it there) any lower.</summary>
    public static int ApplyDecay(int loyalty, CreaturePersonality personality) =>
        loyalty <= DeployFloor ? loyalty : Math.Max(DeployFloor, loyalty - DecayPerDayFor(personality));

    /// <summary>Credits a win against the creature's rolling daily window.</summary>
    public static (int Loyalty, int GainToday) ApplyGain(
        int loyalty, int gainToday, int baseGain, CreaturePersonality personality)
    {
        var scaled = baseGain * Rates(personality).GainPct / 100;
        var credited = Math.Max(0, Math.Min(scaled, DailyGainCap - gainToday));
        return (Math.Min(LoyaltyMax, loyalty + credited), gainToday + credited);
    }

    /// <summary>A defeat. Uncapped by the daily window, and the only thing that can cross the floor.</summary>
    public static int ApplyLoss(int loyalty) => Math.Max(0, loyalty - LossPenalty);

    public static int RitualGainFor(CreaturePersonality personality) =>
        RitualGain * Rates(personality).GainPct / 100;

    /// <summary>Scaled by <see cref="SoulSinkPolicy"/> so the price tracks the faucet that pays for
    /// it (PS-5). <paramref name="thetaContent"/> is required, never defaulted.</summary>
    public static long RitualPrice(CreatureRarity rarity, int thetaContent, FusionRpg.Core.Power.PowerTuning tuning)
    {
        if (!Tuning.RitualPriceSouls.TryGetValue(rarity, out var v))
            throw new ArgumentOutOfRangeException(nameof(rarity), rarity, null);
        return SoulSinkPolicy.Price(v, thetaContent, tuning);
    }

    /// <summary>T3.6 (spec-caps-reconcile.md §2.3, SSOT §11.1a): no ceiling — the escalating price
    /// (see <see cref="NextSlotPrice"/>) was always the real scarcity control, not this `Math.Min`.
    /// A roster of 2,012 costs 600,300,000 cumulative souls; that is the limit, not a hard-coded 48.</summary>
    public static int Capacity(int purchasedSlots) => BaseSlots + Math.Max(0, purchasedSlots);

    /// <summary>The escalating slot price, then scaled by <see cref="SoulSinkPolicy"/> so it tracks
    /// the faucet (PS-5). The escalation is the scarcity control; the scale keeps it meaningful as
    /// income grows.</summary>
    public static long NextSlotPrice(int purchasedSlots, int thetaContent, FusionRpg.Core.Power.PowerTuning tuning) =>
        SoulSinkPolicy.Price((long)SlotPriceStep * (Math.Max(0, purchasedSlots) + 1), thetaContent, tuning);

    /// <summary>Always true post-T3.6 — kept as a named check (rather than removed outright) because
    /// the store's buy-slot gate and the contracts API both call it, and "can this purchase ever
    /// succeed" remains a meaningful question even though nothing refuses it today.</summary>
    public static bool CanBuySlot(int purchasedSlots) => true;

    /// <summary>Whole UTC days between two stamps, clamped to <see cref="MaxSettleDays"/>. Day-quantised,
    /// not 24-hour windows: a minute past midnight UTC is a new tribute day. A stamp in the future
    /// (the SIM clock hook travels forward) bills nothing.</summary>
    public static int ElapsedDays(DateTimeOffset lastSettled, DateTimeOffset now)
    {
        var days = (now.UtcDateTime.Date - lastSettled.UtcDateTime.Date).Days;
        return Math.Clamp(days, 0, MaxSettleDays);
    }

    /// <summary>Personality is a property of the specimen, derived from its id through the owned PRNG —
    /// so creatures minted before this module existed have one too, and it never drifts.</summary>
    public static CreaturePersonality PersonalityFor(string instanceId) =>
        (CreaturePersonality)(int)(SeededRng.DeriveStream(0UL, instanceId ?? "").NextULong() % 5UL);
}
