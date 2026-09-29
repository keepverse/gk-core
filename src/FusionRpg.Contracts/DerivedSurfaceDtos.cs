namespace FusionRpg.Contracts;

/// <summary>Cooked tabbed derived-stat surface for <c>GET /api/catalogs/derived-surface</c>.</summary>
public sealed class DerivedSurfaceDto
{
    public string Lang { get; init; } = "en";
    public string Side { get; init; } = "plant";
    public int SchemaVersion { get; init; }
    public string VersionStamp { get; init; } = "";
    public IReadOnlyList<DerivedSurfaceTabDto> Tabs { get; init; } = Array.Empty<DerivedSurfaceTabDto>();
}

public sealed class DerivedSurfaceTabDto
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Order { get; init; }
    public string Expand { get; init; } = "none";
    public IReadOnlyList<DerivedSurfaceVariantDto> Variants { get; init; } = Array.Empty<DerivedSurfaceVariantDto>();
    /// <summary>Present on the <c>other</c> tab for skill family expand joins.</summary>
    public IReadOnlyList<DerivedSurfaceVariantDto>? ActionCategoryVariants { get; init; }
    public IReadOnlyList<DerivedSurfaceCategoryDto> Categories { get; init; } = Array.Empty<DerivedSurfaceCategoryDto>();
}

public sealed class DerivedSurfaceVariantDto
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Ordinal { get; init; }
    public bool PresentationOnly { get; init; }
}

public sealed class DerivedSurfaceCategoryDto
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Order { get; init; }
    public IReadOnlyList<DerivedSurfaceFamilyDto> Families { get; init; } = Array.Empty<DerivedSurfaceFamilyDto>();
}

public sealed class DerivedSurfaceFamilyDto
{
    public string Family { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Reading { get; init; } = "";
    public string Compose { get; init; } = "";
    public string UnitClass { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Gauge { get; init; } = "";
    public string? CapRef { get; init; }
    public string Expand { get; init; } = "none";
    /// <summary><c>{family}</c> or <c>{family}.{variant}</c> — join to <c>/sheet</c> channelId.</summary>
    public string ChannelPattern { get; init; } = "{family}";
}
