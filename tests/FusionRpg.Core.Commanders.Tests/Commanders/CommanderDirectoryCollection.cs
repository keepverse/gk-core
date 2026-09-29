using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

/// <summary>
/// Serializes the classes that read or reconfigure the process-wide <c>CommanderDirectoryHub</c> —
/// the same "process-wide state, parallel classes" hazard the Server.Tests collection names.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CommanderDirectoryCollection
{
    public const string Name = "CommanderDirectory";
}
