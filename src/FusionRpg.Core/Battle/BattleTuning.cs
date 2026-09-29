using System.Text.Json;

namespace FusionRpg.Core.Battle;

public sealed record TraitMagnitudes(
    int BerserkRampHalfMilli = 0, int BerserkRampQuarterMilli = 0, int RegenPerRoundMilli = 0,
    int OnKillHealMilli = 0, int GuardShareMilli = 0, int InitiativeBonusMilli = 0,
    int DeathRefusalCharges = 0, int RetreatBelowMilli = 0, int SoulLootBonusMilli = 0,
    int SpecimenXpBonusMilli = 0, int EssenceProcMilli = 0, int EssenceRiderMilli = 0,
    IReadOnlyList<BattleStatusSpec>? OnHitRiders = null);

/// <summary>The MAGNITUDES of one battle-mode profile (battle-timeline T14/B29,
/// spec-timeline-tunables.md §2). Deliberately only the numbers: <c>AdvancePolicy</c>,
/// <c>WScope</c>, <c>DefaultCommitment</c>, the economy TYPE and the profile ids all stay in
/// <see cref="Timeline.BattleModeProfileCatalog"/> because they are *which mechanism runs*, not how
/// much of it — the map's "adding a mode adds a row, never a branch" acceptance is about that row's
/// shape, and a row of structure in code is still a row.
///
/// <para><paramref name="MaxPoints"/> is null for profiles whose economy has no budget
/// (<c>OneActionPerTurnEconomy</c>); supplying it for one of those is refused at load rather than
/// silently ignored.</para>
///
/// <para><b>base-defense F2 / battle-clock-profile:</b> <paramref name="MaxRounds"/> and
/// <paramref name="RoundDurationMs"/> are the battle's round horizon, moved here from the
/// engine-global <see cref="BattleRuleset"/> — a siege needs a longer horizon than a squad fight, and
/// with the value global, giving one gives all. <b>Null means "inherit the ruleset"</b>, which is
/// what keeps <c>classic-round</c> byte-identical without a special case: it names neither, so it
/// inherits both, so <see cref="BattleModeProfileCatalog"/>'s resolved
/// <see cref="Timeline.BattleModeProfile.MaxRounds"/> equals <see cref="BattleRuleset.MaxRounds"/>
/// exactly.</para></summary>
public sealed record TimelineProfileTuning(
    int W, int WReact, long PassQuantum, long? MaxPoints,
    int? MaxRounds = null, int? RoundDurationMs = null);

/// <summary>Battle balance surface (tunables-ssot.md T1) — round/affinity constants plus every
/// trait's magnitudes. Trait ids/mechanisms stay in <see cref="TraitBattleCatalog"/> (schema).</summary>
public sealed record BattleTuning(
    int SchemaVersion, int Version,
    int RoundDurationMs, int MaxRounds,
    int PrimaryAffinityDivisor, int SecondaryAffinityDivisor,
    IReadOnlyDictionary<string, TraitMagnitudes> Traits,
    IReadOnlyDictionary<string, TimelineProfileTuning> TimelineProfiles,
    int HybridSecondaryWeightMilli,
    /// <summary>
    /// base-defense F2 (audit C1) — the belt-and-suspenders iteration cap on <c>BattleEngine.Resolve</c>
    /// scaled to a battle's OWN horizon rather than hard-coded to classic-round's 50 rounds.
    /// <b>Structural, not balance</b> — AGENTS.md's per-frame/runtime-cap exemption; it bounds one
    /// resolve's scheduling work, never a progression ceiling. Lives in config anyway because
    /// <c>200_000 / 50 = 4000</c> must stay derivable from the shipped 50-round default rather than
    /// being a second, disconnected magic constant living in code.
    /// </summary>
    int LoopGuardRoundMultiple,
    /// <summary>
    /// `battle-tempo` `tempo-content` (spec-tempo-content.md §2.1) — the reference tempo
    /// <see cref="Battle.SpeciesTempoProjection.SpeedFor"/> projects every species' interval against:
    /// <c>turn.speed = TurnDefaultSpeed × ReferenceIntervalMs / attackIntervalMs</c>. The ONE new
    /// number this module introduces; every other value it reads (`attackTempoIntervalMs`,
    /// `TurnDefaultSpeed`) already exists elsewhere. `long`, milliseconds — a magnitude the ladder
    /// does not scale (it is a fixed content anchor, not a `Θ`-driven value), but `long` throughout
    /// this file's own convention regardless.
    /// </summary>
    long SpeciesTempoReferenceIntervalMs)
{
    public TraitMagnitudes TraitOf(string traitId) =>
        Traits.TryGetValue(traitId, out var m) ? m : new TraitMagnitudes();

    /// <summary>Refuses rather than defaults: a profile the catalog knows about but config forgot is
    /// a missing balance row, not a request for a built-in fallback.</summary>
    public TimelineProfileTuning ProfileOf(string profileId) =>
        TimelineProfiles.TryGetValue(profileId, out var p) ? p : throw new BattleTuningRejection(
            $"battle tuning: no timeline.profiles entry for '{profileId}'. Every profile the catalog " +
            "ships must carry its magnitudes in config — there is no built-in default to fall back to.");
}

public sealed class BattleTuningRejection : Exception
{
    public BattleTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class BattleTuningLoader
{
    public static BattleTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new BattleTuningRejection("battle tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new BattleTuningRejection($"battle tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var ruleset = Obj(root, "ruleset");
            var composer = Obj(root, "statComposer");
            var traitsEl = Obj(root, "traits");

            var traits = new Dictionary<string, TraitMagnitudes>(StringComparer.Ordinal);
            foreach (var prop in traitsEl.EnumerateObject())
            {
                var t = prop.Value;
                traits[prop.Name] = new TraitMagnitudes(
                    BerserkRampHalfMilli: IntOr(t, "berserkRampHalfMilli"),
                    BerserkRampQuarterMilli: IntOr(t, "berserkRampQuarterMilli"),
                    RegenPerRoundMilli: IntOr(t, "regenPerRoundMilli"),
                    OnKillHealMilli: IntOr(t, "onKillHealMilli"),
                    GuardShareMilli: IntOr(t, "guardShareMilli"),
                    InitiativeBonusMilli: IntOr(t, "initiativeBonusMilli"),
                    DeathRefusalCharges: IntOr(t, "deathRefusalCharges"),
                    RetreatBelowMilli: IntOr(t, "retreatBelowMilli"),
                    SoulLootBonusMilli: IntOr(t, "soulLootBonusMilli"),
                    SpecimenXpBonusMilli: IntOr(t, "specimenXpBonusMilli"),
                    EssenceProcMilli: IntOr(t, "essenceProcMilli"),
                    EssenceRiderMilli: IntOr(t, "essenceRiderMilli"),
                    OnHitRiders: Riders(t, prop.Name));
            }

            var timelineEl = Obj(root, "timeline");
            var profilesEl = Obj(timelineEl, "profiles");
            var profiles = new Dictionary<string, TimelineProfileTuning>(StringComparer.Ordinal);
            foreach (var prop in profilesEl.EnumerateObject())
            {
                var v = prop.Value;
                if (v.ValueKind != JsonValueKind.Object)
                    throw new BattleTuningRejection($"battle tuning: timeline.profiles.{prop.Name} is not an object");
                // base-defense F2: MaxRounds/RoundDurationMs are OPTIONAL per profile — absent means
                // "content did not choose", inherited from `ruleset.*` at resolve time
                // (BattleModeProfileCatalog.Build). Present-but-invalid still fails loudly here, the
                // same stance every other field in this profile row already takes.
                int? maxRounds = v.TryGetProperty("maxRounds", out var mr) && mr.ValueKind == JsonValueKind.Number
                    ? mr.GetInt32() : null;
                int? roundDurationMs = v.TryGetProperty("roundDurationMs", out var rd) && rd.ValueKind == JsonValueKind.Number
                    ? rd.GetInt32() : null;

                profiles[prop.Name] = new TimelineProfileTuning(
                    W: Int(v, "w"),
                    WReact: Int(v, "wReact"),
                    PassQuantum: Int(v, "passQuantum"),
                    MaxPoints: v.TryGetProperty("maxPoints", out var mp) && mp.ValueKind == JsonValueKind.Number
                        ? mp.GetInt64()
                        : null,
                    MaxRounds: maxRounds,
                    RoundDurationMs: roundDurationMs);
                if (profiles[prop.Name].W <= 0)
                    throw new BattleTuningRejection($"battle tuning: timeline.profiles.{prop.Name}.w must be > 0 (it is a slot count); got {profiles[prop.Name].W}");
                if (profiles[prop.Name].PassQuantum <= 0)
                    throw new BattleTuningRejection($"battle tuning: timeline.profiles.{prop.Name}.passQuantum must be > 0 (a zero quantum reschedules at `now` forever); got {profiles[prop.Name].PassQuantum}");
                if (maxRounds is <= 0)
                    throw new BattleTuningRejection($"battle tuning: timeline.profiles.{prop.Name}.maxRounds must be > 0 when present (0 would silently produce a battle with no rounds); got {maxRounds}");
                if (roundDurationMs is <= 0)
                    throw new BattleTuningRejection($"battle tuning: timeline.profiles.{prop.Name}.roundDurationMs must be > 0 when present; got {roundDurationMs}");
            }

            var loopGuardRoundMultiple = Int(ruleset, "loopGuardRoundMultiple");
            if (loopGuardRoundMultiple <= 0)
                throw new BattleTuningRejection(
                    $"battle tuning: ruleset.loopGuardRoundMultiple must be > 0 (it is a per-round " +
                    $"scheduling-work bound); got {loopGuardRoundMultiple}");

            var speciesTempo = Obj(root, "speciesTempo");
            var referenceIntervalMs = Long(speciesTempo, "referenceIntervalMs");
            if (referenceIntervalMs <= 0)
                throw new BattleTuningRejection(
                    $"battle tuning: speciesTempo.referenceIntervalMs must be > 0 (it is a projection's " +
                    $"denominator scale); got {referenceIntervalMs}");

            return new BattleTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                RoundDurationMs: Int(ruleset, "roundDurationMs"),
                MaxRounds: Int(ruleset, "maxRounds"),
                PrimaryAffinityDivisor: Int(composer, "primaryAffinityDivisor"),
                SecondaryAffinityDivisor: Int(composer, "secondaryAffinityDivisor"),
                Traits: traits,
                TimelineProfiles: profiles,
                HybridSecondaryWeightMilli: HybridWeight(root),
                LoopGuardRoundMultiple: loopGuardRoundMultiple,
                SpeciesTempoReferenceIntervalMs: referenceIntervalMs);
        }
    }

    /// <summary>Wave E3 — the secondary element's share of the payload, per-mille. Bounded 0..1000 by
    /// nature (it is a share of one payload, and the primary carries the remainder), so the bound is a
    /// structural refusal rather than a progression cap: a share above 1000 would give the primary a
    /// negative weight, which is not a balance outcome but a nonsense payload.</summary>
    static int HybridWeight(JsonElement root)
    {
        var hybrid = Obj(root, "hybrid");
        var w = Int(hybrid, "secondaryWeightMilli");
        if (w < 0 || w > 1000)
            throw new BattleTuningRejection(
                $"battle tuning: hybrid.secondaryWeightMilli must be within 0..1000 (it is a share of one payload); got {w}");
        return w;
    }

    /// <summary>
    /// Wave E1 — a trait's authored on-hit riders. Absent means none, which is every shipped trait.
    /// Reuses <see cref="BattleStatusSpec"/>'s own field names and its defaults (`periodMs` 1000,
    /// `grantChanceMilli` 1000), so a rider is authored the same way an initial status already is.
    /// </summary>
    static IReadOnlyList<BattleStatusSpec>? Riders(JsonElement trait, string traitId)
    {
        if (!trait.TryGetProperty("onHitRiders", out var arr)) return null;
        if (arr.ValueKind != JsonValueKind.Array)
            throw new BattleTuningRejection($"battle tuning: traits.{traitId}.onHitRiders is not an array");

        var list = new List<BattleStatusSpec>();
        foreach (var r in arr.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
                throw new BattleTuningRejection($"battle tuning: traits.{traitId}.onHitRiders has a non-object entry");
            if (!r.TryGetProperty("statusId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                throw new BattleTuningRejection($"battle tuning: traits.{traitId}.onHitRiders entry has no 'statusId'");

            var chance = r.TryGetProperty("grantChanceMilli", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32() : 1000;
            if (chance < 0 || chance > 1000)
                throw new BattleTuningRejection(
                    $"battle tuning: traits.{traitId}.onHitRiders grantChanceMilli must be within 0..1000; got {chance}");

            var period = r.TryGetProperty("periodMs", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 1000;
            if (period <= 0)
                throw new BattleTuningRejection(
                    $"battle tuning: traits.{traitId}.onHitRiders periodMs must be > 0 (a zero period pulses forever); got {period}");

            list.Add(new BattleStatusSpec(
                idEl.GetString()!,
                MagnitudePerPulse: r.TryGetProperty("magnitudePerPulse", out var mg) && mg.ValueKind == JsonValueKind.Number ? mg.GetInt64() : 0,
                DurationMs: r.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
                PeriodMs: period,
                GrantChanceMilli: chance));
        }

        return list;
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new BattleTuningRejection($"battle tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new BattleTuningRejection($"battle tuning: missing or non-integer '{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new BattleTuningRejection($"battle tuning: missing or non-integer '{key}'");
        return v;
    }

    /// <summary>Trait rows only carry the fields they use — absent means the def's own zero default.</summary>
    static int IntOr(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v : 0;
}

/// <summary>Fans one battle.v{n}.json load out to every class that reads it (tunables-ssot.md §7.2 —
/// hosts inject once; TraitBattleCatalog/BattleRuleset/BattleHubCompose stay independently testable).</summary>
public static class BattleTuningHub
{
    static BattleTuning? _tuning;

    public static void Configure(BattleTuning tuning)
    {
        TraitBattleCatalog.Configure(tuning);
        BattleRuleset.Configure(tuning);
        Timeline.BattleModeProfileCatalog.Configure(tuning);
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    /// <summary>battle-hub-fuse T6 — re-homed off the deleted <c>BattleStatComposer.TuningInstance</c>:
    /// the affinity divisors and tempo reference interval the Hub subsystems read directly, same
    /// instance <see cref="Configure"/> just fanned out.</summary>
    public static BattleTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "BattleTuningHub.Configure(...) has not run. The affinity divisors read " +
        "data/tuning/battle.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
