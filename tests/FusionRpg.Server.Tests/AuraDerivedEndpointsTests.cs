using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>aura-skill T18b — GET /api/actors/{instanceId}/derived against a REAL in-process host
/// (same pattern as AptitudeEndpointsTests.cs), proving the endpoint spec-aura-surface.md §3 names as
/// missing actually resolves a live actor's derived channels WITH their per-source contributions —
/// not a stub, not a bridge to `pvz_stat_contributions`.</summary>
public class AuraDerivedEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        PowerTuningHub.Configure(
            PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        AptitudeTuningHub.Configure(
            AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "contracts.v1.json"))));
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "status.v1.json"))));
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "stats.v1.json"))));
        // species-build `battle-allocation` (module 10, path 4): this endpoint now resolves a species
        // allocation too (SpeciesAllocationSource), so its own fixture needs the same roster/tuning a
        // real server configures at startup -- brought in line here rather than relying on another
        // test class in the same process happening to have configured CreatureSpeciesCatalog first.
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        FusionRpg.Core.Progression.SpeciesProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.SpeciesProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "species-progression.v1.json"))));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IActorLiveStateStore, ActorLiveStateStore>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapAuraDerived();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Get_unknownInstance_returns404()
    {
        var resp = await _http.GetAsync("/api/actors/not-a-real-instance/derived");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_freshPlantActor_returnsRealProgressionChannelsFromTheRealSubsystem()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 42);

        var resp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/derived");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<DerivedResponseDto>();
        Assert.NotNull(body);
        Assert.Equal(actor.InstanceId, body!.InstanceId);

        // rpg.progression always contributes these two -- proves ActorHub actually resolved, not a stub.
        var power = Assert.Single(body.Channels, c => c.ChannelId == "progression.power");
        var powerSource = Assert.Single(power.Contributions);
        Assert.Equal("rpg.progression", powerSource.SourceId);

        var realm = Assert.Single(body.Channels, c => c.ChannelId == "progression.realm");
        Assert.Equal("rpg.progression", Assert.Single(realm.Contributions).SourceId);

        // Channels come back sorted -- a stable, deterministic contract for the web layer to render.
        var ids = body.Channels.Select(c => c.ChannelId).ToList();
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal).ToList(), ids);
    }

    [Fact]
    public async Task Get_zombieActor_resolvesTooNotJustPlant()
    {
        var actor = _store.CreateUniqueActor(_playerId, "zombie", typeId: 7);

        var resp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/derived");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DerivedResponseDto>();
        Assert.NotNull(body);
        Assert.Contains(body!.Channels, c => c.ChannelId == "progression.power");
    }

    [Fact]
    public async Task Get_afterARealAptitudeAllocation_reflectsANonVacuousAptitudeSourcedContribution()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 1);

        var before = await (await _http.GetAsync($"/api/actors/{actor.InstanceId}/derived"))
            .Content.ReadFromJsonAsync<DerivedResponseDto>();
        // AptitudeResolver.Resolve: "an aptitude with zero share contributes nothing -- not a
        // zero-valued modifier" (AptitudeResolver.cs:20-22), so an empty allocation names no source.
        Assert.DoesNotContain(before!.Channels, c => c.Contributions.Any(x => x.SourceId.StartsWith("aptitude.", StringComparison.Ordinal)));

        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(_playerId),
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50));

        var after = await (await _http.GetAsync($"/api/actors/{actor.InstanceId}/derived"))
            .Content.ReadFromJsonAsync<DerivedResponseDto>();
        Assert.NotNull(after);
        // GG-49, non-vacuously: a real allocation now names a real source (AptitudeResolver.cs:61 --
        // SourceId is "aptitude.{edge.Source}", e.g. "aptitude.Might") on a real channel.
        Assert.Contains(after!.Channels, c => c.Contributions.Any(x => x.SourceId == "aptitude.Might"));
    }

    [Fact]
    public async Task Get_sheet_returns_full_registry_channels_with_composeKind_and_fiction_labels()
    {
        ActorSurfaceCatalogHub.ConfigureAll(
            AptitudeSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aptitude-catalog.v1.json"))),
            DerivedStatSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "derived-stat-catalog.v3.json"))),
            StatusSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "status-catalog.v1.json"))),
            ResourceSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "resource-catalog.v1.json"))),
            ElementSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "element-catalog.v1.json"))),
            ActorSheetSurfaceCatalogLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "actor-sheet.v1.json"))));

        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 3);
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(_playerId),
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 40));

        var resp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/sheet");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var sheet = await resp.Content.ReadFromJsonAsync<SheetResponseDto>();
        Assert.NotNull(sheet);
        Assert.Equal(actor.InstanceId, sheet!.InstanceId);
        Assert.True(sheet.Derived.Count >= 200, $"expected full registry floor, got {sheet.Derived.Count}");
        Assert.Equal(actor.Level, sheet.Level);
        Assert.Equal(actor.Xp, sheet.Xp);
        Assert.Equal(actor.Phase, sheet.Phase);
        Assert.Equal("Plant", sheet.RoleLabel);
        Assert.Equal(RpgXpCurve.XpToNext(RpgActorKinds.Specimen, actor.Level), sheet.XpToNext);
        Assert.NotNull(sheet.LiveStatuses);
        Assert.Empty(sheet.LiveStatuses!);
        Assert.NotNull(sheet.ResourcePools);
        Assert.Equal(FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds.Count, sheet.ResourcePools!.Count);
        Assert.All(FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds, id =>
        {
            var pool = Assert.Single(sheet.ResourcePools, p => p.ResourceId == id);
            Assert.True(pool.Max is > 0, $"pool {id} Max must be > 0");
            Assert.True(pool.Current is >= 0, $"pool {id} Current must be set");
            Assert.True(pool.Current <= pool.Max, $"pool {id} Current must be <= Max");
        });
        // Hub SSOT: resource.max.* channels appear on the derived sheet from ResourceBaselineSubsystem.
        Assert.All(FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds, id =>
        {
            var ch = Assert.Single(sheet.Derived, c => c.ChannelId == $"resource.max.{id}");
            Assert.True(ch.Value > 0, $"derived resource.max.{id} must be Hub-seeded");
            Assert.Contains(ch.Contributions, x => x.SourceId == "rpg.resource.base");
        });
        Assert.NotNull(sheet.Standing);
        Assert.Null(sheet.ShieldSummary);
        Assert.NotNull(sheet.ShieldLayers);
        Assert.Empty(sheet.ShieldLayers!);

        var power = Assert.Single(sheet.Derived, c => c.ChannelId == "progression.power");
        Assert.Equal("FlatReplace", power.ComposeKind);
        Assert.Equal("Power index", power.DisplayName);
        Assert.Equal("LadderIndex", power.UnitClass);
        Assert.Equal(1.0, power.DefaultValue);
        Assert.Null(power.Cap);
        Assert.Equal("stub", power.RenderState);
        var prog = Assert.Single(power.Contributions);
        Assert.Equal("rpg.progression", prog.SourceId);
        Assert.Equal("Progression", prog.Label);

        var resistDot = Assert.Single(sheet.Derived, c => c.ChannelId == "status.resist.dot");
        Assert.Equal("StatusPotencyPoints", resistDot.UnitClass);
        Assert.Equal(0.0, resistDot.DefaultValue);
        Assert.Equal(DerivedStatPolicy.CategoryResistCap, resistDot.Cap);
        Assert.False(string.IsNullOrEmpty(resistDot.RenderState));

        var resistOmni = Assert.Single(sheet.Derived, c => c.ChannelId == "status.resist.omni");
        Assert.Null(resistOmni.Cap);

        var arm1 = Assert.Single(sheet.Derived, c => c.ChannelId == "progression.bonus.arm1");
        Assert.Equal("no-producer", arm1.RenderState);

        var firePower = Assert.Single(sheet.Derived, c => c.ChannelId == "combat.power.omni");
        Assert.Equal("Power", firePower.DisplayName);

        Assert.Contains(sheet.Derived, c =>
            c.Contributions.Any(x => x.SourceId == "aptitude.Might" && (x.Label?.StartsWith("Aptitude", StringComparison.Ordinal) ?? false)));

        Assert.NotNull(sheet.Primary);
        // Primary bag is always projected; when contributions exist they use primary: grammar.
        Assert.All(sheet.Primary, p => Assert.StartsWith("primary:", p.SourceId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_sheet_joins_species_name_nickname_and_element_typing_for_a_real_specimen()
    {
        var species = FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All
            .First(s => s.ElementSecondary != null && s.Side == "plant");
        var minted = _store.MintCreature(_playerId, new FusionRpg.Contracts.CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = FusionRpg.Core.Creatures.CreatureRarityIds.ToId(species.BaseRarity),
            Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string>(),
            Origin = "test",
            Nickname = "Emberling"
        }).Specimen;

        var resp = await _http.GetAsync($"/api/actors/{minted.Actor.InstanceId}/sheet");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var sheet = await resp.Content.ReadFromJsonAsync<SheetResponseDto>();
        Assert.NotNull(sheet);
        Assert.Equal("Emberling", sheet!.DisplayName);
        Assert.Equal(species.SpeciesId, sheet.SpeciesId);
        Assert.Equal(species.Name, sheet.SpeciesName);
        Assert.NotNull(sheet.ElementTyping);
        Assert.Equal(species.ElementPrimary.ToElementId(), sheet.ElementTyping!.Primary);
        Assert.Equal(species.ElementSecondary?.ToElementId(), sheet.ElementTyping.Secondary);
    }

    [Fact]
    public async Task Get_sheet_bare_actor_has_null_identity_and_honest_empty_current_state()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 3);

        var resp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/sheet");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var sheet = await resp.Content.ReadFromJsonAsync<SheetResponseDto>();
        Assert.NotNull(sheet);
        Assert.Null(sheet!.SpeciesId);
        Assert.Null(sheet.SpeciesName);
        Assert.Null(sheet.ElementTyping);
        Assert.Equal(RpgXpCurve.XpToNext(RpgActorKinds.Specimen, actor.Level), sheet.XpToNext);
        Assert.NotNull(sheet.LiveStatuses);
        Assert.Empty(sheet.LiveStatuses!);
        Assert.NotNull(sheet.ResourcePools);
        Assert.Equal(FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds.Count, sheet.ResourcePools!.Count);
        Assert.NotNull(sheet.Standing);
        Assert.Null(sheet.ShieldSummary);
        Assert.NotNull(sheet.ShieldLayers);
        Assert.Empty(sheet.ShieldLayers!);
    }

    [Fact]
    public async Task Get_sheet_projects_standing_from_equipped_stat_derived_atoms()
    {
        const string channel = DerivedStatChannels.CombatPowerOmni;
        const long amount = 175;
        const string itemRef = "item-hub-standing-fixture";
        var role = ItemRoles.Id(ItemRole.ArmamentPrimary);

        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 11);
        BindStatDerivedEquip(actor.InstanceId, channel, amount, role, itemRef);

        var resp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/sheet");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var sheet = await resp.Content.ReadFromJsonAsync<SheetResponseDto>();
        Assert.NotNull(sheet);
        Assert.NotNull(sheet!.Standing);
        // Equipped combat.power.omni prices into Offense (stat.derived categories) — non-zero Standing.
        Assert.True(
            sheet.Standing!.Offense
            + sheet.Standing.Survivability
            + sheet.Standing.Control
            + sheet.Standing.Utility
            + sheet.Standing.Economy
            > 0,
            "equipped atom must produce a non-zero Standing vector");
    }

    [Fact]
    public async Task Get_sheet_unknownInstance_returns404()
    {
        var resp = await _http.GetAsync("/api/actors/not-a-real-instance/sheet");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_derived_ships_composeKind_and_FlatReplace_honesty_on_progression_power()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 5);
        var resp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/derived");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DerivedResponseDto>();
        Assert.NotNull(body);

        var power = Assert.Single(body!.Channels, c => c.ChannelId == "progression.power");
        Assert.Equal("FlatReplace", power.ComposeKind);
        Assert.NotEmpty(power.Contributions);
        // FlatReplace: contribution sum may disagree with composed value — honesty is composeKind, not arithmetic.
        var sum = power.Contributions.Sum(c => c.Value);
        Assert.True(power.Value != 0 || sum != 0 || power.Contributions.Count > 0);
        Assert.Contains(power.Contributions, c => c.SourceId == "rpg.progression" && c.Label == "Progression");
    }

    [Fact]
    public async Task Get_sheet_and_derived_attribute_equipped_stat_derived_as_equip_role_item()
    {
        const string channel = DerivedStatChannels.CombatPowerOmni;
        const long amount = 175;
        const string itemRef = "item-hub-equip-fixture";
        var role = ItemRoles.Id(ItemRole.ArmamentPrimary);

        var actor = _store.CreateUniqueActor(_playerId, "plant", typeId: 9);
        BindStatDerivedEquip(actor.InstanceId, channel, amount, role, itemRef);

        // Multi-source FlatSum: aptitude Might also writes combat.power.omni.
        _store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(_playerId),
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50));

        var expectedSource = ContributionSourceIds.Equip(role, itemRef);

        var sheetResp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/sheet");
        if (!sheetResp.IsSuccessStatusCode) throw new Exception(await sheetResp.Content.ReadAsStringAsync());
        var sheet = await sheetResp.Content.ReadFromJsonAsync<SheetResponseDto>();
        Assert.NotNull(sheet);
        var sheetCh = Assert.Single(sheet!.Derived, c => c.ChannelId == channel);
        Assert.Equal("FlatSum", sheetCh.ComposeKind);
        var equipSheet = Assert.Single(sheetCh.Contributions, c => c.SourceId == expectedSource);
        Assert.Contains("Equip", equipSheet.Label, StringComparison.Ordinal);
        Assert.Contains(sheetCh.Contributions, c => c.SourceId == "aptitude.Might");
        Assert.True(sheetCh.Contributions.Count >= 2);
        // FlatSum: contribution flats account for the composed channel value.
        Assert.Equal(sheetCh.Value, sheetCh.Contributions.Sum(c => c.Value), 3);

        var derivedResp = await _http.GetAsync($"/api/actors/{actor.InstanceId}/derived");
        derivedResp.EnsureSuccessStatusCode();
        var derived = await derivedResp.Content.ReadFromJsonAsync<DerivedResponseDto>();
        Assert.NotNull(derived);
        var derCh = Assert.Single(derived!.Channels, c => c.ChannelId == channel);
        Assert.Equal("FlatSum", derCh.ComposeKind);
        Assert.Contains(derCh.Contributions, c => c.SourceId == expectedSource && c.Label != null && c.Label.Contains("Equip", StringComparison.Ordinal));
        Assert.Contains(derCh.Contributions, c => c.SourceId == "aptitude.Might");
        Assert.Equal(derCh.Value, derCh.Contributions.Sum(c => c.Value), 3);
    }

    void BindStatDerivedEquip(string specimenId, string channel, long amount, string roleSlot, string itemRef)
    {
        var atomId = "atom.hub-equip-attrib.t1";
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.derived",
            FamilyId = "atom.hub-equip-attrib", Variant = "", Tier = 1, Name = "Hub Equip Attrib",
            ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"flat\",\"amount\":{amount}}}",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.hub-equip-attrib", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, atomId) },
        }).IsOk);

        var tuning = PowerTuning.Build(
            1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);
        var owner = new OwnerScope(OwnerKind.UniqueActor, specimenId);
        var produce = _store.ProduceAndBind(
            _store.GetContainer("item.hub-equip-attrib")!,
            _ => Array.Empty<string>(),
            rollSeed: 11, thetaContent: 20, tuning, owner,
            slot: roleSlot, priority: 0, source: "test",
            out var producedInstanceId, out _);
        Assert.True(produce.IsOk, produce.ToString());
        Assert.NotNull(producedInstanceId);

        _store.SaveAssignment(specimenId, ItemRole.ArmamentPrimary, "rolled", itemRef);
    }

    sealed class SheetResponseDto
    {
        public string InstanceId { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? SpeciesId { get; set; }
        public string? SpeciesName { get; set; }
        public string Phase { get; set; } = "";
        public string? RoleLabel { get; set; }
        public long Level { get; set; }
        public long Xp { get; set; }
        public long? XpToNext { get; set; }
        public SheetElementTypingDto? ElementTyping { get; set; }
        public SheetStandingDto? Standing { get; set; }
        public List<SheetChannelDto> Derived { get; set; } = new();
        public List<DerivedContributionDto> Primary { get; set; } = new();
        public List<SheetStatusGlyphDto>? LiveStatuses { get; set; }
        public List<SheetResourcePoolDto>? ResourcePools { get; set; }
        public SheetShieldSummaryDto? ShieldSummary { get; set; }
        public List<SheetShieldLayerDto>? ShieldLayers { get; set; }
    }

    sealed class SheetStandingDto
    {
        public int Offense { get; set; }
        public int Survivability { get; set; }
        public int Control { get; set; }
        public int Utility { get; set; }
        public int Economy { get; set; }
    }

    sealed class SheetStatusGlyphDto
    {
        public string StatusId { get; set; } = "";
        public int? RemainingPermille { get; set; }
    }

    sealed class SheetResourcePoolDto
    {
        public string ResourceId { get; set; } = "";
        public long? Current { get; set; }
        public long? Max { get; set; }
    }

    sealed class SheetShieldSummaryDto
    {
        public string? ElementId { get; set; }
        public long? Current { get; set; }
        public long? Max { get; set; }
        public int? Stacks { get; set; }
    }

    sealed class SheetShieldLayerDto
    {
        public string ShieldId { get; set; } = "";
        public string? ElementId { get; set; }
        public long Current { get; set; }
        public long Max { get; set; }
        public int Priority { get; set; }
        public string SourceId { get; set; } = "";
        public bool IsInnate { get; set; }
        public long? RegenPerSecond { get; set; }
        public bool Broken { get; set; }
    }

    sealed class SheetElementTypingDto
    {
        public string Primary { get; set; } = "";
        public string? Secondary { get; set; }
    }

    sealed class SheetChannelDto
    {
        public string ChannelId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ComposeKind { get; set; } = "";
        public double Value { get; set; }
        public string UnitClass { get; set; } = "";
        public double DefaultValue { get; set; }
        public double? Cap { get; set; }
        public string RenderState { get; set; } = "";
        public List<DerivedContributionDto> Contributions { get; set; } = new();
    }

    sealed class DerivedResponseDto
    {
        public string InstanceId { get; set; } = "";
        public List<DerivedChannelDto> Channels { get; set; } = new();
    }

    sealed class DerivedChannelDto
    {
        public string ChannelId { get; set; } = "";
        public double Value { get; set; }
        public string ComposeKind { get; set; } = "";
        public List<DerivedContributionDto> Contributions { get; set; } = new();
    }

    sealed class DerivedContributionDto
    {
        public string SourceId { get; set; } = "";
        public string? Label { get; set; }
        public string Op { get; set; } = "";
        public double Value { get; set; }
    }

    static string RepoTuningDir() => Path.Combine(FindRepoRoot(), "data", "tuning");

    static string LatestAptitudesPath()
    {
        var dir = RepoTuningDir();
        var best = Directory.EnumerateFiles(dir, "aptitudes.v*.json")
            .Select(Path.GetFileName)
            .Select(n => (Name: n!, Match: System.Text.RegularExpressions.Regex.Match(n!, @"^aptitudes\.v(\d+)\.json$")))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .First();
        return Path.Combine(dir, best.Name);
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "collect-class-system-realrun.ps1"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
