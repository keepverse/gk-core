using System.Text.Json;

namespace FusionRpg.Core.Actions.Seeding;

public sealed class ActionShareRejection : Exception
{
    public ActionShareRejection(string message) : base(message) { }
}

public sealed class UnsharedChannelException : Exception
{
    public UnsharedChannelException(string channel)
        : base($"no authored sharePermille for channel '{channel}' — rejects rather than defaults (spec-action-seeding.md §2, §7)") { }
}

/// <summary>
/// T31 (spec-action-seeding.md §2, §7): <c>sharePermille</c> is "the entire tunable surface, and a
/// missing one rejects rather than defaults." No arithmetic lives here — the numerics pipeline that
/// turns a share into a magnitude is a separate, not-yet-built module (`spec-numerics.md`, proposed);
/// this class owns only the authored table and the reject-not-default read, which is what this
/// program's own acceptance line actually asks for.
/// </summary>
public sealed class ActionShareTable
{
    readonly IReadOnlyDictionary<string, int> _permilleByChannel;

    ActionShareTable(IReadOnlyDictionary<string, int> permilleByChannel) => _permilleByChannel = permilleByChannel;

    /// <summary>Throws — never returns a default — when <paramref name="channel"/> was never authored.
    /// "A number a model picked is a plausible-looking guess"; a silently-defaulted share is the same
    /// defect one layer down.</summary>
    public int PermilleOf(string channel) =>
        _permilleByChannel.TryGetValue(channel, out var v) ? v : throw new UnsharedChannelException(channel);

    public bool HasChannel(string channel) => _permilleByChannel.ContainsKey(channel);

    public static ActionShareTable Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ActionShareRejection("action shares: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ActionShareRejection($"action shares: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ActionShareRejection("action shares: root must be an object of channel -> permille");

            var table = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var permille))
                    throw new ActionShareRejection($"action shares: channel '{prop.Name}' is not an integer permille");
                if (permille < 0)
                    throw new ActionShareRejection($"action shares: channel '{prop.Name}' permille {permille} must be >= 0");
                table[prop.Name] = permille;
            }

            return new ActionShareTable(table);
        }
    }
}
