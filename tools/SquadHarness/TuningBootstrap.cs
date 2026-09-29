using System.Text.RegularExpressions;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md "Project structure": reads the HIGHEST <c>gk-core/data/tuning/&lt;domain&gt;.v{n}.json</c>
/// per domain present on disk, never a hand-picked version literal -- the same rule
/// <c>gk-core/tools/HybridViability/Program.cs:45-57</c> and <c>gk-forge/tools/DominanceBaseline</c> already follow, so a
/// tuning bump shows up here automatically instead of silently going stale.
///
/// <para>Configures every hub the trial path actually touches. Unlike the closed-form tools
/// (HybridViability, DominanceBaseline), this harness resolves every pair through the real
/// <see cref="BattleEngine.Resolve"/>, so it also needs <see cref="BattleTuningHub"/> (the one hub the
/// duel tools do not configure, spec §"Project structure"), <see cref="BattleRuleset.ConfigureResources"/>
/// and <see cref="ActionTimingPolicy"/> -- both found the hard way by earlier programs as "one more
/// Configure every harness must remember" (<c>ContractTuningTestBootstrap.cs</c>'s own comments on both).
/// </para>
/// </summary>
public static class TuningBootstrap
{
    /// <summary>Idempotent by construction (every <c>Configure</c> call below just replaces static
    /// state), but callers should still only need to call this once per process.</summary>
    public static void Configure(string? repoRoot = null)
    {
        var root = repoRoot ?? FindRepoRoot();
        var tuningDir = Path.Combine(root, "data", "tuning");
        string Read(string domain) => File.ReadAllText(Path.Combine(tuningDir, LatestTuningFileName(tuningDir, domain)));
        string ReadPinned(string fileName) => File.ReadAllText(Path.Combine(tuningDir, fileName));

        // Same seven hubs gk-core/tools/HybridViability and gk-forge/tools/DominanceBaseline configure, for the same
        // reason: AptitudeSubsystem (battle) and the shipped SSOT resolver (overlay) both read them
        // through the one shared AptitudeResolver.Resolve call (battle-hub-fuse T6).
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(Read("aptitudes")));
        CombatPolicy.Configure(CombatTuningLoader.Parse(Read("combat")));
        ShieldPolicy.Configure(ShieldTuningLoader.Parse(Read("shield")));
        DerivedStatPolicy.Configure(DerivedStatTuningLoader.Parse(Read("derived-stats")));
        // ⛔ power-scale is PINNED, not latest-wins. `power-scale.v3.json` is not a newer curve — it is a
        // different artifact kind that happens to share the domain prefix: "Pins, not curves. Each row is a
        // channel's composed value at the Theta=20 reference", owned by passive-tree-repair, and its own
        // _meta says "v2.json frozen". Its channel keys are templates (`combat.power.{variant}`, `arm1Max`)
        // with no `atk`/`defense`, so `BattleRuleset.ChannelLadderFor("atk")` throws against it.
        //
        // Every real consumer of v3 loads it by explicit path (FamilyExpandGen, seedsmith vocab.py); nothing
        // treats it as the successor to v2. Production pins v2 in both hosts. Resolving this domain by
        // "highest n wins" measured a tuning the game never loads — 73 of 193 SquadHarness tests failed on it
        // (2026-09-17), and the two sibling tools had the same defect with no tests to notice.
        PowerTuningHub.Configure(PowerTuningLoader.Parse(ReadPinned("power-scale.v2.json")));
        StatusPolicy.Configure(StatusTuningLoader.Parse(Read("status")));
        StatsTuningHub.Configure(StatsTuningLoader.Parse(Read("stats")));

        // F4 (TreeModel.cs): req(t)/W(T)/H/F and D25's unlock-cost wallet all read this hub --
        // PassiveTreeTuningHub.Tuning throws by design otherwise (T5's "no built-in default").
        PassiveTreeTuningHub.Configure(PassiveTreeTuningLoader.Parse(Read("passive-tree")));

        // The hub the duel tools never needed, because they never call BattleEngine.Resolve.
        // Fans out to TraitBattleCatalog / BattleRuleset / BattleModeProfileCatalog
        // (BattleTuningHub.Configure's own body) -- without it BattleRuleset.Tuning throws by design.
        BattleTuningHub.Configure(BattleTuningLoader.Parse(Read("battle")));

        // BattleRunState seeds every actor's six resource pools from this at construction time,
        // unconditionally -- BattleRuleset.ConfigureResources(...) has not run" otherwise
        // (BattleModels.cs, spec-battle-resources.md S2.6).
        BattleRuleset.ConfigureResources(BattleResourceTuningLoader.Parse(Read("battle-resources")));

        // BattleRunState's constructor unconditionally reads ActionTimingPolicy.Tuning to derive the
        // basic-attack envelope (BattleRunState.cs:68) -- missing this throws on the very first setup
        // this harness builds, not on some rarely-exercised path.
        ActionTimingPolicy.Configure(ActionTimingTuningLoader.Parse(Read("action-timing")));
    }

    static string LatestTuningFileName(string tuningDir, string domain)
    {
        var pattern = new Regex($@"^{Regex.Escape(domain)}\.v(\d+)\.json$");
        var best = Directory.EnumerateFiles(tuningDir)
            .Select(Path.GetFileName)
            .Select(name => (Name: name!, Match: pattern.Match(name!)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Name, Version: int.Parse(x.Match.Groups[1].Value)))
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
        if (best.Name is null)
            throw new InvalidOperationException($"no {domain}.v*.json found in {tuningDir}");
        return best.Name;
    }

    /// Marker: `Directory.Build.props`, the tracked MSBuild root file -- NOT a tool. The marker this
    /// used to name, `scripts/guard-class-system.ps1`, was ported to `.py` and no longer exists, so
    /// EVERY invocation threw `could not locate repo root`. A marker must be a file no tool migration
    /// can delete; the two `FusionRpg.Core.ClassSystem.Tests` files beside this one already used
    /// `Directory.Build.props`, and this adopts their convention.
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
