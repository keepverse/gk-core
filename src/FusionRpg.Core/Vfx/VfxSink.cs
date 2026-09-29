using FusionRpg.Contracts;

namespace FusionRpg.Core.Vfx;

/// <summary>
/// Single presentation sink for all cues — vfx-ssot.md §5.
/// Play must be callable from any thread; production sinks enqueue only.
/// </summary>
public interface IVfxSink
{
    void Play(VfxCueDto cue);
}

public sealed class NoopVfxSink : IVfxSink
{
    public static readonly NoopVfxSink Instance = new();
    public void Play(VfxCueDto cue) { }
}

public sealed class RecordingVfxSink : IVfxSink
{
    readonly object _gate = new();
    public List<VfxCueDto> Items { get; } = new();

    public void Play(VfxCueDto cue)
    {
        lock (_gate) Items.Add(cue);
    }
}
