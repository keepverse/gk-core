using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T30 (`creature-drop-tables` E1) — the ninth `source_kind` and its paired arm.
///
/// <para>A creature kill is a loot source with its own per-rung tables. The source row is resolved
/// at kill time (per-kill source id, never authored), the tables are authored per rung in
/// `gk-data/packs/fusion/data/seed/loot/tables-creature.json`, and the two lists the validator warns about — the kind
/// vocabulary and the `LootCorrelation.Derive` arms — land together.</para>
/// </summary>
[Trait("VerificationId", "core.creature-kill-loot")]
public class CreatureKillLootSourceTests
{
    // ---- the closed vocabulary + the paired arm, in one commit ------------------------------------

    [Fact]
    public void Creature_kill_is_the_ninth_known_source_kind()
    {
        Assert.Contains("creature-kill", DropTableValidator.KnownSourceKinds);
        Assert.Equal("creature-kill", CreatureKillLootSource.SourceKind);
    }

    [Theory]
    [InlineData("dolldiamond:exp-7:3", "loot:kill:dolldiamond:exp-7:3")]
    [InlineData("bigpumpkin:delve-1:0", "loot:kill:bigpumpkin:delve-1:0")]
    public void The_derive_arm_is_prefix_plus_source_id_verbatim(string sourceId, string expected)
    {
        Assert.Equal(expected, LootCorrelation.Derive("creature-kill", sourceId));
        // Deterministic, not just formatted once — a retried kill replays instead of minting twice.
        Assert.Equal(LootCorrelation.Derive("creature-kill", sourceId), LootCorrelation.Derive("creature-kill", sourceId));
    }

    [Fact]
    public void Two_kills_never_collide_on_correlation()
    {
        var a = LootCorrelation.Derive("creature-kill", "dolldiamond:exp-7:3");
        var b = LootCorrelation.Derive("creature-kill", "dolldiamond:exp-7:4");
        var c = LootCorrelation.Derive("creature-kill", "bigpumpkin:exp-7:3");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    // ---- the resolver -----------------------------------------------------------------------------

    [Fact]
    public void A_kill_resolves_to_its_rungs_table_with_the_callers_content_level()
    {
        Assert.True(CreatureKillLootSource.TryResolve("dolldiamond", "chimeric", "exp-7:3", 6, out var source).IsOk);
        Assert.Equal("creature-kill", source!.SourceKind);
        Assert.Equal("dolldiamond:exp-7:3", source.SourceId);
        Assert.Equal("drop.creature.rung-chimeric", source.TableId);
        Assert.Equal(6, source.ContentLevel);
    }

    [Theory]
    [InlineData("", "chimeric", "exp-7:3", 6, "species")]
    [InlineData("dolldiamond", "not-a-rung", "exp-7:3", 6, "rung")]
    [InlineData("dolldiamond", "chimeric", "", 6, "kill")]
    [InlineData("dolldiamond", "chimeric", "exp-7:3", 0, "content")]
    public void A_kill_missing_any_input_is_refused_by_name(
        string speciesId, string rungId, string killRef, int contentLevel, string names)
    {
        var result = CreatureKillLootSource.TryResolve(speciesId, rungId, killRef, contentLevel, out var source);
        Assert.False(result.IsOk);
        Assert.Null(source);
        Assert.Contains(names, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_rung_resolves_to_its_table_id()
    {
        foreach (var rungId in RarityLadder.RungIds)
            Assert.Equal($"drop.creature.rung-{rungId}", CreatureKillLootSource.TableIdFor(rungId));
    }

    // ---- the authored tables ----------------------------------------------------------------------

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    static LootCorpus CreatureCorpus() => LootCorpusReader.Merge(new[]
    {
        LootCorpusReader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "loot", "tables-creature.json"))),
    });

    [Fact]
    public void Every_rung_has_its_table_with_material_and_equipment_entries()
    {
        var byId = CreatureCorpus().Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);
        Assert.Equal(RarityLadder.RungIds.Count, byId.Count);

        foreach (var rungId in RarityLadder.RungIds)
        {
            var table = Assert.Contains($"drop.creature.rung-{rungId}", (IDictionary<string, DropTableRow>)byId);
            Assert.Contains("web", table.SourceAllow);

            // Scoped to the ORIGINAL two groups (species-gear-chain T30) -- the trophy groups T34c
            // added are a separate shape, asserted in their own test below, so this one keeps
            // guarding exactly the fact it always guarded.
            var ordinaryEntries = table.Groups.Where(g => g.GroupKey is "mats" or "rare")
                .SelectMany(g => g.Entries).ToList();
            var kinds = ordinaryEntries.Select(e => e.Kind).ToList();
            Assert.Contains(DropEntryKind.Material, kinds);
            Assert.Contains(DropEntryKind.Equipment, kinds);
            // The *1 generic + 3 commons + 1 rare gate* shape: four materials, one gated equipment.
            Assert.Equal(4, kinds.Count(k => k == DropEntryKind.Material));
            Assert.Single(kinds.Where(k => k == DropEntryKind.Equipment));
            // The generic is the rung's own shard — the kill drops what it was.
            Assert.Contains(ordinaryEntries,
                e => e.Kind == DropEntryKind.Material && e.RefId == $"shard.{rungId}");
        }
    }

    [Fact]
    public void Every_rung_carries_the_species_gear_chain_t34c_trophy_groups()
    {
        var corpus = CreatureCorpus();
        foreach (var rungId in RarityLadder.RungIds)
        {
            var table = corpus.Tables.Single(t => t.TableId == $"drop.creature.rung-{rungId}");
            var speciesGroup = Assert.Single(table.Groups, g => g.GroupKey == "species-trophy");
            var familyGroup = Assert.Single(table.Groups, g => g.GroupKey == "family-trophy");

            var speciesEntries = speciesGroup.Entries.Where(e => e.Kind == DropEntryKind.Material).ToList();
            Assert.Equal(2, speciesEntries.Count); // perSpecies (data/tuning/species-material-run.v1.json)
            Assert.All(speciesEntries, e => Assert.Equal("species", e.TrophyScope));
            Assert.Equal(new[] { 1, 2 }, speciesEntries.Select(e => e.TrophySlot!.Value).OrderBy(s => s));

            var familyEntries = familyGroup.Entries.Where(e => e.Kind == DropEntryKind.Material).ToList();
            Assert.Equal(8, familyEntries.Count); // perFamily
            Assert.All(familyEntries, e => Assert.Equal("family", e.TrophyScope));
            Assert.Equal(Enumerable.Range(1, 8), familyEntries.Select(e => e.TrophySlot!.Value).OrderBy(s => s));

            // A trophy entry names no literal id at all -- scope + slot instead, resolved at draw time.
            Assert.All(speciesEntries.Concat(familyEntries), e => Assert.Equal("", e.RefId));
        }
    }

    [Fact]
    public void No_new_material_id_and_no_new_entry_kind()
    {
        var issuable = MaterialCatalog.All.ToHashSet(StringComparer.Ordinal);
        foreach (var table in CreatureCorpus().Tables)
            foreach (var e in table.Groups.SelectMany(g => g.Entries))
            {
                Assert.Contains(e.Kind, Enum.GetValues<DropEntryKind>());
                // species-gear-chain T34c: a trophy-scoped Material entry names no literal id (scope
                // + slot instead) -- correctly OUTSIDE the closed-27 vocabulary this check guards,
                // never a violation of it.
                if (e.Kind == DropEntryKind.Material && e.TrophyScope is null)
                    Assert.True(issuable.Contains(e.RefId),
                        $"{table.TableId}: material '{e.RefId}' is not one of the closed 27");
            }
    }

    [Fact]
    public void Weights_are_per_million_scale_and_above_the_shipped_floor()
    {
        foreach (var table in CreatureCorpus().Tables)
            foreach (var g in table.Groups)
                foreach (var e in g.Entries)
                    Assert.True(e.Weight >= 1,
                        $"{table.TableId}/{g.GroupKey}: weight {e.Weight} is below the 1/million floor");
    }

    // ---- end to end through the real pipeline -------------------------------------------------------

    [Fact]
    public void A_resolved_kill_source_draws_from_its_table_through_the_real_pipeline()
    {
        Assert.True(CreatureKillLootSource.TryResolve("dolldiamond", "chimeric", "exp-7:3", 6, out var source).IsOk);
        var table = CreatureCorpus().Tables.Single(t => t.TableId == source!.TableId);
        var baseTypes = DropVolumeCorpusTests.BaseTypes();
        var view = new LootContentView(
            new Dictionary<string, LootSourceRow> { [source!.Key] = source },
            new Dictionary<string, DropTableRow> { [table.TableId] = table },
            DropVolumeCorpusTests.Ladder(),
            (frame, role) => baseTypes.TryGetValue((frame, role), out var l)
                ? l
                : (IReadOnlyList<string>)Array.Empty<string>());

        var request = new LootRequest("player-1", source.SourceKind, source.SourceId, 0xBEE5UL, ThetaActor: 20);
        Assert.True(LootPipeline.Resolve(request, view, DropVolumeTests.Tuning(), LootPityState.Empty, out var manifest).IsOk);
        Assert.Equal("loot:kill:dolldiamond:exp-7:3", manifest!.CorrelationId);
        Assert.NotEmpty(manifest.Grants);
        // The generic leg drew the rung's own shard — the kill drops what it was.
        Assert.Contains(manifest.Grants, g => g is { Kind: DropEntryKind.Material, RefId: "shard.chimeric" } ||
                                               g.Kind == DropEntryKind.Equipment);
    }
}
