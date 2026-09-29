using FusionRpg.Contracts;
using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>D4.8 (spec-wild-room.md §2, §6, §7) — the three wild-room HTTP handlers, called directly
/// (this project's own pre-existing `InternalsVisibleTo`) rather than a live HTTP host, matching
/// `DelveDomainsAndStartEndpointsTests.cs`'s own established reason: the routing lambdas in
/// `DelveWildEndpoints.cs` are one line each, DI binding only, so this tests the actual logic, not
/// ASP.NET's own routing. Every assertion on a refusal's shape uses `IStatusCodeHttpResult` (never a
/// concrete `NotFound&lt;T&gt;`/`Conflict&lt;T&gt;` — every refusal here carries an anonymous-typed
/// body, an unnameable generic argument, matching this project's own established workaround in
/// `DelveDomainsAndStartEndpointsTests.cs`); content assertions read the real store's own side effects
/// instead of the anonymous response body.</summary>
public class DelveWildEndpointsTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    int _worldSeq;

    public DelveWildEndpointsTests()
    {
        ConfigureWildTuningOnce();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static bool _tuningConfigured;

    static void ConfigureWildTuningOnce()
    {
        if (_tuningConfigured) return;
        var repoRoot = FindRepoRoot();
        var tuningDir = Path.Combine(repoRoot, "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        var dungeonRegistries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        DungeonTuningHub.Configure(DungeonTuningLoader.Parse(Read("dungeon.v3.json"), dungeonRegistries));
        // Server.Tests' own PowerAndAptitudeTuningTestBootstrap module initializer configures
        // Power/Aptitude/DerivedStat/Rung/Aura/Items/CreatureSpeciesCatalog only -- ContractPolicy (the
        // auto-bind TalkJoin's own MintCreatureUnlocked performs), SoulEarnPolicy (discovery souls) and
        // SummoningTuningHub (the roller + banner catalog PullAtAltar reaches) need their own
        // configure, matching every other class in this assembly that reaches a Policy the shared
        // bootstrap does not cover (WorldBindWardenEndpointTests.cs's own identical comment, for
        // ContractPolicy specifically). StarPolicy is a REAL, found-the-hard-way transitive
        // dependency: the server-owned join resolver calls SummonRoller.RollTraits ->
        // FusionRoller.SlotsFor -> StarPolicy.Tuning for /talk and /cage as well as /pray.
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(Read("fusion.v2.json")));
        _tuningConfigured = true;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not find repo root above " + AppContext.BaseDirectory);
    }

    // ---- fixtures ----

    const ulong Seed = 11UL;

    static WorldState BuildGraph(string worldId, int parties = 1)
    {
        var entities = Enumerable.Range(0, parties)
            .Select(i => new WorldEntity
            {
                EntityId = $"party-{i}", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave",
                AtSectorId = i == 0 ? "r0c0" : "r1c0",
            })
            .ToArray();
        return new WorldState
        {
            WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = Seed, CurrentTurn = 0,
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = parties > 1
                ? new[]
                {
                    new WorldSector { SectorId = "r0c0", TypeId = "wild", Climate = null, OwnerFactionId = "dave" },
                    new WorldSector { SectorId = "r1c0", TypeId = "wild" },
                }
                : new[] { new WorldSector { SectorId = "r0c0", TypeId = "wild", Climate = null, OwnerFactionId = "dave" } },
            Lanes = parties > 1
                ? new[] { new WorldLane { LaneId = "l0", FromSectorId = "r0c0", ToSectorId = "r1c0", TypeId = "passage" } }
                : Array.Empty<WorldLane>(),
            Entities = entities,
        };
    }

    static IReadOnlyList<DelveRoomRow> BuildRooms(string kind = "wild", string? resolvedKind = null, bool includeSecond = false)
    {
        var archetype = kind == "wild" ? "room.wild-none-001" : "room.fight-none-001";
        var rooms = new List<DelveRoomRow>
        {
            new("r0c0", 0, 0, kind, archetype, true, false, null, null, resolvedKind, null, "[]", 0),
        };
        if (includeSecond)
            rooms.Add(new DelveRoomRow("r1c0", 1, 0, kind, archetype, false, false, null, null, null, null, "[]", 0));
        return rooms;
    }

    long CreateDelve(string roomKind = "wild", string? resolvedKind = null, string raidMode = "solo", int parties = 1)
    {
        var worldId = $"delve-wild-ep-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, _, delve) = _store.CreateDelve(
            _playerId, "domain.fire-shallow-001", raidMode, "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", Seed, BuildGraph(worldId, parties), BuildRooms(roomKind, resolvedKind, parties > 1), _rooms, _doors);
        Assert.True(ok);
        if (resolvedKind is not null) _store.MarkRoom(delve!.DelveId, "r0c0", resolvedKind: resolvedKind);
        return delve!.DelveId;
    }

    static long JoinPrice(DelveRow delve)
    {
        var dungeon = DungeonTuningHub.Tuning;
        var banner = SummonBannerCatalog.TryGet(dungeon.AltarBannerId)
            ?? SummonBannerCatalog.TryGet(SummonBannerCatalog.StandardRift)!;
        return DelvePrices.OfferFloor(
            banner.CostPerPull,
            delve.ThetaRun,
            dungeon.WildOfferSoulsMilliOfPullPrice,
            PowerTuningHub.Tuning);
    }

    static string ServerSpecies(DelveRow delve, DelveRoomRow room)
    {
        var resolved = DelveWildJoinResolver.Resolve(
            delve.Seed, room.RowIndex, room.ColIndex, room.ArchetypeId, delve.ThetaRun,
            DungeonTuningHub.Tuning, PowerTuningHub.Tuning);
        Assert.True(resolved.Ok, resolved.Reason);
        return resolved.Resolution!.SpeciesId;
    }

    // spec-species-rank.md §6 (T12): the shared fixture must be a species the cage gate actually
    // ADMITS — non-CaptureOnly AND not the top band. The old predicate picked only on acquisition,
    // which resolves to `dolldiamond` (Sunwoven, the top rarity rung), a species the structural rule
    // has always excluded and that the inert pre-wire endpoint never noticed. Mirrors
    // `ExpeditionResolver.WildBand`'s own two structural conditions exactly.
    static readonly CreatureSpeciesDef WildSpecies = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly
                    && s.BaseRarity != CreatureRarity.Sunwoven
                    && s.TraitPool.Count > 0);

    static CreatureMintSpec JoinSpec() => new()
    {
        SpeciesId = WildSpecies.SpeciesId, Side = WildSpecies.Side, GameTypeId = WildSpecies.GameTypeId,
        Rarity = WildSpecies.BaseRarity.ToId(), Variant = "normal",
        ElementPrimary = WildSpecies.ElementPrimary.ToElementId(), ElementSecondary = WildSpecies.ElementSecondary?.ToElementId(),
        TraitIds = new List<string> { WildSpecies.TraitPool[0] }, Origin = "delve",
    };

    /// <summary>spec-species-rank.md §6 (Task 12 caller wire): a scoped join candidate with an
    /// explicit rank. The compiled-default roster Server.Tests boots from carries NO ranks (its
    /// defs predate the field), so the candidate is scoped in — never read off the shared roster.
    /// Chaff rank, Summonable, non-Sunwoven: clears both structural rules, so a refusal can only
    /// come from the rank floor, never from <c>!captureOnly</c> / <c>!isTopRung</c>.</summary>
    static CreatureSpeciesDef RankedDef(string speciesId, CreatureRank rank, int gameTypeId) => new()
    {
        SpeciesId = speciesId, Name = speciesId, Side = "plant", GameTypeId = gameTypeId,
        CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFor("plant", gameTypeId),
        ElementPrimary = ElementTypeId.Fire, BaseRarity = CreatureRarity.Chaff,
        DeployMode = CreatureDeployMode.PlantAvatar, Acquisition = CreatureAcquisition.Summonable,
        Rank = rank, Variants = new[] { "normal" }, TraitPool = new[] { "swift" },
    };

    static CreatureMintSpec ScopedJoinSpec(CreatureSpeciesDef species) => new()
    {
        SpeciesId = species.SpeciesId, Side = species.Side, GameTypeId = species.GameTypeId,
        Rarity = species.BaseRarity.ToId(), Variant = "normal",
        ElementPrimary = species.ElementPrimary.ToElementId(), ElementSecondary = null,
        TraitIds = new List<string> { "swift" }, Origin = "delve",
    };

    /// <summary>Configure every gate at <paramref name="floor"/> — the shape a balance pass would
    /// publish once a gate is deliberately raised (mirrors CageTests.WithFloorsAt).</summary>
    static void WithFloorsAt(CreatureRank floor, Action body)
    {
        CreatureRankFloors.Configure(new CreatureRankTuning(
            1, CreatureRarityLadder.All.Select(r => r.ToId()).ToList(), Array.Empty<CreatureRankCell>(),
            CreatureRankFloors.DeclaredGates.ToDictionary(g => g, _ => floor.ToId(), StringComparer.Ordinal)));
        try { body(); }
        finally { CreatureRankFloors.ResetToUnconfigured(); }
    }

    /// <summary>The same configuration as a disposable scope, so several `using` blocks can nest
    /// (catalog scope + floor scope) without a closure body.</summary>
    static IDisposable FloorsAt(CreatureRank floor)
    {
        CreatureRankFloors.Configure(new CreatureRankTuning(
            1, CreatureRarityLadder.All.Select(r => r.ToId()).ToList(), Array.Empty<CreatureRankCell>(),
            CreatureRankFloors.DeclaredGates.ToDictionary(g => g, _ => floor.ToId(), StringComparer.Ordinal)));
        return new RestoreFloors();
    }

    sealed class RestoreFloors : IDisposable
    {
        public void Dispose() => CreatureRankFloors.ResetToUnconfigured();
    }

    static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? -1;

    /// <summary>The `reason` on a `Results.BadRequest(new { reason })` body — read off the anonymous
    /// value so a test can prove WHICH refusal fired, not merely that some 400 did.</summary>
    static string ReasonOf(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        var prop = value?.GetType().GetProperty("reason");
        return prop?.GetValue(value) as string ?? "";
    }

    // ==========================================================================================
    // /talk (and /cage, which shares the identical handler -- see the endpoints file's own doc comment)
    // ==========================================================================================

    [Fact]
    public void HandleJoin_for_an_unknown_player_404s()
    {
        var result = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest { PlayerId = 999_999, DelveId = 1, Price = 100, SinkKey = "wild:0:0", Spec = JoinSpec() },
            _store);
        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public void HandleJoin_refuses_an_unknown_room_404s()
    {
        var delveId = CreateDelve();
        var result = DelveWildEndpoints.HandleJoin("r9c9",
            new DelveWildEndpoints.DelveWildJoinRequest { PlayerId = _playerId, DelveId = delveId, Price = 100, SinkKey = "wild:0:0", Spec = JoinSpec() },
            _store);
        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public void HandleJoin_refuses_a_non_steered_party()
    {
        var delveId = CreateDelve();
        // the literal "refuses a non-steered party" check this task's own verify line names.
        var result = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest { PlayerId = _playerId, DelveId = delveId, Price = 100, SinkKey = "wild:0:0", Spec = JoinSpec() },
            _store);
        Assert.Equal(409, StatusOf(result));
    }

    [Fact]
    public void HandleJoin_uses_the_server_floor_and_mint_even_when_the_caller_substitutes_price_and_species()
    {
        var delveId = CreateDelve();
        var delve = _store.LoadDelve(delveId)!;
        var room = _store.LoadDelveRooms(delveId).Single();
        var price = JoinPrice(delve);
        var serverSpecies = ServerSpecies(delve, room);
        var malicious = RankedDef("caller-substitution", CreatureRank.Chaff, 73);
        _store.AccrueUnbanked(delveId, checked(price + 500), "seed");

        var result = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest
            {
                PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0,
                Price = 1, SinkKey = "attacker:chosen", Spec = ScopedJoinSpec(malicious),
            }, _store);

        Assert.True(price > 1);
        Assert.Equal(200, StatusOf(result));
        Assert.Equal(500, _store.LoadDelve(delveId)!.SoulsUnbanked);
        var item = Assert.Single(_store.ListCreatureRoster(_playerId).Items);
        Assert.Equal(serverSpecies, item.Profile.SpeciesId);
        Assert.NotEqual(malicious.SpeciesId, item.Profile.SpeciesId);
    }

    [Fact]
    public void HandleJoin_maps_souls_insufficient_to_409_without_trusting_price_one()
    {
        var delveId = CreateDelve();
        var price = JoinPrice(_store.LoadDelve(delveId)!);
        Assert.True(price > 1);
        var balanceBefore = _store.GetSoulBalance(_playerId).Balance;
        _store.AccrueUnbanked(delveId, price - 1, "seed");

        var result = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest
            {
                PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0,
                Price = 1, SinkKey = "wild:0:0", Spec = JoinSpec(),
            }, _store);

        Assert.Equal(409, StatusOf(result));
        Assert.Equal(price - 1, _store.LoadDelve(delveId)!.SoulsUnbanked);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(_playerId).Balance);
        Assert.Empty(_store.ListCreatureRoster(_playerId).Items); // refused, minted nothing
    }

    [Fact]
    public void HandleJoin_uses_the_persisted_nonzero_selector_and_refuses_the_old_zero_selector()
    {
        var delveId = CreateDelve(raidMode: "pair", parties: 2);
        var price = JoinPrice(_store.LoadDelve(delveId)!);
        _store.AccrueUnbanked(delveId, checked(price + 10), "seed");
        Assert.True(_store.TrySetDelveSteering(delveId, 0, 1).Ok);

        var oldSelector = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest { PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0 },
            _store);
        Assert.Equal(409, StatusOf(oldSelector));
        Assert.Equal("wild.party-not-steered", ReasonOf(oldSelector));

        var persistedSelector = DelveWildEndpoints.HandleJoin("r1c0",
            new DelveWildEndpoints.DelveWildJoinRequest { PlayerId = _playerId, DelveId = delveId, PartyEntityId = 1 },
            _store);
        Assert.Equal(200, StatusOf(persistedSelector));
        Assert.Equal(10, _store.LoadDelve(delveId)!.SoulsUnbanked);
    }

    [Fact]
    public void HandleJoin_does_not_require_caller_owned_spec_price_or_sink_fields()
    {
        var delveId = CreateDelve();
        var price = JoinPrice(_store.LoadDelve(delveId)!);
        _store.AccrueUnbanked(delveId, checked(price + 25), "seed");

        var result = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest
            {
                PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0,
                Price = 0, SinkKey = null, Spec = null,
            }, _store);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(25, _store.LoadDelve(delveId)!.SoulsUnbanked);
        Assert.Single(_store.ListCreatureRoster(_playerId).Items);
    }

    [Fact]
    public void HandleCage_uses_the_same_server_owned_mint_only_after_the_persisted_cage_marker()
    {
        var delveId = CreateDelve(resolvedKind: "cage");
        var price = JoinPrice(_store.LoadDelve(delveId)!);
        _store.AccrueUnbanked(delveId, checked(price + 50), "seed");

        var result = DelveWildEndpoints.HandleCage("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest
            {
                PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0,
                Price = 1, SinkKey = "caller:invented", Spec = JoinSpec(),
            }, _store);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(50, _store.LoadDelve(delveId)!.SoulsUnbanked);
        Assert.Single(_store.ListCreatureRoster(_playerId).Items);
    }

    [Fact]
    public void HandleJoin_admits_the_same_species_when_the_cage_floor_is_at_bottom()
    {
        // Pass-through proof: the same Chaff-ranked candidate joins cleanly at the shipped bottom
        // floor (unconfigured ≡ bottom rung) — the floor, not the species, decides. The minted
        // roster row already carries the catalog rank (Task 9 payload, proven at the endpoint).
        var delveId = CreateDelve();
        var price = JoinPrice(_store.LoadDelve(delveId)!);
        _store.AccrueUnbanked(delveId, checked(price + 20), "seed");
        var species = RankedDef("test-chaff-weed", CreatureRank.Chaff, 41);

        using (CreatureSpeciesCatalog.UseScoped(new[] { species }))
        {
            var result = DelveWildEndpoints.HandleJoin("r0c0",
                new DelveWildEndpoints.DelveWildJoinRequest { PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0 },
                _store);

            Assert.Equal(200, StatusOf(result));
            var item = Assert.Single(_store.ListCreatureRoster(_playerId).Items);
            Assert.Equal("chaff", item.Profile.Rank);
            Assert.Equal("Chaff", item.Profile.RankDisplayName);
        }
    }

    [Fact]
    public void HandleJoin_refuses_a_candidate_below_a_raised_cage_floor_before_any_spend()
    {
        // spec-species-rank.md §6 (T12 caller wire): with `cageEligibility` raised to Almanac, the
        // Chaff candidate clears both structural rules but not the floor, so the refusal can only
        // come from the rank gate — and it lands BEFORE `TalkJoin` spends, so the delve's unbanked
        // souls are untouched.
        var delveId = CreateDelve();
        var before = JoinPrice(_store.LoadDelve(delveId)!) + 500;
        var balanceBefore = _store.GetSoulBalance(_playerId).Balance;
        _store.AccrueUnbanked(delveId, before, "seed");
        var species = RankedDef("test-chaff-weed", CreatureRank.Chaff, 42);

        using (CreatureSpeciesCatalog.UseScoped(new[] { species }))
        using (FloorsAt(CreatureRank.Almanac))
        {
            var result = DelveWildEndpoints.HandleJoin("r0c0",
                new DelveWildEndpoints.DelveWildJoinRequest { PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0 },
                _store);

            Assert.Equal(400, StatusOf(result));
            Assert.Equal("wild.below-rank-floor", ReasonOf(result));
            Assert.Equal(before, _store.LoadDelve(delveId)!.SoulsUnbanked); // untouched: no spend
            Assert.Equal(balanceBefore, _store.GetSoulBalance(_playerId).Balance);
            Assert.Empty(_store.ListCreatureRoster(_playerId).Items);          // no mint either
        }
    }

    [Fact]
    public void HandleJoin_refuses_a_non_wild_room_without_spending_or_minting()
    {
        var delveId = CreateDelve(roomKind: "fight");
        var balanceBefore = _store.GetSoulBalance(_playerId).Balance;
        _store.AccrueUnbanked(delveId, 10_000, "seed");

        var result = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest
            {
                PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0,
                Price = 1, Spec = JoinSpec(),
            }, _store);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("wild.room-kind", ReasonOf(result));
        Assert.Equal(10_000, _store.LoadDelve(delveId)!.SoulsUnbanked);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(_playerId).Balance);
        Assert.Empty(_store.ListCreatureRoster(_playerId).Items);
    }

    [Fact]
    public void HandleJoin_refuses_a_party_not_persisted_at_the_room_without_spending_or_minting()
    {
        var delveId = CreateDelve(raidMode: "pair", parties: 2);
        Assert.True(_store.TrySetDelveSteering(delveId, 0, 1).Ok);
        var balanceBefore = _store.GetSoulBalance(_playerId).Balance;
        _store.AccrueUnbanked(delveId, 10_000, "seed");

        var result = DelveWildEndpoints.HandleJoin("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest
            {
                PlayerId = _playerId, DelveId = delveId, PartyEntityId = 1,
                Price = 1, Spec = JoinSpec(),
            }, _store);

        Assert.Equal(409, StatusOf(result));
        Assert.Equal("delve.steering.party-location", ReasonOf(result));
        Assert.Equal(10_000, _store.LoadDelve(delveId)!.SoulsUnbanked);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(_playerId).Balance);
        Assert.Empty(_store.ListCreatureRoster(_playerId).Items);
    }

    [Fact]
    public void HandleCage_refuses_a_wild_room_without_the_persisted_cage_marker()
    {
        var delveId = CreateDelve();
        var balanceBefore = _store.GetSoulBalance(_playerId).Balance;
        _store.AccrueUnbanked(delveId, 10_000, "seed");

        var result = DelveWildEndpoints.HandleCage("r0c0",
            new DelveWildEndpoints.DelveWildJoinRequest
            {
                PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0,
                Price = 1, Spec = JoinSpec(),
            }, _store);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("wild.not-cage", ReasonOf(result));
        Assert.Equal(10_000, _store.LoadDelve(delveId)!.SoulsUnbanked);
        Assert.Equal(balanceBefore, _store.GetSoulBalance(_playerId).Balance);
        Assert.Empty(_store.ListCreatureRoster(_playerId).Items);
    }

    // ==========================================================================================
    // /pray -- the full end-to-end §6 altar-pull transaction
    // ==========================================================================================

    [Fact]
    public void HandlePray_resolves_row_col_from_the_real_room_and_returns_200()
    {
        var delveId = CreateDelve();
        _store.AccrueUnbanked(delveId, 1_000, "seed");

        var result = DelveWildEndpoints.HandlePray("r0c0",
            new DelveWildEndpoints.DelveWildPrayRequest { PlayerId = _playerId, DelveId = delveId, ThetaRoom = 70, AltarBannerId = "standard-rift" },
            _store);

        Assert.Equal(200, StatusOf(result));
        var party = _store.LoadDelve(delveId)!.Parties.Single(p => p.EntityId == 0);
        var entry = Assert.Single(party.Haul);
        Assert.Equal(0, entry.Row); // r0c0's own real RowIndex/ColIndex, resolved server-side, never client-supplied
        Assert.Equal(0, entry.Col);
        Assert.Empty(_store.ListCreatureRoster(_playerId).Items); // "no UniqueActor... until Extracted"
    }

    [Fact]
    public void HandlePray_refuses_a_non_steered_party_before_any_soul_moves()
    {
        var delveId = CreateDelve(raidMode: "pair", parties: 2);
        Assert.True(_store.TrySetDelveSteering(delveId, 0, 1).Ok);
        _store.AccrueUnbanked(delveId, 1_000, "seed");

        var result = DelveWildEndpoints.HandlePray("r0c0",
            new DelveWildEndpoints.DelveWildPrayRequest { PlayerId = _playerId, DelveId = delveId, PartyEntityId = 0, ThetaRoom = 70, AltarBannerId = "standard-rift" },
            _store);

        Assert.Equal(409, StatusOf(result));
        Assert.Equal(1_000, _store.LoadDelve(delveId)!.SoulsUnbanked); // untouched
    }

    [Fact]
    public void HandlePray_maps_an_unknown_banner_to_bad_request()
    {
        var delveId = CreateDelve();
        _store.AccrueUnbanked(delveId, 1_000, "seed");

        var result = DelveWildEndpoints.HandlePray("r0c0",
            new DelveWildEndpoints.DelveWildPrayRequest { PlayerId = _playerId, DelveId = delveId, ThetaRoom = 70, AltarBannerId = "banner.unknown" },
            _store);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public void HandlePray_refuses_an_unknown_focusElement_as_bad_request()
    {
        var delveId = CreateDelve();
        _store.AccrueUnbanked(delveId, 1_000, "seed");

        var result = DelveWildEndpoints.HandlePray("r0c0",
            new DelveWildEndpoints.DelveWildPrayRequest
            {
                PlayerId = _playerId, DelveId = delveId, ThetaRoom = 70,
                AltarBannerId = "standard-rift", FocusElementId = "not-a-real-element",
            },
            _store);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public void HandlePray_for_an_unknown_room_404s()
    {
        var delveId = CreateDelve();
        _store.AccrueUnbanked(delveId, 1_000, "seed");

        var result = DelveWildEndpoints.HandlePray("r9c9",
            new DelveWildEndpoints.DelveWildPrayRequest { PlayerId = _playerId, DelveId = delveId, ThetaRoom = 70, AltarBannerId = "standard-rift" },
            _store);

        Assert.Equal(404, StatusOf(result));
    }

}
