using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>aptitude-sheet AS-3.1 — RpgStore aptitude preset library (item-loadout discipline).</summary>
[Trait("VerificationId", "data.aptitude-preset")]
public class AptitudePresetStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;

    public AptitudePresetStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(1, 1, SoftMaxPresets: 32, DefaultRowAbsMax: 1000, AssignLadder: MinimalAssignLadder()));
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>A legally loaded ladder order (EP1.3's own contract: known ids, no duplicates, ends
    /// on `even`) for tests that configure <see cref="AptitudePresetTuning"/> by hand and do not
    /// exercise the ladder itself — the shortest order satisfying the load contract.</summary>
    static AssignLadderTuning MinimalAssignLadder() => new(new[] { AptitudeAutoAssignRules.Even });

    static List<RpgAptitudePresetEntryRow> EvenRows(string presetId)
    {
        // 12 × 83 = 996, remainder 4 on first four → sum 1000
        var list = new List<RpgAptitudePresetEntryRow>();
        var i = 0;
        foreach (var apt in AptitudeCatalog.All)
        {
            var pm = 83L + (i < 4 ? 1L : 0L);
            list.Add(new RpgAptitudePresetEntryRow(presetId, apt.Id, pm, null, null, null, null));
            i++;
        }
        return list;
    }

    [Fact]
    public void Save_rejects_permille_sum_not_1000()
    {
        var id = "bad-sum";
        var rows = EvenRows(id);
        rows[0] = rows[0] with { TargetPermille = rows[0].TargetPermille + 1 };
        var reason = _store.SaveAptitudePreset(
            new RpgAptitudePresetRow(id, _playerId, "Bad", RpgStore.AptitudePresetKindPlayer,
                DateTimeOffset.UtcNow.ToString("o"), 0),
            rows, isCreate: true);
        Assert.Equal("presets.targetPermille.sum", reason);
        Assert.Empty(_store.ListAptitudePresets(_playerId));
    }

    [Fact]
    public void Save_and_list_round_trips_across_reopen()
    {
        var id = "even-1";
        var reason = _store.SaveAptitudePreset(
            new RpgAptitudePresetRow(id, _playerId, "Even", RpgStore.AptitudePresetKindPlayer,
                DateTimeOffset.UtcNow.ToString("o"), 0),
            EvenRows(id), isCreate: true);
        Assert.Equal("", reason);

        // Survive process restart = a fresh store over the same storage sees the committed presets.
        var reopened = _testStore.Reopen();
        var listed = reopened.ListAptitudePresets(_playerId);
        Assert.Single(listed);
        Assert.Equal("Even", listed[0].Name);
        var entries = reopened.GetAptitudePresetEntries(id);
        // Reads AptitudeCatalog.Count live rather than a second literal (population-pin SE3.4,
        // 2026-09-20); pinned once, AptitudeCatalogTests.
        Assert.Equal(AptitudeCatalog.Count, entries.Count);
        Assert.Equal(1000, entries.Sum(e => e.TargetPermille));
    }

    [Fact]
    public void SoftMax_refuses_create_past_cap()
    {
        AptitudePresetTuningHub.Configure(new AptitudePresetTuning(1, 1, SoftMaxPresets: 2, DefaultRowAbsMax: 1000, AssignLadder: MinimalAssignLadder()));
        for (var i = 0; i < 2; i++)
        {
            var id = "cap-" + i;
            Assert.Equal("", _store.SaveAptitudePreset(
                new RpgAptitudePresetRow(id, _playerId, "P" + i, RpgStore.AptitudePresetKindPlayer,
                    DateTimeOffset.UtcNow.ToString("o"), 0),
                EvenRows(id), isCreate: true));
        }
        var overflow = _store.SaveAptitudePreset(
            new RpgAptitudePresetRow("cap-2", _playerId, "TooMany", RpgStore.AptitudePresetKindPlayer,
                DateTimeOffset.UtcNow.ToString("o"), 0),
            EvenRows("cap-2"), isCreate: true);
        Assert.Equal("presets.softMax", overflow);
        Assert.Equal(2, _store.CountAptitudePresets(_playerId));
    }

    [Fact]
    public void Delete_clears_active_binding()
    {
        var id = "del-1";
        Assert.Equal("", _store.SaveAptitudePreset(
            new RpgAptitudePresetRow(id, _playerId, "Del", RpgStore.AptitudePresetKindPlayer,
                DateTimeOffset.UtcNow.ToString("o"), 0),
            EvenRows(id), isCreate: true));
        Assert.Equal("", _store.SetAptitudePresetActive(_playerId, "commander", "", id));
        Assert.NotNull(_store.GetAptitudePresetActive(_playerId, "commander", ""));
        Assert.True(_store.DeleteAptitudePreset(_playerId, id));
        Assert.Null(_store.GetAptitudePresetActive(_playerId, "commander", ""));
    }

    [Fact]
    public void Materialize_leftover_legal_and_loGtHi_refuses()
    {
        var id = "mat-1";
        var rows = EvenRows(id);
        // Force leftover: clamp Might maxAbs below its share of budget 1000 → floor(83*1000/1000)=83, maxAbs=10
        rows[0] = rows[0] with { MaxAbs = 10 };
        Assert.Equal("", _store.SaveAptitudePreset(
            new RpgAptitudePresetRow(id, _playerId, "Mat", RpgStore.AptitudePresetKindPlayer,
                DateTimeOffset.UtcNow.ToString("o"), 0),
            rows, isCreate: true));

        var ok = AptitudePresetMaterialize.Materialize(RpgStore.ToRowSpecs(_store.GetAptitudePresetEntries(id)), 1000);
        Assert.True(ok.Ok);
        Assert.True(ok.Leftover > 0);
        Assert.Equal(10, ok.Shares["Might"]);

        var conflict = EvenRows("x").Select(r =>
            r.AptitudeId == "Might"
                ? new AptitudePresetRowSpec(r.AptitudeId, r.TargetPermille, MinAbs: 50, MaxAbs: 10)
                : new AptitudePresetRowSpec(r.AptitudeId, r.TargetPermille)).ToList();
        var bad = AptitudePresetMaterialize.Materialize(conflict, 1000);
        Assert.False(bad.Ok);
        Assert.Equal("presets.materialize.loGtHi", bad.Reason);
    }
}
