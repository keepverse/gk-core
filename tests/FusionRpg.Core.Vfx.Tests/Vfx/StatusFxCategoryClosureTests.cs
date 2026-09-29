using FusionRpg.Core.Status;
using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>
/// combat-math-dedup D17 (backlog-clean-up BCU8.1, 2026-09-20): <see cref="VfxSeedCatalog.StatusFx"/>
/// once claimed "one row per catalog status" while silently sitting at 21 rows against
/// <see cref="StatusCategoryRegistry"/>'s 24 (missing all three `nerve.*` tiers) — a real, live drift,
/// not a hypothetical one.
///
/// <para><b>Real hazard found while writing this test, corrected before landing:</b> the first draft
/// asserted closure against <see cref="StatusCategoryRegistry.AllStatusIds"/> directly. That property
/// reflects a SHARED, MUTABLE, process-wide static dictionary — <c>ExhaustionPolicy</c> and
/// <c>StanceRuntime</c>'s own instance constructors call <c>StatusCategoryRegistry.Register</c> for
/// `exhaustion.*`/`stance.guard` at construction time, not at a fixed compile-time point, so the set
/// this property returns depends on which OTHER tests happened to construct one of those types earlier
/// in the SAME process — exactly the "tests that touch process-wide state" hazard this repo's own
/// hard-edge rule names. Proven live: the full `FusionRpg.Core.Tests` run failed this exact assertion
/// (`stance.guard, exhaustion.stamina, exhaustion.qi` missing) while the Vfx-namespace-only run did
/// not, purely because of unrelated test-class load order, not because of anything this fix changed.
/// Those three ids are a DIFFERENT subsystem's own deliberate runtime extension of the registry
/// (`StatusCategoryRegistry.Register`'s own doc comment: "e.g. the action program's exhaustion
/// debuffs") — whether they need a VFX cue is that subsystem's own authoring decision, not a
/// side-effect this fix should force. This test instead pins the STATIC, hand-authored 24-entry
/// vocabulary declared directly in <c>StatusCategoryRegistry.cs</c>'s own <c>Map</c> initializer — a
/// closed vocabulary a developer edits, not a derived population (validation-ssot.md) — so it stays
/// deterministic regardless of what else ran first in the process.</para>
/// </summary>
public class StatusFxCategoryClosureTests
{
    /// <summary>The exact 24 ids `StatusCategoryRegistry.cs`'s own `Map` initializer declares —
    /// mirrored here on purpose rather than read via `AllStatusIds` (see the class doc comment for
    /// why). A change to that literal is a reviewed change to both files, matching the same
    /// closed-vocabulary discipline `VfxRulesAndCatalogTests`'s own catalog-count pin already uses.</summary>
    static readonly string[] StaticallyDeclaredCategoryIds =
    {
        "wither", "poison", "leech", "bond", "rally", "expose", "command", "shatter",
        "butter", "freeze", "cold", "hypno", "ember", "jala", "kelp", "charm_pulse",
        "blight", "rot", "spark", "pact_mark", "spore",
        "nerve.unsettled", "nerve.shaken", "nerve.afflicted"
    };

    [Fact]
    public void Every_statically_declared_registry_status_has_a_StatusFx_apply_cue_row()
    {
        var statusFxIds = VfxSeedCatalog.StatusFx
            .Select(row => row.Id.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var missing = StaticallyDeclaredCategoryIds
            .Where(id => !statusFxIds.Contains(id.ToLowerInvariant()))
            .ToList();
        Assert.True(missing.Count == 0,
            $"StatusCategoryRegistry.cs's static Map has status id(s) with no VfxSeedCatalog.StatusFx apply-cue row: {string.Join(", ", missing)}");

        // Cross-check the pinned list itself is honest: every one of these ids really does resolve
        // through the live registry (proves the mirror hasn't drifted from the source it copies).
        foreach (var id in StaticallyDeclaredCategoryIds)
            Assert.True(StatusCategoryRegistry.TryGetCategory(id, out _), id);
    }

    [Fact]
    public void The_three_nerve_tiers_each_produce_a_real_transient_apply_cue()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        foreach (var id in new[] { "nerve.unsettled", "nerve.shaken", "nerve.afflicted" })
        {
            Assert.True(catalog.TryGet(StatusVfxCues.CueId(id), out var recipe), id);
            Assert.All(recipe.Primitives, p => Assert.True(p.IsSustained || p.LifeSeconds > 0f));
            // Same shape as the engine-wrapped vanilla statuses (StatusVfxCuesTests) -- a transient
            // apply flash, no sustained aura -- since no StatusSustainFx row names any nerve.* tier.
            Assert.False(recipe.HasSustained, id + " has no authored sustained composition yet");
        }
    }
}
