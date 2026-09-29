namespace FusionRpg.Core.Effects;

/// <summary>Re-entry guard: Harmony TakeDamage Prefix skips emit/DEF while FA10 Writer Add/Die runs.</summary>
public static class OverlayApplyGuard
{
    [ThreadStatic] static int Depth;

    public static bool IsActive => Depth > 0;

    public static IDisposable Enter()
    {
        Depth++;
        return Pop.Instance;
    }

    sealed class Pop : IDisposable
    {
        public static readonly Pop Instance = new();
        public void Dispose()
        {
            if (Depth > 0) Depth--;
        }
    }
}
