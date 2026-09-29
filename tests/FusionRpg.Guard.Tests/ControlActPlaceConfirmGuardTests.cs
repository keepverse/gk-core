using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N30: <c>debug.act place</c> used to emit <c>debug.act.done</c> after invoking the card chain
/// even when nothing was planted (live 2026-09-15, during the battle-start pan). The receipt must be decided by
/// a new live plant of the card's type in the target cell, captured before and after the invoke. Source scan:
/// the Injector has no CI-runnable unit tests.
/// </summary>
public class ControlActPlaceConfirmGuardTests
{
    [Fact]
    public void Place_receipt_is_decided_by_a_new_plant_in_the_target_cell()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "ControlAct.cs"));
        var body = MethodBody(text, "static void DoPlace(JsonElement p)");

        var before = body.IndexOf("var before = PlantPtrsAt(col, row, typeId);", StringComparison.Ordinal);
        var invoke = body.IndexOf("mouse.TryToSetPlantByCard();", StringComparison.Ordinal);
        var after = body.IndexOf("PlantPtrsAt(col, row, typeId).FirstOrDefault(ptr => !before.Contains(ptr))", StringComparison.Ordinal);
        var emit = body.IndexOf("DebugRuntime.Emit(\"debug.act.done\", receipt);", StringComparison.Ordinal);

        Assert.True(before >= 0 && invoke > before, "the cell must be read before the card is used");
        Assert.True(after > invoke, "the cell must be read again after the card is used");
        Assert.True(emit > after, "the receipt must be emitted only after the placement check");
        Assert.Contains("[\"ok\"] = placed != null", body, StringComparison.Ordinal);
    }

    static string MethodBody(string text, string signature)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "missing " + signature);
        var open = text.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
