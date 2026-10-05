using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// The empire commander-pool <b>write</b> path — the write twin of the empire-keyed read
/// (<see cref="RpgStore.CommanderPoolOf"/>, which already resolves an explicit allocation under an
/// empire's own commander scope key and lets it win wholesale).
///
/// <para>Every assertion here reads the pool back through <see cref="RpgStore.CommanderPoolOf"/> —
/// the ordinary production read — never through the writer, so no test can pass on the strength of
/// the write having merely returned true.</para>
/// </summary>
public sealed class EmpireCommanderPoolWriteTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _saveId;

    public EmpireCommanderPoolWriteTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _saveId = _store.GetCurrentPlayerId();
    }

    public void Dispose() => _testStore.Dispose();

    static EmpireRef Zomboss(long saveId) => new(new SaveId(saveId), EmpireId.Zomboss);

    static AptitudeAllocation Shares(params (string Id, long Points)[] rows) =>
        rows.Aggregate(AptitudeAllocation.Empty,
            (acc, kv) => acc + AptitudeAllocation.Single(AllocationScope.Commander, kv.Id, kv.Points));

    /// <summary>A theta far above any proposed spend, so a budget check at the empire's own index can
    /// never mask the write this class is about.</summary>
    const long ReadTheta = 5_000_000;

    [Fact]
    public void A_written_empire_pool_is_read_back_through_the_production_pool_read()
    {
        var owner = Zomboss(_saveId);
        var proposed = Shares(("Might", 40), ("Agility", 25));

        Assert.True(_store.TryWriteEmpireCommanderPool(owner, proposed, out var reason), reason);

        // The ordinary read, at a theta whose budget the proposed pool is inside.
        var read = _store.CommanderPoolOf(owner, ReadTheta, AptitudeTuningHub.Tuning);

        Assert.False(read.IsDefault);            // an explicit pool, not the read-time default
        Assert.Equal(40, read.Allocation.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(25, read.Allocation.PointsAt(AllocationScope.Commander, "Agility"));
    }

    [Fact]
    public void A_written_empire_pool_does_not_touch_the_human_pool()
    {
        var human = new EmpireRef(new SaveId(_saveId), _store.HumanEmpireOf(_saveId));
        _store.SaveAllocation(AllocationScope.Commander, $"player:{_saveId}", Shares(("Might", 11)));

        Assert.True(_store.TryWriteEmpireCommanderPool(Zomboss(_saveId), Shares(("Might", 40)), out var reason), reason);

        // The human's own sheet is a different key and must be byte-identical to what it was.
        var humanShares = _store.LoadAllocation(AllocationScope.Commander, $"player:{_saveId}");
        Assert.Equal(11, humanShares.PointsAt(AllocationScope.Commander, "Might"));

        // ...and the human pool READ (never the writer) still resolves to the human's own sheet.
        Assert.Equal(11, _store.CommanderPoolOf(human, ReadTheta, AptitudeTuningHub.Tuning)
            .Allocation.PointsAt(AllocationScope.Commander, "Might"));
    }

    [Fact]
    public void An_empire_this_save_does_not_carry_is_refused_loudly_and_persists_nothing()
    {
        // A well-formed empire id that is NOT one of this save's empires.
        var stranger = new EmpireRef(new SaveId(_saveId), new EmpireId("not-an-empire-of-this-save"));

        Assert.False(_store.TryWriteEmpireCommanderPool(stranger, Shares(("Might", 40)), out var reason));
        Assert.Equal(RpgStore.EmpirePoolWrite.EmpireNotCarried, reason);

        // ...and it wrote nothing under any key: the real Zomboss pool still reads as unwritten.
        var read = _store.CommanderPoolOf(Zomboss(_saveId), ReadTheta, AptitudeTuningHub.Tuning);
        Assert.True(read.IsDefault);
    }

    [Fact]
    public void The_human_empire_is_refused_so_the_priced_gate_stays_its_only_writer()
    {
        var human = new EmpireRef(new SaveId(_saveId), _store.HumanEmpireOf(_saveId));

        Assert.False(_store.TryWriteEmpireCommanderPool(human, Shares(("Might", 40)), out var reason));
        Assert.Equal(RpgStore.EmpirePoolWrite.HumanEmpireRequiresGate, reason);

        // The refusal wrote nothing: the human's own allocation is untouched.
        Assert.Equal(0, _store.LoadAllocation(AllocationScope.Commander, $"player:{_saveId}")
            .PointsAt(AllocationScope.Commander, "Might"));
    }

    /// <summary>The persisted pool is GONE, asserted on the stored row rather than on what the read
    /// then computes. "The read falls back to its default" depends on process-wide ladder/tuning state
    /// that other classes in this assembly reconfigure, so asserting <c>IsDefault</c> here would be a
    /// claim about another module's configuration. What this path owns is the row, so that is what is
    /// asserted — plus the consequence that the read no longer sees the cleared values as explicit.</summary>
    [Fact]
    public void An_all_zero_write_clears_the_persisted_pool_so_the_read_no_longer_sees_it()
    {
        var owner = Zomboss(_saveId);
        Assert.True(_store.TryWriteEmpireCommanderPool(owner, Shares(("Might", 40)), out var first), first);
        Assert.Equal(40, StoredPool(owner).PointsAt(AllocationScope.Commander, "Might"));

        Assert.True(_store.TryWriteEmpireCommanderPool(owner, AptitudeAllocation.Empty, out var second), second);

        // The stored (scope, scope_key) pair holds nothing, so the read can no longer treat any value as
        // this pool's EXPLICIT allocation and must resolve through its own fall-through instead.
        Assert.Equal(0, StoredPool(owner).PointsAt(AllocationScope.Commander, "Might"));
        Assert.True(_store.CommanderPoolOf(owner, ReadTheta, AptitudeTuningHub.Tuning).IsDefault);

        // The cleared value is NOT among what the read now serves: the written 40 is gone, whatever the
        // ladder happens to compute. Asserting an exact point total here would be a claim about the
        // read-time default's own tuning, which other classes in this assembly reconfigure.
        Assert.NotEqual(40,
            _store.CommanderPoolOf(owner, ReadTheta, AptitudeTuningHub.Tuning).Allocation
                .PointsAt(AllocationScope.Commander, "Might"));
    }

    /// <summary>What is actually persisted for <paramref name="owner"/>, read under the ONE key the
    /// production pool read derives — never a hand-written key, so this cannot drift from it.</summary>
    AptitudeAllocation StoredPool(EmpireRef owner)
    {
        var directory = CommanderDirectoryHub.Current;
        return _store.LoadAllocation(AllocationScope.Commander,
            directory.AllocationScopeKey(directory.DefaultFor(owner.Empire), owner.Save.Value));
    }

    [Fact]
    public void Two_saves_never_share_one_empire_pool()
    {
        var second = _store.CreatePlayer("EmpirePoolSecondSave");

        Assert.True(_store.TryWriteEmpireCommanderPool(Zomboss(_saveId), Shares(("Might", 40)), out var a), a);
        Assert.True(_store.TryWriteEmpireCommanderPool(Zomboss(second.Id), Shares(("Might", 7)), out var b), b);

        Assert.Equal(40, _store.CommanderPoolOf(Zomboss(_saveId), ReadTheta, AptitudeTuningHub.Tuning)
            .Allocation.PointsAt(AllocationScope.Commander, "Might"));
        Assert.Equal(7, _store.CommanderPoolOf(Zomboss(second.Id), ReadTheta, AptitudeTuningHub.Tuning)
            .Allocation.PointsAt(AllocationScope.Commander, "Might"));
    }
}
