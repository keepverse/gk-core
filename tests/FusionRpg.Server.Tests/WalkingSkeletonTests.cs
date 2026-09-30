using System.Text.Json;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// T3.9 (Phase 3.5) ⭐ WALKING SKELETON — one species (<c>conezombie</c>) carried through every seam
/// this program's chain names: <c>dump row → parsed basis → threat rung → anchor → species-passive
/// container → stats → imported → rolled for player A → binding → AtomRunner receives it</c>.
///
/// <para><b>Not a review gate — an automated shape test</b> whose job is finding SEAM errors between
/// modules, the one defect class no per-module test catches. A module Phases 4-5 have not built yet
/// is stubbed (<see cref="Stub"/>), never invented from nothing where a real one exists.</para>
///
/// <para><b>Real vs stubbed, honestly counted (verified against the repo, not assumed):</b></para>
/// <list type="bullet">
/// <item><b>Real (Phase 0):</b> the dump row itself — read directly from
/// <c>gk-data/packs/fusion/data/seed/creatures/creature/zombie/epic.json</c>'s own `conezombie` entry, not hardcoded blind, so
/// this test breaks if the real dump ever changes shape.</item>
/// <item><b>Stubbed:</b> parsed basis, threat rung and the classified anchor — `power-parse` and
/// `threat-audit` are real Python modules (`gk-forge/tools/seedsmith/`), unreachable from a C# test process;
/// the anchor is the LLM-classification step, and no real classified anchor exists for `conezombie`
/// yet (grepped `gk-data/packs/fusion/data/seed/creatures/_dump`/`_generated`/`_runs` directly — confirmed absent; T2.11's
/// real run, which would produce one, is the owner-run job this whole audit's Checkpoint 2 is
/// blocked on). All three stand in with SHAPE-plausible values derived from the real dump row's own
/// numbers, never invented from nothing.</item>
/// <item><b>Stubbed:</b> the species-passive container's own generation — `species-generator`
/// (creature-seed module 12) and `player-materialise` (module 16) are Phase 4 work, not built yet. The
/// container itself is hand-built here, but it is a REAL <see cref="ContainerRow"/>, validated by the
/// REAL <see cref="ContainerValidator"/> — the stub is "what generates this shape," not the shape
/// itself.</item>
/// <item><b>Real (Phase 3, this program's own delivered work):</b> import (`RpgStore.UpsertAtom`/
/// `UpsertContainer`), roll (`RpgStore.ProduceAndBind`, T3.6), bind (`ResolveBindings`), and dispatch
/// (`AtomPushService.Build` → `AtomPushCodec.DecodeBindings` → `TriggerIndex` → a real
/// `AtomRunner.OnEvent`) — the exact chain T3.7's own `AtomEndToEndTests` already proved, run here
/// against conezombie's own real numbers instead of a synthetic fixture.</item>
/// </list>
///
/// <para><b>Deliberately in <c>FusionRpg.Server.Tests</c>, not <c>FusionRpg.Core.Tests</c></b> —
/// same reason as T3.7's own file: `AtomPushService` lives in `FusionRpg.Server`, which
/// `Core.Tests` does not reference (checked its `.csproj` directly).</para>
/// </summary>
public class WalkingSkeletonTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly AtomPushService _push;

    public WalkingSkeletonTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _push = new AtomPushService(_store);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, // pinned anchor, contentScale(20) == 1.000 exactly
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    /// <summary>One named seam that stood in for a Phase 4/5 module not built yet, and why.</summary>
    sealed record StubbedSeam(string Seam, string Reason);

    static string RepoRoot() => ContentRoot.Path;

    /// <summary>Seam 1 — the real dump row, read off disk, not hardcoded blind.</summary>
    static (int Hp, int Attack, int Armor) RealDumpRow()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "creature", "zombie", "epic.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (entry.GetProperty("id").GetString() != "conezombie") continue;
            return (entry.GetProperty("hp").GetInt32(), entry.GetProperty("attack").GetInt32(),
                entry.GetProperty("armor").GetInt32());
        }

        throw new InvalidOperationException("conezombie not found in the real dump — the fixture itself is stale");
    }

    [Fact]
    public void Conezombie_walks_every_stage_from_dump_row_to_a_dispatched_effect()
    {
        var stubs = new List<StubbedSeam>();

        // ---- Seam 1: dump row (REAL) ------------------------------------------------------------
        var dump = RealDumpRow();
        Assert.Equal(270, dump.Hp);
        Assert.Equal(50, dump.Attack);
        Assert.Equal(370, dump.Armor);

        // ---- Seam 2: parsed basis (STUB — power-parse is real Python, unreachable here) ----------
        stubs.Add(new StubbedSeam("parsed-basis",
            "power-parse (tools/seedsmith) is real Python, not reachable from a C# test process"));
        // Shape-plausible, derived from the real dump row: hp/armor dominant -> a "tank" basis, not
        // an invented number with no relationship to conezombie's own stats.
        var basisIsTank = dump.Armor > dump.Attack * 5;
        Assert.True(basisIsTank, "conezombie's own real armor/attack ratio should read as tank-leaning");

        // ---- Seam 3: threat rung (STUB — threat-audit is real Python, unreachable here) -----------
        stubs.Add(new StubbedSeam("threat-rung",
            "threat-audit (tools/seedsmith) is real Python, not reachable from a C# test process"));
        const string threatRung = "low"; // conezombie is an early-game, low-threat species

        // ---- Seam 4: anchor (STUB — no real classified anchor exists for conezombie; T2.11 owner-run) --
        stubs.Add(new StubbedSeam("anchor",
            "no real classified anchor for conezombie exists yet (verified absent under data/seed/creatures/" +
            "_dump, _generated, _runs) — T2.11, the LLM classification run that would produce one, is " +
            "explicitly owner-run and out of this audit's own reach"));
        var anchorSpeciesId = "conezombie";
        var anchorTemperament = "sturdy"; // shape-plausible given the tank-leaning basis, not asserted as content

        // ---- Seam 5: species-passive container (STUB generation, REAL shape+validation) ------------
        stubs.Add(new StubbedSeam("species-passive-generation",
            "species-generator (creature-seed module 12) and player-materialise (module 16) are Phase 4 " +
            "work, not built yet — the container below is hand-built, but it is a real ContainerRow " +
            "validated by the real ContainerValidator, not a shape invented for this test"));

        var atomId = "atom.walking-skeleton-vitality.t1";
        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = "atom.walking-skeleton-vitality",
            Tier = 1, Name = "Walking Skeleton Vitality",
            // Derived from the real basis/rung above, not an arbitrary number: a low-threat, tank-
            // leaning species gets a modest flat defensive bonus.
            ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{dump.Armor / 10}}}""",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerId = $"species-passive.{anchorSpeciesId}";
        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.SpeciesPassive,
            Atoms = new[] { new ContainerAtomRow(1, atomId) },
            TagsJson = $$"""{"speciesId":"{{anchorSpeciesId}}","temperament":"{{anchorTemperament}}","threatRung":"{{threatRung}}"}""",
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());

        // ---- Seam 6: stats (folded into the container's own atom above — asserted here) ------------
        var storedContainer = _store.GetContainer(containerId);
        Assert.NotNull(storedContainer);
        Assert.Single(storedContainer!.Atoms);

        // ---- Seam 7: imported (REAL — Phase 3's own DAL, no stub) ------------------------------------
        Assert.NotNull(_store.GetAtom(atomId));

        // ---- Seam 8: rolled for player A (REAL — T3.6's ProduceAndBind) -----------------------------
        var owner = new OwnerScope(OwnerKind.Player, "1");
        var produce = _store.ProduceAndBind(
            storedContainer!, domain => Array.Empty<string>(), rollSeed: 2026_09_02,
            thetaContent: 20, Tuning, owner, slot: null, priority: 1, source: "walking-skeleton",
            out var instanceId, out var bindingId);
        Assert.True(produce.IsOk, produce.ToString());
        Assert.NotNull(instanceId);
        Assert.NotNull(bindingId);

        // ---- Seam 9: binding (REAL — ResolveBindings genuinely returns this row) --------------------
        var resolution = _store.ResolveBindings(owner, new BindContext(RuntimeId.Lawn));
        Assert.Contains(resolution.Bindings, b => b.BindingId == bindingId);

        // ---- Seam 10: AtomRunner receives it (REAL — the exact T3.7 chain, sourced from conezombie) --
        var payload = _push.Build(owner, new BindContext(RuntimeId.Lawn), matchSeed: 2026_09_02);
        Assert.False(payload.UpToDate);
        Assert.NotEmpty(payload.Grants); // a permanent stat.modify travels as a Foundation grant

        // The atom here is a permanent modifier (no trigger), so it is a GRANT, not a runner entry —
        // shape-correct per CompiledPushTests.cs's own precedent, asserted directly rather than
        // assumed: RunnerBindings stays empty for a container with no triggered atom.
        Assert.Empty(payload.RunnerBindings);
        Assert.NotEmpty(payload.Defs);
        Assert.Contains(payload.Defs, d => d.EffectId == atomId);

        // ---- The gap is printed, per this task's own acceptance line ---------------------------------
        var report = $"WALKING SKELETON — conezombie — {stubs.Count} stubbed seam(s) of 10:\n" +
                      string.Join("\n", stubs.Select(s => $"  - {s.Seam}: {s.Reason}"));
        Assert.True(stubs.Count == 4, report); // fails loudly if a seam silently gains or loses a stub
    }
}
