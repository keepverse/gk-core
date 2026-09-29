using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>One entry of the duel roster -- exactly what <c>gk-core/tools/HybridViability</c> measures, plus a
/// stable <see cref="Id"/> this module's seeding (spec §7) keys off instead of construction-order
/// position (see <see cref="SquadRoster.Duels"/>'s own doc for why).</summary>
public sealed record NamedBuild(string Id, string Kind, AptitudeAllocation Allocation);

/// <summary>
/// spec-squad-harness.md §1: "A squad build is an ordered list of six actor builds." F1 models the
/// allocation layer only -- <c>ActorBuild = (AptitudeAllocation, TreeShare[], long theta)</c> per §1's
/// full type, but the tree layer (<c>TreeShares</c>) is S2/S4's own addition (§11: "Both land in stage
/// S2/S3, not S1") and has no reader until <c>concentration</c>/<c>crossunlock</c> exist, so this task
/// does not add an unused field for it -- <see cref="Actors"/> is the allocation-only slice F1 needs.
/// </summary>
public sealed record SquadBuild(string Id, string Kind, IReadOnlyList<AptitudeAllocation> Actors)
{
    /// <summary>Which allocation shape produced this squad (F1b, §1.1). Carried on the build itself so
    /// an artifact writer (F2) can say "which shape produced this" without a second lookup, per F1b's
    /// acceptance bullet that the two shapes must never be conflated in one artifact.</summary>
    public required AllocationShape Shape { get; init; }
}

/// <summary>
/// F1b (spec-squad-harness.md §1.1, §14 open question 2): which of the two allocation shapes a squad's
/// six actors were built under.
///
/// <para><b>PerActor</b> is D21's shape and F1's own default -- six potentially distinct allocations,
/// the shape <c>tree-state</c> is designing for.</para>
///
/// <para><b>Shipped</b> is today's actual production shape: the squad builder merges only the
/// Commander and CreatureType scopes into each actor's <see cref="BattleHubInputs.Aptitude"/> (§1.1,
/// battle-hub-fuse T6 re-homed this off the deleted <c>WebMatchService.AptitudeChannelMods</c>), so two
/// squad members of the same species cannot differ today -- every actor on a team effectively
/// replicates the commander's allocation. This harness cannot reach <c>RpgStore</c> (§13 "Never"), so
/// it reproduces that shape in memory by collapsing the per-actor list onto its first entry, replicated
/// six times.</para>
/// </summary>
public enum AllocationShape { PerActor, Shipped }

/// <summary>
/// spec-squad-harness.md §1.2 -- the two rosters, both built from <see cref="BuildFactory"/>'s one
/// corner-shape helper so neither roster is a private re-derivation the other cannot be compared
/// against.
/// </summary>
public static class SquadRoster
{
    /// <summary>
    /// The 91-build duel roster: 12 corners + 66 two-way (C(12,2)) + 12 three-way + 1 even-twelve.
    /// Reproduces <c>gk-core/tools/HybridViability/Program.cs:106-115</c>'s own four loops verbatim (same
    /// roster order, same hybrid3 "every consecutive triple" rule, same single spread entry) -- this is
    /// the CONSTRUCTION F1's acceptance bar requires, not an assertion that the count happens to be 91.
    ///
    /// <para><see cref="NamedBuild.Id"/> is the label HybridViability prints (e.g. "Might",
    /// "Might+Fortitude", "even12"). <c>Seeds.Mix</c> hashes each build's Id directly -- never a
    /// position in this list -- so shuffling this method's own construction order (or measuring only a
    /// subset of it) moves no surviving cell (spec §7, and this module's own
    /// <c>Reordering_the_roster_does_not_move_a_cell</c> test).</para>
    /// </summary>
    public static IReadOnlyList<NamedBuild> Duels()
    {
        var roster = BuildFactory.Roster;
        var builds = new List<NamedBuild>(91);

        foreach (var a in roster)
            builds.Add(new NamedBuild(a, "corner", BuildFactory.Build(a)));

        for (var i = 0; i < roster.Count; i++)
        for (var j = i + 1; j < roster.Count; j++)
            builds.Add(new NamedBuild($"{roster[i]}+{roster[j]}", "hybrid2", BuildFactory.Build(roster[i], roster[j])));

        // A 3-way sample: every consecutive triple in roster order, which mixes within and across
        // postures -- HybridViability/Program.cs:110-113's own rule, reproduced exactly.
        for (var i = 0; i < roster.Count; i++)
            builds.Add(new NamedBuild(
                $"{roster[i]}+{roster[(i + 1) % roster.Count]}+{roster[(i + 2) % roster.Count]}",
                "hybrid3",
                BuildFactory.Build(roster[i], roster[(i + 1) % roster.Count], roster[(i + 2) % roster.Count])));

        builds.Add(new NamedBuild("even12", "spread", BuildFactory.EvenSpread()));

        return builds;
    }

    /// <summary>
    /// The 23-squad roster (spec §1.2's table), built under <paramref name="shape"/>. Every id shape in
    /// the spec's table is produced here, in the spec's own order, and every squad has exactly six
    /// actors (F1's own named test).
    ///
    /// <para><b>Posture is READ from the shipped catalog</b> (<see cref="AptitudeCatalog.InPosture"/>),
    /// never re-declared -- the rule <c>HybridViability/Program.cs:307</c> already follows.</para>
    ///
    /// <para><b>Deterministic sampling, stated as a default.</b> The spec names each shape ("one sampled
    /// per posture pair", "two per posture") but not which member of a posture to pick when more than
    /// one qualifies. This resolves every such choice to the FIRST aptitude of a posture in
    /// <see cref="Aptitude.All"/>'s own ordinal order (Might before Fortitude before Vigor... within
    /// Force, etc.) -- a fixed, published rule rather than a random or hand-picked one, so a re-run of
    /// this method always names the same 23 squads.</para>
    /// </summary>
    public static IReadOnlyList<SquadBuild> Squads(AllocationShape shape = AllocationShape.PerActor)
    {
        var postures = Enum.GetValues<Posture>(); // Force, Finesse, Bastion -- Aptitude.cs's own order
        AptitudeRow[] In(Posture p) => AptitudeCatalog.InPosture(p).ToArray();

        var squads = new List<SquadBuild>(23);
        void Add(string id, string kind, IReadOnlyList<AptitudeAllocation> actors) =>
            squads.Add(Finish(id, kind, actors, shape));

        // mono-<apt> x12 -- six copies of one corner.
        foreach (var apt in BuildFactory.Roster)
            Add($"mono-{apt.ToLowerInvariant()}", "mono", Repeat(BuildFactory.Build(apt), 6));

        // posture-<p> x3 -- six actors round-robin over one posture's four corners.
        foreach (var posture in postures)
        {
            var corners = In(posture).Select(a => BuildFactory.Build(a.Id)).ToArray();
            var actors = Enumerable.Range(0, 6).Select(i => corners[i % corners.Length]).ToList();
            Add($"posture-{posture.ToString().ToLowerInvariant()}", "posture", actors);
        }

        // rainbow-balanced x1 -- six distinct corners, two per posture (the D33 exploit build).
        {
            var actors = postures.SelectMany(p => In(p).Take(2)).Select(a => BuildFactory.Build(a.Id)).ToList();
            Add("rainbow-balanced", "rainbow", actors);
        }

        // rainbow-force-finesse x1 -- six distinct corners, three FORCE + three FINESSE.
        {
            var actors = In(Posture.Force).Take(3).Concat(In(Posture.Finesse).Take(3))
                .Select(a => BuildFactory.Build(a.Id)).ToList();
            Add("rainbow-force-finesse", "rainbow", actors);
        }

        // mono-spread x1 -- six copies of even-twelve.
        Add("mono-spread", "mono", Repeat(BuildFactory.EvenSpread(), 6));

        // mono-hybrid2-<a+b> x3 -- six copies of one 2-way hybrid, one sampled per posture pair.
        var posturePairs = new List<(Posture, Posture)>();
        for (var i = 0; i < postures.Length; i++)
        for (var j = i + 1; j < postures.Length; j++)
            posturePairs.Add((postures[i], postures[j]));
        foreach (var (pa, pb) in posturePairs)
        {
            var a = In(pa)[0].Id;
            var b = In(pb)[0].Id;
            Add($"mono-hybrid2-{a}+{b}".ToLowerInvariant(), "mono-hybrid2", Repeat(BuildFactory.Build(a, b), 6));
        }

        // mono-hybrid3-<a+b+c> x1 -- six copies of one 3-way hybrid, one aptitude per posture.
        {
            var ids = postures.Select(p => In(p)[0].Id).ToArray();
            Add($"mono-hybrid3-{string.Join("+", ids)}".ToLowerInvariant(), "mono-hybrid3",
                Repeat(BuildFactory.Build(ids), 6));
        }

        // mixed-corner-spread x1 -- three corners (one per posture) + three even-twelve.
        {
            var corners = postures.Select(p => BuildFactory.Build(In(p)[0].Id));
            var actors = corners.Concat(Repeat(BuildFactory.EvenSpread(), 3)).ToList();
            Add("mixed-corner-spread", "mixed", actors);
        }

        return squads;
    }

    /// <summary>F1b: collapses a per-actor squad onto the SAME first entry the per-actor construction
    /// already produced, replicated six times -- never a second construction path (F1b's own acceptance
    /// bullet). PerActor passes the list through untouched.</summary>
    static SquadBuild Finish(string id, string kind, IReadOnlyList<AptitudeAllocation> actors, AllocationShape shape) =>
        new(id, kind, shape == AllocationShape.Shipped ? Repeat(actors[0], actors.Count) : actors) { Shape = shape };

    static IReadOnlyList<AptitudeAllocation> Repeat(AptitudeAllocation allocation, int count) =>
        Enumerable.Repeat(allocation, count).ToList();
}
