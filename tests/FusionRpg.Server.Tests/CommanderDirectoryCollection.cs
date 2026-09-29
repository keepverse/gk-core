using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// commander-identity SE4.3: serializes the classes that read or reconfigure the process-wide
/// <c>CommanderDirectoryHub</c> while also booting their own <c>WebApplication</c> with their own
/// store — the same "process-wide state, parallel classes" hazard
/// <see cref="SpeciesCatalogSwapCollection"/> names.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CommanderDirectoryCollection
{
    public const string Name = "CommanderDirectory";
}
