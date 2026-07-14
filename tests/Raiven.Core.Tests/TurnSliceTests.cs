using Raiven.Core.Transcripts;

namespace Raiven.Core.Tests;

public class TurnSliceTests
{
    [Fact]
    public void Describe_ReportsLengthsAndTools()
    {
        var slice = new TurnSlice("do it", "done and done", ["Edit", "Bash"]);

        Assert.Equal("prompt 5 chars, assistant text 13 chars, tools: Edit, Bash", slice.Describe());
    }

    [Fact]
    public void Describe_EmptyTextAndNoTools_SaysNone()
    {
        var slice = new TurnSlice("q", "", []);

        Assert.Equal("prompt 1 chars, assistant text 0 chars, tools: none", slice.Describe());
    }
}
