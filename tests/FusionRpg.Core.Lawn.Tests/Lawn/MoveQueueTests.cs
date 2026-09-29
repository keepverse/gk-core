using FusionRpg.Core.Lawn;
using Xunit;

namespace FusionRpg.Core.Tests.Lawn;

/// <summary>
/// A-M2 lawn-reposition (spec-lawn-reposition.md §4) — MoveQueue is the pure, Core-side
/// record-then-drain FIFO MoveDrainHost (injector) wraps around a real Plant/Zombie resolve. Every
/// test here runs with a plain stub writer, no Unity/game reference, matching the same split
/// EntityWriteGate's own note documents ("this assembly needs a real PVZ Fusion install to build
/// and is absent from ci.yml").
/// </summary>
public class MoveQueueTests
{
    sealed class ThrowingWriter : IMoveWriter
    {
        public void Move(in MoveRecord record) => throw new InvalidOperationException(
            "MoveQueue.TryRecord must never reach the writer -- only Drain may.");
    }

    sealed class RecordingWriter : IMoveWriter
    {
        public readonly List<MoveRecord> Calls = new();
        public void Move(in MoveRecord record) => Calls.Add(record);
    }

    [Fact]
    public void Recording_alone_never_reaches_the_writer()
    {
        var q = new MoveQueue();
        var writer = new ThrowingWriter();

        for (var i = 0; i < 10; i++)
            Assert.True(q.TryRecord(new IntPtr(i + 1), 1, i, i, "test"));

        Assert.Equal(10, q.Count);
        // No exception -- recording ten moves against a writer whose only behaviour is `throw`
        // proves TryRecord performs no write (spec §4.1).
    }

    [Fact]
    public void Only_drain_reaches_the_writer_and_it_does()
    {
        var q = new MoveQueue();
        q.TryRecord(new IntPtr(1), 1, 3, 2, "test");

        Assert.Throws<InvalidOperationException>(() => q.Drain(new ThrowingWriter()));
    }

    [Fact]
    public void Drain_applies_in_recorded_order()
    {
        var q = new MoveQueue();
        for (var i = 0; i < 5; i++)
            q.TryRecord(new IntPtr(i + 1), 1, i, 0, "src" + i);

        var writer = new RecordingWriter();
        q.Drain(writer);

        Assert.Equal(5, writer.Calls.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(new IntPtr(i + 1), writer.Calls[i].Ptr);
            Assert.Equal(i, writer.Calls[i].Col);
        }
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Same_recorded_sequence_drained_twice_yields_the_same_writer_calls_in_the_same_order()
    {
        MoveRecord[] Record()
        {
            var q = new MoveQueue();
            for (var i = 0; i < 7; i++)
                q.TryRecord(new IntPtr(100 + i), (byte)(i % 2 == 0 ? 1 : 2), i, i % 5, "s" + i);

            var writer = new RecordingWriter();
            q.Drain(writer);
            return writer.Calls.ToArray();
        }

        var first = Record();
        var second = Record();

        Assert.Equal(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i].Ptr, second[i].Ptr);
            Assert.Equal(first[i].Side, second[i].Side);
            Assert.Equal(first[i].Col, second[i].Col);
            Assert.Equal(first[i].Row, second[i].Row);
            Assert.Equal(first[i].Source, second[i].Source);
        }
    }

    [Fact]
    public void Ring_overflow_drops_and_counts_without_throwing()
    {
        var q = new MoveQueue(capacity: 4);
        for (var i = 0; i < 4; i++)
            Assert.True(q.TryRecord(new IntPtr(i + 1), 1, i, 0, "s"));

        // Fifth record overflows -- dropped and counted, never blocks (spec §2).
        Assert.False(q.TryRecord(new IntPtr(99), 1, 9, 0, "overflow"));
        Assert.Equal(1, q.Dropped);
        Assert.Equal(4, q.Count);

        Assert.False(q.TryRecord(new IntPtr(100), 1, 9, 0, "overflow2"));
        Assert.Equal(2, q.Dropped);
    }

    [Fact]
    public void Drain_interrupted_mid_way_resumes_at_the_next_record_and_never_re_applies_one_already_sent()
    {
        var q = new MoveQueue();
        for (var i = 0; i < 6; i++)
            q.TryRecord(new IntPtr(i + 1), 1, i, 0, "s" + i);

        var writer = new RecordingWriter();
        var stopAfterThree = 0;
        q.Drain(writer, shouldStop: () => stopAfterThree++ >= 3);

        Assert.Equal(3, writer.Calls.Count);
        Assert.Equal(3, q.Count); // three remain, none re-queued or skipped

        // Resume: the next drain picks up exactly where the interrupted one left off.
        q.Drain(writer);
        Assert.Equal(6, writer.Calls.Count);
        Assert.Equal(0, q.Count);

        for (var i = 0; i < 6; i++)
            Assert.Equal(new IntPtr(i + 1), writer.Calls[i].Ptr); // recorded order preserved, no repeats
    }

    [Fact]
    public void Drain_on_an_empty_queue_never_calls_the_writer()
    {
        var q = new MoveQueue();
        // A throwing writer would blow up immediately if Drain (incorrectly) called Move on an
        // empty queue -- no exception here is the proof it did not.
        q.Drain(new ThrowingWriter());
    }
}
