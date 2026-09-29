using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>
/// npc-story-events NR2.1 (`spec-storylet-reseam.md` §1; plan §3 rule 4): the STREAM-IDENTITY FIXTURE, recorded
/// BEFORE the storylet engine moves behind <c>IStoryletHost</c>. It pins what today's <see cref="EventDeck.Resolve"/>
/// produces — the event id, the drawn outcome ordinal, the instance and the next unknown-pity state — for 64 fixed
/// seeds across the six event-capable room kinds plus <c>unknown</c>. After the move (NR2.2) the same fixture must
/// reproduce byte for byte through <c>DelveStoryletHost.Resolve</c>, so a renamed stream, a reordered draw or a
/// changed draw count fails here instead of silently reseeding every existing delve.
///
/// <para>The deck is built in memory from an inline catalog (no committed corpus, no seed file), and the file is
/// only WRITTEN when the documented recorder variable is set — a normal run reads it.</para>
/// </summary>
public class StreamIdentityFixtureTests
{
    /// <summary>Set to <c>1</c> to rewrite the fixture from today's code (the recording step for NR2.1 and, if the
    /// move legitimately changes a stream, a reviewed re-record — never a silent one).</summary>
    const string RecorderVariable = "FUSIONRPG_RECORD_NARRATIVE_STREAM_FIXTURE";

    /// <summary>The room kinds the fixture covers: the six the deck maps to an event kind, plus `unknown`, whose
    /// pity path is the one branch that can resolve to a non-event.</summary>
    static readonly string[] RoomKinds = { "curio", "shrine", "trap", "merchant", "wild", "rest", "unknown" };

    const int SeedCount = 64;

    static readonly IReadOnlyList<string> DropBandOrder = new[] { "staple", "frequent", "occasional", "seldom", "exceptional" };
    static readonly IReadOnlyDictionary<string, int> DropBandWeights = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["staple"] = 1000, ["frequent"] = 300, ["occasional"] = 90, ["seldom"] = 25, ["exceptional"] = 7,
    };

    const int PinTheta = 20;
    static readonly DifficultyRungTuning Hard = DungeonTuningHub.Tuning.Rungs["hard"];
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    /// <summary>The unknown room's pity is tuned so the three non-event arms are impossible: the room always
    /// resolves to the event kind and then takes the ordinary event path, which is the stream this fixture pins.</summary>
    static readonly IReadOnlyDictionary<string, UnknownPityTuning> EventArmPity = new Dictionary<string, UnknownPityTuning>(StringComparer.Ordinal)
    {
        [UnknownPity.CacheKind] = new(0, 0),
        [UnknownPity.MerchantKind] = new(0, 0),
        [UnknownPity.FightKind] = new(0, 0),
    };

    static string FixturePath() => Path.Combine(
        FusionRpg.Core.Tests.Dungeon.DungeonTestFiles.RepoRoot(), "tests", "fixtures", "narrative", "reseam", "event-resolutions.json");

    // ---- the deck: two events per kind, two outcomes per event, built in memory ----------------------

    static EventOutcomeRow Outcome(string ordinal, string dropBand) => new(
        ordinal, dropBand, "none", new[] { new EventEffectRef("spirit-drain", "trivial") });

    /// <summary>One real effect per outcome: an outcome's effects list must not be empty, and the deck builds
    /// each outcome's container eagerly at Resolve time.</summary>
    static readonly IReadOnlyDictionary<string, AtomRow> ProbeAtoms = new Dictionary<string, AtomRow>(StringComparer.Ordinal)
    {
        [AtomRow.DeriveId("spirit-drain", "", 1)] = new()
        {
            AtomId = AtomRow.DeriveId("spirit-drain", "", 1), KindId = "resource.delta",
            FamilyId = "spirit-drain", Tier = 1, ParamsJson = "{\"channel\":\"spirit\",\"amount\":-40}",
        },
    };

    static AtomRow? LookupAtom(string id) => ProbeAtoms.TryGetValue(id, out var atom) ? atom : null;

    static EventRow Row(string id, string kind) => new(
        id, kind, Theme: null, ClimateAffinity: null, RepeatScope: "per-delve", Eligibility: null,
        new[] { Outcome("good", "staple"), Outcome("bad", "occasional") }, SupplyOverride: null,
        // `story` content requires a chain ref (the catalog's own content rule), so the probe carries one.
        ChainRef: kind == "story" ? "chain.probe" : null);

    static (EventDeck Deck, IReadOnlyDictionary<string, string> ArchetypeByKind) ProbeDeck()
    {
        var events = new List<EventRow>();
        var archetypeByKind = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["curio"] = "room.curio.probe",
            ["shrine"] = "room.shrine.probe",
            ["trap"] = "room.trap.probe",
            ["merchant"] = "room.merchant.probe",
            ["wild"] = "room.wild.probe",
            ["rest"] = "room.rest.probe",
        };
        // The room archetype's KIND (EventFilters' own map): merchant holds `bargain`, wild `story`, rest
        // `encounter-event`. The deck's pools are keyed by archetype id.
        var eventKindByRoomKind = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["curio"] = "curio", ["shrine"] = "shrine", ["trap"] = "trap",
            ["merchant"] = "bargain", ["wild"] = "story", ["rest"] = "encounter-event",
        };
        var pools = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var roomKind in archetypeByKind.Keys)
        {
            var eventKind = eventKindByRoomKind[roomKind];
            var ids = new[] { $"ev.{roomKind}.a", $"ev.{roomKind}.b" };
            foreach (var id in ids) events.Add(Row(id, eventKind));
            pools[archetypeByKind[roomKind]] = ids;
        }
        // `unknown` admits any kind, so its pool carries every probe event.
        pools["room.unknown.probe"] = events.Select(e => e.EventId).ToArray();

        var eventKinds = new[] { "curio", "encounter-event", "shrine", "trap", "bargain", "story" };
        var catalog = EventCatalog.Load(events, eventKinds, new[] { "per-delve", "per-domain", "once-per-player" },
            new[] { "good", "mixed", "bad", "nothing" }, DropBandOrder, new[] { "herbs" }, _ => -1);
        Assert.Empty(catalog.Rejections);
        return (EventDeck.Build(pools, catalog.Catalog, LookupAtom, _ => null, Tuning), archetypeByKind);
    }

    static DelveRoomFact Room(string roomKind, IReadOnlyDictionary<string, string> archetypeByKind) => new(
        1, 2, "r01c02", roomKind,
        roomKind == EventFilters.UnknownRoomKind ? "room.unknown.probe" : archetypeByKind[roomKind],
        BaseBand: 0, IsSecret: false, SightLanes: 0, ScoutSightLanes: 0, PartyRouteMask: 0, KeyForLaneId: null);

    static FactReader MidHpFacts() => new(
        self: new EntityFacts(0, 0, 500, -1, -1, -1, false, false, 0),
        target: new EntityFacts(0, 0, 500, -1, -1, -1, false, false, 0));

    static RoomTheta Theta() => new(new ContentContext(0, 0, 0, 0), PinTheta, 0);

    /// <summary>The recorded stream: deterministic, ordered by room kind then seed.</summary>
    static string Record()
    {
        var (deck, archetypeByKind) = ProbeDeck();
        var facts = MidHpFacts();
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"note\": \"Stream identity recorded before the storylet engine move (npc-story-events NR2.1). Reproduced byte for byte by StreamIdentityFixtureTests after the move (NR2.2).\",\n");
        text.Append("  \"roomKinds\": [").Append(string.Join(", ", RoomKinds.Select(Quote))).Append("],\n");
        text.Append("  \"seeds\": ").Append(SeedCount).Append(",\n");
        text.Append("  \"rows\": [\n");

        var rows = new List<string>();
        foreach (var roomKind in RoomKinds)
        {
            var room = Room(roomKind, archetypeByKind);
            var unknown = roomKind == EventFilters.UnknownRoomKind;
            for (ulong seed = 1; seed <= SeedCount; seed++)
            {
                var resolution = EventDeck.Resolve(
                    deck, room, facts, EventSeenSets.Empty, seed, Hard, Theta(),
                    roomClimate: null, climateAffinityMatchMilli: 1000, climateAffinityNoneMilli: 1000,
                    climateAffinityOffMilli: 500, DropBandOrder, DropBandWeights,
                    partyPity: unknown ? UnknownPityState.Empty : null,
                    unknownPityTuning: unknown ? EventArmPity : null);

                rows.Add("    { \"roomKind\": " + Quote(roomKind)
                    + ", \"seed\": " + seed
                    + ", \"eventId\": " + Quote(resolution.EventId)
                    + ", \"drawnOutcomeOrdinal\": " + Quote(resolution.DrawnOutcomeOrdinal)
                    + ", \"instance\": " + Quote(resolution.Instance?.ToString())
                    + ", \"nextPity\": " + Quote(Pity(resolution.NextPity))
                    + " }");
            }
        }
        text.Append(string.Join(",\n", rows)).Append("\n  ]\n}\n");
        return text.ToString();
    }

    static string? Pity(UnknownPityState? state) => state is { } pity
        ? $"cache:{pity.MissesCache},merchant:{pity.MissesMerchant},fight:{pity.MissesFight}"
        : null;

    static string Quote(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);

    // ---- the fixture ---------------------------------------------------------------------------------

    [Fact]
    public void Todays_code_reproduces_the_recorded_stream_identity_fixture()
    {
        var path = FixturePath();
        var recorded = Record();

        if (Environment.GetEnvironmentVariable(RecorderVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, recorded);
            return; // the recording run's own proof is the next normal run
        }

        Assert.True(File.Exists(path), $"{path} is missing — record it with {RecorderVariable}=1");
        var committed = File.ReadAllText(path).Replace("\r\n", "\n");
        if (committed == recorded) return;

        var expected = committed.Split('\n');
        var actual = recorded.Split('\n');
        var index = 0;
        while (index < expected.Length && index < actual.Length && expected[index] == actual[index]) index++;
        Assert.Fail($"the stream-identity fixture drifted at line {index + 1}:\n  fixture: {At(expected, index)}\n  today:   {At(actual, index)}");
    }

    static string At(string[] lines, int index) => index < lines.Length ? lines[index] : "<end of file>";
}
