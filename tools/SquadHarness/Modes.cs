namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// F2 (todo.md "F2: squad-harness S1b"): CLI-facing argument parsing and roster construction, shared by
/// every mode in <see cref="MeasurementModes"/>. This file carries the option-parsing helpers F1/F1b
/// already built (<see cref="ParseAllocationShape"/>, <see cref="BuildDuelRoster"/>,
/// <see cref="BuildSquadRoster"/>, <see cref="RosterScope"/>/<see cref="Verify"/>) plus the F2 additions
/// (<see cref="ParseRefineTrials"/>, <see cref="ParseOpponent"/>) needed to wire the real
/// <c>transfer</c>/<c>squad --opponent wave</c> modes in <c>Program.cs</c>. The three-column report
/// itself lives in <see cref="TransferReport"/>; this file never re-implements it.
/// </summary>
public static class Modes
{
    /// <summary>Parses <c>--allocation-shape shipped|per-actor</c>. Defaults to
    /// <see cref="AllocationShape.PerActor"/> when the flag is absent (F1's own default roster,
    /// F1b acceptance bullet 1).</summary>
    public static AllocationShape ParseAllocationShape(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != "--allocation-shape") continue;
            return args[i + 1] switch
            {
                "shipped" => AllocationShape.Shipped,
                "per-actor" => AllocationShape.PerActor,
                var other => throw new ArgumentException($"--allocation-shape must be 'shipped' or 'per-actor', got '{other}'"),
            };
        }
        return AllocationShape.PerActor;
    }

    /// <summary>The <c>duel</c> roster as <see cref="RosterEntry"/> -- allocation-shape has no meaning
    /// here (a duel build is already exactly one actor), so this ignores the flag entirely rather than
    /// silently accepting and discarding it under a different name.</summary>
    public static IReadOnlyList<RosterEntry> BuildDuelRoster() =>
        SquadRoster.Duels().Select(RosterEntry.From).ToList();

    /// <summary>The <c>squad</c> roster as <see cref="RosterEntry"/>, under <paramref name="shape"/> --
    /// the one seam F1b adds. <c>transfer</c>'s full three-column report is F2's; this only builds the
    /// roster the flag asked for.</summary>
    public static IReadOnlyList<RosterEntry> BuildSquadRoster(AllocationShape shape) =>
        SquadRoster.Squads(shape).Select(RosterEntry.From).ToList();

    /// <summary>Which roster(s) a <c>verify</c> invocation exercises -- an F1-internal cost knob, not a
    /// spec-named mode: the full duel roster alone is already 8,190 ordered pairs, so a self-check that
    /// always paid for both rosters at once would make every CI run of this module pay full-sweep cost
    /// for a test that only needs to prove "run twice, same hash." Defaults to <see cref="All"/> for the
    /// real command-line tool; test code picks a narrower scope on purpose.</summary>
    public enum RosterScope { Duel, Squad, All }

    public static RosterScope ParseRosterScope(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != "--roster") continue;
            return args[i + 1] switch
            {
                "duel" => RosterScope.Duel,
                "squad" => RosterScope.Squad,
                "all" => RosterScope.All,
                var other => throw new ArgumentException($"--roster must be 'duel', 'squad' or 'all', got '{other}'"),
            };
        }
        return RosterScope.All;
    }

    /// <summary>
    /// F1's own <c>verify</c>: sweeps the requested roster(s) and returns the canonical
    /// <see cref="HarnessRun"/>(s), so a re-run (in-process or as a fresh process) can be compared
    /// byte-for-byte -- F1's acceptance bullet "A_second_process_reproduces_the_hash". This is
    /// deliberately NOT the three-column <c>duelClosedForm</c>/<c>duelTrials</c>/<c>squadTrials</c>
    /// report (§5) -- that comparison, its orderings and its half-widths are F2's <c>transfer</c> mode.
    ///
    /// <para><paramref name="limit"/> is another F1-internal cost knob (see <see cref="RosterScope"/>'s
    /// own doc): it takes the first N ids of the Id-sorted roster, deterministically, so a self-check
    /// can prove "run twice, same hash" without paying for all 8,190/506 ordered pairs every time. Null
    /// means the full roster -- the real command-line tool's own default.</para>
    /// </summary>
    public static IReadOnlyList<HarnessRun> Verify(RunSpec spec, AllocationShape shape, RosterScope scope, bool parallel, int? limit = null)
    {
        IReadOnlyList<RosterEntry> Limited(IReadOnlyList<RosterEntry> roster) =>
            limit is null ? roster : roster.OrderBy(r => r.Id, StringComparer.Ordinal).Take(limit.Value).ToList();

        var runs = new List<HarnessRun>();
        if (scope is RosterScope.Duel or RosterScope.All)
            runs.Add(Sweep.Run("duel", Limited(BuildDuelRoster()), spec, parallel));
        if (scope is RosterScope.Squad or RosterScope.All)
            runs.Add(Sweep.Run("squad", Limited(BuildSquadRoster(shape)), spec, parallel));
        return runs;
    }

    /// <summary>spec §9.2's <c>--refine</c> flag: additional trials for cells that cannot call a winner
    /// at the screening trial count (§9.2's two-stage design). Absent means no refine pass at all --
    /// distinct from present-but-zero, which is why this returns <c>long?</c> rather than defaulting to
    /// 0 (a caller checks <c>is null</c> for "screening only").</summary>
    public static long? ParseRefineTrials(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "--refine" && long.TryParse(args[i + 1], out var value))
                return value;
        return null;
    }

    /// <summary>spec §2/Commands: <c>--opponent wave</c> switches <c>squad</c> from the primary
    /// squad-vs-squad verdict to squad-vs-authored-wave content, reported separately (never mixed into
    /// the transfer table). Absent or <c>squad</c> (the default, spelled out so a typo is caught rather
    /// than silently falling through) means the primary mirror-squad mode.</summary>
    public static string ParseOpponent(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != "--opponent") continue;
            return args[i + 1] switch
            {
                "squad" => "squad",
                "wave" => "wave",
                var other => throw new ArgumentException($"--opponent must be 'squad' or 'wave', got '{other}'"),
            };
        }
        return "squad";
    }

    // ---- F4 (concentration/crossunlock) ------------------------------------------------------------

    /// <summary>A comma list of longs behind <paramref name="flag"/> (e.g. <c>--fmax-milli
    /// 1000,1150,1200,1250</c>). Null when the flag is absent -- distinct from an empty list, so a
    /// caller can refuse "missing" separately from "present but empty" (matching
    /// <see cref="ParseRefineTrials"/>'s own null-means-absent convention).</summary>
    public static IReadOnlyList<long>? ParseLongList(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != flag) continue;
            return args[i + 1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => long.TryParse(s, out var v) ? v : throw new ArgumentException($"{flag} must be a comma list of integers, got '{s}'"))
                .ToList();
        }
        return null;
    }

    /// <summary>A single long behind <paramref name="flag"/>, or null when absent -- the same
    /// null-means-absent convention as <see cref="ParseLongList"/>, used for <c>--b</c>,
    /// <c>--fmax-milli</c> and <c>--w-milli</c> when <c>crossunlock</c> reads them as single values
    /// rather than a sweep.</summary>
    public static long? ParseLong(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && long.TryParse(args[i + 1], out var value))
                return value;
        return null;
    }

    /// <summary>A comma list of <see cref="TreeModel.CreditRule"/> values behind <c>--rule</c> (e.g.
    /// <c>--rule none,largest,quarter,full</c>). Null when absent -- <c>crossunlock</c> refuses rather
    /// than defaulting a rule sweep, the same "no default" posture <c>--erosion-milli</c> and
    /// <c>--seed</c> already use for a quantity nobody has decided for the caller.</summary>
    public static IReadOnlyList<TreeModel.CreditRule>? ParseCreditRules(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != "--rule") continue;
            return args[i + 1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s switch
                {
                    "none" => TreeModel.CreditRule.None,
                    "largest" => TreeModel.CreditRule.Largest,
                    "quarter" => TreeModel.CreditRule.Quarter,
                    "full" => TreeModel.CreditRule.Full,
                    var other => throw new ArgumentException($"--rule must be a comma list of none|largest|quarter|full, got '{other}'"),
                })
                .ToList();
        }
        return null;
    }
}
