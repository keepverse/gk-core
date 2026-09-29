namespace FusionRpg.Core.Onboarding;

/// <summary>Durable, non-reward story acknowledgements. This is intentionally separate from checkpoints.</summary>
public static class OnboardingStoryIds
{
    public const string RiftPrologue = "rift-prologue";
    public const int RiftPrologueVersion = 1;
    public static readonly IReadOnlyList<string> Ordered = new[] { RiftPrologue };
}

public static class OnboardingStoryStates
{
    public const string Unseen = "unseen";
    public const string Acknowledged = "acknowledged";
}

public static class OnboardingStoryOutcomes
{
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    public static readonly IReadOnlyList<string> Ordered = new[] { Completed, Skipped };
}
