using System.Globalization;
using System.Text.Json.Serialization;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.ActorSurface;

/// <summary>Fan-in DTO for <c>GET /api/catalogs/actor-surface</c> (hosts wire later).</summary>
public sealed record ActorSurfaceCatalogDto(
    IReadOnlyList<ActorSheetTabDto> Tabs,
    // The sheet catalog's own `defaultOpen`, projected with the same closed wire vocabulary the tab
    // kinds use. It was parsed into `ActorSheetSurfaceCatalog` and then dropped here, so the served
    // fan-in was not a fan-in of its own source: the FE type declares `defaultOpen` as required and
    // `ActorPanel` seeds its active tab from it, so a served catalog without it would have replaced
    // the fixture's real value with `undefined`.
    string DefaultOpen,
    IReadOnlyList<AptitudeSurfaceEntryDto> Aptitudes,
    IReadOnlyList<DerivedStatSurfaceEntryDto> Families,
    IReadOnlyList<ResourceSurfaceEntryDto> Resources,
    IReadOnlyList<ElementSurfaceEntryDto> Elements,
    IReadOnlyList<StatusSurfaceEntryDto> Statuses,
    IReadOnlyList<ActorSheetKitRoleDto> KitRoles,
    ActorHudPresentationDto HudPresentation,
    string VersionStamp);

public sealed record ActorHudPresentationDto(
    double IdentityElementPrimaryPixels,
    double IdentityElementSecondaryPixels,
    double IdentityElementGapPixels);

public sealed record ActorSheetTabDto(
    [property: JsonPropertyName("kind")] string Kind,
    string Label,
    int Order,
    bool Hidden,
    string? Icon);

public sealed record AptitudeSurfaceEntryDto(
    string Id,
    [property: JsonPropertyName("posture")] string Posture,
    int Ordinal,
    string DisplayName,
    string Role,
    string Reading,
    string? Icon);

public sealed record DerivedStatSurfaceEntryDto(
    string Family,
    [property: JsonPropertyName("expand")] string Expand,
    [property: JsonPropertyName("compose")] string Compose,
    [property: JsonPropertyName("unitClass")] string UnitClass,
    string DisplayName,
    string Reading,
    string Icon,
    string Gauge,
    string SheetGroup,
    string? CapRef);

public sealed record ResourceSurfaceEntryDto(
    string Id,
    [property: JsonPropertyName("class")] string Class,
    bool Exhaustion,
    bool ActionCost,
    ResourceSurfaceLabelsDto Labels,
    string Icon,
    string Color,
    string MeterKind);

public sealed record ResourceSurfaceLabelsDto(string Plant, string Zombie);

public sealed record ElementSurfaceEntryDto(
    string Id,
    string DisplayName,
    int Ordinal,
    string Color,
    bool PresentationOnly,
    string? HudGlyph);

public sealed record StatusSurfaceEntryDto(
    string Id,
    [property: JsonPropertyName("kind")] string Kind,
    IReadOnlyList<string> Categories,
    [property: JsonPropertyName("stacking")] string Stacking,
    IReadOnlyList<string> PayloadKinds,
    string DisplayName,
    string Reading,
    string HudToken,
    string Color);

public sealed record ActorSheetKitRoleDto(
    string RoleId,
    ActorSheetKitRoleLabelsDto Labels);

public sealed record ActorSheetKitRoleLabelsDto(string Humanoid, string Plant);

/// <summary>Configures individual surface hubs and builds the fan-in API DTO.</summary>
public static class ActorSurfaceCatalogHub
{
    public static void ConfigureAll(
        AptitudeSurfaceCatalog aptitudes,
        DerivedStatSurfaceCatalog derived,
        StatusSurfaceCatalog statuses,
        ResourceSurfaceCatalog resources,
        ElementSurfaceCatalog elements,
        ActorSheetSurfaceCatalog sheet)
    {
        RejectAptitudeChannelCollisions(aptitudes, derived);
        RejectResourceIdParity(resources);
        AptitudeSurfaceCatalogHub.Configure(aptitudes);
        DerivedStatSurfaceCatalogHub.Configure(derived);
        StatusSurfaceCatalogHub.Configure(statuses);
        StatusCatalogHub.Configure(StatusCatalogFactory.FromSurface(statuses));
        ResourceSurfaceCatalogHub.Configure(resources);
        ElementSurfaceCatalogHub.Configure(elements);
        ActorSheetSurfaceCatalogHub.Configure(sheet);
    }

    static void RejectResourceIdParity(ResourceSurfaceCatalog resources)
    {
        var catalogIds = resources.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var codeIds = FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds
            .ToHashSet(StringComparer.Ordinal);
        if (!catalogIds.SetEquals(codeIds))
        {
            throw new ActorSurfaceCatalogRejection(
                "resource-catalog: entry ids must equal DerivedStatChannels.ResourceIds exactly");
        }
    }

    public static ActorSurfaceCatalogDto BuildDto()
    {
        var aptitudes = AptitudeSurfaceCatalogHub.Catalog;
        var derived = DerivedStatSurfaceCatalogHub.Catalog;
        var statuses = StatusSurfaceCatalogHub.Catalog;
        var resources = ResourceSurfaceCatalogHub.Catalog;
        var elements = ElementSurfaceCatalogHub.Catalog;
        var sheet = ActorSheetSurfaceCatalogHub.Catalog;
        var hud = ActorHudTuningHub.Tuning;

        return new ActorSurfaceCatalogDto(
            Tabs: sheet.Tabs.Select(t => new ActorSheetTabDto(
                Kind: TabKindWire(t.Kind),
                Label: t.Label,
                Order: t.Order,
                Hidden: t.Hidden,
                Icon: t.Icon)).ToList(),
            DefaultOpen: TabKindWire(sheet.DefaultOpen),
            Aptitudes: aptitudes.Entries.Select(a => new AptitudeSurfaceEntryDto(
                Id: a.Id,
                Posture: a.Posture.ToString().ToLowerInvariant(),
                Ordinal: a.Ordinal,
                DisplayName: a.DisplayName,
                Role: a.Role,
                Reading: a.Reading,
                Icon: a.Icon)).ToList(),
            Families: derived.Entries.Select(f => new DerivedStatSurfaceEntryDto(
                Family: f.Family,
                Expand: DerivedStatSurfaceCatalogLoader.ExpandWire(f.Expand),
                Compose: f.Compose.ToString(),
                UnitClass: f.UnitClass.ToString(),
                DisplayName: f.DisplayName.Resolve("en"),
                Reading: f.Reading.Resolve("en"),
                Icon: f.Icon,
                Gauge: f.Gauge,
                SheetGroup: f.SheetGroup,
                CapRef: f.CapRef)).ToList(),
            Resources: resources.Entries.Select(r => new ResourceSurfaceEntryDto(
                Id: r.Id,
                Class: r.Class,
                Exhaustion: r.Exhaustion,
                ActionCost: r.ActionCost,
                Labels: new ResourceSurfaceLabelsDto(r.Labels.Plant, r.Labels.Zombie),
                Icon: r.Icon,
                Color: r.Color,
                MeterKind: r.MeterKind)).ToList(),
            Elements: elements.Entries.Select(e => new ElementSurfaceEntryDto(
                Id: e.Id,
                DisplayName: e.DisplayName,
                Ordinal: e.Ordinal,
                Color: e.Color,
                PresentationOnly: e.PresentationOnly,
                HudGlyph: e.HudGlyph)).ToList(),
            Statuses: statuses.Entries.Select(s => new StatusSurfaceEntryDto(
                Id: s.Id,
                Kind: s.Kind.ToString(),
                Categories: s.Categories,
                Stacking: s.Stacking.ToString(),
                PayloadKinds: s.PayloadKinds.Select(p => p.ToString()).ToList(),
                DisplayName: s.DisplayName,
                Reading: s.Reading,
                HudToken: s.HudToken,
                Color: s.Color)).ToList(),
            KitRoles: sheet.KitRoles.Select(k => new ActorSheetKitRoleDto(
                RoleId: k.RoleId,
                Labels: new ActorSheetKitRoleLabelsDto(k.Labels.Humanoid, k.Labels.Plant))).ToList(),
            HudPresentation: new ActorHudPresentationDto(
                IdentityElementPrimaryPixels: hud.ScreenIdentityElementPrimaryPixels,
                IdentityElementSecondaryPixels: hud.ScreenIdentityElementSecondaryPixels,
                IdentityElementGapPixels: hud.ScreenIdentityElementGapPixels),
            VersionStamp: BuildVersionStamp(aptitudes, derived, statuses, resources, elements, sheet));
    }

    static string BuildVersionStamp(
        AptitudeSurfaceCatalog aptitudes,
        DerivedStatSurfaceCatalog derived,
        StatusSurfaceCatalog statuses,
        ResourceSurfaceCatalog resources,
        ElementSurfaceCatalog elements,
        ActorSheetSurfaceCatalog sheet) =>
        string.Create(CultureInfo.InvariantCulture,
            $"aptitude:{aptitudes.Version}|derived:{derived.Version}|status:{statuses.Version}|resource:{resources.Version}|element:{elements.Version}|sheet:{sheet.Version}");

    static string TabKindWire(ActorSheetTabKind kind) => kind switch
    {
        ActorSheetTabKind.Condition => "condition",
        ActorSheetTabKind.Aptitudes => "aptitudes",
        ActorSheetTabKind.Derived => "derived",
        ActorSheetTabKind.Shield => "shield",
        ActorSheetTabKind.Status => "status",
        ActorSheetTabKind.Elements => "elements",
        ActorSheetTabKind.Kit => "kit",
        ActorSheetTabKind.Paths => "paths",
        _ => throw new ActorSurfaceCatalogRejection($"actor-sheet: unmapped tab kind '{kind}'")
    };

    static void RejectAptitudeChannelCollisions(
        AptitudeSurfaceCatalog aptitudes,
        DerivedStatSurfaceCatalog derived)
    {
        foreach (var apt in aptitudes.Entries)
        {
            foreach (var fam in derived.Entries)
            {
                if (string.Equals(apt.Id, fam.Family, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ActorSurfaceCatalogRejection(
                        $"aptitude-catalog: aptitude id '{apt.Id}' collides with derived family '{fam.Family}'");
                }
            }
        }
    }
}
