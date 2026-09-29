using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// E38 (spec-entity-fields-12plus.md): twelve more Unity fields — plantShield, attackCountdown,
/// attackSpeedAdder, produceCountdown, plantSpeed, plantMoveSpeed, plantLevel, shootingLevel,
/// armorFlat, takeDmgMultiplier, zombieSpeedCurrent, zombieOriginSpeed — become real composed
/// channels. "E16 run a second time": same shape as <see cref="ChannelExtensionGuardTests"/>,
/// twelve times over. Text-based, because the injector assembly needs a real PVZ Fusion install
/// and never builds under CI (spec §4's own note) — these guards are the only regression coverage
/// the writer half gets.
/// </summary>
public class EntityFields12PlusGuardTests
{
    [Fact]
    public void The_extras_path_no_longer_writes_the_twelve_promoted_fields()
    {
        var text = ReadInjector(System.IO.Path.Combine("Stats", "EntityStatWriter.cs"));

        Assert.DoesNotContain("p.theShieldHealth = CheatState.IVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p.thePlantAttackCountDown = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p.attackSpeedAdder = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p.thePlantProduceCountDown = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p.thePlantSpeed = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p.moveSpeed = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p.theLevel = CheatState.IVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p.shootingLevel = CheatState.IVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("z.theArmor = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("z.takeDmgMultiplier = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("z.theSpeed = CheatState.FVal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("z.theOriginSpeed = CheatState.FVal", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ Reads CODE ONLY — comment lines are stripped first. Every assertion in this file used to run
    /// against the raw file text, and <c>EntityStatWriter.cs</c>'s own note says why that is dangerous:
    /// "the guard that keeps them gone reads THIS FILE as text … it cannot tell one from code." When a
    /// write is retired the honest way — commented out with the ruling that retired it written next to
    /// it, as attack was on 2026-09-16 — a raw <c>Assert.Contains</c> keeps passing on the comment.
    /// That is a false green: the guard reports the field is still written when it is not.
    /// </summary>
    static string CodeOf(string relative)
    {
        var lines = ReadInjector(relative).Split('\n');
        var code = lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join("\n", code);
    }

    [Fact]
    public void The_writer_does_write_the_nine_fields_PvZ_alone_owns()
    {
        // The other half of the guard above. Without it, that one passes just as well when the fields
        // are never written at all — a silent capability loss, not a fix (same reasoning as E16's own
        // sibling test in ChannelExtensionGuardTests).
        //
        // Nine, not twelve: plantShield, armorFlat and takeDmgMultiplier moved to the retired set
        // below on 2026-09-16. What remains are the fields whose concept exists ONLY in PvZ — firing
        // and producing cadence, movement, and the two display levels. The RPG has no resolver for
        // any of them, so writing them is not a second payment of anything.
        var code = CodeOf(System.IO.Path.Combine("Stats", "EntityStatWriter.cs"));

        Assert.Contains("p.thePlantAttackCountDown = (float)y.AttackCountdown", code, StringComparison.Ordinal);
        Assert.Contains("p.attackSpeedAdder = (float)y.AttackSpeedAdder", code, StringComparison.Ordinal);
        Assert.Contains("p.thePlantProduceCountDown = (float)y.ProduceCountdown", code, StringComparison.Ordinal);
        Assert.Contains("p.thePlantSpeed = (float)y.PlantSpeed", code, StringComparison.Ordinal);
        Assert.Contains("p.moveSpeed = (float)y.PlantMoveSpeed", code, StringComparison.Ordinal);
        Assert.Contains("p.theLevel = ClampToInt32Reporting(y.PlantLevel, \"plant.level\", source)", code, StringComparison.Ordinal);
        Assert.Contains("p.shootingLevel = ClampToInt32Reporting(y.ShootingLevel, \"plant.shootingLevel\", source)", code, StringComparison.Ordinal);
        Assert.Contains("z.theSpeed = (float)y.ZombieSpeedCurrent", code, StringComparison.Ordinal);
        Assert.Contains("z.theOriginSpeed = (float)y.ZombieOriginSpeed", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Owner ruling 2026-09-16, extending the attack ruling of the same day: <b>no term of the damage
    /// equation is written into PvZ — the RPG's own damage calculator resolves it.</b> Attack went
    /// first (offence); these three are the absorption and mitigation terms, and each one duplicates a
    /// resolver the RPG already runs during a lawn match:
    ///
    /// <list type="bullet">
    /// <item><c>theShieldHealth</c> — <c>ShieldRuntime</c> / <c>ShieldGate</c>, plus the registered
    /// <c>combat.shield.capacity|toughness|pen|regen</c> channels.</item>
    /// <item><c>theArmor</c> — <c>combat.defense.omni</c> and its seven element channels, read by
    /// <c>OverlayCombatCalculator</c>.</item>
    /// <item><c>takeDmgMultiplier</c> — the same calculator, and a LEGAL passive-tree target
    /// (<c>ChannelLegality.LowerIsBetterPrimaries</c>, <c>AtomKindRegistry.PrimaryChannels</c>), so
    /// the double-pay was latent rather than hypothetical.</item>
    /// </list>
    ///
    /// <para>All three stay COMPOSED — the sheet, the power price and every proof still read them.
    /// Only the Unity write is gone. That is the same shape the attack removal took.</para>
    /// </summary>
    [Fact]
    public void The_writer_does_not_write_the_three_damage_equation_terms()
    {
        var code = CodeOf(System.IO.Path.Combine("Stats", "EntityStatWriter.cs"));

        Assert.DoesNotContain("p.theShieldHealth =", code, StringComparison.Ordinal);
        Assert.DoesNotContain("z.theArmor =", code, StringComparison.Ordinal);
        Assert.DoesNotContain("z.takeDmgMultiplier =", code, StringComparison.Ordinal);

        // And the offence term the ruling started with, pinned in the same place so the whole rule
        // reads as one thing rather than two half-rules in different files.
        Assert.DoesNotContain("p.attackDamage =", code, StringComparison.Ordinal);
        Assert.DoesNotContain("z.theAttackDamage =", code, StringComparison.Ordinal);
    }

    [Fact]
    public void P_ATK_ADD_stays_unguarded_by_a_value_check()
    {
        // Pinned per §2b's decision (owner removed themselves as a gate, 2026-09-03): adding a value
        // guard to P-ATK-ADD is a behaviour change to a shipped operator key, not a bugfix. This test
        // forces a conscious edit — and a re-read of the spec's own reasoning — if a later session
        // ever adds one back, in either the map-building function or the writer.
        var cheatState = ReadInjector("CheatState.cs");
        Assert.DoesNotContain("IsUserSet(\"P-ATK-ADD\") && ", cheatState, StringComparison.Ordinal);

        var writer = ReadInjector(System.IO.Path.Combine("Stats", "EntityStatWriter.cs"));
        Assert.DoesNotContain("y.AttackSpeedAdder >", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("y.AttackSpeedAdder <", writer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cheat_keys_still_reach_the_fields_as_overrides()
    {
        // The operator surface is unchanged: each key still works, it just arrives the way P-HP
        // always has, through BuildPlantAbsoluteReal / BuildZombieAbsoluteReal.
        var text = ReadInjector("CheatState.cs");
        foreach (var key in new[]
                 {
                     "P-SHIELD", "P-ATK-CD", "P-ATK-ADD", "P-PROD-CD", "P-SPEED", "P-MOVE",
                     "P-LEVEL", "P-SHOOTLVL", "Z-ARMOR-F", "Z-TAKEMULT", "Z-SPD", "Z-SPD-O",
                 })
            Assert.Contains(key, text, StringComparison.Ordinal);

        Assert.Contains("BuildPlantAbsoluteReal", text, StringComparison.Ordinal);
        Assert.Contains("BuildZombieAbsoluteReal", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_three_guard_shapes_are_each_preserved_in_the_map_builder()
    {
        // §2b's own three shapes, each pinned against the exact map-building code, not merely the
        // key's presence: a `>= 0` filter for the seven legal-zero keys, a `> 0` filter (unchanged
        // from today) for the four zero-refusing keys, and no filter at all for P-ATK-ADD.
        var text = ReadInjector("CheatState.cs");

        Assert.Contains("if (v >= 0) d[channel] = v;", text, StringComparison.Ordinal); // PutGe0
        Assert.Contains("if (IsUserSet(\"P-ATK-ADD\"))", text, StringComparison.Ordinal);
        Assert.Contains("if (IsUserSet(\"Z-ARMOR-F\") && FVal(\"Z-ARMOR-F\") >= 0)", text, StringComparison.Ordinal);
        Assert.Contains("if (IsUserSet(\"Z-TAKEMULT\") && FVal(\"Z-TAKEMULT\") >= 0)", text, StringComparison.Ordinal);
        Assert.Contains("if (IsUserSet(\"Z-SPD\") && FVal(\"Z-SPD\") > 0)", text, StringComparison.Ordinal);
        Assert.Contains("if (IsUserSet(\"Z-SPD-O\") && FVal(\"Z-SPD-O\") > 0)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cheat_absolute_plugin_no_longer_re_filters_real_valued_overrides_by_sign()
    {
        // The second half of the guard shape: even a correctly-built map is worthless if
        // CheatAbsoluteStatPlugin re-drops a legal zero (or P-ATK-ADD's legal negative) with its own
        // blanket filter. This is the file that WAS the actual bug risk (§2b's own citations point at
        // the map-building functions, but the redundant filter one layer downstream was the same
        // class of defect, one level deeper).
        var text = ReadInjector(
            System.IO.Path.Combine("src", "FusionRpg.Core", "Stats", "Plugins", "CheatAbsoluteStatPlugin.cs"),
            fromInjector: false);

        Assert.DoesNotContain("kv.Value <= 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_baselines_are_captured()
    {
        var text = ReadInjector(System.IO.Path.Combine("Stats", "EntityApply.cs"));

        Assert.Contains("PlantShield = p.theShieldHealth", text, StringComparison.Ordinal);
        Assert.Contains("AttackCountdown = p.thePlantAttackCountDown", text, StringComparison.Ordinal);
        Assert.Contains("AttackSpeedAdder = p.attackSpeedAdder", text, StringComparison.Ordinal);
        Assert.Contains("ProduceCountdown = p.thePlantProduceCountDown", text, StringComparison.Ordinal);
        Assert.Contains("PlantSpeed = p.thePlantSpeed", text, StringComparison.Ordinal);
        Assert.Contains("PlantMoveSpeed = p.moveSpeed", text, StringComparison.Ordinal);
        Assert.Contains("PlantLevel = p.theLevel", text, StringComparison.Ordinal);
        Assert.Contains("ShootingLevel = p.shootingLevel", text, StringComparison.Ordinal);
        Assert.Contains("ArmorFlat = z.theArmor", text, StringComparison.Ordinal);
        Assert.Contains("TakeDmgMultiplier = z.takeDmgMultiplier", text, StringComparison.Ordinal);
        Assert.Contains("ZombieSpeedCurrent = z.theSpeed", text, StringComparison.Ordinal);
        Assert.Contains("ZombieOriginSpeed = z.theOriginSpeed", text, StringComparison.Ordinal);
    }

    static string ReadInjector(string relative, bool fromInjector = true)
    {
        var root = FindRepoRoot();
        var path = fromInjector
            ? System.IO.Path.Combine(root, "src", "FusionRpg.Injector", relative)
            : System.IO.Path.Combine(root, relative);
        Assert.True(System.IO.File.Exists(path), "missing " + path);
        return System.IO.File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new System.IO.DirectoryNotFoundException("repo root");
    }
}
