using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `solid-remediation` T4.5 (S5, historical) — the species roster roll HAD a **production** caller
/// (`Program.cs`'s eager boot roll), and this guard proved it, because the register wrongly claimed it
/// had lost one.
///
/// <para><b>Then `species-progression` SP0.4 removed that caller on purpose.</b> C2's ruling (a save
/// that never fuses gets zero layer-1b rows) is incompatible with an eager, unconditional boot roll —
/// so `Program.cs`'s call is gone (spec-species-mod-ledger.md, Migration), and layer 1b is now written
/// ONLY by the append-only ledger (SP0.1/SP0.3, <c>AppendSpeciesModUnlocked</c>). The two tests below
/// invert S5's original assertion on purpose: they now prove the ABSENCE of that caller and of any
/// other write path, so a reintroduced eager roll (or a second `INSERT INTO player_species` outside
/// this file) fails loudly instead of silently reopening C2.</para>
///
/// <para><b>Why a guard and not just a unit test.</b> A unit test that calls the method itself would
/// pass forever regardless of whether production calls it. Only a scan for a call/write site OUTSIDE
/// the test tree expresses "nothing but the one sanctioned seam touches this."</para>
/// </summary>
[Trait("guard", "player-species-materialise-caller")]
public class PlayerSpeciesMaterialiseCallerGuardTests
{
    const string Method = "MaterialisePlayerSpecies";
    const string OwnFile = "RpgStore.PlayerSpecies.cs";

    [Fact]
    public void No_production_code_rolls_a_players_species_eagerly()
    {
        // SP0.4: Program.cs's eager boot call is gone, and no other production file may call it either
        // — the method itself stays (ReforgePlayerSpecies/the debug reforge path still use it, and
        // SP0.6 retires both together), but nothing may call it EAGERLY on a boot/content path again.
        var callers = ProductionCallSites();

        Assert.Empty(callers);
    }

    [Fact]
    public void No_production_code_writes_layer_1b_outside_the_ledger_append()
    {
        // C2's own falsifier restated as a guard: the only sanctioned write to `player_species` is
        // RpgStore.PlayerSpecies.cs's own INSERT (ReforgePlayerSpecies's upsert, SP0.6's to retire).
        // A second INSERT anywhere else in src/ is a second write path to the exact table SP0.1-0.4
        // moved layer 1b OFF of.
        var root = Path.Combine(FindRepoRoot(), "src");
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.EndsWith(OwnFile, StringComparison.Ordinal))
                continue;

            if (File.ReadAllText(file).Contains("INSERT INTO player_species", StringComparison.Ordinal))
                hits.Add(file);
        }

        Assert.Empty(hits);
    }

    [Fact]
    public void Layer1b_reaches_the_fold_through_rpg_species_layer()
    {
        // solid-remediation T4.6 (S4), carried forward through two re-blesses of WHICH seam does the
        // composing: a rolled instance nothing composes is still an inert row. T4.6 first wired it
        // through a hand-rolled BoundAtoms fold (`SpeciesPassiveAtomSource.DerivedAtomsFor`, retired
        // SP3.6); SP0.5/SP3.6 repointed that SAME interim fold onto `GetSpecimenLedgerRoll` +
        // `SpeciesLayerProjector`, called out in its own doc comment as "Still interim
        // (species-mod-ledger spec): SP6.8 replaces it." SP6.8 (this task) is that replacement: the
        // interim BoundAtoms fold is gone, and the sheet registers the SAME `rpg.species-layer`
        // subsystem every other compose path (lawn, world-turn, web-squad) already uses.
        var compose = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "FusionRpg.Server", "UniqueActorHubCompose.cs"));

        Assert.Contains("speciesLayers: SpeciesLayers", compose, StringComparison.Ordinal);
        Assert.Contains("SpeciesLayersForSpecimen(", compose, StringComparison.Ordinal);
        // The interim fold this test used to name is gone, not merely renamed again -- a stale
        // GetSpecimenLedgerRoll call here would mean the OLD interim path silently survived alongside
        // the new one instead of being replaced by it.
        Assert.DoesNotContain("GetSpecimenLedgerRoll", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("SpeciesPassiveAtomSource.DerivedAtomsFor", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ten_pick_refusal_codes_are_a_closed_vocabulary()
    {
        // T4.6's verify clause asks for these to be pinned, and pinning a literal COUNT is correct
        // here for the reason validation-ssot.md draws the line: a refusal code is a declaration the
        // code owns and a human edits, not a population content grows. A tenth code is a reviewed
        // change that should fail this test and be re-read; that is the opposite of the species-count
        // anti-pattern, where the "fix" is to bump the number.
        // TENTH CODE, re-blessed 2026-09-24 (QC fix cycle 2): `picks.source-below-rank-floor` is the
        // reviewed Task-8 fusion rank floor (`promotion.rank-floor` in RpgStore.Fusion.cs, merged
        // `e2ede8ae6`, Fusion filter green) — a deliberate vocabulary growth, read and accepted here.
        var expected = new[]
        {
            "picks.already-materialised",
            "picks.atom-not-rolled",
            "picks.compose-failed.",   // carries the underlying compose reason as a suffix
            "picks.exceeds-slots",
            "picks.no-target-container",
            "picks.source-below-inherit-floor",
            "picks.source-below-rank-floor",
            "picks.source-not-a-sacrifice",
            "picks.source-not-materialised",
            "picks.source-rarity-unknown",
        };

        var found = new SortedSet<string>(StringComparer.Ordinal);
        var rx = new System.Text.RegularExpressions.Regex(@"""(picks\.[a-z.-]+)""");

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(file)))
                found.Add(m.Groups[1].Value);
        }

        Assert.Equal(expected, found.ToArray());
    }

    [Fact]
    public void The_status_clock_costs_no_round_trip_on_the_injector_hot_path()
    {
        // solid-remediation T4.16 (D15). The module's note is explicit: if unifying the clock would add
        // a round trip per tick, the design is wrong — record it and re-plan rather than paying for
        // correctness in frame time. That invariant is not negotiable, so it is asserted structurally
        // rather than left to a measurement nobody re-runs.
        //
        // AdvancedEffectClock.UtcNow is a field read on a type with no I/O surface at all: no HttpClient,
        // no await, no store. A future edit that gave it one would fail here.
        var clock = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Core", "Effects", "AdvancedEffectClock.cs"));

        foreach (var forbidden in new[] { "Http", "await ", "Task<", "RpgStore", "Client" })
            Assert.DoesNotContain(forbidden, clock, StringComparison.Ordinal);

        // And the injector reads that clock rather than the wall clock per question — the role change
        // D15 is about. The wall clock is still the SOURCE (StartingNow seeds from it).
        var runtime = File.ReadAllText(Path.Combine(KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "Effects", "EffectRuntime.cs"));

        Assert.Contains("_bag.UtcNow = () => _clock.UtcNow;", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("_bag.UtcNow = () => DateTimeOffset.UtcNow;", runtime, StringComparison.Ordinal);
        Assert.Contains("new(DateTimeOffset.UtcNow)", runtime, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Call sites under `src/` — production by definition, since `tests/` is excluded and the
    /// declaration itself is not a call.</summary>
    static List<string> ProductionCallSites()
    {
        var root = Path.Combine(FindRepoRoot(), "src");
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // Build output mirrors source and would count a stale copy as a live caller.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();

                // The declaration is not a call, and neither is a comment mentioning it — that
                // distinction is exactly what made the dark state easy to miss by grep.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.Contains($"PlayerSpeciesMaterialiseOutcome {Method}", StringComparison.Ordinal))
                    continue;

                if (line.Contains($".{Method}(", StringComparison.Ordinal))
                    hits.Add(file);
            }
        }

        return hits;
    }

    static string TrimToLastKeyword(string before)
    {
        var tail = before.TrimEnd();
        var cut = tail.LastIndexOfAny(new[] { '\n', '\r' });
        return cut < 0 ? tail : tail[(cut + 1)..].Trim();
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
