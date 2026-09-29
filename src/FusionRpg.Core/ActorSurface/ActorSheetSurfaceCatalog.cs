using System.Text.Json;

namespace FusionRpg.Core.ActorSurface;

/// <summary>Closed ActorSheet tab renderer kinds — a ninth value is a load reject (T5).</summary>
public enum ActorSheetTabKind
{
    Condition,
    Aptitudes,
    Derived,
    Shield,
    Status,
    Elements,
    Kit,
    Paths
}

public sealed record ActorSheetTabEntry(
    ActorSheetTabKind Kind,
    string Label,
    int Order,
    bool Hidden,
    string? Icon);

public sealed record ActorSheetKitRoleLabels(string Humanoid, string Plant);

public sealed record ActorSheetKitRoleEntry(
    string RoleId,
    ActorSheetKitRoleLabels Labels);

public sealed record ActorSheetSurfaceCatalog(
    int SchemaVersion,
    int Version,
    ActorSheetTabKind DefaultOpen,
    IReadOnlyList<ActorSheetTabEntry> Tabs,
    IReadOnlyList<ActorSheetKitRoleEntry> KitRoles);

/// <summary>Pure parser, no file I/O (tunables-ssot.md T7.2).</summary>
public static class ActorSheetSurfaceCatalogLoader
{
    const string Catalog = "actor-sheet";

    public static ActorSheetSurfaceCatalog Parse(string json)
    {
        using var doc = ActorSurfaceJson.ParseDocument(json, Catalog);
        var root = doc.RootElement;
        var schemaVersion = ActorSurfaceJson.Int(root, "schemaVersion", "$", Catalog);
        var version = ActorSurfaceJson.Int(root, "version", "$", Catalog);
        var defaultOpen = ParseTabKind(root, "defaultOpen", "$");

        var tabsArr = ActorSurfaceJson.Arr(root, "tabs", "$", Catalog);
        var tabs = new List<ActorSheetTabEntry>(tabsArr.GetArrayLength());
        var ti = 0;
        foreach (var el in tabsArr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.tabs[{ti}]'");
            var path = $"tabs[{ti}]";
            tabs.Add(new ActorSheetTabEntry(
                Kind: ParseTabKind(el, "kind", path),
                Label: ActorSurfaceJson.Str(el, "label", path, Catalog),
                Order: ActorSurfaceJson.Int(el, "order", path, Catalog),
                Hidden: ActorSurfaceJson.Bool(el, "hidden", path, Catalog),
                Icon: ActorSurfaceJson.OptionalStr(el, "icon", path, Catalog)));
            ti++;
        }

        var rolesArr = ActorSurfaceJson.Arr(root, "kitRoles", "$", Catalog);
        var kitRoles = new List<ActorSheetKitRoleEntry>(rolesArr.GetArrayLength());
        var ri = 0;
        foreach (var el in rolesArr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.kitRoles[{ri}]'");
            var path = $"kitRoles[{ri}]";
            var labelsEl = ActorSurfaceJson.Obj(el, "labels", path, Catalog);
            kitRoles.Add(new ActorSheetKitRoleEntry(
                RoleId: ActorSurfaceJson.Str(el, "roleId", path, Catalog),
                Labels: new ActorSheetKitRoleLabels(
                    Humanoid: ActorSurfaceJson.Str(labelsEl, "humanoid", $"{path}.labels", Catalog),
                    Plant: ActorSurfaceJson.Str(labelsEl, "plant", $"{path}.labels", Catalog))));
            ri++;
        }

        return new ActorSheetSurfaceCatalog(schemaVersion, version, defaultOpen, tabs, kitRoles);
    }

    static ActorSheetTabKind ParseTabKind(JsonElement parent, string key, string path)
    {
        var raw = ActorSurfaceJson.Str(parent, key, path, Catalog);
        if (!Enum.TryParse<ActorSheetTabKind>(raw, ignoreCase: true, out var kind))
            throw new ActorSurfaceCatalogRejection($"{Catalog}: unknown tab kind '{raw}' at '{path}.{key}'");
        return kind;
    }
}

public static class ActorSheetSurfaceCatalogHub
{
    static ActorSheetSurfaceCatalog? _catalog;

    public static void Configure(ActorSheetSurfaceCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static ActorSheetSurfaceCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "ActorSheetSurfaceCatalogHub.Configure(...) has not run. Hosts read data/tuning/actor-sheet.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
