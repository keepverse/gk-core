using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// A26 T63 (`spec-unlock-tuning-activation.md`): the unlock ladder is LIVE on a real host.
///
/// <para><b>Why this lives in E2E and not Server.Tests.</b> The acceptance names `RpgApiFactory :
/// WebApplicationFactory&lt;Program&gt;` — the real bootstrap, "not a hand-rolled test host" — and that
/// factory lives in this project. `FusionRpg.Server.Tests` has no host-boot project reference at all, so
/// putting the file there would have meant either a second hand-rolled host (exactly what the acceptance
/// forbids) or a cross-test-project link of the factory. The real bootstrap is the thing under test, so
/// the test sits where it is reachable. The queue's own Verify filter targets the file by name, which
/// resolves in this project.</para>
///
/// <para><b>The first test is the T62 proof.</b> Before T62 nothing in the server process configured
/// `UnlockTuningPolicy`, so reading `.Tuning` off a booted host threw; it is also why
/// `TryRollActionUnlocks` no-opped and a real level-up could never grant anything. It asserts the booted
/// host's tuning, read from the SHIPPED file rather than a literal, so it cannot drift into passing on a
/// stale number.</para>
///
/// <para><b>The second test owns its own store.</b> It used to award XP on the shared
/// `RpgApiFactory` store, which made its outcome a claim about whatever every other test in the `e2e`
/// collection had left in that store's action catalog by the time it ran (measured ADG-F4,
/// 2026-09-22). It now runs against its own in-memory store, so the only action the unlock roll can
/// draw is the one under test.</para>
/// </summary>
[Collection("e2e")]
public class UnlockTuningActivationTests
{
    readonly RpgApiFactory _factory;

    public UnlockTuningActivationTests(RpgApiFactory factory) => _factory = factory;

    /// <summary>Boots the real host the same way every other E2E test does — a client, which is what
    /// forces the factory to start `Program` and run its bootstrap.</summary>
    HttpClient Boot() => _factory.CreateClient();

    [Fact]
    public void The_real_host_boots_with_the_unlock_tuning_configured()
    {
        using var http = Boot();

        var tuning = UnlockTuningPolicy.Tuning;   // pre-T62 this threw: nothing configured it
        var shipped = UnlockTuningLoader.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "action-unlock.v1.json")));

        Assert.Equal(shipped.P1Milli, tuning.P1Milli);
        Assert.Equal(shipped.DeltaMilli, tuning.DeltaMilli);
        Assert.Equal(shipped.FloorMilli, tuning.FloorMilli);
        Assert.Equal(shipped.HeldCap, tuning.HeldCap);
        Assert.Equal(shipped.RungCap, tuning.RungCap);
    }

    /// <summary>The ladder is not merely configured: a real level-up through the production store path
    /// produces a real `rpg_action_grant` row. The roll is made deterministic by the always-hit control
    /// tuning the Data-side wiring test already uses (P1Milli = DeltaMilli = 1000, so chance(n) stays
    /// 100%) — the shipped 500/880 would make "at least one grant landed" a coin-flip and the test flaky,
    /// and a flaky test proves nothing. What is NOT controlled is the path: the specimen, the XP award and
    /// the grant row all go through the real `RpgStore` under the real host's own process configuration
    /// (the boot is what configures the rung/XP holders this read).
    ///
    /// <para><b>Its own store, not the shared one — why (ADG-F4, measured 2026-09-22).</b>
    /// `ActionUnlockGrantService.TryRollOnce`
    /// (`gk-core/src/FusionRpg.Core/Actions/Unlock/ActionUnlockGrantService.cs:107-153`) draws its candidate from
    /// the WHOLE catalog of the store it awards XP on, so on the shared `_factory` store this assertion
    /// became a claim about every other test's writes. The writer is
    /// `ActionBudgetReportTests.SeedThroughTheRealImportPath` (`ActionBudgetReportTests.cs:135-157`,
    /// called at `:50-52`): it imports the real action corpus into the shared store, which adds `act.attack`
    /// — `Kind = Basic`, `Scope = General`, and `ActionValidator.ValidateGrant`
    /// (`gk-core/src/FusionRpg.Core/Actions/ActionValidator.cs:87`) refuses every basic by construction, because a
    /// basic is intrinsic on every actor and is never granted. Measured with that one sibling test in the
    /// same run: candidates were `act.attack` + three `action.general.*` + this test's own row, the award
    /// threw `action unlock grant refused: BasicCollision` and its transaction rolled the level-up back.
    /// Every accepted candidate is held, so a multi-level award walks the whole list and cannot avoid that
    /// one — which is why the failure is deterministic in the suite, not a 1-in-N flake.</para>
    ///
    /// <para><b>The store is this test's own, and in memory.</b> On the shared `_factory` store the
    /// assertion was a claim about every other test's writes to that store's catalog. Its own
    /// `DataTestStore.Create()` — the sanctioned helper, already linked into this project — is isolated
    /// by construction and leaves nothing to clean up; the disk is not the thing under test here, the
    /// ladder path is. ⛔ The production half of that reading is filed as `ADG-F5` — this file must not
    /// hide it.</para></summary>
    [Fact]
    public void A_real_level_up_grants_a_real_action_row()
    {
        using var _ = Boot();   // the real host: its boot configures the process-wide holders the store reads
        using var own = DataTestStore.Create();
        var store = own.Store;
        var player = store.CreatePlayer("T63 activation");

        var containerId = "skill.t63-activation";
        Assert.True(store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
        }).IsOk);
        Assert.True(store.UpsertAction(new ActionRow
        {
            ActionId = "action.t63-activation",
            Name = "T63 activation",
            Kind = ActionKind.Skill,
            Rung = 1,
            Enabled = true,
            Grantable = true,
            ContainerId = containerId,
        }).IsOk);

        // Process-wide, so put back what the boot installed: leaving the always-hit control in place
        // would change every later level-up roll in this collection.
        var previousTuning = UnlockTuningPolicy.Tuning;
        try
        {
            UnlockTuningPolicy.Configure(new UnlockTuning(
                P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));

            var actor = store.CreateUniqueActor(player.Id, side: "plant", typeId: 1);
            var (ok, reason, updated, _) = store.AwardUniqueActorXp(actor.InstanceId, delta: 1_000_000);
            Assert.True(ok, reason);
            Assert.True(updated!.Level > 1, "the award must actually cross a level threshold");

            var grants = store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId));
            Assert.Contains(grants, g => g.ActionId == "action.t63-activation");
        }
        finally
        {
            if (previousTuning is not null) UnlockTuningPolicy.Configure(previousTuning);
        }
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    /// <summary>
    /// ADG-F5's production shape, end to end: the REAL `authored-basics.json` goes through the REAL
    /// import path into this test's own store — the same call the boot makes (`Program.cs`), which is
    /// what puts `act.attack` (`Kind = Basic`, `grantable = 1`) into a live server's `rpg_action` — and
    /// a real level-up is then awarded on the real `RpgStore`. Pre-fix the roll offered that basic, the
    /// grant threw `BasicCollision`, and the award's transaction discarded the level (measured on the
    /// shared store as ADG-F4). Post-fix the award stands and no grant names a row the grant write path
    /// refuses.
    ///
    /// <para>The un-grantable ids are derived from the store through `ActionValidator.IsGrantable` —
    /// the same predicate the roll filters with — rather than pinned to `act.attack`, and the catalogue
    /// contents are asserted as membership ("the imported basic is present and refused"), never as a
    /// row count: the corpus is a population that grows whenever content ships.</para>
    /// </summary>
    [Fact]
    public void ALevelUpOnAStoreCarryingTheRealImportedBasicStillAwardsAndGrantsNothingItCannot()
    {
        using var _ = Boot();
        using var own = DataTestStore.Create();
        var store = own.Store;

        // The real import path for the basics file: its own atom source (the same one
        // ActionCorpusImporterTests seeds for this file) plus the shipped cost template.
        var atomsPath = Path.Combine(KeepverseRoots.Content(), "data", "seed", "atoms", "fx-core.json");
        var collected = AtomSeedFile.Collect(new[] { (atomsPath, File.ReadAllText(atomsPath)) });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        Assert.Empty(store.UpsertAtoms(collected.Content.Atoms).Rejected);

        var briefs = ActionCorpusBriefJson.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "actions", "authored-basics.json")));
        var template = ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "action-corpus-cost-templates.v2.json")));
        var imported = ActionCorpusImporter.Import(store, briefs, template, RungPolicy.Table);

        var attack = store.GetAction("act.attack");
        Assert.NotNull(attack);
        Assert.Equal(ActionKind.Basic, attack!.Kind);
        Assert.True(imported.ImportedCount >= 1);

        var player = store.CreatePlayer("ADG-F5 basic-in-catalog");
        var containerId = "skill.adg-f5-candidate";
        Assert.True(store.UpsertContainer(new ContainerRow { ContainerId = containerId, Kind = ContainerKind.Skill }).IsOk);
        Assert.True(store.UpsertAction(new ActionRow
        {
            ActionId = "action.adg-f5-candidate",
            Name = "ADG-F5 candidate",
            Kind = ActionKind.Skill,
            Rung = 1,
            Enabled = true,
            Grantable = true,
            ContainerId = containerId,
        }).IsOk);

        var previousTuning = UnlockTuningPolicy.Tuning;
        try
        {
            UnlockTuningPolicy.Configure(new UnlockTuning(
                P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));

            var actor = store.CreateUniqueActor(player.Id, side: "plant", typeId: 1);
            var (ok, reason, updated, _) = store.AwardUniqueActorXp(actor.InstanceId, delta: 1_000_000);

            Assert.True(ok, reason);
            Assert.True(updated!.Level > 1, "the award must actually cross a level threshold");

            var notGrantable = store.ListActionIds()
                .Select(store.GetAction)
                .Where(a => a is not null && !ActionValidator.IsGrantable(a!))
                .Select(a => a!.ActionId)
                .ToList();
            Assert.Contains("act.attack", notGrantable); // the imported basic really is present and refused

            var grants = store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId));
            Assert.Contains(grants, g => g.ActionId == "action.adg-f5-candidate");
            foreach (var g in grants) Assert.DoesNotContain(g.ActionId, notGrantable);
        }
        finally
        {
            if (previousTuning is not null) UnlockTuningPolicy.Configure(previousTuning);
        }
    }
}
