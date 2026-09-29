namespace FusionRpg.Core.Actions.Movement;

/// <summary>One row of the published movement-payload vocabulary — an id plus the negative-clause
/// <c>description</c> AC2 requires (spec-movement-payload.md §2). Ids and prose only; no magnitude
/// ever lands on this record (the atom roll owns those, §3).</summary>
public sealed record MovementPayloadEntry(string Id, string Description);

/// <summary>
/// Parsed, load-validated <c>data/tuning/movement-payload.v{n}.json</c> (A-M1). Three closed lists —
/// <see cref="Channels"/>, <see cref="Statuses"/>, <see cref="PayloadKinds"/> — never a fourth, and
/// never extended with a vocabulary this module does not itself own (§3: "never invent a status or a
/// channel"). Built only by <see cref="MovementPayloadTuningLoader.Parse"/>, which is what actually
/// enforces every closure rule; this record just carries the result.
/// </summary>
public sealed class MovementPayloadTuning
{
    public IReadOnlyList<MovementPayloadEntry> Channels { get; }
    public IReadOnlyList<MovementPayloadEntry> Statuses { get; }
    public IReadOnlyList<MovementPayloadEntry> PayloadKinds { get; }

    public MovementPayloadTuning(
        IReadOnlyList<MovementPayloadEntry> channels,
        IReadOnlyList<MovementPayloadEntry> statuses,
        IReadOnlyList<MovementPayloadEntry> payloadKinds)
    {
        Channels = channels ?? throw new ArgumentNullException(nameof(channels));
        Statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
        PayloadKinds = payloadKinds ?? throw new ArgumentNullException(nameof(payloadKinds));
    }
}
