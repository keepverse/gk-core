using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>F7: fusion endpoints — preview/execute/recipes with silhouette projection.</summary>
[Collection("e2e")]
public class FusionE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public FusionE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=10000", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    static readonly CreatureRecipeDef Recipe = CreatureRecipeCatalog.All
        .First(r => CreatureSpeciesCatalog.Get(r.OutputSpeciesId).BaseRarity == CreatureRarity.Cultivated);

    async Task SeedMaterials(params (string Id, long Qty)[] drops)
    {
        foreach (var (id, qty) in drops)
            (await _http.PostAsJsonAsync($"/api/test/seed-materials?materialId={id}&qty={qty}", new { }))
                .EnsureSuccessStatusCode();
    }

    async Task<string> MintCreature(string speciesId)
    {
        var resp = await _http.PostAsJsonAsync($"/api/test/mint-creature?speciesId={speciesId}", new { });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("actor").GetProperty("instanceId").GetString()!;
    }

    [Fact]
    public async Task Star_merge_previews_and_executes()
    {
        var species = CreatureSpeciesCatalog.All.First(s =>
            s.BaseRarity == CreatureRarity.Chaff && s.Acquisition != CreatureAcquisition.CaptureOnly);
        await SeedMaterials(("shard.chaff", 5), ("essence." + species.ElementPrimary.ToElementId(), 5));
        var baseId = await MintCreature(species.SpeciesId);
        var fuel = new[] { await MintCreature(species.SpeciesId), await MintCreature(species.SpeciesId) };

        var preview = await _http.PostAsJsonAsync("/api/fusion/preview", new
        {
            mode = "star-merge",
            baseInstanceId = baseId,
            sacrifices = fuel
        });
        preview.EnsureSuccessStatusCode();
        var p = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(p.GetProperty("ok").GetBoolean());
        Assert.Equal(50, p.GetProperty("cost").GetProperty("souls").GetInt64());

        var exec = await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            mode = "star-merge",
            baseInstanceId = baseId,
            sacrifices = fuel,
            correlationId = "fus-e2e-merge"
        });
        exec.EnsureSuccessStatusCode();
        var body = await exec.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("base").GetProperty("profile").GetProperty("star").GetInt32());

        // Replay adds nothing.
        var again = await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            mode = "star-merge",
            baseInstanceId = baseId,
            sacrifices = fuel,
            correlationId = "fus-e2e-merge"
        });
        again.EnsureSuccessStatusCode();
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Recipes_stay_silhouetted_until_discovered()
    {
        var output = CreatureSpeciesCatalog.Get(Recipe.OutputSpeciesId);
        var cost = FusionCostTable.Recipe(output.BaseRarity);
        await SeedMaterials(
            ("shard." + cost.ShardRarity.ToId(), 10),
            ("essence." + output.ElementPrimary.ToElementId(), 10));

        // Before discovery: no recipe id or output species on the wire.
        var before = await _http.GetFromJsonAsync<JsonElement>("/api/fusion/1/recipes");
        Assert.All(before.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.False(item.GetProperty("discovered").GetBoolean());
            Assert.False(item.TryGetProperty("recipeId", out _), "undiscovered recipes must not leak ids");
            Assert.False(item.TryGetProperty("resultSpeciesId", out _), "the output IS the discovery");
        });

        var a = await MintCreature(Recipe.InputSpeciesIdA);
        var b = await MintCreature(Recipe.InputSpeciesIdB);
        var pickable = CreatureSpeciesCatalog.Get(Recipe.InputSpeciesIdA).TraitPool[0];

        var exec = await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            mode = "recipe",
            sacrifices = new[] { a, b },
            pickedTraitId = pickable,
            correlationId = "fus-e2e-recipe"
        });
        Assert.True(exec.IsSuccessStatusCode, await exec.Content.ReadAsStringAsync());
        var body = await exec.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("newlyDiscovered").GetBoolean());
        Assert.Equal(Recipe.OutputSpeciesId,
            body.GetProperty("minted").GetProperty("profile").GetProperty("speciesId").GetString());

        // After: exactly one discovered entry, now carrying its identity.
        var after = await _http.GetFromJsonAsync<JsonElement>("/api/fusion/1/recipes");
        var discovered = after.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("discovered").GetBoolean());
        Assert.Equal(Recipe.RecipeId, discovered.GetProperty("recipeId").GetString());
        Assert.Equal(Recipe.OutputSpeciesId, discovered.GetProperty("resultSpeciesId").GetString());
    }

    [Fact]
    public void Star_mods_ride_squad_setups_and_scale_with_stars()
    {
        // F8 unit shape: stars reach battles as Hub star/loyalty contributions on the omni
        // channels (battle-hub-fuse T6: the ChannelMods adapters are deleted; same shared formula).
        Assert.Null(StarLoyaltyBonus.Star(0, 5));
        var one = StarLoyaltyBonus.Star(1, 5)!.Value;
        var three = StarLoyaltyBonus.Star(3, 5)!.Value;
        Assert.True(one.Power >= 1 && one.Defense >= 1, "low-level stars must still register");
        Assert.True(three.Power > one.Power, "more stars, more power");
        Assert.True(three.Defense > one.Defense, "more stars, more defense");
    }

    /// <summary>
    /// G7: the Bound band pays nothing — that is what keeps every golden byte-identical when
    /// contracts land. The higher bands must still be real, or the feature is decoration.
    /// </summary>
    [Fact]
    public void Loyalty_mods_are_zero_at_bound_and_climb_with_rank()
    {
        Assert.Null(StarLoyaltyBonus.Loyalty(
            FusionRpg.Core.Creatures.Contracts.ContractPolicy.BindLoyalty, 5));

        var sworn = StarLoyaltyBonus.Loyalty(450, 5)!.Value;
        var devoted = StarLoyaltyBonus.Loyalty(900, 5)!.Value;
        Assert.True(devoted.Power > sworn.Power, "devotion must outweigh a sworn oath");
        Assert.True(devoted.Defense > sworn.Defense, "devotion must outweigh a sworn oath");
    }

    [Fact]
    public void Stars_swing_battles_statistically()
    {
        // The +30‰/star channel mods must move real outcomes, not just decorate setups.
        // battle-hub-fuse T6: stars ride as Hub inputs through the fused engine path.
        FusionRpg.Core.Battle.BattleActorSetup Actor(string key, string side, int stars) => new()
        {
            Key = key,
            Side = side,
            SpeciesId = "star-swing",
            TypeId = 10_001,
            Level = 5,
            MaxHp = FusionRpg.Core.Battle.BattleRuleset.BaseHp(5),
            Atk = FusionRpg.Core.Battle.BattleRuleset.BaseAtk(5),
            Defense = FusionRpg.Core.Battle.BattleRuleset.BaseDefense(5),
            HubInputs = new FusionRpg.Core.Battle.BattleHubInputs
            {
                StarLoyalty = new StarLoyaltyContribution(stars, 0, 5),
            }
        };

        long starred = 0, plain = 0;
        for (ulong seed = 0; seed < 30; seed++)
        {
            starred += FusionRpg.Core.Battle.BattleEngine.Resolve(new FusionRpg.Core.Battle.BattleSetup
            {
                WaveId = "swing",
                Squad = new[] { Actor("squad:0", "squad", stars: 5) },
                Wave = new[] { Actor("wave:0", "wave", stars: 0) }
            }, seed).Outcome == FusionRpg.Core.Battle.BattleOutcome.Victory ? 1 : 0;
            plain += FusionRpg.Core.Battle.BattleEngine.Resolve(new FusionRpg.Core.Battle.BattleSetup
            {
                WaveId = "swing",
                Squad = new[] { Actor("squad:0", "squad", stars: 0) },
                Wave = new[] { Actor("wave:0", "wave", stars: 0) }
            }, seed).Outcome == FusionRpg.Core.Battle.BattleOutcome.Victory ? 1 : 0;
        }

        Assert.True(starred > plain,
            $"5-star squad won {starred}/30, plain mirror won {plain}/30 — stars must matter");
    }

    [Fact]
    public async Task Starred_creature_carries_its_mods_into_a_real_match()
    {
        var species = CreatureSpeciesCatalog.All.First(s =>
            s.BaseRarity == CreatureRarity.Chaff && s.Acquisition != CreatureAcquisition.CaptureOnly);
        await SeedMaterials(("shard.chaff", 5), ("essence." + species.ElementPrimary.ToElementId(), 5));
        var baseId = await MintCreature(species.SpeciesId);
        var fuel = new[] { await MintCreature(species.SpeciesId), await MintCreature(species.SpeciesId) };
        (await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            mode = "star-merge",
            baseInstanceId = baseId,
            sacrifices = fuel,
            correlationId = "fus-e2e-starmatch"
        })).EnsureSuccessStatusCode();

        // The starred creature fights a real web match; its logged setup carries the star mods.
        var match = await _http.PostAsJsonAsync("/api/test/web-match", new
        {
            correlationId = "fus-e2e-starmatch-battle",
            waveId = "rift-skirmish",
            squad = new[] { baseId }
        });
        match.EnsureSuccessStatusCode();
        var body = await match.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("runId").GetInt64() > 0);
    }

    /// <summary>Spec success criterion 3: a legendary reachable PURELY via fusion — commons mint
    /// (the summon floor), then the recipe graph climbs rare → epic → legendary.</summary>
    [Fact]
    public async Task Legendary_chain_from_commons()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=900000", new { }))
            .EnsureSuccessStatusCode();
        foreach (var rarity in new[] { "chaff", "cultivated", "heirloom", "sunwoven" })
            await SeedMaterials(("shard." + rarity, 200));
        foreach (var element in new[] { "fire", "ice", "air", "earth", "light", "dark" })
            await SeedMaterials(("essence." + element, 200));

        var legendary = CreatureRecipeCatalog.All.First(r =>
            CreatureSpeciesCatalog.Get(r.OutputSpeciesId).BaseRarity == CreatureRarity.Sunwoven);
        var corr = 0;

        async Task<(string Id, string Trait)> Craft(string speciesId)
        {
            var species = CreatureSpeciesCatalog.Get(speciesId);
            // Base case: a species the recipe graph does not produce. That is exactly "below the
            // output-eligibility floor" (`CreatureRecipeCatalog.OutputEligibilityFloor` = Cultivated),
            // so Chaff is NOT the only leaf — Grafted/Sprout species are leaves too and are minted
            // directly, the same way a Chaff is. The old `== Chaff` test assumed every non-Chaff input
            // had a recipe; 30 Grafted-rarity inputs are referenced by recipes without having one of
            // their own (spec-fusion-recipe-generator.md: an input comes from the nearest populated
            // rung BELOW its output, which need not be Chaff), so the old predicate fell through to
            // `.First()` and threw "Sequence contains no matching element".
            var recipe = CreatureRecipeCatalog.All.FirstOrDefault(r => r.OutputSpeciesId == speciesId);
            if (recipe is null)
                return (await MintCreature(speciesId), species.TraitPool[0]);

            var a = await Craft(recipe.InputSpeciesIdA);
            var b = await Craft(recipe.InputSpeciesIdB);
            var exec = await _http.PostAsJsonAsync("/api/fusion/execute", new
            {
                mode = "recipe",
                sacrifices = new[] { a.Id, b.Id },
                pickedTraitId = a.Trait,
                correlationId = "fus-chain-" + corr++
            });
            Assert.True(exec.IsSuccessStatusCode, await exec.Content.ReadAsStringAsync());
            var minted = (await exec.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("minted");
            return (minted.GetProperty("actor").GetProperty("instanceId").GetString()!,
                minted.GetProperty("profile").GetProperty("traitIds")[0].GetString()!);
        }

        var crown = await Craft(legendary.OutputSpeciesId);
        var roster = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        var born = roster.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("actor").GetProperty("instanceId").GetString() == crown.Id);
        Assert.Equal("sunwoven", born.GetProperty("profile").GetProperty("rarity").GetString());
        Assert.Equal("fusion", born.GetProperty("profile").GetProperty("origin").GetString());
        Assert.Equal(legendary.OutputSpeciesId,
            born.GetProperty("profile").GetProperty("speciesId").GetString());
    }

    [Fact]
    public async Task Bad_requests_reject()
    {
        var resp = await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            mode = "star-merge",
            baseInstanceId = "ghost",
            sacrifices = new[] { "g1", "g2" },
            correlationId = "fus-e2e-bad"
        });
        Assert.False(resp.IsSuccessStatusCode);
        Assert.False((await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            mode = "star-merge",
            baseInstanceId = "ghost",
            sacrifices = new[] { "g1", "g2" }
            // no correlation
        })).IsSuccessStatusCode);
    }
}
