namespace ProxyForge.Tests;

public sealed class UiThreadTests
{
    [Fact]
    public void WithoutAnApplicationTheActionRunsInlineOnTheCallingThread()
    {
        var threadId = 0;

        UiThread.Post(() => threadId = Environment.CurrentManagedThreadId);

        Assert.Equal(Environment.CurrentManagedThreadId, threadId);
    }
}
