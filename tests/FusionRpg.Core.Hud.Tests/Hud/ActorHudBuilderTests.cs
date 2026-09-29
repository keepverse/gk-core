using System.Text.Json;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudBuilderTests
{
    [Fact]
    public void Golden_elite_shield_dual_status_matches_fixture()
    {
        var input = new ActorHudComposer.ActorHudComposeInput(
            IsUniquePlant: false,
            BindingPhase: null,
            LevelBand: 12,
            ShieldStacks: new[] { new ActorHudShieldStack("fire", 50, 80) },
            ShieldHp: 50,
            ShieldMax: 80,
            StatusTokens: new[]
            {
                new ActorHudStatusToken("expose", false, MagnitudeBand.Mid),
                new ActorHudStatusToken("command", false, MagnitudeBand.Low),
            },
            StatusStripMax: 3,
            HpSliverEnabled: false);

        var snapshot = ActorHudComposer.Compose(input);
        var actual = ActorHudWireSerializer.ToDictionary(snapshot);

        var path = FindGolden("elite_shield_dual_status.json");
        var expectedJson = File.ReadAllText(path);
        var expected = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(expectedJson)!;

        AssertWireEqual(expected, actual);
    }

    static void AssertWireEqual(Dictionary<string, JsonElement> expected, Dictionary<string, object> actual)
    {
        foreach (var kv in expected)
        {
            Assert.True(actual.ContainsKey(kv.Key), "missing key " + kv.Key);
            AssertJsonEqual(kv.Value, actual[kv.Key], kv.Key);
        }
    }

    static void AssertJsonEqual(JsonElement expected, object actual, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dict = Assert.IsType<Dictionary<string, object>>(actual);
                foreach (var prop in expected.EnumerateObject())
                {
                    Assert.True(dict.ContainsKey(prop.Name), path + "." + prop.Name);
                    AssertJsonEqual(prop.Value, dict[prop.Name], path + "." + prop.Name);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var arr = Assert.IsAssignableFrom<IEnumerable<object>>(actual).ToArray();
                var expArr = expected.EnumerateArray().ToArray();
                Assert.Equal(expArr.Length, arr.Length);
                for (var i = 0; i < expArr.Length; i++)
                    AssertJsonEqual(expArr[i], arr[i], path + "[" + i + "]");
                break;
            }
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), Convert.ToString(actual));
                break;
            case JsonValueKind.Number:
                if (expected.TryGetInt64(out var li))
                    Assert.Equal(li, Convert.ToInt64(actual));
                else
                    Assert.Equal(expected.GetDouble(), Convert.ToDouble(actual), 6);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), Convert.ToBoolean(actual));
                break;
            default:
                throw new NotSupportedException(path + ": " + expected.ValueKind);
        }
    }

    static string FindGolden(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "Goldens", "actor-hud", name);
            if (File.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "Goldens", "actor-hud", name));
            if (File.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new FileNotFoundException("golden " + name);
    }
}
