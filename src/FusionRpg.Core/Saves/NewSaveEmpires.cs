using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Commanders;

namespace FusionRpg.Core.Saves;

/// <summary>One authored row of <c>data/seed/saves/_registry/new-save-empires.v{n}.json</c>.</summary>
public sealed record NewSaveEmpireRow(string EmpireId, EmpireController Controller);

/// <summary>
/// Which empires a new save gets — authored data, not code (ruling R3/R17). <see cref="Parse"/> is pure
/// JSON-in/rows-out (Core never touches a path); the host reads the file and the store seeds from the
/// result. The checks below are the contract: schema, the closed controller vocabulary, exactly one
/// <c>human</c> row, and the empire-id shape. The number of rows is a population and is never asserted.
/// </summary>
public sealed class NewSaveEmpires
{
    public const int SchemaVersion = 1;

    readonly IReadOnlyList<NewSaveEmpireRow> _rows;

    NewSaveEmpires(IReadOnlyList<NewSaveEmpireRow> rows) => _rows = rows;

    public IReadOnlyList<NewSaveEmpireRow> Rows => _rows;

    public static NewSaveEmpires Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var version)
            || version != SchemaVersion)
            throw new FormatException($"new-save-empires: schemaVersion must be {SchemaVersion}");

        if (!root.TryGetProperty("empires", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new FormatException("new-save-empires: missing array 'empires'");

        var rows = new List<NewSaveEmpireRow>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new FormatException("new-save-empires: every 'empires' entry must be an object");
            rows.Add(new NewSaveEmpireRow(Str(el, "empireId"), Controller(Str(el, "controller"))));
        }

        // Exactly one human empire per save is a contract, not a count of empires: every other row is a
        // decider the code must not confuse with the player's own.
        var humans = rows.Count(r => r.Controller == EmpireController.Human);
        if (humans != 1)
            throw new FormatException(
                $"new-save-empires: exactly one 'human' row is required, found {humans}");

        return new NewSaveEmpires(rows);
    }

    /// <summary>The closed controller vocabulary. An unknown token is refused by name, never defaulted.</summary>
    public static EmpireController Controller(string value) => value switch
    {
        "human" => EmpireController.Human,
        "ai" => EmpireController.Ai,
        _ => throw new FormatException(
            $"new-save-empires: controller '{value}' is not one of human/ai"),
    };

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new FormatException($"new-save-empires: missing string '{key}'");
        var v = el.GetString();
        if (string.IsNullOrWhiteSpace(v))
            throw new FormatException($"new-save-empires: '{key}' must not be empty");
        // The id space is the world map's faction id space (WorldFaction.FactionId): a bare token, never
        // a prefixed composite key. A ':' or a space means someone confused it with another id shape.
        if (v.Contains(':', StringComparison.Ordinal) || v.Any(char.IsWhiteSpace))
            throw new FormatException($"new-save-empires: '{key}' must be a bare faction token, got '{v}'");
        return v;
    }
}
