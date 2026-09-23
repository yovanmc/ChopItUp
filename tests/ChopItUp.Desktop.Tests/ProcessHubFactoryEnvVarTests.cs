using ChopItUp.Desktop.Hub;
using ChopItUp.Hub.Hosting;
using Xunit;

namespace ChopItUp.Desktop.Tests;

/// <summary>No ProjectReference from Desktop to the Hub EXE project
/// exists in src (an exe-to-exe reference would drag wwwroot and the client build into the wrong
/// output), so <see cref="ProcessHubFactory"/> repeats the "CHOPITUP_SHELL_TOKEN" literal by hand. This
/// test project takes the test-only reference (mirroring tests\ChopItUp.Hub.Tests's own reference to
/// this same project) so that cross-file invariant is pinned by a compiler-checked equality rather than
/// only a comment.</summary>
public sealed class ProcessHubFactoryEnvVarTests
{
    [Fact]
    public void Shell_token_env_var_literal_matches_the_hub_option()
    {
        Assert.Equal(HubOptions.ShellTokenEnvVar, ProcessHubFactory.ShellTokenEnvVar);
    }
}
