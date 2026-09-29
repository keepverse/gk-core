using System.Text.Json;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>spec-catalog-extension.md §6.1 — the structural half of T2's acceptance: the 157-channel
/// extension resolves, the seed catalog mirrors the code exactly (proven, not assumed), and no
/// non-element family leaked into the generated combat roster.</summary>
public class SeedCatalogTests
{
    [Fact]
    public void AllCombatChannelsRegisterExactlyOnce()
    {
        // Derived, not literal: the two independent sources (the family-count formula and the
        // registered-def count) must agree, and whatever they agree ON is the assertion.
        var combatExpected = DerivedStatChannels.CombatChannelFamilies.Count * (ElementRoster.Concrete.Count + 1);
        var registry = DerivedStatRegistry.CreateDefault();

        foreach (var channelId in DerivedStatChannels.AllCombatChannelIds)
            Assert.True(registry.IsKnown(channelId), $"missing combat channel: {channelId}");

        Assert.Equal(combatExpected, DerivedStatChannels.AllCombatChannelIds.Count);
        // solid-enforcement population-pin (SE3.2, 2026-09-19): this used to also assert
        // `Assert.Equal(268, registry.AllRegistered.Count)` -- an unmarked, unowned literal for the
        // registry's OVERALL count (not just combat channels). `SeedCatalogMatchesCode()` below
        // already proves the real contract this file exists for (its own class doc: "the seed
        // catalog mirrors the code exactly") -- full SET equality between catalog.json's expanded
        // entries and the registry's registered ids, in both directions, which strictly implies
        // equal count. A bare count literal added nothing this test doesn't already prove for real.
    }

    [Fact]
    public void NonElementFamiliesStayOutOfCombatRoster()
    {
        // actor-hub-ssot.md §3G rule 1 / §H.3: a non-element family that joins AllCombatChannelIds
        // breaks the roster assertion AND gets swept into element expansion.
        var combatIds = DerivedStatChannels.AllCombatChannelIds;
        Assert.DoesNotContain(combatIds, id => id.StartsWith("resource.", StringComparison.Ordinal));
        Assert.DoesNotContain(combatIds, id => id.StartsWith("skill.", StringComparison.Ordinal));
        Assert.DoesNotContain(combatIds, id => id.StartsWith("progression.", StringComparison.Ordinal));
        Assert.DoesNotContain(combatIds, id => id.StartsWith("move.", StringComparison.Ordinal));
        Assert.DoesNotContain(combatIds, id => id.StartsWith("status.", StringComparison.Ordinal));
    }

    [Fact]
    public void StatusResistElementIdResolvesAsAValidChannel()
    {
        // The registration half of Q1 (spec-catalog-extension.md §2 — "adds zero channels", already
        // true before T2 via the open status.resist. prefix). The COMBINE-RULE half — status.resist
        // .{element} actually being summed into totalResist as its own term — is status-potency's
        // (T4.2), not this module's; asserting that here would claim a reader this phase never wires.
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryResolveChannel("status.resist.fire", out var def));
        Assert.Equal(StatClass.Contest, def.Class);
    }

    [Fact]
    public void UnknownChannelStillRejects()
    {
        // The reject rule survives a 2.6x larger catalog (99 -> 256) — a plausible-but-wrong id (an
        // element that does not exist, on a family that does) must not accidentally start resolving
        // through some new open prefix T2 introduced.
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.Throws<UnknownDerivedChannelException>(() => registry.ValidateChannel("combat.power.water"));
        Assert.Throws<UnknownDerivedChannelException>(() => registry.ValidateChannel("combat.penetration.water"));
        Assert.Throws<UnknownDerivedChannelException>(() => registry.ValidateChannel("resource.max.mana"));
        Assert.Throws<UnknownDerivedChannelException>(() => registry.ValidateChannel("skill.cooldown.ultimate"));
    }

    [Fact]
    public void MatchedActorsUnchanged()
    {
        // Registration alone changes nothing: a composer fed the same modifiers before and after T2
        // must resolve to byte-identical values. Reuses DerivedStatRegistryTests' own asserted numbers
        // (Combat_power_flat_modifier_sums, Composer_neutral_stub_defaults) as the "before" snapshot —
        // those tests are unmodified by T2 and still pass, which is the golden-stability half of this
        // claim; this test adds the "and a fresh actor pair, fully composed, also agrees" half.
        var composer = new DerivedComposer();
        var attacker = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatPowerFire, DerivedModifierOp.Flat, 12.0),
            new DerivedModifier(DerivedStatChannels.CombatCritRateOmni, DerivedModifierOp.Flat, 30.0)
        });
        var defender = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatDefenseFire, DerivedModifierOp.Flat, 5.0),
            new DerivedModifier(DerivedStatChannels.CombatDodgeOmni, DerivedModifierOp.Flat, 10.0)
        });

        Assert.Equal(12.0, attacker.Get(DerivedStatChannels.CombatPowerFire));
        Assert.Equal(30.0, attacker.Get(DerivedStatChannels.CombatCritRateOmni));
        Assert.Equal(5.0, defender.Get(DerivedStatChannels.CombatDefenseFire));
        Assert.Equal(10.0, defender.Get(DerivedStatChannels.CombatDodgeOmni));

        // Every new H.1-H.7 channel neither actor touched defaults to exactly 0 on both sides —
        // registration is arithmetically a no-op until a reader exists.
        Assert.Equal(0, attacker.Get(DerivedStatChannels.CombatPenetrationPrefix + ".fire"));
        Assert.Equal(0, defender.Get(DerivedStatChannels.CombatAbsorptionPrefix + ".fire"));
        Assert.Equal(0, attacker.Get(DerivedStatChannels.ResourceRestore("hp")));
        Assert.Equal(0, attacker.Get(DerivedStatChannels.ResourceMax("hp")));
        Assert.Equal(0, attacker.Get(DerivedStatChannels.SkillCooldown("attack")));
        Assert.Equal(0, attacker.Get(DerivedStatChannels.MoveRange));
        Assert.Equal(0, attacker.Get(DerivedStatChannels.ProgressionXpRate));
    }

    [Fact]
    public void SeedCatalogMatchesCode()
    {
        var expected = ExpandCatalogEntries().Keys.ToHashSet(StringComparer.Ordinal);
        var registry = DerivedStatRegistry.CreateDefault();
        var actual = registry.AllRegistered.Select(d => d.ChannelId).ToHashSet(StringComparer.Ordinal);

        var missingFromCode = expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missingFromCatalog = actual.Except(expected).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missingFromCode.Count == 0,
            "catalog.json names channels CreateDefault() does not register: " + string.Join(", ", missingFromCode));
        Assert.True(missingFromCatalog.Count == 0,
            "CreateDefault() registers channels catalog.json does not name: " + string.Join(", ", missingFromCatalog));
    }

    [Fact]
    public void SeedCatalogFieldsMatchCode()
    {
        // Found by the T4.4/T4.5 adversarial audit (2026-08-25): SeedCatalogMatchesCode only ever
        // diffed the SET of channel-id strings, so when T4.4 switched resource.efficiency.*/
        // progression.breakthroughSuccess from FlatSum (no Cap) to SumIncreased + Cap in the C#
        // registry, catalog.json -- guard-stat-pairs.ps1's own documented "machine-readable mirror"
        // -- silently kept the stale compose/cap/statClass and nothing caught it. This closes that
        // gap: every entries row's compose/cap/statClass must match what CreateDefault() actually
        // registers for every channel id that row expands to, not just that the id exists somewhere.
        var expected = ExpandCatalogEntries();
        var registry = DerivedStatRegistry.CreateDefault();

        var mismatches = new List<string>();
        foreach (var (channelId, meta) in expected)
        {
            if (!registry.TryGet(channelId, out var def))
                continue; // SeedCatalogMatchesCode already reports a missing channel; do not double-report

            if (def.Compose != meta.Compose)
                mismatches.Add($"{channelId}: compose catalog={meta.Compose} code={def.Compose}");
            if (!meta.CapIsDocumentary && def.Cap != meta.Cap)
                mismatches.Add($"{channelId}: cap catalog={meta.Cap?.ToString() ?? "null"} code={def.Cap?.ToString() ?? "null"}");
            if (def.Class != meta.Class)
                mismatches.Add($"{channelId}: statClass catalog={meta.Class?.ToString() ?? "null"} code={def.Class?.ToString() ?? "null"}");
        }

        Assert.True(mismatches.Count == 0,
            "catalog.json drifted from the registered DerivedStatDef:\n" + string.Join("\n", mismatches));
    }

    readonly record struct CatalogChannelMeta(DerivedComposeKind Compose, double? Cap, bool CapIsDocumentary, StatClass? Class);

    /// <summary>Expands every `entries` row in catalog.json over its declared axis — families plus the
    /// axis they expand over, never a hand-listed channel (mirrors DerivedStatChannels' own
    /// generation) — carrying each row's compose/cap/statClass along so a field-level drift is
    /// provable, not just a missing/extra id. `prefixFamilies` (the sparse, open-ended overrides) are
    /// deliberately excluded: they have no fixed id set to expand and are never part of the static
    /// registry.</summary>
    static Dictionary<string, CatalogChannelMeta> ExpandCatalogEntries()
    {
        var path = FindCatalogPath();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var elementSlots = new[] { ElementRoster.OmniId }.Concat(ElementRoster.Concrete.Select(e => e.ToElementId())).ToList();
        string[] statusCategories = { "omni", "dot", "cc", "contagion" };
        string[] actionCategories = { "attack", "defense", "support", "movement", "status" };
        // Reads the live list rather than a hand-duplicated literal (was a 5-element copy that went
        // stale the moment `poise` was appended, 2026-08-26 -- the exact drift this test exists to
        // catch, just one level removed). A future resource id needs no edit here either.
        var resourceIds = DerivedStatChannels.ResourceIds.ToArray();

        var result = new Dictionary<string, CatalogChannelMeta>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("entries").EnumerateArray())
        {
            var family = entry.GetProperty("family").GetString()!;
            var axis = entry.GetProperty("axis").GetString()!;
            var slots = axis switch
            {
                "none" => new[] { "" },
                "element" => elementSlots.ToArray(),
                "status-category" => statusCategories,
                "action-category" => actionCategories,
                "resource-id" => resourceIds,
                _ => throw new InvalidOperationException($"catalog.json: unknown axis '{axis}' on family '{family}'")
            };

            var compose = Enum.Parse<DerivedComposeKind>(entry.GetProperty("compose").GetString()!);
            var capEl = entry.GetProperty("cap");
            // A handful of entries (e.g. "status.power": "MaxNetFactor") carry a DOCUMENTARY string
            // pointing at the policy constant that actually governs the value, not a literal number to
            // compare -- skip the Cap check for those. Same treatment when a row carries a `capNote`
            // (currently only "status.resist"): the note itself is the author explicitly flagging that
            // one flat `cap` value does not hold for every slot the row's axis expands to (there, omni
            // stays uncapped while dot/cc/contagion cap at 0.95 -- StatusResistOmni really is
            // registered with no Cap in DerivedStatRegistry.cs, unlike its three siblings). The
            // catalog's row-level model has no way to express a per-slot exception directly, so the
            // note is the source of truth for those rows; comparing the flat field would spuriously
            // fail on the very slot the note exists to call out.
            var capIsDocumentary = capEl.ValueKind == JsonValueKind.String || entry.TryGetProperty("capNote", out _);
            var cap = capEl.ValueKind == JsonValueKind.Number ? capEl.GetDouble() : (double?)null;
            var statClassRaw = entry.GetProperty("statClass").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("statClass").GetString();
            var statClass = statClassRaw is null ? (StatClass?)null : Enum.Parse<StatClass>(statClassRaw);
            var meta = new CatalogChannelMeta(compose, cap, capIsDocumentary, statClass);

            foreach (var slot in slots)
                result[slot.Length == 0 ? family : $"{family}.{slot}"] = meta;
        }

        return result;
    }

    static string FindCatalogPath() => Path.Combine(ContentRoot.Path, "data", "seed", "derived-stats", "catalog.json");
}
