namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>What the ladder suggests: which rule won and the twelve target shares it implies.
/// Points are the caller's business, via the scope's existing math (spec-assign-ladder.md, map D3).
/// </summary>
public sealed record AssignSuggestion(
    string RuleId,                                   // closed: AptitudeAutoAssignRules
    IReadOnlyList<AptitudePresetRowSpec> Rows,       // 12 rows; permille; favour/even/posture sum to 1000
    IReadOnlyList<AssignSkip> Skipped);              // every earlier rung and its named reason

public sealed record AssignSkip(string RuleId, string Reason);

public sealed record AssignContext(
    IReadOnlyList<AptitudePresetRowSpec>? ActivePresetRows,
    IReadOnlyDictionary<string, long>? SpeciesFavourPermille,    // a plan row, 0..12 keys
    Posture? SpeciesPosture,
    bool FavourAllowed);                                         // Mode C passes false (spec E-rules)

/// <summary>The walk order — DATA (`aptitude-presets.v2.json`'s `assignLadder.order`, EP1.3). Defined
/// here rather than in <c>AptitudePresetTuning.cs</c> because <see cref="AssignLadder.Suggest"/> needs
/// the shape before that file's loader/validation exists; EP1.3 parses a JSON document into this same
/// record and enforces the load contract (every id known, no duplicates, last id is "even").</summary>
public sealed record AssignLadderTuning(IReadOnlyList<string> Order);

/// <summary>
/// spec-assign-ladder.md — an ordered walk over the closed rule set (<see cref="AptitudeAutoAssignRules"/>),
/// with "posture" as a fourth token that RESOLVES to one of the three posture rule ids from the actor's
/// own primary aptitude; it is not a seventh rule id. Returns the first rung that succeeds, plus every
/// earlier rung and its own named reason — no skip is silent. Total: the terminal rung is "even", which
/// cannot fail, so a legally loaded <see cref="AssignLadderTuning"/> (EP1.3's load contract) always
/// produces a suggestion.
/// </summary>
public static class AssignLadder
{
    public const string PostureRung = "posture";

    /// <summary>The closed vocabulary a tuning `order` array may name (EP1.3's load contract) — the
    /// four walk tokens, never the six raw <see cref="AptitudeAutoAssignRules"/> ids: `posture` is the
    /// resolver token, not `posture-force`/`posture-finesse`/`posture-bastion` directly.</summary>
    public static readonly IReadOnlyList<string> KnownRungs = new[]
    {
        AptitudeAutoAssignRules.ActivePreset,
        AptitudeAutoAssignRules.SpeciesFavour,
        PostureRung,
        AptitudeAutoAssignRules.Even,
    };

    public static AssignSuggestion Suggest(AssignContext ctx, AssignLadderTuning tuning)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var skipped = new List<AssignSkip>();
        foreach (var rung in tuning.Order)                  // data; validated to end on "even"
        {
            var attempt = TryRung(rung, ctx);
            if (attempt.Ok) return new AssignSuggestion(attempt.RuleId, attempt.Rows, skipped);
            skipped.Add(new AssignSkip(rung, attempt.Reason));   // never a silent skip
        }
        // Unreachable given EP1.3's load contract: the last rung is "even", and even cannot fail.
        throw new InvalidOperationException("assign ladder did not terminate on 'even'");
    }

    /// <summary>Runs exactly one rule directly, bypassing the walk and its skip list — what the
    /// explicit auto-assign button needs (`POST /suggest` with `rule` given, spec-assign-ladder.md
    /// "One implementation"). Unlike a tuning `order` entry, this also accepts the three raw
    /// posture-* ids directly (a button naming a posture explicitly, not resolving one from the
    /// actor), which is why this is a separate entry point from the walk's own closed
    /// <see cref="KnownRungs"/> vocabulary.</summary>
    public static (bool Ok, IReadOnlyList<AptitudePresetRowSpec> Rows, string Reason) TryOne(string ruleId, AssignContext ctx)
    {
        if (ruleId is null) throw new ArgumentNullException(nameof(ruleId));
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        var attempt = TryRung(ruleId, ctx);
        return (attempt.Ok, attempt.Rows, attempt.Reason);
    }

    static RungAttempt TryRung(string rung, AssignContext ctx) => rung switch
    {
        AptitudeAutoAssignRules.ActivePreset => TryActivePreset(ctx),
        AptitudeAutoAssignRules.SpeciesFavour => TrySpeciesFavour(ctx),
        PostureRung => TryPostureFromContext(ctx),
        AptitudeAutoAssignRules.PostureForce => RungAttempt.Success(AptitudeAutoAssignRules.PostureForce, PostureRows(Posture.Force)),
        AptitudeAutoAssignRules.PostureFinesse => RungAttempt.Success(AptitudeAutoAssignRules.PostureFinesse, PostureRows(Posture.Finesse)),
        AptitudeAutoAssignRules.PostureBastion => RungAttempt.Success(AptitudeAutoAssignRules.PostureBastion, PostureRows(Posture.Bastion)),
        AptitudeAutoAssignRules.Even => RungAttempt.Success(AptitudeAutoAssignRules.Even, EvenRows()),
        _ => RungAttempt.Fail("assignLadder.rung.unknown"),
    };

    static RungAttempt TryActivePreset(AssignContext ctx) =>
        ctx.ActivePresetRows is { Count: > 0 } rows
            ? RungAttempt.Success(AptitudeAutoAssignRules.ActivePreset, rows)
            : RungAttempt.Fail("autoAssign.activePreset.missing");

    /// <summary>Same zero-fill and refusal-reason contract as
    /// <see cref="AptitudeAutoAssign.FillFromPermille"/> (EP1.1, W3) — a real plan row's missing keys
    /// are zero-filled, never a refusal. This method returns the raw permille ROWS (the ladder's
    /// distribution), never points; a caller materializes against its own budget.</summary>
    static RungAttempt TrySpeciesFavour(AssignContext ctx)
    {
        if (!ctx.FavourAllowed)
            return RungAttempt.Fail("autoAssign.favour.modeC");

        var favour = ctx.SpeciesFavourPermille;
        if (favour is null || favour.Count == 0)
            return RungAttempt.Fail("autoAssign.favour.empty");

        long sum = 0;
        foreach (var (id, pm) in favour)
        {
            if (!AptitudeCatalog.IsAptitudeId(id))
                return RungAttempt.Fail("autoAssign.favour.unknownAptitude");
            checked { sum += pm; }
        }
        if (sum != AptitudePresetMaterialize.RequiredPermilleSum)
            return RungAttempt.Fail("autoAssign.favour.notNormalised");

        var rows = new List<AptitudePresetRowSpec>(AptitudeCatalog.Count);
        foreach (var apt in AptitudeCatalog.All)
        {
            var pm = favour.TryGetValue(apt.Id, out var v) ? v : 0L;
            rows.Add(new AptitudePresetRowSpec(apt.Id, pm));
        }
        return RungAttempt.Success(AptitudeAutoAssignRules.SpeciesFavour, rows);
    }

    /// <summary>The "posture" resolver token: resolves the actor's own known posture to one of the
    /// three real posture rule ids. `autoAssign.posture.unknown` when the actor has no known primary
    /// (e.g. no species, or an unplanned one) — the walk then falls through to `even`.</summary>
    static RungAttempt TryPostureFromContext(AssignContext ctx)
    {
        if (ctx.SpeciesPosture is not { } posture)
            return RungAttempt.Fail("autoAssign.posture.unknown");

        var ruleId = posture switch
        {
            Posture.Force => AptitudeAutoAssignRules.PostureForce,
            Posture.Finesse => AptitudeAutoAssignRules.PostureFinesse,
            Posture.Bastion => AptitudeAutoAssignRules.PostureBastion,
            _ => throw new ArgumentOutOfRangeException(nameof(ctx), posture, "unknown Posture"),
        };
        return RungAttempt.Success(ruleId, PostureRows(posture));
    }

    /// <summary>Leans the whole 1000‰ into `posture`'s four aptitudes (250‰ each —
    /// <see cref="AptitudeCatalog.PerPosture"/> divides 1000 exactly, so no remainder rule is needed
    /// here, unlike <see cref="EvenRows"/>). Shared by the "posture" resolver and a direct
    /// `posture-force`/`-finesse`/`-bastion` call (<see cref="TryOne"/>), which names the posture
    /// itself rather than reading it from the context.</summary>
    static IReadOnlyList<AptitudePresetRowSpec> PostureRows(Posture posture)
    {
        checked
        {
            var each = AptitudePresetMaterialize.RequiredPermilleSum / AptitudeCatalog.PerPosture; // 250
            return AptitudeCatalog.All
                .Select(apt => new AptitudePresetRowSpec(apt.Id, apt.Posture == posture ? each : 0L))
                .ToList();
        }
    }

    /// <summary>Even ‰ across the twelve, summing to exactly 1000 — mirrors the FE editor-seed helper
    /// (`evenPermille.ts`'s `evenPermilleRows`): 1000/12 truncates to 83 with a remainder of 4, and the
    /// first four aptitudes in <see cref="AptitudeCatalog.All"/>'s own ordinal order carry the extra
    /// 1‰ each — never a fifth favoured aptitude, and no RNG.</summary>
    static IReadOnlyList<AptitudePresetRowSpec> EvenRows()
    {
        checked
        {
            var baseShare = AptitudePresetMaterialize.RequiredPermilleSum / AptitudeCatalog.Count; // 83
            var remainder = AptitudePresetMaterialize.RequiredPermilleSum - baseShare * AptitudeCatalog.Count; // 4

            var rows = new List<AptitudePresetRowSpec>(AptitudeCatalog.Count);
            var i = 0;
            foreach (var apt in AptitudeCatalog.All)
            {
                var pm = baseShare + (i < remainder ? 1 : 0);
                rows.Add(new AptitudePresetRowSpec(apt.Id, pm));
                i++;
            }
            return rows;
        }
    }

    readonly record struct RungAttempt(bool Ok, string RuleId, IReadOnlyList<AptitudePresetRowSpec> Rows, string Reason)
    {
        public static RungAttempt Success(string ruleId, IReadOnlyList<AptitudePresetRowSpec> rows) =>
            new(true, ruleId, rows, "");

        public static RungAttempt Fail(string reason) =>
            new(false, "", Array.Empty<AptitudePresetRowSpec>(), reason);
    }
}
