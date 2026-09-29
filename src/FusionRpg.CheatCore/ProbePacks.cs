namespace FusionRpg.CheatCore;

public sealed class ProbeStep
{
    public string Op { get; set; } = ""; // toggle | set-float | action
    public string? Id { get; set; }
    public string? Action { get; set; }
    public bool? Enabled { get; set; }
    public double? Value { get; set; }
    public string? Which { get; set; }
    public bool? Add { get; set; }
}

public sealed class ProbePack
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Hint { get; set; } = "";
    public IReadOnlyList<string> ExpectedKinds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<ProbeStep> Steps { get; set; } = Array.Empty<ProbeStep>();
}

/// <summary>Named live-game probe packs (not sim /api/test/probe).</summary>
public static class ProbePacks
{
    static readonly Lazy<IReadOnlyList<ProbePack>> AllLazy = new(Build);

    public static IReadOnlyList<ProbePack> All => AllLazy.Value;

    public static ProbePack? Get(string packId) =>
        All.FirstOrDefault(p => string.Equals(p.Id, packId, StringComparison.OrdinalIgnoreCase));

    static IReadOnlyList<ProbePack> Build() => new[]
    {
        new ProbePack
        {
            Id = "pack.scale-plant",
            Label = "Scale plant HP%",
            Hint = "Place or spawn a plant after running; expect stat.applied / plant.spawn.",
            ExpectedKinds = new[] { "cheat.inject", "stat.applied", "plant.spawn" },
            Steps = new[]
            {
                new ProbeStep { Op = "toggle", Id = "A-APPLY", Enabled = true },
                new ProbeStep { Op = "set-float", Id = "A-P-HP%", Value = 5 },
                new ProbeStep { Op = "action", Action = "reapply" }
            }
        },
        new ProbePack
        {
            Id = "pack.god-plant",
            Label = "Plant godmode",
            Hint = "Let zombies attack a plant; HP should hold. Filter plant.damage in log.",
            ExpectedKinds = new[] { "cheat.inject" },
            Steps = new[]
            {
                new ProbeStep { Op = "toggle", Id = "P-GOD", Enabled = true }
            }
        },
        new ProbePack
        {
            Id = "pack.board-e",
            Label = "Board.config E-ZH",
            Hint = "Must be in a board; expect board.modifiers.",
            ExpectedKinds = new[] { "cheat.inject", "board.modifiers" },
            Steps = new[]
            {
                new ProbeStep { Op = "set-float", Id = "E-ZH", Value = 3 },
                new ProbeStep { Op = "action", Action = "board-config" }
            }
        },
        new ProbePack
        {
            Id = "pack.economy",
            Label = "Economy sun set",
            Hint = "In a board; expect board.economy poll or inject note.",
            ExpectedKinds = new[] { "cheat.inject", "board.economy" },
            Steps = new[]
            {
                new ProbeStep { Op = "action", Action = "economy", Which = "sun", Value = 9999, Add = false }
            }
        },
        new ProbePack
        {
            Id = "pack.autocollect",
            Label = "Autocollect",
            Hint = "Drop sun/coins and wait; inject only is guaranteed.",
            ExpectedKinds = new[] { "cheat.inject" },
            Steps = new[]
            {
                new ProbeStep { Op = "toggle", Id = "G-AUTOCOLLECT", Enabled = true }
            }
        },
        new ProbePack
        {
            Id = "pack.free-set",
            Label = "Free SetPlant",
            Hint = "Place a plant freely; expect plant.place.",
            ExpectedKinds = new[] { "cheat.inject", "plant.place" },
            Steps = new[]
            {
                new ProbeStep { Op = "toggle", Id = "G-FREE-SET", Enabled = true }
            }
        },
        new ProbePack
        {
            Id = "pack.smoke-core",
            Label = "Smoke core",
            Hint = "One-shot: scales + god + free-set + autocollect. Then place plant and play briefly.",
            ExpectedKinds = new[] { "cheat.inject", "stat.applied", "plant.spawn", "plant.place" },
            Steps = new[]
            {
                new ProbeStep { Op = "toggle", Id = "A-APPLY", Enabled = true },
                new ProbeStep { Op = "set-float", Id = "A-P-HP%", Value = 3 },
                new ProbeStep { Op = "toggle", Id = "P-GOD", Enabled = true },
                new ProbeStep { Op = "toggle", Id = "G-FREE-SET", Enabled = true },
                new ProbeStep { Op = "toggle", Id = "G-AUTOCOLLECT", Enabled = true },
                new ProbeStep { Op = "action", Action = "reapply" }
            }
        }
    };

    public static object ToApiDto(ProbePack p) => new
    {
        id = p.Id,
        label = p.Label,
        hint = p.Hint,
        expectedKinds = p.ExpectedKinds,
        steps = p.Steps.Select(s => new
        {
            op = s.Op,
            id = s.Id,
            action = s.Action,
            enabled = s.Enabled,
            value = s.Value,
            which = s.Which,
            add = s.Add
        }).ToList()
    };
}
