namespace FusionRpg.Core.Lawn;

/// <summary>
/// One recorded "move actor to cell" request — values only, exactly the discipline
/// <c>FusionRpg.Core.Events.GameEventRec</c> documents ("IL2CPP object references are
/// use-after-free at drain time"): nothing here holds a live Plant/Zombie, only the pointer and
/// the destination the hook already decided on. <see cref="Side"/> reuses
/// <c>FusionRpg.Core.Events.GameEventSide</c>'s Plant/Zombie constants rather than inventing a
/// second enum for the same two actor kinds.
/// </summary>
public readonly struct MoveRecord
{
    public readonly IntPtr Ptr;
    public readonly byte Side;
    public readonly int Col;
    public readonly int Row;
    public readonly string Source;
    public readonly long Seq;

    public MoveRecord(IntPtr ptr, byte side, int col, int row, string source, long seq)
    {
        Ptr = ptr;
        Side = side;
        Col = col;
        Row = row;
        Source = source ?? "";
        Seq = seq;
    }
}

/// <summary>
/// The single call a drained move reaches. Kept as an interface so Core-side tests can stub it —
/// a throwing stub proves recording alone never writes (spec-lawn-reposition.md §4.1) — without
/// any Unity/game reference. The real implementation lives in the injector's
/// <c>MoveDrainHost</c>, which resolves <see cref="MoveRecord.Ptr"/>/<see cref="MoveRecord.Side"/>
/// back to a live Plant/Zombie and calls <c>EntityApply.MoveToCell</c>.
/// </summary>
public interface IMoveWriter
{
    void Move(in MoveRecord record);
}

/// <summary>
/// Bounded record-then-drain FIFO for A-M2 lawn-reposition — modelled on
/// <c>FusionRpg.Core.Events.GameEventRing</c>/<c>EventDrain</c> but far smaller: a move has no
/// coalescing, no chain depth, no funnel window, just "apply these, in order, until the budget
/// says stop." Pure C# (no Unity reference) so this queue — the part that actually needs to be
/// right — is unit-testable under <c>dotnet test</c>, unlike the injector assembly that hosts it
/// (see <c>FusionRpg.Core.Stats.EntityWriteGate</c>'s own note: that assembly needs a real PVZ
/// Fusion install to build and is absent from ci.yml).
/// </summary>
public sealed class MoveQueue
{
    // Structural (tunables-ssot.md T2) — a frame-safety ceiling on backlog, not a balance dial,
    // matching GameEventRing.DefaultCapacity's own plain-const treatment for the same reason.
    public const int DefaultCapacity = 256;

    readonly MoveRecord[] _buf;
    int _head;
    int _count;
    long _dropped;
    long _seq;

    public MoveQueue(int capacity = DefaultCapacity)
    {
        if (capacity < 1) capacity = 1;
        _buf = new MoveRecord[capacity];
    }

    public int Count => _count;
    public int Capacity => _buf.Length;

    /// <summary>Ring overflow — dropped and counted, never blocks (spec §2 "Record-then-drain",
    /// "a ring overflow drops and counts, never blocks — the same contract EventDrainHost states").</summary>
    public long Dropped => _dropped;

    /// <summary>Appends one move request. False on overflow — the drop is already counted here;
    /// the caller (MoveDrainHost.TryRecordMove) must not retry.</summary>
    public bool TryRecord(IntPtr ptr, byte side, int col, int row, string source)
    {
        if (_count >= _buf.Length)
        {
            _dropped++;
            return false;
        }

        var tail = _head + _count;
        if (tail >= _buf.Length) tail -= _buf.Length;
        _buf[tail] = new MoveRecord(ptr, side, col, row, source, ++_seq);
        _count++;
        return true;
    }

    /// <summary>
    /// Applies queued records in recorded order via <paramref name="writer"/>, stopping when
    /// <paramref name="shouldStop"/> says so (the frame budget, in the real host) or the queue runs
    /// empty. A record is popped ONLY once it has been handed to the writer, so a drain interrupted
    /// mid-way resumes at the next unpopped record on the following call — it never re-applies one
    /// already sent (spec §4.2).
    /// </summary>
    public void Drain(IMoveWriter writer, Func<bool>? shouldStop = null)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        while (_count > 0)
        {
            if (shouldStop != null && shouldStop()) return;

            var rec = _buf[_head];
            _head++;
            if (_head >= _buf.Length) _head = 0;
            _count--;

            writer.Move(in rec);
        }
    }
}
