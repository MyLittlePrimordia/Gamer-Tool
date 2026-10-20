using System.Linq;
using System.Reflection;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The build id is the only way to tell one rolling release from another: the
/// version has been 1.0.0 throughout and the "latest" asset is replaced on every
/// push. If the metadata stops being emitted, every log and every pasted
/// diagnostics block goes back to saying nothing useful - which is a failure that
/// cannot be seen from inside the running app, because the fallback is a
/// perfectly plausible string.
/// </summary>
public class BuildIdentityTests
{
    [Fact]
    public void The_assembly_carries_a_build_id()
    {
        AssemblyMetadataAttribute? found = typeof(BuildIdentity).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildId");

        Assert.NotNull(found);
        Assert.False(string.IsNullOrWhiteSpace(found!.Value));
        Assert.Equal(found.Value, BuildIdentity.Id);
    }
}
