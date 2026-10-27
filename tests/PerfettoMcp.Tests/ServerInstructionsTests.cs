using PerfettoMcp.Mcp;
using Xunit;

namespace PerfettoMcp.Tests;

public class ServerInstructionsTests
{
    // Claude Code shows only the first 4096 characters of server instructions.
    [Fact]
    public void FitsClientLimit() => Assert.InRange(ServerInstructions.Text.Length, 1, 3000);
}
